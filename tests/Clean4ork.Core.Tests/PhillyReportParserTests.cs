using AngleSharp;
using AngleSharp.Html.Parser;
using Clean4ork.Core.Domain;
using Clean4ork.Scraper.Sources;
using FluentAssertions;

namespace Clean4ork.Core.Tests;

/// <summary>
/// Parser tests run against the real saved fixtures in tests/fixtures/philly/.
/// These are the actual HealthSpace pages, so they lock the parser against the
/// live HTML structure and catch regressions if selectors drift.
/// </summary>
public class PhillyReportParserTests
{
    private static readonly IHtmlParser Html =
        BrowsingContext.New(Configuration.Default).GetService<IHtmlParser>()!;

    private static string FixturesDir()
    {
        // walk up to the repo's tests/fixtures/philly
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "philly")))
            dir = dir.Parent;
        return dir is null
            ? throw new DirectoryNotFoundException("tests/fixtures/philly not found")
            : Path.Combine(dir.FullName, "tests", "fixtures", "philly");
    }

    private static AngleSharp.Dom.IDocument Load(string file) =>
        Html.ParseDocument(File.ReadAllText(Path.Combine(FixturesDir(), file)));

    [Fact]
    public void Reinspection_ParsesHeaderAndViolations()
    {
        var doc = Load("report-reinspection.html");
        var insp = PhillyReportParser.ParseReport(doc, "51496AE7-CF26-F58B-19092583022ED95E");

        insp.Should().NotBeNull();
        insp!.InspectionDate.Should().Be(new DateOnly(2026, 4, 13));
        insp.Type.Should().Be(InspectionType.Reinspection);

        // 6 OUT rows: item 8 (COS), 35/45/51/53/56+ (repeats)
        insp.Violations.Should().HaveCount(6);

        var v8 = insp.Violations.Single(v => v.Code == "8");
        v8.Category.Should().Be(ViolationCategory.FoodborneIllnessRiskFactor);
        v8.Status.Should().Be(ComplianceStatus.CorrectedOnSite);

        insp.Violations.Should().Contain(v => v.Code == "56+");
        insp.Violations.Where(v => v.Code is "35" or "45" or "51" or "53")
            .Should().OnlyContain(v => v.Status == ComplianceStatus.Repeat);
        insp.Violations.Where(v => v.Code is "35" or "45" or "51" or "53" or "56+")
            .Should().OnlyContain(v => v.Category == ViolationCategory.GoodRetailPractice);
    }

    [Fact]
    public void Routine_ParsesManyViolationsWithCorrectCategories()
    {
        var doc = Load("report-routine.html");
        var insp = PhillyReportParser.ParseReport(doc, "63BA2861-0D86-076C-930B22E67885BC9C");

        insp.Should().NotBeNull();
        insp!.InspectionDate.Should().Be(new DateOnly(2025, 7, 9));
        insp.Violations.Should().HaveCount(14);

        // FIRF items 1..27
        insp.Violations.Where(v => v.Code is "1" or "8" or "14" or "22" or "26")
            .Should().OnlyContain(v => v.Category == ViolationCategory.FoodborneIllnessRiskFactor);
        // GRP items 28+
        insp.Violations.Where(v => v.Code is "34" or "35" or "45" or "54" or "56+")
            .Should().OnlyContain(v => v.Category == ViolationCategory.GoodRetailPractice);
    }

    [Fact]
    public void SecondEstablishment_ParsesFourViolations()
    {
        var doc = Load("report-adelinas.html");
        var insp = PhillyReportParser.ParseReport(doc, "100BB32D-E649-6C12-CF60213FB1F43140");

        insp.Should().NotBeNull();
        insp!.InspectionDate.Should().Be(new DateOnly(2025, 4, 4));
        insp.Violations.Should().HaveCount(4);
        insp.Violations.Single(v => v.Code == "14").Status
            .Should().Be(ComplianceStatus.CorrectedOnSite);
    }

    [Fact]
    public void ReportFacilityHeader_Parses()
    {
        var doc = Load("report-reinspection.html");
        var (name, address, phone, type, officer, licensee) =
            PhillyReportParser.ParseReportFacility(doc);

        name.Should().Be("5th Street Pizza Corp");
        address.Should().Contain("4934 N 5TH ST");
        phone.Should().Contain("267");
        type.Should().Contain("Take Out");
    }

    [Fact]
    public void EstabPage_ListsReportInspectionIds()
    {
        var doc = Load("estab.html");
        var ids = PhillyReportParser.ReadReportInspectionIds(doc);

        // two Food reports; the empty EE slot is excluded
        ids.Should().HaveCountGreaterThanOrEqualTo(2);
        ids.Should().OnlyContain(id => id.Length > 10);
    }

    [Fact]
    public void SearchPage_ParsesRowsAndCap()
    {
        var doc = Load("search-19120.html");
        PhillyReportParser_SearchCount(doc).Should().Be(100); // capped

        var rows = SearchResultParser.ParseRows(doc);
        rows.Should().NotBeEmpty();
        rows.First().Name.Should().NotBeNullOrWhiteSpace();
        rows.Should().Contain(r => r.PostalCode == "19120");
    }

    private static int PhillyReportParser_SearchCount(AngleSharp.Dom.IDocument doc) =>
        SearchResultParser.ParseMatchCount(doc);
}
