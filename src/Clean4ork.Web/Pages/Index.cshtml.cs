using System.Globalization;
using Clean4ork.Core.Domain;
using Clean4ork.Core.Scoring;
using Clean4ork.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Clean4ork.Web.Pages;

/// <summary>
/// Home / search page. A scored establishment shows its letter grade (placard);
/// a pass/fail one shows the state result and date (chit).
///
/// SCORE OBSCURITY: only the final letter grade leaves this page model. The raw
/// numeric score is never placed in ResultRow, so it can't reach the HTML.
/// </summary>
public class IndexModel(AppDbContext db, IInspectionScorer scorer) : PageModel
{
    public const string PhillyJurisdiction = "philadelphia-pa";
    public const int PageSize = 50;

    private static readonly TextInfo TitleCase = CultureInfo.InvariantCulture.TextInfo;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>"philly", "pa" (everywhere else), or empty for all.</summary>
    [BindProperty(SupportsGet = true, Name = "area")]
    public string? Area { get; set; }

    /// <summary>Read from ?p=N. ("page" is reserved by Razor Pages.)</summary>
    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<ResultRow> Results { get; private set; } = [];
    public int TotalCount { get; private set; }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public int FirstShown => TotalCount == 0 ? 0 : (PageNumber - 1) * PageSize + 1;
    public int LastShown => Math.Min(PageNumber * PageSize, TotalCount);

    public record ResultRow(
        string Jurisdiction,
        string Slug,
        string Name,
        string? Address,
        string? Place,
        DataTier Tier,
        char? Grade,
        bool? Passed,
        string? InspectedOn);

    /// <summary>Builds a link that keeps the current search and filter.</summary>
    public string Link(int page, string? area = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Q)) parts.Add("Q=" + Uri.EscapeDataString(Q));
        var a = area ?? Area;
        if (!string.IsNullOrWhiteSpace(a)) parts.Add("area=" + Uri.EscapeDataString(a));
        if (page > 1) parts.Add("p=" + page);
        return parts.Count == 0 ? "/" : "/?" + string.Join("&", parts);
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var query = db.Establishments
            .Include(e => e.Inspections).ThenInclude(i => i.Violations)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(Q))
            query = query.Where(e => EF.Functions.ILike(e.Name, $"%{Q}%"));

        if (Area == "philly")
            query = query.Where(e => e.Jurisdiction == PhillyJurisdiction);
        else if (Area == "pa")
            query = query.Where(e => e.Jurisdiction != PhillyJurisdiction);

        TotalCount = await query.CountAsync(ct);

        // Keep the page number in range (no page 0, no page past the end).
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);

        var establishments = await query
            .OrderBy(e => e.Name)
            .ThenBy(e => e.Id) // tie-breaker so the order is stable between pages
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);

        var rows = new List<ResultRow>(establishments.Count);
        foreach (var e in establishments)
        {
            var address = Tidy(e.AddressLine1);
            var place = Place(e);

            if (e.DataTier == DataTier.Scored)
            {
                var s = scorer.Score(e.Inspections, today);
                rows.Add(new ResultRow(e.Jurisdiction, e.Slug, e.Name, address, place,
                    e.DataTier, s.Grade, null, null));
            }
            else
            {
                rows.Add(new ResultRow(e.Jurisdiction, e.Slug, e.Name, address, place,
                    e.DataTier, null, e.SnapshotPassed,
                    e.SnapshotInspectionDate?.ToString("MMM d, yyyy")));
            }
        }
        Results = rows;
    }

    /// <summary>"1001 NORTHAMPTON ST" becomes "1001 Northampton St".</summary>
    private static string? Tidy(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : TitleCase.ToTitleCase(value.Trim().ToLowerInvariant());

    /// <summary>"Easton 18042", built from whatever city/ZIP we have.</summary>
    private static string? Place(Establishment e)
    {
        var bits = new[] { Tidy(e.City), e.PostalCode?.Trim() }
            .Where(b => !string.IsNullOrWhiteSpace(b));
        var s = string.Join(" ", bits);
        return s.Length == 0 ? null : s;
    }
}