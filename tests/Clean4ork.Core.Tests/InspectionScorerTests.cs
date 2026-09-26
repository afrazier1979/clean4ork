using Clean4ork.Core.Domain;
using Clean4ork.Core.Scoring;
using FluentAssertions;

namespace Clean4ork.Core.Tests;

public class InspectionScorerTests
{
    private readonly InspectionScorer _scorer = new();
    private static readonly DateOnly Today = new(2026, 5, 20);

    [Fact]
    public void NoInspections_ReturnsUnscored()
    {
        var result = _scorer.Score([], Today);

        result.Grade.Should().Be('?');
        result.InspectionsConsidered.Should().Be(0);
        result.Factors.Should().BeEmpty();
    }

    [Fact]
    public void CleanRecentInspection_ScoresPerfect()
    {
        var inspection = MakeInspection(Today.AddDays(-30));

        var result = _scorer.Score([inspection], Today);

        result.Score.Should().Be(100);
        result.Grade.Should().Be('A');
        result.InspectionsConsidered.Should().Be(1);
    }

    [Fact]
    public void FirfViolation_CostsMoreThanGrpViolation()
    {
        var firf = MakeInspection(Today.AddDays(-30),
            MakeViolation("3", ViolationCategory.FoodborneIllnessRiskFactor, ComplianceStatus.OutOfCompliance));
        var grp = MakeInspection(Today.AddDays(-30),
            MakeViolation("45", ViolationCategory.GoodRetailPractice, ComplianceStatus.OutOfCompliance));

        var firfScore = _scorer.Score([firf], Today).Score;
        var grpScore = _scorer.Score([grp], Today).Score;

        firfScore.Should().BeLessThan(grpScore);
        // 8 * 1.5 = 12 vs 2 * 1.5 = 3
        firfScore.Should().Be(88);
        grpScore.Should().Be(97);
    }

    [Fact]
    public void CorrectedOnSite_CostsLessThanOutOfCompliance()
    {
        var cos = MakeInspection(Today.AddDays(-30),
            MakeViolation("3", ViolationCategory.FoodborneIllnessRiskFactor, ComplianceStatus.CorrectedOnSite));
        var ooc = MakeInspection(Today.AddDays(-30),
            MakeViolation("3", ViolationCategory.FoodborneIllnessRiskFactor, ComplianceStatus.OutOfCompliance));

        _scorer.Score([cos], Today).Score
            .Should().BeGreaterThan(_scorer.Score([ooc], Today).Score);
    }

    [Fact]
    public void RepeatStatus_DoublesTheDeduction()
    {
        var repeat = MakeInspection(Today.AddDays(-30),
            MakeViolation("3", ViolationCategory.FoodborneIllnessRiskFactor, ComplianceStatus.Repeat));

        // 8 * 2.0 = 16 points
        _scorer.Score([repeat], Today).Score.Should().Be(84);
    }

    [Fact]
    public void SameCodeWithinLookback_TriggersRecurrencePenalty()
    {
        var earlier = MakeInspection(Today.AddDays(-200),
            MakeViolation("10", ViolationCategory.GoodRetailPractice, ComplianceStatus.OutOfCompliance));
        var later = MakeInspection(Today.AddDays(-30),
            MakeViolation("10", ViolationCategory.GoodRetailPractice, ComplianceStatus.OutOfCompliance));

        var result = _scorer.Score([earlier, later], Today);

        var laterFactor = result.Factors.Single(f => f.InspectionDate == later.InspectionDate);
        laterFactor.IsRepeat.Should().BeTrue();
        // Later violation: 2 * 1.5 * 1.75 = 5.25 (recurrence stacked)
        laterFactor.PointsLost.Should().Be(5.25m);
    }

    [Fact]
    public void SameCodeOutsideLookback_NoRecurrencePenalty()
    {
        var earlier = MakeInspection(Today.AddDays(-700),
            MakeViolation("10", ViolationCategory.GoodRetailPractice, ComplianceStatus.OutOfCompliance));
        var later = MakeInspection(Today.AddDays(-30),
            MakeViolation("10", ViolationCategory.GoodRetailPractice, ComplianceStatus.OutOfCompliance));

        var result = _scorer.Score([earlier, later], Today);

        var laterFactor = result.Factors.Single(f => f.InspectionDate == later.InspectionDate);
        laterFactor.IsRepeat.Should().BeFalse();
        laterFactor.PointsLost.Should().Be(3m); // 2 * 1.5, no recurrence multiplier
    }

    [Fact]
    public void OlderInspections_DecayInWeight()
    {
        var recent = MakeInspection(Today.AddDays(-30),
            MakeViolation("3", ViolationCategory.FoodborneIllnessRiskFactor, ComplianceStatus.OutOfCompliance));
        var old = MakeInspection(Today.AddDays(-600),
            MakeViolation("3", ViolationCategory.FoodborneIllnessRiskFactor, ComplianceStatus.OutOfCompliance));

        var recentLoss = _scorer.Score([recent], Today).Factors.Single().PointsLost;
        var oldLoss = _scorer.Score([old], Today).Factors.Single().PointsLost;

        oldLoss.Should().BeLessThan(recentLoss);
        oldLoss.Should().BeGreaterThan(0);
    }

    [Fact]
    public void InspectionsOlderThanTwoYears_AreIgnored()
    {
        var ancient = MakeInspection(Today.AddDays(-800),
            MakeViolation("3", ViolationCategory.FoodborneIllnessRiskFactor, ComplianceStatus.Repeat));

        var result = _scorer.Score([ancient], Today);

        result.Grade.Should().Be('?');
        result.InspectionsConsidered.Should().Be(0);
    }

    [Fact]
    public void ScoreIsClampedAtZero_AndGradesF()
    {
        var violations = Enumerable.Range(1, 20)
            .Select(i => MakeViolation(i.ToString(),
                ViolationCategory.FoodborneIllnessRiskFactor,
                ComplianceStatus.Repeat))
            .ToArray();
        var disaster = MakeInspection(Today.AddDays(-10), violations);

        var result = _scorer.Score([disaster], Today);

        result.Score.Should().Be(0);
        result.Grade.Should().Be('F');
    }

    private static Inspection MakeInspection(DateOnly date, params Violation[] violations) =>
        new()
        {
            SourceInspectionId = Guid.NewGuid().ToString(),
            InspectionDate = date,
            Type = InspectionType.Routine,
            Violations = [.. violations]
        };

    private static Violation MakeViolation(
        string code, ViolationCategory category, ComplianceStatus status) =>
        new()
        {
            Code = code,
            Description = $"Violation {code}",
            Category = category,
            Status = status
        };
}
