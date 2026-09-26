using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Clean4ork.Core.Domain;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Clean4ork.Scraper.Sources;

/// <summary>
/// Statewide facility index backed by Pennsylvania's open-data portal
/// (Socrata / Tyler Data &amp; Insights) rather than by scraping.
///
/// Dataset: data.pa.gov resource "etb6-jzdg" — "Public Food Inspections last
/// 24 months County Agriculture", public domain. This is an authorized JSON
/// API (SODA), so this class is an API client, not a scraper.
///
///   Base:   https://data.pa.gov/resource/etb6-jzdg.json
///   Paging: $limit + $offset, ordered by the system :id for stable paging.
///   Filter: $where / $select supported (e.g. county_name, program_group_type).
///   Token:  an optional Socrata app token (X-App-Token header) raises the
///           anonymous rate limit; without it, throttling is stricter but the
///           data is identical.
///
/// SCOPE: facility-level only — see <see cref="PaFacilityRecord"/> for why this
/// cannot feed the scorer. It seeds the statewide establishment skeleton and
/// geocodes it (lat/long are included), which also satisfies the PostGIS
/// Location population step for free.
///
/// OVERLAP: this dataset includes facilities in independent-county
/// jurisdictions too (Philadelphia's own facilities appear here). The ingest
/// pipeline must treat this as DISCOVERY ONLY and never downgrade an
/// establishment that already carries scoreable data from a depth source.
///
/// FIELD NAMES: the SODA API field names below are the conventional
/// lower_snake_case forms of the dataset's human column labels. They are
/// unverified against the live API metadata endpoint
/// (https://data.pa.gov/api/views/etb6-jzdg/columns.json) — confirm and adjust
/// the constants in <see cref="Fields"/> before the first live run. Parsing is
/// deliberately defensive (missing fields are tolerated) so a name mismatch
/// degrades a field rather than throwing.
/// </summary>
public sealed class PaOpenDataFacilitySource(
    HttpClient httpClient,
    ILogger<PaOpenDataFacilitySource> logger) : IFacilityIndexSource
{
    private const string ResourceUrl = "https://data.pa.gov/resource/etb6-jzdg.json";
    private const int PageSize = 5000; // SODA allows up to 50000; 5000 is polite.

    /// <summary>Counties inspected by an independent health department. For these,
    /// the open-data row is discovery-only; scoreable depth comes from that
    /// jurisdiction's own source (HealthSpace, county app, etc.).</summary>
    private static readonly HashSet<string> IndependentCounties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allegheny", "Bucks", "Delaware", "Erie", "Montgomery", "Philadelphia"
    };

    private static class Fields
    {
        public const string Organization = "organization_name";
        public const string Active = "active_indicator";
        public const string FacilityName = "public_facility_name";
        public const string ProgramGroupType = "program_group_type";
        public const string Address = "address";
        public const string City = "city";
        public const string County = "county_name";
        public const string PostalCode = "zip_code";
        public const string State = "state";
        public const string InspectionDate = "inspection_date";
        public const string InspectionReason = "inspection_reason_type";
        public const string OverallCompliance = "overall_compliance";
        // Socrata Point column: GeoJSON { "type":"Point", "coordinates":[lon,lat] }.
        public const string Location = "georeferenced_latitude_and_longitude";
    }

    public string SourceName => "pa-open-data";

    public async IAsyncEnumerable<Establishment> EnumerateEstablishmentsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var record in EnumerateRecordsAsync(cancellationToken))
        {
            // Skip non-food program types and facilities with no usable name.
            if (record.ProgramGroupType is { } pg &&
                !pg.Equals("Food", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(record.FacilityName))
                continue;

            yield return ToEstablishment(record);
        }
    }

    /// <summary>Lower-level enumeration yielding the raw parsed records.</summary>
    public async IAsyncEnumerable<PaFacilityRecord> EnumerateRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var offset = 0;

        while (true)
        {
            var url = $"{ResourceUrl}?$limit={PageSize}&$offset={offset}&$order=:id";
            logger.LogInformation("Fetching PA open-data facilities (offset={Offset})", offset);

            await using var stream = await httpClient.GetStreamAsync(url, cancellationToken);
            var rows = await JsonSerializer.DeserializeAsync<List<JsonElement>>(
                stream, cancellationToken: cancellationToken) ?? [];

            if (rows.Count == 0)
                yield break;

            foreach (var row in rows)
                yield return MapRecord(row);

            if (rows.Count < PageSize)
                yield break;

            offset += PageSize;
        }
    }

    private static PaFacilityRecord MapRecord(JsonElement row)
    {
        var (lat, lon) = ReadPoint(row, Fields.Location);

        return new PaFacilityRecord
        {
            OrganizationName = GetString(row, Fields.Organization),
            Active = GetYesNo(row, Fields.Active),
            FacilityName = GetString(row, Fields.FacilityName) ?? string.Empty,
            ProgramGroupType = GetString(row, Fields.ProgramGroupType),
            Address = GetString(row, Fields.Address),
            City = GetString(row, Fields.City),
            County = GetString(row, Fields.County),
            PostalCode = GetString(row, Fields.PostalCode),
            State = GetString(row, Fields.State),
            LatestInspectionDate = GetDate(row, Fields.InspectionDate),
            LatestInspectionReason = GetString(row, Fields.InspectionReason),
            OverallCompliance = GetYesNo(row, Fields.OverallCompliance),
            Latitude = lat,
            Longitude = lon
        };
    }

    private Establishment ToEstablishment(PaFacilityRecord r)
    {
        var jurisdiction = JurisdictionFor(r.County);
        var facilityKey = SyntheticFacilityId(r);

        var establishment = new Establishment
        {
            Jurisdiction = jurisdiction,
            SourceFacilityId = facilityKey,
            Name = r.FacilityName,
            Slug = Slugify(r.FacilityName, r.PostalCode),
            AddressLine1 = r.Address,
            City = r.City,
            State = r.State,
            PostalCode = r.PostalCode,
            BusinessEntityName = r.OrganizationName,

            // This source is facility-level only: pass/fail tier, never scored.
            DataTier = DataTier.PassFail,
            SnapshotInspectionDate = r.LatestInspectionDate,
            SnapshotPassed = r.OverallCompliance,
            SnapshotInspectionReason = r.LatestInspectionReason
        };

        if (r is { Latitude: { } lat, Longitude: { } lon })
        {
            // SRID 4326 = WGS84 lon/lat, matching the EF Core mapping.
            establishment.Location = new Point(lon, lat) { SRID = 4326 };
        }

        return establishment;
    }

    /// <summary>
    /// Jurisdiction slug. Independent counties keep their own slug so their
    /// depth source owns them; everything else rolls up to the Ag department.
    /// </summary>
    private static string JurisdictionFor(string? county)
    {
        if (string.IsNullOrWhiteSpace(county))
            return "pa-agriculture";

        if (IndependentCounties.Contains(county))
        {
            // e.g. Philadelphia -> "philadelphia-pa" to line up with the
            // HealthSpace source's jurisdiction key; others -> "<county>-pa".
            return county.Equals("Philadelphia", StringComparison.OrdinalIgnoreCase)
                ? "philadelphia-pa"
                : $"{Slug(county)}-pa";
        }

        return "pa-agriculture";
    }

    /// <summary>
    /// The dataset has no stable facility GUID, so synthesize a deterministic
    /// key from county + name + address. Stable across refreshes as long as
    /// those fields are stable; good enough for upsert dedup.
    /// </summary>
    private static string SyntheticFacilityId(PaFacilityRecord r)
    {
        var basis = string.Join('|',
            (r.County ?? "").Trim().ToLowerInvariant(),
            (r.FacilityName ?? "").Trim().ToLowerInvariant(),
            (r.Address ?? "").Trim().ToLowerInvariant());

        // Short, stable, URL/DB-safe hash of the basis string.
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(basis));
        return "padata-" + Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private static string Slugify(string name, string? postalCode)
    {
        var baseSlug = Slug(name);
        return string.IsNullOrWhiteSpace(postalCode) ? baseSlug : $"{baseSlug}-{postalCode.Trim()}";
    }

    private static string Slug(string value)
    {
        var lowered = value.Trim().ToLowerInvariant();
        var chars = lowered.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("--"))
            collapsed = collapsed.Replace("--", "-");
        return collapsed.Trim('-');
    }

    // ---- defensive JSON readers ---------------------------------------

    private static string? GetString(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? GetYesNo(JsonElement row, string field)
    {
        var s = GetString(row, field);
        if (s is null) return null;
        if (s.Equals("Yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("No", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static DateOnly? GetDate(JsonElement row, string field)
    {
        var s = GetString(row, field);
        if (string.IsNullOrWhiteSpace(s)) return null;

        // Socrata floating-timestamp: "2020-01-08T00:00:00.000"; also handle MM/dd/yyyy.
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
            return DateOnly.FromDateTime(dt);

        if (DateOnly.TryParseExact(s, "MM/dd/yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            return d;

        return null;
    }

    /// <summary>Read a Socrata GeoJSON Point column → (lat, lon).</summary>
    private static (double? lat, double? lon) ReadPoint(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var point) ||
            point.ValueKind != JsonValueKind.Object)
            return (null, null);

        if (!point.TryGetProperty("coordinates", out var coords) ||
            coords.ValueKind != JsonValueKind.Array ||
            coords.GetArrayLength() < 2)
            return (null, null);

        // GeoJSON order is [longitude, latitude].
        var lon = coords[0].GetDouble();
        var lat = coords[1].GetDouble();
        return (lat, lon);
    }
}
