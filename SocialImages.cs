using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

public sealed record SocialImageData(string Source, string Title, string Organizer, long Goal, long Raised);

public sealed class SocialImages : IDisposable
{
    private const int MaxBytes = 8_000_000;
    private const long MaxPixels = 24_000_000;
    private readonly IWebHostEnvironment environment;
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 32_000_000 });
    private readonly SemaphoreSlim generationSlots = new(2);
    private readonly HttpClient client;

    public SocialImages(IWebHostEnvironment environment)
    {
        this.environment = environment;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = ConnectPublicAddress
        };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public string Version(SocialImageData fundraiser, Uri publicOrigin)
    {
        var local = LocalFile(fundraiser.Source, publicOrigin);
        var stamp = local is { Exists: true } ? local.LastModified.UtcTicks.ToString() : "";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("kindwell-card-v2\n" + JsonSerializer.Serialize(fundraiser) + "\n" + stamp));
        return Convert.ToHexString(hash).ToLowerInvariant()[..20];
    }

    public async Task<byte[]> GenerateAsync(SocialImageData fundraiser, Uri publicOrigin, string cacheKey, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(cacheKey, out byte[]? cached) && cached is not null) return cached;
        await generationSlots.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(cacheKey, out cached) && cached is not null) return cached;
            var data = await ReadCoverAsync(fundraiser.Source, publicOrigin, cancellationToken);
            using var encoded = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(encoded);
            if (codec is null || (long)codec.Info.Width * codec.Info.Height > MaxPixels)
                throw new InvalidDataException("Fundraiser cover has invalid or excessive dimensions.");
            using var image = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Fundraiser cover could not be decoded.");
            using var surface = SKSurface.Create(new SKImageInfo(1200, 630)) ?? throw new InvalidDataException("Could not create social image.");
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            using var photoBackground = new SKPaint { Color = new SKColor(232, 238, 233) };
            canvas.DrawRect(new SKRect(500, 0, 1200, 420), photoBackground);
            DrawCover(canvas, image, codec.EncodedOrigin, new SKRect(500, 0, 1200, 420));
            DrawCard(canvas, fundraiser);
            using var snapshot = surface.Snapshot();
            using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85) ?? throw new InvalidDataException("Could not encode social image.");
            var result = jpeg.ToArray();
            cache.Set(cacheKey, result, new MemoryCacheEntryOptions { Size = result.Length, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
            return result;
        }
        finally { generationSlots.Release(); }
    }

    private static void DrawCover(SKCanvas canvas, SKBitmap image, SKEncodedOrigin origin, SKRect bounds, bool contain = false)
    {
        var width = image.Width;
        var height = image.Height;
        var rotated = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var orientedWidth = rotated ? height : width;
        var orientedHeight = rotated ? width : height;
        var scale = contain ? Math.Min(bounds.Width / orientedWidth, bounds.Height / orientedHeight)
            : Math.Max(bounds.Width / orientedWidth, bounds.Height / orientedHeight);
        var verticalOffset = !contain && orientedWidth < orientedHeight ? 0.3f : 0.5f;
        canvas.Save();
        canvas.ClipRect(bounds);
        canvas.Translate(bounds.Left + (bounds.Width - orientedWidth * scale) / 2, bounds.Top + (bounds.Height - orientedHeight * scale) * verticalOffset);
        canvas.Scale(scale);
        var transform = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix { ScaleX = -1, ScaleY = 1, TransX = width, Persp2 = 1 },
            SKEncodedOrigin.BottomRight => new SKMatrix { ScaleX = -1, ScaleY = -1, TransX = width, TransY = height, Persp2 = 1 },
            SKEncodedOrigin.BottomLeft => new SKMatrix { ScaleX = 1, ScaleY = -1, TransY = height, Persp2 = 1 },
            SKEncodedOrigin.LeftTop => new SKMatrix { SkewX = 1, SkewY = 1, Persp2 = 1 },
            SKEncodedOrigin.RightTop => new SKMatrix { SkewX = -1, SkewY = 1, TransX = height, Persp2 = 1 },
            SKEncodedOrigin.RightBottom => new SKMatrix { SkewX = -1, SkewY = -1, TransX = height, TransY = width, Persp2 = 1 },
            SKEncodedOrigin.LeftBottom => new SKMatrix { SkewX = 1, SkewY = -1, TransY = width, Persp2 = 1 },
            _ => SKMatrix.Identity
        };
        canvas.Concat(transform);
        using var sourceImage = SKImage.FromBitmap(image);
        canvas.DrawImage(sourceImage, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));
        canvas.Restore();
    }

    public async Task<(byte[] Main, byte[] Story)> GenerateVideoCardsAsync(SocialImageData fundraiser, string story, Uri publicOrigin, CancellationToken cancellationToken)
    {
        var data = await ReadCoverAsync(fundraiser.Source, publicOrigin, cancellationToken);
        using var encoded = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(encoded);
        if (codec is null || (long)codec.Info.Width * codec.Info.Height > MaxPixels)
            throw new InvalidDataException("Fundraiser cover has invalid or excessive dimensions.");
        using var image = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Fundraiser cover could not be decoded.");
        using var regular = LoadFont("Lato-Regular.ttf");
        using var bold = LoadFont("Lato-Bold.ttf");
        using var cjk = LoadFont("NotoSansCJKsc-Regular.otf");
        using var arabic = LoadFont("NotoSansArabic.ttf");
        using var hebrew = LoadFont("NotoSansHebrew.ttf");
        using var devanagari = LoadFont("NotoSansDevanagari.ttf");
        using var emoji = LoadFont("NotoEmoji.ttf");
        using var unifont = LoadFont("Unifont.otf");
        using var unifontUpper = LoadFont("UnifontUpper.otf");
        using var titleText = new CardText(true, bold, cjk, arabic, hebrew, devanagari, emoji, unifont, unifontUpper);
        using var bodyText = new CardText(false, regular, cjk, arabic, hebrew, devanagari, emoji, unifont, unifontUpper);
        using var logo = SKBitmap.Decode(Path.Combine(environment.WebRootPath, "kindwell-logo.png"))
            ?? throw new InvalidDataException("Kindwell logo is missing or invalid.");
        using var logoImage = SKImage.FromBitmap(logo);
        using var ink = new SKPaint { Color = new SKColor(16, 62, 49), IsAntialias = true };
        using var muted = new SKPaint { Color = new SKColor(78, 103, 89), IsAntialias = true };
        using var lime = new SKPaint { Color = new SKColor(196, 230, 104), IsAntialias = true };
        using var track = new SKPaint { Color = new SKColor(220, 233, 213), IsAntialias = true };
        using var yellow = new SKPaint { Color = new SKColor(255, 223, 113), IsAntialias = true };

        byte[] Render(bool isStory)
        {
            using var surface = SKSurface.Create(new SKImageInfo(1080, 1920)) ?? throw new InvalidDataException("Could not create video frame.");
            var canvas = surface.Canvas;
            canvas.Clear(isStory ? new SKColor(213, 244, 158) : new SKColor(245, 250, 237));
            var photoLeft = isStory ? 724 : 572;
            using (var photoBackground = new SKPaint { Color = new SKColor(220, 233, 213) })
                canvas.DrawRect(new SKRect(photoLeft, 0, 1080, 1000), photoBackground);
            DrawCover(canvas, image, codec.EncodedOrigin, new SKRect(photoLeft, 0, 1080, 1000), contain: true);
            canvas.DrawImage(logoImage, new SKRect(55, 110, 127, 182), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using (var brand = new SKFont(bold, 48)) canvas.DrawText("kindwell", 145, 165, brand, ink);
            if (isStory)
            {
                using (var label = new SKFont(bold, 32)) canvas.DrawText("THE STORY", 55, 295, label, muted);
                var excerpt = story.Trim();
                if (excerpt.Length > 240)
                {
                    var end = StringInfo.ParseCombiningCharacters(excerpt).Last(index => index <= 240);
                    excerpt = excerpt[..end].TrimEnd() + "…";
                }
                DrawWrappedText(canvas, excerpt.Length > 0 ? excerpt : "No story provided yet.", bodyText, ink, 55, 340, 620, 850, 12, 55);
                DrawWrappedText(canvas, "Organized by " + (string.IsNullOrWhiteSpace(fundraiser.Organizer) ? "Organizer not specified" : fundraiser.Organizer), bodyText, muted, 55, 1365, 620, 110, 2, 38);
                DrawWrappedText(canvas, fundraiser.Title, bodyText, muted, 55, 1490, 620, 80, 2, 30);
                using var quote = new SKFont(bold, 160);
                canvas.DrawText("“", 960, 930, quote, yellow);
            }
            else
            {
                DrawWrappedText(canvas, fundraiser.Title, titleText, ink, 55, 290, 465, 440, 5, 75);
                DrawWrappedText(canvas, "Organized by " + (string.IsNullOrWhiteSpace(fundraiser.Organizer) ? "Organizer not specified" : fundraiser.Organizer), bodyText, muted, 55, 805, 465, 110, 2, 38);
                DrawWrappedText(canvas, Money(fundraiser.Raised) + " raised", titleText, ink, 55, 1170, 465, 65, 1, 48);
                DrawWrappedText(canvas, "of " + Money(fundraiser.Goal), bodyText, muted, 55, 1240, 465, 55, 1, 38);
                canvas.DrawRoundRect(new SKRect(55, 1335, 520, 1353), 9, 9, track);
                var fraction = fundraiser.Goal > 0 ? Math.Clamp((double)fundraiser.Raised / fundraiser.Goal, 0, 1) : 0;
                if (fraction > 0) canvas.DrawRoundRect(new SKRect(55, 1335, 55 + (float)(465 * fraction), 1353), 9, 9, lime);
                using (var funded = new SKFont(regular, 32)) canvas.DrawText($"{Math.Round(fraction * 100, MidpointRounding.AwayFromZero)}% funded", 55, 1410, funded, muted);
                canvas.DrawRoundRect(new SKRect(680, 840, 1035, 915), 38, 38, yellow);
                using var badge = new SKFont(bold, 32);
                canvas.DrawText("Give a little good", 857, 889, SKTextAlign.Center, badge, ink);
            }
            canvas.DrawRoundRect(new SKRect(70, 1665, 1010, 1830), 35, 35, yellow);
            using (var callout = new SKFont(bold, 48))
            {
                canvas.DrawText("Make a Difference", 540, 1735, SKTextAlign.Center, callout, ink);
                canvas.DrawText("Donate at Kindwell.care", 540, 1795, SKTextAlign.Center, callout, ink);
            }
            using var snapshot = surface.Snapshot();
            using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("Could not encode video frame.");
            return png.ToArray();
        }
        return (Render(false), Render(true));
    }

    private async Task<byte[]> ReadCoverAsync(string source, Uri publicOrigin, CancellationToken cancellationToken)
    {
        var file = LocalFile(source, publicOrigin);
        if (file is not null)
        {
            if (!file.Exists || file.IsDirectory) throw new FileNotFoundException("Fundraiser cover image was not found.");
            if (file.Length > MaxBytes) throw new InvalidDataException("Fundraiser cover image exceeds the size limit.");
            await using var stream = file.CreateReadStream();
            return await ReadLimitedAsync(stream, cancellationToken);
        }
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Fundraiser cover must be a local image or a public HTTPS URL.");
        return await DownloadAsync(uri, cancellationToken);
    }

    private void DrawCard(SKCanvas canvas, SocialImageData fundraiser)
    {
        using var regular = SKTypeface.FromFile(Path.Combine(environment.ContentRootPath, "SocialAssets", "Lato-Regular.ttf"))
            ?? throw new InvalidDataException("Social image regular font is missing or invalid.");
        using var bold = SKTypeface.FromFile(Path.Combine(environment.ContentRootPath, "SocialAssets", "Lato-Bold.ttf"))
            ?? throw new InvalidDataException("Social image bold font is missing or invalid.");
        using var cjk = LoadFont("NotoSansCJKsc-Regular.otf");
        using var arabic = LoadFont("NotoSansArabic.ttf");
        using var hebrew = LoadFont("NotoSansHebrew.ttf");
        using var devanagari = LoadFont("NotoSansDevanagari.ttf");
        using var emoji = LoadFont("NotoEmoji.ttf");
        using var unifont = LoadFont("Unifont.otf");
        using var unifontUpper = LoadFont("UnifontUpper.otf");
        using var logo = SKBitmap.Decode(Path.Combine(environment.WebRootPath, "kindwell-logo.png"))
            ?? throw new InvalidDataException("Kindwell logo is missing or invalid.");
        using var ink = new SKPaint { Color = new SKColor(30, 37, 34), IsAntialias = true };
        using var muted = new SKPaint { Color = new SKColor(88, 98, 91), IsAntialias = true };
        using var green = new SKPaint { Color = new SKColor(21, 91, 67), IsAntialias = true };
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var logoImage = SKImage.FromBitmap(logo);
        canvas.DrawImage(logoImage, new SKRect(32, 30, 86, 84), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using (var brandFont = new SKFont(bold, 30))
            canvas.DrawText("Kindwell", 99, 69, brandFont, green);

        using var titleText = new CardText(true, bold, cjk, arabic, hebrew, devanagari, emoji, unifont, unifontUpper);
        using var organizerText = new CardText(false, regular, cjk, arabic, hebrew, devanagari, emoji, unifont, unifontUpper);
        DrawWrappedText(canvas, fundraiser.Title, titleText, ink, 32, 148, 430, 260, 4, 46);
        using (var labelFont = new SKFont(bold, 29))
            canvas.DrawText("Organized by", 32, 478, labelFont, muted);
        DrawWrappedText(canvas, string.IsNullOrWhiteSpace(fundraiser.Organizer) ? "Organizer not specified" : fundraiser.Organizer,
            organizerText, ink, 32, 523, 420, 95, 3, 35);

        canvas.DrawCircle(600, 455, 111, white);
        using var track = new SKPaint { Color = new SKColor(232, 238, 233), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 19, StrokeCap = SKStrokeCap.Round };
        canvas.DrawCircle(600, 455, 91, track);
        using var progress = new SKPaint { Color = new SKColor(76, 158, 85), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 19, StrokeCap = SKStrokeCap.Round };
        var fraction = fundraiser.Goal > 0 ? Math.Clamp((double)fundraiser.Raised / fundraiser.Goal, 0, 1) : 0;
        if (fraction >= 1) canvas.DrawCircle(600, 455, 91, progress);
        else if (fraction > 0) canvas.DrawArc(new SKRect(509, 364, 691, 546), -90, (float)(360 * fraction), false, progress);
        using var heart = new SKPath();
        heart.MoveTo(600, 479);
        heart.CubicTo(585, 466, 570, 455, 570, 443);
        heart.CubicTo(570, 426, 590, 420, 600, 435);
        heart.CubicTo(610, 420, 630, 426, 630, 443);
        heart.CubicTo(630, 455, 615, 466, 600, 479);
        heart.Close();
        using var heartFill = new SKPaint { Color = progress.Color, IsAntialias = true };
        canvas.DrawPath(heart, heartFill);

        var amount = fundraiser.Goal > 0 && fundraiser.Raised < fundraiser.Goal
            ? Money(fundraiser.Goal - Math.Max(0, fundraiser.Raised)) + " to goal"
            : Money(Math.Max(0, fundraiser.Raised)) + " raised";
        DrawBadge(canvas, amount, bold, 707, 425, 330, new SKColor(197, 239, 112), ink);
        DrawBadge(canvas, "Donate now!", bold, 852, 514, 292, new SKColor(255, 215, 72), ink);
    }

    private SKTypeface LoadFont(string filename) =>
        SKTypeface.FromFile(Path.Combine(environment.ContentRootPath, "SocialAssets", filename))
        ?? throw new InvalidDataException($"Social image font {filename} is missing or invalid.");

    private static string Money(long cents) =>
        "$" + (cents / 100m).ToString("#,##0.##", CultureInfo.GetCultureInfo("en-US"));

    private static void DrawBadge(SKCanvas canvas, string text, SKTypeface typeface, float x, float y, float width, SKColor color, SKPaint ink)
    {
        using var fill = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(x, y, x + width, y + 60), 30, 30, fill);
        using var font = new SKFont(typeface, 31);
        while (font.Size > 18 && font.MeasureText(text) > width - 32) font.Size--;
        canvas.DrawText(text, x + width / 2, y + 41, SKTextAlign.Center, font, ink);
    }

    private static void DrawWrappedText(SKCanvas canvas, string text, CardText font, SKPaint paint,
        float x, float top, float width, float height, int maxLines, int maxSize)
    {
        font.Size = maxSize;
        List<string> lines;
        do
        {
            lines = WrapText(text, font, width);
            if ((lines.Count <= maxLines && font.Size * (1 + Math.Max(0, lines.Count - 1) * 1.18f) <= height) || font.Size <= 1) break;
            font.Size--;
        } while (true);

        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            var last = lines[^1];
            while (last.Length > 0 && font.Measure(last + "…") > width)
                last = last[..StringInfo.ParseCombiningCharacters(last)[^1]];
            lines[^1] = last.TrimEnd() + "…";
        }
        var lineHeight = font.Size * 1.18f;
        for (var i = 0; i < lines.Count; i++)
            font.Draw(canvas, lines[i], x, top + font.Size + i * lineHeight, paint);
    }

    private static List<string> WrapText(string text, CardText font, float width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (font.Measure(candidate) <= width) { line = candidate; continue; }
            if (line.Length > 0) { lines.Add(line); line = ""; }
            var elements = StringInfo.GetTextElementEnumerator(word);
            while (elements.MoveNext())
            {
                var part = elements.GetTextElement();
                if (line.Length > 0 && font.Measure(line + part) > width) { lines.Add(line); line = ""; }
                line += part;
            }
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    private sealed class CardText : IDisposable
    {
        private readonly SKFont[] fonts;
        private readonly SKShaper[] shapers;

        public CardText(bool boldFallback, params SKTypeface[] typefaces)
        {
            fonts = typefaces.Select((face, index) => new SKFont(face, 1) { Embolden = boldFallback && index > 0 }).ToArray();
            shapers = typefaces.Select(face => new SKShaper(face)).ToArray();
        }

        public float Size
        {
            get => fonts[0].Size;
            set { foreach (var font in fonts) font.Size = value; }
        }

        public float Measure(string text) => Runs(text).Sum(run => shapers[run.Font].Shape(run.Text, fonts[run.Font]).Width);

        public void Draw(SKCanvas canvas, string text, float x, float y, SKPaint paint)
        {
            var runs = Runs(text);
            var firstLetter = text.EnumerateRunes().FirstOrDefault(Rune.IsLetter);
            if (firstLetter.Value is >= 0x590 and <= 0x8ff) runs.Reverse();
            foreach (var run in runs)
            {
                canvas.DrawShapedText(shapers[run.Font], run.Text, x, y, SKTextAlign.Left, fonts[run.Font], paint);
                x += shapers[run.Font].Shape(run.Text, fonts[run.Font]).Width;
            }
        }

        private List<(string Text, int Font)> Runs(string text)
        {
            var runs = new List<(string Text, int Font)>();
            var elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var supported = string.Concat(element.EnumerateRunes()
                    .Where(rune => Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format && rune.Value is not (0xfe0e or 0xfe0f))
                    .Select(rune => rune.ToString()));
                var index = Array.FindIndex(fonts, font => font.ContainsGlyphs(supported));
                if (index < 0) throw new InvalidDataException("Fundraiser text contains characters unavailable in the social image fonts.");
                if (runs.Count > 0 && runs[^1].Font == index)
                    runs[^1] = (runs[^1].Text + element, index);
                else
                    runs.Add((element, index));
            }
            return runs;
        }

        public void Dispose()
        {
            foreach (var shaper in shapers) shaper.Dispose();
            foreach (var font in fonts) font.Dispose();
        }
    }
    private Microsoft.Extensions.FileProviders.IFileInfo? LocalFile(string source, Uri publicOrigin)
    {
        string path;
        if (source.StartsWith('/') && !source.StartsWith("//", StringComparison.Ordinal))
            path = source;
        else if (Uri.TryCreate(source, UriKind.Absolute, out var absolute) && absolute.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(absolute.UserInfo) && absolute.Authority.Equals(publicOrigin.Authority, StringComparison.OrdinalIgnoreCase))
            path = absolute.AbsolutePath;
        else return null;

        path = Uri.UnescapeDataString(path.Split('?', '#')[0]);
        if (path.Contains('\\') || path.Split('/').Any(part => part is "." or "..") || !path.StartsWith("/uploads/", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid local fundraiser image path.");
        return environment.WebRootFileProvider.GetFileInfo(path);
    }

    private async Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("Fundraiser image redirects must use public HTTPS URLs.");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (response.Headers.Location is null || redirects == 3) throw new InvalidDataException("Invalid fundraiser image redirect.");
                uri = new Uri(uri, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Fundraiser cover image exceeds the size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await ReadLimitedAsync(stream, cancellationToken);
        }
        throw new InvalidDataException("Too many fundraiser image redirects.");
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > MaxBytes) throw new InvalidDataException("Fundraiser cover image exceeds the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static async ValueTask<Stream> ConnectPublicAddress(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        foreach (var address in addresses.Where(IsPublicAddress))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Fundraiser image host has no reachable public address.");
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (bytes[0] & 0xe0) == 0x20
                && !(bytes[0] == 0x20 && bytes[1] == 0x02)
                && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0)
                && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        return address.AddressFamily == AddressFamily.InterNetwork
            && bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 || bytes[1] == 2))
            && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100)))
            && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }

    public void Dispose()
    {
        client.Dispose();
        cache.Dispose();
        generationSlots.Dispose();
    }
}
