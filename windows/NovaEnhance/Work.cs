using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>One line in a task: an email, a file place, or a piece of text.</summary>
class WorkItem
{
    public string Kind = "mail";            // mail · file · text
    public string Entry = "", Store = "", Subject = "", From = "", Summary = "", Att = "";
    public DateTime Date;
    public string Path = "", Loc = "", Goto = "", Text = "";
    /// <summary>open · answered · handled · done (ticked by you)</summary>
    public string State = "open";
    public string Why = "";
    public bool IsNew;
    public DateTime Added = DateTime.Now;
}

class WorkLog { public DateTime T; public string Text = ""; }

/// <summary>A saved search or answer that becomes a live task note.</summary>
class WorkTask
{
    public string Id = "", Title = "", Question = "", Source = "ask", Notes = "", Status = "todo", OutlookTask = "";
    public JsonObject Filter;
    public DateTime Created = DateTime.Now, Refreshed = DateTime.Now, NotesEdited, Done;
    public DateTime? Due, Remind;
    public bool Reminded;
    public List<string> Tags = new();
    public List<WorkItem> Items = new();
    public List<WorkLog> Log = new();

    public bool Mail => Source is "ask" or "brief";
    public int OpenCount => Items.Count(i => i.State == "open");
    public int DoneCount => Items.Count(i => i.State != "open");
    public bool Overdue => Status != "done" && Due != null && Due.Value.Date < DateTime.Today;
    public bool DueToday => Status != "done" && Due != null && Due.Value.Date == DateTime.Today;
    public void Add(string text) => Log.Add(new WorkLog { T = DateTime.Now, Text = text });
}

/// <summary>
/// 📌 My Work: saved searches and answers as live task notes in Documents\Nova Enhance\My Work — refresh with
/// auto-ticks for answered emails, notes, due dates, reminders, a log with date and time, an Outlook task for each
/// (it shows in Outlook To-Do and on the phone), and a weekly report. Designed by A.HAMROUNI
/// </summary>
static class Work
{
    public static string Dir { get { var p = System.IO.Path.Combine(Paths.Root, "My Work"); Directory.CreateDirectory(p); return p; } }
    static string Reports { get { var p = System.IO.Path.Combine(Dir, "Reports"); Directory.CreateDirectory(p); return p; } }
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, IncludeFields = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly object L = new();

    public static bool OutlookSync { get => Store.Bool("win_work_outlook", true); set => Store.SetBool("win_work_outlook", value); }
    public static bool WeeklyAuto { get => Store.Bool("win_work_weekly", true); set => Store.SetBool("win_work_weekly", value); }

    public static List<WorkTask> All()
    {
        var res = new List<WorkTask>();
        lock (L)
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
                try { var t = JsonSerializer.Deserialize<WorkTask>(File.ReadAllText(f), Opt); if (t != null && t.Id.Length > 0) res.Add(t); } catch { }
        return res;
    }

    public static WorkTask Get(string id) => All().FirstOrDefault(t => t.Id == id);

    public static void Save(WorkTask t)
    {
        lock (L) File.WriteAllText(System.IO.Path.Combine(Dir, t.Id + ".json"), JsonSerializer.Serialize(t, Opt), new UTF8Encoding(false));
    }

    public static void Delete(WorkTask t)
    {
        lock (L) try { File.Delete(System.IO.Path.Combine(Dir, t.Id + ".json")); } catch { }
        if (t.OutlookTask.Length > 0) try { WithTask(t, it => it.Delete()); } catch { }
    }

    // ================================================================ creating

    static string NewId() => DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Random.Shared.Next(100, 999);

    static WorkItem FromHit(MailHit h) => new()
    {
        Kind = "mail", Entry = h.Entry, Store = h.StoreId, Subject = h.Subject, From = h.Sent ? "Me → " + h.To : h.FromName, Date = h.Date,
        Summary = h.Summary, Att = h.AttNames, State = Outstanding.IsHandled(h) ? "handled" : "open"
    };

    /// <summary>Projects from the subjects and the Outlook folders the emails are filed in (SLV, Nouran-Saadiyat, RS5-C7, CSU1095…).</summary>
    static List<string> Tags(IEnumerable<MailHit> hits)
    {
        var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(string k) { if (k.Length >= 3) count[k] = count.TryGetValue(k, out var n) ? n + 1 : 1; }
        foreach (var h in hits)
        {
            var f = h.Folder ?? "";
            if (f.Length > 0 && !Regex.IsMatch(f, @"^(inbox|sent|sent items|archive|deleted|drafts|to do|junk)", RegexOptions.IgnoreCase)) Add(f);
            var subj = Outstanding.NormSubject(h.Subject).ToUpperInvariant();
            var m = Regex.Match(subj, @"^([A-Z]{2,6})\b");
            if (m.Success && m.Value is not ("RE" or "FW" or "THE" or "NEW" or "RFI" or "MAR" or "WIR")) Add(m.Value);
            foreach (Match c in Regex.Matches(h.Subject ?? "", @"\b(CSU\d{3,5}|RS\d-C\d+)\b", RegexOptions.IgnoreCase)) Add(c.Value.ToUpperInvariant());
        }
        return count.Where(x => x.Value >= 1).OrderByDescending(x => x.Value).Take(3).Select(x => x.Key).ToList();
    }

    public static WorkTask FromMail(string question, JsonObject filter, string intent, List<MailHit> hits, string source = "ask")
    {
        var t = new WorkTask
        {
            Id = NewId(), Question = question, Filter = filter?.DeepClone() as JsonObject, Source = source,
            Title = intent == "outstanding" || source == "brief" ? $"Waiting for my reply — {DateTime.Now:dd MMM}" : $"{Cut(question, 50)} — {DateTime.Now:dd MMM}",
            Tags = Tags(hits)
        };
        t.Items = hits.Where(h => h.MaybeAnswered.Length == 0).Select(FromHit).ToList();
        foreach (var h in hits.Where(h => h.MaybeAnswered.Length > 0)) { var w = FromHit(h); w.State = "answered"; w.Why = "probably answered — " + h.MaybeAnswered; t.Items.Add(w); }
        t.Add($"Task saved from {(source == "brief" ? "the morning brief" : "Ask Nova")} ({t.Items.Count} email{(t.Items.Count == 1 ? "" : "s")})");
        Save(t);
        SyncOutlook(t);
        return t;
    }

    public static WorkTask FromText(string title, string question, string text, string source, IEnumerable<WorkItem> links = null)
    {
        var t = new WorkTask { Id = NewId(), Title = $"{Cut(title, 50)} — {DateTime.Now:dd MMM}", Question = question, Source = source };
        if (!string.IsNullOrWhiteSpace(text)) t.Items.Add(new WorkItem { Kind = "text", Text = text });
        if (links != null) t.Items.AddRange(links);
        t.Add($"Task saved from {source switch { "chat" => "Nova Chat", "pdf" => "the PDF panel", "folder" => "folder search", _ => "Nova" }}");
        Save(t);
        SyncOutlook(t);
        return t;
    }

    public static WorkTask FromFiles(string question, string folder, List<FileHit> hits)
    {
        var links = hits.Take(300).Select(h => new WorkItem
        {
            Kind = "file", Path = h.Path, Subject = h.Name, Loc = h.Pieces.FirstOrDefault()?.Loc ?? "", Goto = h.Pieces.FirstOrDefault()?.Goto ?? "",
            Summary = h.Pieces.FirstOrDefault()?.Text ?? "", Date = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, h.Mtime)).LocalDateTime
        });
        var t = FromText($"“{question}” in {System.IO.Path.GetFileName(folder.TrimEnd('\\'))}", question, "", "folder", links);
        t.Tags = new() { System.IO.Path.GetFileName(folder.TrimEnd('\\')) };
        Save(t);
        return t;
    }

    public static WorkTask NewEmpty()
    {
        var t = new WorkTask { Id = NewId(), Title = $"New task — {DateTime.Now:dd MMM}", Source = "manual" };
        t.Add("Task created");
        Save(t);
        SyncOutlook(t);
        return t;
    }

    static string Cut(string s, int n) { s = Regex.Replace(s ?? "", @"\s+", " ").Trim(); return s.Length > n ? s[..n].TrimEnd() + "…" : s; }

    /// <summary>Logs an action on an email into every open task that lists it (e.g. ✓ handled, ✦ draft, 💾 saved).</summary>
    public static void LogMail(MailHit h, string text)
    {
        try
        {
            foreach (var t in All().Where(t => t.Status != "done" && t.Items.Any(i => i.Entry == h.Entry)))
            {
                t.Add($"{text} — {Cut(h.Subject, 60)}");
                if (text.StartsWith("✓")) foreach (var i in t.Items.Where(i => i.Entry == h.Entry)) { i.State = "handled"; i.Why = $"marked handled by you {DateTime.Now:ddd HH:mm}"; }
                Save(t);
            }
        }
        catch { }
    }

    // ================================================================ refresh

    /// <summary>Runs the saved question again: answered emails tick themselves, new ones are added (➕).</summary>
    public static async Task<string> Refresh(WorkTask t, CancellationToken ct)
    {
        if (!t.Mail) return "This task was saved from " + (t.Source == "folder" ? "folder search" : t.Source == "pdf" ? "a PDF" : "Nova Chat") + " — there is nothing to refresh.";
        List<MailHit> hits;
        if (t.Source == "brief") hits = (await BriefLogic.Build(ct)).Needs;
        else hits = (await Assistant.Ask(t.Question, null, new List<MailHit>(), ct)).Hits;
        var waiting = hits.Where(h => h.MaybeAnswered.Length == 0).ToList();
        var byEntry = waiting.GroupBy(h => h.Entry ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        int answered = 0, added = 0;
        foreach (var i in t.Items) i.IsNew = false;
        foreach (var i in t.Items.Where(i => i.Kind == "mail" && i.State == "open"))
        {
            var h = new MailHit { Entry = i.Entry };
            if (Outstanding.IsHandled(h)) { i.State = "handled"; i.Why = "marked handled by you"; answered++; continue; }
            if (byEntry.ContainsKey(i.Entry)) continue;
            // Gone from the waiting list: answered by you (or no longer waiting).
            var maybe = hits.FirstOrDefault(x => x.Entry == i.Entry && x.MaybeAnswered.Length > 0);
            i.State = "answered";
            i.Why = maybe != null ? "probably answered — " + maybe.MaybeAnswered : $"answered by you (found at refresh {DateTime.Now:ddd dd MMM HH:mm})";
            answered++;
        }
        // Earlier emails of the same topic are folded into the newer one: an old line is answered when its topic's newest is.
        foreach (var h in waiting.Where(h => !t.Items.Any(i => i.Entry.Equals(h.Entry, StringComparison.OrdinalIgnoreCase))))
        {
            var w = FromHit(h); w.IsNew = true; w.Added = DateTime.Now;
            t.Items.Add(w); added++;
        }
        var still = t.Items.Count(i => i.Kind == "mail" && i.State == "open");
        t.Refreshed = DateTime.Now;
        t.Add($"↻ Refreshed: {answered} answered, {added} new, {still} still waiting");
        if (still == 0 && t.Status != "done") { t.Status = "done"; t.Done = DateTime.Now; t.Add("✓ Done — nothing is waiting any more"); }
        Save(t);
        SyncOutlook(t);
        return $"↻ Since {t.Log.Count(l => l.Text.StartsWith("↻")) switch { > 1 => "the last refresh", _ => t.Created.ToString("ddd HH:mm") }}: ✓ {answered} answered · ➕ {added} new · ⏳ {still} still waiting";
    }

    // ================================================================ Outlook task (shows in Outlook To-Do and Microsoft To Do on the phone)

    static void WithTask(WorkTask t, Action<dynamic> act)
    {
        dynamic app = Outlook.Running();
        if (app == null) return;
        dynamic ns = app.GetNamespace("MAPI");
        dynamic it = null;
        try
        {
            if (t.OutlookTask.Length > 0) try { it = ns.GetItemFromID(t.OutlookTask); } catch { it = null; }
            if (it == null)
            {
                it = app.CreateItem(3);   // olTaskItem
                it.Subject = "📌 " + t.Title;
                it.Save();
                t.OutlookTask = Outlook.S(() => it.EntryID);
            }
            act(it);
        }
        finally { Outlook.Release(it); Outlook.Release(ns); }
    }

    public static void SyncOutlook(WorkTask t)
    {
        if (!OutlookSync) return;
        try
        {
            WithTask(t, it =>
            {
                it.Subject = "📌 " + t.Title;
                var body = new StringBuilder();
                if (t.Question.Length > 0) body.AppendLine("Question: " + t.Question);
                body.AppendLine($"Nova task · created {t.Created:ddd dd MMM yyyy HH:mm} · {t.DoneCount} of {t.Items.Count} done");
                foreach (var i in t.Items.Where(i => i.Kind != "text").Take(40)) body.AppendLine((i.State == "open" ? "☐ " : "☑ ") + (i.Kind == "mail" ? $"{i.Subject} — {i.From} · {i.Date:dd MMM}" : $"{i.Subject} {i.Loc}"));
                if (t.Notes.Length > 0) body.AppendLine().AppendLine("Notes: " + t.Notes);
                body.AppendLine().AppendLine("Open it in Nova Enhance → 📌 My Work. " + Program.Sign);
                it.Body = body.ToString();
                if (t.Due != null) it.DueDate = t.Due.Value.Date;
                if (t.Remind != null && t.Remind > DateTime.Now) { it.ReminderSet = true; it.ReminderTime = t.Remind.Value; }
                it.Status = t.Status == "done" ? 2 : t.Status == "waiting" ? 3 : 0;   // complete · waiting · not started
                if (t.Status == "done") it.MarkComplete();
                it.Save();
            });
            Save(t);
        }
        catch (Exception e) { Log.Warn("my work", "Outlook task not updated: " + e.Message); }
    }

    /// <summary>Done on the phone or in Outlook? Then done in Nova too.</summary>
    public static void PullOutlook(List<WorkTask> tasks)
    {
        if (!OutlookSync) return;
        try
        {
            dynamic app = Outlook.Running();
            if (app == null) return;
            dynamic ns = app.GetNamespace("MAPI");
            foreach (var t in tasks.Where(t => t.Status != "done" && t.OutlookTask.Length > 0))
            {
                dynamic it = null;
                try
                {
                    it = ns.GetItemFromID(t.OutlookTask);
                    if ((bool)it.Complete) { t.Status = "done"; t.Done = DateTime.Now; t.Add("✓ Done in Outlook / To Do"); Save(t); }
                }
                catch { }
                finally { Outlook.Release(it); }
            }
            Outlook.Release(ns);
        }
        catch { }
    }

    // ================================================================ reminders & weekly report

    static bool busy;

    /// <summary>Every minute: reminders that are due pop up; on Thursday from 17:00 the weekly report is written once.</summary>
    public static void Tick()
    {
        if (busy) return;
        busy = true;
        try
        {
            foreach (var t in All().Where(t => t.Remind != null && !t.Reminded && t.Remind <= DateTime.Now && t.Status != "done"))
            {
                t.Reminded = true;
                t.Add("⏰ Reminder shown");
                Save(t);
                new WorkReminder(t).Show();
            }
            var key = "win_work_weekly_" + DateTime.Today.ToString("yyyyMMdd");
            if (WeeklyAuto && DateTime.Now.DayOfWeek == DayOfWeek.Thursday && DateTime.Now.Hour >= 17 && !Store.Bool(key, false) && All().Count > 0)
            {
                Store.SetBool(key, true);
                var f = WeeklyReport();
                Program.Balloon("📊 Weekly work report ready", System.IO.Path.GetFileName(f) + " — in Documents\\Nova Enhance\\My Work\\Reports");
            }
        }
        catch (Exception e) { Log.Error("my work tick", e); }
        finally { busy = false; }
    }

    static string H(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    /// <summary>Monday to today, per project: done, still open, waiting for others. Opens in the browser (print or send).</summary>
    public static string WeeklyReport()
    {
        var today = DateTime.Today;
        var start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var all = All();
        var touched = all.Where(t => t.Created >= start || t.Log.Any(l => l.T >= start) || t.Status != "done").ToList();
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset='utf-8'><title>Weekly work report</title><style>body{font-family:Segoe UI,Arial;margin:36px;color:#222;max-width:900px}h1{color:#5B4BDB;font-size:22px;margin:0}h2{font-size:16px;margin:22px 0 6px;border-bottom:1px solid #ddd;padding-bottom:4px}li{margin:3px 0}.m{color:#777;font-size:13px}.s{color:#7C6CFF;font-weight:bold;margin-top:30px}</style></head><body>");
        sb.Append($"<h1>Weekly work report — {start:dd MMM} to {today:dd MMM yyyy}</h1><div class='m'>{H(Me.Name)} · generated {DateTime.Now:ddd dd MMM HH:mm} from Nova My Work</div>");
        foreach (var g in touched.GroupBy(t => t.Tags.FirstOrDefault() ?? "Other").OrderBy(g => g.Key == "Other" ? 1 : 0).ThenBy(g => g.Key))
        {
            var done = g.Where(t => t.Status == "done" && t.Done >= start).ToList();
            var open = g.Where(t => t.Status == "todo").ToList();
            var wait = g.Where(t => t.Status == "waiting").ToList();
            sb.Append($"<h2>{H(g.Key)} — ✓ {done.Count} done · ⏳ {open.Count} open · ⏸ {wait.Count} waiting</h2><ul>");
            foreach (var t in done) sb.Append($"<li>✓ {H(t.Title)} <span class='m'>(done {t.Done:ddd dd MMM})</span></li>");
            foreach (var t in open) sb.Append($"<li>⏳ {H(t.Title)} <span class='m'>— {t.OpenCount} open item(s){(t.Due != null ? $", due {t.Due:ddd dd MMM}" : "")}{(t.Overdue ? " · OVERDUE" : "")}</span></li>");
            foreach (var t in wait) sb.Append($"<li>⏸ {H(t.Title)} <span class='m'>— waiting since {t.Log.LastOrDefault(l => l.Text.Contains("Waiting"))?.T.ToString("ddd dd MMM") ?? t.Created.ToString("ddd dd MMM")}</span></li>");
            sb.Append("</ul>");
            var acts = g.SelectMany(t => t.Log.Where(l => l.T >= start && (l.Text.StartsWith("✓") || l.Text.StartsWith("✦") || l.Text.StartsWith("💾")))).OrderBy(l => l.T).Take(30).ToList();
            if (acts.Count > 0) { sb.Append("<div class='m'>Actions:</div><ul class='m'>"); foreach (var l in acts) sb.Append($"<li>{l.T:ddd HH:mm} · {H(l.Text)}</li>"); sb.Append("</ul>"); }
        }
        sb.Append($"<div class='s'>{H(Program.Sign)}</div></body></html>");
        var file = System.IO.Path.Combine(Reports, $"Weekly work report {today:yyyy-MM-dd}.html");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        return file;
    }

    public static string Export(WorkTask t)
    {
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html><head><meta charset='utf-8'><title>{H(t.Title)}</title><style>body{{font-family:Segoe UI,Arial;margin:36px;color:#222;max-width:900px}}h1{{color:#5B4BDB;font-size:21px}}.m{{color:#777;font-size:13px}}li{{margin:4px 0}}.d{{color:#999;text-decoration:line-through}}</style></head><body>");
        sb.Append($"<h1>📌 {H(t.Title)}</h1><div class='m'>Created {t.Created:ddd dd MMM yyyy HH:mm} · status {t.Status}{(t.Due != null ? $" · due {t.Due:ddd dd MMM}" : "")} · {t.DoneCount} of {t.Items.Count} done</div>");
        if (t.Question.Length > 0) sb.Append($"<p>🔍 {H(t.Question)}</p>");
        sb.Append("<ul>");
        foreach (var i in t.Items)
            sb.Append(i.Kind == "text" ? $"<li style='white-space:pre-wrap'>{H(i.Text)}</li>" :
                $"<li class='{(i.State == "open" ? "" : "d")}'>{(i.State == "open" ? "☐" : "☑")} {H(i.Subject)} <span class='m'>— {H(i.Kind == "mail" ? i.From + " · " + i.Date.ToString("dd MMM") : i.Path + " " + i.Loc)}{(i.Why.Length > 0 ? " · " + H(i.Why) : "")}</span></li>");
        sb.Append("</ul>");
        if (t.Notes.Length > 0) sb.Append($"<h3>Notes</h3><p style='white-space:pre-wrap'>{H(t.Notes)}</p>");
        sb.Append("<h3>Log</h3><ul class='m'>");
        foreach (var l in t.Log) sb.Append($"<li>{l.T:ddd dd MMM yyyy HH:mm} · {H(l.Text)}</li>");
        sb.Append($"</ul><div class='m' style='color:#7C6CFF'>{H(Program.Sign)}</div></body></html>");
        var file = System.IO.Path.Combine(Reports, Regex.Replace(t.Title, @"[\\/:*?""<>|]", " ") + ".html");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        return file;
    }
}

/// <summary>⏰ A task reminder in the corner of the screen.</summary>
class WorkReminder : NovaForm
{
    protected override bool ShowWithoutActivation => true;

    public WorkReminder(WorkTask t)
    {
        Text = "Nova Enhance · reminder";
        FormBorderStyle = FormBorderStyle.FixedToolWindow; TopMost = true; ShowInTaskbar = false;
        Width = 440; Height = 280; StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        var s = new Stack(); Controls.Add(s);
        s.Add(Th.L($"⏰ {t.Remind:ddd dd MMM HH:mm}", 9.5f, Th.AccentText, true));
        s.Add(Th.Wrap(t.Title, 12, Th.Text)).Font = Th.F(12, true);
        s.Add(Th.Wrap((t.Due != null ? $"due {t.Due:ddd dd MMM} · " : "") + $"{t.OpenCount} item(s) still open", 9, Th.Muted));
        if (t.Notes.Length > 0) s.Add(Th.Wrap("📝 " + (t.Notes.Length > 160 ? t.Notes[..160] + "…" : t.Notes), 9.5f, Th.Text));
        var row = s.Add(Th.Row(false));
        row.Controls.Add(Th.B("Open task", () => { Screens.Work(t.Id); Close(); }, true));
        var first = t.Items.FirstOrDefault(i => i.Kind == "mail" && i.State == "open");
        if (first != null) row.Controls.Add(Th.B("✉ Open email", () => { try { Outlook.Open(first.Entry, first.Store); } catch { } }));
        row.Controls.Add(Th.B("Snooze 1 h", () =>
        {
            var x = Work.Get(t.Id);
            if (x != null) { x.Remind = DateTime.Now.AddHours(1); x.Reminded = false; x.Add("⏰ Snoozed 1 h"); Work.Save(x); Work.SyncOutlook(x); }
            Close();
        }));
        s.Add(Th.Signature());
    }
}
