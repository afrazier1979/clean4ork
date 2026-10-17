using System.Globalization;
using Clean4ork.Core.Domain;
using Clean4ork.Core.Scoring;
using Clean4ork.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Clean4ork.Web.Pages;

/// <summary>
/// Restaurant detail page. Renders one of two ways depending on DataTier:
///   Scored   -> letter grade placard + violation history
///   PassFail -> state result chit + guardrailed PassFailSnapshot copy, NO grade
///
/// SCORE OBSCURITY: the ScoreResult never reaches the view. Only the letter,
/// the as-of date, and violation descriptions are projected out. No numeric
/// score, no points per violation, and violations are ordered by date (not by
/// weight), so the formula can't be read off the page.
/// </summary>
public class RestaurantModel(AppDbContext db, IInspectionScorer scorer) : PageModel
{
    private static readonly TextInfo TitleCase = CultureInfo.InvariantCulture.TextInfo;

    public Establishment? Establishment { get; private set; }
    public string? AddressLine { get; private set; }

    /// <summary>Populated only for Scored establishments.</summary>
    public string? GradeLetter { get; private set; }
    public string? GradeAsOf { get; private set; }
    public IReadOnlyList<FactorRow> Factors { get; private set; } = [];

    /// <summary>Populated only for PassFail establishments.</summary>
    public PassFailSnapshot? Snapshot { get; private set; }

    public record FactorRow(string Description, string Code, string Inspected, bool IsRepeat);

    public async Task<IActionResult> OnGetAsync(string jurisdiction, string slug, CancellationToken ct)
    {
        Establishment = await db.Establishments
            .Include(e => e.Inspections)
            .ThenInclude(i => i.Violations)
            .FirstOrDefaultAsync(e => e.Jurisdiction == jurisdiction && e.Slug == slug, ct);

        if (Establishment is null)
            return NotFound();

        AddressLine = BuildAddress(Establishment);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        switch (Establishment.DataTier)
        {
            case DataTier.Scored:
                var s = scorer.Score(Establishment.Inspections, today);
                var letter = s.Grade.ToString();
                GradeLetter = string.IsNullOrEmpty(letter) ? null : letter;
                GradeAsOf = s.AsOf.ToString("MMMM d, yyyy");
                Factors = s.Factors
                    .OrderByDescending(f => f.InspectionDate)
                    .Select(f => new FactorRow(
                        $"{f.Description}",
                        $"{f.ViolationCode}",
                        f.InspectionDate.ToString("MMM d, yyyy"),
                        f.IsRepeat))
                    .ToList();
                break;

            case DataTier.PassFail:
                Snapshot = PassFailSnapshot.ForEstablishment(Establishment, today);
                break;

            case DataTier.Unknown:
            default:
                // Neither card; the view shows a neutral "no data on record" state.
                break;
        }

        return Page();
    }

    private static string? Tidy(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : TitleCase.ToTitleCase(value.Trim().ToLowerInvariant());

    /// <summary>"1001 Northampton St, Easton, PA 18042"</summary>
    private static string? BuildAddress(Establishment e)
    {
        var stateZip = string.Join(" ",
            new[] { e.State?.Trim().ToUpperInvariant(), e.PostalCode?.Trim() }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

        var parts = new[] { Tidy(e.AddressLine1), Tidy(e.City), stateZip.Length == 0 ? null : stateZip }
            .Where(x => x is not null);

        var line = string.Join(", ", parts);
        return line.Length == 0 ? null : line;
    }
}