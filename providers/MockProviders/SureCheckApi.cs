using System.Text.Json;
using System.Text.RegularExpressions;

namespace MockProviders;

/// <summary>
/// SureCheck: synchronous REST verification. Modelled on the style of VerifyMe's CAC endpoint:
///   POST /v1/verifications/identities/cac
///   Headers: Authorization: Bearer &lt;api key&gt;
///   Body: { "type": "limited_company" | "business", "rcNumber": "1234567" }
///   Response: { "status": "success", "data": { ... } }
/// Reliable but slow and expensive. Does NOT support incorporated trustees.
/// </summary>
public static partial class SureCheckApi
{
    private static readonly Dictionary<string, CompanyKind> SupportedTypes = new()
    {
        ["business"] = CompanyKind.BusinessName,
        ["limited_company"] = CompanyKind.LimitedCompany,
    };

    [GeneratedRegex(@"^\d{6,8}$")]
    private static partial Regex RcDigits();

    public sealed record VerifyBody(string? Type, JsonElement? RcNumber);

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", () => Results.Ok(new
        {
            provider = "SureCheck",
            verify = "POST /v1/verifications/identities/cac  { \"type\": \"limited_company|business\", \"rcNumber\": \"1234567\" }",
            auth = "Authorization: Bearer <api key>",
        }));

        g.MapPost("/v1/verifications/identities/cac", Verify);
    }

    private static async Task<IResult> Verify(
        HttpContext http, VerifyBody? body, ProviderRegistry registry, MockSecrets secrets,
        ILogger<ProviderRegistry> log, CancellationToken ct)
    {
        var p = registry["surecheck"];
        var call = p.NextCall();

        // 1. Authentication
        var auth = http.Request.Headers.Authorization.FirstOrDefault();
        const string prefix = "Bearer ";
        if (auth is null || !auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("[SureCheck #{Call}] missing bearer token -> 401", call);
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Error(401, "Authorization header with a Bearer token is required.");
        }
        var key = auth[prefix.Length..].Trim();
        if (!Crypto.SafeEquals(key, secrets.SureCheck.ApiKey))
        {
            log.LogWarning("[SureCheck #{Call}] invalid token {Masked} -> 401", call, Crypto.Mask(key));
            http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            return Error(401, "Invalid API key.");
        }

        // 2. Validation
        var rc = body?.RcNumber switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString()?.Trim() ?? "",
            { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
            _ => ""
        };
        var type = body?.Type?.Trim() ?? "";
        if (type == "incorporated_trustee")
            return Error(400, "Company type 'incorporated_trustee' is not supported.");
        if (!SupportedTypes.TryGetValue(type, out var kind))
            return Error(400, "Invalid company type. Supported types: limited_company, business.");
        if (!RcDigits().IsMatch(rc))
            return Error(400, "rcNumber must be 6 to 8 digits, without the RC prefix.");

        // 3. Simulated unreliability
        var (outcome, delay, slow) = await Chaos.ApplyAsync(p, ct);
        switch (outcome)
        {
            case ChaosOutcome.Down:
                log.LogWarning("[SureCheck #{Call}] DOWN -> 503", call);
                return Error(503, "Service temporarily unavailable. Please try again later.");
            case ChaosOutcome.Cancelled:
                log.LogWarning("[SureCheck #{Call}] caller gave up after waiting", call);
                return Results.Empty;
            case ChaosOutcome.Fail:
                log.LogWarning("[SureCheck #{Call}] random failure -> 500 ({Delay} ms)", call, delay);
                return Error(500, "An unexpected error occurred.");
        }

        // 4. Lookup
        if (!CompanyRegister.TryFind(rc, kind, out var c))
        {
            log.LogInformation("[SureCheck #{Call}] {Rc} -> 404 not found ({Delay} ms)", call, rc, delay);
            return Error(404, "Company with provided CAC not found.");
        }

        log.LogInformation("[SureCheck #{Call}] {Rc} -> {Status} ({Delay} ms{Slow})",
            call, rc, c.Active ? "ACTIVE" : "INACTIVE", delay, slow ? ", slow" : "");

        return Results.Ok(new
        {
            status = "success",
            data = new
            {
                rcNumber = c.RcDigits,
                companyName = c.LegalName,
                companyType = kind == CompanyKind.LimitedCompany ? "PRIVATE_COMPANY_LIMITED_BY_SHARES" : "BUSINESS_NAME",
                registrationDate = c.RegisteredOn,
                state = c.State,
                status = c.Active ? "ACTIVE" : "INACTIVE",
            }
        });
    }

    private static IResult Error(int status, string message) =>
        Results.Json(new { status = "error", message }, statusCode: status);
}
