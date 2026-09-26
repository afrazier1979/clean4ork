using Clean4ork.Core.Domain;

namespace Clean4ork.Scraper.Sources;

/// <summary>
/// A source that enumerates food establishments across a region WITHOUT
/// necessarily providing scoreable violation detail. This is the "breadth"
/// seam — statewide discovery and geocoding — kept deliberately separate from
/// <see cref="IInspectionSource"/>, which is the "depth" seam that yields full
/// inspections and violations for a single jurisdiction.
///
/// The two compose: a facility-index source seeds the establishment skeleton
/// (which facilities exist, where, when last inspected, pass/fail), and an
/// inspection source later fills in the violation-level detail the scorer needs
/// for the jurisdictions where that detail is obtainable.
/// </summary>
public interface IFacilityIndexSource
{
    /// <summary>Identifier for the index source, e.g. "pa-open-data".</summary>
    string SourceName { get; }

    /// <summary>
    /// Stream every facility the source knows about. Implementations page
    /// internally; callers just enumerate.
    /// </summary>
    IAsyncEnumerable<Establishment> EnumerateEstablishmentsAsync(
        CancellationToken cancellationToken);
}
