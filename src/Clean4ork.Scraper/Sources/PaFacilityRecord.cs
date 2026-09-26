namespace Clean4ork.Scraper.Sources;

/// <summary>
/// One facility row from Pennsylvania's open-data inspection dataset
/// (data.pa.gov resource etb6-jzdg, "Public Food Inspections last 24 months").
///
/// IMPORTANT — what this is and isn't:
///   This dataset is FACILITY-LEVEL, not violation-level. Each row is a single
///   facility carrying its LATEST inspection date, the inspection reason, and a
///   single Overall Compliance (Yes/No) flag. It does NOT contain individual
///   violations, FIRF/GRP line items, violation codes, COS/repeat flags, or
///   inspector observations — i.e. none of the raw material the Clean4ork
///   scorer consumes. Many rows have blank inspection fields entirely.
///
///   Therefore this source populates the statewide FACILITY INDEX (discovery +
///   geocoding + "last inspected / passed") but cannot produce a real
///   Clean4ork Score. Scoreable depth still requires the per-jurisdiction
///   report sources (HealthSpace for Philadelphia/Bucks, pafoodsafety.pa.gov
///   report pages for the 61 Ag-covered counties, etc.).
/// </summary>
public sealed record PaFacilityRecord
{
    /// <summary>Inspecting organization, e.g. "City of Philadelphia", "Allentown City".</summary>
    public string? OrganizationName { get; init; }

    /// <summary>"Yes"/"No" — whether the license/facility is currently active.</summary>
    public bool? Active { get; init; }

    public required string FacilityName { get; init; }

    /// <summary>e.g. "Food". Present so non-restaurant program types can be filtered.</summary>
    public string? ProgramGroupType { get; init; }

    public string? Address { get; init; }
    public string? City { get; init; }
    public string? County { get; init; }
    public string? PostalCode { get; init; }
    public string? State { get; init; }

    /// <summary>Date of the latest inspection, when present.</summary>
    public DateOnly? LatestInspectionDate { get; init; }

    /// <summary>e.g. "Regular", "Complaint". The reason for the latest inspection.</summary>
    public string? LatestInspectionReason { get; init; }

    /// <summary>"Yes"/"No" overall pass-fail for the latest inspection, when present.</summary>
    public bool? OverallCompliance { get; init; }

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
}
