using System.Globalization;
using AngleSharp.Dom;
using Clean4ork.Core.Domain;

namespace Clean4ork.Scraper.Sources;

/// <summary>
/// Parses the Philadelphia HealthSpace pages: the establishment profile
/// (estab.cfm) and the full inspection report (_report_full.cfm). Built and
/// validated against real saved fixtures in tests/fixtures/philly/ (a routine,
/// a reinspection, and a second establishment), 2026-08.
///
/// Confirmed report structure:
///   HEADER — two cell layouts coexist:
///     (a) "label / value" in ADJACENT cells: Date, Time In, Time Out, and the
///         Risk-Factor / Repeat / Corrections counts.
///     (b) "&lt;b class='eleven'&gt;Label&lt;/b&gt;&lt;br/&gt;value" WITHIN one cell:
///         Food Facility, Address, Telephone, Establishment Type, Licensee,
///         Corporate Officer, Purpose of Inspection, Inspection Type.
///         (District/Sub sit in a nested table inside the same row.)
///   MATRIX — each item is a 5-cell row: [item#, status, description, COS, R].
///     status ∈ IN | OUT | N/A | N/O. Only OUT rows are violations.
///     COS cell == "X" → CorrectedOnSite; R cell == "X" → Repeat; else OutOfCompliance.
///     item numbers: 1–27 FIRF, 28–54 GRP, 56/56+ Philadelphia Ordinances (GRP).
///   OBSERVATIONS — an "OBSERVATIONS AND CORRECTIVE ACTIONS" section repeats
///     each item number followed by the inspector's free-text observation.
/// </summary>
public static class PhillyReportParser
{
    // ---- establishment page (estab.cfm) ----

    /// <summary>
    /// Parse the establishment profile. estab.cfm carries the facility name and
    /// address in its header ("Inspection Report" → name → address lines); the
    /// richer fields (phone, type, officer) come from a full report and are
    /// backfilled by the caller.
    /// </summary>
    public static Establishment? ParseEstablishment(
        IDocument doc, string jurisdiction, string facilityId)
    {
        // The estab page repeats the facility name/address; the first
        // occurrence after the "Inspection Report" heading is the profile.
        var name = FirstNonEmpty(doc, "h1, h2, h3, b");
        // More robust: the "Related Reports" block leads with name + address.
        var (parsedName, address, city, state, zip) = ReadEstabHeader(doc);
        name = parsedName ?? name;

        if (string.IsNullOrWhiteSpace(name))
            return null;

        return new Establishment
        {
            Jurisdiction = jurisdiction,
            SourceFacilityId = facilityId,
            Name = name!,
            Slug = Slugify(name!, zip),
            AddressLine1 = address,
            City = city,
            State = state,
            PostalCode = zip
        };
    }

    /// <summary>Collect the full-report inspection IDs listed on estab.cfm.</summary>
    public static IReadOnlyList<string> ReadReportInspectionIds(IDocument doc) =>
        doc.QuerySelectorAll("a[href*='_report_full.cfm']")
            .Select(a => a.GetAttribute("href"))
            .Where(h => h is not null)
            .Select(h => SearchResultParser.QueryParam(h!, "inspectionID"))
            .Where(id => !string.IsNullOrWhiteSpace(id))     // EE slot is empty
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---- full report (_report_full.cfm) ----

    public static Inspection? ParseReport(IDocument doc, string inspectionId)
    {
        var dateText = AdjacentValue(doc, "Date");
        if (!TryParseDate(dateText, out var date))
            return null; // no usable date → skip

        var purpose = LabelBrValue(doc, "Purpose of Inspection")
                      ?? LabelBrValue(doc, "Inspection Type");

        var inspection = new Inspection
        {
            SourceInspectionId = inspectionId,
            InspectionDate = date,
            Type = MapType(purpose),
            SummaryNotes = LabelBrValue(doc, "Inspection Type")
        };

        foreach (var v in ParseViolations(doc))
            inspection.Violations.Add(v);

        // Attach observation text by item number where present.
        var obs = ParseObservations(doc);
        foreach (var v in inspection.Violations)
            if (obs.TryGetValue(v.Code, out var text))
                v.Observation = text;

        return inspection;
    }

    /// <summary>Facility header fields from a full report (used to enrich the establishment).</summary>
    public static (string? name, string? address, string? phone, string? type,
        string? officer, string? licensee) ParseReportFacility(IDocument doc) =>
    (
        LabelBrValue(doc, "Food Facility"),
        LabelBrValue(doc, "Address"),
        LabelBrValue(doc, "Telephone"),
        LabelBrValue(doc, "Establishment Type"),
        LabelBrValue(doc, "Corporate Officer"),
        LabelBrValue(doc, "Licensee")
    );

    // ---- matrix ----

    private static IEnumerable<Violation> ParseViolations(IDocument doc)
    {
        foreach (var tr in doc.QuerySelectorAll("tr"))
        {
            // direct-child cells only
            var cells = tr.Children
                .Where(c => c.TagName is "TD" or "TH")
                .Select(c => c.TextContent.Trim())
                .ToArray();

            if (cells.Length != 5) continue;

            var code = cells[0];
            var status = cells[1];
            if (!IsItemNumber(code)) continue;
            if (!status.Equals("OUT", StringComparison.OrdinalIgnoreCase)) continue;

            var cos = cells[3].Trim().Equals("X", StringComparison.OrdinalIgnoreCase);
            var repeat = cells[4].Trim().Equals("X", StringComparison.OrdinalIgnoreCase);

            yield return new Violation
            {
                Code = code,
                Description = cells[2],
                Category = CategoryFor(code),
                Status = cos ? ComplianceStatus.CorrectedOnSite
                       : repeat ? ComplianceStatus.Repeat
                       : ComplianceStatus.OutOfCompliance
            };
        }
    }

    private static Dictionary<string, string> ParseObservations(IDocument doc)
    {
        // The observations section repeats "item# <text>". We capture the text
        // for OUT items; harmless if it also captures a few IN items.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var header = doc.QuerySelectorAll("*")
            .FirstOrDefault(e => e.TextContent.Contains(
                "OBSERVATIONS AND CORRECTIVE ACTIONS", StringComparison.OrdinalIgnoreCase));
        if (header is null) return map;

        var scope = header.Closest("table") ?? doc.Body!;
        foreach (var tr in scope.QuerySelectorAll("tr"))
        {
            var cells = tr.Children
                .Where(c => c.TagName is "TD" or "TH")
                .Select(c => c.TextContent.Trim())
                .Where(t => t.Length > 0)
                .ToArray();
            if (cells.Length >= 2 && IsItemNumber(cells[0]))
            {
                var text = string.Join(' ', cells.Skip(1)).Trim();
                if (text.Length > 0 && !map.ContainsKey(cells[0]))
                    map[cells[0]] = text;
            }
        }
        return map;
    }

    // ---- header helpers ----

    /// <summary>"label" then value in the NEXT cell (Date / Time In / Time Out).</summary>
    private static string? AdjacentValue(IDocument doc, string label)
    {
        var cells = doc.QuerySelectorAll("td").ToArray();
        for (var i = 0; i < cells.Length - 1; i++)
            if (cells[i].TextContent.Trim() == label)
                return cells[i + 1].TextContent.Trim();
        return null;
    }

    /// <summary>"&lt;b&gt;Label&lt;/b&gt;&lt;br/&gt;value" inside one cell.</summary>
    private static string? LabelBrValue(IDocument doc, string label)
    {
        var b = doc.QuerySelectorAll("b")
            .FirstOrDefault(e => e.TextContent.Trim() == label);
        var td = b?.Closest("td");
        if (td is null) return null;

        // Drop nested tables (District/Sub live in one) so we read this cell only.
        var clone = (IElement)td.Clone(true);
        foreach (var nested in clone.QuerySelectorAll("table").ToArray())
            nested.Remove();

        var lines = clone.TextContent
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        // First line is the label; the value is the remainder joined.
        if (lines.Length >= 2 && lines[0] == label)
            return string.Join(' ', lines.Skip(1)).Trim();
        return null;
    }

    private static (string? name, string? address, string? city, string? state, string? zip)
        ReadEstabHeader(IDocument doc)
    {
        // estab.cfm shows: "Inspection Report" then facility name then address,
        // "CITY, STATE ZIP". Read the first plausible block.
        var lines = (doc.Body?.TextContent ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var idx = lines.FindIndex(l =>
            l.Equals("Inspection Report", StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx + 2 >= lines.Count)
            return (null, null, null, null, null);

        var name = lines[idx + 1];
        var addr = lines[idx + 2];
        string? city = null, state = null, zip = null;

        // The following line is usually "CITY, STATE ZIP".
        if (idx + 3 < lines.Count)
        {
            var cityLine = lines[idx + 3];
            var comma = cityLine.IndexOf(',');
            if (comma > 0)
            {
                city = cityLine[..comma].Trim();
                var rest = cityLine[(comma + 1)..].Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (rest.Length > 0) state = rest[0];
                if (rest.Length > 1) zip = rest[1];
            }
        }
        return (name, addr, city, state, zip);
    }

    private static string? FirstNonEmpty(IDocument doc, string selector) =>
        doc.QuerySelectorAll(selector)
            .Select(e => e.TextContent.Trim())
            .FirstOrDefault(t => t.Length > 0);

    // ---- mapping ----

    private static bool IsItemNumber(string s)
    {
        s = s.Trim().TrimEnd('+');
        return s.Length > 0 && s.All(char.IsDigit);
    }

    private static ViolationCategory CategoryFor(string code)
    {
        var digits = new string(code.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) && n is >= 1 and <= 27
            ? ViolationCategory.FoodborneIllnessRiskFactor
            : ViolationCategory.GoodRetailPractice;
    }

    private static InspectionType MapType(string? purpose) => purpose?.ToLowerInvariant() switch
    {
        null => InspectionType.Unknown,
        var p when p.Contains("reinspection") => InspectionType.Reinspection,
        var p when p.Contains("complaint") => InspectionType.Complaint,
        var p when p.Contains("follow") => InspectionType.FollowUp,
        var p when p.Contains("initial") => InspectionType.Initial,
        var p when p.Contains("inspection") => InspectionType.Routine,
        _ => InspectionType.Unknown
    };

    private static bool TryParseDate(string? s, out DateOnly date)
    {
        date = default;
        return !string.IsNullOrWhiteSpace(s) &&
               DateOnly.TryParseExact(s.Trim(), "MM/dd/yyyy",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static string Slugify(string name, string? zip)
    {
        var lowered = name.Trim().ToLowerInvariant();
        var chars = lowered.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return string.IsNullOrWhiteSpace(zip) ? slug : $"{slug}-{zip.Trim()}";
    }
}
