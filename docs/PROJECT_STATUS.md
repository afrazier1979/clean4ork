# Clean4ork — Project Status & Decision Log

_Last updated: 2026-07-06. This document is the canonical tracker for the
project across working sessions._

## The product in one paragraph

Clean4ork is a Philadelphia restaurant health inspection platform with two
interlocking surfaces: a consumer-facing SEO content layer (restaurant grade
pages capturing organic search) and a B2B operator dashboard (Stripe
subscription; score breakdown + peer comparison, unlocked by claiming a
listing). The economics: near-fixed infrastructure costs against compounding
SEO traffic → high-margin solo-operator profitability.

## Decisions made (and why)

| Decision | Rationale |
|---|---|
| Server-rendered Razor Pages for the consumer surface | SEO-driven discovery requires server rendering; Angular deferred to the operator dashboard |
| C# for the scraper (not Python) | One runtime; shared EF Core + Npgsql/PostGIS data layer with the future API; AngleSharp + resilience handlers cover parsing/retry |
| "Clean4ork Score," never "Health Grade" | A synthetic grade labeled as our analysis of public data is legally defensible; an implied official grade is a lawsuit magnet |
| Transparency over disclaimers | Published methodology + per-page score audit + mechanical formula = First Amendment-protected opinion about public records |
| Deterministic scoring, no ML | Every deduction maps to a ScoreFactor; auditability is both the legal shield and the B2B product |
| Editorial UI aesthetic | Grade Card as hero; serif display type, warm non-traffic-light palette; Infatuation/Eater register, not civic-tech |

## Scoring algorithm (spec)

Start at 100. Per violation: FIRF −8 / GRP −2 base; ×0.5 corrected-on-site,
×1.5 out-of-compliance, ×2.0 repeat-flagged; ×1.75 recurrence if same code
within 365 days (stacks); time decay full ≤90 days → linear → 20% at 730
days → excluded after. Buckets A≥90 / B≥80 / C≥70 / D≥60 / F. No inspection
in 2 years → unscored ('?'). Re-calibrate thresholds quarterly targeting
~30/35/20/10/5 distribution. Methodology page and code constants must change
in the same commit.

## Data source (recon complete — live-verified 2026-07)

HealthSpace USA, `philadelphia-pa.healthinspections.us`, domainID=551.
All GET, no auth, userID=0 = anonymous.

- `/philadelphia/search.cfm?searchShow=zip` — full ZIP index (~160 ZIPs incl.
  junk/out-of-town) → scrape for ZIP discovery, no hardcoded list
- `/philadelphia/search.cfm?searchType=zip&zc={zip}&start={n}` — facilities in
  ZIP; 20/page, start is 1-BASED (1, 21, 41...); header shows "N Facilities
  matched" / "Displaying results X – Y of N". **Rows carry name, full address,
  facilityID AND the latest inspectionID** — so the initial crawl can go
  search → _report_full directly and skip estab.cfm, roughly halving requests.
- `/philadelphia/estab.cfm?facilityID={guid}` — latest report(s) ONLY (latest
  Food + latest EE, possibly empty) with inline violation summaries + links to
  full reports. NOT full 3-year history.
- `/_templates/551/RetailFood/_report_full.cfm?inspectionID={id}&domainID=551&userID=0`
  — full FDA form. FIRF items 1–27, GRP 28–54, Philly ordinances 55/56+;
  IN/OUT + COS/R flags; observations keyed by item number; temps; inspector.
  EE reports use /_templates/551/EE/.

**⚠ CRITICAL CONSTRAINT: every query silently caps at 100 results.**
Confirmed 2026-07 on both axes: ZIP 19120 → "100 matched" (alphabetical, ends
at "D"); name search "pizza" → "100 matched" (Philly has far more). `start=101`
is rejected outright, so there is NO paging past the cap. Only narrower queries
help. Cap detection + warning is implemented in PageThroughAsync.

**Second search axis (confirmed parameters):**
`search.cfm?start=1&SEARCHTYPE=name&KW1={kw}&REL1=F.organization_facility&PRE=Contains&BTNSEARCH=Search`
- `PRE` ∈ {Exact Match, Contains, Sounds-Like}
- `REL1` ∈ {Establishment Name = `F.organization_facility`, Address = **UNKNOWN**}
  — `F.address_facility` was rejected; unknown values bounce to the empty form.
  **Anthony: read the `<select name="REL1">` options from the form's HTML.**
- **The axes do NOT compose**: appending `zc=` to a name query bounces to the
  empty form, so "name within ZIP" is unavailable. Subdivision must therefore
  use citywide address/street queries, not street-within-ZIP.
- Only two search modes exist (nav confirms: Name/Address and Zipcode). There
  is no date-range or A–Z browse mode.

**Coverage strategy (planned):** union of (a) all ~160 ZIP queries, (b) citywide
Address/Contains queries per street name (street list from OpenDataPhilly
centerlines), (c) name-substring queries for the residue; dedupe on facilityID
and track the discovery curve until new queries stop yielding new IDs.

**⚠ History is shallow**: because estab.cfm shows only latest report(s), the
initial crawl is a snapshot; depth accumulates via repeated crawls. Raw HTML
archiving is therefore load-bearing (the archive becomes the only history —
and the moat). No official bulk dataset exists (OpenDataPhilly only lists the
Inquirer's own "Clean Plates" compilation).

Platform hosts 100+ municipalities on identical templates → parser is
portable; multi-city expansion is config (subdomain + domainID), not a
rewrite. This is the primary growth lever after Philly is stable.

## Build state

- [x] Solution scaffold (Core / Data / Scraper / Web / Tests) — 2026-05-20,
      reconstructed into this project 2026-07-06 with the scraper base URL
      corrected to the confirmed HealthSpace endpoints
- [x] Domain models, scoring engine, 10 xUnit scorer tests
- [x] EF Core + PostGIS context, docker-compose, DI wiring
- [x] Scraper worker loop with change detection + InspectionEvent log
- [x] Methodology page copy (docs/methodology.md)
- [x] Financial model (Launch/Growth/Scale; highest-sensitivity levers:
      operator conversion rate, subscription pricing) — 2026-06-08 session
- [x] ZIP discovery (DiscoverPostalCodesAsync), 1-based pagination, match-count
      parsing, 100-result cap detection — implemented 2026-07-23
- [ ] Recon: identify remaining search axes (name/street/date modes) to
      subdivide capped ZIP queries — **Anthony to check search page tabs**
- [x] Search results parser written against live HTML (SearchResultParser +
      FacilitySearchResult) — name, address, city/state/zip, last inspection
      date + ID
- [x] Name-axis search implemented (SearchByNameAsync); shared paginator with
      cap detection (PageThroughAsync)
- [x] REL1 Address relevance value resolved: `F.address_full_facility` (from
      the form's <select> options). PRE values: exact | Contains | soundex.
- [x] Name/address axis is SESSION-BASED: stateless GETs bounce to the empty
      form; must POST the form then GET pages on the same cookie jar.
      Implemented (SearchByNameAsync / SearchByAddressAsync / SearchKeywordAsync)
      + CookieContainer wired in Program.cs.
- [ ] Cap subdivision strategy implemented (street-name address queries)
- [ ] Raw HTML archive step in ScraperWorker (fetch → archive → parse → persist)
- [ ] HTML fixtures saved to tests/fixtures/philly/
- [x] Parser DONE — FetchEstablishmentAsync + FetchInspectionsAsync implemented
      (PhillyReportParser), validated against real fixtures 2026-08. Header (two
      cell layouts), 5-cell violation matrix [item,status,desc,COS,R], category
      map (1-27 FIRF / 28+ GRP / 56+ Ordinances), observations by item number.
- [x] Real HTML fixtures saved to tests/fixtures/philly/ (search, estab, 3 reports)
- [x] xUnit parser tests against fixtures (PhillyReportParserTests)
- [ ] End-to-end scrape of ZIP 19103 into Postgres
- [ ] First restaurant detail Razor page (slug → score render)
- [ ] Grade Card UI + design system
- [ ] Auth + claim flow, Stripe, operator dashboard
- [ ] Email alerts (Postmark), geocoding, sitemap/SEO plumbing
- [x] Statewide facility INDEX source built (PaOpenDataFacilitySource +
      IFacilityIndexSource + PaFacilityRecord) against the PA open-data API
- [ ] Verify Socrata field names vs live metadata (columns.json) before first run
- [x] Two-tier data model: DataTier {Scored, PassFail, Unknown} on Establishment
      + pass/fail snapshot fields (date, passed, reason); EF mapping + index
- [x] Ingest path with NEVER-DOWNGRADE rule (IngestFacilityIndexAsync): index
      rows create/refresh PassFail but never overwrite Scored; depth pass
      promotes to Scored
- [x] PassFailSnapshot guardrail (Core): no score-like output for pass/fail
      areas; date-forward, event-framed ("was out of compliance", not "is
      failing"); freshness decay Recent→Stale→Expired (180/540 days)
- [x] Restaurant detail page branches on DataTier (Grade Card vs pass/fail card)
- [ ] Bucks County depth via HealthSpace (same platform — config change)
- [ ] pafoodsafety.pa.gov per-facility report pages (violation-level, 61 counties)
- [ ] Custom county depth sources: Allegheny, Montgomery, Delaware, Erie

## Full system build (2026-08) — DONE (code), pending local verify/deploy

- [x] Cap-subdivision roster crawler (PhillyRosterCrawler): ZIP walk → detect
      100-cap → subdivide by street (address axis) → house-number split if still
      capped → dedupe by facilityID. Algorithm validated on a modeled universe
      (300/300 found, complete=True). Emits RosterCrawlReport completeness signal.
- [x] IngestService: owns all DB writes + tier rules. migrate / seed-passfail /
      crawl-philly / crawl-zip. Never-downgrade (Scored beats PassFail); promotes
      in place; logs InspectionEvents; 750ms politeness delay.
- [x] CLI runner (Scraper Program.cs): `dotnet run -- <command>`; no command =
      always-on worker.
- [x] DB-backed web pages (Index queries DB, ILike name search, scored vs
      pass/fail cards; Restaurant detail already branches on DataTier).
- [x] Deployment artifacts: Dockerfile (web), Dockerfile.scraper, railway.toml,
      Web Program.cs PORT/0.0.0.0 bind + optional migrate-on-startup.
- [x] RUNBOOK.md: build → verify → deploy → run scraper (the manual steps).
- [ ] EF Initial migration — generate locally: `dotnet ef migrations add Initial`
      (must be generated with the SDK, not hand-written).
- [ ] dotnet build / dotnet test — never run here (no SDK). Verify locally.
- [ ] Session-based name/address search — UNVERIFIED headlessly; step 6 in the
      runbook is the proof point the full crawl depends on.

## Statewide expansion — data landscape (recon 2026-08)

**Breadth is cheap and public; depth is the moat.** PA publishes inspection
data through many independent systems, but the facility layer is a free API.

**Facility INDEX (breadth) — data.pa.gov Socrata dataset `etb6-jzdg`:**
- Public domain. SODA JSON (`/resource/etb6-jzdg.json`, $limit/$offset/$where),
  OData, and bulk CSV. Optional X-App-Token raises rate limit.
- FACILITY-LEVEL ONLY: one row per facility with latest inspection date,
  reason, and a single Overall Compliance Yes/No + geocoded lat/long. **No
  violations, no FIRF/GRP items, no codes/flags** → cannot feed the scorer.
- Great for: statewide establishment skeleton, free geocoding (PostGIS), SEO
  surface, "last inspected / passed". Implemented as PaOpenDataFacilitySource.
- Includes independent-county facilities too (Philadelphia appears) → treat as
  DISCOVERY ONLY; never overwrite scoreable depth. IndependentCounties set in
  the source handles jurisdiction routing.

**Violation DEPTH (scoreable) — per-jurisdiction, by platform:**
- Philadelphia → HealthSpace (done, full depth).
- Bucks → `pa.healthinspections.us/bucks/` — SAME HealthSpace platform; near-free.
- PA Dept of Agriculture (61 of 67 counties) → `pafoodsafety.pa.gov` report
  pages (scrapable HTML). NOT yet confirmed to expose full violation detail —
  next recon step to decide if statewide *scoring* is possible.
- Allegheny (Pittsburgh) → custom app AND its own open-data portal with actual
  violation data (worth checking as an API, like the state one).
- Montgomery → `webapp.montcopa.org`; Delaware → `public.cdpehs.com` (CDP);
  Erie → `public.eriecountypa.gov` (address-search only). Each a new parser.
- Delaware Co. caveat: Marple Twp (and others) court-enjoined from inspection
  → that data legally does not exist anywhere.

**Competitive landscape (matters for strategy):**
- restaurantswatch.com (PA Eat Safe LLC): statewide lookup mirror, name+address
  search, deliberately non-judgmental. No score, no ranking, no operator product.
- Spotlight PA "Restaurant Safety Tracker": nonprofit newsroom, 61 counties from
  the same PA Ag data, auto-generated county articles, subscriber alerts. No
  computed grade, no operator monetization.
- Both pull the SAME free public sources. Breadth is therefore NOT defensible
  for anyone. The Clean4ork Score, Grade Card, and Stripe operator dashboard
  remain unoccupied — that's the moat. Strategy: consume the free breadth as an
  input, lead with scored depth where violation data is obtainable.

## Known open items / risks

- Scorer weights are provisional until calibrated on ~90 days of real data
- Politeness policy for the scraper (rate limiting beyond resilience retries)
  not yet defined — worth a small delay-between-requests setting before the
  first full-ZIP crawl
- EF migration not yet generated (run `dotnet ef migrations add Initial`)
