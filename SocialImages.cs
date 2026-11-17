using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

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

    public string Version(string source, Uri publicOrigin)
    {
        var local = LocalFile(source, publicOrigin);
        var stamp = local is { Exists: true } ? local.LastModified.UtcTicks.ToString() : "";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + stamp));
        return Convert.ToHexString(hash).ToLowerInvariant()[..20];
    }

    public async Task<byte[]> GenerateAsync(string source, Uri publicOrigin, string cacheKey, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(cacheKey, out byte[]? cached) && cached is not null) return cached;
        await generationSlots.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(cacheKey, out cached) && cached is not null) return cached;
            var file = LocalFile(source, publicOrigin);
            byte[] data;
            if (file is not null)
            {
                if (!file.Exists || file.IsDirectory) throw new FileNotFoundException("Fundraiser cover image was not found.");
                if (file.Length > MaxBytes) throw new InvalidDataException("Fundraiser cover image exceeds the size limit.");
                await using var stream = file.CreateReadStream();
                data = await ReadLimitedAsync(stream, cancellationToken);
            }
            else
            {
                if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new InvalidDataException("Fundraiser cover must be a local image or a public HTTPS URL.");
                data = await DownloadAsync(uri, cancellationToken);
            }

            using var encoded = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(encoded);
            if (codec is null || (long)codec.Info.Width * codec.Info.Height > MaxPixels)
                throw new InvalidDataException("Fundraiser cover has invalid or excessive dimensions.");
            using var image = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Fundraiser cover could not be decoded.");
            var width = image.Width;
            var height = image.Height;
            var origin = codec.EncodedOrigin;
            var rotated = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var orientedWidth = rotated ? height : width;
            var orientedHeight = rotated ? width : height;
            var scale = orientedWidth < orientedHeight
                ? Math.Min(1200f / orientedWidth, 630f / orientedHeight)
                : Math.Max(1200f / orientedWidth, 630f / orientedHeight);
            using var surface = SKSurface.Create(new SKImageInfo(1200, 630)) ?? throw new InvalidDataException("Could not create social image.");
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(30, 37, 39));
            canvas.Translate((1200 - orientedWidth * scale) / 2, (630 - orientedHeight * scale) / 2);
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
            using var snapshot = surface.Snapshot();
            using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85) ?? throw new InvalidDataException("Could not encode social image.");
            var result = jpeg.ToArray();
            cache.Set(cacheKey, result, new MemoryCacheEntryOptions { Size = result.Length, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
            return result;
        }
        finally { generationSlots.Release(); }
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
