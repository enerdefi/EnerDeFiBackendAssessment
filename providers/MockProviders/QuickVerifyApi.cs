using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace MockProviders;

/// <summary>
/// QuickVerify: synchronous REST lookup. Modelled on the style of Dojah's CAC lookup:
///   GET /api/v1/kyc/cac/basic?rc_number=1234567&amp;company_type=COMPANY
///   Headers: Authorization: &lt;secret key, NOT Bearer&gt;, AppId: &lt;app id&gt;
///   Response: { "entity": { ... } }
/// Fast and cheap, but unreliable and rate limited (5 requests per second).
/// </summary>
public static partial class QuickVerifyApi
{
    private const int RateLimitPerSecond = 5;
    private static readonly ConcurrentDictionary<long, int> RequestsPerSecond = new();

    private static readonly Dictionary<string, CompanyKind> CompanyTypes = new()
    {
        ["BUSINESS_NAME"] = CompanyKind.BusinessName,
        ["COMPANY"] = CompanyKind.LimitedCompany,
        ["INCORPORATED_TRUSTEES"] = CompanyKind.IncorporatedTrustees,
    };

    [GeneratedRegex(@"^\d{6,8}$")]
    private static partial Regex RcDigits();

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", () => Results.Ok(new
        {
            provider = "QuickVerify",
            lookup = "GET /api/v1/kyc/cac/basic?rc_number={digits}&company_type={BUSINESS_NAME|COMPANY|INCORPORATED_TRUSTEES}",
            auth = "Authorization: <secret key> (no Bearer prefix) and AppId: <app id>",
        }));

        g.MapGet("/api/v1/kyc/cac/basic", Lookup);
    }

    private static async Task<IResult> Lookup(
        HttpContext http, ProviderRegistry registry, MockSecrets secrets, ILogger<ProviderRegistry> log, CancellationToken ct)
    {
        var p = registry["quickverify"];
        var call = p.NextCall();
        var req = http.Request;

        // 1. Authentication
        var secret = req.Headers.Authorization.FirstOrDefault();
        var appId = req.Headers["AppId"].FirstOrDefault();
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(appId))
        {
            log.LogWarning("[QuickVerify #{Call}] missing credentials -> 401", call);
            return Error(401, "Missing credentials. Send your secret key in the Authorization header and your app ID in the AppId header.");
        }
        if (secret.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("[QuickVerify #{Call}] Bearer prefix used -> 401", call);
            return Error(401, "Send the secret key as-is in the Authorization header, without the 'Bearer' prefix.");
        }
        if (!Crypto.SafeEquals(secret, secrets.QuickVerify.SecretKey) || !Crypto.SafeEquals(appId, secrets.QuickVerify.AppId))
        {
            log.LogWarning("[QuickVerify #{Call}] invalid credentials {Masked} -> 401", call, Crypto.Mask(secret));
            return Error(401, "Invalid secret key or app ID.");
        }

        // 2. Rate limit
        var second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var count = RequestsPerSecond.AddOrUpdate(second, 1, (_, c) => c + 1);
        foreach (var old in RequestsPerSecond.Keys.Where(k => k < second - 5)) RequestsPerSecond.TryRemove(old, out _);
        if (count > RateLimitPerSecond)
        {
            log.LogWarning("[QuickVerify #{Call}] rate limited -> 429", call);
            http.Response.Headers.RetryAfter = "1";
            return Error(429, $"Too many requests. The limit is {RateLimitPerSecond} requests per second.");
        }

        // 3. Validation
        var rc = req.Query["rc_number"].FirstOrDefault()?.Trim() ?? "";
        var type = req.Query["company_type"].FirstOrDefault()?.Trim() ?? "";
        if (!RcDigits().IsMatch(rc))
            return Error(400, "rc_number is required and must be 6 to 8 digits, without the RC prefix.");
        if (!CompanyTypes.TryGetValue(type, out var kind))
            return Error(400, "company_type must be one of BUSINESS_NAME, COMPANY, INCORPORATED_TRUSTEES.");

        // 4. Simulated unreliability
        var (outcome, delay, slow) = await Chaos.ApplyAsync(p, ct);
        switch (outcome)
        {
            case ChaosOutcome.Down:
                log.LogWarning("[QuickVerify #{Call}] DOWN -> 503", call);
                return Error(503, "Service temporarily unavailable.");
            case ChaosOutcome.Cancelled:
                log.LogWarning("[QuickVerify #{Call}] caller gave up after waiting", call);
                return Results.Empty;
            case ChaosOutcome.Fail:
                log.LogWarning("[QuickVerify #{Call}] random failure -> 500 ({Delay} ms)", call, delay);
                return Error(500, "Internal server error.");
        }

        // 5. Lookup
        if (!CompanyRegister.TryFind(rc, kind, out var c))
        {
            log.LogInformation("[QuickVerify #{Call}] {Rc} -> 404 not found ({Delay} ms)", call, rc, delay);
            return Error(404, "No record found for the supplied identifier.");
        }

        log.LogInformation("[QuickVerify #{Call}] {Rc} -> {Status} ({Delay} ms{Slow})",
            call, rc, c.Active ? "ACTIVE" : "INACTIVE", delay, slow ? ", slow" : "");

        return Results.Ok(new
        {
            entity = new
            {
                company_name = c.LegalName,
                type_of_company = type,
                status = c.Active ? "ACTIVE" : "INACTIVE",
                rc_number = c.RcDigits,
                state = c.State,
                date_of_registration = c.RegisteredOn.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK"),
            }
        });
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
