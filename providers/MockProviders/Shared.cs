using System.Security.Cryptography;
using System.Text;

namespace MockProviders;

// ---------------------------------------------------------------------
// Runtime behaviour shared by all simulated providers
// ---------------------------------------------------------------------

/// <summary>
/// Normal  = behaves as configured (random failures and slow responses).
/// Healthy = never fails, never slow.
/// Slow    = every response is slow.
/// Down    = every request returns 503 immediately.
/// </summary>
public enum ProviderMode { Normal, Healthy, Slow, Down }

public sealed class ProviderBehaviour
{
    public double FailureRate { get; set; }
    public double SlowRate { get; set; }
    public int NormalDelayMs { get; set; } = 150;
    public int SlowDelayMs { get; set; } = 5000;
}

public sealed class ProviderRuntime(string key, string name, ProviderBehaviour behaviour)
{
    private long _calls;
    private volatile ProviderMode _mode = ProviderMode.Normal;

    public string Key { get; } = key;
    public string Name { get; } = name;
    public ProviderBehaviour Behaviour { get; } = behaviour;
    public ProviderMode Mode { get => _mode; set => _mode = value; }
    public long CallCount => Interlocked.Read(ref _calls);
    public long NextCall() => Interlocked.Increment(ref _calls);
}

public sealed class ProviderRegistry
{
    private readonly Dictionary<string, ProviderRuntime> _providers;

    public ProviderRegistry(IConfiguration config)
    {
        ProviderRuntime Make(string key, string name) =>
            new(key, name, config.GetSection($"Behaviour:{name}").Get<ProviderBehaviour>() ?? new ProviderBehaviour());

        _providers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["quickverify"] = Make("quickverify", "QuickVerify"),
            ["surecheck"] = Make("surecheck", "SureCheck"),
            ["trustlink"] = Make("trustlink", "TrustLink"),
        };
    }

    public ProviderRuntime this[string key] => _providers[key];
    public IEnumerable<ProviderRuntime> All => _providers.Values;
    public bool TryGet(string key, out ProviderRuntime provider) => _providers.TryGetValue(key, out provider!);
}

public enum ChaosOutcome { Proceed, Fail, Down, Cancelled }

public static class Chaos
{
    /// <summary>Applies the provider's mode: may delay, fail, or report the provider as down.</summary>
    public static async Task<(ChaosOutcome Outcome, int DelayMs, bool Slow)> ApplyAsync(ProviderRuntime p, CancellationToken ct)
    {
        var mode = p.Mode;
        if (mode == ProviderMode.Down) return (ChaosOutcome.Down, 0, false);

        var b = p.Behaviour;
        var fail = mode == ProviderMode.Normal && Random.Shared.NextDouble() < b.FailureRate;
        var slow = mode == ProviderMode.Slow
                   || (mode == ProviderMode.Normal && !fail && Random.Shared.NextDouble() < b.SlowRate);
        var delay = slow ? b.SlowDelayMs : b.NormalDelayMs;

        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { return (ChaosOutcome.Cancelled, delay, slow); }

        return (fail ? ChaosOutcome.Fail : ChaosOutcome.Proceed, delay, slow);
    }
}

// ---------------------------------------------------------------------
// The fake company register all providers look up.
// Outcomes are deterministic so candidates and reviewers can test them:
//   RC number ending in 0 -> not found
//   RC number ending in 9 -> found, but INACTIVE
//   anything else         -> found and ACTIVE
// ---------------------------------------------------------------------

public enum CompanyKind { BusinessName, LimitedCompany, IncorporatedTrustees }

public sealed record CompanyRecord(
    string RcDigits, string LegalName, CompanyKind Kind, bool Active, DateTime RegisteredOn, string State);

public static class CompanyRegister
{
    private static readonly string[] Words =
        ["SUNRISE", "GREENFIELD", "ARKLIGHT", "NIGERGRID", "SAHEL", "COASTAL", "HARMATTAN", "DELTA", "SAVANNAH", "ZENITH"];
    private static readonly string[] States =
        ["Lagos", "Abuja (FCT)", "Kano", "Rivers", "Oyo", "Kaduna", "Enugu", "Ogun", "Plateau", "Edo"];

    public static bool TryFind(string rcDigits, CompanyKind kind, out CompanyRecord record)
    {
        record = default!;
        if (rcDigits.EndsWith('0')) return false;

        var seed = rcDigits.Aggregate(17, (h, c) => h * 31 + c);
        var word = Words[Math.Abs(seed) % Words.Length];
        var state = States[Math.Abs(seed / 7) % States.Length];
        var suffix = kind switch
        {
            CompanyKind.LimitedCompany => "ENERGY LIMITED",
            CompanyKind.IncorporatedTrustees => "ENERGY FOUNDATION",
            _ => "SOLAR ENTERPRISES"
        };
        var registered = new DateTime(2010, 1, 1, 9, 0, 0, DateTimeKind.Utc).AddDays(Math.Abs(seed) % 5000);

        record = new CompanyRecord(rcDigits, $"{word} {suffix}", kind, Active: !rcDigits.EndsWith('9'), registered, state);
        return true;
    }
}

// ---------------------------------------------------------------------
// Credentials. Loaded from mocksecrets.json (git-ignored, sent by email).
// ---------------------------------------------------------------------

public sealed class MockSecrets
{
    public QuickVerifySecrets QuickVerify { get; set; } = new();
    public SureCheckSecrets SureCheck { get; set; } = new();
    public TrustLinkSecrets TrustLink { get; set; } = new();

    public sealed class QuickVerifySecrets { public string AppId { get; set; } = ""; public string SecretKey { get; set; } = ""; }
    public sealed class SureCheckSecrets { public string ApiKey { get; set; } = ""; }
    public sealed class TrustLinkSecrets
    {
        public string PartnerId { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string WebhookSecret { get; set; } = "";
    }

    public IEnumerable<string> MissingValues()
    {
        if (string.IsNullOrWhiteSpace(QuickVerify.AppId)) yield return "QuickVerify:AppId";
        if (string.IsNullOrWhiteSpace(QuickVerify.SecretKey)) yield return "QuickVerify:SecretKey";
        if (string.IsNullOrWhiteSpace(SureCheck.ApiKey)) yield return "SureCheck:ApiKey";
        if (string.IsNullOrWhiteSpace(TrustLink.PartnerId)) yield return "TrustLink:PartnerId";
        if (string.IsNullOrWhiteSpace(TrustLink.ApiKey)) yield return "TrustLink:ApiKey";
        if (string.IsNullOrWhiteSpace(TrustLink.WebhookSecret)) yield return "TrustLink:WebhookSecret";
    }
}

public static class Crypto
{
    public static string HmacSha256Hex(string key, string message) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message))).ToLowerInvariant();

    public static string HmacSha256Base64(string key, string message) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message)));

    public static bool SafeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }

    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "(none)" : value.Length <= 8 ? "****" : value[..8] + "****";
}
