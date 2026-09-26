namespace Clean4ork.Scraper.Sources;

/// <summary>
/// One row from a HealthSpace search results page.
///
/// Search pages are richer than expected: each row carries the facility name,
/// full address, AND the facilityID + latest inspectionID. That means the
/// initial crawl can go straight from search → _report_full and skip
/// estab.cfm entirely, roughly halving request volume. estab.cfm is only
/// needed to pick up the secondary (Environmental/EE) report.
/// </summary>
public record FacilitySearchResult(
    string FacilityId,
    string Name,
    string? AddressLine,
    string? City,
    string? State,
    string? PostalCode,
    DateOnly? LastInspectionDate,
    string? LastInspectionId);
