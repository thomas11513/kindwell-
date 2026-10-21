using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

/// <summary>Exchange rates expressed as units of each currency per 1 USD.</summary>
public sealed record RateSnapshot(
    IReadOnlyDictionary<string, decimal> UnitsPerUsd,
    DateTimeOffset FetchedAt,
    DateTimeOffset? ProviderUpdatedAt,
    bool Stale,
    string Source);

public static class DonationMoney
{
    // Amounts are held as integer minor units (cents/kobo/pence). The currencies offered here all use two decimals.
    public static long ToUsdCents(long originalMinor, decimal unitsPerUsd)
    {
        if (unitsPerUsd <= 0) throw new ArgumentOutOfRangeException(nameof(unitsPerUsd));
        return (long)Math.Round(originalMinor / unitsPerUsd, 0, MidpointRounding.AwayFromZero);
    }

    public static long ToMinor(decimal major) => (long)Math.Round(major * 100m, 0, MidpointRounding.AwayFromZero);

    public static string FormatRate(decimal rate) => rate.ToString("0.############", CultureInfo.InvariantCulture);

    public static bool TryParseRate(string? text, out decimal rate) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out rate) && rate > 0;
}

public static class CurrencyRules
{
    static readonly Regex CodePattern = new("^[A-Z]{3}$", RegexOptions.Compiled);

    // Currencies that do not use two minor-unit decimals; unsupported by the minor-unit arithmetic used here.
    static readonly HashSet<string> NotTwoDecimals = new(StringComparer.Ordinal)
    {
        "JPY", "KRW", "VND", "CLP", "ISK", "UGX", "RWF", "XAF", "XOF", "GNF", "KWD", "BHD", "OMR", "JOD", "TND"
    };

    static readonly HashSet<string> EuroCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "CY", "DE", "EE", "ES", "FI", "FR", "GR", "HR", "IE", "IT", "LT", "LU", "LV", "MT", "NL", "PT", "SI", "SK"
    };

    // Two-decimal currencies Flutterwave can charge by card; donors anywhere can always use USD.
    const string DefaultCurrencies = "USD,EUR,GBP,NGN,GHS,KES,ZAR,CAD,AUD,TZS,ZMW,EGP";

    public static IReadOnlyList<string> ParseAllowed(string? configured)
    {
        var codes = new List<string>();
        foreach (var part in (string.IsNullOrWhiteSpace(configured) ? DefaultCurrencies : configured).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = part.ToUpperInvariant();
            if (CodePattern.IsMatch(code) && !NotTwoDecimals.Contains(code) && !codes.Contains(code)) codes.Add(code);
        }
        if (!codes.Contains("USD")) codes.Insert(0, "USD");
        return codes;
    }

    public static string CurrencyForCountry(string? country) => country?.ToUpperInvariant() switch
    {
        "NG" => "NGN",
        "GB" => "GBP",
        "US" => "USD",
        "GH" => "GHS",
        "KE" => "KES",
        "ZA" => "ZAR",
        "CA" => "CAD",
        "AU" => "AUD",
        "TZ" => "TZS",
        "ZM" => "ZMW",
        "EG" => "EGP",
        { } c when EuroCountries.Contains(c) => "EUR",
        _ => "USD"
    };

    // Region from a CDN/proxy country header first, then the browser's language region (for example en-NG).
    public static string? DetectCountry(IHeaderDictionary headers, string? ipCountry = null)
    {
        foreach (var name in new[] { "CF-IPCountry", "X-Vercel-IP-Country", "X-Country-Code" })
        {
            var value = headers[name].ToString();
            if (value.Length == 2 && char.IsLetter(value[0]) && char.IsLetter(value[1])) return value.ToUpperInvariant();
        }
        if (ipCountry is { Length: 2 }) return ipCountry.ToUpperInvariant();
        foreach (var language in headers.AcceptLanguage.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = language.Split(';')[0];
            var dash = tag.LastIndexOf('-');
            if (dash > 0 && tag.Length - dash - 1 == 2) return tag[(dash + 1)..].ToUpperInvariant();
        }
        return null;
    }
}

/// <summary>Looks up a visitor's country from their IP address. Only used to suggest a currency; the donor can always change it.</summary>
public sealed class GeoIpService
{
    readonly HttpClient http;
    readonly ILogger logger;
    readonly string? urlTemplate;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Country, DateTimeOffset At)> cache = new();

    public GeoIpService(HttpClient http, ILogger logger, string? urlTemplate)
    {
        this.http = http;
        this.logger = logger;
        this.urlTemplate = string.IsNullOrWhiteSpace(urlTemplate) ? null : urlTemplate;
    }

    public static System.Net.IPAddress? ClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (forwarded is not null && System.Net.IPAddress.TryParse(forwarded, out var ip)) return ip;
        return context.Connection.RemoteIpAddress;
    }

    static bool IsPublic(System.Net.IPAddress ip)
    {
        if (System.Net.IPAddress.IsLoopback(ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC);
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }

    public async Task<string?> CountryAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (urlTemplate is null) return null;
        var ip = ClientIp(context);
        if (ip is null || !IsPublic(ip)) return null;
        var key = ip.ToString();
        if (cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromHours(24)) return hit.Country;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync(urlTemplate.Replace("{ip}", Uri.EscapeDataString(key)), timeout.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            string? code = null;
            foreach (var name in new[] { "country_code", "countryCode", "country" })
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: 2 } c) { code = c.ToUpperInvariant(); break; }
            if (cache.Count > 5000) cache.Clear();
            cache[key] = (code, DateTimeOffset.UtcNow);
            return code;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            logger.LogWarning("IP country lookup failed: {Message}", ex.Message);
            return null;
        }
    }
}

/// <summary>Fetches, caches and persists exchange rates. Rates are never hardcoded.</summary>
public sealed class ExchangeRateService
{
    readonly Func<CancellationToken, Task<SqliteConnection>> openDatabase;
    readonly HttpClient http;
    readonly ILogger logger;
    readonly string url;
    readonly TimeSpan cacheFor;
    readonly TimeSpan maxStale;
    readonly HashSet<string> wanted;
    readonly SemaphoreSlim gate = new(1, 1);
    RateSnapshot? cached;

    public ExchangeRateService(Func<CancellationToken, Task<SqliteConnection>> openDatabase, HttpClient http, ILogger logger, string url, TimeSpan cacheFor, TimeSpan maxStale, IEnumerable<string> currencies)
    {
        this.openDatabase = openDatabase;
        this.http = http;
        this.logger = logger;
        this.url = url;
        this.cacheFor = cacheFor;
        this.maxStale = maxStale;
        wanted = new HashSet<string>(currencies, StringComparer.Ordinal);
    }

    public TimeSpan MaxStale => maxStale;

    /// <summary>Returns fresh rates, stale-but-usable rates, or null when rates are unavailable or too old.</summary>
    public async Task<RateSnapshot?> GetAsync(CancellationToken cancellationToken)
    {
        var current = cached;
        if (current is not null && Age(current) < cacheFor) return current;

        await gate.WaitAsync(cancellationToken);
        try
        {
            cached ??= await LoadLatestAsync(cancellationToken);
            if (cached is not null && Age(cached) < cacheFor) return cached;

            try
            {
                var fresh = await FetchAsync(cancellationToken);
                await SaveAsync(fresh, cancellationToken);
                cached = fresh;
                return fresh;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException or SqliteException)
            {
                logger.LogWarning(ex, "Exchange-rate refresh failed.");
                if (cached is not null && Age(cached) <= maxStale) return cached with { Stale = true };
                return null;
            }
        }
        finally { gate.Release(); }
    }

    TimeSpan Age(RateSnapshot snapshot) => DateTimeOffset.UtcNow - snapshot.FetchedAt;

    async Task<RateSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (root.TryGetProperty("result", out var result) && result.GetString() != "success") throw new InvalidOperationException("Exchange-rate provider reported an error.");
        if (!root.TryGetProperty("rates", out var rates) || rates.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Exchange-rate response had no rates.");
        var picked = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var property in rates.EnumerateObject())
            if (wanted.Contains(property.Name) && property.Value.TryGetDecimal(out var rate) && rate > 0) picked[property.Name] = rate;
        picked["USD"] = 1m;
        DateTimeOffset? providerUpdated = root.TryGetProperty("time_last_update_unix", out var unix) && unix.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        return new RateSnapshot(picked, DateTimeOffset.UtcNow, providerUpdated, false, new Uri(url).Host);
    }

    async Task SaveAsync(RateSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var db = await openDatabase(cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO exchange_rate_snapshots(source,fetched_at,provider_updated_at,rates_json) VALUES($source,$fetched,$provider,$rates);
            DELETE FROM exchange_rate_snapshots WHERE id NOT IN (SELECT id FROM exchange_rate_snapshots ORDER BY id DESC LIMIT 200);
            """;
        command.Parameters.AddWithValue("$source", snapshot.Source);
        command.Parameters.AddWithValue("$fetched", snapshot.FetchedAt.ToString("O"));
        command.Parameters.AddWithValue("$provider", snapshot.ProviderUpdatedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$rates", JsonSerializer.Serialize(snapshot.UnitsPerUsd.ToDictionary(pair => pair.Key, pair => DonationMoney.FormatRate(pair.Value))));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<RateSnapshot?> LoadLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await openDatabase(cancellationToken);
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT source,fetched_at,provider_updated_at,rates_json FROM exchange_rate_snapshots ORDER BY id DESC LIMIT 1";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var rates = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3)) ?? [])
                if (wanted.Contains(pair.Key) && DonationMoney.TryParseRate(pair.Value, out var rate)) rates[pair.Key] = rate;
            rates["USD"] = 1m;
            // Stored rates from a narrower currency list are discarded so a refresh picks up the new currencies.
            if (wanted.Any(code => !rates.ContainsKey(code))) return null;
            DateTimeOffset? provider = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
            return new RateSnapshot(rates, DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), provider, false, reader.GetString(0));
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or FormatException)
        {
            logger.LogWarning(ex, "Could not load stored exchange rates.");
            return null;
        }
    }
}

/// <summary>Selected payment mode; a null filter means all modes.</summary>
public sealed record ModeScope(string? Filter, bool Invalid);

/// <summary>Validated filters for the payment search and export.</summary>
public sealed record PaymentFilter(string? Search, string? Status, string? Currency, string? Mode, string? From, string? To, string? Error);

/// <summary>One payment with original, estimated-USD and provider-reported amounts kept separate.</summary>
public sealed record PaymentRow(
    long Id, string Campaign, string Donor, string Status, string Currency, long OriginalMinor, string? ExchangeRate, string? RateRecordedAt,
    long UsdEquivalentCents, long? ChargedMinor, string? ChargedCurrency, long? ProviderFeeMinor, long? SettledMinor, string? SettlementCurrency,
    string? Reference, string? ProviderTransactionId, string? Mode, string Date, string? Email);