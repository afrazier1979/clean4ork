using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
    /// <summary>Prefix of the synthetic IDs the PA open-data source assigns.</summary>
    private const string OpenDataIdPrefix = "padata-";

    /// <summary>Apply EF migrations and ensure PostGIS is enabled. Safe to re-run.</summary>
    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        logger.LogInformation("Applying migrations...");
        await db.Database.MigrateAsync(ct);
        // PostGIS extension is declared in the model (HasPostgresExtension) and
        // created by the migration; this is a belt-and-suspenders no-op if present.
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS postgis;", ct);
        logger.LogInformation("Migrations applied.");
    }

    /// <summary>
    /// Seed the statewide pass/fail tier from the open-data API. No scraping,
    /// no parser, just an API pull. Never downgrades a Scored establishment.
    ///
    /// Philadelphia is skipped entirely: the HealthSpace crawl is the complete,
    /// authoritative source there, and the two sources use different facility
    /// IDs, so loading both would list every Philly restaurant twice.
    /// </summary>
    public async Task SeedPassFailAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The open-data feed lists a facility once per inspection, so the same
        // restaurant can appear many times. Track rows touched since the last save
        // so a repeat updates the pending row instead of inserting a duplicate.
        var pending = new Dictionary<(string, string), Establishment>();

        var n = 0;
        var added = 0;
        var skippedPhilly = 0;
        await foreach (var incoming in facilityIndex.EnumerateEstablishmentsAsync(ct))
        {
            if (incoming.Jurisdiction == philly.Jurisdiction)
            {
                skippedPhilly++;
                continue;
            }

            // Name + ZIP is not unique (two Subways in one ZIP), so add a short,
            // stable suffix from the synthetic facility id to keep slugs unique.
            var id = incoming.SourceFacilityId;
            incoming.Slug = $"{incoming.Slug}-{id[^8..]}";

            var key = (incoming.Jurisdiction, id);

            if (pending.TryGetValue(key, out var queued))
            {
                RefreshSnapshot(queued, incoming);
            }
            else
            {
                var existing = await db.Establishments.FirstOrDefaultAsync(e =>
                    e.Jurisdiction == incoming.Jurisdiction &&
                    e.SourceFacilityId == id, ct);

                if (existing is null)
                {
                    db.Establishments.Add(incoming); // already PassFail
                    pending[key] = incoming;
                    added++;
                }
                else if (existing.DataTier != DataTier.Scored)
                {
                    // never touch a Scored row
                    RefreshSnapshot(existing, incoming);
                    pending[key] = existing;
                }
            }

            if (++n % 500 == 0)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                pending.Clear();
                logger.LogInformation("...{N} rows processed ({Added} new restaurants)", n, added);
            }
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Pass/fail seed complete: {N} rows processed, {Added} new restaurants, {Skipped} Philadelphia rows skipped",
            n, added, skippedPhilly);
    }

    private static void RefreshSnapshot(Establishment target, Establishment incoming)
    {
        target.Location ??= incoming.Location;

        // Only move the snapshot forward in time; an older row never overwrites a newer one.
        if (target.SnapshotInspectionDate is { } current &&
            (incoming.SnapshotInspectionDate is null || incoming.SnapshotInspectionDate <= current))
            return;

        target.SnapshotInspectionDate = incoming.SnapshotInspectionDate;
        target.SnapshotPassed = incoming.SnapshotPassed;
        target.SnapshotInspectionReason = incoming.SnapshotInspectionReason;
        target.LastScrapedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Crawl Philadelphia (all ZIPs, or one if <paramref name="singleZip"/> is
    /// set), pulling full scoreable reports for each facility. Promotes rows to
    /// Scored. This is the depth tier, the differentiator.
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

        // Only replace the state-feed Philadelphia rows once the city source has
        // actually returned a roster, so a failed crawl never empties Philadelphia.
        if (roster.Count == 0)
        {
            logger.LogWarning("Roster came back empty; leaving existing Philadelphia rows untouched.");
            return;
        }
        await RemoveOpenDataPhillyRowsAsync(ct);

        logger.LogInformation("Roster: {Count} facilities. Fetching reports...", roster.Count);

        var processed = 0;
        var failed = 0;
        foreach (var facility in roster)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await IngestFacilityAsync(facility, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex, "Failed to ingest facility {Id} ({Name})",
                    facility.FacilityId, facility.Name);
            }

            if (++processed % 25 == 0)
                logger.LogInformation("...{Done}/{Total} facilities ({Failed} failed)",
                    processed, roster.Count, failed);

            // Politeness: a small pause between facilities. Slow is fine.
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
        }

        logger.LogInformation("Philadelphia crawl complete. {Failed} failed. {Report}", failed, report);
    }

    /// <summary>
    /// One-time cleanup, safe to re-run: deletes Philadelphia rows that came
    /// from the state open-data feed. HealthSpace replaces them with complete,
    /// scoreable records under the city's own facility IDs.
    /// </summary>
    private async Task RemoveOpenDataPhillyRowsAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var removed = await db.Establishments
            .Where(e => e.Jurisdiction == philly.Jurisdiction &&
                        e.SourceFacilityId.StartsWith(OpenDataIdPrefix))
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
            logger.LogInformation(
                "Removed {Count} Philadelphia rows from the state feed (HealthSpace replaces them)", removed);
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

            // Name + ZIP is not unique (two Subways in one ZIP). Add a short,
            // stable suffix from the city's facility ID so every slug is unique.
            fetched.Slug = $"{fetched.Slug}-{ShortId(facility.FacilityId)}";

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

    // ---------------------------------------------------------------------
    // PA Department of Agriculture CSV import
    // ---------------------------------------------------------------------

    private const string PdaJurisdiction = "pa-agriculture";
    private const string PdaIdPrefix = "pda-";

    private static readonly Regex PhoneSuffix =
        new(@"\s+-\s*Phone:.*$", RegexOptions.Compiled);

    // "<street>  <city> PA <zip>": the LAST run of 2+ spaces separates street from city.
    private static readonly Regex AddressPart =
        new(@"^(?<street>.*\S)\s{2,}(?<city>\S.*?)\s+PA\s+(?<zip>\d{4,5})(?:-\d{4})?$", RegexOptions.Compiled);

    private sealed record PdaRow(
        string Name, string Street, string City, string Zip,
        DateOnly Date, string? InspectionType, bool? Passed);

    /// <summary>
    /// Import the CSV exported from PDA's public inspection search
    /// (pafoodsafety.pa.gov). One row per facility: its most recent inspection
    /// date, type, and In/Out compliance. No violation detail, so these stay
    /// PassFail. Safe to re-run with a newer export.
    ///
    /// Matching: a restaurant already in the database (from the older state
    /// open-data feed, or a previous import) is matched on name + street + ZIP
    /// and updated in place, keeping its map location. Everything else is added.
    /// Philadelphia (ZIP 191xx) is skipped; the city source owns it. Scored rows
    /// are never touched.
    /// </summary>
    public async Task ImportPdaCsvAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"CSV not found: {path}", path);

        // 1. Parse the CSV, keeping the latest row per facility.
        var rows = new Dictionary<string, PdaRow>();
        int total = 0, unparsed = 0, skippedPhilly = 0;

        using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
        {
            var header = SplitCsvLine(await reader.ReadLineAsync(ct) ?? "")
                .Select(h => h.Trim()).ToList();
            int iName = header.IndexOf("Name / Address");
            int iDate = header.IndexOf("Most Recent Inspection");
            int iType = header.IndexOf("Inspection Type");
            int iComp = header.IndexOf("Compliance");
            if (iName < 0 || iDate < 0 || iComp < 0)
                throw new InvalidDataException(
                    "Unexpected CSV header. Expected 'Name / Address', 'Most Recent Inspection', 'Compliance'.");

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                total++;
                var f = SplitCsvLine(line);
                string Field(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";

                var parts = ParseNameAddress(Field(iName));
                if (parts is null) { unparsed++; continue; }
                var (name, street, city, zip) = parts.Value;

                if (zip.StartsWith("191", StringComparison.Ordinal)) { skippedPhilly++; continue; }

                if (!DateTime.TryParse(Field(iDate), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dt)) { unparsed++; continue; }

                bool? passed = Field(iComp).ToUpperInvariant() switch
                {
                    "IN" => true,
                    "OUT" => false,
                    _ => null
                };
                var type = Field(iType);

                var row = new PdaRow(name, street, city, zip, DateOnly.FromDateTime(dt),
                    type.Length == 0 ? null : type, passed);

                var key = MatchKey(name, street, zip);
                if (!rows.TryGetValue(key, out var seen) || row.Date > seen.Date)
                    rows[key] = row;
            }
        }

        logger.LogInformation(
            "Parsed {Total} CSV rows: {Unique} facilities, {Philly} Philadelphia rows skipped, {Bad} unparseable",
            total, rows.Count, skippedPhilly, unparsed);

        // 2. Index existing pass/fail restaurants outside Philadelphia: a light,
        //    untracked read of just the matching fields (no map geometry).
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.Establishments
            .AsNoTracking()
            .Where(e => e.Jurisdiction != philly.Jurisdiction && e.DataTier != DataTier.Scored)
            .Select(e => new { e.Id, e.Name, e.AddressLine1, e.PostalCode })
            .ToListAsync(ct);

        var index = existing
            .GroupBy(e => MatchKey(e.Name, e.AddressLine1, e.PostalCode))
            .ToDictionary(g => g.Key, g => g.First().Id);

        logger.LogInformation("Loaded {Count} existing pass/fail restaurants to match against", existing.Count);

        var matches = rows.Where(kv => index.ContainsKey(kv.Key))
            .Select(kv => (Id: index[kv.Key], Row: kv.Value))
            .ToList();
        var newRows = rows.Where(kv => !index.ContainsKey(kv.Key)).ToList();

        logger.LogInformation("{Matched} restaurants to update, {New} to add", matches.Count, newRows.Count);

        // 3. Save in small batches, the same way the state seed did successfully:
        //    500 per save, and EF's change tracker cleared after each one.
        //    If a batch is rejected, retry it one row at a time and skip only the bad rows.
        //    Re-running is safe: rows saved by an earlier run are matched, not duplicated.
        const int batchSize = 500;
        int updated = 0, added = 0, skipped = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var chunk in matches.Chunk(batchSize))
        {
            var ids = chunk.Select(c => c.Id).ToList();
            var entities = await db.Establishments.Where(e => ids.Contains(e.Id)).ToListAsync(ct);
            var byId = entities.ToDictionary(e => e.Id);

            foreach (var (id, row) in chunk)
            {
                if (!byId.TryGetValue(id, out var e)) continue;
                e.SnapshotInspectionDate = row.Date;
                e.SnapshotPassed = row.Passed;
                e.SnapshotInspectionReason = row.InspectionType;
                e.LastScrapedAt = now;
            }

            var (ok, bad) = await SaveBatchAsync(db, entities, ct);
            updated += ok;
            skipped += bad;
            logger.LogInformation("...updated {Done} of {Total}", updated + skipped, matches.Count);
        }

        foreach (var chunk in newRows.Chunk(batchSize))
        {
            var batch = new List<Establishment>(chunk.Length);
            foreach (var (key, row) in chunk)
            {
                var id = PdaIdPrefix + ShortHash(key);
                var e = new Establishment
                {
                    Jurisdiction = PdaJurisdiction,
                    SourceFacilityId = id,
                    Name = Clean(row.Name),
                    Slug = $"{SlugPart(row.Name)}-{row.Zip}-{id[^8..]}",
                    AddressLine1 = Clean(row.Street),
                    City = Clean(row.City),
                    State = "PA",
                    PostalCode = row.Zip,
                    DataTier = DataTier.PassFail,
                    SnapshotInspectionDate = row.Date,
                    SnapshotPassed = row.Passed,
                    SnapshotInspectionReason = row.InspectionType,
                    LastScrapedAt = now
                };
                db.Establishments.Add(e);
                batch.Add(e);
            }

            var (ok, bad) = await SaveBatchAsync(db, batch, ct);
            added += ok;
            skipped += bad;
            logger.LogInformation("...added {Done} of {Total}", added, newRows.Count);
        }

        logger.LogInformation(
            "PDA import complete: {Updated} updated, {Added} added, {Skipped} skipped, {Stale} older records not in this export (left as archive)",
            updated, added, skipped, existing.Count - updated);
    }

    /// <summary>
    /// Save one batch. If the database rejects it, fall back to saving each row on
    /// its own so a single bad row can't block the rest; bad rows are logged and skipped.
    /// Always clears the change tracker afterwards so memory stays flat.
    /// </summary>
    private async Task<(int Saved, int Skipped)> SaveBatchAsync(
        AppDbContext db, IReadOnlyList<Establishment> batch, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return (batch.Count, 0);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning("Batch of {Count} rejected ({Message}); retrying one row at a time",
                batch.Count, ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        int saved = 0, skipped = 0;
        foreach (var e in batch)
        {
            try
            {
                var exists = await db.Establishments.AsNoTracking().AnyAsync(x => x.Id == e.Id, ct);
                db.Entry(e).State = exists ? EntityState.Modified : EntityState.Added;
                await db.SaveChangesAsync(ct);
                saved++;
            }
            catch (DbUpdateException ex)
            {
                skipped++;
                logger.LogWarning("Skipped '{Name}' at '{Address}' {Zip}: {Message}",
                    e.Name, e.AddressLine1, e.PostalCode, ex.InnerException?.Message ?? ex.Message);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return (saved, skipped);
    }

    /// <summary>Remove characters PostgreSQL text columns reject (NUL) and trim.</summary>
    private static string Clean(string value) => value.Replace("\0", "").Trim();

    /// <summary>
    /// Split "NAME - STREET  CITY PA ZIP - Phone: ..." into its parts. Names and
    /// cities can contain " - " too, so try each " - " and prefer the first split
    /// whose street starts with a house number.
    /// </summary>
    private static (string Name, string Street, string City, string Zip)? ParseNameAddress(string raw)
    {
        var s = PhoneSuffix.Replace(raw, "").Trim();
        (string Name, Match M)? fallback = null;

        for (var i = s.IndexOf(" - ", StringComparison.Ordinal);
             i >= 0;
             i = s.IndexOf(" - ", i + 1, StringComparison.Ordinal))
        {
            var m = AddressPart.Match(s[(i + 3)..].Trim());
            if (!m.Success) continue;

            var name = s[..i].Trim();
            var street = m.Groups["street"].Value.Trim();
            if (street.Length > 0 && char.IsDigit(street[0]))
                return Build(name, m);
            fallback ??= (name, m);
        }

        return fallback is { } f ? Build(f.Name, f.M) : null;

        static (string, string, string, string) Build(string name, Match m) =>
        (
            name,
            Regex.Replace(m.Groups["street"].Value.Trim(), @"\s{2,}", " "),
            m.Groups["city"].Value.Trim(),
            m.Groups["zip"].Value
        );
    }

    /// <summary>Minimal CSV splitter that honours double-quoted fields.</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>Name + street + 5-digit ZIP, uppercased, punctuation and extra spaces removed.</summary>
    private static string MatchKey(string? name, string? street, string? zip)
    {
        var z = (zip ?? "").Trim();
        if (z.Length > 5) z = z[..5];
        return $"{Normalize(name)}|{Normalize(street)}|{z}";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                sb.Append(ch);
                pendingSpace = false;
            }
            else pendingSpace = true;
        }
        return sb.ToString();
    }

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value)), 0, 8).ToLowerInvariant();

    private static string SlugPart(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) && ch < 128 ? ch : '-');
        var slug = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        if (slug.Length > 80) slug = slug[..80].TrimEnd('-');
        return slug.Length == 0 ? "facility" : slug;
    }

    /// <summary>First 8 letters/digits of a facility GUID, lowercased.</summary>
    private static string ShortId(string facilityId) =>
        new string(facilityId.Where(char.IsLetterOrDigit).Take(8).ToArray()).ToLowerInvariant();
}
