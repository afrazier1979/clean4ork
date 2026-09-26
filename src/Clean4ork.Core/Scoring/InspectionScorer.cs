using Clean4ork.Core.Domain;

namespace Clean4ork.Core.Scoring;

/// <summary>
/// Default scoring algorithm.
///
/// Design goals:
///   1. Fully deterministic and auditable — no ML, no hidden weights.
///   2. Every point of deduction maps to a specific violation on a specific
///      inspection, so the methodology page and operator dashboard can show
///      "here's exactly why" for every score.
///   3. Calibrated to the FDA Food Code's existing severity split (FIRF vs GRP)
///      rather than inventing new categories.
///   4. Penalises systemic problems (repeats, out-of-compliance) more than
///      one-off issues (corrected on site).
///   5. Fades older inspections gradually rather than cliff-edging them.
///
/// The weights below are a defensible starting point. Calibrate against the
/// real Philly distribution once we have 90 days of scrape data — the
/// thresholds should yield roughly:
///   ~30% A, ~35% B, ~20% C, ~10% D, ~5% F.
/// </summary>
public class InspectionScorer : IInspectionScorer
{
    // Base point deductions per violation (from a starting score of 100).
    private const decimal FoodborneIllnessRiskWeight = 8m;
    private const decimal GoodRetailPracticeWeight = 2m;

    // Compliance status multipliers.
    private const decimal OutOfComplianceMultiplier = 1.5m;
    private const decimal CorrectedOnSiteMultiplier = 0.5m;
    private const decimal RepeatStatusMultiplier = 2.0m;

    // Multiplier when the same violation code appears on an earlier inspection
    // within the repeat lookback window. Stacks on top of compliance multiplier.
    private const decimal RecurrenceMultiplier = 1.75m;
    private static readonly int RepeatLookbackDays = 365;

    // Inspections older than this don't contribute at all.
    private const int MaxAgeDays = 730;

    // Full weight up to this age; linear decay from here to the floor at MaxAgeDays.
    private const int FullWeightDays = 90;

    // Decay floor: an inspection at the edge of the window still carries 20% weight.
    private const decimal MinDecayFactor = 0.2m;

    // Grade buckets — starting thresholds, to be re-calibrated quarterly
    // against the live Philadelphia distribution.
    private const int GradeAThreshold = 90;
    private const int GradeBThreshold = 80;
    private const int GradeCThreshold = 70;
    private const int GradeDThreshold = 60;

    public ScoreResult Score(IReadOnlyList<Inspection> inspections, DateOnly asOf)
    {
        // Only inspections inside the two-year window contribute.
        var considered = inspections
            .Where(i => AgeInDays(i.InspectionDate, asOf) is >= 0 and <= MaxAgeDays)
            .OrderBy(i => i.InspectionDate)
            .ToList();

        if (considered.Count == 0)
            return ScoreResult.Unscored(asOf);

        var factors = new List<ScoreFactor>();
        var runningScore = 100m;

        foreach (var inspection in considered)
        {
            var decay = DecayFactor(inspection.InspectionDate, asOf);

            foreach (var violation in inspection.Violations)
            {
                var baseWeight = violation.Category switch
                {
                    ViolationCategory.FoodborneIllnessRiskFactor => FoodborneIllnessRiskWeight,
                    ViolationCategory.GoodRetailPractice => GoodRetailPracticeWeight,
                    _ => GoodRetailPracticeWeight
                };

                var statusMultiplier = violation.Status switch
                {
                    ComplianceStatus.CorrectedOnSite => CorrectedOnSiteMultiplier,
                    ComplianceStatus.OutOfCompliance => OutOfComplianceMultiplier,
                    ComplianceStatus.Repeat => RepeatStatusMultiplier,
                    _ => 1m
                };

                // Recurrence: the same code on an earlier inspection within the
                // lookback window signals a systemic problem, not a bad day.
                var isRecurrence = IsRecurrence(considered, inspection, violation);
                var recurrenceMultiplier = isRecurrence ? RecurrenceMultiplier : 1m;

                var pointsLost = Math.Round(
                    baseWeight * statusMultiplier * recurrenceMultiplier * decay, 2);

                if (pointsLost <= 0)
                    continue;

                runningScore -= pointsLost;

                factors.Add(new ScoreFactor(
                    ViolationCode: violation.Code,
                    Description: violation.Description,
                    PointsLost: pointsLost,
                    InspectionDate: inspection.InspectionDate,
                    IsRepeat: isRecurrence || violation.Status == ComplianceStatus.Repeat));
            }
        }

        var score = (int)Math.Round(Math.Clamp(runningScore, 0m, 100m));

        return new ScoreResult(
            Score: score,
            Grade: GradeFor(score),
            AsOf: asOf,
            InspectionsConsidered: considered.Count,
            Factors: factors);
    }

    private static char GradeFor(int score) => score switch
    {
        >= GradeAThreshold => 'A',
        >= GradeBThreshold => 'B',
        >= GradeCThreshold => 'C',
        >= GradeDThreshold => 'D',
        _ => 'F'
    };

    /// <summary>
    /// 1.0 for inspections up to FullWeightDays old, then linear decay down
    /// to MinDecayFactor at MaxAgeDays.
    /// </summary>
    private static decimal DecayFactor(DateOnly inspectionDate, DateOnly asOf)
    {
        var age = AgeInDays(inspectionDate, asOf);

        if (age <= FullWeightDays)
            return 1m;

        if (age >= MaxAgeDays)
            return MinDecayFactor;

        var span = (decimal)(MaxAgeDays - FullWeightDays);
        var progressed = (decimal)(age - FullWeightDays);
        return 1m - (1m - MinDecayFactor) * (progressed / span);
    }

    private static bool IsRecurrence(
        List<Inspection> allConsidered,
        Inspection current,
        Violation violation)
    {
        return allConsidered
            .Where(i => i.InspectionDate < current.InspectionDate)
            .Where(i => AgeInDays(i.InspectionDate, current.InspectionDate) <= RepeatLookbackDays)
            .SelectMany(i => i.Violations)
            .Any(v => string.Equals(v.Code, violation.Code, StringComparison.OrdinalIgnoreCase));
    }

    private static int AgeInDays(DateOnly from, DateOnly to) =>
        to.DayNumber - from.DayNumber;
}
