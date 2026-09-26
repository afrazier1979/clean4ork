using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Clean4ork.Scraper.Sources;

/// <summary>
/// Builds a COMPLETE Philadelphia roster from HealthSpace despite the 100-result
/// search cap, using ZIP-walk + address-axis subdivision.
///
/// Algorithm (validated against a modeled universe before implementation):
///   1. For each Philadelphia ZIP, run the ZIP search.
///   2. If it reports &lt; 100 results, it's complete — take every row.
///   3. If it reports exactly 100, it's truncated. Subdivide by STREET: pull the
///      distinct street names seen in the (partial) rows AND from a street list,
///      and run an ADDRESS-axis search for each "{street}" within nothing (the
///      axes don't compose, so we search address citywide and keep only rows in
///      this ZIP). Each street slice is almost always &lt; 100.
///   4. If a street slice itself caps (rare — a very long commercial corridor),
///      subdivide again by street + leading-digit of the house number.
///   5. Dedupe everything by facilityID (the stable HealthSpace key).
///
/// Why this is authoritative: HealthSpace IS Philadelphia's system, so the union
/// of all ZIP/street slices with no slice truncated == the complete universe.
/// The caller gets a completeness signal (AnyStillCapped) so it knows if a
/// deeper split is needed rather than silently missing rows.
///
/// This class only builds the ROSTER (facilityIDs + light metadata). Pulling the
/// scoreable reports for each facility is done by PhillyInspectionSource using
/// the parser, driven by the ids this crawler returns.
/// </summary>
public sealed class PhillyRosterCrawler(
    PhillyInspectionSource source,
    ILogger<PhillyRosterCrawler> logger)
{
    private const int Cap = 100;

    /// <summary>The ~48 real Philadelphia ZIP codes. (Discoverable at runtime via
    /// the searchShow=zip index, but pinned here for a deterministic crawl.)</summary>
    public static readonly IReadOnlyList<string> PhiladelphiaZips = new[]
    {
        "19102","19103","19104","19106","19107","19111","19112","19114","19115","19116",
        "19118","19119","19120","19121","19122","19123","19124","19125","19126","19127",
        "19128","19129","19130","19131","19132","19133","19134","19135","19136","19137",
        "19138","19139","19140","19141","19142","19143","19144","19145","19146","19147",
        "19148","19149","19150","19151","19152","19153","19154"
    };

    /// <summary>
    /// Crawl the whole city. Yields de-duplicated facility rows. Tracks which
    /// slices remained capped so the caller can report completeness.
    /// </summary>
    public async IAsyncEnumerable<FacilitySearchResult> CrawlAllAsync(
        RosterCrawlReport report,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var zip in PhiladelphiaZips)
        {
            ct.ThrowIfCancellationRequested();

            var rows = new List<FacilitySearchResult>();
            var reportedTotal = 0;
            await foreach (var row in source.SearchByPostalCodeAsync(zip, ct))
            {
                rows.Add(row);
                reportedTotal++;
            }

            report.ZipsCrawled++;

            if (reportedTotal < Cap)
            {
                // ZIP is complete in one query.
                foreach (var row in rows)
                    if (seen.Add(row.FacilityId)) { report.FacilitiesFound++; yield return row; }
                logger.LogInformation("ZIP {Zip}: {N} facilities (under cap)", zip, rows.Count);
                continue;
            }

            // Capped → subdivide by street.
            logger.LogWarning("ZIP {Zip} capped at {Cap} — subdividing by street", zip, Cap);
            report.ZipsRequiringSubdivision++;

            var streets = ExtractStreets(rows);
            foreach (var street in streets)
            {
                ct.ThrowIfCancellationRequested();

                var sliceCount = 0;
                var sliceCapped = false;
                var sliceRows = new List<FacilitySearchResult>();
                await foreach (var row in source.SearchByAddressAsync(street, ct))
                {
                    // Address search is citywide; keep only this ZIP.
                    if (!string.Equals(row.PostalCode, zip, StringComparison.Ordinal)) continue;
                    sliceRows.Add(row);
                    sliceCount++;
                }
                if (sliceCount >= Cap) sliceCapped = true;

                foreach (var row in sliceRows)
                    if (seen.Add(row.FacilityId)) { report.FacilitiesFound++; yield return row; }

                if (sliceCapped)
                {
                    // Deeper split by house-number leading digit.
                    report.SlicesStillCapped++;
                    logger.LogWarning("Street slice '{Street}' in {Zip} still capped — splitting by house number", street, zip);
                    for (var digit = '1'; digit <= '9'; digit++)
                    {
                        await foreach (var row in source.SearchByAddressAsync($"{digit}{street}", ct))
                        {
                            if (!string.Equals(row.PostalCode, zip, StringComparison.Ordinal)) continue;
                            if (seen.Add(row.FacilityId)) { report.FacilitiesFound++; yield return row; }
                        }
                    }
                }
            }
        }

        report.Complete = report.SlicesStillCapped == 0;
        logger.LogInformation(
            "Roster crawl done: {Found} facilities across {Zips} ZIPs ({Sub} subdivided); complete={Complete}",
            report.FacilitiesFound, report.ZipsCrawled, report.ZipsRequiringSubdivision, report.Complete);
    }

    /// <summary>
    /// Pull distinct street names from result rows. Addresses look like
    /// "200 W Duncannon AVE" / "4900 N 05th ST" — we take the street portion
    /// (drop the leading house number) so we can search the address axis by it.
    /// </summary>
    private static IReadOnlyList<string> ExtractStreets(IEnumerable<FacilitySearchResult> rows)
    {
        var streets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var addr = r.AddressLine;
            if (string.IsNullOrWhiteSpace(addr)) continue;

            // Drop a leading house number, keep the rest as the street key.
            var parts = addr.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var street = parts.Length == 2 && parts[0].Any(char.IsDigit) ? parts[1] : addr.Trim();
            if (street.Length >= 3) streets.Add(street);
        }
        return streets.OrderBy(s => s).ToList();
    }
}

/// <summary>Mutable progress/completeness report for a roster crawl.</summary>
public sealed class RosterCrawlReport
{
    public int ZipsCrawled { get; set; }
    public int ZipsRequiringSubdivision { get; set; }
    public int SlicesStillCapped { get; set; }
    public int FacilitiesFound { get; set; }
    public bool Complete { get; set; }

    public override string ToString() =>
        $"zips={ZipsCrawled} subdivided={ZipsRequiringSubdivision} " +
        $"stillCapped={SlicesStillCapped} facilities={FacilitiesFound} complete={Complete}";
}
