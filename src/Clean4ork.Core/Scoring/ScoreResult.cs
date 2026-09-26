namespace Clean4ork.Core.Scoring;

/// <summary>
/// A single contributor to a score, surfaced verbatim on the methodology
/// page and on the operator dashboard so every point is explainable.
/// </summary>
public record ScoreFactor(
    string ViolationCode,
    string Description,
    decimal PointsLost,
    DateOnly InspectionDate,
    bool IsRepeat);

public record ScoreResult(
    int Score,
    char Grade,
    DateOnly AsOf,
    int InspectionsConsidered,
    IReadOnlyList<ScoreFactor> Factors)
{
    /// <summary>Used when there isn't enough data to score.</summary>
    public static ScoreResult Unscored(DateOnly asOf) =>
        new(Score: 0, Grade: '?', AsOf: asOf, InspectionsConsidered: 0, Factors: []);
}
