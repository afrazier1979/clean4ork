using Clean4ork.Core.Domain;
using Clean4ork.Core.Scoring;
using Clean4ork.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Clean4ork.Web.Pages;

/// <summary>
/// Home / search page. Renders real establishments from the database. A scored
/// establishment shows its computed grade; a pass/fail one shows the snapshot.
/// </summary>
public class IndexModel(AppDbContext db, IInspectionScorer scorer) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public IReadOnlyList<ResultRow> Results { get; private set; } = [];
    public int TotalCount { get; private set; }

    public record ResultRow(
        string Jurisdiction,
        string Slug,
        string Name,
        string? Address,
        DataTier Tier,
        char? Grade,
        int? Score,
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

        var establishments = await query
            .OrderBy(e => e.Name)
            .Take(50)
            .ToListAsync(ct);

        var rows = new List<ResultRow>(establishments.Count);
        foreach (var e in establishments)
        {
            if (e.DataTier == DataTier.Scored)
            {
                var s = scorer.Score(e.Inspections, today);
                rows.Add(new ResultRow(e.Jurisdiction, e.Slug, e.Name, e.AddressLine1,
                    e.DataTier, s.Grade, s.Score, null));
            }
            else if (e.DataTier == DataTier.PassFail)
            {
                var snap = PassFailSnapshot.ForEstablishment(e, today);
                rows.Add(new ResultRow(e.Jurisdiction, e.Slug, e.Name, e.AddressLine1,
                    e.DataTier, null, null, snap.Headline));
            }
            else
            {
                rows.Add(new ResultRow(e.Jurisdiction, e.Slug, e.Name, e.AddressLine1,
                    e.DataTier, null, null, "No inspection on record"));
            }
        }
        Results = rows;
    }
}
