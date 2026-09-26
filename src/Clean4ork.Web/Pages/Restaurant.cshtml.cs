using Clean4ork.Core.Domain;
using Clean4ork.Core.Scoring;
using Clean4ork.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Clean4ork.Web.Pages;

/// <summary>
/// Restaurant detail page. Renders one of two ways depending on DataTier:
///   Scored   → full Clean4ork Score + Grade Card (via IInspectionScorer)
///   PassFail → guardrailed pass/fail card (via PassFailSnapshot), NO score
/// The branch is driven by the stored tier, not inferred at render time.
/// </summary>
public class RestaurantModel(AppDbContext db, IInspectionScorer scorer) : PageModel
{
    public Establishment? Establishment { get; private set; }

    /// <summary>Populated only when Establishment.DataTier == Scored.</summary>
    public ScoreResult? Score { get; private set; }

    /// <summary>Populated only when Establishment.DataTier == PassFail.</summary>
    public PassFailSnapshot? Snapshot { get; private set; }

    public async Task<IActionResult> OnGetAsync(string jurisdiction, string slug, CancellationToken ct)
    {
        Establishment = await db.Establishments
            .Include(e => e.Inspections)
            .ThenInclude(i => i.Violations)
            .FirstOrDefaultAsync(e => e.Jurisdiction == jurisdiction && e.Slug == slug, ct);

        if (Establishment is null)
            return NotFound();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        switch (Establishment.DataTier)
        {
            case DataTier.Scored:
                Score = scorer.Score(Establishment.Inspections, today);
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
}
