using Clean4ork.Core.Domain;
using Clean4ork.Data;
using Clean4ork.Scraper.Sources;
using Microsoft.EntityFrameworkCore;

namespace Clean4ork.Scraper;

/// <summary>
/// Walks the configured ZIP codes for each registered source, upserts
/// establishments and inspections, and records InspectionEvents when
/// something new is found. Runs once per invocation interval.
/// </summary>
public class ScraperWorker(
    IServiceScopeFactory scopeFactory,
    IInspectionSource source,
    IFacilityIndexSource facilityIndex,
    IConfiguration configuration,
    ILogger<ScraperWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var zips = configuration.GetSection("Scraper:PostalCodes").Get<string[]>()
            ?? ["19103"];

        var delay = TimeSpan.FromHours(
            configuration.GetValue("Scraper:IntervalHours", 24));

        var indexEnabled = configuration.GetValue("Scraper:FacilityIndexEnabled", true);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Breadth first: seed/refresh the statewide pass/fail index, then
            // let the depth pass promote the jurisdictions we can score.
            if (indexEnabled)
            {
                try
                {
                    await IngestFacilityIndexAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Facility index ingest failed");
                }
            }

            foreach (var zip in zips)
            {
                try
                {
                    await ScrapePostalCodeAsync(zip, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Scrape failed for ZIP {Zip}", zip);
                }
            }

            logger.LogInformation("Scrape cycle complete. Sleeping {Delay}.", delay);
            await Task.Delay(delay, stoppingToken);
        }
    }

    /// <summary>
    /// Upsert the statewide facility index (pass/fail tier). The cardinal rule:
    /// NEVER downgrade a Scored establishment. An index row may create a new
    /// PassFail establishment or refresh an existing PassFail one, but it must
    /// leave Scored establishments' tier and snapshot untouched — depth data
    /// always wins.
    /// </summary>
    private async Task IngestFacilityIndexAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var batch = 0;
        await foreach (var incoming in facilityIndex.EnumerateEstablishmentsAsync(ct))
        {
            var existing = await db.Establishments.FirstOrDefaultAsync(e =>
                e.Jurisdiction == incoming.Jurisdiction &&
                e.SourceFacilityId == incoming.SourceFacilityId, ct);

            if (existing is null)
            {
                db.Establishments.Add(incoming); // already tagged PassFail by the source
                db.InspectionEvents.Add(new InspectionEvent
                {
                    EstablishmentId = incoming.Id,
                    EventType = InspectionEventType.EstablishmentDiscovered
                });
            }
            else if (existing.DataTier == DataTier.Scored)
            {
                // Never downgrade. Refresh only non-authoritative contact fields;
                // leave tier and the (irrelevant) snapshot alone.
                existing.Location ??= incoming.Location;
                existing.LastScrapedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                // Refresh the pass/fail snapshot in place.
                existing.DataTier = DataTier.PassFail;
                existing.Name = incoming.Name;
                existing.AddressLine1 = incoming.AddressLine1;
                existing.City = incoming.City;
                existing.State = incoming.State;
                existing.PostalCode = incoming.PostalCode;
                existing.Location = incoming.Location ?? existing.Location;
                existing.SnapshotInspectionDate = incoming.SnapshotInspectionDate;
                existing.SnapshotPassed = incoming.SnapshotPassed;
                existing.SnapshotInspectionReason = incoming.SnapshotInspectionReason;
                existing.LastScrapedAt = DateTimeOffset.UtcNow;
            }

            if (++batch % 500 == 0)
                await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Facility index ingest complete ({Count} rows)", batch);
    }

    private async Task ScrapePostalCodeAsync(string zip, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await foreach (var facilityId in source.ListFacilityIdsForPostalCodeAsync(zip, ct))
        {
            var existing = await db.Establishments
                .Include(e => e.Inspections)
                .FirstOrDefaultAsync(e =>
                    e.Jurisdiction == source.Jurisdiction &&
                    e.SourceFacilityId == facilityId, ct);

            if (existing is null)
            {
                var fetched = await source.FetchEstablishmentAsync(facilityId, ct);
                if (fetched is null)
                {
                    logger.LogWarning("Could not parse establishment {FacilityId}", facilityId);
                    continue;
                }

                // A depth source yields full violation detail → this is a Scored
                // establishment, regardless of any prior pass/fail index row.
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
                // Depth data always wins: promote to Scored if an index pass had
                // previously created this as PassFail.
                existing.DataTier = DataTier.Scored;
            }

            // Change detection: only fetch/attach inspections we haven't seen.
            var inspections = await source.FetchInspectionsAsync(facilityId, ct);
            var knownIds = existing.Inspections
                .Select(i => i.SourceInspectionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var inspection in inspections.Where(i => !knownIds.Contains(i.SourceInspectionId)))
            {
                inspection.EstablishmentId = existing.Id;
                db.Inspections.Add(inspection);
                db.InspectionEvents.Add(new InspectionEvent
                {
                    EstablishmentId = existing.Id,
                    InspectionId = inspection.Id,
                    EventType = InspectionEventType.NewInspection
                });
            }

            existing.LastScrapedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }
}
