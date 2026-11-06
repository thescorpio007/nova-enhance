using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>Everything Nova makes lives in Documents\Nova Enhance.</summary>
static class Paths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Nova Enhance");
    public static string Backups => Sub("Backups");
    public static string History => Sub("History");
    public static string Originals => Sub("Originals");
    public static string Briefs => Sub("Briefs");
    public static string Drafts => Sub("Drafts");
    public static string Logs => Sub("Logs");
    public static string SearchResults => Sub("Search Results");
    public static string MailIndexDocs => Sub("Mail Index");
    /// <summary>Used when "keep the mail index on this PC only" is on (not synced by OneDrive).</summary>
    public static string MailIndexLocal => Make(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova Enhance", "Mail Index"));
    public static string Cache => Make(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova Enhance", "cache"));

    static string Sub(string n) => Make(Path.Combine(Root, n));
    static string Make(string p) { try { Directory.CreateDirectory(p); } catch { } return p; }

    public static void Ensure()
    {
        Make(Root);
        foreach (var s in new[] { Backups, History, Originals, Briefs, Drafts, Logs, MailIndexDocs }) { }
    }
}

static class Shell
{
    public static void Open(string path)
    {
        try
        {
            // A Nova folder that hasn't been used yet (e.g. Search Results before the first export) is created instead of failing.
            if (path.StartsWith(Paths.Root, StringComparison.OrdinalIgnoreCase) && !File.Exists(path) && !Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) { Log.Error("open " + path, e); }
    }

    public static void Select(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true }); } catch { }
    }
}

/// <summary>
/// Settings in Documents\Nova Enhance\settings.json. Key names match the phone app so a backup moves both ways.
/// API keys and the backup password are locked to this Windows account (DPAPI).
/// </summary>
static class Store
{
    static readonly object L = new();
    static JsonObject d;
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nova Enhance · A.HAMROUNI");
    static string FilePath => Path.Combine(Paths.Root, "settings.json");

    static JsonObject D { get { if (d == null) Load(); return d; } }

    public static void Load()
    {
        lock (L)
        {
            try { if (File.Exists(FilePath)) d = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject; } catch (Exception e) { Log.Error("settings load", e); }
            d ??= new JsonObject();
        }
    }

    static void Save()
    {
        lock (L)
        {
            try
            {
                Directory.CreateDirectory(Paths.Root);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, D.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception e) { Log.Error("settings save", e); }
        }
    }

    public static List<KeyValuePair<string, JsonNode>> Snapshot()
    {
        lock (L) return D.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value?.DeepClone())).ToList();
    }

    public static void SetNode(string k, JsonNode v) { lock (L) D[k] = v; Save(); }

    public static string Str(string k, string def = "")
    {
        lock (L)
        {
            var n = D[k];
            if (n == null) return def;
            try { return n.GetValue<string>(); } catch { return n.ToJsonString().Trim('"'); }
        }
    }

    public static void Set(string k, string v) { lock (L) D[k] = v ?? ""; Save(); }

    public static bool Bool(string k, bool def)
    {
        lock (L)
        {
            var n = D[k];
            if (n == null) return def;
            try { return n.GetValue<bool>(); } catch { return def; }
        }
    }

    public static void SetBool(string k, bool v) { lock (L) D[k] = v; Save(); }

    public static long Long(string k, long def)
    {
        lock (L)
        {
            var n = D[k];
            if (n == null) return def;
            try { return n.GetValue<long>(); } catch { }
            try { return n.GetValue<int>(); } catch { }
            try { return (long)n.GetValue<double>(); } catch { }
            return long.TryParse(n.ToString(), out var x) ? x : def;
        }
    }

    public static int Int(string k, int def) => (int)Long(k, def);
    public static void SetLong(string k, long v) { lock (L) D[k] = v; Save(); }
    public static void SetInt(string k, int v) { lock (L) D[k] = v; Save(); }

    public static string Secret(string k)
    {
        var s = Str(k);
        if (!s.StartsWith("dpapi:")) return s;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s[6..]), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    public static void SetSecret(string k, string v)
    {
        if (string.IsNullOrEmpty(v)) { Set(k, ""); return; }
        Set(k, "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(v), Entropy, DataProtectionScope.CurrentUser)));
    }

    // ---------------------------------------------------------------- shared with the phone
    public static string Target { get => Str("target", "gemini"); set => Set("target", value); }
    public static string Mode { get => Str("mode", "grammar"); set => Set("mode", value); }
    public static string TrLang { get { var s = Str("tr_lang", "English"); return string.IsNullOrWhiteSpace(s) ? "English" : s; } set => Set("tr_lang", value); }
    public static string Custom { get => Str("custom"); set => Set("custom", value); }
    public static string ProfileFields { get => Str("p_fields"); set => Set("p_fields", value); }
    public static string ProfileAbout { get => Str("p_about"); set => Set("p_about", value); }
    public static string ProfileTerms { get => Str("p_terms"); set => Set("p_terms", value); }
    public static string Key(string p) => Secret("key_" + p);
    public static void SetKey(string p, string v) => SetSecret("key_" + p, v?.Trim());
    public static string Model(string p) { var m = Str("model_" + p); return string.IsNullOrWhiteSpace(m) ? Ai.ById(p).DefaultModel : m; }
    public static void SetModel(string p, string v) => Set("model_" + p, v?.Trim());

    // ---------------------------------------------------------------- Windows only
    /// <summary>pc · m365 · both</summary>
    public static string EmailSource { get => Str("win_email_source", "both"); set => Set("win_email_source", value); }
    /// <summary>What the AI may see: A only the question · B + subject and first lines · C + full email when tapped · D = B and C.</summary>
    public static string AiSees { get => Str("win_ai_sees", "A"); set => Set("win_ai_sees", value); }
    public static bool AiSeesLines => AiSees is "B" or "D";
    public static bool AiSeesFull => AiSees is "C" or "D";
    /// <summary>3m · 12m · pc · all365</summary>
    public static string Range { get => Str("win_range", "all365"); set => Set("win_range", value); }
    public static bool NeedTo { get => Bool("win_need_to", true); set => SetBool("win_need_to", value); }
    public static bool NeedCcNamed { get => Bool("win_need_cc", false); set => SetBool("win_need_cc", value); }
    public static bool NeedAi { get => Bool("win_need_ai", false); set => SetBool("win_need_ai", value); }
    public static bool FollowUps { get => Bool("win_followups", true); set => SetBool("win_followups", value); }
    public static int FollowDays { get => Math.Max(1, Int("win_follow_days", 3)); set => SetInt("win_follow_days", value); }
    public static int BriefDays { get => Math.Max(1, Int("win_brief_days", 14)); set => SetInt("win_brief_days", value); }
    public static bool BriefOn { get => Bool("win_brief", true); set => SetBool("win_brief", value); }
    public static int BriefHour { get => Int("win_brief_hour", 7); set => SetInt("win_brief_hour", value); }
    public static bool IndexAttachments { get => Bool("win_index_att", true); set => SetBool("win_index_att", value); }
    public static bool IndexLocal { get => Bool("win_index_local", false); set => SetBool("win_index_local", value); }
    public static bool AutoBackup { get => Bool("win_autobackup", true); set => SetBool("win_autobackup", value); }
    public static int KeepBackups { get => Math.Max(1, Int("win_keep_backups", 14)); set => SetInt("win_keep_backups", value); }
    public static string BackupPassword { get => Secret("win_backup_pw"); set => SetSecret("win_backup_pw", value); }
    public static bool Autostart { get => Bool("win_autostart", true); set => SetBool("win_autostart", value); }

    public static DateTime CutOff => Range switch
    {
        "3m" => DateTime.Now.AddMonths(-3),
        "12m" => DateTime.Now.AddMonths(-12),
        _ => DateTime.MinValue
    };
}

/// <summary>Starts Nova with Windows (per user, no admin needed).</summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (Store.Autostart) k.SetValue("Nova Enhance", "\"" + Environment.ProcessPath + "\" --tray");
            else k.DeleteValue("Nova Enhance", false);
        }
        catch (Exception e) { Log.Error("autostart", e); }
    }
}

/// <summary>History, originals and drafts, kept as plain files in Documents\Nova Enhance.</summary>
static class History
{
    static readonly object L = new();

    public class Original { public long Ts { get; set; } public string Mode { get; set; } public string Text { get; set; } public string Output { get; set; } }

    static string OriginalsFile => Path.Combine(Paths.Originals, "originals.jsonl");

    public static void Add(string kind, string provider, string model, string mode, string input, string output)
    {
        try
        {
            var o = new JsonObject
            {
                ["ts"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(), ["kind"] = kind, ["provider"] = provider, ["model"] = model,
                ["mode"] = mode, ["input"] = input, ["output"] = output
            };
            lock (L) File.AppendAllText(Path.Combine(Paths.History, DateTime.Now.ToString("yyyy-MM") + ".jsonl"), o.ToJsonString() + "\n");
        }
        catch (Exception e) { Log.Error("history", e); }
    }

    public static void SaveOriginal(string mode, string text, string output)
    {
        try
        {
            var line = JsonSerializer.Serialize(new Original { Ts = DateTimeOffset.Now.ToUnixTimeMilliseconds(), Mode = mode, Text = text, Output = output });
            lock (L)
            {
                File.AppendAllText(OriginalsFile, line + "\n");
                var all = File.ReadAllLines(OriginalsFile);
                if (all.Length > 600) File.WriteAllLines(OriginalsFile, all[^500..]);
            }
        }
        catch (Exception e) { Log.Error("originals", e); }
    }

    public static List<Original> LastOriginals(int n = 20)
    {
        try
        {
            lock (L)
            {
                if (!File.Exists(OriginalsFile)) return new();
                return File.ReadAllLines(OriginalsFile).Where(l => l.Trim().Length > 0).TakeLast(n).Reverse()
                    .Select(l => { try { return JsonSerializer.Deserialize<Original>(l); } catch { return null; } })
                    .Where(o => o != null).ToList();
            }
        }
        catch { return new(); }
    }

    /// <summary>History in the phone's backup format (conversations with messages).</summary>
    public static JsonArray ExportForBackup()
    {
        var arr = new JsonArray();
        try
        {
            foreach (var f in Directory.GetFiles(Paths.History, "*.jsonl").OrderBy(x => x))
                foreach (var l in File.ReadAllLines(f))
                {
                    if (l.Trim().Length == 0) continue;
                    try
                    {
                        var o = JsonNode.Parse(l).AsObject();
                        long ts = o["ts"]?.GetValue<long>() ?? 0;
                        var kind = o["kind"]?.ToString() ?? "enhance";
                        string input = o["input"]?.ToString() ?? "", output = o["output"]?.ToString() ?? "";
                        arr.Add(new JsonObject
                        {
                            ["project"] = kind == "ask" ? "Ask AI" : "Enhancement",
                            ["provider"] = o["provider"]?.ToString() ?? "", ["mode"] = o["mode"]?.ToString() ?? "",
                            ["title"] = input.Length > 60 ? input[..60] : input, ["route"] = "api",
                            ["created"] = ts, ["updated"] = ts,
                            ["messages"] = new JsonArray(
                                new JsonObject { ["role"] = "user", ["content"] = input, ["ts"] = ts },
                                new JsonObject { ["role"] = "assistant", ["content"] = output, ["ts"] = ts })
                        });
                    }
                    catch { }
                }
        }
        catch (Exception e) { Log.Error("history export", e); }
        return arr;
    }

    public static string SafeName(string s, int max = 50)
    {
        var bad = Path.GetInvalidFileNameChars();
        var t = new string((s ?? "").Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim();
        if (t.Length > max) t = t[..max].Trim();
        return t.Length == 0 ? "untitled" : t;
    }
}
