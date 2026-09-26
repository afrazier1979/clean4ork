using Clean4ork.Data;
using Clean4ork.Scraper;
using Clean4ork.Scraper.Sources;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddClean4orkData(builder.Configuration);

builder.Services.AddHttpClient<PhillyInspectionSource>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Clean4orkBot/0.1 (+https://clean4ork.example/about)");
    })
    // The name/address search axis is stateful: the form POST sets a session
    // cookie that the follow-up page GETs require. Give this client its own
    // cookie jar so that handshake works. (UseCookies defaults on, but we set
    // an explicit container so the intent — and the per-client scope — is clear.)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        CookieContainer = new System.Net.CookieContainer(),
        UseCookies = true,
        AllowAutoRedirect = true
    })
    .AddStandardResilienceHandler(); // retries, circuit breaker, timeout — Polly under the hood

builder.Services.AddSingleton<IInspectionSource>(sp =>
    sp.GetRequiredService<PhillyInspectionSource>());

// Statewide facility INDEX via the PA open-data (Socrata) API. This is the
// breadth layer — discovery + geocoding + pass/fail — not scoreable depth.
builder.Services.AddHttpClient<PaOpenDataFacilitySource>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Clean4orkBot/0.1 (+https://clean4ork.example/about)");

        // An optional Socrata app token raises the anonymous rate limit.
        // Set "PaOpenData:AppToken" in configuration/user-secrets to use one.
        var appToken = builder.Configuration["PaOpenData:AppToken"];
        if (!string.IsNullOrWhiteSpace(appToken))
            client.DefaultRequestHeaders.Add("X-App-Token", appToken);
    })
    .AddStandardResilienceHandler();

builder.Services.AddSingleton<IFacilityIndexSource>(sp =>
    sp.GetRequiredService<PaOpenDataFacilitySource>());

builder.Services.AddSingleton<PhillyRosterCrawler>();
builder.Services.AddSingleton<IngestService>();

// CLI mode: `dotnet run -- <command>` runs one job and exits. With no command,
// it runs as the always-on scheduled worker.
//   seed-passfail   → ingest the statewide open-data pass/fail tier
//   crawl-philly    → full ZIP-subdivision roster crawl + scoreable reports
//   crawl-zip 19120 → crawl a single ZIP (fast test)
//   migrate         → apply EF migrations + enable PostGIS, then exit
var command = args.FirstOrDefault();

if (string.IsNullOrWhiteSpace(command))
{
    builder.Services.AddHostedService<ScraperWorker>();
    builder.Build().Run();
    return;
}

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var sp = scope.ServiceProvider;
var ingest = sp.GetRequiredService<IngestService>();
var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("cli");

try
{
    switch (command)
    {
        case "migrate":
            await ingest.MigrateAsync(CancellationToken.None);
            break;
        case "seed-passfail":
            await ingest.SeedPassFailAsync(CancellationToken.None);
            break;
        case "crawl-philly":
            await ingest.CrawlPhiladelphiaAsync(null, CancellationToken.None);
            break;
        case "crawl-zip":
            var zip = args.ElementAtOrDefault(1)
                ?? throw new ArgumentException("usage: crawl-zip <zipcode>");
            await ingest.CrawlPhiladelphiaAsync(zip, CancellationToken.None);
            break;
        default:
            log.LogError("Unknown command '{Command}'. Try: migrate | seed-passfail | crawl-philly | crawl-zip <zip>", command);
            Environment.ExitCode = 2;
            break;
    }
}
catch (Exception ex)
{
    log.LogError(ex, "Command '{Command}' failed", command);
    Environment.ExitCode = 1;
}
