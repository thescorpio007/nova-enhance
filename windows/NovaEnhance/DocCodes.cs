using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// Reads the document number in an email (SLV-ZUC-MS-SSM-00012, RS5-C7-ACC-AR-SSM-00012…): discipline (MS · Mechanical) and
/// type (SSM · Sample) — for sorting, grouping and filtering the results. Designed by A.HAMROUNI
/// </summary>
static class DocCodes
{
    public static readonly Dictionary<string, string> Disciplines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AR"] = "Architecture", ["ID"] = "Interior design", ["LS"] = "Landscape", ["ST"] = "Structure", ["MS"] = "Mechanical",
        ["ME"] = "MEP", ["MEP"] = "MEP", ["EL"] = "Electrical", ["PL"] = "Plumbing", ["FF"] = "Firefighting", ["FS"] = "Fire safety",
        ["HV"] = "HVAC", ["CW"] = "Civil works", ["CV"] = "Civil", ["CI"] = "Civil", ["IN"] = "Infrastructure", ["PM"] = "Project management",
        ["QA"] = "Quality", ["QC"] = "Quality", ["HS"] = "HSE", ["FA"] = "Façade", ["FC"] = "Façade", ["LT"] = "Lighting", ["LI"] = "Lighting",
        ["AV"] = "Audio-visual", ["IT"] = "ICT", ["ICT"] = "ICT", ["SG"] = "Signage", ["VT"] = "Vertical transport", ["LF"] = "Lifts",
        ["GE"] = "General", ["GN"] = "General", ["SU"] = "Survey", ["GT"] = "Geotechnical", ["WP"] = "Waterproofing", ["KT"] = "Kitchen"
    };

    public static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SSM"] = "Sample", ["MAR"] = "Material approval", ["MAT"] = "Material", ["MIR"] = "Material inspection", ["WIR"] = "Work inspection",
        ["RFI"] = "RFI", ["SDS"] = "Shop drawing", ["SD"] = "Shop drawing", ["DRW"] = "Drawing", ["MSS"] = "Method statement", ["MST"] = "Method statement",
        ["MS"] = "Method statement", ["NCR"] = "Non-conformance", ["TQ"] = "Technical query", ["LTR"] = "Letter", ["TRN"] = "Transmittal",
        ["SI"] = "Site instruction", ["EI"] = "Engineer's instruction", ["PQ"] = "Prequalification", ["CAL"] = "Calculation", ["RPT"] = "Report",
        ["MOM"] = "Minutes", ["DET"] = "Detail", ["GEN"] = "General", ["COM"] = "Commercial", ["VO"] = "Variation", ["SUB"] = "Submittal",
        ["TS"] = "Technical submittal", ["DS"] = "Design submittal", ["ASB"] = "As-built", ["SNG"] = "Snag", ["HO"] = "Handover", ["IR"] = "Inspection"
    };

    static readonly Regex Num = new(@"\b[A-Z][A-Z0-9]{1,5}(?:-[A-Z0-9]{1,6}){2,7}-\d{3,6}", RegexOptions.Compiled);
    static readonly Dictionary<long, (string No, string Disc, string Type)> cache = new();

    /// <summary>The first document number in the subject (or else the attachment names) and its discipline / type codes ("" when none).</summary>
    public static (string No, string Disc, string Type) Of(MailHit h)
    {
        lock (cache) if (cache.TryGetValue(h.Id, out var c)) return c;
        var r = Parse((h.Subject ?? "").ToUpperInvariant());
        if (r.No.Length == 0) r = Parse((h.AttNames ?? "").ToUpperInvariant());
        lock (cache) { if (cache.Count > 20000) cache.Clear(); cache[h.Id] = r; }
        return r;
    }

    static (string No, string Disc, string Type) Parse(string s)
    {
        var m = Num.Match(s);
        if (!m.Success) return ("", "", "");
        var parts = m.Value.Split('-');
        // …-[discipline]-(zone…)-[type]-[number]: the type is just before the number, the discipline the nearest known code before it.
        var type = Regex.IsMatch(parts[^2], "^[A-Z]{2,4}$") ? parts[^2] : "";
        var disc = parts.Take(parts.Length - 2).Reverse().FirstOrDefault(p => Disciplines.ContainsKey(p)) ?? "";
        return (m.Value, disc, type);
    }

    public static string DiscLabel(string code) => code.Length == 0 ? "Other" : Disciplines.TryGetValue(code, out var n) ? $"{code} · {n}" : code;
    public static string TypeLabel(string code) => code.Length == 0 ? "Other" : Types.TryGetValue(code, out var n) ? $"{code} · {n}" : code;

    /// <summary>Sorting key of a document number: same base together, revisions in order.</summary>
    public static string SortNo(string no) => Regex.Replace(no, @"\d+", d => d.Value.PadLeft(8, '0'));
}
