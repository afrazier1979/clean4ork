namespace Clean4ork.Core.Domain;

public enum InspectionEventType
{
    NewInspection,
    GradeChanged,
    EstablishmentDiscovered
}

/// <summary>
/// Append-only log of notable changes detected by the scraper.
/// This is what powers alerts to consumers (favorites) and operators
/// (their own restaurant). Never updated — only inserted.
/// </summary>
public class InspectionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EstablishmentId { get; set; }
    public Establishment Establishment { get; set; } = null!;

    public Guid? InspectionId { get; set; }

    public required InspectionEventType EventType { get; set; }

    /// <summary>Letter grade before this event (if applicable).</summary>
    public char? PreviousGrade { get; set; }
    /// <summary>Letter grade after this event (if applicable).</summary>
    public char? NewGrade { get; set; }

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Whether alerts have been dispatched for this event.</summary>
    public DateTimeOffset? AlertedAt { get; set; }
}
