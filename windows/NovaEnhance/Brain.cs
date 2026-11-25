using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🧠 Nova Brain — one encrypted file (Documents\Nova Enhance\Nova Brain.nbrain, AES-256-GCM with ITS OWN password) holding the rules
/// that guide every task: how to talk to the AI, how to search, what comes first, your work knowledge (document numbers, Thinkproject,
/// codes, towers…), privacy and lessons learned. Every AI call reads the sections that apply to it; the Submittal log reads its settings
/// ([overdue.WIR = 2] …) from here. A version is saved before every change; daily backups are checked right after writing; the brain also
/// travels inside the full Nova backup. Designed by A.HAMROUNI
/// </summary>
static class Brain
{
    public static string FilePath => Path.Combine(Paths.Root, "Nova Brain.nbrain");
    public static string VersionsDir { get { var p = Path.Combine(Paths.Root, "Brain", "versions"); Directory.CreateDirectory(p); return p; } }
    public static string BackupsDir { get { var p = Path.Combine(Paths.Root, "Brain backups"); Directory.CreateDirectory(p); return p; } }
    static string PendingRestore => Path.Combine(Paths.Root, "Brain", "pending-restore.nbrain");

    static readonly object L = new();
    static JsonObject data;          // the unlocked brain (null = locked, or not made yet)
    static JsonObject defaults;      // the built-in rules (used while locked / before you make yours)
    public static event Action Changed;

    /// <summary>The brain password, remembered on this PC only (Windows protection). Never inside the full Nova backup.</summary>
    static string Password { get => Store.Secret("win_brain_pw"); set => Store.SetSecret("win_brain_pw", value); }

    public static bool Exists => File.Exists(FilePath);
    public static bool Unlocked { get { lock (L) return data != null; } }
    public static bool HasPending => File.Exists(PendingRestore);
    public static int Version { get { lock (L) return (int)(data?["version"]?.GetValue<int>() ?? 0); } }
    public static int RuleCount { get { lock (L) return Rules(data ?? Defaults()).Count(); } }
    public static string LastBackupText { get => Store.Str("win_brain_last_backup", ""); set => Store.Set("win_brain_last_backup", value); }
    public static int KeepDaily { get => Store.Int("win_brain_keep_daily", 30); set => Store.SetInt("win_brain_keep_daily", value); }
    public static int KeepMonthly { get => Store.Int("win_brain_keep_monthly", 12); set => Store.SetInt("win_brain_keep_monthly", value); }

    // ================================================================ encryption

    static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    static string Pack(JsonObject brain, string pw)
    {
        var plain = brain.ToJsonString();
        return new JsonObject
        {
            ["app"] = "Nova Brain", ["format"] = 1, ["version"] = brain["version"]?.GetValue<int>() ?? 0, ["saved"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            ["sha"] = Sha(plain), ["enc"] = Backup.Encrypt(plain, pw)
        }.ToJsonString();
    }

    /// <summary>Opens a brain file with a password: checks the file is whole, unlocks it and checks its fingerprint. Throws a clear message.</summary>
    public static JsonObject Unpack(string fileText, string pw)
    {
        JsonObject o;
        try { o = JsonNode.Parse(fileText)?.AsObject(); } catch { throw new InvalidDataException("the file is damaged (it can't be read)"); }
        if (o?["app"]?.ToString() != "Nova Brain" || o["enc"] is not JsonObject enc) throw new InvalidDataException("this isn't a Nova Brain file");
        string plain;
        try { plain = Backup.Decrypt(enc, pw); } catch { throw new InvalidDataException("the password doesn't open it"); }
        if (o["sha"] is { } sha && sha.ToString() != Sha(plain)) throw new InvalidDataException("the fingerprint doesn't match (the file was changed or damaged)");
        var b = JsonNode.Parse(plain)?.AsObject() ?? throw new InvalidDataException("the inside is damaged");
        if (b["sections"] is not JsonArray) throw new InvalidDataException("the inside has no sections");
        return b;
    }

    static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }

    // ================================================================ open · make · lock

    /// <summary>At start: opens the brain with the remembered password, adds new built-in rules, starts the daily backup.</summary>
    public static void Start()
    {
        try { if (Exists && Password.Length > 0) Unlock(Password, remember: true); } catch (Exception e) { Log.Warn("brain", "open: " + e.Message); }
        var t = new System.Threading.Timer(_ => { try { DailyBackup(); } catch (Exception e) { Log.Warn("brain", "daily backup: " + e.Message); } }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));
        GC.KeepAlive(t); keep = t;
    }
    static System.Threading.Timer keep;

    public static void Create(string pw)
    {
        if (pw.Length < 6) throw new InvalidDataException("use at least 6 characters");
        lock (L)
        {
            var b = Defaults().DeepClone().AsObject();
            b["version"] = 1; b["created"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            b["history"] = new JsonArray(new JsonObject { ["v"] = 1, ["when"] = Now(), ["by"] = "Nova · built-in", ["summary"] = $"Brain created with {Rules(b).Count()} built-in rules" });
            Password = pw;
            data = b;
            SaveLocked(null);
        }
        Changed?.Invoke();
    }

    public static void Unlock(string pw, bool remember = true)
    {
        var b = Unpack(File.ReadAllText(FilePath), pw);
        lock (L) { data = b; if (remember) Password = pw; }
        var added = MergeBuiltIns();
        if (added > 0) Log.Write($"brain: {added} new built-in rule(s) added");
        Changed?.Invoke();
    }

    public static void Lock() { lock (L) data = null; Password = ""; Changed?.Invoke(); }

    public static bool CheckPassword(string pw) => pw.Length > 0 && pw == Password;

    public static void ChangePassword(string oldPw, string newPw)
    {
        if (oldPw != Password) throw new InvalidDataException("the current password is wrong");
        if (newPw.Length < 6) throw new InvalidDataException("use at least 6 characters");
        lock (L)
        {
            if (data == null) throw new InvalidDataException("open the brain first");
            // Every version is locked again with the new password (older backups keep the old one).
            foreach (var f in Directory.GetFiles(VersionsDir, "*.nbrain"))
                try { var v = Unpack(File.ReadAllText(f), oldPw); WriteAtomic(f, Pack(v, newPw)); } catch { }
            Password = newPw;
            WriteAtomic(FilePath, Pack(data, newPw));
        }
        Changed?.Invoke();
    }

    static long Now() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    /// <summary>Saves a version of the brain as it is now (before a change), then the change itself.</summary>
    static void SaveLocked(string _)
    {
        var pw = Password;
        WriteAtomic(FilePath, Pack(data, pw));
        var v = data["version"]?.GetValue<int>() ?? 0;
        WriteAtomic(Path.Combine(VersionsDir, $"v{v:0000}.nbrain"), Pack(data, pw));
    }

    /// <summary>A copy of the brain (for showing or changing without touching the real one).</summary>
    public static JsonObject Snapshot() { if (Override.Value is { } o) return o.DeepClone().AsObject(); lock (L) return (data ?? Defaults()).DeepClone().AsObject(); }

    /// <summary>🧪 TEST: inside this async flow every part of Nova reads this brain instead of the real one (nothing is saved).</summary>
    public static readonly AsyncLocal<JsonObject> Override = new();

    /// <summary>For the phone: every rule with its id, title and 🧪 test mark (read-only view).</summary>
    public static JsonArray ViewForPhone()
    {
        var a = new JsonArray();
        foreach (var sec in (Snapshot()["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            a.Add(new JsonObject
            {
                ["name"] = ((sec["icon"]?.ToString() ?? "") + " " + sec["name"]).Trim(),
                ["rules"] = new JsonArray((sec["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = r["id"]?.ToString(), ["title"] = r["title"]?.ToString(), ["text"] = SetRx.Replace(r["text"]?.ToString() ?? "", m => m.Value).Trim(),
                    ["off"] = r["off"]?.GetValue<bool>() == true, ["tested"] = r["tested"]?.DeepClone()
                }).ToArray())
            });
        return a;
    }

    /// <summary>Replaces the brain with a changed copy: version +1, history line, saved, version file written.</summary>
    public static int Commit(JsonObject changed, string by, string summary)
    {
        lock (L)
        {
            if (data == null) throw new InvalidDataException("the brain is locked — open it first");
            var v = (data["version"]?.GetValue<int>() ?? 0) + 1;
            changed["version"] = v;
            changed["updated"] = Now();
            var hist = changed["history"] as JsonArray ?? new JsonArray();
            if (changed["history"] == null) changed["history"] = hist;
            hist.Add(new JsonObject { ["v"] = v, ["when"] = Now(), ["by"] = by, ["summary"] = summary });
            data = changed;
            SaveLocked(null);
        }
        Changed?.Invoke();
        return Version;
    }

    // ================================================================ rules · sections · settings

    public static IEnumerable<(JsonObject Section, JsonObject Rule)> Rules(JsonObject b) =>
        (b?["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .SelectMany(s => (s["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(r => (s, r)));

    static readonly Regex SetRx = new(@"\[([a-z][\w.\-]*)\s*=\s*([^\]]+)\]", RegexOptions.IgnoreCase);

    /// <summary>A setting written inside a rule as [name = value] (e.g. [overdue.WIR = 2]). The newest rule wins.</summary>
    public static string Setting(string name, string def, JsonObject b = null)
    {
        b ??= Snapshot();
        string found = null;
        foreach (var (_, r) in Rules(b))
        {
            if (r["off"]?.GetValue<bool>() == true) continue;
            foreach (Match m in SetRx.Matches(r["text"]?.ToString() ?? ""))
                if (m.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) found = m.Groups[2].Value.Trim();
        }
        return found ?? def;
    }

    public static int Num(string name, int def, JsonObject b = null) => int.TryParse(Setting(name, def.ToString(), b), out var n) ? n : def;

    public static List<string> ListOf(string name, string def, JsonObject b = null) =>
        Setting(name, def, b).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    // ================================================================ 🧭 every AI task asks the brain

    /// <summary>Set while the brain chat itself talks to the AI (it gets the whole brain another way).</summary>
    public static readonly AsyncLocal<bool> Quiet = new();

    /// <summary>The rules that apply to this AI task, added to its instructions. Empty when none apply.</summary>
    public static string Guide(string system, string user)
    {
        if (Quiet.Value) return "";
        try
        {
            var s = (system ?? "").ToLowerInvariant();
            var tags = new HashSet<string> { "all" };
            bool writing = Regex.IsMatch(s, @"proofread|grammar|rewrite|improve (the|this) text|spreadsheet cells|translate the text|enhance");
            if (writing) tags.Add("writing");
            else
            {
                if (Regex.IsMatch(s, @"search|emails?|find|rank")) tags.Add("search");
                if (s.Contains("deep search") || s.Contains("parts of")) tags.Add("deep");
                if (Regex.IsMatch(s, @"folder|passage|files?\b|drawing")) tags.Add("files");
                if (Regex.IsMatch(s, @"\bwir\b|\bmir\b|\bmar\b|submittal|thinkproject|approval|rfi\b")) tags.Add("submittal");
                tags.Add("answer");
            }
            var b = Snapshot();
            var sb = new StringBuilder();
            foreach (var sec in (b["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var st = (sec["tags"] as JsonArray)?.Select(t => t?.ToString() ?? "").ToList() ?? new();
                if (!st.Any(tags.Contains)) continue;
                if (writing && !st.Contains("writing")) continue;   // grammar work gets only the writing rules
                var rules = (sec["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(r => r["off"]?.GetValue<bool>() != true).ToList();
                if (rules.Count == 0) continue;
                sb.Append('[').Append(sec["name"]).Append("]\n");
                foreach (var r in rules) sb.Append("• ").Append(SetRx.Replace(r["text"]?.ToString() ?? "", "").Trim()).Append('\n');
            }
            if (sb.Length == 0) return "";
            var t = sb.ToString();
            if (t.Length > 5000) t = t[..5000] + "…";
            return "\n\n— Nova Brain: the user's own rules. Follow them whenever they apply to this task; they win over general habits, never over the facts in front of you. —\n" + t;
        }
        catch { return ""; }
    }

    /// <summary>Every [name = value] setting of the brain (the newest rule wins) — the phone reads the same ones.</summary>
    public static JsonObject AllSettings()
    {
        var o = new JsonObject();
        foreach (var (_, r) in Rules(Snapshot()))
        {
            if (r["off"]?.GetValue<bool>() == true) continue;
            foreach (Match m in SetRx.Matches(r["text"]?.ToString() ?? "")) o[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value.Trim();
        }
        return o;
    }

    /// <summary>The rules as text for the phone (the same brain there).</summary>
    public static JsonObject ForPhone()
    {
        var b = Snapshot();
        var o = new JsonObject();
        foreach (var sec in (b["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            o[sec["name"]?.ToString() ?? "?"] = new JsonObject
            {
                ["tags"] = sec["tags"]?.DeepClone(),
                ["rules"] = new JsonArray((sec["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(r => r["off"]?.GetValue<bool>() != true)
                    .Select(r => (JsonNode)SetRx.Replace(r["text"]?.ToString() ?? "", "").Trim()).ToArray())
            };
        return o;
    }

    // ================================================================ changes (brain chat · connector · your edits)

    /// <summary>Applies a list of changes {op: add|change|remove, section, id, title, text} to a copy. Returns the copy and a readable summary.</summary>
    public static (JsonObject Brain, string Summary) Apply(JsonObject b, JsonArray changes, string by)
    {
        var copy = b.DeepClone().AsObject();
        var secs = copy["sections"] as JsonArray ?? new JsonArray();
        var said = new List<string>();
        foreach (var c in changes.OfType<JsonObject>())
        {
            var op = c["op"]?.ToString() ?? "add";
            var secName = c["section"]?.ToString() ?? "🧪 Lessons learned";
            var sec = secs.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.ToString() == secName || Same(s["name"]?.ToString(), secName));
            var id = c["id"]?.ToString() ?? "";
            if (op == "remove")
            {
                var (s0, r0) = Rules(copy).FirstOrDefault(x => x.Rule["id"]?.ToString() == id);
                if (r0 == null) continue;
                (s0["rules"] as JsonArray)!.Remove(r0);
                var rem = copy["removed"] as JsonArray ?? new JsonArray(); copy["removed"] = rem; rem.Add(id);
                said.Add("− " + id);
                continue;
            }
            if (op == "change")
            {
                var (_, r1) = Rules(copy).FirstOrDefault(x => x.Rule["id"]?.ToString() == id);
                if (r1 != null)
                {
                    if (c["text"] != null) r1["text"] = c["text"]!.ToString();
                    if (c["title"] != null) r1["title"] = c["title"]!.ToString();
                    r1["by"] = by; r1["when"] = Now();
                    said.Add("✎ " + id);
                    continue;
                }
                op = "add";
            }
            if (sec == null)
            {
                sec = new JsonObject { ["id"] = Slug(secName), ["name"] = secName, ["icon"] = "", ["tags"] = new JsonArray("all"), ["rules"] = new JsonArray() };
                secs.Add(sec);
            }
            var rules = sec["rules"] as JsonArray ?? new JsonArray(); sec["rules"] = rules;
            if (id.Length == 0 || Rules(copy).Any(x => x.Rule["id"]?.ToString() == id)) id = NextId(copy, sec);
            rules.Add(new JsonObject { ["id"] = id, ["title"] = c["title"]?.ToString() ?? "", ["text"] = c["text"]?.ToString() ?? "", ["by"] = by, ["when"] = Now() });
            said.Add("+ " + id);
        }
        return (copy, string.Join(" · ", said));
    }

    static bool Same(string a, string b) => Norm(a) == Norm(b);
    static string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", "");
    static string Slug(string s) => Regex.Replace(Norm(s), "^$", "section");

    static string NextId(JsonObject b, JsonObject sec)
    {
        var pre = sec["prefix"]?.ToString();
        if (string.IsNullOrEmpty(pre)) pre = new string(Norm(sec["name"]?.ToString()).Where(char.IsLetter).Take(2).ToArray()).ToUpperInvariant();
        if (pre.Length == 0) pre = "R";
        var used = Rules(b).Select(x => x.Rule["id"]?.ToString() ?? "").ToHashSet();
        for (int i = 1; ; i++) { var id = $"{pre}-{i:00}"; if (!used.Contains(id)) return id; }
    }

    /// <summary>New built-in rules from a Nova update join your brain (never over your own edits, never back after you removed them).</summary>
    static int MergeBuiltIns()
    {
        JsonObject copy;
        lock (L) { if (data == null) return 0; copy = data.DeepClone().AsObject(); }
        var removed = (copy["removed"] as JsonArray)?.Select(x => x?.ToString()).ToHashSet() ?? new HashSet<string>();
        var have = Rules(copy).Select(x => x.Rule["id"]?.ToString()).ToHashSet();
        var secs = copy["sections"] as JsonArray ?? new JsonArray();
        int n = 0;
        foreach (var ds in (Defaults()["sections"] as JsonArray)!.OfType<JsonObject>())
        {
            var mine = secs.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.ToString() == ds["id"]?.ToString());
            foreach (var r in (ds["rules"] as JsonArray)!.OfType<JsonObject>())
            {
                var id = r["id"]?.ToString();
                if (have.Contains(id) || removed.Contains(id)) continue;
                if (mine == null) { mine = new JsonObject { ["id"] = ds["id"]?.ToString(), ["name"] = ds["name"]?.ToString(), ["icon"] = ds["icon"]?.ToString(), ["prefix"] = ds["prefix"]?.ToString(), ["tags"] = ds["tags"]?.DeepClone(), ["rules"] = new JsonArray() }; secs.Add(mine); }
                var nr = r.DeepClone().AsObject(); nr["by"] = "Claude · build " + Program.Shown; nr["when"] = Now();
                (mine["rules"] as JsonArray)!.Add(nr);
                n++;
            }
        }
        if (n > 0) Commit(copy, "Claude · Nova update " + Program.Shown, $"+ {n} new built-in rule(s)");
        return n;
    }

    // ================================================================ versions

    public static JsonArray Versions()
    {
        var hist = Snapshot()["history"] as JsonArray ?? new JsonArray();
        var res = new JsonArray();
        foreach (var h in hist.OfType<JsonObject>().Reverse())
        {
            var v = h["v"]?.GetValue<int>() ?? 0;
            var o = h.DeepClone().AsObject();
            o["file"] = File.Exists(Path.Combine(VersionsDir, $"v{v:0000}.nbrain"));
            res.Add(o);
        }
        return res;
    }

    public static JsonObject LoadVersion(int v) => Unpack(File.ReadAllText(Path.Combine(VersionsDir, $"v{v:0000}.nbrain")), Password);

    /// <summary>What changed between two brains, rule by rule.</summary>
    public static JsonArray Diff(JsonObject before, JsonObject after)
    {
        var a = Rules(before).ToDictionary(x => x.Rule["id"]?.ToString() ?? "", x => x);
        var b = Rules(after).ToDictionary(x => x.Rule["id"]?.ToString() ?? "", x => x);
        var res = new JsonArray();
        foreach (var (id, x) in b)
            if (!a.TryGetValue(id, out var y)) res.Add(new JsonObject { ["op"] = "add", ["id"] = id, ["section"] = x.Section["name"]?.ToString(), ["text"] = x.Rule["text"]?.ToString() });
            else if (y.Rule["text"]?.ToString() != x.Rule["text"]?.ToString()) res.Add(new JsonObject { ["op"] = "change", ["id"] = id, ["section"] = x.Section["name"]?.ToString(), ["old"] = y.Rule["text"]?.ToString(), ["text"] = x.Rule["text"]?.ToString() });
        foreach (var (id, y) in a)
            if (!b.ContainsKey(id)) res.Add(new JsonObject { ["op"] = "remove", ["id"] = id, ["section"] = y.Section["name"]?.ToString(), ["old"] = y.Rule["text"]?.ToString() });
        return res;
    }

    public static int BackTo(int v)
    {
        var old = LoadVersion(v);
        var cur = Snapshot();
        old["history"] = cur["history"]?.DeepClone();
        old["removed"] = cur["removed"]?.DeepClone();
        return Commit(old, "you · ↩ back to a version", $"↩ back to version {v}");
    }

    // ================================================================ 💾 backups

    /// <summary>One backup of the brain (daily · monthly · manual · before-restore), opened again right after writing.</summary>
    public static string BackupNow(string kind = "manual")
    {
        string text;
        lock (L) { if (data == null) throw new InvalidDataException("open the brain first"); text = Pack(data, Password); }
        var path = Path.Combine(BackupsDir, $"brain-{DateTime.Now:yyyy-MM-dd-HHmm}-{kind}.nbrain");
        WriteAtomic(path, text);
        var b = Unpack(File.ReadAllText(path), Password);   // a damaged copy is found today, not on the day it's needed
        LastBackupText = $"{DateTime.Now:dd MMM HH:mm} ✓ checked · version {b["version"]}";
        Prune();
        _ = Task.Run(async () => { try { await Backup.CopyToDrive(path); } catch { } });
        Changed?.Invoke();
        return path;
    }

    static void DailyBackup()
    {
        if (!Unlocked) return;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (Directory.GetFiles(BackupsDir, $"brain-{today}-*-daily.nbrain").Length > 0) return;
        // At 03:00, or at the next start when the PC was off then.
        var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
        bool missed = Directory.GetFiles(BackupsDir, $"brain-{yesterday}-*-daily.nbrain").Length == 0;
        if (DateTime.Now.Hour < 3 && !missed) return;
        BackupNow("daily");
        var month = DateTime.Now.ToString("yyyy-MM");
        if (Directory.GetFiles(BackupsDir, $"brain-{month}-*-monthly.nbrain").Length == 0) BackupNow("monthly");
    }

    static void Prune()
    {
        void Keep(string kind, int n)
        {
            foreach (var f in Directory.GetFiles(BackupsDir, $"*-{kind}.nbrain").OrderByDescending(f => f).Skip(Math.Max(1, n)))
                try { File.Delete(f); } catch { }
        }
        Keep("daily", KeepDaily); Keep("monthly", KeepMonthly); Keep("manual", 60); Keep("before-restore", 20);
    }

    /// <summary>The backups with their check (opened with the password, fingerprint, version, rules).</summary>
    public static JsonArray Backups()
    {
        var res = new JsonArray();
        foreach (var f in Directory.GetFiles(BackupsDir, "*.nbrain").OrderByDescending(f => File.GetLastWriteTime(f)).Take(80))
        {
            var o = new JsonObject { ["file"] = Path.GetFileName(f), ["when"] = new DateTimeOffset(File.GetLastWriteTime(f)).ToUnixTimeMilliseconds(), ["size"] = new FileInfo(f).Length };
            o["kind"] = Regex.Match(Path.GetFileNameWithoutExtension(f), @"-(daily|monthly|manual|before-restore)$").Groups[1].Value;
            try { var b = Unpack(File.ReadAllText(f), Password); o["ok"] = true; o["v"] = b["version"]?.GetValue<int>() ?? 0; o["rules"] = Rules(b).Count(); }
            catch (Exception e) { o["ok"] = false; o["why"] = e.Message; }
            res.Add(o);
        }
        return res;
    }

    /// <summary>Restores a brain file after every check; your current brain is first kept as "before restore". Nothing is replaced when a check fails.</summary>
    public static string RestoreFile(string fileText, string pw, string from)
    {
        var b = Unpack(fileText, pw);   // throws: nothing replaced
        lock (L)
        {
            if (data != null)
                try { WriteAtomic(Path.Combine(BackupsDir, $"brain-{DateTime.Now:yyyy-MM-dd-HHmm}-before-restore.nbrain"), Pack(data, Password)); } catch { }
            var keepHist = data?["history"]?.DeepClone() as JsonArray ?? new JsonArray();
            var v = Math.Max(data?["version"]?.GetValue<int>() ?? 0, b["version"]?.GetValue<int>() ?? 0) + 1;
            var restoredFrom = b["version"]?.GetValue<int>() ?? 0;
            b["version"] = v;
            keepHist.Add(new JsonObject { ["v"] = v, ["when"] = Now(), ["by"] = "you · restore", ["summary"] = $"↩ restored version {restoredFrom} from {from}" });
            b["history"] = keepHist;
            if (pw != Password) Password = pw;
            data = b;
            SaveLocked(null);
        }
        try { File.Delete(PendingRestore); } catch { }
        Changed?.Invoke();
        return $"🧠 Brain restored from {from} · version {Version} · {RuleCount} rules";
    }

    // ---------------------------------------------------------------- inside the full Nova backup

    /// <summary>The brain file for the full backup (still locked with the brain password), or null.</summary>
    public static JsonObject ForFullBackup()
    {
        if (!Exists) return null;
        var t = File.ReadAllText(FilePath);
        return new JsonObject { ["file"] = t, ["sha"] = Sha(t) };
    }

    /// <summary>From a full Nova backup: checked, then restored when the remembered brain password opens it; otherwise kept until you type it.</summary>
    public static string FromFullBackup(JsonObject o)
    {
        if (o?["file"] is not { } f) return "";
        var t = f.ToString();
        if (o["sha"]?.ToString() is { Length: > 0 } sha && sha != Sha(t)) return " · 🧠 Brain NOT restored (the copy in the backup is damaged — your current brain is unchanged)";
        if (Password.Length > 0)
            try { return " · " + RestoreFile(t, Password, "the full Nova backup"); }
            catch (Exception e) { if (!e.Message.Contains("password")) return " · 🧠 Brain NOT restored (" + e.Message + ") — your current brain is unchanged"; }
        WriteAtomic(PendingRestore, t);
        return " · 🧠 Brain waits for ITS password: open 🧠 Nova Brain and type it to finish (your current brain is unchanged until then)";
    }

    public static string RestorePending(string pw) => RestoreFile(File.ReadAllText(PendingRestore), pw, "the full Nova backup");

    // ================================================================ the built-in rules (Nova updates add new ones)

    static JsonObject R(string id, string title, string text) => new() { ["id"] = id, ["title"] = title, ["text"] = text, ["by"] = "Nova · built-in" };
    static JsonObject S(string id, string icon, string name, string prefix, string[] tags, params JsonObject[] rules) =>
        new() { ["id"] = id, ["icon"] = icon, ["name"] = name, ["prefix"] = prefix, ["tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray()), ["rules"] = new JsonArray(rules) };

    public static JsonObject Defaults()
    {
        if (defaults != null) return defaults;
        defaults = new JsonObject
        {
            ["app"] = "Nova Brain", ["version"] = 0,
            ["sections"] = new JsonArray(
                S("priorities", "🎯", "Priorities", "PR", new[] { "all" },
                    R("PR-01", "Facts first", "Answer only from what was found (emails, files, Drive). Never invent a number, date, code, status or name; say what is missing instead."),
                    R("PR-02", "Newest and final", "When several versions exist, the latest revision and the newest approved document come first; older ones are history."),
                    R("PR-03", "Approvals weigh more", "An approval, a reply sheet or a consultant code (Code 1–4) is stronger evidence than a submission or a discussion."),
                    R("PR-04", "Show the source", "Every statement in an answer points to its source [n] so it can be opened.")),
                S("ai", "💬", "How to talk to the AI", "AI", new[] { "all" },
                    R("AI-01", "Short and exact", "Write short, plain English. Keep document numbers, codes and names exactly as written."),
                    R("AI-02", "Tables for lists", "When the answer lists several documents or items, use a table: item · document no. · status · date · source.")),
                S("search", "🔍", "Searching emails", "SE", new[] { "search" },
                    R("SE-01", "Document numbers", "A document number (e.g. SLV-ZUC-CW-WIR-15280, RS5-C7-ACC-AR-MAR-00121) is the strongest key: search it exactly, with and without the revision."),
                    R("SE-02", "Threads", "A reply on the same thread (same document number and revision, “Re:”) belongs to its submission; read the reply to know the outcome."),
                    R("SE-03", "Symbols", "Symbols alone (#, //, -) are not words: ignore them when searching.")),
                S("files", "📂", "Folders & files", "FI", new[] { "files" },
                    R("FI-01", "Latest revision", "In project folders the file with the highest revision (Rev02 > Rev01, R01 > R00) is the current one."),
                    R("FI-02", "Reply sheets", "Next to a submitted document look for its reply/response/comment sheet: it holds the consultant's code.")),
                S("deep", "🔎", "Deep search", "DS", new[] { "deep" },
                    R("DS-01", "Whole system first", "Search the whole system first (e.g. the approved waterproofing system), then each of its parts, then the document numbers found on the way."),
                    R("DS-02", "Follow approvals", "For each document found, look for its approval status and its later revisions before answering.")),
                S("submittal", "📋", "Submittal logs", "SL", new[] { "submittal", "search" },
                    R("SL-01", "Document types", "Main types: WIR = work inspection request, MIR = material inspection request, MAR = material approval request, PQS = prequalification, RFI = request for information, SDS = shop drawing submittal."),
                    R("SL-02", "Submission · Thinkproject", "Thinkproject (SLV): the submission is the notification “SLV # WIR <number>_RevNN_<title>”. Its table gives the Deadline and the Date of creation (dd/mm/yyyy)."),
                    R("SL-03", "Closed · Thinkproject", "Thinkproject: closed when a “Re:” on the same thread from MZA carries “Approval completed: yes”. The code is the “Approval result” line (e.g. Code 2 Make Corrections Noted and Proceed) or the “-C2.pdf” file."),
                    R("SL-04", "Submission · Outlook only", "Projects without Thinkproject (RS5): the submission is the email “RS5-C7-ACC-AR-WIR-00409-R01_<title>” with the PDF attached."),
                    R("SL-05", "Closed · Outlook only", "Outlook only: closed when MZA replies on the same thread with a signed or coded file [closed.files = signed, -C1, -C2, -C3, -C4, reply]. Code from the file name (C1–C4), else the reply text."),
                    R("SL-06", "Who is MZA", "A reply is from MZA when the sender name or address contains [mza.match = MZ ARCHITECTS, mz-architects.com] — or when I sent it."),
                    R("SL-07", "Revisions", "Rev01 / R01 after a Code 3 is a resubmission: the older revision becomes “Superseded”."),
                    R("SL-08", "Counted once", "“(Updated)” notifications, forwards (FW:), automatic replies and “_with MZA Response” copies are counted once."),
                    R("SL-09", "Overdue", "Overdue when past the Thinkproject Deadline; otherwise after [overdue.WIR = 2] days for WIR, [overdue.MIR = 2] MIR, [overdue.RFI = 7] RFI, [overdue.MAR = 14] MAR, [overdue.PQS = 14] PQS, [overdue.SDS = 14] SDS."),
                    R("SL-10", "Never drop silently", "Anything without its submission email, or a WIR/MIR without tower, zone or level, goes to the “Check” sheet with the reason."),
                    R("SL-11", "All types", "Logged types (every project): [log.all = WIR, MIR, MAR, MSS, SDS, SSM, TSR, PQS, RFI, RPT, HSE, CAL, RTS, HOV, PLN, LTR, PAY, DSR, VO, AUTH, SOR, NCR, EI, MOM]. SHD folder = SDS in emails (shop drawings, “SLV # DRW //”); REP folder = RPT in emails (weekly/monthly reports). GEN is not logged, except HSE that comes as …-HSE-GEN-…. Contract documents and Technical assessments are not transmittals (searchable only)."),
                    R("SL-12", "Closed by MZA code", "WIR, MIR, MAR, MSS, SDS, SSM, TSR, PQS, RFI, RPT, HSE, CAL, RTS, HOV, PLN: closed by MZA's reply on the same thread with a code — Thinkproject “Approval completed: yes”, a signed or coded file (-C2, Code2), or a “_with MZA Response” copy."),
                    R("SL-13", "Closed by a reply", "LTR (contractor letters) and VO (variations): closed when MZA replies. PAY (payment applications: letters or emails about payment, IPC, IPA, statement of account): closed when MZA replies or certifies. AUTH (ADCD, Civil Defence, ADM, Municipality, DMT, Aldar, Miral, NOC — any local authority): closed by any reply on the same thread. VO and AUTH have no document code: found by their words, one row per email thread."),
                    R("SL-14", "Issued by MZA", "SOR and NCR are issued by MZA (…-MZA-…-SOR/NCR-…): closed only when MZA confirms closure after the contractor's reply (until then “Replied”). EI = Engineer’s Instruction (MZA letter “Engineer’s Instruction no. 083 …”, ref MZA-ZUB-…-L-2026-616): closed when the contractor acknowledges on the same thread or letter. An “Engineer’s Determination for EI No. 51” is shown on EI 51."),
                    R("SL-15", "Meetings", "MOM = minutes of meetings, kick-off meetings and any meeting record (“MOM-00077-DRAFT”, “Progress Meeting No. 115 held on …”, “Kick-Off Meeting”, “Draft MoM”): Open while only the DRAFT exists, Closed when the final minutes are issued. Each series is a register: a missing meeting or EI number is listed in Check."),
                    R("SL-16", "Daily reports", "DSR (contractor daily reports) are listed as “Received”: nothing to close."),
                    R("SL-17", "Project codes", "A project code read wrong from a subject (“FSLV”, “LV”) is corrected to the known project (SLV, RS5…)."),
                    R("SL-18", "Overdue · other types", "Overdue after [overdue.MSS = 14] MSS, [overdue.SSM = 14] SSM, [overdue.TSR = 14] TSR, [overdue.RPT = 14] RPT, [overdue.HSE = 14] HSE, [overdue.CAL = 14] CAL, [overdue.RTS = 7] RTS, [overdue.HOV = 14] HOV, [overdue.PLN = 14] PLN, [overdue.LTR = 14] LTR, [overdue.PAY = 14] PAY, [overdue.VO = 14] VO, [overdue.AUTH = 14] AUTH, [overdue.SOR = 7] SOR, [overdue.NCR = 14] NCR, [overdue.EI = 7] EI, [overdue.MOM = 7] MOM days.")),
                // 🧭 How Nova reads what you type in the search box (used on the PC and the phone, never sent to the AI).
                S("intent", "🧭", "What I mean", "IN", new[] { "local" },
                    R("IN-01", "Unsorted", "Show the emails not sorted into a project when I say [intent.unsorted = unsorted, not sorted, no project, without project, unassigned]."),
                    R("IN-02", "Unread", "Show unread emails when I say [intent.unread = unread, not read, new emails]."),
                    R("IN-03", "Waiting for me", "Show emails waiting for my reply when I say [intent.waiting = waiting for my reply, need my reply, needs my reply, not answered, unanswered, to reply, I didn't answer]."),
                    R("IN-04", "No answer to me", "Show my emails nobody answered when I say [intent.noanswer = no answer to me, no reply to me, they didn't reply, nobody replied, follow up]."),
                    R("IN-05", "Logs", "A type (WIR, EI, MOM, RFI, letters, meetings…) with a list word (which, list, log, register, open, overdue, pending, this month…) opens the 📋 Submittal log with those filters; [intent.pending = pending, outstanding, still open, not closed, waiting] means Open."),
                    R("IN-06", "Windows", "Open a window by name: [intent.brain = open the brain, nova brain, my rules] · [intent.history = search history, my searches, my history] · [intent.settings = open settings, settings] · [intent.log = submittal log, open the log]."),
                    R("IN-07", "Follow-ups", "After a log, “only the overdue ones”, “now tower 2”, “level 5 to 8”, “export it”, “this month” change that log instead of starting a new search.")),
                S("projects", "🏗", "Projects & document numbers", "PJ", new[] { "all" },
                    R("PJ-01", "SLV", "SLV = Sea La Vie, uses Thinkproject. Numbers like SLV-ZUC-<discipline>-<type>-<number>_RevNN (ZUC = Züblin)."),
                    R("PJ-02", "RS5", "RS5 = Outlook only (no Thinkproject). Numbers like RS5-C7-ACC-<discipline>-<type>-<number>-RNN (ACC = contractor)."),
                    R("PJ-03", "Disciplines", "AR architecture · CW civil works · ST structure · ES electrical · MS mechanical/fire · PLB plumbing · ID interior design · LS landscape · MEP coordination."),
                    Filing.ProjectRules(R)[0], Filing.ProjectRules(R)[1]),
                // 📐 How every project file is labelled and filed (codes learned per project with 🧠 Learn this project).
                S("filing", "📐", "Filing & document codes", "FC", new[] { "files" }, Filing.BuiltIn(R)),
                S("meanings", "📖", "Meanings", "ME", new[] { "all" },
                    R("ME-01", "Codes", "Code 1 Approved · Code 2 Approved as noted / make corrections noted and proceed · Code 3 Revise and resubmit · Code 4 Rejected · Code 5 For information only."),
                    R("ME-02", "Other words", "DSR = contractor daily report · SOR = site observation report · NCR = non-conformance report · DC = document controller.")),
                S("location", "🧭", "Tower · zone · level", "TL", new[] { "submittal" },
                    R("TL-01", "Reading the place", "Read the place from the title: “T4”, “Tower 4”, “T 3_L3” → tower; “Z 01”, “Zone 1”, “Z-4 & 5” → zone; “L 12”, “L-4 to 6”, “10th Floor”, “GF”, “podium”, “attic” → level; “apt 1003,1004”, “TH 17,18,19” → apartments / townhouses.")),
                // Kept on the PC only (tag "local"): the private words themselves are never sent to the AI.
                S("privacy", "🔒", "Privacy", "PV", new[] { "local" },
                    R("PV-01", "Private words", "Files, folders and emails matching the 🔒 private words (Settings → 📁 Projects) are never read, never sent to the AI or to Google."),
                    R("PV-02", "Consent", "Pictures and recordings leave the PC only after you agree.")),
                S("lessons", "🧪", "Lessons learned", "LL", new[] { "all" },
                    R("LL-01", "Pictures in emails", "When replacing text in an email that has pictures or tables, change only the paragraphs that hold words; if that is not possible, change nothing and offer Copy."))
            )
        };
        return defaults;
    }
}
