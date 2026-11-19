using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>
/// ✎ My log settings — what you chose with ✎ Edit in the 📋 log: hidden projects / types / disciplines, project order, your own types,
/// discipline groups, saved periods, towers · zones · levels per project, the status chips. Kept in Documents\Nova Enhance\Rules\log-settings.json
/// (so it is in every full backup), sent to the phone with the hourly update, and shown read-only in the 🧠 Nova Brain. Designed by A.HAMROUNI
/// </summary>
static class LogPrefs
{
    static string FilePath => Path.Combine(Paths.Root, "Rules", "log-settings.json");
    static readonly object L = new();
    static JsonObject cache; static DateTime cacheAt;

    public static readonly string[] AllStatuses = { "Open", "Overdue", "Closed", "Code3/4", "Code 1", "Code 2", "Reviewing", "Replied", "Draft", "Superseded", "Received" };

    static JsonObject Defaults() => new()
    {
        ["hiddenProjects"] = new JsonArray(), ["projectOrder"] = new JsonArray(), ["hiddenTypes"] = new JsonArray(), ["customTypes"] = new JsonArray(),
        ["periods"] = new JsonArray(), ["places"] = new JsonObject(), ["hiddenDiscs"] = new JsonArray(),
        ["discGroups"] = new JsonArray(
            G("Architecture", "AR", "ARC", "CW", "ID", "LS"), G("MEP", "MEP", "ES", "MS", "PLB", "FF", "ME"), G("Structure", "ST"), G("HSE", "HSE")),
        ["statuses"] = new JsonArray("Open", "Overdue", "Closed", "Code3/4")
    };

    static JsonObject G(string name, params string[] codes) => new() { ["name"] = name, ["codes"] = new JsonArray(codes.Select(c => (JsonNode)c).ToArray()) };

    /// <summary>The settings (a copy): your file, with every missing part filled from the defaults.</summary>
    public static JsonObject Get()
    {
        lock (L)
        {
            try
            {
                var t = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                if (cache == null || t != cacheAt)
                {
                    var d = Defaults();
                    if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject f)
                        foreach (var (k, v) in f) d[k] = v?.DeepClone();
                    cache = d; cacheAt = t;
                }
            }
            catch (Exception e) { Log.Warn("log settings", e.Message); cache ??= Defaults(); }
            return cache.DeepClone().AsObject();
        }
    }

    public static void Save(JsonObject o)
    {
        lock (L)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            o["saved"] = DateTime.Now.ToString("s");
            File.WriteAllText(FilePath, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            cache = null;
        }
    }

    static List<string> L_(JsonObject o, string k) => (o[k] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? new();

    public static HashSet<string> HiddenProjects => L_(Get(), "hiddenProjects").ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> HiddenTypes => L_(Get(), "hiddenTypes").ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> HiddenDiscs => L_(Get(), "hiddenDiscs").ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static List<string> ProjectOrder => L_(Get(), "projectOrder");

    /// <summary>Discipline groups: name → codes (Architecture = AR + ARC + CW + ID + LS …).</summary>
    public static Dictionary<string, HashSet<string>> Groups()
    {
        var d = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in (Get()["discGroups"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            var n = g["name"]?.ToString() ?? "";
            if (n.Length > 0) d[n] = ((g["codes"] as JsonArray)?.Select(x => x?.ToString() ?? "") ?? Array.Empty<string>()).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return d;
    }

    /// <summary>Does this item's discipline match the choice (a code, or a group name)?</summary>
    public static bool DiscFits(string choice, string disc, Dictionary<string, HashSet<string>> groups) =>
        choice.Equals(disc, StringComparison.OrdinalIgnoreCase) || (groups.TryGetValue(choice, out var g) && g.Contains(disc));

    /// <summary>Your own types (✎ Edit on the Type row): by a code in the document number, or by words in the subject.</summary>
    public static List<SubmittalLog.TypeDef> CustomTypes()
    {
        var l = new List<SubmittalLog.TypeDef>();
        foreach (var t in (Get()["customTypes"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            var code = (t["code"]?.ToString() ?? "").Trim().ToUpperInvariant();
            if (code.Length < 2) continue;
            var label = t["label"]?.ToString() ?? code;
            var close = t["close"]?.ToString() is { Length: > 0 } c ? c : "reply";
            int.TryParse(t["overdue"]?.ToString(), out var od);
            var words = (t["words"]?.ToString() ?? "").Split(',', ';').Select(w => w.Trim()).Where(w => w.Length > 1).ToList();
            var done = (t["done"]?.ToString() ?? "").Split(',', ';').Select(w => w.Trim()).Where(w => w.Length > 1).ToList();
            string Rx(IEnumerable<string> ws) => string.Join("|", ws.Select(w => @"\b" + System.Text.RegularExpressions.Regex.Escape(w).Replace("\\ ", @"\s+") + @"\b"));
            if ((t["find"]?.ToString() ?? "code") == "code")
                l.Add(new SubmittalLog.TypeDef(code, label, "code", close is "done" ? "reply" : close, od, Words: Rx(words.Append(code))));
            else if (words.Count > 0)
                l.Add(new SubmittalLog.TypeDef(code, label, "ref", close, od, Rx(words), string.Join(";", words.Select(w => "%" + w + "%")), done.Count > 0 ? Rx(done) : "", Rx(words.Append(code))));
        }
        return l;
    }

    /// <summary>Read-only view for the 🧠 Nova Brain window.</summary>
    public static JsonObject BrainSection()
    {
        var o = Get();
        var rules = new JsonArray();
        int n = 0;
        void R(string title, string text) { if (text.Length > 0) rules.Add(new JsonObject { ["id"] = $"ML-{++n:00}", ["title"] = title, ["text"] = text, ["by"] = "you · ✎ Edit in the log" }); }
        string J(string k) => string.Join(", ", L_(o, k));
        R("Hidden projects", J("hiddenProjects"));
        R("Project order", J("projectOrder"));
        R("Hidden types", J("hiddenTypes"));
        foreach (var t in CustomTypes()) R("Your type " + t.Code, $"{t.Label} · found by {(t.Find == "code" ? "the code in the document number" : "words in the subject")} · closed by {t.Close} · overdue after {t.Overdue} days");
        foreach (var (g, c) in Groups()) R("Discipline group " + g, string.Join(" + ", c));
        R("Hidden disciplines", J("hiddenDiscs"));
        foreach (var p in (o["periods"] as JsonArray ?? new()).OfType<JsonObject>()) R("Period " + p["name"], $"{p["from"]} → {p["to"]}");
        foreach (var (p, v) in o["places"] as JsonObject ?? new()) if (v is JsonObject pv) R("Places " + p, string.Join(" · ", pv.Select(x => $"{x.Key}: {x.Value}")));
        R("Status chips", J("statuses"));
        return new JsonObject { ["id"] = "mylog", ["icon"] = "✎", ["name"] = "My log settings", ["readonly"] = true, ["tags"] = new JsonArray("submittal"), ["rules"] = rules };
    }
}
