using Clean4ork.Core.Domain;
using Clean4ork.Core.Scoring;
using Clean4ork.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Clean4ork.Web.Pages;

/// <summary>
/// Home / search page. Renders real establishments from the database. A scored
/// establishment shows its computed letter grade; a pass/fail one shows the snapshot.
///
/// SCORE OBSCURITY: only the final letter grade leaves this page model. The raw
/// numeric score is never placed in ResultRow, so it can't reach the HTML.
/// </summary>
public class IndexModel(AppDbContext db, IInspectionScorer scorer) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    // Which page of results to show, read from ?page=N in the address bar.
    // (Named PageNumber because "Page" is already a method on PageModel.)
    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public const int PageSize = 50;

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
        DataTier Tier,
        char? Grade,
        string? PassFailHeadline);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var query = db.Establishments
            .Include(e => e.Inspections).ThenInclude(i => i.Violations)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(Q))
            query = query.Where(e => EF.Functions.ILike(e.Name, $"%{Q}%"));

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
            if (e.DataTier == DataTier.Scored)
            {
                var s = scorer.Score(e.Inspections, today);
                rows.Add(new ResultRow(e.Jurisdiction, e.Slug, e.Name, e.AddressLine1,
                    e.DataTier, s.Grade, null));
            }
            else if (e.DataTier == DataTier.PassFail)
            {
                var snap = PassFailSnapshot.ForEstablishment(e, today);
                rows.Add(new ResultRow(e.Jurisdiction, e.Slug, e.Name, e.AddressLine1,
                    e.DataTier, null, snap.Headline));
            }
            else
            {
                rows.Add(new ResultRow(e.Jurisdiction, e.Slug, e.Name, e.AddressLine1,
                    e.DataTier, null, "No inspection on record"));
            }
        }
        Results = rows;
    }
}