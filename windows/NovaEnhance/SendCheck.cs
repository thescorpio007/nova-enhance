using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🛡 Check before sending (anti-oops): when you press Send in Outlook, the email waits in a large Nova window — your text next to
/// the checked one (signature untouched), and checks for attachments, outside people, dates and private words — until you choose.
/// Nova listens to Outlook's Send from this PC (nothing to install). If Nova is closed, Outlook sends as usual. Designed by A.HAMROUNI
/// </summary>
static class SendCheck
{
    public static bool On { get => Store.Bool("win_sendcheck", false); set { Store.SetBool("win_sendcheck", value); Ensure(); } }
    /// <summary>all = grammar + checks · grammar = grammar and spelling only · quick = attachments, people, dates (no AI).</summary>
    public static string What { get => Store.Str("win_sendcheck_what", "all"); set => Store.Set("win_sendcheck_what", value); }
    /// <summary>all = every email · external = only to people outside my company · noforward = new emails and replies only.</summary>
    public static string When { get => Store.Str("win_sendcheck_when", "all"); set => Store.Set("win_sendcheck_when", value); }
    public static bool SkipClean { get => Store.Bool("win_sendcheck_skipclean", true); set => Store.SetBool("win_sendcheck_skipclean", value); }
    public static bool Short { get => Store.Bool("win_sendcheck_short", false); set => Store.SetBool("win_sendcheck_short", value); }
    public static string Status = "";

    delegate void ItemSendHandler(object item, ref bool cancel);
    static readonly Guid AppEvents = new("0006300E-0000-0000-C000-000000000046");   // Outlook ApplicationEvents_11
    const int ItemSendId = 0xF002;
    static object hooked;
    static ItemSendHandler handler;
    static bool passNext;

    /// <summary>Connects to Outlook's Send (on the window thread; called at start and every minute — Outlook may start later or restart).</summary>
    static System.Windows.Forms.Timer watch;
    static bool warned;

    public static void Ensure()
    {
        // Checked every 10 s: a restarted Outlook is watched again almost at once.
        if (watch == null) { watch = new System.Windows.Forms.Timer { Interval = 10_000 }; watch.Tick += (_, _) => Ensure(); watch.Start(); }
        if (!On) { if (hooked != null) Log.Write("🛡 check before sending: off"); Unhook(); Status = "Off"; return; }
        try { if (hooked != null) { _ = ((dynamic)hooked).Name; return; } }
        catch { hooked = null; Log.Write("🛡 check before sending: Outlook closed or restarted — connecting again"); }
        try
        {
            object app = Outlook.Running();
            if (app == null) { Status = "Waiting for Outlook to open…"; return; }
            handler = OnItemSend;
            ComEventsHelper.Combine(app, AppEvents, ItemSendId, handler);
            hooked = app;
            Status = $"✓ Watching Outlook's Send (since {DateTime.Now:HH:mm:ss})";
            warned = false;
            Log.Write("🛡 check before sending: connected to Outlook's Send");
        }
        catch (Exception e)
        {
            Status = "⚠ Could not connect to Outlook's Send: " + e.Message;
            Log.Warn("check before sending", "connect: " + e.Message);
            if (!warned) { warned = true; Program.Balloon("🛡 Check before sending is NOT active", "Nova could not connect to Outlook's Send — emails go out without the check. " + e.Message); }
        }
    }

    static void Unhook()
    {
        try { if (hooked != null && handler != null) ComEventsHelper.Remove(hooked, AppEvents, ItemSendId, handler); } catch { }
        hooked = null;
    }

    public class Snap
    {
        public string Subject = "", To = "", Cc = "", Own = "";
        public List<string> Outside = new(), Atts = new();
        public bool Forward;
    }

    static void OnItemSend(object item, ref bool cancel)
    {
        try
        {
            if (passNext) { passNext = false; Log.Write("🛡 Send: your choice in the check window — sent"); return; }
            if (!On) { Log.Write("🛡 Send: check is off — sent"); return; }
            dynamic m = item;
            int cls = 0; try { cls = (int)m.Class; } catch { }
            string subj = ""; try { subj = (string)m.Subject ?? ""; } catch { }
            if (cls != 43) { Log.Write($"🛡 Send: not an email (class {cls}) — sent"); return; }   // meeting replies, tasks…
            Snap s = Take(m);
            if (When == "external" && s.Outside.Count == 0) { Log.Write($"🛡 Send “{subj}”: nobody outside the company — sent without the check"); return; }
            if (When == "noforward" && s.Forward) { Log.Write($"🛡 Send “{subj}”: a forward — sent without the check"); return; }
            if (!Short && Regex.Matches(s.Own, @"\S+").Count <= 6) { Log.Write($"🛡 Send “{subj}”: one-line email — sent without the check"); return; }
            cancel = true;                                          // held: nothing leaves until you choose
            Log.Write($"🛡 Send “{subj}”: held for your check ({Regex.Matches(s.Own, @"\S+").Count} words)");
            Program.UiContext.Post(_ => { try { new SendCheckForm(item, s).Begin(); } catch (Exception e) { Log.Error("check before sending", e); } }, null);
        }
        catch (Exception e) { Log.Warn("check before sending", "⚠ sent without the check: " + e.Message); }   // any problem: Outlook sends as usual
    }

    /// <summary>Sends the email now (your choice in the window) — Nova lets this one through.</summary>
    public static void SendNow(object item)
    {
        passNext = true;
        try { ((dynamic)item).Send(); }
        catch { passNext = false; throw; }
    }

    static string S(Func<object> f) { try { return f()?.ToString() ?? ""; } catch { return ""; } }

    static Snap Take(dynamic m)
    {
        var s = new Snap { Subject = S(() => m.Subject), To = S(() => m.To), Cc = S(() => m.CC) };
        s.Forward = Regex.IsMatch(s.Subject, @"^\s*(fw|fwd|tr|wg)\s*:", RegexOptions.IgnoreCase);
        var mine = Me.Addrs.Select(a => a.Split('@').LastOrDefault()?.ToLowerInvariant() ?? "").Where(d => d.Length > 0).ToHashSet();
        try
        {
            dynamic rs = m.Recipients; int n = (int)rs.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic r = rs.Item(i);
                var addr = S(() => r.Address);
                if (!addr.Contains('@')) { try { addr = S(() => r.AddressEntry.GetExchangeUser().PrimarySmtpAddress); } catch { } }
                if (!addr.Contains('@') || mine.Count == 0) continue;
                if (!mine.Contains(addr.Split('@').Last().ToLowerInvariant())) s.Outside.Add(S(() => r.Name) is { Length: > 0 } nm ? nm : addr);
            }
        }
        catch { }
        try
        {
            dynamic a = m.Attachments; int n = (int)a.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic x = a.Item(i);
                var name = S(() => x.FileName);
                bool hidden = false;
                try { hidden = (bool)x.PropertyAccessor.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x7FFE000B"); } catch { }
                if (!hidden && !Regex.IsMatch(name, @"^image\d*\.(png|jpe?g|gif|bmp)$", RegexOptions.IgnoreCase)) s.Atts.Add(name);
            }
        }
        catch { }
        s.Own = OutlookBody.ReadFrom(OutlookBody.EditorFor(m));
        if (s.Own.Length == 0) s.Own = OwnFromPlain(S(() => m.Body));
        return s;
    }

    /// <summary>Plain text: down to the closing, the signature or the earlier emails.</summary>
    static string OwnFromPlain(string body)
    {
        var t = (body ?? "").Replace("\r\n", "\n");
        var q = Regex.Match(t, @"\n\s*(From|De|Von|Van|Da|من)\s*:\s|\n[\-_*=]{8,}", RegexOptions.IgnoreCase);
        if (q.Success) t = t[..q.Index];
        var lines = t.Split('\n');
        var cut = OutlookBody.SigLine(lines);
        if (cut >= 0) t = string.Join("\n", lines.Take(cut));
        return t.Trim().Replace("\n", "\r\n");
    }

    // ------------------------------------------------------------------ the checks that need no AI

    static readonly Regex AttachWords = new(@"\b(attach(ed|ment|ments)?|enclosed|herewith|find (the )?(file|document|drawing)s?)\b|مرفق|ci-joint|pièce jointe|anbei|im anhang", RegexOptions.IgnoreCase);
    static readonly string[] Days = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };
    static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

    public static List<(string Level, string Title, string Note)> Quick(Snap s)
    {
        var l = new List<(string, string, string)>();
        if (AttachWords.IsMatch(s.Own) && s.Atts.Count == 0) l.Add(("bad", "📎 No attachment", "The email mentions an attachment, but nothing is attached."));
        else if (s.Atts.Count > 0) l.Add(("ok", $"📎 {s.Atts.Count} attachment(s)", string.Join(" · ", s.Atts.Take(4))));
        if (s.Outside.Count > 0) l.Add(("warn", $"👥 {s.Outside.Count} outside your company", string.Join(" · ", s.Outside.Take(5))));
        if (s.Subject.Trim().Length == 0 || Regex.IsMatch(s.Subject, @"^\s*(re|fw|fwd)\s*:\s*$", RegexOptions.IgnoreCase)) l.Add(("warn", "✉ No subject", "The email has no subject."));
        foreach (Match d in Regex.Matches(s.Own, @"\b(sun|mon|tues?|wed(?:nes)?|thu(?:rs)?|fri|sat(?:ur)?)(?:day)?\b[\s,]*(\d{1,2})(?:st|nd|rd|th)?[\s\-/]*(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?(?:[\s,]+(\d{4}))?", RegexOptions.IgnoreCase))
        {
            int di = Array.FindIndex(Days, x => x.StartsWith(d.Groups[1].Value.ToLowerInvariant()[..3]));
            int mi = Array.IndexOf(Months, d.Groups[3].Value.ToLowerInvariant());
            if (di < 0 || mi < 0 || !int.TryParse(d.Groups[2].Value, out var day) || day < 1) continue;
            int year = d.Groups[4].Success ? int.Parse(d.Groups[4].Value) : DateTime.Today.Year;
            if (!d.Groups[4].Success && new DateTime(year, mi + 1, Math.Min(day, 28)) < DateTime.Today.AddMonths(-2)) year++;
            if (day > DateTime.DaysInMonth(year, mi + 1)) { l.Add(("warn", "📅 Date check", $"“{d.Value.Trim()}” — that date does not exist.")); continue; }
            var real = new DateTime(year, mi + 1, day).DayOfWeek;
            if ((int)real != di) l.Add(("warn", "📅 Date check", $"“{d.Value.Trim()}” — {day} {System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(mi + 1)} {year} is a {real}."));
        }
        if (Projects.Private.IsMatch(s.Own)) l.Add(("warn", "🔒 Private words", "Words like password, bank or salary — not sent to the AI."));
        else l.Add(("ok", "✓ No 🔒 private words", "Nothing private found."));
        return l;
    }

    // ------------------------------------------------------------------ the AI check

    public static async Task<(string Text, List<(string Level, string Title, string Note)> Issues, string Tone)> AiCheck(Snap s, CancellationToken ct)
    {
        var grammarOnly = What == "grammar";
        var sys = "You check an email written by " + Persona.Who + " just before it is sent. Reply with JSON only: " +
                  "{\"text\":\"the same email text with grammar, spelling, punctuation and wrong or misused words corrected — same language, same meaning, same tone, " +
                  "same names, numbers, document numbers and line breaks; do not add a greeting, closing or signature\"" +
                  (grammarOnly ? "}" : ",\"tone\":\"one or two words\",\"issues\":[{\"level\":\"warn|bad\",\"title\":\"short\",\"note\":\"short reason\"}]} — issues only for real problems a careful colleague would flag: " +
                  "an unclear or ambiguous sentence, something promised but missing, numbers or dates that contradict each other, a rude or too-blunt sentence, a wrong recipient name in the greeting. No issues = []. ") +
                  " Never invent facts.";
        var user = $"Subject: {s.Subject}\nTo: {s.To}\nCc: {s.Cc}\nAttachments: {(s.Atts.Count > 0 ? string.Join(", ", s.Atts) : "none")}\n\nEMAIL TEXT:\n{s.Own}";
        var j = NovaEnhance.Ai.ParseJson(await NovaEnhance.Ai.Chat(sys, user, true, true, null, ct));
        var issues = new List<(string, string, string)>();
        foreach (var x in (j["issues"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            issues.Add((x["level"]?.ToString() == "bad" ? "bad" : "warn", "✦ " + (x["title"]?.ToString() ?? ""), x["note"]?.ToString() ?? ""));
        return ((j["text"]?.ToString() ?? "").Trim(), issues, j["tone"]?.ToString() ?? "");
    }

    // ------------------------------------------------------------------ word differences (for the two panes)

    public static List<(string Text, int Kind)> Diff(string a, string b)
    {
        var x = Regex.Split(a ?? "", @"(\s+)").Where(t => t.Length > 0).ToArray();
        var y = Regex.Split(b ?? "", @"(\s+)").Where(t => t.Length > 0).ToArray();
        var res = new List<(string, int)>();
        if (x.Length * (long)y.Length > 4_000_000) { res.Add((a, -1)); res.Add((b, 1)); return res; }
        var L = new int[x.Length + 1, y.Length + 1];
        for (int i = x.Length - 1; i >= 0; i--)
            for (int k = y.Length - 1; k >= 0; k--)
                L[i, k] = x[i] == y[k] ? L[i + 1, k + 1] + 1 : Math.Max(L[i + 1, k], L[i, k + 1]);
        int p = 0, q = 0;
        while (p < x.Length && q < y.Length)
        {
            if (x[p] == y[q]) { res.Add((x[p], 0)); p++; q++; }
            else if (L[p + 1, q] >= L[p, q + 1]) res.Add((x[p++], -1));
            else res.Add((y[q++], 1));
        }
        while (p < x.Length) res.Add((x[p++], -1));
        while (q < y.Length) res.Add((y[q++], 1));
        return res;
    }
}

/// <summary>🛡 The large window: your email next to the checked one, the checks, and your choice.</summary>
class SendCheckForm : NovaForm
{
    readonly object item;
    readonly SendCheck.Snap snap;
    readonly RichTextBox mine, theirs;
    readonly FlowLayoutPanel checks;
    readonly Label status, count;
    readonly Button sendChecked, anyway;
    string corrected = "";
    bool decided;
    CancellationTokenSource cts;

    public SendCheckForm(object item, SendCheck.Snap s)
    {
        this.item = item; snap = s;
        Text = "Nova Enhance · 🛡 Check before sending";
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.PrimaryScreen.WorkingArea;
        Width = Math.Min(1400, wa.Width - 60); Height = Math.Min(900, wa.Height - 40);
        MinimumSize = new Size(900, 600);
        TopMost = true; KeyPreview = true;

        var top = new Stack { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20, 12, 20, 4) };
        top.Add(Th.L("🛡 Check before sending", 16, Th.AccentText, true));
        top.Add(Th.Wrap("Nova read your text (your closing, signature and earlier emails are left untouched). Nothing has been sent yet.", 9.5f, Th.Muted), true);
        top.Add(Th.Wrap($"To: {s.To}" + (s.Cc.Length > 0 ? $"\nCc: {s.Cc}" : "") + $"\nSubject: {s.Subject}", 10, Th.Text), true);
        status = top.Add(Th.Wrap("", 9.5f, Th.AccentText), true);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Th.Bg, Padding = new Padding(20, 4, 20, 4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(Th.L("YOUR EMAIL", 9, Th.Muted, true), 0, 0);
        count = Th.L("✦ CHECKED BY NOVA", 9, Th.Muted, true); count.AutoSize = true;
        grid.Controls.Add(count, 1, 0);
        grid.Controls.Add(Th.L("CHECKS", 9, Th.Muted, true), 2, 0);
        RichTextBox Box() => new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(0x19, 0x1E, 0x28), ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(11), Margin = new Padding(0, 4, 10, 4), DetectUrls = false };
        mine = Box(); theirs = Box();
        theirs.ReadOnly = false;   // you may still change the checked text before sending
        grid.Controls.Add(mine, 0, 1); grid.Controls.Add(theirs, 1, 1);
        checks = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Th.Bg, Margin = new Padding(0, 4, 0, 4) };
        grid.Controls.Add(checks, 2, 1);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, BackColor = Color.FromArgb(0x0E, 0x11, 0x17), Padding = new Padding(20, 12, 20, 8) };
        sendChecked = Th.B("✓ Send the checked version", () => Send(true), true);
        sendChecked.BackColor = Color.FromArgb(0x1E, 0x7A, 0x4C); sendChecked.Enabled = false;
        bottom.Controls.Add(sendChecked);
        bottom.Controls.Add(Th.B("Send my original", () => Send(false)));
        bottom.Controls.Add(Th.B("✎ Back to Outlook to edit", Back));
        bottom.Controls.Add(Th.B("📎 Attach a file…", Attach));
        anyway = Th.B("✦ Check with my AI anyway", async () => await RunAi(quick.Any(c => c.Level != "ok" && !c.Title.Contains("Private"))), true);
        anyway.Visible = false;
        bottom.Controls.Add(anyway);
        var hint = Th.L("Enter = send checked · Esc = back to edit · " + Program.Sign, 8.5f, Th.Muted); hint.Margin = new Padding(16, 10, 0, 0);
        bottom.Controls.Add(hint);

        Controls.Add(grid); Controls.Add(bottom); Controls.Add(top);
        mine.Text = s.Own; theirs.Text = s.Own;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Back(); }
            else if (e.KeyCode == Keys.Enter && !theirs.Focused && sendChecked.Enabled) { e.Handled = true; e.SuppressKeyPress = true; Send(true); }
        };
        FormClosing += (_, _) => { cts?.Cancel(); if (!decided) Program.Balloon("🛡 Not sent", "Your email is still open in Outlook."); };
    }

    void Card((string Level, string Title, string Note) c)
    {
        var col = c.Level == "bad" ? Th.Bad : c.Level == "warn" ? Th.Warn : Th.Good;
        var p = new Panel { Width = 310, BackColor = Color.FromArgb(0x16, 0x1A, 0x23), Margin = new Padding(0, 0, 0, 8), Padding = new Padding(10, 8, 10, 8) };
        var t = Th.L(c.Title, 10, col, true); t.Dock = DockStyle.Top;
        var n = Th.Wrap(c.Note, 9, Th.Muted); n.Dock = DockStyle.Top; n.MaximumSize = new Size(290, 0);
        p.Controls.Add(n); p.Controls.Add(t);
        p.Height = t.PreferredHeight + n.GetPreferredSize(new Size(290, 0)).Height + 20;
        checks.Controls.Add(p);
    }

    List<(string Level, string Title, string Note)> quick = new();

    void DrawChecks(IEnumerable<(string, string, string)> extra)
    {
        checks.SuspendLayout();
        foreach (Control c in checks.Controls.Cast<Control>().ToList()) c.Dispose();
        foreach (var c in quick.Concat(extra).OrderBy(c => c.Item1 == "bad" ? 0 : c.Item1 == "warn" ? 1 : 2)) Card(c);
        checks.ResumeLayout();
    }

    public async void Begin()
    {
        Show(); Activate();
        quick = SendCheck.Quick(snap);
        DrawChecks(Array.Empty<(string, string, string)>());
        var problems = quick.Any(c => c.Level != "ok");
        if (SendCheck.What == "quick" || NovaEnhance.Ai.Active() == null)
        {
            status.Text = SendCheck.What == "quick" ? "Quick checks only (no AI)." : "No AI key — quick checks only.";
            sendChecked.Visible = false; count.Text = "";
            if (!problems && SendCheck.SkipClean) { Done("✓ Checked — no problems found. Sent."); }
            return;
        }
        if (Projects.Private.IsMatch(snap.Own))
        {
            // 🔒 Private words: your choice, email by email.
            status.Text = "🔒 Private words (password, bank…) — the text was not sent to the AI.";
            sendChecked.Visible = false; count.Text = "";
            anyway.Visible = true;
            return;
        }
        await RunAi(problems);
    }

    async Task RunAi(bool problems)
    {
        anyway.Visible = false;
        sendChecked.Visible = true;
        status.Text = "✦ Checking grammar, spelling and wording…";
        cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var (text, issues, tone) = await SendCheck.AiCheck(snap, cts.Token);
            if (decided || IsDisposed) return;
            corrected = text.Length > 0 ? text : snap.Own;
            var diff = SendCheck.Diff(snap.Own, corrected);
            int changes = diff.Count(d => d.Kind != 0 && d.Text.Trim().Length > 0);
            PaintDiff(mine, diff.Where(d => d.Kind <= 0).ToList(), true);
            PaintDiff(theirs, diff.Where(d => d.Kind >= 0).ToList(), false);
            var fixes = diff.Count(d => d.Kind == 1 && d.Text.Trim().Length > 0);
            count.Text = $"✦ CHECKED BY NOVA · {fixes} correction(s)" + (tone.Length > 0 ? $" · tone: {tone}" : "");
            var gram = fixes == 0 ? ("ok", "✓ Grammar & spelling", "No corrections needed.") : ("ok", "✓ Grammar & spelling", $"{fixes} fix(es), shown in green — you can still edit the checked text.");
            DrawChecks(issues.Append(gram));
            sendChecked.Enabled = true;
            status.Text = "Choose below. Nothing has been sent yet.";
            if (changes == 0 && !problems && issues.Count == 0 && SendCheck.SkipClean) Done("✓ Checked — no problems found. Sent.");
        }
        catch (Exception e) when (!decided)
        {
            Log.Warn("check before sending", e.Message);
            sendChecked.Visible = false;
            if (!problems) { Done("Sent as written — the AI did not answer within 30 s."); return; }
            status.Text = "⚠ The AI did not answer within 30 s — the quick checks are shown; choose below.";
        }
        catch { }
    }

    static void PaintDiff(RichTextBox box, List<(string Text, int Kind)> parts, bool left)
    {
        box.SuspendLayout();
        box.Clear();
        var normal = box.Font;
        using var strike = new Font(normal, FontStyle.Strikeout);
        foreach (var (t, k) in parts)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor = k == 0 ? Th.Text : left ? Color.FromArgb(0xFF, 0x9A, 0xA5) : Color.FromArgb(0x8B, 0xF0, 0xB5);
            box.SelectionBackColor = k == 0 ? box.BackColor : left ? Color.FromArgb(0x4A, 0x1E, 0x26) : Color.FromArgb(0x17, 0x3A, 0x2A);
            box.SelectionFont = k != 0 && left ? strike : normal;
            box.AppendText(t);
        }
        box.SelectionStart = 0;
        box.ResumeLayout();
    }

    void Done(string note)
    {
        try { SendCheck.SendNow(item); Program.Balloon("🛡 Check before sending", note); }
        catch (Exception e) { Program.Balloon("🛡 Not sent", "Outlook refused: " + e.Message); }
        decided = true;
        Close();
    }

    void Send(bool checkedVersion)
    {
        if (checkedVersion)
        {
            var text = theirs.Text.Trim();
            if (text.Length > 0 && text != snap.Own.Trim())
            {
                var doc = OutlookBody.EditorFor(item);
                if (!OutlookBody.ReplaceIn(doc, text))
                {
                    status.Text = "⚠ Outlook did not accept the corrected text — choose “Back to Outlook to edit”, or send your original.";
                    return;
                }
            }
        }
        Done(checkedVersion ? "✓ Sent the checked version." : "✓ Sent your original.");
    }

    void Back()
    {
        decided = true;
        try { dynamic m = item; m.Display(false); } catch { }
        Close();
    }

    void Attach()
    {
        using var d = new OpenFileDialog { Title = "Attach a file", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            dynamic m = item;
            foreach (var f in d.FileNames) { m.Attachments.Add(f); snap.Atts.Add(Path.GetFileName(f)); }
            quick = SendCheck.Quick(snap);
            DrawChecks(Array.Empty<(string, string, string)>());
            status.Text = $"📎 Attached {d.FileNames.Length} file(s).";
        }
        catch (Exception e) { status.Text = "⚠ " + e.Message; }
    }
}
