using System.Globalization;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>One calendar entry, read from Outlook on this PC (nothing goes to the AI).</summary>
class Meeting
{
    public string Entry = "", Subject = "", Location = "", Organizer = "", Attendees = "";
    public DateTime Start, End;
    /// <summary>0 appointment (no attendees) · 1 meeting I organise · 3 invitation I received · 5/7 cancelled</summary>
    public int Status;
    /// <summary>0 none · 1 organiser · 2 tentative · 3 accepted · 4 declined · 5 not answered</summary>
    public int Response;
    public bool AllDay, Reminder;
    public int Busy, BodyLen, AttendeeCount;

    public bool IsMeeting => Status is 1 or 3;
    public bool Cancelled => Status is 5 or 7;
    public bool Declined => Response == 4;
    public bool NeedsAnswer => Status == 3 && Response is 0 or 5;
    public string Time => AllDay ? "All day" : $"{Start:HH:mm}–{End:HH:mm}";

    // Filled by Calendar.Prepare
    public List<MailHit> Related = new();
    public List<string> Advice = new();
    public string LastFile = "";
}

/// <summary>
/// The calendar part of Nova (Settings → Email & AI → Calendar): today and tomorrow in the morning brief, invitations waiting
/// for an answer, your own reminders as a checklist, what is still open for each meeting, advice notes, and a pop-up before
/// each meeting. Read from Outlook on this PC only. Designed by A.HAMROUNI
/// </summary>
static class Calendar
{
    public static bool InBrief { get => Store.Bool("win_cal_brief", true); set => Store.SetBool("win_cal_brief", value); }
    public static bool Popup { get => Store.Bool("win_meet_popup", true); set => Store.SetBool("win_meet_popup", value); }
    public static int Lead { get => Math.Clamp(Store.Int("win_meet_lead", 30), 5, 60); set => Store.SetInt("win_meet_lead", Math.Clamp(value, 5, 60)); }

    /// <summary>Calendar entries overlapping [from, to), from the default calendar, recurring ones expanded.</summary>
    public static List<Meeting> Read(DateTime from, DateTime to)
    {
        var res = new List<Meeting>();
        object app = Outlook.Running();
        if (app == null) return res;
        dynamic ns = null, cal = null, items = null, found = null;
        try
        {
            ns = ((dynamic)app).GetNamespace("MAPI");
            cal = ns.GetDefaultFolder(9);
            items = cal.Items;
            items.IncludeRecurrences = true;
            items.Sort("[Start]");
            // Outlook reads these dates in the PC's own date format.
            var f = $"[Start] < '{to.ToString("g", CultureInfo.CurrentCulture)}' AND [End] > '{from.ToString("g", CultureInfo.CurrentCulture)}'";
            found = items.Restrict(f);
            dynamic it = found.GetFirst();
            int n = 0;
            while (it != null && n++ < 400)
            {
                try
                {
                    var m = new Meeting
                    {
                        Entry = Outlook.S(() => it.EntryID),
                        Subject = Outlook.S(() => it.Subject),
                        Location = Outlook.S(() => it.Location),
                        Organizer = Outlook.S(() => it.Organizer),
                        Start = (DateTime)it.Start,
                        End = (DateTime)it.End
                    };
                    try { m.Status = (int)it.MeetingStatus; } catch { }
                    try { m.Response = (int)it.ResponseStatus; } catch { }
                    try { m.AllDay = (bool)it.AllDayEvent; } catch { }
                    try { m.Reminder = (bool)it.ReminderSet; } catch { }
                    try { m.Busy = (int)it.BusyStatus; } catch { }
                    var req = Outlook.S(() => it.RequiredAttendees);
                    var opt = Outlook.S(() => it.OptionalAttendees);
                    m.Attendees = string.Join("; ", new[] { req, opt }.Where(x => x.Length > 0));
                    m.AttendeeCount = m.Attendees.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
                    try { m.BodyLen = Regex.Replace(Outlook.S(() => it.Body), @"\s+|_{3,}|Microsoft Teams.*", " ", RegexOptions.Singleline).Trim().Length; } catch { }
                    if (m.Start < to && m.End > from) res.Add(m);
                }
                catch { }
                Outlook.Release(it);
                it = found.GetNext();
            }
        }
        catch (Exception e) when (!Outlook.Gone(e)) { Log.Warn("calendar", "Calendar not read: " + e.Message); }
        catch { }
        finally { Outlook.Release(found); Outlook.Release(items); Outlook.Release(cal); Outlook.Release(ns); }
        return res;
    }

    static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase)
    {
        "meeting", "weekly", "daily", "call", "review", "discussion", "update", "the", "and", "for", "with", "re", "fw", "teams", "online", "workshop", "session"
    };

    static List<string> Words(string subject) =>
        Regex.Split(Outstanding.NormSubject(subject), @"[^\p{L}\p{N}\-]+").Where(w => w.Length >= 3 && !Small.Contains(w)).Distinct().ToList();

    static string First(string name) { var n = (name ?? "").Trim(); if (n.Contains(',')) n = n[(n.IndexOf(',') + 1)..].Trim(); var i = n.IndexOf(' '); return i > 0 ? n[..i] : n; }

    /// <summary>For each meeting: related emails still waiting for you, the last file sent around, and advice notes.</summary>
    public static void Prepare(List<Meeting> meetings, List<MailHit> waiting)
    {
        waiting ??= new();
        foreach (var m in meetings.Where(x => x.IsMeeting && !x.Cancelled))
        {
            var words = Words(m.Subject);
            var refs = Outstanding.Refs(m.Subject);
            int Score(MailHit h)
            {
                var s = Outstanding.NormSubject(h.Subject);
                int sc = words.Count(w => s.Contains(w, StringComparison.OrdinalIgnoreCase)) * 2 + refs.Count(r => (h.Subject ?? "").Contains(r, StringComparison.OrdinalIgnoreCase)) * 4;
                if (sc > 0 && h.FromName.Length > 0 && m.Attendees.Contains(h.FromName, StringComparison.OrdinalIgnoreCase)) sc += 1;
                return sc;
            }
            // Open emails (waiting for your reply) about this meeting's subject.
            m.Related = waiting.Where(h => h.MaybeAnswered.Length == 0).Select(h => (h, s: Score(h))).Where(x => x.s >= 3 || (words.Count <= 2 && x.s >= 2))
                .OrderByDescending(x => x.s).ThenByDescending(x => x.h.Date).Take(3).Select(x => x.h).ToList();
            // The latest file sent around about it (last 30 days, from the index).
            try
            {
                if (words.Count > 0)
                {
                    var q = new MailQuery { Words = words.OrderByDescending(w => w.Length).Take(2).ToList(), After = DateTime.Now.AddDays(-30), HasAttach = true, Limit = 10 };
                    var withFiles = MailIndex.Search(q).OrderByDescending(h => h.Date).FirstOrDefault();
                    if (withFiles != null) m.LastFile = withFiles.AttNames.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() + $" ({First(withFiles.FromName)}, {withFiles.Date:dd MMM})";
                }
            }
            catch { }

            if (m.NeedsAnswer) m.Advice.Add("Invitation not answered yet — Accept or Decline in Outlook");
            foreach (var h in m.Related)
                if (Regex.IsMatch(h.NewPart, @"postpon|reschedul|re-schedul|move (the|this|our) meeting|new (time|date)|another (time|date)|cancel", RegexOptions.IgnoreCase))
                { m.Advice.Add($"{First(h.FromName)} asked to change this meeting ({h.Date:dd MMM}) — you haven't replied"); break; }
            if (m.Status == 3 && m.AttendeeCount >= 3 && m.BodyLen < 40) m.Advice.Add("No agenda in the invitation — ask the organiser for one?");
            if (m.Related.Count >= 2) m.Advice.Add($"{m.Related.Count} related emails still waiting for your reply");
        }
        // Clashes: two busy meetings at the same time.
        var busy = meetings.Where(x => !x.AllDay && !x.Cancelled && !x.Declined && x.Busy != 0 && x.IsMeeting).OrderBy(x => x.Start).ToList();
        for (int i = 0; i < busy.Count; i++)
            for (int j = i + 1; j < busy.Count && busy[j].Start < busy[i].End; j++)
            {
                busy[i].Advice.Add($"⚠ Clashes with “{busy[j].Subject}” at {busy[j].Start:HH:mm}");
                busy[j].Advice.Add($"⚠ Clashes with “{busy[i].Subject}” at {busy[i].Start:HH:mm}");
            }
    }

    /// <summary>"You have 7.5 h of meetings on Tuesday" for heavy days.</summary>
    public static string DayLoad(List<Meeting> meetings, DateTime day)
    {
        var h = meetings.Where(x => x.Start.Date == day.Date && x.IsMeeting && !x.AllDay && !x.Cancelled && !x.Declined).Sum(x => (x.End - x.Start).TotalHours);
        return h >= 6 ? $"You have {h:0.#} h of meetings on {day:dddd} — block time for emails?" : "";
    }

    /// <summary>Your own reminders today (appointments without attendees, like "Reminder to Check…").</summary>
    public static List<Meeting> Reminders(List<Meeting> meetings, DateTime day) =>
        meetings.Where(x => x.Start.Date == day.Date && !x.IsMeeting && !x.Cancelled && (x.Reminder || x.AttendeeCount == 0)).OrderBy(x => x.Start).ToList();

    public static void Open(Meeting m)
    {
        try
        {
            dynamic app = Outlook.Get();
            dynamic ns = app.GetNamespace("MAPI");
            dynamic it = ns.GetItemFromID(m.Entry);
            it.Display(false);
        }
        catch (Exception e) { MessageBox.Show("Could not open it in Outlook: " + e.Message, "Nova Enhance"); }
    }

    // ================================================================ pop-up before meetings

    static List<Meeting> soon = new();
    static DateTime loaded = DateTime.MinValue;
    static readonly HashSet<string> shown = new();
    static bool busyTick;

    /// <summary>Called every minute: shows a pop-up <see cref="Lead"/> minutes before each meeting.</summary>
    public static async void Tick()
    {
        if (!Popup || busyTick) return;
        busyTick = true;
        try
        {
            if (DateTime.Now - loaded > TimeSpan.FromMinutes(10))
            {
                var list = await Task.Run(() => Read(DateTime.Now, DateTime.Now.AddMinutes(Lead + 90)));
                soon = list.Where(x => x.IsMeeting && !x.AllDay && !x.Cancelled && !x.Declined).ToList();
                loaded = DateTime.Now;
            }
            foreach (var m in soon)
            {
                var mins = (m.Start - DateTime.Now).TotalMinutes;
                var key = m.Entry + "|" + m.Start.ToString("o");
                if (mins <= Lead && mins > -2 && !shown.Contains(key))
                {
                    shown.Add(key);
                    var waiting = await Task.Run(() =>
                    {
                        try
                        {
                            var q = new MailQuery { Folder = "received", Unanswered = true, After = DateTime.Now.AddDays(-30), Limit = 300, MeIn = "any" };
                            return Outstanding.Check(MailIndex.Search(q).Where(h => !BriefLogic.IsAuto(h) && !Me.IsMe(h.FromAddr)).ToList());
                        }
                        catch { return new List<MailHit>(); }
                    });
                    var day = await Task.Run(() => Read(m.Start.Date, m.Start.Date.AddDays(1)));
                    var self = day.FirstOrDefault(x => x.Entry == m.Entry && x.Start == m.Start) ?? m;
                    Prepare(day, waiting);
                    new MeetingPopup(self).Show();
                }
            }
        }
        catch (Exception e) { Log.Error("meeting pop-up", e); }
        finally { busyTick = false; }
    }
}

/// <summary>The small window before a meeting: when, where, what is still open, the last file, advice.</summary>
class MeetingPopup : NovaForm
{
    protected override bool ShowWithoutActivation => true;

    public MeetingPopup(Meeting m)
    {
        Text = "Nova Enhance · next meeting";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true;
        ShowInTaskbar = false;
        Width = 460; Height = 330;
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        var s = new Stack();
        Controls.Add(s);
        var mins = Math.Max(0, (int)Math.Round((m.Start - DateTime.Now).TotalMinutes));
        s.Add(Th.L(mins > 0 ? $"📅 In {mins} min · {m.Time}" : $"📅 Now · {m.Time}", 9.5f, Th.AccentText, true));
        s.Add(Th.Wrap(m.Subject, 12, Th.Text)).Font = Th.F(12, true);
        var where = (m.Location.Length > 0 ? "📍 " + m.Location : "") + (m.Organizer.Length > 0 ? (m.Location.Length > 0 ? "  ·  " : "") + "Organiser: " + m.Organizer : "");
        if (where.Length > 0) s.Add(Th.Wrap(where, 9, Th.Muted));
        foreach (var a in m.Advice) s.Add(Th.Wrap((a.StartsWith("⚠") ? "" : "⚠ ") + a, 9.5f, Th.Warn));
        if (m.Related.Count > 0)
        {
            s.Add(Th.Section("Still open"));
            foreach (var h in m.Related)
            {
                var b = Th.B($"✉ {h.FromName} · {h.Subject}", () => { try { Outlook.Open(h.Entry, h.StoreId); } catch { } });
                b.Font = Th.F(9); b.TextAlign = ContentAlignment.MiddleLeft; b.AutoEllipsis = true; b.AutoSize = false; b.Width = 400; b.Height = 28;
                s.Add(b);
            }
        }
        if (m.LastFile.Length > 0) s.Add(Th.Wrap("📎 Last file: " + m.LastFile, 9, Th.Muted));
        var row = s.Add(Th.Row(false));
        row.Controls.Add(Th.B("Open meeting", () => Calendar.Open(m), true));
        row.Controls.Add(Th.B("☀ Brief", Screens.Brief));
        row.Controls.Add(Th.B("Dismiss", Close));
        s.Add(Th.Signature());
        var t = new System.Windows.Forms.Timer { Interval = 15 * 60_000 };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
        Load += (_, _) => { var h = s.PreferredSize.Height + 40; if (h > Height) Height = Math.Min(h, wa.Height - 40); Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16); };
    }
}
