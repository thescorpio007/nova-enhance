using System.Text;
using System.Text.Json;

namespace NovaEnhance;

/// <summary>
/// 📏 Nova Rules — one rule system for every feature (☀ Morning brief, 📁 Email projects; Search and Files later).
/// A rule: all its conditions must be true (AND); each condition is "any of" a list (OR); "except when" cancels it; the first rule
/// that matches decides. Kept in Documents\Nova Enhance\Rules (one file per feature, with undo). Designed by A.HAMROUNI
/// </summary>
class NRule
{
    public bool On = true;
    public List<string> From = new(), Subject = new(), Text = new(), Project = new(), Except = new();
    public string In = "any";        // any · to (I'm in To) · cc (I'm only in Cc)
    public bool NoQuestion, Auto;
    public string Then = "";         // brief: urgent · waiting · fyi · hide · include — projects: a project code or "unsorted"
    public string Why = "";
    public string Source = "";       // words (made by the AI from your words) · you (added / edited by you)

    public NRule Copy() => JsonSerializer.Deserialize<NRule>(JsonSerializer.Serialize(this, RuleBooks.J), RuleBooks.J);
}

class RuleBook
{
    public List<NRule> Rules = new();
    public List<string> First = new();   // brief: projects shown first
    public string AiCheck = "";          // what the AI judges for the emails no rule decides
    public string Instructions = "";     // your words
    public RuleBook Copy() => JsonSerializer.Deserialize<RuleBook>(JsonSerializer.Serialize(this, RuleBooks.J), RuleBooks.J);
}

static class RuleBooks
{
    internal static readonly JsonSerializerOptions J = new() { IncludeFields = true, WriteIndented = true };
    public static readonly (string Id, string Label, bool Ready)[] Features =
    {
        ("brief", "☀ Morning brief", true), ("projects", "📁 Email projects", true), ("search", "🔍 Search for email", false), ("files", "📂 File search", false)
    };

    static string Dir { get { var d = Path.Combine(Paths.Root, "Rules"); try { Directory.CreateDirectory(d); } catch { } return d; } }
    static string FileOf(string f) => Path.Combine(Dir, f + ".json");
    static readonly Dictionary<string, RuleBook> cache = new();

    public static RuleBook Load(string feature)
    {
        lock (cache)
        {
            if (cache.TryGetValue(feature, out var c)) return c;
            RuleBook b = null;
            try { if (File.Exists(FileOf(feature))) b = JsonSerializer.Deserialize<RuleBook>(File.ReadAllText(FileOf(feature)), J); } catch (Exception e) { Log.Warn("rules", feature + ": " + e.Message); }
            b ??= feature == "brief" ? BriefRules.FromOldSettings() : new RuleBook();
            cache[feature] = b;
            return b;
        }
    }

    /// <summary>Saves (the previous version is kept for ↩ Undo).</summary>
    public static void Save(string feature, RuleBook b)
    {
        lock (cache)
        {
            try
            {
                var f = FileOf(feature);
                if (File.Exists(f))
                {
                    var hist = Path.Combine(Dir, "history"); Directory.CreateDirectory(hist);
                    File.Copy(f, Path.Combine(hist, $"{feature}-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
                    foreach (var old in Directory.GetFiles(hist, feature + "-*.json").OrderByDescending(x => x).Skip(20)) File.Delete(old);
                }
                File.WriteAllText(f, JsonSerializer.Serialize(b, J));
            }
            catch (Exception e) { Log.Warn("rules", "save: " + e.Message); }
            cache[feature] = b;
        }
        if (feature == "projects") MailProjects.SortAgain();
    }

    /// <summary>↩ The version before the last save (null when there is none).</summary>
    public static RuleBook Undo(string feature)
    {
        try
        {
            var hist = Path.Combine(Dir, "history");
            var last = Directory.Exists(hist) ? Directory.GetFiles(hist, feature + "-*.json").OrderByDescending(x => x).FirstOrDefault() : null;
            if (last == null) return null;
            var b = JsonSerializer.Deserialize<RuleBook>(File.ReadAllText(last), J);
            File.Delete(last);
            lock (cache) { File.WriteAllText(FileOf(feature), JsonSerializer.Serialize(b, J)); cache[feature] = b; }
            return b;
        }
        catch { return null; }
    }

    static bool AnyOf(string text, List<string> words) => words.Count == 0 || words.Any(w => w.Trim().Length > 0 && (text ?? "").Contains(w.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Does this rule match this email? (all conditions true, none of the exceptions)</summary>
    public static bool Matches(NRule r, MailHit h, bool inTo, string project)
    {
        if (!r.On) return false;
        if (r.Project.Count > 0 && !r.Project.Contains(project ?? "", StringComparer.OrdinalIgnoreCase) && !r.Project.Any(p => (h.Subject ?? "").Contains(p, StringComparison.OrdinalIgnoreCase))) return false;
        if (!AnyOf((h.FromName ?? "") + " " + (h.FromAddr ?? ""), r.From)) return false;
        if (!AnyOf((h.Subject ?? "") + " " + (h.AttNames ?? ""), r.Subject)) return false;
        if (!AnyOf(h.NewPart, r.Text)) return false;
        if (r.NoQuestion && h.NewPart.Contains('?')) return false;
        if (r.In == "to" && !inTo) return false;
        if (r.In == "cc" && inTo) return false;
        if (r.Auto && !BriefLogic.IsAuto(h)) return false;
        if (r.Except.Count > 0 && AnyOf(h.NewPart + " " + h.Subject, r.Except)) return false;
        return true;
    }

    public static string ThenText(string feature, string then) => feature == "brief"
        ? then switch { "urgent" => "🔴 urgent", "waiting" => "🟠 waiting", "fyi" => "⚪ FYI", "hide" => "✗ hide", "include" => "➕ include", _ => then }
        : then == "unsorted" ? "❔ unsorted" : "📁 " + then;

    public static string When(NRule r)
    {
        var c = new List<string>();
        string L(List<string> x) => string.Join(" / ", x.Take(4)) + (x.Count > 4 ? $" +{x.Count - 4}" : "");
        if (r.Project.Count > 0) c.Add("project " + L(r.Project));
        if (r.From.Count > 0) c.Add("sender " + L(r.From));
        if (r.Subject.Count > 0) c.Add("subject/attachment " + L(r.Subject));
        if (r.Text.Count > 0) c.Add("text " + L(r.Text));
        if (r.In == "to") c.Add("I'm in To"); else if (r.In == "cc") c.Add("I'm only in Cc");
        if (r.NoQuestion) c.Add("no question");
        if (r.Auto) c.Add("automatic sender");
        return c.Count == 0 ? "any email" : string.Join("  AND  ", c);
    }

    public static string Describe(string feature, RuleBook b)
    {
        var sb = new StringBuilder();
        if (b.First.Count > 0) sb.AppendLine("⬆ Show first: " + string.Join(", ", b.First));
        int i = 1;
        foreach (var r in b.Rules)
            sb.AppendLine($"{(r.On ? "" : "(off) ")}{i++}. If {When(r)}{(r.Except.Count > 0 ? " — except when text has " + string.Join(" / ", r.Except) : "")} → {ThenText(feature, r.Then)}{(r.Why.Length > 0 ? "   (" + r.Why + ")" : "")}");
        if (b.AiCheck.Length > 0) sb.AppendLine("✦ The AI judges the emails no rule decides: " + b.AiCheck);
        return sb.Length == 0 ? "(no rules)" : sb.ToString().TrimEnd();
    }
}
