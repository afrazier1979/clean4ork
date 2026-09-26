using Clean4ork.Core.Domain;

namespace Clean4ork.Core.Scoring;

/// <summary>
/// How confidently a pass/fail snapshot describes the present.
/// </summary>
public enum SnapshotFreshness
{
    /// <summary>Recent enough to state plainly (still just a snapshot, not a grade).</summary>
    Recent,

    /// <summary>Old enough that it must be framed as historical, not current.</summary>
    Stale,

    /// <summary>So old it should not be presented as a status at all.</summary>
    Expired
}

/// <summary>
/// The render-ready form of a pass/fail establishment, with the honesty
/// guardrails baked in so no surface can accidentally present a single blunt
/// flag as if it were a current, graded verdict.
///
/// The guardrails:
///   1. NEVER emit a letter/number that resembles a Clean4ork Score. A pass/fail
///      area is explicitly not scored; conflating the two misleads users and
///      undercuts the methodology.
///   2. ALWAYS lead with the inspection date. A pass/fail flag is only
///      meaningful as of a moment; a failure from many months ago is not a
///      statement about the restaurant today.
///   3. Decay the framing with age. Recent → state it; stale → past tense with
///      the date foregrounded; expired → don't assert a status, just point to
///      the official record.
///   4. Word "fail" carefully. A single out-of-compliance snapshot commonly
///      reflects issues corrected during the very same visit, so the copy says
///      "was out of compliance" (a factual event) rather than "is failing" (an
///      unsupported present-tense judgement).
/// </summary>
public sealed record PassFailSnapshot(
    string Headline,
    string SubText,
    SnapshotFreshness Freshness,
    bool? Passed,
    DateOnly? AsOf)
{
    /// <summary>Recent up to here; stale between here and Expired; expired beyond.</summary>
    public const int StaleAfterDays = 180;
    public const int ExpiredAfterDays = 540; // ~18 months; snapshot source is "last 24 months"

    /// <summary>
    /// Build the presentation for a PassFail establishment as-of the given date.
    /// Callers should only pass establishments whose DataTier is PassFail; other
    /// tiers get their own rendering path.
    /// </summary>
    public static PassFailSnapshot ForEstablishment(Establishment establishment, DateOnly asOf)
    {
        var date = establishment.SnapshotInspectionDate;
        var passed = establishment.SnapshotPassed;

        if (date is null)
        {
            return new PassFailSnapshot(
                Headline: "No inspection on record",
                SubText: "We don't have a recent inspection for this location. "
                       + "Check the official health department record for the latest.",
                Freshness: SnapshotFreshness.Expired,
                Passed: null,
                AsOf: null);
        }

        var ageDays = asOf.DayNumber - date.Value.DayNumber;
        var freshness = ageDays > ExpiredAfterDays ? SnapshotFreshness.Expired
                      : ageDays > StaleAfterDays ? SnapshotFreshness.Stale
                      : SnapshotFreshness.Recent;

        var when = date.Value.ToString("MMMM d, yyyy");

        // Expired: don't assert a current status at all.
        if (freshness == SnapshotFreshness.Expired)
        {
            return new PassFailSnapshot(
                Headline: $"Last inspected {when}",
                SubText: "This is older than we'll summarize as a current status. "
                       + "See the official health department record for up-to-date results.",
                Freshness: freshness,
                Passed: passed,
                AsOf: date);
        }

        // Guardrail #2 + #4: date-forward, event-framed, never present-tense judgement.
        var (headline, sub) = passed switch
        {
            true => (
                $"Passed its {when} inspection",
                DateForwardNote(freshness, when,
                    "This location met overall compliance at its most recent inspection.")),

            false => (
                $"Was out of compliance at its {when} inspection",
                DateForwardNote(freshness, when,
                    "Being out of compliance on one visit often reflects issues noted — and "
                  + "sometimes corrected — during that inspection. It isn't a judgement about "
                  + "the restaurant today.")),

            null => (
                $"Inspected {when}",
                DateForwardNote(freshness, when,
                    "The overall result wasn't recorded in the data we have for this area.")),
        };

        return new PassFailSnapshot(headline, sub, freshness, passed, date);
    }

    /// <summary>
    /// This establishment is in a pass/fail area and therefore has no Clean4ork
    /// Score. Guardrail #1: surfaces call this to decide whether to render the
    /// Grade Card at all — it must always be false here.
    /// </summary>
    public bool HasScore => false;

    private static string DateForwardNote(SnapshotFreshness freshness, string when, string body)
    {
        var lead = freshness == SnapshotFreshness.Stale
            ? $"As of {when} (the most recent inspection we have, now several months old): "
            : $"As of {when}: ";
        return lead + body + " This area isn't graded — we show the inspection result, not a Clean4ork Score.";
    }
}
