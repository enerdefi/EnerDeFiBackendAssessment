// =====================================================================
//  EnerDeFi assessment: simulated company verification providers
//  ---------------------------------------------------------------------
//  DO NOT MODIFY THIS PROJECT. Treat these as third-party services you
//  don't control. Your API should call them over HTTP.
//
//  Each provider runs on its own port, as separate third-party APIs
//  would run on separate hosts:
//    QuickVerify    http://localhost:5101   sync REST, header auth
//    SureCheck      http://localhost:5102   sync REST, Bearer auth
//    TrustLink      http://localhost:5103   async, signed requests + signed webhooks
//    Test control   http://localhost:5100   not part of the providers
// =====================================================================

using MockProviders;

var builder = WebApplication.CreateBuilder(args);

// Credentials live in a git-ignored file that is sent to you separately.
builder.Configuration.AddJsonFile("mocksecrets.json", optional: true, reloadOnChange: false);

var secrets = builder.Configuration.GetSection("Secrets").Get<MockSecrets>() ?? new MockSecrets();
var missing = secrets.MissingValues().ToList();
if (missing.Count > 0)
{
    Console.Error.WriteLine($"""

        Mock providers can't start: missing credentials ({string.Join(", ", missing)}).
        Save the mocksecrets.json file you received with the assessment into:
            providers/MockProviders/mocksecrets.json
        (See mocksecrets.example.json for the expected shape.)

        """);
    return 1;
}

var ports = new
{
    Admin = builder.Configuration.GetValue("Ports:Admin", 5100),
    QuickVerify = builder.Configuration.GetValue("Ports:QuickVerify", 5101),
    SureCheck = builder.Configuration.GetValue("Ports:SureCheck", 5102),
    TrustLink = builder.Configuration.GetValue("Ports:TrustLink", 5103),
};

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenLocalhost(ports.Admin);
    k.ListenLocalhost(ports.QuickVerify);
    k.ListenLocalhost(ports.SureCheck);
    k.ListenLocalhost(ports.TrustLink);
});

builder.Services.AddSingleton(secrets);
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddProblemDetails();

var app = builder.Build();

QuickVerifyApi.Map(app.MapGroup("").RequireHost($"*:{ports.QuickVerify}"));
SureCheckApi.Map(app.MapGroup("").RequireHost($"*:{ports.SureCheck}"));
TrustLinkApi.Map(app.MapGroup("").RequireHost($"*:{ports.TrustLink}"));
AdminApi.Map(app.MapGroup("").RequireHost($"*:{ports.Admin}"), app.Configuration);

app.Logger.LogInformation(
    "QuickVerify http://localhost:{Qv} | SureCheck http://localhost:{Sc} | TrustLink http://localhost:{Tl} | Test control http://localhost:{Admin}",
    ports.QuickVerify, ports.SureCheck, ports.TrustLink, ports.Admin);

app.Run();
return 0;

public partial class Program;
