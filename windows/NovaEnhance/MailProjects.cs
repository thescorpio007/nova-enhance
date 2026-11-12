using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NovaEnhance;

/// <summary>
/// 📁 Emails per project: every email of the index is sorted into one of your projects, on this PC — project tag in the subject,
/// document numbers, project names / email codes, Outlook folder, same conversation, learned senders — and (option B) the AI only
/// for the emails those clues can't settle (subject + first lines). Your corrections always win. Designed by A.HAMROUNI
/// </summary>
static class MailProjects
{
    public const string Gen = "GEN";
    public static readonly string[] RuleNames =
    {
        "", "🏷 Project tag in the subject", "📐 Document numbers", "📛 Project names & email codes", "🗂 Outlook folder",
        "🧵 Same conversation", "👤 Learned senders", "✦ AI for the unclear ones", "✋ Sender set by you", "✋ Moved by you"
    };
    public static readonly string[] RuleHelp =
    {
        "", "“RE: SLV # WIR …”, “[NOU] …” — the project code as a word in the subject", "SLV-ZUC-AR-SSM-00171 in the subject or attachment names",
        "the project name and the extra names below, in the subject or the first lines", "emails filed in an Outlook folder linked below (sub-folders too)",
        "a reply without a code takes the project of its thread", "a sender / company that writes about one project only (≥ 90 % of their sorted emails)",
        "subject + first lines only · your project list · answers a project or “not sure”"
    };
    public static readonly string[] RuleStrength = { "", "strong", "strong", "medium", "strong", "medium", "helper", "last" };

    public static bool AiOn { get => Store.Bool("win_mproj_ai", true); set { Store.SetBool("win_mproj_ai", value); wake.Set(); } }

    /// <summary>Order of the rules that read the email itself (1–4); 5, 6, 7 then sort what is left.</summary>
    public static List<int> Order
    {
        get
        {
            var o = Store.Str("win_mproj_order", "1,2,3,4,5,6,7").Split(',').Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i is >= 1 and <= 7).Distinct().ToList();
            foreach (var i in Enumerable.Range(1, 7)) if (!o.Contains(i)) o.Add(i);
            return o;
        }
        set => Store.Set("win_mproj_order", string.Join(",", value));
    }

    public static bool RuleOn(int r) => !Store.Str("win_mproj_off", "").Split(',').Contains(r.ToString());
    public static void SetRule(int r, bool on)
    {
        var off = Store.Str("win_mproj_off", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (on) off.Remove(r.ToString()); else off.Add(r.ToString());
        Store.Set("win_mproj_off", string.Join(",", off));
    }

    public class Cfg { public List<string> Names = new(), Folders = new(), Senders = new(); }
    static readonly JsonSerializerOptions J = new() { IncludeFields = true };

    public static Dictionary<string, Cfg> Config
    {
        get { try { return JsonSerializer.Deserialize<Dictionary<string, Cfg>>(Store.Str("win_mproj_cfg", "{}"), J) ?? new(); } catch { return new(); } }
        set => Store.Set("win_mproj_cfg", JsonSerializer.Serialize(value ?? new(), J));
    }

    public static Cfg CfgOf(string code) { var c = Config; return c.TryGetValue(code, out var x) ? x : new Cfg(); }
    public static void SaveCfg(string code, Cfg cfg) { var c = Config; c[code] = cfg; Config = c; }

    /// <summary>Projects for the email list: your projects + GEN (general / office).</summary>
    public static List<(string Code, string Label)> Choices() =>
        Projects.All().Where(p => p.Code.Length > 0).Select(p => (p.Code, p.Name.Length > 0 && p.Name != p.Code ? $"{p.Code} · {p.Name}" : p.Code))
            .Append((Gen, "GEN · General / office")).GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    public static string Label(string code) => code == "" ? "🌐 All projects" : code == "?" ? "❔ Unsorted" : "📁 " + (Choices().FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Label ?? code);

    // ------------------------------------------------------------------ state

    public static volatile int Done, Total, Sorted;
    public static volatile string Phase = "starting…";
    public static Dictionary<string, int> Counts = new();
    public static int[] RuleCounts = new int[10];
    public static Dictionary<string, string> Learned = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<long, string> map = new();
    static readonly object L = new();
    static readonly AutoResetEvent wake = new(false);
    static Thread thread;
    static bool ready;

    /// <summary>The project of an email (by its index id), "" when unsorted or not sorted yet.</summary>
    public static string Of(long id) { lock (L) return map.TryGetValue(id, out var p) ? p : ""; }

    public static void Ensure()
    {
        if (ready) return;
        using var c = MailIndex.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS mproj(id INTEGER PRIMARY KEY, proj TEXT, rule INTEGER);" +
                          "CREATE INDEX IF NOT EXISTS mproj_p ON mproj(proj);" +
                          "CREATE TABLE IF NOT EXISTS mfix(entry TEXT PRIMARY KEY, proj TEXT);";
        cmd.ExecuteNonQuery();
        ready = true;
    }

    public static void Start()
    {
        if (thread != null) return;
        // Build 52: stricter rules (documents of another project are never claimed) → sort once again.
        if (Store.Int("win_mproj_ver", 0) < 2) { redo = true; Store.SetInt("win_mproj_ver", 2); }
        thread = new Thread(() => Guard.Run("email projects", Loop)) { IsBackground = true, Name = "Nova email projects", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    public static void Wake() => wake.Set();

    static volatile bool redo;
    /// <summary>"↻ Sort everything again" (after you change codes, names, folders or rules). Your corrections are kept.</summary>
    public static void SortAgain() { redo = true; wake.Set(); }

    // ------------------------------------------------------------------ your corrections

    public static void Move(IEnumerable<MailHit> hs, string code)
    {
        try
        {
            Ensure();
            using var c = MailIndex.Open();
            using var tx = c.BeginTransaction();
            foreach (var h in hs)
            {
                using var a = c.CreateCommand(); a.Transaction = tx;
                a.CommandText = "INSERT OR REPLACE INTO mfix(entry,proj) VALUES($e,$p); INSERT OR REPLACE INTO mproj(id,proj,rule) VALUES($i,$p,9);";
                a.Parameters.AddWithValue("$e", h.Entry ?? ""); a.Parameters.AddWithValue("$p", code); a.Parameters.AddWithValue("$i", h.Id);
                a.ExecuteNonQuery();
                lock (L) map[h.Id] = code;
            }
            tx.Commit();
            graphAt = DateTime.MinValue; wake.Set();
        }
        catch (Exception e) { Log.Warn("email projects", "move: " + e.Message); }
    }

    /// <summary>✋ Always: this sender → project (their other emails move too, except the ones you moved yourself).</summary>
    public static void AlwaysSender(string addr, string code)
    {
        addr = (addr ?? "").Trim().ToLowerInvariant();
        if (addr.Length == 0) return;
        foreach (var (k, cfg) in Config) if (cfg.Senders.Remove(addr)) SaveCfg(k, cfg);
        var my = CfgOf(code); my.Senders.Add(addr); SaveCfg(code, my);
        try
        {
            using var c = MailIndex.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE mproj SET proj=$p, rule=8 WHERE rule<>9 AND id IN (SELECT id FROM mail WHERE lower(from_addr)=$a)";
            cmd.Parameters.AddWithValue("$p", code); cmd.Parameters.AddWithValue("$a", addr);
            cmd.ExecuteNonQuery();
            Reload(c);
        }
        catch (Exception e) { Log.Warn("email projects", "sender: " + e.Message); }
    }

    public static List<(string Entry, string Proj, string Subject)> Corrections()
    {
        var res = new List<(string, string, string)>();
        try
        {
            using var c = MailIndex.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT f.entry, f.proj, IFNULL((SELECT subject FROM mail m WHERE m.entry=f.entry LIMIT 1),'') FROM mfix f";
            using var r = cmd.ExecuteReader();
            while (r.Read()) res.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        catch { }
        return res;
    }

    public static void Undo(string entry)
    {
        try
        {
            using var c = MailIndex.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM mfix WHERE entry=$e; DELETE FROM mproj WHERE id IN (SELECT id FROM mail WHERE entry=$e);";
            cmd.Parameters.AddWithValue("$e", entry);
            cmd.ExecuteNonQuery();
            wake.Set();
        }
        catch { }
    }

    // ------------------------------------------------------------------ the rules

    class Row { public long Id; public string Entry, Subject, Att, Folder, Conv, From, Body; public bool Sent; }

    class Rules
    {
        public List<(string Code, List<string> Codes, List<string> Names, List<string> Folders)> P = new();
        public Dictionary<string, string> Senders = new(StringComparer.OrdinalIgnoreCase), Fixes = new();
        public List<int> Order;
        public HashSet<int> On;
    }

    static Rules Load(SqliteConnection c)
    {
        var r = new Rules { Order = Order, On = Enumerable.Range(1, 7).Where(RuleOn).ToHashSet() };
        var cfg = Config;
        foreach (var p in Projects.All().Where(p => p.Code.Length > 0))
        {
            var x = cfg.TryGetValue(p.Code, out var cc) ? cc : new Cfg();
            r.P.Add((p.Code, p.Codes.Append(p.Code).Where(s => s.Trim().Length >= 2).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                (p.Name.Length > 3 ? new List<string> { p.Name } : new()).Concat(x.Names).Where(s => s.Trim().Length >= 3).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                x.Folders.Where(s => s.Trim().Length > 0).ToList()));
            foreach (var s in x.Senders) r.Senders[s] = p.Code;
        }
        if (cfg.TryGetValue(Gen, out var g))
        {
            r.P.Add((Gen, new List<string>(), g.Names.ToList(), g.Folders.ToList()));
            foreach (var s in g.Senders) r.Senders[s] = Gen;
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT entry, proj FROM mfix";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) r.Fixes[rd.GetString(0)] = rd.GetString(1);
        return r;
    }

    static readonly Regex Re = new(@"^\s*((re|fw|fwd|aw|wg)\s*:\s*)+", RegexOptions.IgnoreCase);
    static bool Word(string text, string w, bool caseSens) =>
        Regex.IsMatch(text, @"(?<![A-Za-z0-9])" + Regex.Escape(w) + @"(?![A-Za-z0-9])", caseSens ? RegexOptions.None : RegexOptions.IgnoreCase);
    // A code written in capitals is matched in capitals only ("MAR" the project, not "Mar 2026").
    static bool Code(string text, string code) => Word(text, code, code.Any(char.IsLetter) && code == code.ToUpperInvariant());

    /// <summary>(project, rule) for one email; rule 0 = no clue, −1 = two projects tie.</summary>
    static (string, int) Classify(Row m, Rules R)
    {
        if (R.Fixes.TryGetValue(m.Entry ?? "", out var fx)) return (fx, 9);
        var from = (m.From ?? "").ToLowerInvariant();
        if (from.Length > 0)
        {
            if (R.Senders.TryGetValue(from, out var sp)) return (sp, 8);
            var at = from.IndexOf('@');
            if (at > 0 && R.Senders.TryGetValue(from[at..], out var dp)) return (dp, 8);
        }
        var subj = Re.Replace(m.Subject ?? "", "");
        // Document numbers of a project that is not in your list (RS5-C7-…): no weak rule may claim it.
        var pre = Prefixes(subj);
        bool foreign = pre.Count > 0 && !pre.Any(x => R.P.Any(p => p.Codes.Any(c => c.StartsWith(x, StringComparison.OrdinalIgnoreCase) || x.Equals(c, StringComparison.OrdinalIgnoreCase)))) &&
                       !R.P.Any(p => p.Codes.Any(c => Code(subj, c)));
        if (foreign) return ("", -3);
        foreach (var rule in R.Order.Where(x => x <= 4 && R.On.Contains(x)))
        {
            var hits = new HashSet<string>();
            foreach (var p in R.P)
            {
                bool ok = rule switch
                {
                    1 => p.Codes.Any(c => Code(subj, c)),
                    2 => Revisions.Numbers(subj + "\n" + (m.Att ?? "")).Any(n => p.Codes.Any(c => n.StartsWith(c + "-", StringComparison.OrdinalIgnoreCase))),
                    3 => p.Names.Any(n => subj.Contains(n, StringComparison.OrdinalIgnoreCase) || (m.Body ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)) ||
                         p.Codes.Any(c => c.Length >= 3 && Code(m.Body ?? "", c)),
                    4 => p.Folders.Any(f => (m.Folder ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)),
                    _ => false
                };
                if (ok) hits.Add(p.Code);
            }
            if (hits.Count == 1) return (hits.First(), rule);
            if (hits.Count > 1)
            {
                // Two projects: the one written first in the subject wins when the subject decides; otherwise no guessing.
                if (rule is 1 or 2)
                {
                    var first = hits.Select(h => (h, i: R.P.First(p => p.Code == h).Codes.Select(c => subj.IndexOf(c, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(int.MaxValue).Min()))
                        .OrderBy(x => x.i).ToList();
                    if (first.Count >= 2 && first[0].i < first[1].i && first[0].i < 12) return (first[0].h, rule);
                }
                return ("", -1);
            }
        }
        return ("", 0);
    }

    // ------------------------------------------------------------------ the background sorter

    static DateTime graphAt = DateTime.MinValue;
    static int insertedSinceGraph;

    static void Loop()
    {
        Thread.Sleep(TimeSpan.FromSeconds(45));
        while (true)
        {
            try
            {
                Ensure();
                using var c = MailIndex.Open();
                // The email indexer writes in big batches: wait for it (up to 30 s) instead of failing.
                using (var bt = c.CreateCommand()) { bt.CommandText = "PRAGMA busy_timeout=30000;"; bt.ExecuteNonQuery(); }
                if (redo)
                {
                    redo = false;
                    using var d = c.CreateCommand(); d.CommandText = "DELETE FROM mproj"; d.ExecuteNonQuery();
                    lock (L) map = new();
                    Done = 0; insertedSinceGraph = 1;
                }
                var R = Load(c);
                // 1) Every email not sorted yet: the clues in the email itself.
                var rows = new List<Row>();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT m.id,m.entry,m.subject,m.att_names,m.folder,m.conv,m.from_addr,substr(m.body,1,600),m.sent FROM mail m LEFT JOIN mproj p ON p.id=m.id WHERE p.id IS NULL ORDER BY m.id DESC LIMIT 3000";
                    using var r = cmd.ExecuteReader();
                    string S(SqliteDataReader x, int i) => x.IsDBNull(i) ? "" : x.GetString(i);
                    while (r.Read()) rows.Add(new Row { Id = r.GetInt64(0), Entry = S(r, 1), Subject = S(r, 2), Att = S(r, 3), Folder = S(r, 4), Conv = S(r, 5), From = S(r, 6), Body = S(r, 7), Sent = r.GetInt64(8) == 1 });
                }
                if (rows.Count > 0)
                {
                    Phase = "sorting by subject codes, numbers, names and folders…";
                    using var tx = c.BeginTransaction();
                    using var ins = c.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT OR REPLACE INTO mproj(id,proj,rule) VALUES($i,$p,$r)";
                    var pi = ins.Parameters.Add("$i", SqliteType.Integer); var pp = ins.Parameters.Add("$p", SqliteType.Text); var pr = ins.Parameters.Add("$r", SqliteType.Integer);
                    var add = new List<(long, string)>();
                    foreach (var m in rows)
                    {
                        var (p, rule) = Classify(m, R);
                        pi.Value = m.Id; pp.Value = p; pr.Value = rule; ins.ExecuteNonQuery();
                        if (p.Length > 0) add.Add((m.Id, p));
                    }
                    tx.Commit();
                    lock (L) foreach (var (i, p) in add) map[i] = p;
                    insertedSinceGraph += rows.Count;
                    Count(c);
                    Thread.Sleep(80);
                    continue;
                }
                // 2) Same conversation and 3) learned senders — after new emails were sorted (and every 10 minutes).
                if (insertedSinceGraph > 0 || DateTime.Now - graphAt > TimeSpan.FromMinutes(10))
                {
                    Phase = "sorting replies by conversation and by sender…";
                    Graph(c, R);
                    insertedSinceGraph = 0; graphAt = DateTime.Now;
                    Reload(c);
                }
                // 4) ✦ The AI, for the emails no clue settles (option B).
                if (AiOn && RuleOn(7) && Ai.Active() != null && (!Gentle.On || !Gentle.WorkingNow || Gentle.Idle > TimeSpan.FromMinutes(2)))
                {
                    var n = AiBatch(c);
                    if (n > 0) { Count(c); Thread.Sleep(2500); continue; }
                }
                Count(c);
                Phase = Total > 0 && Done >= Total ? "✓ up to date" : "waiting for the email index…";
                wake.WaitOne(TimeSpan.FromMinutes(2));
            }
            catch (Exception e) when (e.Message.Contains("locked", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("busy", StringComparison.OrdinalIgnoreCase))
            {
                // The indexer is writing: try again shortly (not an error).
                Phase = "waiting for the email indexer…";
                Thread.Sleep(TimeSpan.FromSeconds(15));
            }
            catch (Exception e)
            {
                Phase = "⚠ " + e.Message;
                Log.Warn("email projects", e.Message);
                wake.WaitOne(TimeSpan.FromMinutes(3));
            }
        }
    }

    static void Graph(SqliteConnection c, Rules R)
    {
        var all = new List<(long Id, string Proj, int Rule, string Conv, string From, bool Sent)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT p.id,p.proj,p.rule,IFNULL(m.conv,''),lower(IFNULL(m.from_addr,'')),m.sent FROM mproj p JOIN mail m ON m.id=p.id";
            using var r = cmd.ExecuteReader();
            while (r.Read()) all.Add((r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetInt64(5) == 1));
        }
        var upd = new List<(long, string, int)>();
        // 🧵 Same conversation: one project among the sorted emails of the thread → the others take it.
        if (RuleOn(5))
        {
            var conv = all.Where(x => x.Conv.Length > 0 && x.Proj.Length > 0 && x.Rule is >= 1 and <= 4 or 8 or 9)
                .GroupBy(x => x.Conv).Select(g => (g.Key, P: g.Select(x => x.Proj).Distinct().ToList())).Where(x => x.P.Count == 1).ToDictionary(x => x.Key, x => x.P[0]);
            foreach (var x in all.Where(x => x.Rule is 0 or 7 or -2 && x.Proj.Length == 0 && x.Conv.Length > 0))
                if (conv.TryGetValue(x.Conv, out var p)) upd.Add((x.Id, p, 5));
        }
        // 👤 Learned senders: ≥ 90 % of a sender's (or company's) sorted emails are one project.
        var learned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (RuleOn(6))
        {
            var mine = new HashSet<string>(Me.Addrs.Select(a => a.ToLowerInvariant()));
            var myDomains = new HashSet<string>(mine.Select(a => a.Contains('@') ? a[a.IndexOf('@')..] : a));
            var publicDomains = new HashSet<string> { "@gmail.com", "@outlook.com", "@hotmail.com", "@yahoo.com", "@yahoo.fr", "@live.com", "@icloud.com" };
            var recv = all.Where(x => !x.Sent && x.From.Length > 0 && !mine.Contains(x.From) && x.Proj.Length > 0 && x.Rule is >= 1 and <= 5 or 9).ToList();
            void Learn(IEnumerable<IGrouping<string, (long Id, string Proj, int Rule, string Conv, string From, bool Sent)>> groups, int min)
            {
                foreach (var g in groups)
                {
                    var n = g.Count();
                    if (n < min) continue;
                    var top = g.GroupBy(x => x.Proj).OrderByDescending(x => x.Count()).First();
                    if (top.Count() >= n * 0.9) learned[g.Key] = top.Key;
                }
            }
            Learn(recv.GroupBy(x => x.From), 5);
            Learn(recv.Where(x => x.From.Contains('@')).GroupBy(x => x.From[x.From.IndexOf('@')..]).Where(g => !myDomains.Contains(g.Key) && !publicDomains.Contains(g.Key)), 12);
            var done = upd.Select(u => u.Item1).ToHashSet();
            foreach (var x in all.Where(x => x.Rule is 0 or 7 or -2 && x.Proj.Length == 0 && x.From.Length > 0 && !x.Sent && !done.Contains(x.Id)))
            {
                if (learned.TryGetValue(x.From, out var p)) upd.Add((x.Id, p, 6));
                else if (x.From.Contains('@') && learned.TryGetValue(x.From[x.From.IndexOf('@')..], out var dp)) upd.Add((x.Id, dp, 6));
            }
        }
        lock (L) Learned = learned;
        if (upd.Count == 0) return;
        using var tx = c.BeginTransaction();
        using var u2 = c.CreateCommand();
        u2.Transaction = tx;
        u2.CommandText = "UPDATE mproj SET proj=$p, rule=$r WHERE id=$i";
        var pi = u2.Parameters.Add("$i", SqliteType.Integer); var pp = u2.Parameters.Add("$p", SqliteType.Text); var pr = u2.Parameters.Add("$r", SqliteType.Integer);
        foreach (var (i, p, r) in upd) { pi.Value = i; pp.Value = p; pr.Value = r; u2.ExecuteNonQuery(); }
        tx.Commit();
    }

    static int AiBatch(SqliteConnection c)
    {
        var choices = Choices();
        if (choices.Count < 2) return 0;
        var batch = new List<(long Id, string Text)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT m.id, IFNULL(m.subject,''), substr(IFNULL(m.body,''),1,400), IFNULL(m.from_name,''), IFNULL(m.att_names,'') FROM mproj p JOIN mail m ON m.id=p.id WHERE p.rule IN (0,-1) ORDER BY m.date DESC LIMIT 30";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var subj = r.GetString(1); var body = r.GetString(2);
                if (Projects.Private.IsMatch(subj) || Projects.Private.IsMatch(body)) { batch.Add((r.GetInt64(0), null)); continue; }   // 🔒 never sent
                var first = Regex.Replace(MailIndex.NewPart(body), @"\s+", " ").Trim();
                batch.Add((r.GetInt64(0), $"{subj} · from {r.GetString(3)}{(r.GetString(4).Length > 0 ? " · 📎 " + r.GetString(4) : "")} · “{(first.Length > 160 ? first[..160] : first)}”"));
            }
        }
        if (batch.Count == 0) return 0;
        Phase = $"✦ asking the AI about {batch.Count} unclear emails (subject + first lines)…";
        var res = new Dictionary<long, string>();
        var send = batch.Where(b => b.Text != null).ToList();
        if (send.Count > 0)
        {
            var sb = new StringBuilder("Projects:\n");
            foreach (var p in Projects.All().Where(p => p.Code.Length > 0)) sb.Append($"- {p.Code}: {p.Name}{(p.Codes.Count > 0 ? " (codes " + string.Join(", ", p.Codes.Take(6)) + ")" : "")}\n");
            sb.Append("- GEN: general office matters (HR, IT, admin, company news, several projects at once)\nEmails:\n");
            for (int i = 0; i < send.Count; i++) sb.Append($"[{i + 1}] {send[i].Text}\n");
            var sys = "You sort an architect's work emails into his projects. Reply with JSON only: {\"p\":{\"1\":\"SLV\",\"2\":\"?\"}} — the project code for each email, or \"?\" when you are not sure. Never guess.";
            try
            {
                // Your cloud AI (Gemini first), 60 s at most — the local model took up to 30 minutes per batch.
                using var lim = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var j = Ai.ParseJson(Ai.Chat(sys, sb.ToString(), true, true, SmartSearch.Cloud(), lim.Token).GetAwaiter().GetResult());
                if (j["p"] is JsonObject o)
                    foreach (var (k, v) in o)
                        if (int.TryParse(k, out var i) && i >= 1 && i <= send.Count)
                        {
                            var code = v?.ToString()?.Trim() ?? "?";
                            if (choices.Any(ch => ch.Code.Equals(code, StringComparison.OrdinalIgnoreCase))) res[send[i - 1].Id] = choices.First(ch => ch.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Code;
                        }
            }
            catch (Exception e) { Phase = "⚠ AI: " + e.Message; Log.Warn("email projects", "ai: " + e.Message); return 0; }
        }
        using var tx = c.BeginTransaction();
        using var u = c.CreateCommand();
        u.Transaction = tx;
        u.CommandText = "UPDATE mproj SET proj=$p, rule=$r WHERE id=$i";
        var pi = u.Parameters.Add("$i", SqliteType.Integer); var pp = u.Parameters.Add("$p", SqliteType.Text); var pr = u.Parameters.Add("$r", SqliteType.Integer);
        foreach (var b in batch)
        {
            var ok = res.TryGetValue(b.Id, out var p);
            pi.Value = b.Id; pp.Value = ok ? p : ""; pr.Value = ok ? 7 : -2;
            u.ExecuteNonQuery();
            if (ok) lock (L) map[b.Id] = p;
        }
        tx.Commit();
        return batch.Count;
    }

    static void Reload(SqliteConnection c)
    {
        var m = new Dictionary<long, string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, proj FROM mproj WHERE proj<>''";
        using var r = cmd.ExecuteReader();
        while (r.Read()) m[r.GetInt64(0)] = r.GetString(1);
        lock (L) map = m;
    }

    static void Count(SqliteConnection c)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM mail), (SELECT COUNT(*) FROM mproj), (SELECT COUNT(*) FROM mproj WHERE proj<>'')";
            using (var r = cmd.ExecuteReader()) if (r.Read()) { Total = r.GetInt32(0); Done = r.GetInt32(1); Sorted = r.GetInt32(2); }
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            cmd.CommandText = "SELECT proj, COUNT(*) FROM mproj GROUP BY proj";
            using (var r = cmd.ExecuteReader()) while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
            var rules = new int[10];
            cmd.CommandText = "SELECT rule, COUNT(*) FROM mproj WHERE proj<>'' GROUP BY rule";
            using (var r = cmd.ExecuteReader()) while (r.Read()) { var k = r.GetInt32(0); if (k is >= 0 and < 10) rules[k] = r.GetInt32(1); }
            Counts = counts; RuleCounts = rules;
            if (map.Count == 0 && Sorted > 0) Reload(c);
        }
        catch { }
    }

    static readonly Regex DocLike = new(@"(?<![A-Za-z0-9])([A-Z][A-Z0-9]{1,6})-[A-Z0-9]{1,6}-[A-Z0-9\-_]{2,}", RegexOptions.Compiled);

    /// <summary>The codes a subject's document numbers start with (SLV-ZUC-… → SLV · RS5-C7-ACC-… → RS5).</summary>
    static List<string> Prefixes(string text) => DocLike.Matches(text ?? "").Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>📁 Strict project check for a search: sorted into this project, and the subject does not name ANOTHER project
    /// (another code, or document numbers like RS5-C7-… that are not this project's).</summary>
    public static bool Fits(MailHit h, string code)
    {
        if (code == "?") return Of(h.Id).Length == 0;
        var p = Of(h.Id);
        if (p.Length > 0 && !p.Equals(code, StringComparison.OrdinalIgnoreCase)) return false;
        var subj = Re.Replace(h.Subject ?? "", "");
        var proj = Projects.All().FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        var mine = proj == null ? new List<string> { code } : proj.Codes.Append(proj.Code).Where(c => c.Length >= 2).ToList();
        if (mine.Any(c => Code(subj, c))) return true;
        var pre = Prefixes(subj + " " + (h.AttNames ?? ""));
        if (pre.Any(x => mine.Any(c => c.StartsWith(x, StringComparison.OrdinalIgnoreCase) || x.Equals(c, StringComparison.OrdinalIgnoreCase)))) return true;
        if (pre.Count > 0) return false;   // only another project's document numbers
        foreach (var o in Projects.All().Where(o => !o.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
            if (o.Codes.Append(o.Code).Where(c => c.Length >= 2).Any(c => Code(subj, c))) return false;
        return true;
    }

    public static int CountOf(string code) => code == "" ? Total : Counts.TryGetValue(code == "?" ? "" : code, out var n) ? n : 0;
}
