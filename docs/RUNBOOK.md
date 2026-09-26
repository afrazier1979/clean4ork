# Clean4ork — Runbook (build → verify → deploy → run the scraper)

This is the exact ordered list of steps to take the system live. The code is
complete; these are the things only you can do (they need the .NET SDK, a real
database, and network access to Railway and the live Philadelphia site).

Prereqs on your machine: .NET 9 SDK, Docker, a GitHub account, a Railway
account, and the `dotnet-ef` tool: `dotnet tool install --global dotnet-ef`.

---

## PHASE 1 — Build & verify locally (no deployment yet)

1. **Restore & build**
   ```
   dotnet restore
   dotnet build
   ```
   Fix any compile errors first. (The code was validated structurally and its
   parsing logic proven against real HTML, but it has not been compiled here.)

2. **Run the parser tests** — proves the parser still matches the real fixtures.
   ```
   dotnet test
   ```
   Expect PhillyReportParserTests green (6 reports/edge cases) + scorer tests.

3. **Generate the initial EF migration** (must be done with the SDK; not
   hand-written). From the repo root:
   ```
   dotnet ef migrations add Initial \
     --project src/Clean4ork.Data \
     --startup-project src/Clean4ork.Web
   ```
   This creates src/Clean4ork.Data/Migrations/. Commit it.

4. **Start a local Postgres+PostGIS** (Docker):
   ```
   docker run --name clean4ork-db -e POSTGRES_PASSWORD=clean4ork_dev \
     -e POSTGRES_USER=clean4ork -e POSTGRES_DB=clean4ork \
     -p 5432:5432 -d postgis/postgis:16-3.4
   ```
   The default connection string in appsettings already points here.

5. **Create the schema**:
   ```
   dotnet run --project src/Clean4ork.Scraper -- migrate
   ```

6. **Prove the linchpin: the session-based name/address search.** Before the
   full crawl, confirm one ZIP works end to end:
   ```
   dotnet run --project src/Clean4ork.Scraper -- crawl-zip 19120
   ```
   Watch the logs. Success = establishments + inspections written. If the search
   bounces to an empty form (0 results everywhere), the session/cookie handshake
   needs adjusting — capture the logs and we fix SearchKeywordAsync before
   trusting the full crawl.

7. **Seed the pass/fail tier** (statewide, no scraping — just the open-data API):
   ```
   dotnet run --project src/Clean4ork.Scraper -- seed-passfail
   ```
   NOTE: verify the Socrata field names first (one-time) against
   https://data.pa.gov/api/views/etb6-jzdg/columns.json and adjust the Fields
   constants in PaOpenDataFacilitySource if any differ.

8. **Run the web app** and confirm real data renders:
   ```
   dotnet run --project src/Clean4ork.Web
   ```
   Search a name; open a restaurant; confirm scored vs pass/fail cards.

---

## PHASE 2 — Get the shell live on Railway (the "test site ASAP" win)

9. **Push to GitHub** (Railway deploys from GitHub). Ensure NO secrets are
   committed (passwords/keys live in Railway env vars, not the repo):
   ```
   git init && git add . && git commit -m "Clean4ork initial"
   git remote add origin https://github.com/<you>/clean4ork.git
   git push -u origin main
   ```

10. **Create the Railway project** → New Project → Deploy from GitHub repo →
    pick the repo. It reads railway.toml + Dockerfile and builds the WEB app.

11. **Add Postgres** → in the project, New → Database → PostgreSQL. Railway’s
    image supports PostGIS. Copy its connection details.

12. **Set env vars on the web service**:
    - `ConnectionStrings__Default` = the Railway Postgres connection string, in
      Npgsql form: `Host=…;Port=5432;Database=…;Username=…;Password=…`
    - `RUN_MIGRATIONS_ON_STARTUP` = `true` (for the first deploy; the app will
      migrate + enable PostGIS on boot)
    - `ASPNETCORE_ENVIRONMENT` = `Production`

13. **Deploy.** When it’s green, open the Railway-provided URL — the shell is
    live. (It’ll be empty until Phase 3 seeds data.)

14. **Attach the domain** → web service → Settings → Networking → Custom Domain
    → `www.clean4ork.com`. Railway gives you a CNAME; add it at your DNS
    registrar. Railway issues HTTPS automatically. (Point the domain at
    **Railway**, not Vercel — Vercel can’t run ASP.NET.)

---

## PHASE 3 — Get data flowing

15. **Seed pass/fail into the live DB.** Easiest: add a SECOND Railway service
    from the same repo using `Dockerfile.scraper`, then run a one-off command on
    it: set its start command to `dotnet Clean4ork.Scraper.dll seed-passfail`
    (run once), or use Railway’s one-off command feature. Real statewide data
    appears on the site within minutes.

16. **Start the Philadelphia crawl** (the differentiator). On the scraper
    service, run `dotnet Clean4ork.Scraper.dll crawl-philly`. This is the slow,
    polite, over-hours/days crawl (750ms between facilities). Rows upgrade from
    pass/fail to scored in place as reports come in.

17. **Switch the scraper to scheduled mode.** Once seeded, set the scraper
    service’s command back to default (no args) so it runs as the always-on
    worker on its interval, catching new inspections.

18. **Turn off first-boot migration.** After the first successful deploy, set
    `RUN_MIGRATIONS_ON_STARTUP` back to `false` (or leave migrations to an
    explicit `migrate` command) so web boots don’t run schema changes.

---

## Order of leverage if you want to go faster
- Shell live (steps 9–14) is independent of the scraper — do it first for a URL.
- Pass/fail seed (step 15) makes the site show real data fastest.
- Philadelphia crawl (step 16) adds the score; it’s the slow part, safe to run
  for days in the background.

## Known things to watch
- The name/address search is session-based and UNVERIFIED headlessly (step 6 is
  the proof point). Everything about full Philadelphia completeness rests on it.
- Socrata field names in PaOpenDataFacilitySource are unverified (step 7 note).
- Nothing here has been compiled in this environment — expect to fix a small
  thing or two on first `dotnet build`; that’s normal.
