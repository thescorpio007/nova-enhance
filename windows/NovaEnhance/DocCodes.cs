using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// Reads the document number in an email (SLV-ZUC-MS-SSM-00012, RS5-C7-ACC-AR-SSM-00012…): discipline (MS · Mechanical) and
/// type (SSM · Sample) — for sorting, grouping and filtering the results. Designed by A.HAMROUNI
/// </summary>
static class DocCodes
{
    /// <summary>From the person's field pack (construction: AR · Architecture, SSM · Sample …; other fields: their own or none).</summary>
    public static Dictionary<string, string> Disciplines => Packs.Current.Disciplines;
    public static Dictionary<string, string> Types => Packs.Current.Types;

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
