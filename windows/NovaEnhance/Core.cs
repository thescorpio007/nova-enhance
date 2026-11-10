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
            // ✓ A file that moved is followed; one that is gone says which folder is wrong.
            if (System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z]:\\|^\\\\[^?]") && !File.Exists(path) && !Directory.Exists(path))
            {
                var real = FileCheck.Resolve(path, out var why);
                if (real == null) { MessageBox.Show(why, "Nova Enhance · file not found", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                path = real;
            }
            Process.Start(new ProcessStartInfo(File.Exists(path) ? LongPath.ForApps(path) : LongPath.Short(path)) { UseShellExecute = true });
        }
        catch (Exception e) { Log.Error("open " + path, e); }
    }

    /// <summary>
    /// 📁 Show in folder: opens the file's folder with the file selected — also for paths longer than 260 characters
    /// (deep project folders), which "explorer /select" can't handle (it opened the Desktop instead).
    /// </summary>
    public static void Select(string file)
    {
        if (string.IsNullOrEmpty(file)) return;
        try
        {
            var real = FileCheck.Resolve(file, out var why);
            if (real == null) { MessageBox.Show(why, "Nova Enhance · file not found", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            file = real;
            var shortP = LongPath.Short(file);
            var pidl = ILCreateFromPathW(shortP);
            if (pidl != IntPtr.Zero)
                try { if (SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0) return; }
                finally { ILFree(pidl); }
            if (shortP.Length < 260) { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + shortP + "\"") { UseShellExecute = true }); return; }
            // Still too long: open the folder itself (or the deepest parent Explorer can open).
            var dir = LongPath.Short(Path.GetDirectoryName(file) ?? "");
            while (dir.Length >= 248 && Path.GetDirectoryName(dir) is { Length: > 0 } up) dir = up;
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch (Exception e) { Log.Warn("show in folder", file + ": " + e.Message); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr ILCreateFromPathW(string path);
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern void ILFree(IntPtr pidl);
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint flags);
}

/// <summary>
/// Paths longer than 260 characters (deep project folders with long drawing names): .NET reads them, but Explorer,
/// the PDF renderer and some apps don't. Short (8.3) names when the drive has them; otherwise a temporary copy to draw from.
/// </summary>
static class LongPath
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    public static bool IsLong(string p) => (p ?? "").Length >= 248;

    /// <summary>The 8.3 form of a long path (same file), or the path unchanged.</summary>
    public static string Short(string p)
    {
        if ((!IsLong(p) && FileCheck.OddName(p) == null) || p.StartsWith(@"\\?\")) return p;
        try
        {
            var unc = p.StartsWith(@"\\") ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;
            var sb = new StringBuilder(1024);
            var n = GetShortPathNameW(unc, sb, (uint)sb.Capacity);
            if (n == 0 || n >= sb.Capacity) return p;
            var r = sb.ToString();
            if (r.StartsWith(@"\\?\UNC\")) r = @"\\" + r[8..];
            else if (r.StartsWith(@"\\?\")) r = r[4..];
            return r.Length < p.Length ? r : p;
        }
        catch { return p; }
    }

    /// <summary>For other apps (PDF-XChange, Word…): the path as is, its short form, or a copy when a folder name can't be opened.</summary>
    public static string ForApps(string p) => Usable(p);

    /// <summary>A path every Windows component can open: the short form, else a temporary copy (kept while unchanged).</summary>
    public static string Usable(string p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        var plainOk = File.Exists(p);
        if (!IsLong(p) && (plainOk || !File.Exists(FileCheck.Exact(p)))) return p;
        var s = Short(p);
        if (!IsLong(s) && File.Exists(s) && FileCheck.OddName(s) == null) return s;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Nova Enhance", "long");
            Directory.CreateDirectory(dir);
            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(p.ToLowerInvariant())))[..16];
            var copy = Path.Combine(dir, key + Path.GetExtension(p));
            var src = new FileInfo(FileCheck.Exact(p));
            if (!File.Exists(copy) || File.GetLastWriteTimeUtc(copy) < src.LastWriteTimeUtc || new FileInfo(copy).Length != src.Length)
                File.Copy(FileCheck.Exact(p), copy, true);
            return copy;
        }
        catch { return p; }
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
    public static string EmailSource { get => Str("win_email_source", "pc"); set => Set("win_email_source", value); }
    /// <summary>What the AI may see: A only the question · B + subject and first lines · C + full email when tapped · D = B and C.</summary>
    public static string AiSees { get => Str("win_ai_sees", "C"); set => Set("win_ai_sees", value); }
    public static bool AiSeesLines => AiSees is "B" or "D";
    public static bool AiSeesFull => AiSees is "C" or "D";
    /// <summary>3m · 12m · pc · all365</summary>
    public static string Range { get => Str("win_range", "pc"); set => Set("win_range", value); }
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
