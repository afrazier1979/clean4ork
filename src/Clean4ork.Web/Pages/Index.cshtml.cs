using Clean4ork.Core.Domain;
using Clean4ork.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Clean4ork.Web.Pages;

/// <summary>
/// Landing page. Explains what Clean4ork is before anyone sees data.
/// Only pulls two cheap counts for the stats row.
/// </summary>
public class IndexModel(AppDbContext db) : PageModel
{
    public int TotalPlaces { get; private set; }
    public int GradedPlaces { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        TotalPlaces = await db.Establishments.CountAsync(ct);
        GradedPlaces = await db.Establishments.CountAsync(e => e.DataTier == DataTier.Scored, ct);
    }
}
