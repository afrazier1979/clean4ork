using AngleSharp;
using AngleSharp.Html.Parser;
using Clean4ork.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Clean4ork.Scraper.Sources;

/// <summary>
/// Source for Philadelphia's health inspection data on the HealthSpace USA
/// platform. All endpoints live-verified 2026-07: ColdFusion, GET, no auth,
/// no cookies, userID=0 is the anonymous view.
///
///   /philadelphia/index.cfm
///       → main page
///   /philadelphia/search.cfm?searchShow=zip
///       → index of every ZIP present in the database (~160, incl. junk and
///         out-of-town owner/mailing ZIPs). Scrape this for ZIP discovery.
///   /philadelphia/search.cfm?searchType=zip&amp;zc={zip}&amp;start={n}
///       → facilities in a ZIP. 20/page; start is 1-based (1, 21, 41, ...).
///         Header shows "N Facilities matched" and "Displaying results X – Y of N".
///         *** RESULTS ARE CAPPED AT 100 PER QUERY (alphabetical, silently
///         truncated) — a count of exactly 100 means the query must be
///         subdivided via another search axis. ***
///         Each result: facility name → estab.cfm?facilityID={guid}, address,
///         city/state/zip, last inspection date → estab.cfm?...&amp;inspectionID=...&amp;inspType=Food
///   /philadelphia/estab.cfm?facilityID={guid}
///       → "Related Reports": the facility's most recent report(s) only (NOT
///         full 3-year history — typically latest Food + latest EE/Environmental,
///         which may be an empty slot). Each block: inspection date, inline
///         violation summaries (item number + observation text), and a
///         "View Full Inspection Report" link to _report_full.cfm.
///   /_templates/551/RetailFood/_report_full.cfm?inspectionID={id}&amp;domainID=551&amp;userID=0
///       → complete FDA-standard inspection form: FIRF matrix items 1–27,
///         GRP items 28–54, Philadelphia ordinances 55/56+; per-item IN/OUT
///         with COS and R flags; temperature observations; Observations and
///         Corrective Actions keyed by item number; Remarks; Summary
///         Statements; inspector name/phone; date + time in/out.
///         (EE reports use /_templates/551/EE/ instead of /RetailFood/.)
///
/// Category mapping for the scorer: 1–27 → FoodborneIllnessRiskFactor,
/// 28–54 → GoodRetailPractice, 55+ → GoodRetailPractice (Philly ordinance).
///
/// Because estab.cfm exposes only the latest report(s), full history is
/// accumulated by repeated crawls over time. Always archive raw HTML — the
/// portal's own window rolls off and our archive becomes the only history.
///
/// domainID=551 identifies Philadelphia within HealthSpace; the
/// /_templates/{domainID}/ path segment tracks it. Other cities on the
/// platform use the same templates with a different subdomain + domainID.
/// </summary>
public class PhillyInspectionSource(
    HttpClient httpClient,
    ILogger<PhillyInspectionSource> logger) : IInspectionSource
{
    private const string BaseUrl = "https://philadelphia-pa.healthinspections.us/";
    private const string DomainId = "551";

    private const string SearchPath = "philadelphia/search.cfm";
    private const string EstabPath = "philadelphia/estab.cfm";
    private const string ReportPath = "_templates/551/RetailFood/_report_full.cfm";

    private const int PageSize = 20;

    /// <summary>
    /// HealthSpace truncates EVERY query at 100 results, silently, sorted by
    /// facility name. Verified 2026-07: ZIP 19120 and name search "pizza" both
    /// report exactly 100; start=101 is rejected outright. There is no way to
    /// page past it — the only remedy is to issue narrower queries.
    /// </summary>
    private const int SearchResultCap = 100;

    // ---- Name/Address search axis --------------------------------------
    //
    // Form (from the page source): method="post" action="search.cfm" with
    //   <input type="hidden" name="searchType" value="name">
    //   <input type="text"   name="kw1">
    //   <select name="rel1">  F.organization_facility | F.address_full_facility
    //   <select name="pre">   exact | Contains | soundex
    //   <input type="submit" name="btnSearch" value="Search">
    //
    // *** THIS AXIS REQUIRES A SESSION. *** Verified 2026-07: issuing the
    // pagination URL as a plain stateless GET — even one copied verbatim from
    // a working result page — redirects to the empty search form. The scraper
    // must POST the form first and carry cookies for subsequent page GETs.
    // (The ZIP axis has no such requirement; plain GET works there.)
    private const string RelevanceName = "F.organization_facility";
    private const string RelevanceAddress = "F.address_full_facility";

    private const string PrecisionExact = "exact";
    private const string PrecisionContains = "Contains";
    private const string PrecisionSoundex = "soundex";

    // NOTE: the two axes do NOT compose — appending zc= to a name query
    // bounces back to the empty form, so "name within ZIP" is not available.

    private readonly IHtmlParser _parser =
        BrowsingContext.New(Configuration.Default).GetService<IHtmlParser>()!;

    public string Jurisdiction => "philadelphia-pa";

    /// <summary>
    /// Scrape the ZIP index page to discover every postal code in the
    /// database. Includes junk (00000, 99999) and out-of-town ZIPs; callers
    /// can filter, but crawling them all is cheap and misses nothing.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverPostalCodesAsync(
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{SearchPath}?searchShow=zip";
        var html = await httpClient.GetStringAsync(url, cancellationToken);
        var document = await _parser.ParseDocumentAsync(html, cancellationToken);

        var zips = document.QuerySelectorAll("a[href*='searchType=zip']")
            .Select(a => a.GetAttribute("href"))
            .Where(h => h is not null)
            .Select(h => SearchResultParser.QueryParam(h!, "zc"))
            .Where(z => !string.IsNullOrWhiteSpace(z))
            .Select(z => z!)
            .Distinct()
            .OrderBy(z => z)
            .ToList();

        logger.LogInformation("Discovered {Count} ZIP codes from index", zips.Count);
        return zips;
    }

    /// <summary>
    /// Enumerate facilities in a ZIP. Yields full search rows (name, address,
    /// latest inspection ID) rather than bare IDs — the search page carries
    /// all of it, so callers can skip estab.cfm on the initial pass.
    /// </summary>
    public async IAsyncEnumerable<FacilitySearchResult> SearchByPostalCodeAsync(
        string postalCode,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{SearchPath}?searchType=zip&zc={Uri.EscapeDataString(postalCode)}";
        await foreach (var row in PageThroughAsync(url, $"ZIP {postalCode}", cancellationToken))
            yield return row;
    }

    /// <summary>
    /// Enumerate facilities whose NAME matches a keyword. Fills coverage gaps
    /// left by ZIP queries that hit the 100-result cap.
    /// </summary>
    public IAsyncEnumerable<FacilitySearchResult> SearchByNameAsync(
        string keyword,
        CancellationToken cancellationToken) =>
        SearchKeywordAsync(keyword, RelevanceName, PrecisionContains, cancellationToken);

    /// <summary>
    /// Enumerate facilities whose ADDRESS matches a keyword — the workhorse for
    /// subdividing capped ZIPs, since a single street rarely holds 100+ food
    /// establishments citywide.
    /// </summary>
    public IAsyncEnumerable<FacilitySearchResult> SearchByAddressAsync(
        string keyword,
        CancellationToken cancellationToken) =>
        SearchKeywordAsync(keyword, RelevanceAddress, PrecisionContains, cancellationToken);

    /// <summary>
    /// Runs a keyword search on the name/address axis. Posts the search form
    /// to establish the session, parses the first page from the POST response,
    /// then pages through the rest with GETs on the same cookie jar.
    /// </summary>
    private async IAsyncEnumerable<FacilitySearchResult> SearchKeywordAsync(
        string keyword,
        string relevance,
        string precision,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var label = $"{(relevance == RelevanceAddress ? "address" : "name")} '{keyword}'";
        logger.LogInformation("Searching {Label}", label);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["searchType"] = "name",
            ["kw1"] = keyword,
            ["rel1"] = relevance,
            ["pre"] = precision,
            ["btnSearch"] = "Search"
        });

        using var response = await httpClient.PostAsync(
            $"{BaseUrl}{SearchPath}", form, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var document = await _parser.ParseDocumentAsync(html, cancellationToken);

        var total = SearchResultParser.ParseMatchCount(document);
        if (total >= SearchResultCap)
        {
            logger.LogWarning(
                "Query {Label} hit the {Cap}-result cap — TRUNCATED. Narrow the keyword.",
                label, SearchResultCap);
        }
        else if (total == 0)
        {
            // Also the signal for "bounced back to the empty form".
            logger.LogWarning("Query {Label} returned no results", label);
            yield break;
        }

        var firstPage = SearchResultParser.ParseRows(document);
        foreach (var row in firstPage)
            yield return row;

        if (firstPage.Count < PageSize) yield break;

        // Subsequent pages are GETs carrying the same query, on the session
        // cookie established by the POST above.
        var pagedUrl = $"{BaseUrl}{SearchPath}?SEARCHTYPE=name" +
                       $"&KW1={Uri.EscapeDataString(keyword)}" +
                       $"&REL1={relevance}" +
                       $"&PRE={precision}" +
                       $"&BTNSEARCH=Search";

        for (var start = 1 + PageSize; start <= SearchResultCap; start += PageSize)
        {
            var pageHtml = await httpClient.GetStringAsync(
                $"{pagedUrl}&start={start}", cancellationToken);
            var pageDoc = await _parser.ParseDocumentAsync(pageHtml, cancellationToken);

            var rows = SearchResultParser.ParseRows(pageDoc);
            foreach (var row in rows)
                yield return row;

            if (rows.Count < PageSize) yield break;
        }
    }

    /// <summary>
    /// Shared pagination + cap detection for both search axes. The caller
    /// supplies a base URL without a start parameter.
    /// </summary>
    private async IAsyncEnumerable<FacilitySearchResult> PageThroughAsync(
        string baseQueryUrl,
        string label,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var start = 1; // 1-based: 1, 21, 41, ...
        var totalReported = -1;

        while (true)
        {
            var url = $"{baseQueryUrl}&start={start}";
            logger.LogInformation("Searching {Label} (start={Start})", label, start);

            var html = await httpClient.GetStringAsync(url, cancellationToken);
            var document = await _parser.ParseDocumentAsync(html, cancellationToken);

            if (totalReported < 0)
            {
                totalReported = SearchResultParser.ParseMatchCount(document);

                if (totalReported >= SearchResultCap)
                {
                    // Results are truncated alphabetically and there is no way
                    // to page past the cap. The caller must narrow the query.
                    logger.LogWarning(
                        "Query {Label} hit the {Cap}-result cap — TRUNCATED. Narrower queries required for full coverage.",
                        label, SearchResultCap);
                }
                else if (totalReported == 0)
                {
                    // A bounce back to the empty search form (invalid params)
                    // also lands here.
                    logger.LogWarning("Query {Label} returned no results", label);
                    yield break;
                }
            }

            var rows = SearchResultParser.ParseRows(document);
            foreach (var row in rows)
                yield return row;

            if (rows.Count < PageSize) yield break;

            start += PageSize;
            if (start > SearchResultCap) yield break; // hard ceiling
        }
    }

    public async IAsyncEnumerable<string> ListFacilityIdsForPostalCodeAsync(
        string postalCode,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        await foreach (var row in SearchByPostalCodeAsync(postalCode, cancellationToken))
            yield return row.FacilityId;
    }

    public async Task<Establishment?> FetchEstablishmentAsync(
        string facilityId,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{EstabPath}?facilityID={Uri.EscapeDataString(facilityId)}";
        logger.LogDebug("Fetching establishment {FacilityId}", facilityId);

        var html = await httpClient.GetStringAsync(url, cancellationToken);
        var document = await _parser.ParseDocumentAsync(html, cancellationToken);

        var establishment = PhillyReportParser.ParseEstablishment(
            document, Jurisdiction, facilityId);
        if (establishment is null)
        {
            logger.LogWarning("Could not parse establishment header for {FacilityId}", facilityId);
            return null;
        }

        // Enrich with richer header fields from the latest full report
        // (phone, type, corporate officer) when one is available.
        var inspectionIds = PhillyReportParser.ReadReportInspectionIds(document);
        var firstId = inspectionIds.FirstOrDefault();
        if (firstId is not null)
        {
            var reportUrl =
                $"{BaseUrl}{ReportPath}?inspectionID={Uri.EscapeDataString(firstId)}&domainID={DomainId}&userID=0";
            try
            {
                var reportHtml = await httpClient.GetStringAsync(reportUrl, cancellationToken);
                var report = await _parser.ParseDocumentAsync(reportHtml, cancellationToken);
                var (name, address, phone, type, officer, licensee) =
                    PhillyReportParser.ParseReportFacility(report);

                establishment.Phone ??= phone;
                establishment.FacilityType ??= type;
                establishment.BusinessEntityName ??= officer ?? licensee;
                if (string.IsNullOrWhiteSpace(establishment.AddressLine1) && address is not null)
                    establishment.AddressLine1 = address;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Enrichment report fetch failed for {FacilityId}", facilityId);
            }
        }

        return establishment;
    }

    public async Task<IReadOnlyList<Inspection>> FetchInspectionsAsync(
        string facilityId,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{EstabPath}?facilityID={Uri.EscapeDataString(facilityId)}";
        var html = await httpClient.GetStringAsync(url, cancellationToken);
        var document = await _parser.ParseDocumentAsync(html, cancellationToken);

        // Only the latest report(s) appear here — history accumulates across crawls.
        var inspectionIds = PhillyReportParser.ReadReportInspectionIds(document);

        var inspections = new List<Inspection>();
        foreach (var inspectionId in inspectionIds)
        {
            var reportUrl =
                $"{BaseUrl}{ReportPath}?inspectionID={Uri.EscapeDataString(inspectionId)}&domainID={DomainId}&userID=0";
            var reportHtml = await httpClient.GetStringAsync(reportUrl, cancellationToken);
            var report = await _parser.ParseDocumentAsync(reportHtml, cancellationToken);

            var inspection = PhillyReportParser.ParseReport(report, inspectionId);
            if (inspection is not null)
                inspections.Add(inspection);
            else
                logger.LogWarning("Could not parse report {InspectionId}", inspectionId);
        }

        return inspections;
    }

}
