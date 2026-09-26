namespace Clean4ork.Core.Domain;

/// <summary>
/// How much inspection data backs an establishment, which determines how its
/// page renders. This is an explicit, stored property set at ingest time — not
/// something inferred at render — so it can drive the UI, the sitemap, and the
/// methodology page's coverage claims, and so it's auditable.
/// </summary>
public enum DataTier
{
    /// <summary>
    /// No usable inspection data yet (freshly discovered, or an index row with
    /// blank inspection fields). Render as "no inspection on record".
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Full violation-level detail is available (e.g. Philadelphia and other
    /// HealthSpace jurisdictions). The Clean4ork Score and Grade Card render.
    /// </summary>
    Scored = 1,

    /// <summary>
    /// Only a facility-level snapshot is available — latest inspection date plus
    /// a single overall pass/fail flag (the PA open-data index). No score is
    /// computed; a date-forward pass/fail card renders instead.
    /// </summary>
    PassFail = 2
}
