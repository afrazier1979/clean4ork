using System.Globalization;
using AngleSharp.Dom;

namespace Clean4ork.Scraper.Sources;

/// <summary>
/// Parses HealthSpace search result pages. Built against live HTML captured
/// 2026-07 (see tests/fixtures/philly/). The markup is flat and unsemantic:
///
///   &lt;b&gt;100 Facilities matched &lt;/b&gt;
///   Displaying results 1 &amp;ndash; 20 of 100
///   &lt;a href="estab.cfm?facilityID={guid}"&gt;&lt;b&gt;NAME&lt;/b&gt;&lt;/a&gt;
///   &lt;div style="margin-bottom:10px;"&gt;
///     1800  Chestnut  ST   &lt;br /&gt;
///     PHILADELPHIA, PA 19103
///     &lt;div style="color:green;"&gt;
///       Last Inspection Date:
///       &lt;a href="estab.cfm?facilityID={guid}&amp;inspectionID={guid}&amp;inspType=Food"&gt;05/14/2026&lt;/a&gt;
///     &lt;/div&gt;
///   &lt;/div&gt;
///
/// Facility anchors are distinguished from "last inspection date" anchors by
/// the absence of an inspectionID in the query string.
/// </summary>
public static class SearchResultParser
{
    /// <summary>
    /// Total the page reports as matching. HealthSpace caps this at 100 and
    /// truncates silently, so a value of exactly 100 means "at least 100".
    /// </summary>
    public static int ParseMatchCount(IDocument document)
    {
        var text = document.Body?.TextContent ?? string.Empty;

        var idx = text.IndexOf("Facilities matched", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;

        var head = text[..idx].TrimEnd();
        var lastSpace = head.LastIndexOf(' ');
        var numberText = lastSpace >= 0 ? head[(lastSpace + 1)..] : head;

        return int.TryParse(numberText, out var n) ? n : 0;
    }

    public static IReadOnlyList<FacilitySearchResult> ParseRows(IDocument document)
    {
        var rows = new List<FacilitySearchResult>();

        foreach (var anchor in document.QuerySelectorAll("a[href*='estab.cfm']"))
        {
            var href = anchor.GetAttribute("href");
            if (href is null) continue;

            var facilityId = QueryParam(href, "facilityID");
            if (facilityId is null) continue;

            // Skip the "Last Inspection Date" anchor — it points at the same
            // facility but carries an inspectionID.
            if (QueryParam(href, "inspectionID") is not null) continue;

            var name = anchor.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            // The address block is the next element sibling div.
            var block = anchor.NextElementSibling;
            while (block is not null && block.TagName != "DIV")
                block = block.NextElementSibling;

            string? addressLine = null, city = null, state = null, postalCode = null;
            DateOnly? lastDate = null;
            string? lastInspectionId = null;

            if (block is not null)
            {
                // Green sub-div holds the last inspection date + link.
                var dateAnchor = block.QuerySelector("a[href*='inspectionID=']");
                if (dateAnchor is not null)
                {
                    lastInspectionId = QueryParam(dateAnchor.GetAttribute("href") ?? "", "inspectionID");

                    if (DateOnly.TryParseExact(
                            dateAnchor.TextContent.Trim(),
                            "MM/dd/yyyy",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var parsed))
                    {
                        lastDate = parsed;
                    }
                }

                // Remove the green div so the remaining text is just the address.
                var green = dateAnchor?.ParentElement;
                green?.Remove();

                var addressText = block.TextContent;
                var lines = addressText
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => CollapseWhitespace(l))
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                if (lines.Count > 0)
                    addressLine = lines[0];

                // Last line looks like "PHILADELPHIA, PA 19103".
                var cityLine = lines.LastOrDefault(l => l.Contains(','));
                if (cityLine is not null)
                {
                    var commaIdx = cityLine.IndexOf(',');
                    city = cityLine[..commaIdx].Trim();

                    var rest = cityLine[(commaIdx + 1)..].Trim().Split(' ',
                        StringSplitOptions.RemoveEmptyEntries);

                    if (rest.Length > 0) state = rest[0];
                    if (rest.Length > 1) postalCode = rest[1];

                    if (lines.Count > 1 && ReferenceEquals(cityLine, lines[0]))
                        addressLine = null;
                }
            }

            rows.Add(new FacilitySearchResult(
                FacilityId: facilityId,
                Name: name,
                AddressLine: addressLine,
                City: city,
                State: state,
                PostalCode: postalCode,
                LastInspectionDate: lastDate,
                LastInspectionId: lastInspectionId));
        }

        return rows;
    }

    private static string CollapseWhitespace(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static string? QueryParam(string href, string name)
    {
        var key = name + "=";
        var idx = href.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var value = href[(idx + key.Length)..];
        var amp = value.IndexOf('&');
        if (amp >= 0) value = value[..amp];

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
