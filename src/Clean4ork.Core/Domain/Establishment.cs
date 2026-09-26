using NetTopologySuite.Geometries;

namespace Clean4ork.Core.Domain;

/// <summary>
/// A food establishment as known to a jurisdiction's inspection system.
/// One row per (jurisdiction, source facility id).
/// </summary>
public class Establishment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Jurisdiction key, e.g. "philadelphia-pa". Matches IInspectionSource.Jurisdiction.</summary>
    public required string Jurisdiction { get; set; }

    /// <summary>The facilityID GUID used by the HealthSpace portal.</summary>
    public required string SourceFacilityId { get; set; }

    public required string Name { get; set; }

    /// <summary>URL slug, e.g. "pats-king-of-steaks-19147". Unique per jurisdiction.</summary>
    public required string Slug { get; set; }

    public string? AddressLine1 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Phone { get; set; }

    /// <summary>Facility type as reported by the source (e.g. "Food Preparation Serving").</summary>
    public string? FacilityType { get; set; }

    /// <summary>Business entity / corporate officer info from the full report, when available.</summary>
    public string? BusinessEntityName { get; set; }

    /// <summary>Geocoded location (PostGIS point, SRID 4326). Populated by a later geocoding pass.</summary>
    public Point? Location { get; set; }

    /// <summary>Set when an operator claims this listing.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>
    /// How much data backs this establishment, controlling how its page renders.
    /// Set at ingest: Scored for full-detail jurisdictions, PassFail for the
    /// open-data index, Unknown when there's nothing usable yet.
    /// </summary>
    public DataTier DataTier { get; set; } = DataTier.Unknown;

    // --- Pass/Fail snapshot (only meaningful when DataTier == PassFail) ------
    // These come from the facility-index source, which carries a single overall
    // compliance flag and a date rather than scoreable violations.

    /// <summary>Date of the latest inspection per the pass/fail source.</summary>
    public DateOnly? SnapshotInspectionDate { get; set; }

    /// <summary>Latest overall compliance: true = passed, false = failed, null = unknown.</summary>
    public bool? SnapshotPassed { get; set; }

    /// <summary>Reason for the latest inspection, e.g. "Regular", "Complaint".</summary>
    public string? SnapshotInspectionReason { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastScrapedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Inspection> Inspections { get; set; } = [];
}
