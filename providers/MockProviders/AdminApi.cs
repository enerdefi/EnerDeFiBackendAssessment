namespace MockProviders;

/// <summary>
/// Test control panel (port 5100). Not part of any provider: use it to change provider
/// behaviour and to inspect or re-send webhooks while you test.
/// </summary>
public static class AdminApi
{
    public static void Map(RouteGroupBuilder g, IConfiguration config)
    {
        g.MapGet("/", () => Results.Ok(new
        {
            service = "EnerDeFi mock providers - test control panel",
            providers = new
            {
                quickVerify = $"http://localhost:{config.GetValue("Ports:QuickVerify", 5101)}",
                sureCheck = $"http://localhost:{config.GetValue("Ports:SureCheck", 5102)}",
                trustLink = $"http://localhost:{config.GetValue("Ports:TrustLink", 5103)}",
            },
            endpoints = new[]
            {
                "GET  /admin/status",
                "PUT  /admin/{quickverify|surecheck|trustlink}/mode/{Normal|Healthy|Slow|Down}",
                "GET  /admin/trustlink/webhooks",
                "POST /admin/trustlink/webhooks/{trustlinkJobId}/replay",
                "POST /admin/trustlink/webhooks/{trustlinkJobId}/forged",
            }
        }));

        g.MapGet("/admin/status", (ProviderRegistry registry) => Results.Ok(registry.All.Select(p => new
        {
            provider = p.Name,
            mode = p.Mode.ToString(),
            calls = p.CallCount,
            p.Behaviour.FailureRate,
            p.Behaviour.SlowRate,
            p.Behaviour.NormalDelayMs,
            p.Behaviour.SlowDelayMs,
        })));

        g.MapPut("/admin/{provider}/mode/{mode}", (string provider, string mode, ProviderRegistry registry) =>
        {
            if (!registry.TryGet(provider, out var p))
                return Results.Problem(statusCode: 404, title: "Unknown provider. Use quickverify, surecheck or trustlink.");
            if (!Enum.TryParse<ProviderMode>(mode, ignoreCase: true, out var m))
                return Results.Problem(statusCode: 400, title: "Mode must be Normal, Healthy, Slow or Down.");
            p.Mode = m;
            return Results.Ok(new { provider = p.Name, mode = p.Mode.ToString() });
        });

        g.MapGet("/admin/trustlink/webhooks", (WebhookDispatcher d) => Results.Ok(d.Deliveries.Take(100).Select(x => new
        {
            x.DeliveryId,
            x.TrustLinkJobId,
            x.Kind,
            x.Attempt,
            x.SentAt,
            callbackUrl = x.CallbackUrl.ToString(),
            x.ResponseStatus,
            x.Error,
        })));

        g.MapPost("/admin/trustlink/webhooks/{id}/replay", async (string id, WebhookDispatcher d, CancellationToken ct) =>
        {
            var result = await d.ReplayAsync(id, ct);
            return result is null
                ? Results.Problem(statusCode: 404, title: "No delivered webhook found for that TrustLink job id.")
                : Results.Ok(new
                {
                    note = "Re-sent with the ORIGINAL timestamp and signature, like a replay attack or a late retry.",
                    result.DeliveryId,
                    result.ResponseStatus,
                    result.Error
                });
        });

        g.MapPost("/admin/trustlink/webhooks/{id}/forged", async (string id, WebhookDispatcher d, CancellationToken ct) =>
        {
            var result = await d.SendForgedAsync(id, ct);
            return result is null
                ? Results.Problem(statusCode: 404, title: "Unknown TrustLink job id.")
                : Results.Ok(new
                {
                    note = "Sent with a signature made with the wrong secret. Your API should reject it.",
                    result.DeliveryId,
                    result.ResponseStatus,
                    result.Error
                });
        });
    }
}
