using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 📋 Submittal log: every WIR · MIR · MAR · PQS · RFI · SDS found in Outlook — submitted through Thinkproject (SLV) or by plain email (RS5) —
/// with the MZA reply on the same thread (closed, code, date, who), revisions, tower · zone · level, overdue, and an Excel log
/// (Log · Open & overdue · Summary · Check · About). Its rules come from the 🧠 Nova Brain (section 📋 Submittal logs). Designed by A.HAMROUNI
/// </summary>
static class SubmittalLog
{
    public class Options
    {
        public List<string> Projects = new();   // SLV, RS5 … (empty = all)
        public List<string> Types = new();      // WIR, MIR … (empty = the brain's list)
        public DateTime From = DateTime.Today.AddDays(-30), To = DateTime.Today.AddDays(1).AddSeconds(-1);
        public string Tower = "", Zone = "", Discipline = "", Status = "";
        public int LevelFrom = -99, LevelTo = 999;
    }

    public class Item
    {
        public string Project = "", Via = "", Type = "", No = "", Rev = "", Disc = "", Title = "", Tower = "", Zone = "", Level = "", Apt = "";
        public DateTime? Submitted, Deadline, Reply;
        public string By = "", Code = "", Status = "", Why = "", Check = "";
        public int RevNum, Emails;
        public MailHit Sub, Rep;
        public int Days => Submitted is { } s ? (int)((Reply ?? DateTime.Now) - s).TotalDays : 0;
        public int? LevelNum;
    }

    public class Result { public List<Item> Items = new(); public int Mails; public Options Opt; public double Seconds; }

    static readonly Regex Prefix = new(@"^\s*((re|aw|antw|fw|fwd|tr)\s*:\s*)+", RegexOptions.IgnoreCase);

    static Regex NumRx(IEnumerable<string> types) => new(
        @"\b([A-Z0-9]{2,6}(?:-[A-Z0-9]{1,6}){1,4}?-(" + string.Join("|", types.Select(Regex.Escape)) + @")-(\d{2,6}))(?:[_\- ]*(?:Rev|R)\s?(\d{1,2}))?",
        RegexOptions.IgnoreCase);

    public static Result Run(Options o, JsonObject brain = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        brain ??= Brain.Snapshot();
        var types = (o.Types.Count > 0 ? o.Types : Brain.ListOf("log.types", "WIR, MIR, MAR, PQS, RFI, SDS", brain)).Select(t => t.ToUpperInvariant()).ToList();
        var mza = Brain.ListOf("mza.match", "MZ ARCHITECTS, mz-architects.com", brain);
        var closedFiles = Brain.ListOf("closed.files", "signed, -C1, -C2, -C3, -C4, reply", brain);
        var rx = NumRx(types);
        // Replies come after the submission: read up to today.
        var mails = MailIndex.Submittals(types, o.From.AddDays(-3), DateTime.Now.AddDays(1));
        ct.ThrowIfCancellationRequested();

        bool IsMza(MailHit h) => h.Sent || mza.Any(m => (h.FromName ?? "").Contains(m, StringComparison.OrdinalIgnoreCase) || (h.FromAddr ?? "").Contains(m, StringComparison.OrdinalIgnoreCase));

        var docs = new Dictionary<string, Dictionary<int, Item>>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in mails.OrderBy(h => h.Date))
        {
            var subj = h.Subject ?? "";
            var m = rx.Match(subj);
            if (!m.Success) continue;
            var no = m.Groups[1].Value.ToUpperInvariant();
            var type = m.Groups[2].Value.ToUpperInvariant();
            int rev = int.TryParse(m.Groups[4].Value, out var rv) ? rv : 0;
            var proj = no.Split('-')[0];
            if (o.Projects.Count > 0 && !o.Projects.Any(p => p.Equals(proj, StringComparison.OrdinalIgnoreCase))) continue;
            if (!docs.TryGetValue(no, out var revs)) docs[no] = revs = new();
            if (!revs.TryGetValue(rev, out var it))
                revs[rev] = it = new Item { No = no, Type = type, Project = proj, RevNum = rev, Disc = no.Split('-').Length >= 4 ? no.Split('-')[^3] : "" };
            it.Emails++;
            var isReply = Regex.IsMatch(subj, @"^\s*(re|aw|antw)\s*:", RegexOptions.IgnoreCase);
            var isFwd = Regex.IsMatch(subj, @"^\s*(fw|fwd|tr)\s*:", RegexOptions.IgnoreCase) || Regex.IsMatch(subj, @"^\s*(automatic reply|auto|out of office)", RegexOptions.IgnoreCase);
            var thinkproject = (h.FromAddr ?? "").Contains("thinkproject", StringComparison.OrdinalIgnoreCase) || (h.Body ?? "").Contains("thinkproject", StringComparison.OrdinalIgnoreCase);
            if (thinkproject) it.Via = "Thinkproject"; else if (it.Via.Length == 0) it.Via = "Outlook";
            var title = TitleOf(subj, m);
            if (it.Title.Length == 0 || (!isReply && !isFwd)) it.Title = title.Length > 0 ? title : it.Title;

            if (!isReply && !isFwd && !IsMza(h))
            {
                if (it.Sub == null)
                {
                    it.Sub = h; it.Submitted = h.Date;
                    if (thinkproject) { var (dl, created) = TpDates(h.Body); it.Deadline = dl; if (created != null && created < h.Date.Date.AddDays(1)) it.Submitted = h.Date; }
                }
                continue;
            }
            if (isReply && IsMza(h))
            {
                var body = h.Body ?? "";
                var atts = h.AttNames ?? "";
                bool done;
                if (thinkproject)
                    done = Regex.IsMatch(body, @"Approval completed[\s\S]{0,600}?\byes\b", RegexOptions.IgnoreCase) || Regex.IsMatch(body, @"Final step", RegexOptions.IgnoreCase)
                           || Regex.IsMatch(body + " " + atts, @"-C[1-5]\.pdf", RegexOptions.IgnoreCase);
                else
                    done = closedFiles.Any(f => atts.Contains(f, StringComparison.OrdinalIgnoreCase) || subj.Contains(f, StringComparison.OrdinalIgnoreCase)) || h.Sent || Regex.IsMatch(body, @"\bcode\s*[1-5]\b|approved|no objection|rejected|revise and resubmit", RegexOptions.IgnoreCase);
                var code = CodeOf(body, atts, subj);
                if (done && (it.Rep == null || h.Date < it.Rep.Date))
                {
                    it.Rep = h; it.Reply = h.Date; it.By = h.Sent ? "MZA (me)" : (h.FromName ?? "");
                    if (code.Length > 0) it.Code = code;
                }
                else if (!done && it.Rep == null && it.Status.Length == 0) it.Status = "Reviewing";
                if (it.Code.Length == 0 && code.Length > 0 && done) it.Code = code;
            }
        }

        var res = new Result { Mails = mails.Count, Opt = o };
        foreach (var (no, revs) in docs)
        {
            var order = revs.Keys.OrderBy(x => x).ToList();
            foreach (var rv in order)
            {
                var it = revs[rv];
                bool latest = rv == order[^1];
                it.Rev = (it.Project.Equals("SLV", StringComparison.OrdinalIgnoreCase) || it.Via == "Thinkproject" ? "Rev" : "R") + rv.ToString("00");
                (it.Tower, it.Zone, it.Level, it.Apt, it.LevelNum) = Place(it.Title);
                if (it.Rep != null) it.Status = "Closed";
                else if (it.Sub == null) it.Status = "?";
                else
                {
                    var lim = Brain.Num("overdue." + it.Type, it.Type is "WIR" or "MIR" ? 2 : it.Type == "RFI" ? 7 : 14, brain);
                    var due = it.Deadline ?? it.Submitted!.Value.AddDays(lim);
                    it.Status = DateTime.Now > due ? "Overdue" : it.Status == "Reviewing" ? "Reviewing" : "Open";
                    if (it.Deadline == null) it.Deadline = due;
                }
                if (!latest && it.Status is "Open" or "Overdue" or "Reviewing") it.Status = "Superseded";
                var why = new List<string>();
                if (it.Sub != null) why.Add($"submitted {it.Submitted:dd MMM HH:mm} by {it.Sub.FromName} ({it.Via})");
                if (it.Rep != null) why.Add($"closed: MZA reply {it.Reply:dd MMM HH:mm} by {it.By}" + (it.Code.Length > 0 ? " · " + it.Code : ""));
                else if (it.Status == "Superseded") why.Add("a later revision exists");
                else if (it.Sub != null) why.Add($"no MZA reply yet · due {it.Deadline:dd MMM}");
                it.Why = string.Join(" · ", why);
                var chk = new List<string>();
                if (it.Sub == null) chk.Add("its submission email is not in the index for this period (only a reply or forward was found)");
                if (it.Type is "WIR" or "MIR" && it.Tower.Length + it.Zone.Length + it.Level.Length == 0) chk.Add("no tower / zone / level in the title");
                it.Check = string.Join("; ", chk);
                // Your filters (the period is the submission date; items without submission are kept by their reply date).
                var when = it.Submitted ?? it.Reply;
                if (when == null || when < o.From || when > o.To) continue;
                if (o.Tower.Length > 0 && !it.Tower.Equals(o.Tower, StringComparison.OrdinalIgnoreCase) && !(o.Tower == "TH" && it.Tower.StartsWith("TH"))) continue;
                if (o.Zone.Length > 0 && !it.Zone.StartsWith(o.Zone, StringComparison.OrdinalIgnoreCase)) continue;
                if (o.Discipline.Length > 0 && !it.Disc.Equals(o.Discipline, StringComparison.OrdinalIgnoreCase)) continue;
                if ((o.LevelFrom > -99 || o.LevelTo < 999) && (it.LevelNum == null || it.LevelNum < o.LevelFrom || it.LevelNum > o.LevelTo)) continue;
                if (o.Status.Length > 0 && !(o.Status == "Code3/4" ? it.Code is "Code 3" or "Code 4" : it.Status.Equals(o.Status, StringComparison.OrdinalIgnoreCase))) continue;
                res.Items.Add(it);
            }
        }
        res.Items = res.Items.OrderByDescending(i => i.Submitted ?? i.Reply).ToList();
        res.Seconds = sw.Elapsed.TotalSeconds;
        return res;
    }

    static string TitleOf(string subj, Match m)
    {
        var after = subj[(m.Index + m.Length)..];
        var t = Regex.Replace(after, @"^[\s_\-–:]+", "");
        t = Regex.Replace(t, @"^\(updated\)[_\s]*", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"_with MZA Response\s*$", "", RegexOptions.IgnoreCase);
        return t.Trim(' ', '_', '-');
    }

    /// <summary>Thinkproject notification table: the Deadline and the Date of creation (dd/mm/yyyy).</summary>
    static (DateTime?, DateTime?) TpDates(string body)
    {
        body ??= "";
        var i = body.IndexOf("Deadline", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return (null, null);
        var seg = body.Substring(i, Math.Min(1500, body.Length - i));
        var ds = Regex.Matches(seg, @"\b(\d{2})/(\d{2})/(\d{4})\b").Select(x => new DateTime(int.Parse(x.Groups[3].Value), int.Parse(x.Groups[2].Value), int.Parse(x.Groups[1].Value))).ToList();
        // “-/-” in the deadline column means none: then only the creation date is there.
        var dash = Regex.IsMatch(seg, @"\t-/-\t");
        DateTime? dl = null, created = null;
        if (ds.Count >= 2) { dl = ds[0].AddHours(23).AddMinutes(59); created = ds[1]; }
        else if (ds.Count == 1) { if (dash) created = ds[0]; else dl = ds[0].AddHours(23).AddMinutes(59); }
        return (dl, created);
    }

    static string CodeOf(string body, string atts, string subj)
    {
        var m = Regex.Match(body ?? "", @"Approval result[\s\S]{0,300}?\bCode\s*([1-5])\b", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match((atts ?? "") + " " + (subj ?? ""), @"[-_ ]C([1-5])(?:[-_ .]|\.pdf|$)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match((subj ?? "") + " " + (atts ?? "") + " " + (body ?? "").Substring(0, Math.Min(800, (body ?? "").Length)), @"\bCode\s*[-:]?\s*([1-5])\b", RegexOptions.IgnoreCase);
        return m.Success ? "Code " + m.Groups[1].Value : "";
    }

    /// <summary>Tower · zone · level · apartments from a title: “T 3_L3 Apt”, “Z-1,L-4 to 6”, “TH 17,18,19 @ Z-4 & 5,L-GF”, “10th Floor”, “Zone 1 L02 podium”.</summary>
    public static (string Tower, string Zone, string Level, string Apt, int? LevelNum) Place(string title)
    {
        var s = " " + (title ?? "").Replace('_', ' ') + " ";
        string T = "", Z = "", L = "", A = ""; int? n = null;
        var x = Regex.Match(s, @"\b(?:Tower|T)\s?[-_]?\s?(\d)\b", RegexOptions.IgnoreCase);
        if (x.Success) T = "T" + x.Groups[1].Value;
        else { x = Regex.Match(s, @"\bTH\s?([\d ,&]*\d)"); if (x.Success) T = "TH " + Regex.Replace(x.Groups[1].Value, @"\s", ""); }
        x = Regex.Match(s, @"\b(?:Zone|Z)\s?[-_]?\s?0?(\d)(?:\s?&\s?(\d))?\b", RegexOptions.IgnoreCase);
        if (x.Success) Z = "Z" + x.Groups[1].Value + (x.Groups[2].Success ? "–" + x.Groups[2].Value : "");
        var y = Regex.Match(s, @"\b(?:Level|L)\s?[-_]?\s?(\d{1,2})\s*(?:to|-|–)\s*(\d{1,2})\b", RegexOptions.IgnoreCase);
        if (y.Success) { L = $"L{int.Parse(y.Groups[1].Value)}–{int.Parse(y.Groups[2].Value)}"; n = int.Parse(y.Groups[1].Value); }
        else
        {
            x = Regex.Match(s, @"\b(?:Level|L)\s?[-_]?\s?(\d{1,2}|GF|FF|UG|B\d)\b", RegexOptions.IgnoreCase);
            if (!x.Success) x = Regex.Match(s, @"\b(\d{1,2})(?:st|nd|rd|th)\s+Floor", RegexOptions.IgnoreCase);
            if (x.Success)
            {
                var v = x.Groups[1].Value.ToUpperInvariant();
                if (int.TryParse(v, out var k)) { L = "L" + k; n = k; }
                else { L = v; n = v == "GF" ? 0 : v == "FF" ? 1 : v == "UG" ? -1 : v.StartsWith("B") && int.TryParse(v[1..], out var bn) ? -bn : (int?)null; }
            }
            else
                foreach (var (w, v, k) in new[] { ("ground floor", "GF", 0), ("first floor", "L1", 1), ("attic", "Attic", 99), ("roof", "Roof", 100), ("podium", "Podium", 1), ("basement", "B", -1) })
                    if (s.Contains(w, StringComparison.OrdinalIgnoreCase)) { L = v; n = k; break; }
        }
        if (s.Contains("podium", StringComparison.OrdinalIgnoreCase) && L.Length > 0 && L != "Podium") L += " podium";
        x = Regex.Match(s, @"\b(?:apt|apartment)s?\.?\s*-?\s*(\d{3,4}(?:\s*(?:,|&|-|to)\s*\d{3,4})*)", RegexOptions.IgnoreCase);
        if (x.Success) A = Regex.Replace(x.Groups[1].Value, @"\s+", " ").Trim();
        return (T, Z, L, A, n);
    }

    // ================================================================ for the window, the connector and the brain test

    public static JsonObject Json(Item i, Activity.Hider hide = null) => new()
    {
        ["proj"] = i.Project, ["via"] = i.Via, ["type"] = i.Type, ["no"] = i.No, ["rev"] = i.Rev, ["disc"] = i.Disc, ["title"] = i.Title,
        ["tower"] = i.Tower, ["zone"] = i.Zone, ["level"] = i.Level, ["apt"] = i.Apt,
        ["sub"] = i.Submitted?.ToString("dd MMM yyyy HH:mm") ?? "", ["due"] = i.Deadline?.ToString("dd MMM yyyy") ?? "", ["reply"] = i.Reply?.ToString("dd MMM yyyy HH:mm") ?? "",
        ["by"] = hide != null && i.By.Length > 0 ? hide.Hide(i.By) : i.By, ["code"] = i.Code, ["days"] = i.Submitted != null ? i.Days : null, ["status"] = i.Status,
        ["why"] = hide != null ? hide.Hide(i.Why) : i.Why, ["check"] = i.Check,
        ["entry"] = (i.Rep ?? i.Sub)?.Entry ?? "", ["store"] = (i.Rep ?? i.Sub)?.StoreId ?? "", ["subEntry"] = i.Sub?.Entry ?? "", ["subStore"] = i.Sub?.StoreId ?? ""
    };

    public static JsonObject Kpis(Result r)
    {
        var closed = r.Items.Where(i => i.Submitted != null && i.Reply != null).ToList();
        return new JsonObject
        {
            ["found"] = r.Items.Count, ["closed"] = r.Items.Count(i => i.Status == "Closed"), ["open"] = r.Items.Count(i => i.Status is "Open" or "Reviewing"),
            ["overdue"] = r.Items.Count(i => i.Status == "Overdue"), ["check"] = r.Items.Count(i => i.Check.Length > 0),
            ["avg"] = closed.Count > 0 ? Math.Round(closed.Average(i => (i.Reply!.Value - i.Submitted!.Value).TotalDays), 1) : null,
            ["mails"] = r.Mails, ["secs"] = Math.Round(r.Seconds, 1)
        };
    }

    /// <summary>One document explained (for the brain chat: “why was WIR-15280 marked Closed?”).</summary>
    public static string Explain(string no)
    {
        var r = Run(new Options { From = DateTime.Today.AddDays(-400) });
        var hits = r.Items.Where(i => i.No.Contains(no, StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Count == 0 ? $"{no}: not found in the indexed emails of the last 400 days."
            : string.Join("\n", hits.Select(i => $"{i.No} {i.Rev} · {i.Status}{(i.Code.Length > 0 ? " · " + i.Code : "")} · {i.Why}{(i.Check.Length > 0 ? " · check: " + i.Check : "")}"));
    }

    // ================================================================ 📗 Excel: Log · Open & overdue · Summary · Check · About

    public static string Excel(Result r, string title)
    {
        var dir = Prompts.Folder;
        Directory.CreateDirectory(dir);
        var safe = Regex.Replace(title, @"[\\/:*?""<>|]+", " ").Trim();
        var path = Path.Combine(dir, $"{safe} — {DateTime.Now:dd MMM yyyy HHmm}.xlsx");
        string[] head = { "Project", "Via", "Type", "Document no.", "Rev", "Disc.", "Title", "Tower", "Zone", "Level", "Apt / TH", "Submitted", "Deadline", "MZA reply", "Replied by", "Code", "Days", "Status", "Why" };
        int[] widths = { 8, 12, 7, 26, 7, 7, 60, 9, 8, 11, 14, 17, 12, 17, 22, 9, 6, 11, 70 };
        string[] Row(Item i) => new[] { i.Project, i.Via, i.Type, i.No, i.Rev, i.Disc, i.Title, i.Tower, i.Zone, i.Level, i.Apt, i.Submitted?.ToString("dd MMM yyyy HH:mm") ?? "",
            i.Deadline?.ToString("dd MMM yyyy") ?? "", i.Reply?.ToString("dd MMM yyyy HH:mm") ?? "", i.By, i.Code, i.Submitted != null ? i.Days.ToString() : "", i.Status, i.Why };
        var o = r.Opt;
        var choices = $"Projects {(o.Projects.Count > 0 ? string.Join(" + ", o.Projects) : "all")} · {(o.Types.Count > 0 ? string.Join(", ", o.Types) : "all types")} · {o.From:dd MMM yyyy} – {o.To:dd MMM yyyy}"
                      + (o.Tower.Length > 0 ? " · " + o.Tower : "") + (o.Zone.Length > 0 ? " · " + o.Zone : "") + (o.Discipline.Length > 0 ? " · " + o.Discipline : "") + (o.Status.Length > 0 ? " · " + o.Status : "");
        var sheets = new List<XSheet>
        {
            new("Log", "📋 " + title, choices + $" · {r.Items.Count} item(s) from {r.Mails:N0} emails · Designed by A.HAMROUNI", head, widths, r.Items.Select(Row).ToList(), 17),
            new("Open & overdue", $"⏳ Waiting for MZA · oldest first", "Overdue = past the Thinkproject Deadline, else the days set in 🧠 Nova Brain (📋 Submittal logs)", head, widths,
                r.Items.Where(i => i.Status is "Open" or "Overdue" or "Reviewing").OrderBy(i => i.Submitted).Select(Row).ToList(), 17),
        };
        // Summary
        var sum = new List<string[]>();
        void Block(string name, Func<Item, string> key, string[] cols, Func<Item, string, bool> hit)
        {
            sum.Add(new[] { name }); sum.Add(new[] { "" }.Concat(cols).Append("Total").ToArray());
            foreach (var g in r.Items.Where(i => key(i) != null).GroupBy(key).OrderBy(g => g.Key))
                sum.Add(new[] { g.Key }.Concat(cols.Select(c => { var n = g.Count(i => hit(i, c)); return n > 0 ? n.ToString() : ""; })).Append(g.Count().ToString()).ToArray());
            sum.Add(new[] { "" });
        }
        Block("By project and type", i => $"{i.Project} · {i.Type}", new[] { "Closed", "Open", "Reviewing", "Overdue", "Superseded" }, (i, c) => i.Status == c);
        Block("By tower (WIR / MIR)", i => i.Type is "WIR" or "MIR" ? (i.Tower.Length > 0 ? i.Tower : "— not stated") : null, new[] { "Closed", "Open", "Overdue" }, (i, c) => i.Status == c);
        Block("MZA codes", i => i.Code.Length > 0 ? $"{i.Project} · {i.Type}" : null, new[] { "Code 1", "Code 2", "Code 3", "Code 4" }, (i, c) => i.Code == c);
        sheets.Add(new("Summary", "📊 Summary", choices, null, new[] { 26, 11, 11, 11, 11, 11, 9 }, sum, -1));
        var chk = r.Items.Where(i => i.Check.Length > 0).Select(i => Row(i).Append(i.Check).ToArray()).ToList();
        sheets.Add(new("Check", $"🔍 Check · {chk.Count} item(s) Nova could not fully place", "Nothing is dropped silently: each line says why", head.Append("Why it is here").ToArray(), widths.Append(60).ToArray(), chk, 17));
        var about = new List<string[]>
        {
            new[] { "Made by", "Nova Enhance · 📋 Submittal log · " + Program.Shown + " · Designed by A.HAMROUNI" },
            new[] { "Your choices", choices },
            new[] { "Read", $"{r.Mails:N0} emails with a document number in the subject ({r.Seconds:0.0} s)" },
            new[] { "Rules", "🧠 Nova Brain · 📋 Submittal logs (version " + Brain.Version + ")" }
        };
        foreach (var (s, rule) in Brain.Rules(Brain.Snapshot()).Where(x => x.Section["id"]?.ToString() is "submittal" or "location"))
            about.Add(new[] { rule["id"]?.ToString() ?? "", rule["text"]?.ToString() ?? "" });
        sheets.Add(new("About", "ℹ About this log", "", null, new[] { 22, 140 }, about, -1));
        WriteBook(path, sheets);
        return path;
    }

    record XSheet(string Name, string Title, string Sub, string[] Head, int[] Widths, List<string[]> Rows, int StatusCol);

    static string X(string s) => System.Security.SecurityElement.Escape(Regex.Replace(s ?? "", @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "")) ?? "";
    static string Col(int i) { var s = ""; i++; while (i > 0) { var m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; } return s; }

    static void WriteBook(string path, List<XSheet> sheets)
    {
        using var fs = File.Create(path);
        using var z = new ZipArchive(fs, ZipArchiveMode.Create);
        void W(string name, string xml) { using var s = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false)); s.Write(xml); }
        const string H = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
        var ct = new StringBuilder(H + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (int i = 1; i <= sheets.Count; i++) ct.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        W("[Content_Types].xml", ct.Append("</Types>").ToString());
        W("_rels/.rels", H + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        var rels = new StringBuilder(H + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        var wb = new StringBuilder(H + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        var names = new StringBuilder();
        for (int i = 0; i < sheets.Count; i++)
        {
            rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            wb.Append($"<sheet name=\"{X(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            if (sheets[i].Head != null)
                names.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">'{X(sheets[i].Name)}'!$A$4:${Col(sheets[i].Head.Length - 1)}${sheets[i].Rows.Count + 4}</definedName>");
        }
        rels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        W("xl/_rels/workbook.xml.rels", rels.ToString());
        W("xl/workbook.xml", wb.Append("</sheets>").Append(names.Length > 0 ? "<definedNames>" + names + "</definedNames>" : "").Append("</workbook>").ToString());
        // Styles: 0 normal · 1 header · 2 title · 3 note · 4 Closed · 5 Open · 6 Overdue · 7 Superseded · 8 bold · 9 Reviewing
        W("xl/styles.xml", H + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
          "<fonts count=\"9\"><font><sz val=\"10\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"14\"/><color rgb=\"FF2B2F3A\"/><name val=\"Calibri\"/></font><font><i/><sz val=\"9\"/><color rgb=\"FF6B7385\"/><name val=\"Calibri\"/></font>" +
          "<font><b/><sz val=\"10\"/><color rgb=\"FF137A44\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><color rgb=\"FF8A5A00\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><color rgb=\"FFB42318\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><color rgb=\"FF666666\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><name val=\"Calibri\"/></font></fonts>" +
          "<fills count=\"8\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF2B2F3A\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE3F6EC\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFF4DD\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFDE4E4\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEEEEEE\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE8E5FF\"/></patternFill></fill></fills>" +
          "<borders count=\"2\"><border/><border><bottom style=\"thin\"><color rgb=\"FFD5D8DE\"/></bottom></border></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
          "<cellXfs count=\"10\"><xf borderId=\"1\" applyBorder=\"1\"/><xf fontId=\"1\" fillId=\"2\" applyFont=\"1\" applyFill=\"1\"/><xf fontId=\"2\" applyFont=\"1\"/><xf fontId=\"3\" applyFont=\"1\"/>" +
          "<xf fontId=\"4\" fillId=\"3\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/><xf fontId=\"5\" fillId=\"4\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/><xf fontId=\"6\" fillId=\"5\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/><xf fontId=\"7\" fillId=\"6\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/><xf fontId=\"8\" applyFont=\"1\"/><xf fontId=\"5\" fillId=\"7\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/></cellXfs></styleSheet>");
        for (int si = 0; si < sheets.Count; si++)
        {
            var sh = sheets[si];
            var sb = new StringBuilder(H + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (sh.Head != null) sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane xSplit=\"4\" ySplit=\"4\" topLeftCell=\"E5\" activePane=\"bottomRight\" state=\"frozen\"/></sheetView></sheetViews>");
            sb.Append("<cols>");
            for (int i = 0; i < sh.Widths.Length; i++) sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{sh.Widths[i]}\" customWidth=\"1\"/>");
            sb.Append("</cols><sheetData>");
            void Row(int r, IEnumerable<string> cells, Func<int, string, int> style)
            {
                sb.Append($"<row r=\"{r}\">");
                int c = 0;
                foreach (var v in cells)
                {
                    var st = style(c, v);
                    var refc = Col(c++) + r;
                    if (st == 0 && v.Length is > 0 and < 9 && int.TryParse(v, out var n)) sb.Append($"<c r=\"{refc}\" s=\"0\"><v>{n}</v></c>");
                    else sb.Append($"<c r=\"{refc}\" t=\"inlineStr\" s=\"{st}\"><is><t xml:space=\"preserve\">{X(v)}</t></is></c>");
                }
                sb.Append("</row>");
            }
            Row(1, new[] { sh.Title }, (_, _) => 2);
            Row(2, new[] { sh.Sub }, (_, _) => 3);
            int rr = 4;
            if (sh.Head != null) Row(rr++, sh.Head, (_, _) => 1);
            foreach (var row in sh.Rows)
            {
                bool header = sh.Head == null && row.Length > 1 && row[0] == "";
                bool block = sh.Head == null && row.Length == 1 && row[0].Length > 0;
                Row(rr++, row, (c, v) => header ? 1 : block ? 8 : c == sh.StatusCol ? v switch { "Closed" => 4, "Open" => 5, "Overdue" => 6, "Superseded" => 7, "Reviewing" => 9, _ => 0 } : (sh.Head == null && c == 0 ? 8 : 0));
            }
            sb.Append("</sheetData>");
            if (sh.Head != null) sb.Append($"<autoFilter ref=\"A4:{Col(sh.Head.Length - 1)}{Math.Max(5, sh.Rows.Count + 4)}\"/>");
            sb.Append("</worksheet>");
            W($"xl/worksheets/sheet{si + 1}.xml", sb.ToString());
        }
    }

    // ================================================================ “WIR log Tower 4, open ones” typed in Search for email

    static readonly Regex AskRx = new(@"\b(WIR|MIR|MAR|PQS|RFI|SDS|submittal|inspection request)s?\b[\s\S]*\blog\b|\blog\b[\s\S]*\b(WIR|MIR|MAR|PQS|RFI|SDS|submittals?)\b", RegexOptions.IgnoreCase);
    public static bool Wanted(string q) => AskRx.IsMatch(q ?? "");

    /// <summary>Options read from your words: types, project, tower, zone, level, period, status.</summary>
    public static Options FromWords(string q, string project)
    {
        var o = new Options();
        foreach (Match m in Regex.Matches(q, @"\b(WIR|MIR|MAR|PQS|RFI|SDS)s?\b", RegexOptions.IgnoreCase)) if (!o.Types.Contains(m.Groups[1].Value.ToUpperInvariant())) o.Types.Add(m.Groups[1].Value.ToUpperInvariant());
        if (Regex.IsMatch(q, @"inspection request", RegexOptions.IgnoreCase) && o.Types.Count == 0) o.Types.AddRange(new[] { "WIR", "MIR" });
        if (!string.IsNullOrEmpty(project) && project != "?") o.Projects.Add(project);
        foreach (var p in new[] { "SLV", "RS5" }) if (Regex.IsMatch(q, $@"\b{p}\b", RegexOptions.IgnoreCase) && !o.Projects.Contains(p)) o.Projects.Add(p);
        var (t, z, _, _, n) = Place(q);
        o.Tower = t; o.Zone = z;
        var lv = Regex.Match(q, @"\b(?:Level|L)\s?(\d{1,2})\s*(?:to|-|–)\s*(\d{1,2})\b", RegexOptions.IgnoreCase);
        if (lv.Success) { o.LevelFrom = int.Parse(lv.Groups[1].Value); o.LevelTo = int.Parse(lv.Groups[2].Value); }
        else if (n != null) { o.LevelFrom = o.LevelTo = n.Value; }
        if (Regex.IsMatch(q, @"\boverdue\b", RegexOptions.IgnoreCase)) o.Status = "Overdue";
        else if (Regex.IsMatch(q, @"\bopen\b|not (yet )?(closed|replied)|pending", RegexOptions.IgnoreCase)) o.Status = "Open";
        else if (Regex.IsMatch(q, @"\bclosed\b", RegexOptions.IgnoreCase)) o.Status = "Closed";
        // Period: “1 Oct – 19 Nov”, “this month”, “last month”, “this week”, “last 30 days”.
        var mm = Regex.Matches(q, @"\b(\d{1,2})\s*(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s*(\d{4})?", RegexOptions.IgnoreCase);
        DateTime? D(Match m) { try { var y = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : DateTime.Today.Year; return DateTime.ParseExact($"{m.Groups[1].Value} {m.Groups[2].Value[..3]} {y}", "d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture); } catch { return null; } }
        if (mm.Count >= 2 && D(mm[0]) is { } a && D(mm[1]) is { } b) { o.From = a; o.To = b.AddDays(1).AddSeconds(-1); }
        else if (mm.Count == 1 && D(mm[0]) is { } a1) { o.From = a1; }
        else if (Regex.IsMatch(q, @"last month", RegexOptions.IgnoreCase)) { var f = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1); o.From = f; o.To = f.AddMonths(1).AddSeconds(-1); }
        else if (Regex.IsMatch(q, @"this month", RegexOptions.IgnoreCase)) o.From = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        else if (Regex.IsMatch(q, @"this week", RegexOptions.IgnoreCase)) o.From = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        else if (Regex.Match(q, @"last (\d{1,3}) days", RegexOptions.IgnoreCase) is { Success: true } ld) o.From = DateTime.Today.AddDays(-int.Parse(ld.Groups[1].Value));
        return o;
    }
}
