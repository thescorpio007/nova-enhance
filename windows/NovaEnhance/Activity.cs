using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🧪 Activity history (for development): every task Nova does — searches, conversations, AI calls, sorting — with the time of each
/// step, what Nova understood and decided ("thinking"), and the text sent to the AI. Kept on this PC (encrypted, 7 / 14 / 30 days);
/// "📦 Export for Claude" makes one ZIP with names and email addresses hidden. Designed by A.HAMROUNI
/// </summary>
static class Activity
{
    public static bool On { get => Store.Bool("win_act_on", true); set => Store.SetBool("win_act_on", value); }
    public static int KeepDays { get => Math.Clamp(Store.Int("win_act_days", 30), 7, 30); set => Store.SetInt("win_act_days", value); }
    public static string Dir { get { var d = Path.Combine(Paths.Root, "Activity"); try { Directory.CreateDirectory(d); } catch { } return d; } }
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nova Enhance activity v1");
    static readonly AsyncLocal<Job> current = new();
    static readonly object L = new();

    /// <summary>The task running on this flow (deep code adds its steps to it), or null.</summary>
    public static Job Current => current.Value;

    public class Step { public string Name = "", Info = ""; public long Ms; }

    public class Job : IDisposable
    {
        public string Id = Guid.NewGuid().ToString("N")[..10], Feature = "", Title = "", Result = "";
        public DateTime Start = DateTime.Now;
        public long Ms;
        public bool Failed;
        public List<Step> Steps = new();
        public Dictionary<string, string> Think = new();
        public List<JsonObject> Ai = new();
        readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        long last;
        Job parent;
        bool done;

        internal void Enter() { parent = current.Value; current.Value = this; }

        /// <summary>Ends the step that started at the previous mark (its time is measured).</summary>
        public void Mark(string step, string info = "")
        {
            var now = sw.ElapsedMilliseconds;
            lock (Steps) Steps.Add(new Step { Name = step, Info = Cut(info, 600), Ms = now - last });
            last = now;
        }

        /// <summary>What Nova understood / decided (kept as text).</summary>
        public void Note(string key, string value) { lock (Think) Think[key] = Cut(value, 4000); }

        public void Fail(string why) { Failed = true; Result = Cut(why, 600); }

        public void Dispose()
        {
            if (done) return;
            done = true;
            Ms = sw.ElapsedMilliseconds;
            if (current.Value == this) current.Value = parent;
            Write(this);
        }
    }

    /// <summary>Starts a task: <c>using var t = Activity.Begin("email search", question);</c></summary>
    public static Job Begin(string feature, string title)
    {
        var t = new Job { Feature = feature, Title = Cut(title, 300) };
        t.Enter();
        return t;
    }

    /// <summary>An AI call (from Ai.Chat): which AI, how long, what was sent and answered (option C: the full text).</summary>
    public static void AiCall(string provider, string system, string user, string answer, long ms, string error = null)
    {
        if (!On) return;
        var o = new JsonObject
        {
            ["ai"] = provider, ["ms"] = ms, ["sent_chars"] = (system?.Length ?? 0) + (user?.Length ?? 0), ["answer_chars"] = answer?.Length ?? 0,
            ["system"] = Cut(system, 6000), ["sent"] = Cut(user, 20000), ["answer"] = Cut(answer, 8000)
        };
        if (error != null) o["error"] = Cut(error, 600);
        var t = current.Value;
        if (t != null) { lock (t.Ai) t.Ai.Add(o); t.Mark("✦ AI · " + provider, $"{ms} ms · {o["sent_chars"]} chars sent" + (error != null ? " · ⚠ " + error : "")); return; }
        // An AI call outside a task (e.g. background sorting): recorded on its own.
        var own = new Job { Feature = "ai call", Title = Cut(user, 120), Ms = ms, Failed = error != null, Result = error ?? "" };
        own.Ai.Add(o);
        own.Steps.Add(new Step { Name = "✦ AI · " + provider, Ms = ms });
        Write(own);
    }

    static string Cut(string s, int n) { s ??= ""; return s.Length > n ? s[..n] + "…" : s; }

    // ------------------------------------------------------------------ storing (one encrypted line per task, one file per day)

    static void Write(Job t)
    {
        if (!On) return;
        try
        {
            var o = ToJson(t);
            var line = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(o.ToJsonString()), Entropy, DataProtectionScope.CurrentUser));
            lock (L) File.AppendAllText(Path.Combine(Dir, $"{t.Start:yyyy-MM-dd}.act"), line + "\n");
        }
        catch { }
        Clean();
    }

    static JsonObject ToJson(Job t)
    {
        var steps = new JsonArray(); lock (t.Steps) foreach (var s in t.Steps) steps.Add(new JsonObject { ["step"] = s.Name, ["ms"] = s.Ms, ["info"] = s.Info });
        var think = new JsonObject(); lock (t.Think) foreach (var (k, v) in t.Think) think[k] = v;
        var ai = new JsonArray(); lock (t.Ai) foreach (var a in t.Ai) ai.Add(a.DeepClone());
        return new JsonObject
        {
            ["id"] = t.Id, ["start"] = t.Start.ToString("yyyy-MM-dd HH:mm:ss.fff"), ["feature"] = t.Feature, ["title"] = t.Title, ["ms"] = t.Ms,
            ["failed"] = t.Failed, ["result"] = t.Result, ["steps"] = steps, ["thinking"] = think, ["ai_calls"] = ai, ["build"] = Program.Build
        };
    }

    static DateTime cleaned;
    static void Clean()
    {
        if (DateTime.Now - cleaned < TimeSpan.FromHours(6)) return;
        cleaned = DateTime.Now;
        try
        {
            foreach (var f in Directory.GetFiles(Dir, "*.act"))
                if (DateTime.TryParse(Path.GetFileNameWithoutExtension(f), out var d) && d < DateTime.Today.AddDays(-KeepDays)) File.Delete(f);
        }
        catch { }
    }

    public static List<JsonObject> Read(int days)
    {
        var res = new List<JsonObject>();
        try
        {
            foreach (var f in Directory.GetFiles(Dir, "*.act").OrderBy(x => x))
            {
                if (!DateTime.TryParse(Path.GetFileNameWithoutExtension(f), out var d) || d < DateTime.Today.AddDays(-(days - 1))) continue;
                string[] lines; lock (L) lines = File.ReadAllLines(f);
                foreach (var l in lines)
                    try { res.Add(JsonNode.Parse(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(l), Entropy, DataProtectionScope.CurrentUser)))!.AsObject()); }
                    catch { }
            }
        }
        catch { }
        return res;
    }

    // ------------------------------------------------------------------ 📦 Export for Claude

    /// <summary>Names → "Person 12", addresses → "person12@…" (always the same label for the same person).</summary>
    class Hider
    {
        readonly Dictionary<string, int> ids = new(StringComparer.OrdinalIgnoreCase);
        readonly List<(Regex Rx, string Name)> names = new();
        static readonly Regex Mail = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

        int Id(string k) { if (!ids.TryGetValue(k, out var i)) ids[k] = i = ids.Count + 1; return i; }

        public Hider()
        {
            var people = new List<(string Name, string Addr)>();
            try
            {
                using var c = MailIndex.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT from_name, from_addr, COUNT(*) n FROM mail WHERE from_name<>'' GROUP BY lower(from_addr) ORDER BY n DESC LIMIT 4000";
                using var r = cmd.ExecuteReader();
                while (r.Read()) people.Add((r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
            }
            catch { }
            if (Me.Name.Length > 3) names.Add((Rx(Me.Name), "Me"));
            foreach (var (n, a) in people)
            {
                var clean = Regex.Replace(n, @"[,;""']", " ").Trim();
                if (clean.Length < 4 || !clean.Contains(' ')) continue;
                var label = "Person " + Id(a.Length > 0 ? a : clean);
                names.Add((Rx(clean), label));
                // "Ltaif, Mark" is also written "Mark Ltaif".
                var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) names.Add((Rx(parts[1] + " " + parts[0]), label));
            }
        }

        static Regex Rx(string n) => new(@"(?<![\p{L}])" + Regex.Escape(n).Replace(@"\ ", @"[\s,]+") + @"(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public string Hide(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Mail.Replace(s, m => Me.Addrs.Contains(m.Value, StringComparer.OrdinalIgnoreCase) ? "me@…" : $"person{Id(m.Value)}@…");
            foreach (var (rx, label) in names) if (s.Length < 200000) s = rx.Replace(s, label);
            return s;
        }

        public JsonNode Hide(JsonNode n) => n switch
        {
            JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, kv.Value == null ? null : Hide(kv.Value)))),
            JsonArray a => new JsonArray(a.Select(x => x == null ? null : Hide(x)).ToArray()),
            JsonValue v when v.TryGetValue<string>(out var str) => JsonValue.Create(Hide(str)),
            _ => n.DeepClone()
        };
    }

    // ------------------------------------------------------------------ 💾 Automatic save (every 1 h / 6 h / 1 day)

    /// <summary>0 = off, otherwise hours between automatic saves (1, 6 or 24).</summary>
    public static int AutoEvery { get => Store.Int("win_act_auto", 0); set { Store.SetInt("win_act_auto", value); } }
    public static DateTime LastAuto
    {
        get => DateTime.TryParse(Store.Str("win_act_auto_last", ""), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : DateTime.MinValue;
        set => Store.Set("win_act_auto_last", value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
    }
    public static string AutoFile => Path.Combine(Dir, "Nova-Activity-Auto.zip");
    static Thread autoThread;

    /// <summary>Background job: when the time comes (or was missed while Nova was closed) the Auto file is written again.</summary>
    public static void StartAuto()
    {
        if (autoThread != null) return;
        autoThread = new Thread(() => Guard.Run("activity auto-save", () =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(2));          // let Nova finish starting first
            while (true)
            {
                var h = AutoEvery;
                if (h > 0 && DateTime.Now - LastAuto >= TimeSpan.FromHours(h)) SaveAuto();
                Thread.Sleep(TimeSpan.FromMinutes(1));
            }
        })) { IsBackground = true, Name = "Nova activity auto-save", Priority = ThreadPriority.BelowNormal };
        autoThread.Start();
    }

    /// <summary>Writes Nova-Activity-Auto.zip (replacing the previous one only once the new one is complete).</summary>
    public static void SaveAuto()
    {
        try
        {
            var tmp = Export(KeepDays, null, true);
            File.Move(tmp, AutoFile, true);
            LastAuto = DateTime.Now;
        }
        catch (Exception e) { Log.Warn("activity", "automatic save: " + e.Message); }
    }

    /// <summary>📦 One ZIP. Yours: Nova-Activity-User-…zip (kept). Automatic: written as a temporary file, then it replaces Nova-Activity-Auto.zip.</summary>
    public static string Export(int days, Action<string> status = null, bool auto = false)
    {
        status?.Invoke("Reading the activity…");
        var items = Read(days);
        status?.Invoke("Hiding names and email addresses…");
        var hider = new Hider();
        var path = auto ? Path.Combine(Dir, "Nova-Activity-Auto.zip.part") : Path.Combine(Dir, $"Nova-Activity-User-{days}d-{DateTime.Now:yyyy-MM-dd-HHmm}.zip");
        if (auto) try { File.Delete(path); } catch { }
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                var e = z.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            Add("README.txt", $"Nova Enhance activity history · last {days} days · {items.Count:N0} tasks · {(auto ? $"saved automatically (every {AutoEvery} h)" : "saved by the user")} {DateTime.Now:dd MMM yyyy HH:mm} · build {Program.Build}\r\n" +
                              "Names and email addresses are replaced by Person N / personN@… (the same label for the same person). Project codes, subjects and file names are kept.\r\n" +
                              "activity.jsonl = one task per line: steps with milliseconds, thinking (what Nova understood and decided), AI calls with the text sent.\r\n" + Program.Sign + "\r\n");
            status?.Invoke("Writing the summary…");
            Add("summary.md", hider.Hide(Summary(items, days)));
            var sb = new StringBuilder();
            foreach (var it in items) sb.Append(hider.Hide(it).ToJsonString()).Append('\n');
            Add("activity.jsonl", sb.ToString());
            Add("errors.txt", hider.Hide(string.Join("\r\n\r\n", Log.ReadAll().Where(x => x.Time > DateTime.Now.AddDays(-days)).Select(Log.Format))));
            Add("system.txt", Log.Environment());
        }
        return path;
    }

    /// <summary>The readable summary: tasks per feature, average and slowest times, slow AI calls, failures.</summary>
    public static string Summary(List<JsonObject> items, int days)
    {
        long Ms(JsonObject o) => o["ms"]?.GetValue<long>() ?? 0;
        var sb = new StringBuilder($"# Nova activity · last {days} days\n\n{items.Count:N0} tasks · build {Program.Build}\n\n## Per feature\n\n| Feature | Tasks | Average | Slowest | Failed |\n|---|---|---|---|---|\n");
        foreach (var g in items.GroupBy(o => o["feature"]?.ToString() ?? "").OrderByDescending(g => g.Count()))
            sb.Append($"| {g.Key} | {g.Count()} | {g.Average(Ms) / 1000:0.0} s | {g.Max(Ms) / 1000.0:0.0} s | {g.Count(o => o["failed"]?.GetValue<bool>() == true)} |\n");
        sb.Append("\n## 10 slowest tasks\n\n");
        foreach (var o in items.OrderByDescending(Ms).Take(10))
        {
            sb.Append($"- **{Ms(o) / 1000.0:0.0} s** · {o["feature"]} · {o["start"]} · {o["title"]}\n");
            foreach (var s in o["steps"]?.AsArray() ?? new JsonArray()) sb.Append($"  - {s?["step"]} — {s?["ms"]} ms {s?["info"]}\n");
        }
        var ai = items.SelectMany(o => o["ai_calls"]?.AsArray() ?? new JsonArray()).Where(x => x != null).Select(x => x!.AsObject()).ToList();
        sb.Append($"\n## AI calls\n\n{ai.Count} calls");
        foreach (var g in ai.GroupBy(a => a["ai"]?.ToString() ?? ""))
            sb.Append($"\n- {g.Key}: {g.Count()} calls · average {g.Average(a => a["ms"]?.GetValue<long>() ?? 0) / 1000:0.0} s · {g.Count(a => a["error"] != null)} failed");
        sb.Append("\n\n## Failed tasks\n\n");
        foreach (var o in items.Where(o => o["failed"]?.GetValue<bool>() == true).Take(30)) sb.Append($"- {o["start"]} · {o["feature"]} · {o["title"]} — {o["result"]}\n");
        return sb.ToString();
    }
}
