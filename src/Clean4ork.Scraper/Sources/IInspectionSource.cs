using Clean4ork.Core.Domain;

namespace Clean4ork.Scraper.Sources;

/// <summary>
/// Abstracts a per-jurisdiction inspection data source. Implementations
/// handle the city-specific HTML parsing and return canonical domain objects.
///
/// This interface is the multi-city seam: the HealthSpace platform hosts
/// 100+ municipalities on identical templates, so most future jurisdictions
/// are a configuration (base URL + domainID) rather than a new parser.
/// </summary>
public interface IInspectionSource
{
    /// <summary>Stable identifier for the source, e.g. "philadelphia-pa".</summary>
    string Jurisdiction { get; }

    /// <summary>
    /// Enumerate all establishment IDs in the given ZIP code from the search page.
    /// </summary>
    IAsyncEnumerable<string> ListFacilityIdsForPostalCodeAsync(
        string postalCode,
        CancellationToken cancellationToken);

    /// <summary>Fetch and parse an establishment's profile (no inspections).</summary>
    Task<Establishment?> FetchEstablishmentAsync(
        string facilityId,
        CancellationToken cancellationToken);

    /// <summary>Fetch and parse all inspections (and their violations) for an establishment.</summary>
    Task<IReadOnlyList<Inspection>> FetchInspectionsAsync(
        string facilityId,
        CancellationToken cancellationToken);
}
