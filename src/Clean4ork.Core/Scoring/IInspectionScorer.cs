using Clean4ork.Core.Domain;

namespace Clean4ork.Core.Scoring;

public interface IInspectionScorer
{
    /// <summary>
    /// Compute a 0–100 score and a letter grade for a restaurant given
    /// its inspection history, evaluated as-of the supplied date.
    /// </summary>
    /// <param name="inspections">All known inspections for the establishment.</param>
    /// <param name="asOf">The date the score is being computed for (usually today).</param>
    ScoreResult Score(IReadOnlyList<Inspection> inspections, DateOnly asOf);
}
