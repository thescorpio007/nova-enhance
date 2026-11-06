using System.Text.Json.Nodes;
using NAudio.Utils;
using NAudio.Wave;

namespace NovaEnhance;

/// <summary>A list of emails with Open, Summary and ✦ Draft reply on each row.</summary>
class MailList : FlowLayoutPanel
{
    public Action<MailHit> OnSummary, OnDraft;

    public MailList()
    {
        FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.Transparent; Margin = new Padding(0);
    }

    public void Show(IEnumerable<MailHit> hits, int width)
    {
        SuspendLayout();
        foreach (Control c in Controls) c.Dispose();
        Controls.Clear();
        foreach (var h in hits) Controls.Add(RowFor(h, width));
        ResumeLayout();
    }

    static Color DotColor(string s) => s switch { "needs" => Th.Bad, "week" => Th.Warn, "follow" => Th.AccentText, "fyi" => Color.FromArgb(0x6B, 0x73, 0x85), _ => Th.Accent };

    Control RowFor(MailHit h, int width)
    {
        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Th.Card, Padding = new Padding(10, 8, 10, 6), Margin = new Padding(0, 0, 0, 6), MinimumSize = new Size(width, 0), MaximumSize = new Size(width, 0)
        };
        var top = Th.Row(false);
        var dot = Th.L("●", 11, DotColor(h.Status), true);
        top.Controls.Add(dot);
        var subj = Th.L(string.IsNullOrWhiteSpace(h.Subject) ? "(no subject)" : h.Subject, 10.5f, Th.Text, true);
        subj.MaximumSize = new Size(width - 60, 0);
        top.Controls.Add(subj);
        card.Controls.Add(top);
        var meta = h.Sent
            ? $"You → {Short(h.To)}{(h.Cc.Length > 0 ? " · Cc: " + Short(h.Cc) : "")} · {h.Date:ddd dd MMM HH:mm}"
            : $"{h.FromName} → {Short(h.To)}{(h.Cc.Length > 0 ? " · Cc: " + Short(h.Cc) : "")} · {h.Date:ddd dd MMM HH:mm}";
        if (h.AttNames.Length > 0) meta += " · 📎 " + h.AttNames.Split(';').Length;
        if (h.Status is "needs" or "week") meta += $" · waiting {h.DaysWaiting} day{(h.DaysWaiting == 1 ? "" : "s")}";
        if (h.Status == "follow") meta += $" · no answer for {h.DaysWaiting} days";
        if (h.Status == "fyi") meta += " · FYI";
        var ml = Th.L(meta, 9, h.Status == "needs" ? Th.Warn : Th.Muted);
        ml.MaximumSize = new Size(width - 24, 0);
        card.Controls.Add(ml);
        if (h.Summary.Length > 0)
        {
            var sl = Th.L("✦ " + h.Summary, 9.5f, Th.Text);
            sl.MaximumSize = new Size(width - 24, 0);
            card.Controls.Add(sl);
        }
        var btns = Th.Row(false);
        btns.Margin = new Padding(0, 4, 0, 0);
        btns.Controls.Add(Th.B("Open", () =>
        {
            try { Outlook.Open(h.Entry, h.StoreId); }
            catch (Exception e) { MessageBox.Show("Could not open it in Outlook: " + e.Message, "Nova Enhance"); }
        }));
        btns.Controls.Add(Th.B("Summary", () => OnSummary?.Invoke(h)));
        btns.Controls.Add(Th.B(h.Sent ? "✦ Draft follow-up" : "✦ Draft reply", () => OnDraft?.Invoke(h), true));
        card.Controls.Add(btns);
        return card;
    }

    static string Short(string s)
    {
        s ??= "";
        var parts = s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length <= 2 ? string.Join(", ", parts) : $"{parts[0]}, {parts[1]} +{parts.Length - 2}";
    }
}

/// <summary>Records the question for Ask Nova (Gemini turns it into text).</summary>
class Voice
{
    WaveInEvent wave;
    MemoryStream mem;
    WaveFileWriter writer;
    public bool On => wave != null;

    public void Start()
    {
        mem = new MemoryStream();
        wave = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
        writer = new WaveFileWriter(new IgnoreDisposeStream(mem), wave.WaveFormat);
        wave.DataAvailable += (_, e) => { try { writer?.Write(e.Buffer, 0, e.BytesRecorded); } catch { } };
        wave.StartRecording();
    }

    public byte[] Stop()
    {
        try { wave?.StopRecording(); } catch { }
        Thread.Sleep(150);
        try { writer?.Flush(); writer?.Dispose(); } catch { }
        try { wave?.Dispose(); } catch { }
        wave = null; writer = null;
        var b = mem?.ToArray() ?? Array.Empty<byte>();
        mem = null;
        return b;
    }
}

/// <summary>Ctrl+Shift+F: ask about your email in your own words.</summary>
class AskForm : NovaForm
{
    readonly Stack stack;
    readonly TextBox question, follow;
    readonly Label understood, info, message, idx;
    readonly FlowLayoutPanel examples, chips, bottom;
    readonly MailList list;
    readonly Button micBtn;
    readonly Voice voice = new();
    JsonObject lastFilter;
    List<MailHit> hits = new();
    string lastQuestion = "";
    CancellationTokenSource cts;
    readonly System.Windows.Forms.Timer idxTimer;

    public AskForm()
    {
        Text = "Ask Nova · your email";
        Width = 820; Height = 760;
        MinimumSize = new Size(620, 500);
        KeyPreview = true;
        stack = new Stack();
        Controls.Add(stack);

        var head = stack.Add(Th.Row(false));
        head.Controls.Add(Th.L("🔍 Ask Nova", 14, Th.Text, true));
        head.Controls.Add(Th.L("about your email", 10, Th.Muted));

        var qrow = stack.Add(new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = false, Height = 40, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 6) }, true);
        qrow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        qrow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        qrow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        question = Th.Box(false);
        question.Font = Th.F(12);
        question.Dock = DockStyle.Fill;
        question.PlaceholderText = "e.g. Did Hamza send an email to Ibrahim while I was in Cc?";
        qrow.Controls.Add(question, 0, 0);
        micBtn = Th.B("🎤", ToggleMic);
        micBtn.Margin = new Padding(8, 0, 0, 0);
        qrow.Controls.Add(micBtn, 1, 0);
        var askBtn = Th.B("Ask", () => Run(question.Text, false), true);
        askBtn.Margin = new Padding(8, 0, 0, 0);
        qrow.Controls.Add(askBtn, 2, 0);

        chips = stack.Add(Th.Row());
        idx = stack.Add(Th.Wrap("", 8.5f));

        examples = stack.Add(Th.Row());
        examples.FlowDirection = FlowDirection.TopDown;
        examples.WrapContents = false;
        examples.Controls.Add(Th.Section("Try asking"));
        foreach (var ex in new[]
                 {
                     "What is still waiting for my reply this week?",
                     "Emails from the consultant about the façade drawings last month, not answered",
                     "Find the RFI where structural asked about the slab openings",
                     "Which of my emails got no answer after 3 days?",
                     "هل أرسل حمزة الرسومات المعدلة؟"
                 })
        {
            var b = Th.B("“" + ex + "”", () => { question.Text = ex; Run(ex, false); });
            b.Font = Th.F(9.5f);
            b.ForeColor = Color.FromArgb(0xC9, 0xCE, 0xD8);
            b.BackColor = Th.Card;
            examples.Controls.Add(b);
        }

        understood = stack.Add(Th.Wrap("", 9.5f, Th.Text));
        info = stack.Add(Th.Wrap("", 9, Th.AccentText));
        message = stack.Add(Th.Wrap("", 10, Th.Text));
        message.BackColor = Th.Card;
        message.Padding = new Padding(8);
        message.Visible = false;
        list = stack.Add(new MailList());
        list.OnSummary = Summary;
        list.OnDraft = Draft;

        bottom = stack.Add(Th.Row(false));
        follow = Th.Box(false);
        follow.Width = 360;
        follow.PlaceholderText = "Ask a follow-up… e.g. only with attachments";
        bottom.Controls.Add(follow);
        bottom.Controls.Add(Th.B("Ask", () => Run(follow.Text, true)));
        bottom.Controls.Add(Th.B("⇩ Export list", Export));
        bottom.Controls.Add(Th.B("☀ Brief", Screens.Brief));
        bottom.Controls.Add(Th.B("⚙", () => Screens.Settings("Email & AI")));
        bottom.Visible = false;
        stack.Add(Th.Signature());

        question.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Run(question.Text, false); } };
        follow.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Run(follow.Text, true); } };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        VisibleChanged += (_, _) => { if (Visible) DrawChips(); };
        idxTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        idxTimer.Tick += (_, _) => { if (Visible) idx.Text = "Index: " + MailIndex.Status; };
        idxTimer.Start();
        DrawChips();
    }

    public void FocusQuestion() { question.Focus(); question.SelectAll(); }

    void DrawChips()
    {
        chips.Controls.Clear();
        var src = Store.EmailSource switch { "pc" => "Outlook on this PC", "m365" => "Microsoft 365", _ => "Outlook on PC + Microsoft 365" };
        var range = Store.Range switch { "3m" => "Last 3 months", "12m" => "Last 12 months", "pc" => "All on this PC", _ => "All dates" };
        var sees = Store.AiSees switch { "B" => "AI sees: question + first lines", "C" => "AI sees: question + emails I tap", "D" => "AI sees: first lines + emails I tap", _ => "AI sees: question only" };
        foreach (var c in new[] { "📧 " + src, "🕘 " + range, "🔒 " + sees })
        {
            var b = Th.Chip(c, false, () => Screens.Settings("Email & AI"));
            b.Font = Th.F(9);
            chips.Controls.Add(b);
        }
        idx.Text = "Index: " + MailIndex.Status;
    }

    async void ToggleMic()
    {
        try
        {
            if (!voice.On)
            {
                if (!Ai.HasKey("gemini")) { info.Text = "Voice needs a Gemini key (Settings → AI & API keys)."; return; }
                voice.Start();
                micBtn.Text = "■ Stop";
                micBtn.BackColor = Th.Bad;
                info.Text = "🎤 Listening… speak your question, then press Stop";
                return;
            }
            var wav = voice.Stop();
            micBtn.Text = "🎤"; micBtn.BackColor = Th.Card2;
            info.Text = "Turning your voice into text…";
            var text = await Ai.Transcribe(wav);
            question.Text = text;
            info.Text = "";
            if (text.Trim().Length > 0) Run(text, false);
        }
        catch (Exception e) { micBtn.Text = "🎤"; micBtn.BackColor = Th.Card2; info.Text = "⚠ Voice: " + e.Message; Log.Error("voice", e); }
    }

    async void Run(string q, bool followUp)
    {
        q = (q ?? "").Trim();
        if (q.Length == 0) return;
        if (Store.EmailSource == "m365")
        {
            ShowMessage("Microsoft 365 connection comes in build 2 (your IT admin approves it once). Choose \"Outlook on this PC\" or \"Both\" in Settings → Email & AI to search now.");
            return;
        }
        cts?.Cancel();
        var my = cts = new CancellationTokenSource();
        examples.Visible = false;
        message.Visible = false;
        understood.Text = "🔍 " + q;
        info.Text = Ai.Active() != null ? "Understanding your question…" : "Searching (no AI key — simple word search)…";
        try
        {
            var ans = await Assistant.Ask(q, followUp ? lastFilter : null, hits, my.Token);
            if (my.IsCancellationRequested) return;
            if (ans.Intent is "summarize" or "draft")
            {
                if (ans.Message.Length > 0) { ShowMessage(ans.Message); info.Text = ""; return; }
                var i = Math.Clamp(ans.Target < 0 ? 0 : ans.Target, 0, hits.Count - 1);
                if (ans.Intent == "summarize") Summary(hits[i]); else Draft(hits[i]);
                return;
            }
            lastFilter = ans.Filter;
            lastQuestion = q;
            hits = ans.Hits;
            understood.Text = "Understood as: " + ans.Explain;
            var count = hits.Count == 0 ? "No emails found" : $"{hits.Count} email{(hits.Count == 1 ? "" : "s")} found";
            info.Text = $"{count} in {ans.Seconds:0.0} s" + (MailIndex.Count() == 0 ? " · the index is still empty — open Outlook and wait a few minutes" : "");
            list.Show(hits, Math.Max(300, stack.ClientSize.Width - 50));
            bottom.Visible = true;
            follow.Text = "";
            History.Add("ask", Ai.Active()?.Id ?? "", "", "ask-mail", q, $"{hits.Count} results · {ans.Explain}");
        }
        catch (Exception e)
        {
            if (my.IsCancellationRequested) return;
            info.Text = "⚠ " + e.Message;
            Log.Error("ask", e);
        }
    }

    void ShowMessage(string s)
    {
        message.Text = s;
        message.Visible = true;
    }

    async void Summary(MailHit h)
    {
        if (!Store.AiSeesFull)
        {
            ShowMessage($"“{h.Subject}”\n\n{h.FirstLines(600)}\n\n(An AI summary needs Settings → Email & AI → C or D. This is the start of the email, read on this PC only.)");
            return;
        }
        ShowMessage($"✦ Summarising “{h.Subject}”…");
        try { ShowMessage($"✦ {h.Subject}\n\n" + await Assistant.Summarize(h, CancellationToken.None)); }
        catch (Exception e) { ShowMessage("⚠ " + e.Message); }
    }

    async void Draft(MailHit h)
    {
        if (!Store.AiSeesFull)
        {
            ShowMessage("✦ Draft reply needs Settings → Email & AI → C or D: the full email goes to the AI only when you tap Draft.");
            return;
        }
        var instr = Prompt.Ask(this, "✦ Draft reply", "Anything to include? (optional — e.g. \"agree, send revised drawings by Thursday\")");
        if (instr == null) return;
        ShowMessage($"✦ Writing a reply to “{h.Subject}”…");
        try
        {
            var draft = await Assistant.Draft(h, instr, CancellationToken.None);
            try
            {
                Outlook.Reply(h.Entry, h.StoreId, draft, false);
                ShowMessage("✓ The draft is open in Outlook — check it, then press Send. A copy is saved in Documents\\Nova Enhance\\Drafts.");
            }
            catch (Exception e)
            {
                Clipboard.SetText(draft);
                ShowMessage("Draft copied (Outlook could not open the reply: " + e.Message + ")\n\n" + draft);
            }
        }
        catch (Exception e) { ShowMessage("⚠ " + e.Message); }
    }

    void Export()
    {
        if (hits.Count == 0) return;
        try
        {
            var f = BriefLogic.Export("Ask Nova - " + lastQuestion, hits);
            info.Text = "⇩ Saved for Excel and as a printable page in Documents\\Nova Enhance\\Briefs";
            Shell.Select(f);
        }
        catch (Exception e) { info.Text = "⚠ " + e.Message; }
    }
}

/// <summary>Morning brief: waiting for your reply, and your emails with no answer.</summary>
class BriefForm : NovaForm
{
    readonly Stack stack;
    readonly Label head, info, needsTitle, followTitle, message;
    readonly MailList needs, follow;
    List<MailHit> lastNeeds = new(), lastFollow = new();
    bool busy;

    public BriefForm()
    {
        Text = "Nova Enhance · Morning brief";
        Width = 820; Height = 780;
        MinimumSize = new Size(620, 500);
        stack = new Stack();
        Controls.Add(stack);
        head = stack.Add(Th.L("☀ Good morning", 15, Th.Text, true));
        info = stack.Add(Th.Wrap("", 9.5f));
        var row = stack.Add(Th.Row());
        row.Controls.Add(Th.B("↻ Refresh", Reload, true));
        row.Controls.Add(Th.B("⇩ Export", Export));
        row.Controls.Add(Th.B("📁 Briefs folder", () => Shell.Open(Paths.Briefs)));
        row.Controls.Add(Th.B("🔍 Ask Nova", Screens.Ask));
        row.Controls.Add(Th.B("⚙ Rules", () => Screens.Settings("Email & AI")));
        message = stack.Add(Th.Wrap("", 10, Th.Text));
        message.BackColor = Th.Card; message.Padding = new Padding(8); message.Visible = false;
        needsTitle = stack.Add(Th.Section("Waiting for your reply"));
        needs = stack.Add(new MailList());
        followTitle = stack.Add(Th.Section("Your emails with no answer"));
        follow = stack.Add(new MailList());
        foreach (var l in new[] { needs, follow })
        {
            l.OnSummary = h => Screens.Ask();
            l.OnDraft = h => Screens.Ask();
        }
        needs.OnSummary = follow.OnSummary = async h =>
        {
            message.Visible = true;
            message.Text = Store.AiSeesFull ? "✦ Summarising…" : h.FirstLines(600) + "\n\n(AI summary needs Settings → Email & AI → C or D.)";
            if (Store.AiSeesFull) try { message.Text = "✦ " + h.Subject + "\n\n" + await Assistant.Summarize(h, CancellationToken.None); } catch (Exception e) { message.Text = "⚠ " + e.Message; }
        };
        needs.OnDraft = follow.OnDraft = async h =>
        {
            message.Visible = true;
            if (!Store.AiSeesFull) { message.Text = "✦ Draft reply needs Settings → Email & AI → C or D."; return; }
            var instr = Prompt.Ask(this, "✦ Draft", "Anything to include? (optional)");
            if (instr == null) return;
            message.Text = "✦ Writing…";
            try
            {
                var d = await Assistant.Draft(h, (h.Sent ? "This is a polite follow-up to my own email that got no answer. " : "") + instr, CancellationToken.None);
                Outlook.Reply(h.Entry, h.StoreId, d, false);
                message.Text = "✓ The draft is open in Outlook — check it, then press Send.";
            }
            catch (Exception e) { message.Text = "⚠ " + e.Message; }
        };
        stack.Add(Th.Signature());
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public async void Reload()
    {
        if (busy) return;
        busy = true;
        var hour = DateTime.Now.Hour;
        head.Text = (hour < 12 ? "☀ Good morning" : hour < 17 ? "☀ Good afternoon" : "☾ Good evening") + (Me.FirstName.Length > 0 ? ", " + Me.FirstName : "");
        info.Text = "Reading your index…";
        try
        {
            var (n, f) = await BriefLogic.Build(CancellationToken.None);
            lastNeeds = n; lastFollow = f;
            int red = n.Count(h => h.Status == "needs"), orange = n.Count(h => h.Status == "week"), fyi = n.Count(h => h.Status == "fyi");
            info.Text = $"{DateTime.Now:dddd dd MMMM} · 🔴 {red} overdue · 🟠 {orange} this week · ⚪ {fyi} FYI · ↩ {f.Count} follow-ups · last {Store.BriefDays} days · Index: {MailIndex.Status}";
            needsTitle.Text = $"WAITING FOR YOUR REPLY ({n.Count})";
            followTitle.Text = Store.FollowUps ? $"YOUR EMAILS WITH NO ANSWER AFTER {Store.FollowDays} DAYS ({f.Count})" : "FOLLOW-UPS ARE OFF (SETTINGS → EMAIL & AI)";
            var w = Math.Max(300, stack.ClientSize.Width - 50);
            needs.Show(n, w);
            follow.Show(f, w);
            if (n.Count == 0 && f.Count == 0)
            {
                message.Visible = true;
                message.Text = MailIndex.Count() == 0
                    ? "The email index is still empty. Open Outlook and leave it running — Nova builds the index in the background (the first time can take a while)."
                    : "✓ Nothing is waiting for you. Enjoy your day!";
            }
            else message.Visible = false;
        }
        catch (Exception e) { info.Text = "⚠ " + e.Message; Log.Error("brief", e); }
        finally { busy = false; }
    }

    void Export()
    {
        try
        {
            var f = BriefLogic.Export($"Morning brief {DateTime.Now:yyyy-MM-dd}", lastNeeds.Concat(lastFollow));
            info.Text = "⇩ Saved for Excel and as a printable page in Documents\\Nova Enhance\\Briefs";
            Shell.Select(f);
        }
        catch (Exception e) { info.Text = "⚠ " + e.Message; }
    }
}
