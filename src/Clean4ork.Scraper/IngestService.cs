using Clean4ork.Core.Domain;
using Clean4ork.Data;
using Clean4ork.Scraper.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Clean4ork.Scraper;

/// <summary>
/// The jobs the CLI can run. One place that owns writing to the database, so
/// the tier rules (Scored beats PassFail, never downgrade) live in a single
/// auditable spot.
/// </summary>
public sealed class IngestService(
    IServiceScopeFactory scopeFactory,
    PhillyInspectionSource philly,
    PhillyRosterCrawler crawler,
    IFacilityIndexSource facilityIndex,
    ILogger<IngestService> logger)
{
    /// <summary>Apply EF migrations and ensure PostGIS is enabled. Safe to re-run.</summary>
    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        logger.LogInformation("Applying migrations…");
        await db.Database.MigrateAsync(ct);
        // PostGIS extension is declared in the model (HasPostgresExtension) and
        // created by the migration; this is a belt-and-suspenders no-op if present.
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS postgis;", ct);
        logger.LogInformation("Migrations applied.");
    }

    /// <summary>
    /// Seed the statewide pass/fail tier from the open-data API. No scraping,
    /// no parser — just an API pull. Never downgrades a Scored establishment.
    /// </summary>
    public async Task SeedPassFailAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var n = 0;
        await foreach (var incoming in facilityIndex.EnumerateEstablishmentsAsync(ct))
        {
            var existing = await db.Establishments.FirstOrDefaultAsync(e =>
                e.Jurisdiction == incoming.Jurisdiction &&
                e.SourceFacilityId == incoming.SourceFacilityId, ct);

            if (existing is null)
            {
                db.Establishments.Add(incoming); // already PassFail
            }
            else if (existing.DataTier != DataTier.Scored)
            {
                // refresh snapshot in place; never touch a Scored row
                existing.SnapshotInspectionDate = incoming.SnapshotInspectionDate;
                existing.SnapshotPassed = incoming.SnapshotPassed;
                existing.SnapshotInspectionReason = incoming.SnapshotInspectionReason;
                existing.Location ??= incoming.Location;
                existing.LastScrapedAt = DateTimeOffset.UtcNow;
            }

            if (++n % 500 == 0) await db.SaveChangesAsync(ct);
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Pass/fail seed complete: {N} rows processed", n);
    }

    /// <summary>
    /// Crawl Philadelphia (all ZIPs, or one if <paramref name="singleZip"/> is
    /// set), pulling full scoreable reports for each facility. Promotes rows to
    /// Scored. This is the depth tier — the differentiator.
    /// </summary>
    public async Task CrawlPhiladelphiaAsync(string? singleZip, CancellationToken ct)
    {
        var report = new RosterCrawlReport();

        // Roster: either one ZIP (fast test) or the whole city (subdivided).
        var roster = new List<FacilitySearchResult>();
        if (singleZip is not null)
        {
            logger.LogInformation("Crawling single ZIP {Zip}", singleZip);
            await foreach (var row in philly.SearchByPostalCodeAsync(singleZip, ct))
                roster.Add(row);
            report.ZipsCrawled = 1;
            report.FacilitiesFound = roster.Count;
        }
        else
        {
            logger.LogInformation("Crawling ALL Philadelphia ZIPs with subdivision");
            await foreach (var row in crawler.CrawlAllAsync(report, ct))
                roster.Add(row);
        }

        logger.LogInformation("Roster: {Count} facilities. Fetching reports…", roster.Count);

        var processed = 0;
        foreach (var facility in roster)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await IngestFacilityAsync(facility, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to ingest facility {Id} ({Name})",
                    facility.FacilityId, facility.Name);
            }

            if (++processed % 25 == 0)
                logger.LogInformation("…{Done}/{Total} facilities", processed, roster.Count);

            // Politeness: a small pause between facilities. Slow is fine.
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
        }

        logger.LogInformation("Philadelphia crawl complete. {Report}", report);
    }

    /// <summary>
    /// Upsert one facility: fetch its establishment + inspections, mark Scored,
    /// attach only new inspections, and log NewInspection / GradeChanged events.
    /// </summary>
    private async Task IngestFacilityAsync(FacilitySearchResult facility, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.Establishments
            .Include(e => e.Inspections)
            .FirstOrDefaultAsync(e =>
                e.Jurisdiction == philly.Jurisdiction &&
                e.SourceFacilityId == facility.FacilityId, ct);

        if (existing is null)
        {
            var fetched = await philly.FetchEstablishmentAsync(facility.FacilityId, ct);
            if (fetched is null)
            {
                logger.LogWarning("Could not parse establishment {Id}", facility.FacilityId);
                return;
            }
            fetched.DataTier = DataTier.Scored;
            db.Establishments.Add(fetched);
            db.InspectionEvents.Add(new InspectionEvent
            {
                EstablishmentId = fetched.Id,
                EventType = InspectionEventType.EstablishmentDiscovered
            });
            existing = fetched;
        }
        else
        {
            existing.DataTier = DataTier.Scored; // promote if it was PassFail
        }

        var inspections = await philly.FetchInspectionsAsync(facility.FacilityId, ct);
        var known = existing.Inspections
            .Select(i => i.SourceInspectionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var insp in inspections.Where(i => !known.Contains(i.SourceInspectionId)))
        {
            insp.EstablishmentId = existing.Id;
            db.Inspections.Add(insp);
            db.InspectionEvents.Add(new InspectionEvent
            {
                EstablishmentId = existing.Id,
                InspectionId = insp.Id,
                EventType = InspectionEventType.NewInspection
            });
        }

        existing.LastScrapedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
