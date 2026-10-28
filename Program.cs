using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var fundraiserCategories = new[] { "Medical", "Emergency", "Education", "Community", "Animals", "Memorial", "Environment", "Other" };
var databasePath = Path.Combine(Environment.GetEnvironmentVariable("DATA_DIR") is { Length: > 0 } dataDir ? dataDir : Path.Combine(builder.Environment.ContentRootPath, "App_Data"), "kindwell.db");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true }.ToString();

builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRateLimiter(options => options.AddPolicy("admin-login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
{
    PermitLimit = 5,
    Window = TimeSpan.FromMinutes(1),
    QueueLimit = 0,
    AutoReplenishment = true
})));
builder.Services.AddRateLimiter(options => options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
{
    PermitLimit = 10,
    Window = TimeSpan.FromMinutes(1),
    QueueLimit = 0,
    AutoReplenishment = true
})));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "kindwell.admin";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
}).AddCookie("UserCookie", options =>
{
    options.Cookie.Name = "kindwell.user";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
});
builder.Services.AddAuthorization(options => options.AddPolicy("Administrator", policy => policy.RequireRole("Administrator")));

var flutterwaveHttp = new HttpClient { BaseAddress = new Uri("https://api.flutterwave.com/v3/"), Timeout = TimeSpan.FromSeconds(20) };
var app = builder.Build();
await InitializeDatabase();
var paylioHttp = new HttpClient { BaseAddress = new Uri("https://paylio.org/api/v1/"), Timeout = TimeSpan.FromSeconds(20) };
var paylioActive = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAYLIO_API_KEY"));
var paylioCurrencies = new HashSet<string>(["USD", "EUR", "CAD", "INR"]);
IReadOnlyList<string> donationCurrencies = CurrencyRules.ParseAllowed(Environment.GetEnvironmentVariable("DONATION_CURRENCIES"));
// Paylio can only charge USD, EUR, CAD and INR, so other currencies are not offered while it is the active provider.
if (paylioActive) donationCurrencies = donationCurrencies.Where(paylioCurrencies.Contains).ToList();
if (donationCurrencies.Count == 0) donationCurrencies = ["USD"];
var rateCacheMinutes = int.TryParse(Environment.GetEnvironmentVariable("RATE_CACHE_MINUTES"), out var configuredCache) ? Math.Clamp(configuredCache, 1, 1440) : 60;
var rateMaxStaleHours = int.TryParse(Environment.GetEnvironmentVariable("RATE_MAX_STALE_HOURS"), out var configuredStale) ? Math.Clamp(configuredStale, 1, 168) : 24;
var rateService = new ExchangeRateService(
    OpenDatabase,
    new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
    app.Logger,
    Environment.GetEnvironmentVariable("EXCHANGE_RATE_API_URL") ?? "https://open.er-api.com/v6/latest/USD",
    TimeSpan.FromMinutes(rateCacheMinutes),
    TimeSpan.FromHours(rateMaxStaleHours),
    donationCurrencies);
var geoIp = new GeoIpService(
    new HttpClient { Timeout = TimeSpan.FromSeconds(5) },
    app.Logger,
    Environment.GetEnvironmentVariable("GEOIP_API_URL") ?? "https://ipwho.is/{ip}");
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
    KnownNetworks = { },
    KnownProxies = { }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/api/csrf", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { token = tokens.RequestToken });
});

app.MapGet("/api/fundraisers", async () =>
{
    await using var db = await OpenDatabase();
    await using var command = db.CreateCommand();
    command.CommandText = "SELECT slug,title,summary,story,image_url,goal,raised+reported_raised,donation_count,reported_raised,reported_donation_count,category,organizer_name,organizer_location,organizer_profile_image,organizer_bio FROM fundraisers WHERE published=1 ORDER BY featured DESC, created_at DESC";
    await using var reader = await command.ExecuteReaderAsync();
    var campaigns = new List<object>();
    while (await reader.ReadAsync()) campaigns.Add(ReadFundraiser(reader));
    return Results.Ok(campaigns);
});

app.MapGet("/api/fundraisers/search", async (string? q, string? category, string? sort, int page = 1, int pageSize = 12) =>
{
    var search = q?.Trim() ?? "";
    if (search.Length > 120) return Results.BadRequest(new { error = "Search text must be 120 characters or fewer." });
    if (!string.IsNullOrWhiteSpace(category) && !fundraiserCategories.Contains(category, StringComparer.Ordinal))
        return Results.BadRequest(new { error = "Choose a valid fundraiser category." });

    page = Math.Max(page, 1);
    pageSize = Math.Clamp(pageSize, 1, 24);
    var orderBy = sort switch
    {
        "most-donated" => "(donation_count + reported_donation_count) DESC, created_at DESC",
        "closest-to-goal" => "CASE WHEN goal > 0 THEN CAST(raised + reported_raised AS REAL) / goal ELSE 0 END DESC, created_at DESC",
        "highest-raised" => "(raised + reported_raised) DESC, created_at DESC",
        "recent" => "created_at DESC",
        _ => "featured DESC, created_at DESC"
    };
    var escapedSearch = search.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
    var searchPattern = $"%{escapedSearch}%";

    await using var db = await OpenDatabase();
    await using var countCommand = db.CreateCommand();
    countCommand.CommandText = """
        SELECT COUNT(*) FROM fundraisers
        WHERE published=1
          AND ($category IS NULL OR category=$category)
          AND ($search='' OR title LIKE $pattern ESCAPE '\' OR summary LIKE $pattern ESCAPE '\' OR story LIKE $pattern ESCAPE '\' OR category LIKE $pattern ESCAPE '\' OR organizer_name LIKE $pattern ESCAPE '\' OR organizer_location LIKE $pattern ESCAPE '\')
        """;
    countCommand.Parameters.AddWithValue("$category", string.IsNullOrWhiteSpace(category) ? DBNull.Value : category);
    countCommand.Parameters.AddWithValue("$search", search);
    countCommand.Parameters.AddWithValue("$pattern", searchPattern);
    var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    var pages = (int)Math.Ceiling(total / (double)pageSize);
    page = Math.Min(page, Math.Max(1, pages));

    await using var command = db.CreateCommand();
    command.CommandText = $"""
        SELECT slug,title,summary,story,image_url,goal,raised+reported_raised,donation_count+reported_donation_count,reported_raised,reported_donation_count,category,organizer_name,organizer_location,organizer_profile_image,organizer_bio
        FROM fundraisers
        WHERE published=1
          AND ($category IS NULL OR category=$category)
          AND ($search='' OR title LIKE $pattern ESCAPE '\' OR summary LIKE $pattern ESCAPE '\' OR story LIKE $pattern ESCAPE '\' OR category LIKE $pattern ESCAPE '\' OR organizer_name LIKE $pattern ESCAPE '\' OR organizer_location LIKE $pattern ESCAPE '\')
        ORDER BY {orderBy}
        LIMIT $limit OFFSET $offset
        """;
    command.Parameters.AddWithValue("$category", string.IsNullOrWhiteSpace(category) ? DBNull.Value : category);
    command.Parameters.AddWithValue("$search", search);
    command.Parameters.AddWithValue("$pattern", searchPattern);
    command.Parameters.AddWithValue("$limit", pageSize);
    command.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
    await using var reader = await command.ExecuteReaderAsync();
    var items = new List<object>();
    while (await reader.ReadAsync()) items.Add(ReadFundraiser(reader));
    return Results.Ok(new { items, page, pageSize, total, pages });
});

app.MapGet("/api/fundraisers/{slug}", async (string slug) =>
{
    await using var db = await OpenDatabase();
    await using var command = db.CreateCommand();
    command.CommandText = "SELECT id,slug,title,summary,story,image_url,goal,raised+reported_raised,donation_count,raised,reported_raised,reported_donation_count,category,organizer_name,organizer_location,organizer_profile_image,organizer_bio FROM fundraisers WHERE slug=$slug AND published=1";
    command.Parameters.AddWithValue("$slug", slug);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return Results.NotFound(new { error = "This fundraiser could not be found." });
    var campaign = new { id = reader.GetInt64(0), slug = reader.GetString(1), title = reader.GetString(2), summary = reader.GetString(3), story = reader.GetString(4), image = reader.GetString(5), goal = reader.GetInt64(6), raised = reader.GetInt64(7), donations = reader.GetInt64(8), verifiedRaised = reader.GetInt64(9), reportedRaised = reader.GetInt64(10), reportedDonations = reader.GetInt64(11), category = reader.GetString(12), organizerName = reader.GetString(13), organizerLocation = reader.GetString(14), organizerProfileImage = reader.GetString(15), organizerBio = reader.GetString(16) };
    await reader.CloseAsync();
    command.CommandText = "SELECT donor_name,is_anonymous,amount,created_at FROM donations WHERE fundraiser_id=$id AND status='succeeded' ORDER BY created_at DESC LIMIT 8";
    command.Parameters.Clear();
    command.Parameters.AddWithValue("$id", campaign.id);
    await using var donationsReader = await command.ExecuteReaderAsync();
    var donations = new List<object>();
    while (await donationsReader.ReadAsync()) donations.Add(new { name = donationsReader.GetInt64(1) == 1 ? "A kind donor" : donationsReader.GetString(0), amount = donationsReader.GetInt64(2), date = donationsReader.GetString(3) });
    await donationsReader.CloseAsync();
    command.CommandText = "SELECT title,body,created_at FROM campaign_updates WHERE fundraiser_id=$id ORDER BY created_at DESC LIMIT 20";
    await using var updatesReader = await command.ExecuteReaderAsync();
    var updates = new List<object>();
    while (await updatesReader.ReadAsync()) updates.Add(new { title = updatesReader.GetString(0), body = updatesReader.GetString(1), date = updatesReader.GetString(2) });
    return Results.Ok(new { campaign, recentDonations = donations, updates });
});

app.MapGet("/api/people/search", async (string? q, int page = 1, int pageSize = 12) =>
{
    var search = q?.Trim() ?? "";
    if (search.Length > 120) return Results.BadRequest(new { error = "Search text must be 120 characters or fewer." });
    page = Math.Max(page, 1);
    pageSize = Math.Clamp(pageSize, 1, 24);
    var escaped = search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    var pattern = $"%{escaped}%";
    await using var db = await OpenDatabase();
    await using var command = db.CreateCommand();
    command.CommandText = """
        SELECT organizer_name,organizer_location,organizer_profile_image,organizer_bio
        FROM fundraisers
        WHERE published=1
          AND organizer_name <> ''
          AND ($search='' OR organizer_name LIKE $pattern ESCAPE '\' OR organizer_location LIKE $pattern ESCAPE '\')
        GROUP BY organizer_name,organizer_location,organizer_profile_image,organizer_bio
        ORDER BY organizer_name COLLATE NOCASE ASC
        LIMIT $limit OFFSET $offset
        """;
    command.Parameters.AddWithValue("$search", search);
    command.Parameters.AddWithValue("$pattern", pattern);
    command.Parameters.AddWithValue("$limit", pageSize);
    command.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
    await using var reader = await command.ExecuteReaderAsync();
    var items = new List<object>();
    while (await reader.ReadAsync())
    {
        var name = reader.GetString(0);
        items.Add(new
        {
            name,
            slug = Slugify(name),
            location = reader.GetString(1),
            profileImage = reader.GetString(2),
            bio = reader.GetString(3)
        });
    }
    return Results.Ok(new { items, page, pageSize, total = items.Count, pages = Math.Max(1, (int)Math.Ceiling(items.Count / (double)pageSize)) });
});

app.MapGet("/api/people/{slug}", async (string slug) =>
{
    await using var db = await OpenDatabase();
    await using var command = db.CreateCommand();
    command.CommandText = "SELECT slug,title,summary,story,image_url,goal,raised+reported_raised,donation_count+reported_donation_count,reported_raised,reported_donation_count,category,organizer_name,organizer_location,organizer_profile_image,organizer_bio FROM fundraisers WHERE published=1 ORDER BY created_at DESC";
    await using var reader = await command.ExecuteReaderAsync();
    var matches = new List<object>();
    object? profile = null;
    while (await reader.ReadAsync())
    {
        var name = reader.IsDBNull(11) ? "" : reader.GetString(11);
        if (string.IsNullOrWhiteSpace(name)) continue;
        if (Slugify(name).Equals(slug, StringComparison.OrdinalIgnoreCase))
        {
            if (profile is null)
            {
                profile = new
                {
                    name,
                    slug = Slugify(name),
                    location = reader.GetString(12),
                    profileImage = reader.GetString(13),
                    bio = reader.GetString(14)
                };
            }
            matches.Add(ReadFundraiser(reader));
        }
    }
    if (profile is null) return Results.NotFound(new { error = "This profile could not be found." });
    return Results.Ok(new { profile, fundraisers = matches });
});

var oauthStates = new System.Collections.Concurrent.ConcurrentDictionary<string, (string Provider, DateTimeOffset Expires)>();
var oauthHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

string? OAuthClientId(string provider) => Environment.GetEnvironmentVariable(provider.ToUpperInvariant() + "_CLIENT_ID") is { Length: > 0 } id ? id : null;
bool OAuthConfigured(string provider) => OAuthClientId(provider) is not null && (provider == "apple"
    ? !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPLE_TEAM_ID")) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPLE_KEY_ID")) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPLE_PRIVATE_KEY"))
    : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(provider.ToUpperInvariant() + "_CLIENT_SECRET")));
string OAuthOrigin(HttpContext context)
{
    var configured = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
    return string.IsNullOrWhiteSpace(configured) ? $"{context.Request.Scheme}://{context.Request.Host}" : configured.TrimEnd('/');
}
static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

// Apple's client secret is a short-lived ES256 JWT signed with the developer's private key.
string AppleClientSecret()
{
    var key = Environment.GetEnvironmentVariable("APPLE_PRIVATE_KEY")!.Replace("\\n", "\n");
    var pem = string.Concat(key.Split('\n').Where(line => !line.StartsWith("-----", StringComparison.Ordinal))).Trim();
    using var ecdsa = ECDsa.Create();
    ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(pem), out _);
    var now = DateTimeOffset.UtcNow;
    var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = Environment.GetEnvironmentVariable("APPLE_KEY_ID") }));
    var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = Environment.GetEnvironmentVariable("APPLE_TEAM_ID"), iat = now.ToUnixTimeSeconds(), exp = now.AddMinutes(5).ToUnixTimeSeconds(), aud = "https://appleid.apple.com", sub = OAuthClientId("apple") }));
    var signature = ecdsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    return $"{header}.{claims}.{Base64Url(signature)}";
}

app.MapGet("/api/auth/me", async (HttpContext context) =>
{
    var result = await context.AuthenticateAsync("UserCookie");
    var providers = new { google = OAuthConfigured("google"), apple = OAuthConfigured("apple"), facebook = OAuthConfigured("facebook") };
    if (!result.Succeeded) return Results.Ok(new { user = (object?)null, providers });
    return Results.Ok(new { user = new { name = result.Principal!.Identity?.Name ?? "friend", provider = result.Principal.FindFirstValue("provider") }, providers });
});

app.MapPost("/api/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest();
    await context.SignOutAsync("UserCookie");
    return Results.Ok(new { ok = true });
});

app.MapGet("/auth/{provider}", (string provider, HttpContext context) =>
{
    provider = provider.ToLowerInvariant();
    if (provider is not ("google" or "apple" or "facebook") || !OAuthConfigured(provider)) return Results.Redirect("/login?error=not_configured");
    foreach (var stale in oauthStates.Where(pair => pair.Value.Expires < DateTimeOffset.UtcNow).Select(pair => pair.Key).ToList()) oauthStates.TryRemove(stale, out _);
    var state = Base64Url(RandomNumberGenerator.GetBytes(24));
    oauthStates[state] = (provider, DateTimeOffset.UtcNow.AddMinutes(10));
    var redirect = Uri.EscapeDataString($"{OAuthOrigin(context)}/auth/{provider}/callback");
    var clientId = Uri.EscapeDataString(OAuthClientId(provider)!);
    var url = provider switch
    {
        "google" => $"https://accounts.google.com/o/oauth2/v2/auth?client_id={clientId}&redirect_uri={redirect}&response_type=code&scope={Uri.EscapeDataString("openid email profile")}&state={state}&prompt=select_account",
        "facebook" => $"https://www.facebook.com/v19.0/dialog/oauth?client_id={clientId}&redirect_uri={redirect}&response_type=code&scope={Uri.EscapeDataString("public_profile,email")}&state={state}",
        _ => $"https://appleid.apple.com/auth/authorize?client_id={clientId}&redirect_uri={redirect}&response_type=code&response_mode=form_post&scope={Uri.EscapeDataString("name email")}&state={state}"
    };
    return Results.Redirect(url);
});

// Apple returns by POST (form_post); Google and Facebook return by GET.
async Task<IResult> OAuthCallback(string provider, HttpContext context, CancellationToken cancellationToken)
{
    provider = provider.ToLowerInvariant();
    var query = context.Request.HasFormContentType ? context.Request.Form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()) : context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
    var state = query.GetValueOrDefault("state") ?? "";
    if (!oauthStates.TryRemove(state, out var saved) || saved.Provider != provider || saved.Expires < DateTimeOffset.UtcNow || !OAuthConfigured(provider)) return Results.Redirect("/login?error=failed");
    var code = query.GetValueOrDefault("code");
    if (string.IsNullOrWhiteSpace(code)) return Results.Redirect("/login?error=cancelled");
    try
    {
        var redirectUri = $"{OAuthOrigin(context)}/auth/{provider}/callback";
        var secret = provider == "apple" ? AppleClientSecret() : Environment.GetEnvironmentVariable(provider.ToUpperInvariant() + "_CLIENT_SECRET")!;
        var tokenUrl = provider switch { "google" => "https://oauth2.googleapis.com/token", "facebook" => "https://graph.facebook.com/v19.0/oauth/access_token", _ => "https://appleid.apple.com/auth/token" };
        using var tokenResponse = await oauthHttp.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri, ["client_id"] = OAuthClientId(provider)!, ["client_secret"] = secret
        }), cancellationToken);
        using var tokens = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
        if (!tokenResponse.IsSuccessStatusCode) { app.Logger.LogWarning("{Provider} sign-in token exchange was rejected (HTTP {Status}).", provider, (int)tokenResponse.StatusCode); return Results.Redirect("/login?error=failed"); }

        string? subject, name = null, email = null;
        if (provider == "apple")
        {
            // The id_token comes straight from Apple's token endpoint over TLS, so its payload can be read without a separate signature check.
            var parts = tokens.RootElement.GetProperty("id_token").GetString()!.Split('.');
            var padded = parts[1].Replace('-', '+').Replace('_', '/'); padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(padded));
            subject = claims.RootElement.GetProperty("sub").GetString();
            email = claims.RootElement.TryGetProperty("email", out var appleEmail) ? appleEmail.GetString() : null;
            // Apple only sends the name on the very first sign-in.
            if (query.GetValueOrDefault("user") is { Length: > 0 } userJson)
            {
                try
                {
                    using var user = JsonDocument.Parse(userJson);
                    if (user.RootElement.TryGetProperty("name", out var n)) name = $"{(n.TryGetProperty("firstName", out var f) ? f.GetString() : "")} {(n.TryGetProperty("lastName", out var l) ? l.GetString() : "")}".Trim();
                }
                catch (JsonException) { }
            }
        }
        else
        {
            var accessToken = tokens.RootElement.GetProperty("access_token").GetString()!;
            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, provider == "google" ? "https://openidconnect.googleapis.com/v1/userinfo" : "https://graph.facebook.com/me?fields=id,name,email");
            profileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var profileResponse = await oauthHttp.SendAsync(profileRequest, cancellationToken);
            using var profile = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync(cancellationToken));
            if (!profileResponse.IsSuccessStatusCode) return Results.Redirect("/login?error=failed");
            subject = profile.RootElement.TryGetProperty(provider == "google" ? "sub" : "id", out var id) ? id.GetString() : null;
            name = profile.RootElement.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            email = profile.RootElement.TryGetProperty("email", out var em) ? em.GetString() : null;
        }
        if (string.IsNullOrWhiteSpace(subject)) return Results.Redirect("/login?error=failed");
        var display = string.IsNullOrWhiteSpace(name) ? (email?.Split('@')[0] ?? "friend") : name!;
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, $"{provider}:{subject}"), new Claim(ClaimTypes.Name, display),
            new Claim(ClaimTypes.Email, email ?? ""), new Claim("provider", provider)
        }, "UserCookie");
        await context.SignInAsync("UserCookie", new ClaimsPrincipal(identity));
        return Results.Redirect("/login");
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or FormatException or CryptographicException)
    {
        app.Logger.LogWarning("{Provider} sign-in failed ({Type}).", provider, ex.GetType().Name);
        return Results.Redirect("/login?error=failed");
    }
}
app.MapGet("/auth/{provider}/callback", OAuthCallback);
app.MapPost("/auth/{provider}/callback", OAuthCallback).DisableAntiforgery();

const int PasswordIterations = 210_000;
string HashPassword(string password)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
    return $"pbkdf2${PasswordIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
}
bool VerifyPassword(string password, string stored)
{
    var parts = stored.Split('$');
    if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations)) return false;
    var expected = Convert.FromBase64String(parts[3]);
    return CryptographicOperations.FixedTimeEquals(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]), iterations, HashAlgorithmName.SHA256, expected.Length), expected);
}
string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
// Used to keep response time similar whether or not an account exists.
var dummyPasswordHash = HashPassword("not-a-real-password");

async Task SignInUser(HttpContext context, long id, string name, string email)
{
    var identity = new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, $"email:{id}"), new Claim(ClaimTypes.Name, name),
        new Claim(ClaimTypes.Email, email), new Claim("provider", "email")
    }, "UserCookie");
    await context.SignInAsync("UserCookie", new ClaimsPrincipal(identity));
}

async Task SendResetEmail(string to, string link)
{
    var host = Environment.GetEnvironmentVariable("SMTP_HOST");
    if (string.IsNullOrWhiteSpace(host))
    {
        // Without SMTP the link is only written to the log in development so it can be tested locally.
        if (app.Environment.IsDevelopment()) app.Logger.LogWarning("SMTP is not configured. Password reset link for {Email}: {Link}", to, link);
        else app.Logger.LogError("Password reset requested but SMTP_HOST is not configured, so no email was sent.");
        return;
    }
    try
    {
        var from = Environment.GetEnvironmentVariable("SMTP_FROM") ?? "hello@kindwell.care";
        using var client = new System.Net.Mail.SmtpClient(host, int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var port) ? port : 587) { EnableSsl = Environment.GetEnvironmentVariable("SMTP_SSL") != "false", Timeout = 15000 };
        if (Environment.GetEnvironmentVariable("SMTP_USER") is { Length: > 0 } user) client.Credentials = new System.Net.NetworkCredential(user, Environment.GetEnvironmentVariable("SMTP_PASSWORD"));
        using var message = new System.Net.Mail.MailMessage(from, to, "Reset your Kindwell password", $"We received a request to reset your Kindwell password.\n\nChoose a new password here (the link works for 1 hour):\n{link}\n\nIf you didn't ask for this, you can safely ignore this email.");
        await client.SendMailAsync(message);
    }
    catch (Exception ex) { app.Logger.LogError("Could not send the password reset email ({Type}).", ex.GetType().Name); }
}

async Task<JsonElement?> ReadJson(HttpContext context)
{
    if (context.Request.ContentLength > 4096) return null;
    try { using var document = await JsonDocument.ParseAsync(context.Request.Body); return document.RootElement.Clone(); }
    catch (JsonException) { return null; }
}
string Field(JsonElement? body, string name) => body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

app.MapPost("/api/auth/signup", async (HttpContext context, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    var body = await ReadJson(context);
    var name = Field(body, "name").Trim(); var email = Field(body, "email").Trim().ToLowerInvariant(); var password = Field(body, "password");
    if (name.Length is 0 or > 80) return Results.BadRequest(new { error = "Enter your name." });
    if (!IsValidEmail(email)) return Results.BadRequest(new { error = "Enter a valid email address." });
    if (password.Length is < 8 or > 128) return Results.BadRequest(new { error = "Use a password with at least 8 characters." });
    await using var db = await OpenDatabase(cancellationToken);
    await using var insert = db.CreateCommand();
    insert.CommandText = "INSERT INTO users(email,name,password_hash,created_at) VALUES($e,$n,$p,$d); SELECT last_insert_rowid();";
    insert.Parameters.AddWithValue("$e", email); insert.Parameters.AddWithValue("$n", name);
    insert.Parameters.AddWithValue("$p", HashPassword(password)); insert.Parameters.AddWithValue("$d", DateTimeOffset.UtcNow.ToString("O"));
    long id;
    try { id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture); }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return Results.BadRequest(new { error = "An account with this email already exists. Try logging in." }); }
    await SignInUser(context, id, name, email);
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (HttpContext context, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    var body = await ReadJson(context);
    var email = Field(body, "email").Trim().ToLowerInvariant(); var password = Field(body, "password");
    long id = 0; string? name = null, hash = null;
    if (password.Length <= 128)
    {
        await using var db = await OpenDatabase(cancellationToken);
        await using var lookup = db.CreateCommand();
        lookup.CommandText = "SELECT id,name,password_hash FROM users WHERE email=$e";
        lookup.Parameters.AddWithValue("$e", email);
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken)) { id = reader.GetInt64(0); name = reader.GetString(1); hash = reader.GetString(2); }
    }
    var valid = VerifyPassword(password.Length <= 128 ? password : "", hash ?? dummyPasswordHash) && hash is not null;
    if (!valid) return Results.Json(new { error = "That email and password don't match." }, statusCode: 401);
    await SignInUser(context, id, name!, email);
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/forgot", async (HttpContext context, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    var email = Field(await ReadJson(context), "email").Trim().ToLowerInvariant();
    if (!IsValidEmail(email)) return Results.BadRequest(new { error = "Enter a valid email address." });
    await using var db = await OpenDatabase(cancellationToken);
    await using var lookup = db.CreateCommand();
    lookup.CommandText = "SELECT id FROM users WHERE email=$e";
    lookup.Parameters.AddWithValue("$e", email);
    if (await lookup.ExecuteScalarAsync(cancellationToken) is long userId)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        await using var insert = db.CreateCommand();
        insert.CommandText = "DELETE FROM password_resets WHERE user_id=$u OR expires_at<$now; INSERT INTO password_resets(user_id,token_hash,expires_at) VALUES($u,$h,$x)";
        insert.Parameters.AddWithValue("$u", userId); insert.Parameters.AddWithValue("$h", HashToken(token));
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); insert.Parameters.AddWithValue("$x", DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await SendResetEmail(email, $"{OAuthOrigin(context)}/reset-password?token={token}");
    }
    // The same answer is returned whether or not the account exists.
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/reset", async (HttpContext context, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    var body = await ReadJson(context);
    var token = Field(body, "token"); var password = Field(body, "password");
    if (password.Length is < 8 or > 128) return Results.BadRequest(new { error = "Use a password with at least 8 characters." });
    await using var db = await OpenDatabase(cancellationToken);
    await using var lookup = db.CreateCommand();
    lookup.CommandText = "SELECT id,user_id FROM password_resets WHERE token_hash=$h AND used=0 AND expires_at>$now";
    lookup.Parameters.AddWithValue("$h", HashToken(token)); lookup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
    long resetId, userId;
    await using (var reader = await lookup.ExecuteReaderAsync(cancellationToken))
    {
        if (!await reader.ReadAsync(cancellationToken)) return Results.BadRequest(new { error = "This reset link is invalid or has expired. Please request a new one." });
        resetId = reader.GetInt64(0); userId = reader.GetInt64(1);
    }
    await using var update = db.CreateCommand();
    update.CommandText = "UPDATE users SET password_hash=$p WHERE id=$u; UPDATE password_resets SET used=1 WHERE user_id=$u";
    update.Parameters.AddWithValue("$p", HashPassword(password)); update.Parameters.AddWithValue("$u", userId);
    await update.ExecuteNonQueryAsync(cancellationToken);
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("auth");

app.MapPost("/api/admin/login", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    var expectedEmail = builder.Configuration["ADMIN_EMAIL"];
    var expectedPassword = builder.Configuration["ADMIN_PASSWORD"];
    if (string.IsNullOrWhiteSpace(expectedEmail) || string.IsNullOrWhiteSpace(expectedPassword)) return Results.Problem("Admin access is not configured. Set ADMIN_EMAIL and ADMIN_PASSWORD before signing in.", statusCode: 503);
    using var payload = await JsonDocument.ParseAsync(context.Request.Body);
    var email = payload.RootElement.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
    var password = payload.RootElement.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
    if (!SecureEquals(email.Trim(), expectedEmail.Trim()) || !SecureEquals(password, expectedPassword)) return Results.Unauthorized();
    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, expectedEmail), new Claim(ClaimTypes.Role, "Administrator") }, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("admin-login");

app.MapPost("/api/admin/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest();
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { ok = true });
}).RequireAuthorization("Administrator");

var admin = app.MapGroup("/api/admin").RequireAuthorization("Administrator");
admin.MapGet("/start-requests", async (CancellationToken cancellationToken) =>
{
    await using var db = await OpenDatabase(cancellationToken);
    await using var command = db.CreateCommand();
    command.CommandText = "SELECT id,category,beneficiary,reason,goal_usd,timeline,name,email,created_at FROM start_requests ORDER BY id DESC LIMIT 200";
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var rows = new List<object>();
    while (await reader.ReadAsync(cancellationToken))
        rows.Add(new { id = reader.GetInt64(0), category = reader.GetString(1), beneficiary = reader.GetString(2), reason = reader.GetString(3), goalUsd = reader.GetInt64(4), timeline = reader.GetString(5), name = reader.GetString(6), email = reader.GetString(7), createdAt = reader.GetString(8) });
    return Results.Ok(rows);
});
admin.MapGet("/overview", async () =>
{
    await using var db = await OpenDatabase();
    await using var command = db.CreateCommand();
    command.CommandText = """
        SELECT
          (SELECT COUNT(*) FROM fundraisers WHERE published=1),
          (SELECT COUNT(*) FROM fundraisers WHERE published=0),
          (SELECT COUNT(*) FROM donations WHERE status='succeeded'),
          (SELECT COALESCE(SUM(amount),0) FROM donations WHERE status='succeeded'),
          (SELECT COUNT(*) FROM donations WHERE status='pending'),
          (SELECT COUNT(*) FROM donations WHERE status='failed')
        """;
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return Results.Ok(new
    {
        publishedFundraisers = reader.GetInt64(0),
        draftFundraisers = reader.GetInt64(1),
        confirmedDonations = reader.GetInt64(2),
        confirmedGross = reader.GetInt64(3),
        pendingPayments = reader.GetInt64(4),
        failedPayments = reader.GetInt64(5)
    });
});
admin.MapGet("/reports", async (string? from, string? to) =>
{
    if (from is not null && !DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        return Results.BadRequest(new { error = "Start date must use YYYY-MM-DD format." });
    if (to is not null && !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        return Results.BadRequest(new { error = "End date must use YYYY-MM-DD format." });
    if (from is not null && to is not null && string.CompareOrdinal(from, to) > 0)
        return Results.BadRequest(new { error = "Start date must be on or before end date." });

    await using var db = await OpenDatabase();
    await using var summaryCommand = db.CreateCommand();
    summaryCommand.CommandText = """
        SELECT
          COUNT(CASE WHEN status='succeeded' THEN 1 END),
          (SELECT COALESCE(SUM(amount),0) FROM ledger_transactions
           WHERE ($from IS NULL OR date(occurred_at) >= $from)
             AND ($to IS NULL OR date(occurred_at) <= $to)),
          COUNT(CASE WHEN status='pending' THEN 1 END),
          COALESCE(SUM(CASE WHEN status='pending' THEN amount ELSE 0 END),0),
          COUNT(CASE WHEN status='failed' THEN 1 END),
          COALESCE(SUM(CASE WHEN status='failed' THEN amount ELSE 0 END),0)
        FROM donations
        WHERE ($from IS NULL OR date(CASE WHEN status='succeeded' THEN COALESCE(paid_at,created_at) ELSE created_at END) >= $from)
          AND ($to IS NULL OR date(CASE WHEN status='succeeded' THEN COALESCE(paid_at,created_at) ELSE created_at END) <= $to)
        """;
    summaryCommand.Parameters.AddWithValue("$from", from is null ? DBNull.Value : from);
    summaryCommand.Parameters.AddWithValue("$to", to is null ? DBNull.Value : to);
    await using var summaryReader = await summaryCommand.ExecuteReaderAsync();
    await summaryReader.ReadAsync();
    var summary = new
    {
        confirmedDonations = summaryReader.GetInt64(0),
        confirmedGross = summaryReader.GetInt64(1),
        pendingPayments = summaryReader.GetInt64(2),
        pendingAmount = summaryReader.GetInt64(3),
        failedPayments = summaryReader.GetInt64(4),
        failedAmount = summaryReader.GetInt64(5)
    };
    await summaryReader.CloseAsync();

    await using var dailyCommand = db.CreateCommand();
    dailyCommand.CommandText = """
        SELECT date(CASE WHEN status='succeeded' THEN COALESCE(paid_at,created_at) ELSE created_at END),
          COUNT(CASE WHEN status='succeeded' THEN 1 END),
          COALESCE(SUM(CASE WHEN status='succeeded' THEN amount ELSE 0 END),0),
          COUNT(CASE WHEN status='pending' THEN 1 END),
          COUNT(CASE WHEN status='failed' THEN 1 END)
        FROM donations
        WHERE ($from IS NULL OR date(CASE WHEN status='succeeded' THEN COALESCE(paid_at,created_at) ELSE created_at END) >= $from)
          AND ($to IS NULL OR date(CASE WHEN status='succeeded' THEN COALESCE(paid_at,created_at) ELSE created_at END) <= $to)
        GROUP BY date(CASE WHEN status='succeeded' THEN COALESCE(paid_at,created_at) ELSE created_at END)
        ORDER BY date(CASE WHEN status='succeeded' THEN COALESCE(paid_at,created_at) ELSE created_at END)
        """;
    dailyCommand.Parameters.AddWithValue("$from", from is null ? DBNull.Value : from);
    dailyCommand.Parameters.AddWithValue("$to", to is null ? DBNull.Value : to);
    await using var dailyReader = await dailyCommand.ExecuteReaderAsync();
    var daily = new List<object>();
    while (await dailyReader.ReadAsync())
        daily.Add(new
        {
            date = dailyReader.GetString(0),
            confirmedDonations = dailyReader.GetInt64(1),
            confirmedGross = dailyReader.GetInt64(2),
            pendingPayments = dailyReader.GetInt64(3),
            failedPayments = dailyReader.GetInt64(4)
        });
    return Results.Ok(new { from, to, summary, daily });
});
admin.MapGet("/fundraisers", async () =>
{
    await using var db = await OpenDatabase();
    await using var command = db.CreateCommand();
    command.CommandText = "SELECT id,slug,title,summary,story,image_url,goal,raised+reported_raised,donation_count+reported_donation_count,published,featured,reported_raised,reported_donation_count,category,organizer_name,organizer_location,organizer_profile_image,organizer_bio FROM fundraisers ORDER BY created_at DESC";
    await using var reader = await command.ExecuteReaderAsync();
    var campaigns = new List<object>();
    while (await reader.ReadAsync()) campaigns.Add(new { id = reader.GetInt64(0), slug = reader.GetString(1), title = reader.GetString(2), summary = reader.GetString(3), story = reader.GetString(4), image = reader.GetString(5), goal = reader.GetInt64(6), raised = reader.GetInt64(7), donations = reader.GetInt64(8), published = reader.GetInt64(9) == 1, featured = reader.GetInt64(10) == 1, reportedRaised = reader.GetInt64(11), reportedDonations = reader.GetInt64(12), category = reader.GetString(13), organizerName = reader.GetString(14), organizerLocation = reader.GetString(15), organizerProfileImage = reader.GetString(16), organizerBio = reader.GetString(17) });
    return Results.Ok(campaigns);
});
admin.MapGet("/donations", async (string? status, string? from, string? to, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) =>
{
    if (status is not null && status is not ("pending" or "succeeded" or "failed"))
        return Results.BadRequest(new { error = "Choose a valid payment status." });
    if (from is not null && !DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        return Results.BadRequest(new { error = "Start date must use YYYY-MM-DD format." });
    if (to is not null && !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        return Results.BadRequest(new { error = "End date must use YYYY-MM-DD format." });
    if (from is not null && to is not null && string.CompareOrdinal(from, to) > 0)
        return Results.BadRequest(new { error = "Start date must be on or before end date." });

    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);
    await using var db = await OpenDatabase(cancellationToken);
    await using var countCommand = db.CreateCommand();
    countCommand.CommandText = """
        SELECT COUNT(*) FROM donations
        WHERE ($status IS NULL OR status=$status)
          AND ($from IS NULL OR date(CASE WHEN status='succeeded' THEN COALESCE(paid_at,created_at) ELSE created_at END) >= $from)
          AND ($to IS NULL OR date(CASE WHEN status='succeeded' THEN COALESCE(paid_at,created_at) ELSE created_at END) <= $to)
        """;
    countCommand.Parameters.AddWithValue("$status", status is null ? DBNull.Value : status);
    countCommand.Parameters.AddWithValue("$from", from is null ? DBNull.Value : from);
    countCommand.Parameters.AddWithValue("$to", to is null ? DBNull.Value : to);
    var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    var pages = (int)Math.Ceiling(total / (double)pageSize);
    page = Math.Min(page, Math.Max(1, pages));

    await using var command = db.CreateCommand();
    command.CommandText = """
        SELECT donations.id,fundraisers.title,donations.amount,donations.donor_name,donations.is_anonymous,
               donations.status,COALESCE(donations.paid_at,donations.created_at)
        FROM donations JOIN fundraisers ON fundraisers.id=donations.fundraiser_id
        WHERE ($status IS NULL OR donations.status=$status)
          AND ($from IS NULL OR date(CASE WHEN donations.status='succeeded' THEN COALESCE(donations.paid_at,donations.created_at) ELSE donations.created_at END) >= $from)
          AND ($to IS NULL OR date(CASE WHEN donations.status='succeeded' THEN COALESCE(donations.paid_at,donations.created_at) ELSE donations.created_at END) <= $to)
        ORDER BY COALESCE(donations.paid_at,donations.created_at) DESC,donations.id DESC
        LIMIT $limit OFFSET $offset
        """;
    command.Parameters.AddWithValue("$status", status is null ? DBNull.Value : status);
    command.Parameters.AddWithValue("$from", from is null ? DBNull.Value : from);
    command.Parameters.AddWithValue("$to", to is null ? DBNull.Value : to);
    command.Parameters.AddWithValue("$limit", pageSize);
    command.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var donations = new List<object>();
    while (await reader.ReadAsync()) donations.Add(new { id = reader.GetInt64(0), campaign = reader.GetString(1), amount = reader.GetInt64(2), donor = reader.GetInt64(4) == 1 ? "Anonymous" : reader.GetString(3), status = reader.GetString(5), date = reader.GetString(6) });
    return Results.Ok(new { items = donations, page, pageSize, total, pages });
});
admin.MapGet("/ledger", async (string? from, string? to, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) =>
{
    if (from is not null && !DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        return Results.BadRequest(new { error = "Start date must use YYYY-MM-DD format." });
    if (to is not null && !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        return Results.BadRequest(new { error = "End date must use YYYY-MM-DD format." });
    if (from is not null && to is not null && string.CompareOrdinal(from, to) > 0)
        return Results.BadRequest(new { error = "Start date must be on or before end date." });

    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);
    await using var db = await OpenDatabase(cancellationToken);
    await using var countCommand = db.CreateCommand();
    countCommand.CommandText = """
        SELECT COUNT(*) FROM ledger_transactions
        WHERE ($from IS NULL OR date(occurred_at) >= $from)
          AND ($to IS NULL OR date(occurred_at) <= $to)
        """;
    countCommand.Parameters.AddWithValue("$from", from is null ? DBNull.Value : from);
    countCommand.Parameters.AddWithValue("$to", to is null ? DBNull.Value : to);
    var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    var pages = (int)Math.Ceiling(total / (double)pageSize);
    page = Math.Min(page, Math.Max(1, pages));

    await using var command = db.CreateCommand();
    command.CommandText = """
        SELECT ledger_transactions.id,ledger_transactions.donation_id,fundraisers.title,
               ledger_transactions.currency,ledger_transactions.amount,ledger_transactions.occurred_at,
               ledger_transactions.provider_reference
        FROM ledger_transactions
        JOIN donations ON donations.id=ledger_transactions.donation_id
        JOIN fundraisers ON fundraisers.id=donations.fundraiser_id
        WHERE ($from IS NULL OR date(ledger_transactions.occurred_at) >= $from)
          AND ($to IS NULL OR date(ledger_transactions.occurred_at) <= $to)
        ORDER BY ledger_transactions.occurred_at DESC,ledger_transactions.id DESC
        LIMIT $limit OFFSET $offset
        """;
    command.Parameters.AddWithValue("$from", from is null ? DBNull.Value : from);
    command.Parameters.AddWithValue("$to", to is null ? DBNull.Value : to);
    command.Parameters.AddWithValue("$limit", pageSize);
    command.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var entries = new List<object>();
    while (await reader.ReadAsync(cancellationToken))
    {
        entries.Add(new
        {
            id = reader.GetInt64(0),
            donationId = reader.GetInt64(1),
            campaign = reader.GetString(2),
            currency = reader.GetString(3),
            amount = reader.GetInt64(4),
            date = reader.GetString(5),
            providerReference = reader.IsDBNull(6) ? null : reader.GetString(6),
            debitAccount = "processor_clearing",
            creditAccount = "campaign_funds_payable"
        });
    }
    return Results.Ok(new { items = entries, page, pageSize, total, pages });
});
admin.MapPost("/fundraisers/{id:long}/updates", async (long id, HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    var input = await context.Request.ReadFromJsonAsync<CampaignUpdateInput>();
    if (input is null || string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 120)
        return Results.BadRequest(new { error = "Add an update title of 1 to 120 characters." });
    if (string.IsNullOrWhiteSpace(input.Body) || input.Body.Trim().Length > 5000)
        return Results.BadRequest(new { error = "Add update text of 1 to 5,000 characters." });

    var createdAt = DateTimeOffset.UtcNow.ToString("O");
    await using var db = await OpenDatabase();
    await using var command = db.CreateCommand();
    command.CommandText = """
        INSERT INTO campaign_updates(fundraiser_id,title,body,created_at)
        SELECT id,$title,$body,$created_at FROM fundraisers WHERE id=$id
        """;
    command.Parameters.AddWithValue("$id", id);
    command.Parameters.AddWithValue("$title", input.Title.Trim());
    command.Parameters.AddWithValue("$body", input.Body.Trim());
    command.Parameters.AddWithValue("$created_at", createdAt);
    if (await command.ExecuteNonQueryAsync() == 0) return Results.NotFound(new { error = "This fundraiser could not be found." });
    command.CommandText = "SELECT last_insert_rowid()";
    var updateId = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    return Results.Ok(new { id = updateId, title = input.Title.Trim(), body = input.Body.Trim(), date = createdAt });
});
admin.MapPost("/uploads", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("image");
    if (file is null || file.Length == 0 || file.Length > 8_000_000) return Results.BadRequest(new { error = "Choose an image smaller than 8 MB." });
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp")) return Results.BadRequest(new { error = "Use a JPG, PNG, or WebP image." });
    var fileName = $"{Guid.NewGuid():N}{extension}";
    var uploadFolder = Path.Combine(builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"), "uploads");
    Directory.CreateDirectory(uploadFolder);
    await using var output = System.IO.File.Create(Path.Combine(uploadFolder, fileName));
    await file.CopyToAsync(output);
    return Results.Ok(new { url = $"/uploads/{fileName}" });
});
admin.MapPost("/fundraisers", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    var input = await context.Request.ReadFromJsonAsync<FundraiserInput>();
    var error = ValidateFundraiser(input);
    if (error is not null) return Results.BadRequest(new { error });
    var slug = Slugify(input!.Title);
    await using var db = await OpenDatabase();
    await using var command = db.CreateCommand();
    command.CommandText = "INSERT INTO fundraisers(slug,title,summary,story,image_url,goal,reported_raised,reported_donation_count,published,featured,created_at,category,organizer_name,organizer_location,organizer_profile_image,organizer_bio) VALUES($slug,$title,$summary,$story,$image,$goal,$reported_raised,$reported_donation_count,$published,$featured,$created,$category,$organizer_name,$organizer_location,$organizer_profile_image,$organizer_bio)";
    AddFundraiserParameters(command, input, slug);
    command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
    try { await command.ExecuteNonQueryAsync(); }
    catch (SqliteException) { return Results.Conflict(new { error = "A fundraiser with a similar title already exists." }); }
    return Results.Ok(new { slug });
});
admin.MapPut("/fundraisers/{id:long}", async (long id, HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    var input = await context.Request.ReadFromJsonAsync<FundraiserInput>();
    var error = ValidateFundraiser(input);
    if (error is not null) return Results.BadRequest(new { error });
    await using var db = await OpenDatabase();
    await using var command = db.CreateCommand();
    command.CommandText = "UPDATE fundraisers SET title=$title,summary=$summary,story=$story,image_url=$image,goal=$goal,reported_raised=$reported_raised,reported_donation_count=$reported_donation_count,published=$published,featured=$featured,category=$category,organizer_name=$organizer_name,organizer_location=$organizer_location,organizer_profile_image=$organizer_profile_image,organizer_bio=$organizer_bio WHERE id=$id";
    AddFundraiserParameters(command, input!, Slugify(input!.Title));
    command.Parameters.AddWithValue("$id", id);
    if (await command.ExecuteNonQueryAsync() == 0) return Results.NotFound();
    return Results.Ok(new { ok = true });
});
admin.MapDelete("/fundraisers/{id:long}", async (long id, HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest();
    await using var db = await OpenDatabase();
    await using var donations = db.CreateCommand();
    donations.CommandText = "SELECT COUNT(*) FROM donations WHERE fundraiser_id=$id";
    donations.Parameters.AddWithValue("$id", id);
    if (Convert.ToInt64(await donations.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0)
        return Results.Conflict(new { error = "This fundraiser has donation records and cannot be deleted. Unpublish it to preserve its financial history." });
    await using var command = db.CreateCommand();
    command.CommandText = "DELETE FROM fundraisers WHERE id=$id";
    command.Parameters.AddWithValue("$id", id);
    return await command.ExecuteNonQueryAsync() == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
});

app.MapPost("/api/donations/checkout", async (HttpContext context, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    var secretKey = Environment.GetEnvironmentVariable("FLUTTERWAVE_SECRET_KEY");
    var paylioWallet = Environment.GetEnvironmentVariable("PAYLIO_WALLET_ADDRESS");
    if (paylioActive ? string.IsNullOrWhiteSpace(paylioWallet) : string.IsNullOrWhiteSpace(secretKey)) return Results.Problem("Secure checkout is not configured yet. Please try again later.", statusCode: 503);
    secretKey ??= "";
    using var payload = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
    var root = payload.RootElement;
    var slug = root.TryGetProperty("slug", out var slugValue) ? slugValue.GetString()?.Trim() : null;
    var amount = root.TryGetProperty("amount", out var amountValue) && amountValue.TryGetInt64(out var parsedAmount) ? parsedAmount : 0;
    var currency = (root.TryGetProperty("currency", out var currencyValue) ? currencyValue.GetString() : null)?.Trim().ToUpperInvariant() ?? "USD";
    var donorName = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString()?.Trim() ?? "" : "";
    var donorEmail = root.TryGetProperty("email", out var emailValue) ? emailValue.GetString()?.Trim() ?? "" : "";
    var anonymous = root.TryGetProperty("anonymous", out var anonymousValue) && anonymousValue.ValueKind == JsonValueKind.True;
    if (slug is null || donorName.Length > 80) return Results.BadRequest(new { error = "Check your details and try again." });
    if (!donationCurrencies.Contains(currency)) return Results.BadRequest(new { error = "That currency isn't available for donations." });
    if (!IsValidEmail(donorEmail)) return Results.BadRequest(new { error = "Enter a valid email address for your payment receipt." });

    RateSnapshot? rates = null;
    var unitsPerUsd = 1m;
    if (currency != "USD")
    {
        rates = await rateService.GetAsync(cancellationToken);
        if (rates is null || !rates.UnitsPerUsd.TryGetValue(currency, out unitsPerUsd))
            return Results.Problem("Exchange rates are temporarily unavailable for that currency. Please try USD or try again shortly.", statusCode: 503);
    }
    var usdCents = amount > 0 ? DonationMoney.ToUsdCents(amount, unitsPerUsd) : 0;
    if (usdCents < 100 || usdCents > 10000000) return Results.BadRequest(new { error = "Choose a gift worth between $1 and $100,000 USD." });

    var configuredOrigin = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
    var origin = string.IsNullOrWhiteSpace(configuredOrigin) ? $"{context.Request.Scheme}://{context.Request.Host}" : configuredOrigin.TrimEnd('/');
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var publicUri) || (app.Environment.IsProduction() && (publicUri.Scheme != "https" || string.IsNullOrWhiteSpace(configuredOrigin))))
        return Results.Problem("A secure public URL is required to start checkout.", statusCode: 503);

    await using var db = await OpenDatabase(cancellationToken);
    await using var fundraiserCommand = db.CreateCommand();
    fundraiserCommand.CommandText = "SELECT id,title FROM fundraisers WHERE slug=$slug AND published=1";
    fundraiserCommand.Parameters.AddWithValue("$slug", slug);
    await using var fundraiserReader = await fundraiserCommand.ExecuteReaderAsync(cancellationToken);
    if (!await fundraiserReader.ReadAsync(cancellationToken)) return Results.NotFound(new { error = "This fundraiser is no longer available." });
    var fundraiserId = fundraiserReader.GetInt64(0);
    var title = fundraiserReader.GetString(1);
    await fundraiserReader.CloseAsync();

    var txRef = $"kw-{Guid.NewGuid():N}";
    await using var insert = db.CreateCommand();
    insert.CommandText = """
        INSERT INTO donations(fundraiser_id,amount,donor_name,is_anonymous,status,provider,provider_session_id,created_at,currency,original_amount_minor,exchange_rate,rate_fetched_at,        rate_source,provider_mode,donor_email)
                VALUES($id,$amount,$name,$anonymous,'pending',$provider,$tx_ref,$date,$currency,$original,$rate,$rate_at,$rate_source,$mode,$email);
        SELECT last_insert_rowid();
        """;
    insert.Parameters.AddWithValue("$id", fundraiserId);
    insert.Parameters.AddWithValue("$amount", usdCents);
    insert.Parameters.AddWithValue("$currency", currency);
    insert.Parameters.AddWithValue("$original", amount);
    insert.Parameters.AddWithValue("$rate", DonationMoney.FormatRate(unitsPerUsd));
    insert.Parameters.AddWithValue("$rate_at", rates is null ? DBNull.Value : rates.FetchedAt.ToString("O"));
    insert.Parameters.AddWithValue("$rate_source", rates is null ? DBNull.Value : rates.Source);
    insert.Parameters.AddWithValue("$mode", CurrentMode());
    insert.Parameters.AddWithValue("$provider", paylioActive ? "paylio" : "flutterwave");
    insert.Parameters.AddWithValue("$name", donorName.Length == 0 ? "Kind donor" : donorName);
    insert.Parameters.AddWithValue("$anonymous", anonymous ? 1 : 0);
    insert.Parameters.AddWithValue("$tx_ref", txRef);
    insert.Parameters.AddWithValue("$email", donorEmail);
    insert.Parameters.AddWithValue("$date", DateTimeOffset.UtcNow.ToString("O"));
    var donationId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

    if (paylioActive)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "wallet")
            {
                Content = JsonContent.Create(new
                {
                    address = paylioWallet,
                    // Paylio rejects non-https callbacks. Over plain-http local development the webhook is unreachable anyway, so the periodic payment re-check confirms the donation instead.
                    callback = $"{(origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? origin : "https://localhost.invalid")}/api/paylio/webhook?ref={txRef}",
                    amount = (amount / 100m).ToString("0.00", CultureInfo.InvariantCulture),
                    currency,
                    email = donorEmail,
                    note = $"Kindwell {txRef}"
                })
            };
            request.Headers.Add("X-API-Key", Environment.GetEnvironmentVariable("PAYLIO_API_KEY"));
            using var response = await paylioHttp.SendAsync(request, cancellationToken);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root2 = result.RootElement;
            var paymentId = root2.TryGetProperty("payment_id", out var pid) ? pid.GetString() : null;
            var ipnToken = root2.TryGetProperty("ipn_token", out var tok) ? tok.GetString() : null;
            var checkoutUrl = root2.TryGetProperty("checkout_url", out var cu) ? cu.GetString() : null;
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(paymentId) || string.IsNullOrWhiteSpace(ipnToken) || string.IsNullOrWhiteSpace(checkoutUrl))
            {
                app.Logger.LogWarning("Paylio rejected checkout for {Currency} (HTTP {Status}).", currency, (int)response.StatusCode);
                throw new HttpRequestException("Paylio did not return a checkout link.");
            }
            await using var save = db.CreateCommand();
            save.CommandText = "UPDATE donations SET provider_transaction_id=$pid,provider_secret=$secret WHERE id=$id";
            save.Parameters.AddWithValue("$pid", paymentId);
            save.Parameters.AddWithValue("$secret", ipnToken);
            save.Parameters.AddWithValue("$id", donationId);
            await save.ExecuteNonQueryAsync(cancellationToken);
            return Results.Ok(new { url = checkoutUrl, txRef, amount = amount / 100m, currency, campaign = title });
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            await using var markFailed = db.CreateCommand();
            markFailed.CommandText = "UPDATE donations SET status='failed' WHERE id=$id AND status='pending'";
            markFailed.Parameters.AddWithValue("$id", donationId);
            await markFailed.ExecuteNonQueryAsync(CancellationToken.None);
            return Results.Problem($"We couldn't start secure checkout in {currency}. No payment was taken. Please try again shortly.", statusCode: 502);
        }
    }

    // With a Flutterwave public key the card is entered in Flutterwave's secure popup on our own payment screen; otherwise fall back to the hosted page.
    var publicKey = Environment.GetEnvironmentVariable("FLUTTERWAVE_PUBLIC_KEY");
    // Kindwell's own card form: the donor never sees a Flutterwave page. The charge is created later with the card details.
    if (CardFormEnabled())
        return Results.Ok(new { card = true, txRef, amount = amount / 100m, currency, campaign = title });

    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "payments")
        {
            Content = JsonContent.Create(new
            {
                tx_ref = txRef,
                amount = amount / 100m,
                currency,
                redirect_url = $"{origin}/thank-you?tx_ref={txRef}",
                customer = new { email = donorEmail, name = donorName.Length == 0 ? "Kind donor" : donorName },
                customizations = new { title = "Kindwell", description = $"Donation to {title}", logo = $"{origin}/kindwell-logo.png" },
                meta = new { donation_id = donationId }
            })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secretKey);
        using var response = await flutterwaveHttp.SendAsync(request, cancellationToken);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var link = response.IsSuccessStatusCode
            && result.RootElement.TryGetProperty("status", out var statusValue) && statusValue.GetString() == "success"
            && result.RootElement.TryGetProperty("data", out var dataValue) && dataValue.TryGetProperty("link", out var linkValue)
            ? linkValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(link))
        {
            app.Logger.LogWarning("Flutterwave rejected checkout for {Currency} (HTTP {Status}).", currency, (int)response.StatusCode);
            throw new HttpRequestException("Flutterwave did not return a checkout link.");
        }
        // The hosted link also proves Flutterwave accepts this currency for the account; it is kept as a fallback if the popup can't load.
        if (!string.IsNullOrWhiteSpace(publicKey))
            return Results.Ok(new { inline = true, url = link, publicKey, txRef, amount = amount / 100m, currency, email = donorEmail, name = donorName.Length == 0 ? "Kind donor" : donorName, campaign = title });
        return Results.Ok(new { url = link });
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
    {
        await using var markFailed = db.CreateCommand();
        markFailed.CommandText = "UPDATE donations SET status='failed' WHERE id=$id AND status='pending'";
        markFailed.Parameters.AddWithValue("$id", donationId);
        await markFailed.ExecuteNonQueryAsync(CancellationToken.None);
        return Results.Problem($"We couldn't start secure checkout in {currency}. No payment was taken. Try another currency if this continues.", statusCode: 502);
    }
});

// Card details only pass through memory on their way to Flutterwave's charge API. They are never written to the database, logs or admin views.
bool CardFormEnabled()
{
    if (paylioActive) return false;
    var secret = Environment.GetEnvironmentVariable("FLUTTERWAVE_SECRET_KEY");
    if (string.IsNullOrWhiteSpace(secret)) return false;
    return secret.StartsWith("FLWSECK_TEST", StringComparison.Ordinal) || Environment.GetEnvironmentVariable("CARD_FORM_PCI_CONFIRMED") == "true";
}

string EncryptionKey()
{
    var configured = Environment.GetEnvironmentVariable("FLUTTERWAVE_ENCRYPTION_KEY");
    if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
    var secret = Environment.GetEnvironmentVariable("FLUTTERWAVE_SECRET_KEY") ?? "";
    var adjusted = secret.Replace("FLWSECK-", "", StringComparison.Ordinal);
    var md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
    return adjusted[..Math.Min(12, adjusted.Length)] + md5[^12..];
}

string Encrypt3Des(string plain)
{
    using var tdes = TripleDES.Create();
    tdes.Mode = CipherMode.ECB;
    tdes.Padding = PaddingMode.PKCS7;
    tdes.Key = Encoding.UTF8.GetBytes(EncryptionKey());
    var bytes = Encoding.UTF8.GetBytes(plain);
    return Convert.ToBase64String(tdes.CreateEncryptor().TransformFinalBlock(bytes, 0, bytes.Length));
}

bool PassesLuhn(string digits)
{
    var sum = 0;
    for (var i = 0; i < digits.Length; i++)
    {
        var d = digits[digits.Length - 1 - i] - '0';
        if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
        sum += d;
    }
    return sum % 10 == 0;
}

app.MapPost("/api/donations/charge", async (HttpContext context, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    if (!CardFormEnabled()) return Results.Problem("Card payments are not enabled.", statusCode: 503);
    if (context.Request.ContentLength > 4096) return Results.BadRequest(new { error = "Check your card details and try again." });
    JsonDocument payload;
    try { payload = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken); }
    catch (JsonException) { return Results.BadRequest(new { error = "Check your card details and try again." }); }
    using (payload)
    {
        var root = payload.RootElement;
        string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";
        var txRef = Str(root, "txRef");
        root.TryGetProperty("card", out var card);
        var number = new string(Str(card, "number").Where(char.IsDigit).ToArray());
        var cvv = Str(card, "cvv");
        var month = Str(card, "month");
        var year = Str(card, "year");
        var pin = Str(root, "pin");
        if (!txRef.StartsWith("kw-", StringComparison.Ordinal) || txRef.Length > 100) return Results.NotFound();
        if (number.Length is < 12 or > 19 || !PassesLuhn(number)) return Results.BadRequest(new { error = "Check your card number." });
        if (cvv.Length is < 3 or > 4 || !cvv.All(char.IsDigit)) return Results.BadRequest(new { error = "Check your CVV." });
        if (!int.TryParse(month, out var mm) || mm is < 1 or > 12 || !int.TryParse(year, out var yy)) return Results.BadRequest(new { error = "Check the card's expiry date." });
        if (yy >= 100) yy %= 100;
        var now = DateTime.UtcNow;
        if (2000 + yy < now.Year || (2000 + yy == now.Year && mm < now.Month)) return Results.BadRequest(new { error = "This card has expired." });
        if (pin.Length > 0 && (pin.Length is < 4 or > 6 || !pin.All(char.IsDigit))) return Results.BadRequest(new { error = "Enter the PIN as 4 to 6 digits." });

        string? currency, email, name;
        long minor;
        await using (var db = await OpenDatabase(cancellationToken))
        await using (var lookup = db.CreateCommand())
        {
            lookup.CommandText = "SELECT currency,COALESCE(original_amount_minor,amount),donor_email,donor_name FROM donations WHERE provider_session_id=$tx AND status='pending'";
            lookup.Parameters.AddWithValue("$tx", txRef);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Results.NotFound(new { error = "This payment is no longer open. Please start again." });
            currency = reader.GetString(0); minor = reader.GetInt64(1);
            email = reader.IsDBNull(2) ? null : reader.GetString(2); name = reader.GetString(3);
        }
        if (string.IsNullOrWhiteSpace(email)) return Results.BadRequest(new { error = "Please start the donation again." });

        var configuredOrigin = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
        var origin = string.IsNullOrWhiteSpace(configuredOrigin) ? $"{context.Request.Scheme}://{context.Request.Host}" : configuredOrigin.TrimEnd('/');
        var chargeBody = new Dictionary<string, object>
        {
            ["card_number"] = number, ["cvv"] = cvv, ["expiry_month"] = mm.ToString("00"), ["expiry_year"] = yy.ToString("00"),
            ["currency"] = currency, ["amount"] = (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture), ["email"] = email, ["fullname"] = name,
            ["tx_ref"] = txRef, ["redirect_url"] = $"{origin}/thank-you?tx_ref={txRef}"
        };
        if (pin.Length > 0) chargeBody["authorization"] = new Dictionary<string, string> { ["mode"] = "pin", ["pin"] = pin };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "charges?type=card") { Content = JsonContent.Create(new { client = Encrypt3Des(JsonSerializer.Serialize(chargeBody)) }) };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("FLUTTERWAVE_SECRET_KEY"));
            using var response = await flutterwaveHttp.SendAsync(request, cancellationToken);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return Results.Ok(ChargeOutcome(result.RootElement, txRef));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            app.Logger.LogWarning("Flutterwave card charge could not be completed ({Type}).", ex.GetType().Name);
            return Results.Problem("We couldn't reach the payment network. You were not charged. Please try again.", statusCode: 502);
        }
    }
});

app.MapPost("/api/donations/charge/validate", async (HttpContext context, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    if (!CardFormEnabled()) return Results.Problem("Card payments are not enabled.", statusCode: 503);
    using var payload = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
    var root = payload.RootElement;
    var txRef = root.TryGetProperty("txRef", out var t) ? t.GetString() ?? "" : "";
    var flwRef = root.TryGetProperty("flwRef", out var f) ? f.GetString() ?? "" : "";
    var otp = root.TryGetProperty("otp", out var o) ? o.GetString()?.Trim() ?? "" : "";
    if (!txRef.StartsWith("kw-", StringComparison.Ordinal) || txRef.Length > 100 || flwRef.Length is 0 or > 100 || otp.Length is < 3 or > 10 || !otp.All(char.IsLetterOrDigit))
        return Results.BadRequest(new { error = "Enter the code you were sent." });
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "validate-charge") { Content = JsonContent.Create(new { otp, flw_ref = flwRef, type = "card" }) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("FLUTTERWAVE_SECRET_KEY"));
        using var response = await flutterwaveHttp.SendAsync(request, cancellationToken);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return Results.Ok(ChargeOutcome(result.RootElement, txRef));
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
    {
        return Results.Problem("We couldn't reach the payment network. Please try again.", statusCode: 502);
    }
});

object ChargeOutcome(JsonElement body, string txRef)
{
    string? Get(JsonElement e, params string[] path)
    {
        foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }
    var apiStatus = Get(body, "status");
    var message = Get(body, "message") ?? "The payment could not be completed.";
    var mode = Get(body, "meta", "authorization", "mode");
    var flwRef = Get(body, "data", "flw_ref");
    var dataStatus = Get(body, "data", "status");
    if (apiStatus != "success")
    {
        if (message.Contains("DCC", StringComparison.OrdinalIgnoreCase))
            message = "This currency isn't enabled for card payments on our payment account yet. Please choose another currency (such as NGN, GBP or EUR) and try again.";
        return new { step = "failed", message };
    }
    if (mode == "pin") return new { step = "pin", message = "Enter your card PIN to continue." };
    if (mode == "otp") return new { step = "otp", flwRef, message = Get(body, "data", "processor_response") ?? "Enter the code your bank sent you." };
    if (mode == "redirect") return new { step = "redirect", redirect = Get(body, "meta", "authorization", "redirect") };
    if (mode == "avs_noauth") return new { step = "failed", message = "This card needs address verification, which isn't supported here. Please try another card." };
    if (dataStatus == "successful") return new { step = "verify", flwRef };
    if (dataStatus == "failed") return new { step = "failed", message = Get(body, "data", "processor_response") ?? message };
    return new { step = "verify", flwRef };
}

app.MapPost("/api/flutterwave/webhook", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    var expectedHash = Environment.GetEnvironmentVariable("FLUTTERWAVE_WEBHOOK_HASH");
    if (string.IsNullOrWhiteSpace(expectedHash)) return Results.Problem("Webhook is not configured.", statusCode: 503);
    if (request.ContentLength > 65_536) return Results.BadRequest();
    string rawBody;
    using (var bodyReader = new StreamReader(request.Body, Encoding.UTF8, false, 4096, true)) rawBody = await bodyReader.ReadToEndAsync(cancellationToken);
    if (rawBody.Length > 65_536) return Results.BadRequest();
    // Flutterwave v3 sends the dashboard secret hash in `verif-hash`; the newer `flutterwave-signature` is a base64 HMAC-SHA256 of the raw body keyed with that hash. Either proves origin.
    var signature = request.Headers["flutterwave-signature"].ToString();
    var expectedSignature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(expectedHash), Encoding.UTF8.GetBytes(rawBody)));
    var authentic = SecureEquals(request.Headers["verif-hash"].ToString(), expectedHash) || (signature.Length > 0 && SecureEquals(signature, expectedSignature));
    if (!authentic) { app.Logger.LogWarning("Rejected Flutterwave webhook with an invalid signature."); return Results.Unauthorized(); }
    string? txRef;
    try
    {
        using var body = JsonDocument.Parse(rawBody);
        var data = body.RootElement.TryGetProperty("data", out var d) ? d : body.RootElement;
        txRef = data.TryGetProperty("tx_ref", out var t) ? t.GetString() : data.TryGetProperty("txRef", out var legacy) ? legacy.GetString() : null;
    }
    catch (JsonException) { return Results.BadRequest(); }
    if (string.IsNullOrWhiteSpace(txRef) || txRef.Length > 100) return Results.Ok();
    // The payload is never trusted: payment details are re-fetched from Flutterwave before anything is recorded.
    var outcome = await ConfirmFlutterwavePayment(txRef, cancellationToken);
    return outcome == "unverified" ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok();
});

app.MapPost("/api/paylio/webhook", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    var txRef = request.Query["ref"].ToString();
    if (txRef.Length is 0 or > 100 || !txRef.StartsWith("kw-", StringComparison.Ordinal)) return Results.BadRequest();
    if (request.ContentLength > 65_536) return Results.BadRequest();
    string rawBody;
    using (var bodyReader = new StreamReader(request.Body, Encoding.UTF8, false, 4096, true)) rawBody = await bodyReader.ReadToEndAsync(cancellationToken);
    if (rawBody.Length > 65_536) return Results.BadRequest();
    string? secret;
    await using (var db = await OpenDatabase(cancellationToken))
    {
        await using var lookup = db.CreateCommand();
        lookup.CommandText = "SELECT provider_secret FROM donations WHERE provider='paylio' AND provider_session_id=$ref";
        lookup.Parameters.AddWithValue("$ref", txRef);
        secret = await lookup.ExecuteScalarAsync(cancellationToken) as string;
    }
    if (string.IsNullOrWhiteSpace(secret)) return Results.NotFound();
    // X-PayLio-Signature is the hex HMAC-SHA256 of the raw body, keyed with the payment's ipn_token.
    var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
    if (!SecureEquals(request.Headers["X-PayLio-Signature"].ToString().Trim().ToLowerInvariant(), expected))
    {
        app.Logger.LogWarning("Rejected Paylio webhook with an invalid signature.");
        return Results.Unauthorized();
    }
    // The payload is never trusted: the payment is re-fetched from Paylio before anything is recorded.
    var outcome = await ConfirmFlutterwavePayment(txRef, cancellationToken);
    return outcome == "unverified" ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok();
});

app.MapGet("/api/donations/status/{txRef}", async (string txRef, CancellationToken cancellationToken) =>
{
    if (txRef.Length > 100 || !txRef.StartsWith("kw-", StringComparison.Ordinal)) return Results.NotFound();
    var outcome = await ConfirmFlutterwavePayment(txRef, cancellationToken);
    if (outcome != "succeeded") return Results.Ok(new { confirmed = false, failed = outcome == "failed" });
    await using var db = await OpenDatabase(cancellationToken);
    await using var command = db.CreateCommand();
    command.CommandText = "SELECT fundraisers.title,donations.amount,donations.currency,COALESCE(donations.original_amount_minor,donations.amount) FROM donations JOIN fundraisers ON fundraisers.id=donations.fundraiser_id WHERE donations.provider_session_id=$tx_ref AND donations.status='succeeded'";
    command.Parameters.AddWithValue("$tx_ref", txRef);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    return await reader.ReadAsync(cancellationToken) ? Results.Ok(new { confirmed = true, failed = false, campaign = reader.GetString(0), amount = reader.GetInt64(1), currency = reader.GetString(2), originalAmount = reader.GetInt64(3) }) : Results.Ok(new { confirmed = false, failed = false });
});

app.MapPost("/api/start-requests", async (HttpContext context, IAntiforgery antiforgery, CancellationToken cancellationToken) =>
{
    if (!await IsValidRequestToken(context, antiforgery)) return Results.BadRequest(new { error = "Please refresh and try again." });
    if (context.Request.ContentLength > 20_000) return Results.BadRequest(new { error = "Your answers are too long." });
    JsonDocument payload;
    try { payload = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken); }
    catch (JsonException) { return Results.BadRequest(new { error = "Check your answers and try again." }); }
    using (payload)
    {
        string Str(string name) => payload.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";
        var beneficiaries = new[] { "Myself", "My family", "A friend or someone I know", "A community or charity" };
        var timelines = new[] { "Right away", "Within a month", "Just exploring" };
        var category = Str("category"); var beneficiary = Str("beneficiary"); var reason = Str("reason");
        var timeline = Str("timeline"); var name = Str("name"); var email = Str("email");
        var goal = payload.RootElement.TryGetProperty("goal", out var g) && g.TryGetInt64(out var parsedGoal) ? parsedGoal : 0;
        if (!fundraiserCategories.Contains(category, StringComparer.Ordinal)) return Results.BadRequest(new { error = "Choose what the fundraiser is for." });
        if (!beneficiaries.Contains(beneficiary, StringComparer.Ordinal)) return Results.BadRequest(new { error = "Choose who you are raising money for." });
        if (reason.Length is < 20 or > 2000) return Results.BadRequest(new { error = "Tell us a little more about why (20 to 2000 characters)." });
        if (goal is < 1 or > 100_000_000) return Results.BadRequest(new { error = "Enter a goal between $1 and $100,000,000." });
        if (!timelines.Contains(timeline, StringComparer.Ordinal)) return Results.BadRequest(new { error = "Choose when you'd like to start." });
        if (name.Length is 0 or > 80) return Results.BadRequest(new { error = "Enter your name." });
        if (!IsValidEmail(email)) return Results.BadRequest(new { error = "Enter a valid email address." });
        await using var db = await OpenDatabase(cancellationToken);
        await using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO start_requests(category,beneficiary,reason,goal_usd,timeline,name,email,created_at) VALUES($c,$b,$r,$g,$t,$n,$e,$d)";
        insert.Parameters.AddWithValue("$c", category); insert.Parameters.AddWithValue("$b", beneficiary); insert.Parameters.AddWithValue("$r", reason);
        insert.Parameters.AddWithValue("$g", goal); insert.Parameters.AddWithValue("$t", timeline); insert.Parameters.AddWithValue("$n", name);
        insert.Parameters.AddWithValue("$e", email); insert.Parameters.AddWithValue("$d", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return Results.Ok(new { ok = true });
    }
});

app.MapGet("/api/rates", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var snapshot = await rateService.GetAsync(cancellationToken);
    var country = CurrencyRules.DetectCountry(context.Request.Headers, await geoIp.CountryAsync(context, cancellationToken));
    var suggested = CurrencyRules.CurrencyForCountry(country);
    if (!donationCurrencies.Contains(suggested)) suggested = "USD";
    // Currencies without a usable rate are not offered, except USD which needs no conversion.
    var offered = donationCurrencies.Where(code => code == "USD" || (snapshot?.UnitsPerUsd.ContainsKey(code) ?? false)).ToList();
    if (!offered.Contains(suggested)) suggested = "USD";
    return Results.Ok(new
    {
        baseCurrency = "USD",
        country,
        suggestedCurrency = suggested,
        available = snapshot is not null,
        stale = snapshot?.Stale ?? false,
        updatedAt = snapshot?.FetchedAt,
        providerUpdatedAt = snapshot?.ProviderUpdatedAt,
        source = snapshot?.Source,
        currencies = offered.Select(code => new { code, unitsPerUsd = code == "USD" ? "1" : DonationMoney.FormatRate(snapshot!.UnitsPerUsd[code]) })
    });
});

admin.MapGet("/finance", async (string? mode, CancellationToken cancellationToken) =>
{
    var scope = ResolveMode(mode);
    if (scope.Invalid) return Results.BadRequest(new { error = "Mode must be test, live or all." });
    await using var db = await OpenDatabase(cancellationToken);
    const string confirmed = "status='succeeded' AND ($mode IS NULL OR provider_mode=$mode)";
    const string paidDate = "date(COALESCE(paid_at,created_at))";
    async Task<List<object?[]>> Rows(string sql, int columns)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$mode", scope.Filter is null ? DBNull.Value : scope.Filter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object?[columns];
            for (var i = 0; i < columns; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
    long Number(object? value) => value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    var lifetime = (await Rows($"SELECT COALESCE(SUM(amount),0),COUNT(*) FROM donations WHERE {confirmed}", 2))[0];
    var today = (await Rows($"SELECT COALESCE(SUM(amount),0),COUNT(*) FROM donations WHERE {confirmed} AND {paidDate}=date('now')", 2))[0];
    var week = (await Rows($"SELECT COALESCE(SUM(amount),0),COUNT(*) FROM donations WHERE {confirmed} AND {paidDate}>=date('now','-6 days')", 2))[0];
    var months = await Rows($"SELECT substr(COALESCE(paid_at,created_at),1,7),SUM(amount),COUNT(*) FROM donations WHERE {confirmed} GROUP BY 1 ORDER BY 1 DESC LIMIT 12", 3);
    var currencies = await Rows($"SELECT currency,COUNT(*),SUM(COALESCE(original_amount_minor,amount)),SUM(amount),COALESCE(SUM(provider_fee_minor),0),COUNT(provider_fee_minor) FROM donations WHERE {confirmed} GROUP BY currency ORDER BY SUM(amount) DESC", 6);
    var statuses = await Rows("SELECT status,COUNT(*) FROM donations WHERE ($mode IS NULL OR provider_mode=$mode) GROUP BY status", 2);
    var settlements = await Rows($"SELECT COALESCE(settlement_currency,'unreported'),SUM(settled_amount_minor),COUNT(*) FROM donations WHERE {confirmed} AND settled_amount_minor IS NOT NULL GROUP BY 1 ORDER BY 1", 3);
    var snapshot = await rateService.GetAsync(cancellationToken);
    long StatusCount(string name) => Number(statuses.FirstOrDefault(row => (string?)row[0] == name)?[1]);
    return Results.Ok(new
    {
        mode = scope.Filter ?? "all",
        configuredMode = CurrentMode(),
        note = "usdEquivalent values are estimates: the donor's currency amount multiplied by the exchange rate recorded when checkout started. They are not money converted or received in USD. Settlement figures are what Flutterwave reported for each transaction.",
        lifetime = new { usdEquivalent = Number(lifetime[0]), count = Number(lifetime[1]) },
        today = new { usdEquivalent = Number(today[0]), count = Number(today[1]) },
        last7Days = new { usdEquivalent = Number(week[0]), count = Number(week[1]) },
        months = months.Select(row => new { month = (string?)row[0], usdEquivalent = Number(row[1]), count = Number(row[2]) }),
        byCurrency = currencies.Select(row => new { currency = (string?)row[0], count = Number(row[1]), originalMinor = Number(row[2]), usdEquivalent = Number(row[3]), providerFeesMinor = Number(row[4]), transactionsWithFeeData = Number(row[5]) }),
        statuses = new { succeeded = StatusCount("succeeded"), pending = StatusCount("pending"), failed = StatusCount("failed"), refunded = StatusCount("refunded") },
        settlements = settlements.Select(row => new { currency = (string?)row[0], settledMinor = Number(row[1]), count = Number(row[2]) }),
        rates = new
        {
            available = snapshot is not null,
            stale = snapshot?.Stale ?? false,
            updatedAt = snapshot?.FetchedAt,
            providerUpdatedAt = snapshot?.ProviderUpdatedAt,
            source = snapshot?.Source,
            cacheMinutes = rateCacheMinutes,
            maxStaleHours = rateMaxStaleHours
        },
        currencyConversion = new
        {
            enabled = false,
            explanation = "Kindwell only records USD equivalents. It never converts or trades funds. Settlement currency and any conversion are governed by your Flutterwave account settings; ask Flutterwave to enable the settlement currency you need."
        }
    });
});

admin.MapGet("/finance/payments", async (string? q, string? status, string? currency, string? mode, string? from, string? to, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) =>
{
    var filter = ParsePaymentFilter(q, status, currency, mode, from, to);
    if (filter.Error is not null) return Results.BadRequest(new { error = filter.Error });
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 1, 100);
    var result = await QueryPayments(filter, pageSize, (page - 1) * pageSize, cancellationToken);
    var pages = Math.Max(1, (int)Math.Ceiling(result.Total / (double)pageSize));
    return Results.Ok(new { items = result.Rows, page, pageSize, total = result.Total, pages });
});

admin.MapGet("/finance/export.csv", async (string? q, string? status, string? currency, string? mode, string? from, string? to, CancellationToken cancellationToken) =>
{
    var filter = ParsePaymentFilter(q, status, currency, mode, from, to);
    if (filter.Error is not null) return Results.BadRequest(new { error = filter.Error });
    var result = await QueryPayments(filter, 50000, 0, cancellationToken);
    string Cell(string? value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
    string Major(long? minor) => minor is null ? "" : (minor.Value / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    var csv = new StringBuilder("date_utc,reference,flutterwave_id,mode,status,campaign,donor,original_amount,original_currency,exchange_rate_units_per_usd,rate_recorded_at,usd_equivalent_estimate,charged_amount,charged_currency,provider_fee,settled_amount,settlement_currency,donor_email\r\n");
    foreach (var row in result.Rows)
        csv.Append(string.Join(',', new[]
        {
            Cell(row.Date), Cell(row.Reference), Cell(row.ProviderTransactionId), Cell(row.Mode), Cell(row.Status), Cell(row.Campaign), Cell(row.Donor),
            Major(row.OriginalMinor), Cell(row.Currency), Cell(row.ExchangeRate), Cell(row.RateRecordedAt), Major(row.UsdEquivalentCents),
            Major(row.ChargedMinor), Cell(row.ChargedCurrency), Major(row.ProviderFeeMinor), Major(row.SettledMinor), Cell(row.SettlementCurrency), Cell(row.Email)
        })).Append("\r\n");
    return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"kindwell-payments-{DateTime.UtcNow:yyyyMMdd}.csv");
});

app.MapFallbackToFile("index.html");
if (paylioActive)
{
    // Paylio sends each webhook once, so open payments from the last day are re-checked every two minutes.
    _ = Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2));
                var open = new List<string>();
                await using (var db = await OpenDatabase(CancellationToken.None))
                {
                    await using var query = db.CreateCommand();
                    query.CommandText = "SELECT provider_session_id FROM donations WHERE provider='paylio' AND status='pending' AND provider_transaction_id IS NOT NULL AND created_at>=$since LIMIT 50";
                    query.Parameters.AddWithValue("$since", DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
                    await using var reader = await query.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) open.Add(reader.GetString(0));
                }
                foreach (var reference in open) await ConfirmFlutterwavePayment(reference, CancellationToken.None);
            }
            catch (Exception ex) { app.Logger.LogWarning(ex, "Paylio reconciliation run failed."); }
        }
    });
}

app.Run();

async Task<SqliteConnection> OpenDatabase(CancellationToken cancellationToken = default)
{
    var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    return connection;
}

async Task InitializeDatabase()
{
    await using var db = await OpenDatabase();
    await using var command = db.CreateCommand();
    command.CommandText = """
        PRAGMA foreign_keys=ON;
        CREATE TABLE IF NOT EXISTS schema_migrations(migration_id TEXT PRIMARY KEY,applied_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS fundraisers(id INTEGER PRIMARY KEY AUTOINCREMENT,slug TEXT NOT NULL UNIQUE,title TEXT NOT NULL,summary TEXT NOT NULL,story TEXT NOT NULL,image_url TEXT NOT NULL,goal INTEGER NOT NULL,raised INTEGER NOT NULL DEFAULT 0,donation_count INTEGER NOT NULL DEFAULT 0,reported_raised INTEGER NOT NULL DEFAULT 0,reported_donation_count INTEGER NOT NULL DEFAULT 0,published INTEGER NOT NULL DEFAULT 0,featured INTEGER NOT NULL DEFAULT 0,created_at TEXT NOT NULL,category TEXT NOT NULL DEFAULT 'Community',organizer_name TEXT NOT NULL DEFAULT '',organizer_location TEXT NOT NULL DEFAULT '',organizer_profile_image TEXT NOT NULL DEFAULT '',organizer_bio TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS users(id INTEGER PRIMARY KEY AUTOINCREMENT,email TEXT NOT NULL UNIQUE,name TEXT NOT NULL,password_hash TEXT NOT NULL,created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS password_resets(id INTEGER PRIMARY KEY AUTOINCREMENT,user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,token_hash TEXT NOT NULL UNIQUE,expires_at TEXT NOT NULL,used INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS start_requests(id INTEGER PRIMARY KEY AUTOINCREMENT,category TEXT NOT NULL,beneficiary TEXT NOT NULL,reason TEXT NOT NULL,goal_usd INTEGER NOT NULL,timeline TEXT NOT NULL,name TEXT NOT NULL,email TEXT NOT NULL,created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS donations(id INTEGER PRIMARY KEY AUTOINCREMENT,fundraiser_id INTEGER NOT NULL REFERENCES fundraisers(id) ON DELETE CASCADE,amount INTEGER NOT NULL,donor_name TEXT NOT NULL,is_anonymous INTEGER NOT NULL DEFAULT 0,status TEXT NOT NULL,provider_session_id TEXT UNIQUE,created_at TEXT NOT NULL,paid_at TEXT);
        CREATE TABLE IF NOT EXISTS exchange_rate_snapshots(id INTEGER PRIMARY KEY AUTOINCREMENT,source TEXT NOT NULL,fetched_at TEXT NOT NULL,provider_updated_at TEXT,rates_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS campaign_updates(id INTEGER PRIMARY KEY AUTOINCREMENT,fundraiser_id INTEGER NOT NULL REFERENCES fundraisers(id) ON DELETE CASCADE,title TEXT NOT NULL,body TEXT NOT NULL,created_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS idx_campaign_updates_fundraiser_date ON campaign_updates(fundraiser_id,created_at DESC);
        CREATE TABLE IF NOT EXISTS ledger_transactions(id INTEGER PRIMARY KEY AUTOINCREMENT,donation_id INTEGER NOT NULL UNIQUE REFERENCES donations(id) ON DELETE RESTRICT,currency TEXT NOT NULL CHECK(currency='usd'),amount INTEGER NOT NULL CHECK(amount>0),occurred_at TEXT NOT NULL,provider_reference TEXT,created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ledger_postings(id INTEGER PRIMARY KEY AUTOINCREMENT,transaction_id INTEGER NOT NULL REFERENCES ledger_transactions(id) ON DELETE RESTRICT,account_code TEXT NOT NULL CHECK(account_code IN ('processor_clearing','campaign_funds_payable')),side TEXT NOT NULL CHECK(side IN ('debit','credit')),amount INTEGER NOT NULL CHECK(amount>0),UNIQUE(transaction_id,account_code,side));
        CREATE INDEX IF NOT EXISTS idx_ledger_transactions_occurred_at ON ledger_transactions(occurred_at DESC);
        CREATE TRIGGER IF NOT EXISTS trg_ledger_transactions_no_update
        BEFORE UPDATE ON ledger_transactions BEGIN SELECT RAISE(ABORT,'Ledger transactions are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS trg_ledger_transactions_no_delete
        BEFORE DELETE ON ledger_transactions BEGIN SELECT RAISE(ABORT,'Ledger transactions are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS trg_ledger_postings_no_update
        BEFORE UPDATE ON ledger_postings BEGIN SELECT RAISE(ABORT,'Ledger postings are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS trg_ledger_postings_no_delete
        BEFORE DELETE ON ledger_postings BEGIN SELECT RAISE(ABORT,'Ledger postings are immutable'); END;
        """;
    await command.ExecuteNonQueryAsync();
    await command.DisposeAsync();
    var donationColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using (var columnQuery = db.CreateCommand())
    {
        columnQuery.CommandText = "PRAGMA table_info(donations)";
        await using var columnReader = await columnQuery.ExecuteReaderAsync();
        while (await columnReader.ReadAsync()) donationColumns.Add(columnReader.GetString(1));
    }
    foreach (var (column, definition) in new[]
    {
        ("provider", "TEXT NOT NULL DEFAULT 'flutterwave'"), ("provider_transaction_id", "TEXT"),
        ("currency", "TEXT NOT NULL DEFAULT 'USD'"), ("original_amount_minor", "INTEGER"), ("exchange_rate", "TEXT"),
        ("rate_fetched_at", "TEXT"), ("rate_source", "TEXT"), ("provider_mode", "TEXT"),
        ("charged_amount_minor", "INTEGER"), ("charged_currency", "TEXT"), ("provider_fee_minor", "INTEGER"),
        ("settled_amount_minor", "INTEGER"), ("settlement_currency", "TEXT"), ("donor_email", "TEXT"), ("provider_secret", "TEXT")
    })
    {
        if (donationColumns.Contains(column)) continue;
        await using var addProviderColumn = db.CreateCommand();
        addProviderColumn.CommandText = $"ALTER TABLE donations ADD COLUMN {column} {definition}";
        await addProviderColumn.ExecuteNonQueryAsync();
    }
    if (!donationColumns.Contains("paid_at"))
    {
        await using var addColumn = db.CreateCommand();
        addColumn.CommandText = "ALTER TABLE donations ADD COLUMN paid_at TEXT";
        await addColumn.ExecuteNonQueryAsync();
    }
    await using (var ledgerMigration = await db.BeginTransactionAsync())
    {
        await using var migrateLedger = db.CreateCommand();
        migrateLedger.Transaction = (SqliteTransaction)ledgerMigration;
        migrateLedger.CommandText = """
            INSERT OR IGNORE INTO ledger_transactions(donation_id,currency,amount,occurred_at,provider_reference,created_at)
            SELECT id,'usd',amount,COALESCE(paid_at,created_at),provider_session_id,COALESCE(paid_at,created_at)
            FROM donations WHERE status='succeeded';
            INSERT OR IGNORE INTO ledger_postings(transaction_id,account_code,side,amount)
            SELECT id,'processor_clearing','debit',amount FROM ledger_transactions;
            INSERT OR IGNORE INTO ledger_postings(transaction_id,account_code,side,amount)
            SELECT id,'campaign_funds_payable','credit',amount FROM ledger_transactions;
            """;
        await migrateLedger.ExecuteNonQueryAsync();
        await ledgerMigration.CommitAsync();
    }
    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using (var columnQuery = db.CreateCommand())
    {
        columnQuery.CommandText = "PRAGMA table_info(fundraisers)";
        await using var columnReader = await columnQuery.ExecuteReaderAsync();
        while (await columnReader.ReadAsync()) columns.Add(columnReader.GetString(1));
    }
    var needsOpeningBalanceMigration = !columns.Contains("reported_raised") || !columns.Contains("reported_donation_count");
    if (!columns.Contains("reported_raised"))
    {
        await using var addColumn = db.CreateCommand();
        addColumn.CommandText = "ALTER TABLE fundraisers ADD COLUMN reported_raised INTEGER NOT NULL DEFAULT 0";
        await addColumn.ExecuteNonQueryAsync();
    }
    if (!columns.Contains("reported_donation_count"))
    {
        await using var addColumn = db.CreateCommand();
        addColumn.CommandText = "ALTER TABLE fundraisers ADD COLUMN reported_donation_count INTEGER NOT NULL DEFAULT 0";
        await addColumn.ExecuteNonQueryAsync();
    }
    if (!columns.Contains("category"))
    {
        await using var addColumn = db.CreateCommand();
        addColumn.CommandText = "ALTER TABLE fundraisers ADD COLUMN category TEXT NOT NULL DEFAULT 'Community'";
        await addColumn.ExecuteNonQueryAsync();
    }
    foreach (var (column, definition) in new[]
    {
        ("organizer_name", "TEXT NOT NULL DEFAULT ''"),
        ("organizer_location", "TEXT NOT NULL DEFAULT ''"),
        ("organizer_profile_image", "TEXT NOT NULL DEFAULT ''"),
        ("organizer_bio", "TEXT NOT NULL DEFAULT ''")
    })
    {
        if (columns.Contains(column)) continue;
        await using var addColumn = db.CreateCommand();
        addColumn.CommandText = $"ALTER TABLE fundraisers ADD COLUMN {column} {definition}";
        await addColumn.ExecuteNonQueryAsync();
    }
    await using (var categoryMigration = await db.BeginTransactionAsync())
    {
        await using var migrationCheck = db.CreateCommand();
        migrationCheck.Transaction = (SqliteTransaction)categoryMigration;
        migrationCheck.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE migration_id='legacy-campaign-categories-v1'";
        if (Convert.ToInt64(await migrationCheck.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 0)
        {
            await using var backfillCategories = db.CreateCommand();
            backfillCategories.Transaction = (SqliteTransaction)categoryMigration;
            backfillCategories.CommandText = """
                UPDATE fundraisers
                SET category=CASE slug
                    WHEN 'a-brighter-start-for-maya' THEN 'Emergency'
                    WHEN 'keeping-the-neighborhood-garden-growing' THEN 'Environment'
                    WHEN 'a-safe-ride-to-every-appointment' THEN 'Medical'
                END
                WHERE category='Community'
                  AND slug IN ('a-brighter-start-for-maya','keeping-the-neighborhood-garden-growing','a-safe-ride-to-every-appointment')
                """;
            await backfillCategories.ExecuteNonQueryAsync();
            await using var recordMigration = db.CreateCommand();
            recordMigration.Transaction = (SqliteTransaction)categoryMigration;
            recordMigration.CommandText = "INSERT INTO schema_migrations(migration_id,applied_at) VALUES('legacy-campaign-categories-v1',$date)";
            recordMigration.Parameters.AddWithValue("$date", DateTimeOffset.UtcNow.ToString("O"));
            await recordMigration.ExecuteNonQueryAsync();
        }
        await categoryMigration.CommitAsync();
    }
    await using (var starterBalanceMigration = await db.BeginTransactionAsync())
    {
        await using var migrationCheck = db.CreateCommand();
        migrationCheck.Transaction = (SqliteTransaction)starterBalanceMigration;
        migrationCheck.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE migration_id='remove-demo-campaign-balances-v1'";
        if (Convert.ToInt64(await migrationCheck.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 0)
        {
            await using var clearStarterBalances = db.CreateCommand();
            clearStarterBalances.Transaction = (SqliteTransaction)starterBalanceMigration;
            clearStarterBalances.CommandText = """
                UPDATE fundraisers SET reported_raised=0,reported_donation_count=0
                WHERE slug IN ('a-brighter-start-for-maya','keeping-the-neighborhood-garden-growing','a-safe-ride-to-every-appointment')
                  AND raised=0 AND donation_count=0
                  AND reported_raised IN (1264000,613500,1748200)
                """;
            await clearStarterBalances.ExecuteNonQueryAsync();
            await using var recordMigration = db.CreateCommand();
            recordMigration.Transaction = (SqliteTransaction)starterBalanceMigration;
            recordMigration.CommandText = "INSERT INTO schema_migrations(migration_id,applied_at) VALUES('remove-demo-campaign-balances-v1',$date)";
            recordMigration.Parameters.AddWithValue("$date", DateTimeOffset.UtcNow.ToString("O"));
            await recordMigration.ExecuteNonQueryAsync();
        }
        await starterBalanceMigration.CommitAsync();
    }
    if (needsOpeningBalanceMigration)
    {
        await using var migrate = db.CreateCommand();
        migrate.CommandText = "UPDATE fundraisers SET reported_raised=MAX(0,raised-COALESCE((SELECT SUM(amount) FROM donations WHERE fundraiser_id=fundraisers.id AND status='succeeded'),0)),raised=COALESCE((SELECT SUM(amount) FROM donations WHERE fundraiser_id=fundraisers.id AND status='succeeded'),0),reported_donation_count=MAX(0,donation_count-COALESCE((SELECT COUNT(*) FROM donations WHERE fundraiser_id=fundraisers.id AND status='succeeded'),0)),donation_count=COALESCE((SELECT COUNT(*) FROM donations WHERE fundraiser_id=fundraisers.id AND status='succeeded'),0)";
        await migrate.ExecuteNonQueryAsync();
    }
    await using var count = db.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM fundraisers";
    if (Convert.ToInt64(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0) return;
    var seeds = new[]
    {
        new SeedCampaign("A brighter start for Maya", "Help a young family rebuild after a house fire", "After a late-night electrical fire displaced Maya and her two children, their neighbors began collecting what they could. Contributions will help replace everyday essentials, cover temporary housing, and give this family room to begin again.", "https://images.unsplash.com/photo-1488521787991-ed7bbaae773c?auto=format&fit=crop&w=1000&q=82", 1800000, "Emergency"),
        new SeedCampaign("Keeping the neighborhood garden growing", "A little patch of green with room for everyone", "For eight years, the Maple Street garden has brought neighbors together. This season we are raising funds for accessible paths, new soil, and a water-saving irrigation system so the garden can welcome even more families.", "https://images.unsplash.com/photo-1416879595882-3373a0480b5b?auto=format&fit=crop&w=1000&q=82", 950000, "Environment"),
        new SeedCampaign("A safe ride to every appointment", "Reliable transport makes recovery possible", "Getting to regular treatment should not be another worry. This community fund will help cover accessible rides for local residents attending ongoing medical appointments, with funds distributed through the care team.", "https://images.unsplash.com/photo-1576091160399-112ba8d25d1d?auto=format&fit=crop&w=1000&q=82", 2400000, "Medical")
    };
    var organizerProfiles = new Dictionary<string, (string location, string image, string bio)>
    {
        ["A brighter start for Maya"] = ("Lagos, Nigeria", "https://images.unsplash.com/photo-1544005313-94ddf0286df2?auto=format&fit=crop&w=500&q=80", "Helping my family rebuild after a sudden fire with practical support and a safe place to restart."),
        ["Keeping the neighborhood garden growing"] = ("Austin, Texas", "https://images.unsplash.com/photo-1500648767791-00dcc994a43e?auto=format&fit=crop&w=500&q=80", "Community gardener supporting neighbors through shared green spaces and easy access to fresh food."),
        ["A safe ride to every appointment"] = ("Accra, Ghana", "https://images.unsplash.com/photo-1494790108377-be9c29b29330?auto=format&fit=crop&w=500&q=80", "I organize transport support so patients can keep showing up for care, work, and family time.")
    };
    foreach (var seed in seeds)
    {
        var organizer = organizerProfiles[seed.Title];
        await using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO fundraisers(slug,title,summary,story,image_url,goal,reported_raised,reported_donation_count,published,featured,created_at,category,organizer_name,organizer_location,organizer_profile_image,organizer_bio) VALUES($slug,$title,$summary,$story,$image,$goal,0,0,1,1,$created,$category,$organizer_name,$organizer_location,$organizer_profile_image,$organizer_bio)";
        insert.Parameters.AddWithValue("$slug", Slugify(seed.Title));
        insert.Parameters.AddWithValue("$title", seed.Title);
        insert.Parameters.AddWithValue("$summary", seed.Summary);
        insert.Parameters.AddWithValue("$story", seed.Story);
        insert.Parameters.AddWithValue("$image", seed.Image);
        insert.Parameters.AddWithValue("$goal", seed.Goal);
        insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("$category", seed.Category);
        insert.Parameters.AddWithValue("$organizer_name", seed.Title.Contains("Maya") ? "Maya Sanni" : seed.Title.Contains("garden") ? "Aisha Brown" : "Nadia Mensah");
        insert.Parameters.AddWithValue("$organizer_location", organizer.location);
        insert.Parameters.AddWithValue("$organizer_profile_image", organizer.image);
        insert.Parameters.AddWithValue("$organizer_bio", organizer.bio);
        await insert.ExecuteNonQueryAsync();
    }
}

string CurrentMode()
{
    // Paylio has no sandbox: every payment is real.
    if (paylioActive) return "live";
    var key = Environment.GetEnvironmentVariable("FLUTTERWAVE_SECRET_KEY");
    return string.IsNullOrWhiteSpace(key) || key.StartsWith("FLWSECK_TEST", StringComparison.Ordinal) ? "test" : "live";
}

ModeScope ResolveMode(string? mode) => mode?.ToLowerInvariant() switch
{
    null or "" => new ModeScope(CurrentMode(), false),
    "all" => new ModeScope(null, false),
    "test" => new ModeScope("test", false),
    "live" => new ModeScope("live", false),
    _ => new ModeScope(null, true)
};

PaymentFilter ParsePaymentFilter(string? q, string? status, string? currency, string? mode, string? from, string? to)
{
    var scope = ResolveMode(mode);
    if (scope.Invalid) return new PaymentFilter(null, null, null, null, null, null, "Mode must be test, live or all.");
    if (status is not null && status is not ("pending" or "succeeded" or "failed" or "refunded")) return new PaymentFilter(null, null, null, null, null, null, "Choose a valid payment status.");
    if (currency is not null && !System.Text.RegularExpressions.Regex.IsMatch(currency, "^[A-Za-z]{3}$")) return new PaymentFilter(null, null, null, null, null, null, "Currency must be a 3-letter code.");
    if (from is not null && !DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return new PaymentFilter(null, null, null, null, null, null, "Start date must use YYYY-MM-DD format.");
    if (to is not null && !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return new PaymentFilter(null, null, null, null, null, null, "End date must use YYYY-MM-DD format.");
    var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
    if (search is { Length: > 100 }) return new PaymentFilter(null, null, null, null, null, null, "Search text is too long.");
    return new PaymentFilter(search, status, currency?.ToUpperInvariant(), scope.Filter, from, to, null);
}

async Task<(List<PaymentRow> Rows, int Total)> QueryPayments(PaymentFilter filter, int limit, int offset, CancellationToken cancellationToken)
{
    const string where = """
        WHERE ($mode IS NULL OR d.provider_mode=$mode)
          AND ($status IS NULL OR d.status=$status)
          AND ($currency IS NULL OR d.currency=$currency)
          AND ($from IS NULL OR date(COALESCE(d.paid_at,d.created_at))>=$from)
          AND ($to IS NULL OR date(COALESCE(d.paid_at,d.created_at))<=$to)
          AND ($q IS NULL OR instr(lower(COALESCE(d.provider_session_id,'')||' '||COALESCE(d.provider_transaction_id,'')||' '||f.title||' '||CASE WHEN d.is_anonymous=1 THEN 'anonymous' ELSE d.donor_name END||' '||d.currency||' '||COALESCE(d.donor_email,'')),lower($q))>0)
        """;
    void Bind(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$mode", (object?)filter.Mode ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)filter.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$currency", (object?)filter.Currency ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", (object?)filter.From ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)filter.To ?? DBNull.Value);
        command.Parameters.AddWithValue("$q", (object?)filter.Search ?? DBNull.Value);
    }
    await using var db = await OpenDatabase(cancellationToken);
    await using var count = db.CreateCommand();
    count.CommandText = $"SELECT COUNT(*) FROM donations d JOIN fundraisers f ON f.id=d.fundraiser_id {where}";
    Bind(count);
    var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    await using var command = db.CreateCommand();
    command.CommandText = $"""
        SELECT d.id,f.title,d.donor_name,d.is_anonymous,d.status,d.currency,COALESCE(d.original_amount_minor,d.amount),d.exchange_rate,d.rate_fetched_at,
               d.amount,d.charged_amount_minor,d.charged_currency,d.provider_fee_minor,d.settled_amount_minor,d.settlement_currency,
               d.provider_session_id,d.provider_transaction_id,               d.provider_mode,COALESCE(d.paid_at,d.created_at),d.donor_email
        FROM donations d JOIN fundraisers f ON f.id=d.fundraiser_id {where}
        ORDER BY COALESCE(d.paid_at,d.created_at) DESC,d.id DESC LIMIT $limit OFFSET $offset
        """;
    Bind(command);
    command.Parameters.AddWithValue("$limit", limit);
    command.Parameters.AddWithValue("$offset", offset);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var rows = new List<PaymentRow>();
    string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
    long? Long(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
    while (await reader.ReadAsync(cancellationToken))
        rows.Add(new PaymentRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(3) == 1 ? "Anonymous" : reader.GetString(2), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), Text(7), Text(8),
            reader.GetInt64(9), Long(10), Text(11), Long(12), Long(13), Text(14), Text(15), Text(16), Text(17), reader.GetString(18), Text(19)));
    return (rows, total);
}

bool IsValidEmail(string value)
{
    if (value.Length is 0 or > 254) return false;
    try { return new System.Net.Mail.MailAddress(value).Address == value; }
    catch (FormatException) { return false; }
}

// Returns "succeeded", "failed", "pending" or "unverified" (could not be checked with Flutterwave).
async Task<string> ConfirmFlutterwavePayment(string txRef, CancellationToken cancellationToken)
{
    var secretKey = Environment.GetEnvironmentVariable("FLUTTERWAVE_SECRET_KEY") ?? "";
    await using var db = await OpenDatabase(cancellationToken);
    await using var getDonation = db.CreateCommand();
    getDonation.CommandText = "SELECT id,fundraiser_id,amount,status,currency,COALESCE(original_amount_minor,amount),provider,provider_transaction_id FROM donations WHERE provider_session_id=$tx_ref";
    getDonation.Parameters.AddWithValue("$tx_ref", txRef);
    long donationId, fundraiserId, amount, originalMinor;
    string status, donationCurrency, provider;
    string? paylioPaymentId;
    await using (var donationReader = await getDonation.ExecuteReaderAsync(cancellationToken))
    {
        if (!await donationReader.ReadAsync(cancellationToken)) return "unknown";
        donationId = donationReader.GetInt64(0);
        fundraiserId = donationReader.GetInt64(1);
        amount = donationReader.GetInt64(2);
        status = donationReader.GetString(3);
        donationCurrency = donationReader.GetString(4);
        originalMinor = donationReader.GetInt64(5);
        provider = donationReader.GetString(6);
        paylioPaymentId = donationReader.IsDBNull(7) ? null : donationReader.GetString(7);
    }
    if (status is "succeeded" or "failed") return status;

    string verifiedStatus, verifiedRef, verifiedCurrency;
    decimal verifiedAmount;
    string? verifiedId, settlementCurrency;
    long? chargedMinor, feeMinor, settledMinor;
    if (provider == "paylio")
    {
        var apiKey = Environment.GetEnvironmentVariable("PAYLIO_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(paylioPaymentId)) return "unverified";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"payment-status?payment_id={Uri.EscapeDataString(paylioPaymentId)}");
            request.Headers.Add("X-API-Key", apiKey);
            using var response = await paylioHttp.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return response.StatusCode == System.Net.HttpStatusCode.NotFound ? "pending" : "unverified";
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var data = result.RootElement;
            string? Text(string name) => data.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;
            decimal? Money(string name) => decimal.TryParse(Text(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var m) ? m : null;
            var remoteStatus = Text("status") ?? "";
            verifiedRef = Text("payment_id") ?? "";
            verifiedStatus = remoteStatus.Equals("paid", StringComparison.OrdinalIgnoreCase) ? "successful" : remoteStatus.ToLowerInvariant() is "failed" or "canceled" or "cancelled" ? "failed" : "pending";
            verifiedCurrency = Text("currency") ?? "";
            // The requested amount is compared; `amount` only differs when fees are passed to the donor, which Kindwell does not do.
            verifiedAmount = Money("original_amount") ?? Money("amount") ?? -1;
            verifiedId = verifiedRef;
            chargedMinor = Money("amount") is { } charged ? DonationMoney.ToMinor(charged) : null;
            feeMinor = null;
            settledMinor = Money("forwarded_amount") is { } forwarded ? DonationMoney.ToMinor(forwarded) : null;
            settlementCurrency = settledMinor is null ? null : "USDC";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { app.Logger.LogWarning(ex, "Could not verify Paylio payment for {TxRef}.", txRef); return "unverified"; }
        if (verifiedRef != paylioPaymentId) return "unverified";
        verifiedRef = txRef;
    }
    else
    {
    if (string.IsNullOrWhiteSpace(secretKey)) return "unverified";
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"transactions/verify_by_reference?tx_ref={Uri.EscapeDataString(txRef)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secretKey);
        using var response = await flutterwaveHttp.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return response.StatusCode == System.Net.HttpStatusCode.NotFound ? "pending" : "unverified";
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!result.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return "pending";
        verifiedStatus = data.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        verifiedRef = data.TryGetProperty("tx_ref", out var r) ? r.GetString() ?? "" : "";
        verifiedCurrency = data.TryGetProperty("currency", out var c) ? c.GetString() ?? "" : "";
        verifiedAmount = data.TryGetProperty("amount", out var a) && a.TryGetDecimal(out var parsed) ? parsed : -1;
        verifiedId = data.TryGetProperty("id", out var id) ? id.ToString() : null;
        long? OptionalMinor(string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? DonationMoney.ToMinor(number) : null;
        chargedMinor = OptionalMinor("charged_amount");
        feeMinor = OptionalMinor("app_fee");
        settledMinor = OptionalMinor("amount_settled");
        settlementCurrency = data.TryGetProperty("settlement_currency", out var sc) && sc.ValueKind == JsonValueKind.String && sc.GetString() is { Length: 3 } code ? code.ToUpperInvariant() : null;
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { app.Logger.LogWarning(ex, "Could not verify Flutterwave transaction {TxRef}.", txRef); return "unverified"; }
    }

    if (verifiedRef != txRef) return "unverified";
    if (verifiedStatus.Equals("failed", StringComparison.OrdinalIgnoreCase) || verifiedStatus.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
    {
        await using var markFailed = db.CreateCommand();
        markFailed.CommandText = "UPDATE donations SET status='failed' WHERE id=$id AND status='pending'";
        markFailed.Parameters.AddWithValue("$id", donationId);
        await markFailed.ExecuteNonQueryAsync(cancellationToken);
        return "failed";
    }
    if (!verifiedStatus.Equals("successful", StringComparison.OrdinalIgnoreCase)) return "pending";
    if (!verifiedCurrency.Equals(donationCurrency, StringComparison.OrdinalIgnoreCase) || DonationMoney.ToMinor(verifiedAmount) != originalMinor)
    {
        app.Logger.LogWarning("Flutterwave transaction {TxRef} did not match the expected amount or currency; not recorded.", txRef);
        return "unverified";
    }

    await using var transaction = await db.BeginTransactionAsync(cancellationToken);
    var paidAt = DateTimeOffset.UtcNow.ToString("O");
    await using var confirm = db.CreateCommand();
    confirm.Transaction = (SqliteTransaction)transaction;
    confirm.CommandText = """
        UPDATE donations SET status='succeeded',paid_at=$paid_at,provider_transaction_id=$provider_id,
            charged_amount_minor=$charged,charged_currency=$charged_currency,provider_fee_minor=$fee,
            settled_amount_minor=$settled,settlement_currency=$settlement_currency
        WHERE id=$id AND status='pending'
        """;
    confirm.Parameters.AddWithValue("$id", donationId);
    confirm.Parameters.AddWithValue("$paid_at", paidAt);
    confirm.Parameters.AddWithValue("$provider_id", (object?)verifiedId ?? DBNull.Value);
    confirm.Parameters.AddWithValue("$charged", (object?)chargedMinor ?? DBNull.Value);
    confirm.Parameters.AddWithValue("$charged_currency", verifiedCurrency.ToUpperInvariant());
    confirm.Parameters.AddWithValue("$fee", (object?)feeMinor ?? DBNull.Value);
    confirm.Parameters.AddWithValue("$settled", (object?)settledMinor ?? DBNull.Value);
    confirm.Parameters.AddWithValue("$settlement_currency", (object?)settlementCurrency ?? DBNull.Value);
    if (await confirm.ExecuteNonQueryAsync(cancellationToken) == 1)
    {
        await using var ledger = db.CreateCommand();
        ledger.Transaction = (SqliteTransaction)transaction;
        ledger.CommandText = """
            INSERT INTO ledger_transactions(donation_id,currency,amount,occurred_at,provider_reference,created_at)
            VALUES($donation_id,'usd',$amount,$occurred_at,$provider_reference,$created_at);
            SELECT last_insert_rowid();
            """;
        ledger.Parameters.AddWithValue("$donation_id", donationId);
        ledger.Parameters.AddWithValue("$amount", amount);
        ledger.Parameters.AddWithValue("$occurred_at", paidAt);
        ledger.Parameters.AddWithValue("$provider_reference", txRef);
        ledger.Parameters.AddWithValue("$created_at", paidAt);
        var ledgerId = Convert.ToInt64(await ledger.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await using var postings = db.CreateCommand();
        postings.Transaction = (SqliteTransaction)transaction;
        postings.CommandText = """
            INSERT INTO ledger_postings(transaction_id,account_code,side,amount)
            VALUES
                ($transaction_id,'processor_clearing','debit',$amount),
                ($transaction_id,'campaign_funds_payable','credit',$amount)
            """;
        postings.Parameters.AddWithValue("$transaction_id", ledgerId);
        postings.Parameters.AddWithValue("$amount", amount);
        await postings.ExecuteNonQueryAsync(cancellationToken);
        await using var updateFundraiser = db.CreateCommand();
        updateFundraiser.Transaction = (SqliteTransaction)transaction;
        updateFundraiser.CommandText = "UPDATE fundraisers SET raised=raised+$amount,donation_count=donation_count+1 WHERE id=$fundraiser";
        updateFundraiser.Parameters.AddWithValue("$amount", amount);
        updateFundraiser.Parameters.AddWithValue("$fundraiser", fundraiserId);
        await updateFundraiser.ExecuteNonQueryAsync(cancellationToken);
    }
    await transaction.CommitAsync(cancellationToken);
    return "succeeded";
}

bool SecureEquals(string left, string right)
{
    var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
    var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
    return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
}

async Task<bool> IsValidRequestToken(HttpContext context, IAntiforgery antiforgery)
{
    try { await antiforgery.ValidateRequestAsync(context); return true; }
    catch (AntiforgeryValidationException) { return false; }
}

string? ValidateFundraiser(FundraiserInput? input)
{
    if (input is null || string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 100) return "Add a title of 1 to 100 characters.";
    if (string.IsNullOrWhiteSpace(input.Summary) || input.Summary.Trim().Length > 220) return "Add a short description of up to 220 characters.";
    if (string.IsNullOrWhiteSpace(input.Story) || input.Story.Trim().Length > 12000) return "Add a story of up to 12,000 characters.";
    if (string.IsNullOrWhiteSpace(input.Image) || input.Image.Length > 500) return "Add a valid campaign image.";
    if (string.IsNullOrWhiteSpace(input.OrganizerName) || input.OrganizerName.Trim().Length > 100) return "Add a fundraiser organizer name up to 100 characters.";
    if (input.OrganizerLocation.Length > 120) return "Keep the organizer location under 120 characters.";
    if (input.OrganizerProfileImage.Length > 500) return "Use a valid profile image URL.";
    if (input.OrganizerBio.Length > 500) return "Keep the organizer bio under 500 characters.";
    if (!fundraiserCategories.Contains(input.Category, StringComparer.Ordinal)) return "Choose a valid fundraiser category.";
    if (input.Goal < 100 || input.Goal > 100_000_000) return "Set a goal between $1 and $1,000,000.";
    if (input.ReportedRaised < 0 || input.ReportedRaised > 100_000_000) return "Set prior support between $0 and $1,000,000.";
    if (input.ReportedDonationCount < 0 || input.ReportedDonationCount > 1_000_000) return "Set the number of prior gifts between 0 and 1,000,000.";
    return null;
}

void AddFundraiserParameters(SqliteCommand command, FundraiserInput input, string slug)
{
    command.Parameters.AddWithValue("$slug", slug);
    command.Parameters.AddWithValue("$title", input.Title.Trim());
    command.Parameters.AddWithValue("$summary", input.Summary.Trim());
    command.Parameters.AddWithValue("$story", input.Story.Trim());
    command.Parameters.AddWithValue("$image", input.Image.Trim());
    command.Parameters.AddWithValue("$category", input.Category);
    command.Parameters.AddWithValue("$organizer_name", input.OrganizerName.Trim());
    command.Parameters.AddWithValue("$organizer_location", input.OrganizerLocation.Trim());
    command.Parameters.AddWithValue("$organizer_profile_image", input.OrganizerProfileImage.Trim());
    command.Parameters.AddWithValue("$organizer_bio", input.OrganizerBio.Trim());
    command.Parameters.AddWithValue("$goal", input.Goal);
    command.Parameters.AddWithValue("$reported_raised", input.ReportedRaised);
    command.Parameters.AddWithValue("$reported_donation_count", input.ReportedDonationCount);
    command.Parameters.AddWithValue("$published", input.Published ? 1 : 0);
    command.Parameters.AddWithValue("$featured", input.Featured ? 1 : 0);
}

string Slugify(string value)
{
    var slug = string.Join('-', value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => new string(part.Where(char.IsLetterOrDigit).ToArray())).Where(part => part.Length > 0));
    return slug.Length > 70 ? slug[..70].TrimEnd('-') : slug;
}

object ReadFundraiser(SqliteDataReader reader)
{
    var index = 0;
    return new
    {
        slug = reader.GetString(index++),
        title = reader.GetString(index++),
        summary = reader.GetString(index++),
        story = reader.GetString(index++),
        image = reader.GetString(index++),
        goal = reader.GetInt64(index++),
        raised = reader.GetInt64(index++),
        donations = reader.GetInt64(index++),
        reportedRaised = reader.GetInt64(index++),
        reportedDonations = reader.GetInt64(index++),
        category = reader.GetString(index++),
        organizerName = reader.GetString(index++),
        organizerLocation = reader.GetString(index++),
        organizerProfileImage = reader.GetString(index++),
        organizerBio = reader.GetString(index++)
    };
}

record FundraiserInput(string Title, string Summary, string Story, string Image, long Goal, long ReportedRaised, long ReportedDonationCount, bool Published, bool Featured, string Category, string OrganizerName, string OrganizerLocation, string OrganizerProfileImage, string OrganizerBio);
record CampaignUpdateInput(string Title, string Body);
record SeedCampaign(string Title, string Summary, string Story, string Image, long Goal, string Category);