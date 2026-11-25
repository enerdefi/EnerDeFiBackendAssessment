using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MockProviders;

/// <summary>
/// TrustLink: asynchronous verification with webhooks. Modelled on the style of
/// Smile ID's async business verification:
///   POST /v1/async_business_verification   (body carries partner_id, timestamp and a signature)
///   -> 202 Accepted with a TrustLink job id
///   ... a few seconds later the result is POSTed to your callback_url, signed with your webhook secret.
/// Cheapest provider and supports every company type, but results are not immediate.
/// </summary>
public static partial class TrustLinkApi
{
    public static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> BusinessTypes = ["bn", "co", "it"];

    [GeneratedRegex(@"^RC\d{6,8}$")]
    private static partial Regex RcNumber();

    public sealed class SubmitBody
    {
        [JsonPropertyName("partner_id")] public string? PartnerId { get; set; }
        [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
        [JsonPropertyName("signature")] public string? Signature { get; set; }
        [JsonPropertyName("callback_url")] public string? CallbackUrl { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("id_type")] public string? IdType { get; set; }
        [JsonPropertyName("id_number")] public string? IdNumber { get; set; }
        [JsonPropertyName("business_type")] public string? BusinessType { get; set; }
        [JsonPropertyName("partner_params")] public PartnerParams? PartnerParams { get; set; }
    }

    public sealed class PartnerParams
    {
        [JsonPropertyName("job_id")] public string? JobId { get; set; }
        [JsonPropertyName("user_id")] public string? UserId { get; set; }
    }

    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", () => Results.Ok(new
        {
            provider = "TrustLink",
            submit = "POST /v1/async_business_verification",
            signature = "base64( HMAC-SHA256( key = api key, message = timestamp + partner_id + \"sid_request\" ) )",
            webhook = "Result is POSTed to callback_url. Verify X-TrustLink-Signature = \"sha256=\" + hex( HMAC-SHA256( key = webhook secret, message = X-TrustLink-Timestamp + \".\" + raw body ) )",
        }));

        g.MapPost("/v1/async_business_verification", Submit);
    }

    private static async Task<IResult> Submit(
        SubmitBody? body, ProviderRegistry registry, MockSecrets secrets, WebhookDispatcher dispatcher,
        ILogger<ProviderRegistry> log, CancellationToken ct)
    {
        var p = registry["trustlink"];
        var call = p.NextCall();
        var s = secrets.TrustLink;

        if (body is null) return Error(400, "Request body is required.");

        // 1. Authentication: partner id + request signature + fresh timestamp
        if (!Crypto.SafeEquals(body.PartnerId, s.PartnerId))
        {
            log.LogWarning("[TrustLink #{Call}] unknown partner_id -> 401", call);
            return Error(401, "Unknown partner_id.");
        }
        if (!DateTimeOffset.TryParse(body.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
            return Error(401, "timestamp is required, in ISO 8601 format.");
        if ((DateTimeOffset.UtcNow - ts).Duration() > SignatureTolerance)
        {
            log.LogWarning("[TrustLink #{Call}] stale timestamp {Ts} -> 401", call, body.Timestamp);
            return Error(401, "Request timestamp is outside the allowed 5 minute window.");
        }
        var expected = Crypto.HmacSha256Base64(s.ApiKey, body.Timestamp + body.PartnerId + "sid_request");
        if (!Crypto.SafeEquals(body.Signature, expected))
        {
            log.LogWarning("[TrustLink #{Call}] invalid request signature -> 401", call);
            return Error(401, "Invalid signature.");
        }

        // 2. Validation
        if (!Uri.TryCreate(body.CallbackUrl, UriKind.Absolute, out var callback) || callback.Scheme is not ("http" or "https"))
            return Error(400, "callback_url must be an absolute http or https URL.");
        if (body.Country != "NG") return Error(400, "country must be NG.");
        if (body.IdType != "BUSINESS_REGISTRATION") return Error(400, "id_type must be BUSINESS_REGISTRATION.");
        if (body.IdNumber is null || !RcNumber().IsMatch(body.IdNumber))
            return Error(400, "id_number must be the RC number including the RC prefix, e.g. RC1234567.");
        if (body.BusinessType is null || !BusinessTypes.Contains(body.BusinessType))
            return Error(400, "business_type must be one of bn, co, it.");
        if (string.IsNullOrWhiteSpace(body.PartnerParams?.JobId))
            return Error(400, "partner_params.job_id is required and must be unique per request.");
        if (!dispatcher.TryReservePartnerJobId(body.PartnerParams.JobId))
        {
            log.LogWarning("[TrustLink #{Call}] duplicate job_id {JobId} -> 409", call, body.PartnerParams.JobId);
            return Error(409, $"partner_params.job_id '{body.PartnerParams.JobId}' has already been used.");
        }

        // 3. Simulated unreliability (on submission)
        var (outcome, delay, _) = await Chaos.ApplyAsync(p, ct);
        if (outcome != ChaosOutcome.Proceed)
        {
            dispatcher.ReleasePartnerJobId(body.PartnerParams.JobId);
            if (outcome == ChaosOutcome.Down)
            {
                log.LogWarning("[TrustLink #{Call}] DOWN -> 503", call);
                return Error(503, "Service temporarily unavailable.");
            }
            if (outcome == ChaosOutcome.Cancelled) return Results.Empty;
            log.LogWarning("[TrustLink #{Call}] random failure -> 500 ({Delay} ms)", call, delay);
            return Error(500, "Internal error. The job was not created.");
        }

        // 4. Accept and schedule the webhook
        var job = dispatcher.Schedule(body, p.Mode);
        log.LogInformation("[TrustLink #{Call}] accepted {Rc} as {TlJob} (job_id {JobId}); webhook in ~{Seconds}s",
            call, body.IdNumber, job.TrustLinkJobId, body.PartnerParams.JobId, (int)job.InitialDelay.TotalSeconds);

        return Results.Json(new
        {
            success = true,
            trustlink_job_id = job.TrustLinkJobId,
            message = "Job accepted. The result will be sent to your callback_url.",
        }, statusCode: 202);
    }

    private static IResult Error(int status, string message) =>
        Results.Json(new { success = false, error = message }, statusCode: status);
}

// ---------------------------------------------------------------------
// Webhook delivery
// ---------------------------------------------------------------------

public sealed record WebhookJob(
    string TrustLinkJobId, string PartnerJobId, Uri CallbackUrl, string Body, TimeSpan InitialDelay, bool SendDuplicate);

public sealed record WebhookDelivery(
    string DeliveryId, string TrustLinkJobId, string Kind, int Attempt, DateTimeOffset SentAt,
    int? ResponseStatus, string? Error, string Timestamp, string Signature, string Body, Uri CallbackUrl);

public sealed class WebhookDispatcher(
    IHttpClientFactory httpFactory, MockSecrets secrets, ILogger<WebhookDispatcher> log, IHostApplicationLifetime lifetime)
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, byte> _usedPartnerJobIds = new();
    private readonly ConcurrentDictionary<string, WebhookJob> _jobs = new();
    private readonly ConcurrentQueue<WebhookDelivery> _deliveries = new();

    public bool TryReservePartnerJobId(string jobId) => _usedPartnerJobIds.TryAdd(jobId, 0);
    public void ReleasePartnerJobId(string jobId) => _usedPartnerJobIds.TryRemove(jobId, out _);

    public IEnumerable<WebhookDelivery> Deliveries => _deliveries.Reverse();
    public bool TryGetJob(string id, out WebhookJob job) => _jobs.TryGetValue(id, out job!);

    public WebhookJob Schedule(TrustLinkApi.SubmitBody body, ProviderMode mode)
    {
        var rc = body.IdNumber![2..];
        var kind = body.BusinessType switch
        {
            "co" => CompanyKind.LimitedCompany,
            "it" => CompanyKind.IncorporatedTrustees,
            _ => CompanyKind.BusinessName
        };

        var issuerUnavailable = mode == ProviderMode.Normal && Random.Shared.NextDouble() < 0.10;
        var found = CompanyRegister.TryFind(rc, kind, out var company);

        var (code, text, verify) = issuerUnavailable
            ? ("1003", "Issuer unavailable. The registry could not be reached; please retry.", "Issuer Unavailable")
            : found
                ? ("1001", "Business verified.", "Verified")
                : ("1002", "Business not found.", "Not Verified");

        var tlJobId = "tl_" + Guid.NewGuid().ToString("N")[..16];
        var payload = new
        {
            @event = "business_verification.completed",
            trustlink_job_id = tlJobId,
            partner_params = new { job_id = body.PartnerParams!.JobId, user_id = body.PartnerParams.UserId },
            result_code = code,
            result_text = text,
            actions = new { verify_business = verify },
            company_information = found && !issuerUnavailable
                ? new
                {
                    legal_name = company.LegalName,
                    registration_number = body.IdNumber,
                    company_type = body.BusinessType,
                    status = company.Active ? "ACTIVE" : "INACTIVE",
                    registration_date = company.RegisteredOn.ToString("yyyy-MM-dd"),
                    state = company.State,
                }
                : null,
            completed_at = DateTimeOffset.UtcNow.ToString("O"),
        };

        var delay = mode switch
        {
            ProviderMode.Slow => TimeSpan.FromSeconds(30),
            ProviderMode.Healthy => TimeSpan.FromSeconds(Random.Shared.Next(2, 5)),
            _ => TimeSpan.FromSeconds(Random.Shared.Next(3, 11)),
        };
        var duplicate = mode == ProviderMode.Normal && Random.Shared.NextDouble() < 0.15;

        var job = new WebhookJob(tlJobId, body.PartnerParams.JobId!, new Uri(body.CallbackUrl!),
            JsonSerializer.Serialize(payload, Json), delay, duplicate);
        _jobs[tlJobId] = job;

        _ = Task.Run(() => DeliverWithRetriesAsync(job), lifetime.ApplicationStopping);
        return job;
    }

    private async Task DeliverWithRetriesAsync(WebhookJob job)
    {
        var stopping = lifetime.ApplicationStopping;
        try
        {
            await Task.Delay(job.InitialDelay, stopping);

            for (var attempt = 1; attempt <= RetryDelays.Length + 1; attempt++)
            {
                var ok = await SendAsync(job, "result", attempt, Sign(job.Body), stopping);
                if (ok)
                {
                    if (job.SendDuplicate)
                    {
                        // At-least-once delivery: the same event is sometimes delivered twice.
                        await Task.Delay(TimeSpan.FromSeconds(2), stopping);
                        await SendAsync(job, "duplicate", 1, Sign(job.Body), stopping);
                    }
                    return;
                }
                if (attempt <= RetryDelays.Length)
                    await Task.Delay(RetryDelays[attempt - 1], stopping);
            }
            log.LogError("[TrustLink webhook] {Job} gave up after {Attempts} attempts", job.TrustLinkJobId, RetryDelays.Length + 1);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogError(ex, "[TrustLink webhook] {Job} delivery crashed", job.TrustLinkJobId); }
    }

    /// <summary>Resend the most recent delivery of a job with its ORIGINAL timestamp and signature.</summary>
    public async Task<WebhookDelivery?> ReplayAsync(string tlJobId, CancellationToken ct)
    {
        var last = _deliveries.Reverse().FirstOrDefault(d => d.TrustLinkJobId == tlJobId && d.Kind != "forged");
        if (last is null || !_jobs.TryGetValue(tlJobId, out var job)) return null;
        await SendAsync(job, "replay", 1, (last.Timestamp, last.Signature), ct);
        return _deliveries.Reverse().First();
    }

    /// <summary>Send a job's payload with a fresh timestamp but a signature made with the wrong secret.</summary>
    public async Task<WebhookDelivery?> SendForgedAsync(string tlJobId, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(tlJobId, out var job)) return null;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var forged = "sha256=" + Crypto.HmacSha256Hex("not-the-real-secret", ts + "." + job.Body);
        await SendAsync(job, "forged", 1, (ts, forged), ct);
        return _deliveries.Reverse().First();
    }

    private (string Timestamp, string Signature) Sign(string body)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return (ts, "sha256=" + Crypto.HmacSha256Hex(secrets.TrustLink.WebhookSecret, ts + "." + body));
    }

    private async Task<bool> SendAsync(WebhookJob job, string kind, int attempt, (string Timestamp, string Signature) sig, CancellationToken ct)
    {
        var deliveryId = "dlv_" + Guid.NewGuid().ToString("N")[..12];
        int? status = null;
        string? error = null;

        try
        {
            using var client = httpFactory.CreateClient("webhooks");
            using var request = new HttpRequestMessage(HttpMethod.Post, job.CallbackUrl)
            {
                Content = new StringContent(job.Body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-TrustLink-Event", "business_verification.completed");
            request.Headers.Add("X-TrustLink-Delivery", deliveryId);
            request.Headers.Add("X-TrustLink-Timestamp", sig.Timestamp);
            request.Headers.Add("X-TrustLink-Signature", sig.Signature);

            using var response = await client.SendAsync(request, ct);
            status = (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            error = ex is TaskCanceledException ? "Timed out after 5 seconds" : ex.Message;
        }

        var ok = status is >= 200 and < 300;
        _deliveries.Enqueue(new WebhookDelivery(deliveryId, job.TrustLinkJobId, kind, attempt, DateTimeOffset.UtcNow,
            status, error, sig.Timestamp, sig.Signature, job.Body, job.CallbackUrl));
        while (_deliveries.Count > 500) _deliveries.TryDequeue(out _);

        if (ok)
            log.LogInformation("[TrustLink webhook] {Kind} {Job} -> {Url} : {Status}", kind, job.TrustLinkJobId, job.CallbackUrl, status);
        else
            log.LogWarning("[TrustLink webhook] {Kind} {Job} attempt {Attempt} -> {Url} : {Result}",
                kind, job.TrustLinkJobId, attempt, job.CallbackUrl, status?.ToString() ?? error);
        return ok;
    }
}
