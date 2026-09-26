using Clean4ork.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddClean4orkData(builder.Configuration);

// Register the scorer so pages can compute grades from inspections.
builder.Services.AddScoped<Clean4ork.Core.Scoring.IInspectionScorer,
    Clean4ork.Core.Scoring.InspectionScorer>();

// Railway/containers provide the port via the PORT env var, and the app must
// bind 0.0.0.0 (not localhost) to be reachable. Honor PORT when present.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// Apply migrations on startup so a fresh Railway database schemas itself.
// (Enable via RUN_MIGRATIONS_ON_STARTUP=true; off by default for safety.)
if (Environment.GetEnvironmentVariable("RUN_MIGRATIONS_ON_STARTUP") == "true")
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS postgis;");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Railway terminates TLS at its edge; forcing HTTPS redirect inside the
// container causes redirect loops. Only redirect in local dev.
if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

app.Run();
