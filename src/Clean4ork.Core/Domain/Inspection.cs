namespace Clean4ork.Core.Domain;

public enum InspectionType
{
    Unknown = 0,
    Initial,
    Routine,
    Reinspection,
    Complaint,
    FollowUp
}

/// <summary>
/// A single inspection event for an establishment, parsed from the
/// full FDA-standard report (_report_full.cfm).
/// </summary>
public class Inspection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EstablishmentId { get; set; }
    public Establishment Establishment { get; set; } = null!;

    /// <summary>The inspectionID used by the HealthSpace portal.</summary>
    public required string SourceInspectionId { get; set; }

    public required DateOnly InspectionDate { get; set; }
    public InspectionType Type { get; set; } = InspectionType.Unknown;

    public string? InspectorName { get; set; }

    /// <summary>Regulatory summary notes from the report footer (license status, etc.).</summary>
    public string? SummaryNotes { get; set; }

    public List<Violation> Violations { get; set; } = [];
}
