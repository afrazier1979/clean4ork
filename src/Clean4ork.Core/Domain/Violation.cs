namespace Clean4ork.Core.Domain;

/// <summary>
/// The FDA Food Code's two severity buckets. FIRF items are the ones that
/// actually make people sick; GRP items are hygiene/maintenance signals.
/// </summary>
public enum ViolationCategory
{
    /// <summary>Foodborne Illness Risk Factor (FDA codes 1–29 region). Weighted heavily.</summary>
    FoodborneIllnessRiskFactor,

    /// <summary>Good Retail Practice. Weighted lightly.</summary>
    GoodRetailPractice
}

public enum ComplianceStatus
{
    /// <summary>Marked out of compliance and not fixed during the visit.</summary>
    OutOfCompliance,

    /// <summary>COS — corrected while the inspector was on site.</summary>
    CorrectedOnSite,

    /// <summary>Flagged as a repeat on the report itself.</summary>
    Repeat
}

/// <summary>
/// A single out-of-compliance item on an inspection. IN / N/A / N/O rows
/// are not stored — only actual violations.
/// </summary>
public class Violation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid InspectionId { get; set; }
    public Inspection Inspection { get; set; } = null!;

    /// <summary>FDA Food Code item number (1–54) or Philadelphia-specific code (55+).</summary>
    public required string Code { get; set; }

    public required string Description { get; set; }

    public required ViolationCategory Category { get; set; }
    public required ComplianceStatus Status { get; set; }

    /// <summary>Ordinance citation when present, e.g. "46.671".</summary>
    public string? OrdinanceCitation { get; set; }

    /// <summary>Inspector's free-text observation for this item.</summary>
    public string? Observation { get; set; }
}
