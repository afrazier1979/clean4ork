# Clean4ork

Philadelphia restaurant health inspection grades — a consumer-facing SEO
content layer plus a B2B operator dashboard, built on scraped public records
from the HealthSpace USA platform.

## Solution layout

```
src/Clean4ork.Core      Domain models + the scoring algorithm (the core IP)
src/Clean4ork.Data      EF Core 9 + Npgsql + PostGIS. AppDbContext, DI wiring
src/Clean4ork.Scraper   .NET Worker Service. AngleSharp parsing, resilience via
                      AddStandardResilienceHandler, IInspectionSource seam
src/Clean4ork.Web       Razor Pages frontend (server-rendered for SEO)
tests/Clean4ork.Core.Tests   10 xUnit tests on the scorer
tests/fixtures/philly      Saved HTML fixtures for offline parser development
```

## Quickstart

```bash
# 1. Start Postgres + PostGIS
docker compose up -d

# 2. Restore + build
dotnet restore
dotnet build

# 3. Run the scoring tests (should pass — fill in fixtures for parser tests later)
dotnet test

# 4. Create the database schema
dotnet ef migrations add Initial --project src/Clean4ork.Data --startup-project src/Clean4ork.Web
dotnet ef database update --project src/Clean4ork.Data --startup-project src/Clean4ork.Web

# 5. Run the web app
dotnet run --project src/Clean4ork.Web

# 6. In a second terminal, run the scraper
dotnet run --project src/Clean4ork.Scraper
```

## Data source

Philadelphia's inspections live on the HealthSpace USA platform at
`philadelphia-pa.healthinspections.us`. Three confirmed endpoints, all GET,
no auth, no cookies:

| Endpoint | Purpose | Key |
|---|---|---|
| `search.cfm?zip={zip}&start={n}` | Facility search by ZIP, 20/page | — |
| `estab.cfm?facilityID={guid}` | Facility profile + inspection history | facilityID GUID |
| `_report_full.cfm?inspectionID={id}&domainID=551` | Full FDA-standard report | inspectionID + domainID |

`domainID=551` identifies Philadelphia. The platform hosts 100+ other
municipalities on identical templates — the `IInspectionSource` interface in
the scraper is the seam for multi-city expansion.

## The scoring algorithm

See `src/Clean4ork.Core/Scoring/InspectionScorer.cs`. Every point of deduction
is explainable via a `ScoreFactor`. Weights are a defensible starting point;
calibrate against the real Philly distribution once you have ~90 days of
scraped data so buckets land around 30% A / 35% B / 20% C / 10% D / 5% F.

| Component | Value |
|---|---|
| Foodborne Illness Risk Factor violation | −8 pts base |
| Good Retail Practice violation | −2 pts base |
| Corrected on site | ×0.5 |
| Out of compliance | ×1.5 |
| Repeat (flagged on report) | ×2.0 |
| Recurrence (same code within 365 days) | ×1.75, stacks |
| Time decay | Full weight ≤90 days, linear to 20% at 730 days, ignored after |
| Grade buckets (initial) | A ≥90, B ≥80, C ≥70, D ≥60, F <60 |

## Weekend plan

**Saturday morning** — Get green. Spin up the Postgres container,
restore/build the solution, run the scorer tests. Hit each endpoint by hand
(search.cfm, estab.cfm, _report_full.cfm) for a known ZIP and save 3–5 raw
HTML files into `tests/fixtures/philly/` (a Routine, a Reinspection, a
Complaint, an Initial). These are your parser test bed.

**Saturday afternoon** — Fill in the `// TODO:` parser blocks in
`PhillyInspectionSource.cs` against the fixtures. Use AngleSharp's CSS
selectors. Don't hit the live site yet — work entirely against saved HTML so
the iteration loop is instant. End the afternoon when you can parse one
fixture into a populated `Establishment` + `Inspection` + `Violation` graph.

**Sunday morning** — Wire it up end to end. Run the scraper against ZIP 19103
(or another small ZIP) and let it persist to Postgres. Verify the data with a
couple of SQL queries (`select count(*) from "Establishments"`, then look at
one establishment's inspections). Iterate until the scrape is clean and
repeatable.

**Sunday afternoon** — Build the first real Razor page: a restaurant detail
page that takes a slug, loads the establishment + inspections, runs the
scorer, and renders the result. Doesn't need to be styled yet — that's the
next chat.

By Sunday night you'll have: data flowing in, a stored Postgres view of one
ZIP's restaurants, and a working web page showing a score for any one of
them. That's a credible v0 spine — every subsequent weekend adds polish,
coverage, auth, claim flow, Stripe.

## What's deferred

- The Grade Card UI and the rest of the public design system (editorial
  aesthetic: serif display type, warm non-traffic-light palette)
- Auth + claim-your-restaurant flow
- Stripe + operator dashboard (score breakdown + peer comparison)
- Email alerts via Postmark (InspectionEvent is the append-only feed for this)
- Methodology page publishing (`docs/methodology.md` is the copy)
- Geocoding (PostGIS is wired; call out to Nominatim or Mapbox to populate
  `Establishment.Location`)
- Multi-jurisdiction support (the `IInspectionSource` abstraction is the
  seam — Camden, Wilmington, NYC all plug in here later)
