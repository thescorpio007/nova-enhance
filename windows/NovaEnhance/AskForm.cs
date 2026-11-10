using System.Text.Json.Nodes;
using NAudio.Utils;
using NAudio.Wave;

namespace NovaEnhance;

/// <summary>
/// A list of emails. Cards mode: Open, Summary, ✦ Draft reply and ✓ Handled on each card (wider window = longer lines and the
/// first lines of the email). Compact mode (next to the reading pane): click a card to read it on the right.
/// Emails Nova thinks were probably answered already go in a grey "Probably answered" section at the bottom.
/// </summary>
class MailList : FlowLayoutPanel
{
    public Action<MailHit> OnSummary, OnDraft, OnSelect, OnHandled;
    public bool Compact;
    /// <summary>✦ Prompts: group the list (project / sender / day), and the emails your instructions hid (shown on request).</summary>
    public Func<MailHit, string> GroupKey;
    public List<(string Why, MailHit Hit)> HiddenShown;
    readonly Dictionary<MailHit, Control> cards = new();
    public MailHit Selected { get; private set; }
    public List<MailHit> Order { get; } = new();
    static readonly Color SelColor = Color.FromArgb(0x26, 0x2A, 0x40), GreyCard = Color.FromArgb(0x15, 0x18, 0x1F);

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
        cards.Clear();
        Order.Clear();
        var all = hits.ToList();
        var main = all.Where(h => h.MaybeAnswered.Length == 0).ToList();
        var maybe = all.Where(h => h.MaybeAnswered.Length > 0).ToList();
        if (GroupKey == null) foreach (var h in main) Add(h, width, false);
        else
            foreach (var g in main.GroupBy(GroupKey).OrderBy(g => g.Key == "Other" ? 1 : 0).ThenBy(g => g.Key))
            {
                var t = Th.Section($"🏷 {(g.Key.Length > 0 ? g.Key : "Other")} ({g.Count()})");
                t.Margin = new Padding(0, Controls.Count == 0 ? 0 : 12, 0, 6);
                Controls.Add(t);
                foreach (var h in g) Add(h, width, false);
            }
        if (maybe.Count > 0)
        {
            var t = Th.Section($"Probably answered ({maybe.Count}) — check before replying");
            t.Margin = new Padding(0, 14, 0, 6);
            Controls.Add(t);
            foreach (var h in maybe) Add(h, width, true);
        }
        if (HiddenShown is { Count: > 0 })
        {
            var t = Th.Section($"Hidden by your instructions ({HiddenShown.Count})");
            t.Margin = new Padding(0, 14, 0, 6);
            Controls.Add(t);
            foreach (var (why, h) in HiddenShown) { var c = RowFor(h, width, true, "⛔ " + why); cards[h] = c; Order.Add(h); Controls.Add(c); }
        }
        ResumeLayout();
        if (Selected != null && !cards.ContainsKey(Selected)) Selected = null;
        if (Selected != null) PaintCard(Selected);
    }

    void Add(MailHit h, int width, bool grey)
    {
        var c = RowFor(h, width, grey);
        cards[h] = c;
        Order.Add(h);
        Controls.Add(c);
    }

    public void Select(MailHit h)
    {
        if (h == null || !cards.ContainsKey(h)) return;
        var old = Selected;
        Selected = h;
        if (old != null && cards.ContainsKey(old)) PaintCard(old);
        PaintCard(h);
        OnSelect?.Invoke(h);
        if (Parent is ScrollableControl sc) try { sc.ScrollControlIntoView(cards[h]); } catch { }
    }

    void OpenMenu(MailHit h, Point pt) => EmailMenu.Show(h, this, pt, OnSummary, OnDraft, OnHandled, Compact ? m => Select(m) : null);

    /// <summary>Menu key / Shift+F10: the menu of the selected email.</summary>
    public bool ShowMenu()
    {
        var h = Selected ?? Order.FirstOrDefault();
        if (h == null || !cards.TryGetValue(h, out var c)) return false;
        OpenMenu(h, c.PointToScreen(new Point(60, 34)));
        return true;
    }

    public void Unselect()
    {
        var old = Selected;
        Selected = null;
        if (old != null) PaintCard(old);
    }

    public void MoveBy(int d)
    {
        if (Order.Count == 0) return;
        var i = Selected == null ? -1 : Order.IndexOf(Selected);
        Select(Order[Math.Clamp(i + d, 0, Order.Count - 1)]);
    }

    void PaintCard(MailHit h)
    {
        if (!cards.TryGetValue(h, out var c)) return;
        var back = h == Selected && Compact ? SelColor : h.MaybeAnswered.Length > 0 || (HiddenShown?.Any(x => x.Hit == h) ?? false) ? GreyCard : Th.Card;
        c.BackColor = back;
    }

    static Color DotColor(string s) => s switch { "needs" => Th.Bad, "week" => Th.Warn, "follow" => Th.AccentText, "fyi" => Color.FromArgb(0x6B, 0x73, 0x85), _ => Th.Accent };

    Control RowFor(MailHit h, int width, bool grey, string hiddenWhy = null)
    {
        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = grey ? GreyCard : Th.Card, Padding = new Padding(10, 8, 10, 6), Margin = new Padding(0, 0, 0, 6),
            MinimumSize = new Size(width, 0), MaximumSize = new Size(width, 0)
        };
        var textColor = grey ? Th.Muted : Th.Text;
        var top = Th.Row(false);
        top.Controls.Add(new Dot(grey ? Color.FromArgb(0x6B, 0x73, 0x85) : DotColor(h.Status)));
        var subjText = string.IsNullOrWhiteSpace(h.Subject) ? "(no subject)" : h.Subject;
        var subj = Th.L(subjText, Compact ? 10 : 10.5f, textColor, true);
        if (Compact) { subj.AutoSize = false; subj.AutoEllipsis = true; subj.Width = width - 60; subj.Height = 22; }
        else subj.MaximumSize = new Size(width - 60, 0);
        top.Controls.Add(subj);
        card.Controls.Add(top);

        var who = h.Sent ? "You → " + Short(h.To, 4) : Compact ? h.FromName : $"{h.FromName} → {Short(h.To, width > 900 ? 4 : 2)}";
        var meta = who + (!Compact && h.Cc.Length > 0 ? " · Cc: " + Short(h.Cc, width > 900 ? 4 : 2) : "") + $" · {h.Date:ddd dd MMM HH:mm}";
        if (h.AttNames.Length > 0) meta += " · 📎 " + h.AttNames.Split(';').Length;
        if (!grey && h.Status is "needs" or "week") meta += $" · waiting {h.DaysWaiting} day{(h.DaysWaiting == 1 ? "" : "s")}";
        if (h.Status == "follow") meta += $" · no answer for {h.DaysWaiting} days";
        if (h.Status == "fyi" && !grey) meta += " · FYI";
        if (h.Earlier > 0) meta += $" · +{h.Earlier} earlier in this topic";
        var ml = Th.L(meta, 9, grey ? Th.Muted : h.Status == "needs" ? Th.Warn : Th.Muted);
        ml.MaximumSize = new Size(width - 24, 0);
        card.Controls.Add(ml);
        if (grey)
        {
            var why = Th.L(hiddenWhy ?? "↩ " + h.MaybeAnswered, 9, Th.AccentText);
            why.MaximumSize = new Size(width - 24, 0);
            card.Controls.Add(why);
        }
        if (h.Why.Length > 0)
        {
            var wl = Th.L("✦ " + h.Why, 9.5f, Color.FromArgb(0x8F, 0xD6, 0xB4));
            wl.MaximumSize = new Size(width - 24, 0);
            card.Controls.Add(wl);
        }
        if (h.Summary.Length > 0)
        {
            var sl = Th.L("✦ " + h.Summary, 9.5f, textColor);
            sl.MaximumSize = new Size(width - 24, 0);
            card.Controls.Add(sl);
        }
        // Compact cards (beside the reading pane): one or two lines of the email, so you can judge it without opening it.
        if (Compact && width >= 360)
        {
            var fl = Th.L(h.FirstLines(Math.Clamp(width / 3, 120, 260)), 8.5f, Color.FromArgb(0x8A, 0x93, 0xA5));
            fl.AutoSize = false; fl.AutoEllipsis = true; fl.Width = width - 24; fl.Height = 32; fl.UseMnemonic = false;
            card.Controls.Add(fl);
        }
        // Wider window: the first lines of the email, read on this PC only.
        if (!Compact && width >= 900)
        {
            var n = Math.Clamp((width - 300) / 4, 150, 420);
            var fl = Th.L("“" + h.FirstLines(n) + "”", 8.5f, Color.FromArgb(0x8A, 0x93, 0xA5));
            fl.MaximumSize = new Size(width - 24, 0);
            card.Controls.Add(fl);
        }
        if (!Compact)
        {
            var btns = Th.Row(false);
            btns.Margin = new Padding(0, 4, 0, 0);
            btns.Controls.Add(Th.B("Open", () =>
            {
                try { Outlook.Open(h.Entry, h.StoreId); }
                catch (Exception e) { MessageBox.Show("Could not open it in Outlook: " + e.Message, "Nova Enhance"); }
            }));
            btns.Controls.Add(Th.B("Summary", () => OnSummary?.Invoke(h)));
            btns.Controls.Add(Th.B(h.Sent ? "✦ Draft follow-up" : "✦ Draft reply", () => OnDraft?.Invoke(h), true));
        btns.Controls.Add(Th.B("✦ Ask about this email", () => Screens.MailAsk(h)));
            if (!h.Sent && OnHandled != null) btns.Controls.Add(Th.B("✓ Handled", () => OnHandled(h)));
            btns.Controls.Add(Th.B("💾 Save as…", () => MailFile.SaveAs(h, FindForm())));
            btns.Controls.Add(Th.B("⧉ Copy as file", () => MailFile.Copy(h, FindForm())));
            card.Controls.Add(btns);
        }
        else
        {
            void Hook(Control c) { c.Cursor = Cursors.Hand; c.Click += (_, _) => Select(h); foreach (Control k in c.Controls) Hook(k); }
            Hook(card);
        }
        // Right-click: the usual options for this email.
        EmailMenu.Hook(card, h, pt => { if (Compact) Select(h); OpenMenu(h, pt); return true; });
        return card;
    }

    static string Short(string s, int max = 2)
    {
        s ??= "";
        var parts = s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length <= max ? string.Join(", ", parts) : string.Join(", ", parts.Take(max)) + $" +{parts.Length - max}";
    }
}

/// <summary>💾 Save as… and ⧉ Copy as file: the email as an Outlook .msg file, named date · sender · subject.</summary>
static class MailFile
{
    public static string Name(MailHit h)
    {
        var who = h.Sent ? "Me" : (h.FromName ?? "");
        var subj = System.Text.RegularExpressions.Regex.Replace(h.Subject ?? "", @"^\s*((re|fw|fwd)\s*:\s*)+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var name = $"{h.Date:yyyy-MM-dd} {who} - {subj}";
        name = System.Text.RegularExpressions.Regex.Replace(name, @"[\\/:*?""<>|\r\n\t]+", " ");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s{2,}", " ").Trim().TrimEnd('.');
        return name.Length > 150 ? name[..150].Trim() : name;
    }

    public static void SaveAs(MailHit h, IWin32Window owner)
    {
        try
        {
            using var d = new SaveFileDialog { Title = "Save the email", Filter = "Outlook email with attachments (*.msg)|*.msg", FileName = Name(h) + ".msg", AddExtension = true, DefaultExt = "msg", OverwritePrompt = true };
            var last = Store.Str("win_msg_dir");
            if (last.Length > 0 && Directory.Exists(last)) d.InitialDirectory = last;
            if (d.ShowDialog(owner) != DialogResult.OK) return;
            Outlook.SaveMsg(h.Entry, h.StoreId, d.FileName);
            Store.Set("win_msg_dir", Path.GetDirectoryName(d.FileName) ?? "");
            Work.LogMail(h, "💾 Saved as " + Path.GetFileName(d.FileName));
            Program.Balloon("Nova Enhance", "✓ Email saved: " + Path.GetFileName(d.FileName));
        }
        catch (Exception e) { MessageBox.Show(owner, "Could not save the email: " + e.Message + "\n\n(Outlook needs to be open.)", "Nova Enhance"); Log.Error("save msg", e); }
    }

    public static void Copy(MailHit h, IWin32Window owner)
    {
        try
        {
            var dir = Path.Combine(Paths.Cache, "copied emails", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Name(h) + ".msg");
            Outlook.SaveMsg(h.Entry, h.StoreId, file);
            var list = new System.Collections.Specialized.StringCollection { file };
            Clipboard.SetFileDropList(list);
            Program.Balloon("Nova Enhance", "✓ Copied — open any folder and press Ctrl+V to paste the email there");
            // Old copies are cleaned after a week.
            try { foreach (var old in Directory.GetDirectories(Path.Combine(Paths.Cache, "copied emails")).Where(x => Directory.GetCreationTime(x) < DateTime.Now.AddDays(-7))) Directory.Delete(old, true); } catch { }
        }
        catch (Exception e) { MessageBox.Show(owner, "Could not copy the email: " + e.Message + "\n\n(Outlook needs to be open.)", "Nova Enhance"); Log.Error("copy msg", e); }
    }
}

/// <summary>The email on the right of a wide Ask Nova window: who, attachments, ✦ summary, full text, and the buttons.</summary>
class ReadPane : Panel
{
    readonly Stack s, textView;
    readonly Panel body;
    Microsoft.Web.WebView2.WinForms.WebView2 web;
    Button tabText, tabPreview, pics;
    Label previewNote;
    bool preview = true, allowRemote;
    public Action<MailHit> OnSummary, OnDraft, OnHandled;
    public MailHit Mail { get; private set; }
    Label aiBox;

    public ReadPane()
    {
        BackColor = Th.Card; Padding = new Padding(4);
        body = new Panel { Dock = DockStyle.Fill, BackColor = Th.Card };
        textView = new Stack { BackColor = Th.Card, Padding = new Padding(16, 6, 16, 12) };
        body.Controls.Add(textView);
        s = new Stack { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Th.Card, Padding = new Padding(16, 12, 16, 0) };
        Controls.Add(body);
        Controls.Add(s);
        Empty();
    }

    void Empty()
    {
        s.Controls.Clear();
        textView.Controls.Clear();
        if (web != null) web.Visible = false;
        textView.Visible = true;
        s.Add(Th.Wrap("Click an email on the left to read it here. ↑ ↓ move through the list.", 10, Th.Muted));
    }

    public void ShowAi(string text)
    {
        if (aiBox == null || aiBox.IsDisposed) return;
        aiBox.Text = text;
        aiBox.Visible = true;
    }

    public void Show(MailHit h)
    {
        Mail = h;
        allowRemote = false;
        s.SuspendLayout();
        foreach (Control c in s.Controls) c.Dispose();
        s.Controls.Clear();
        foreach (Control c in textView.Controls) c.Dispose();
        textView.Controls.Clear();
        if (h == null) { Empty(); s.ResumeLayout(); return; }
        var top = s.Add(Th.Row());
        top.Controls.Add(Th.L(string.IsNullOrWhiteSpace(h.Subject) ? "(no subject)" : h.Subject, 13, Th.Text, true));
        if (h.MaybeAnswered.Length > 0) s.Add(Th.Wrap("↩ Probably answered: " + h.MaybeAnswered, 9.5f, Th.AccentText));
        else if (!h.Sent && h.Status is "needs" or "week") top.Controls.Add(Th.L($"  waiting {h.DaysWaiting} day{(h.DaysWaiting == 1 ? "" : "s")}", 9.5f, Th.Warn, true));
        var hdr = h.Sent ? $"From: you\nTo: {h.To}" : $"From: {h.FromName}  <{h.FromAddr}>\nTo: {h.To}";
        if (h.Cc.Length > 0) hdr += "\nCc: " + h.Cc;
        hdr += $"\n{h.Date:dddd dd MMM yyyy HH:mm}" + (h.Earlier > 0 ? $"   ·   +{h.Earlier} earlier email{(h.Earlier == 1 ? "" : "s")} in this topic" : "");
        s.Add(Th.Wrap(hdr, 9.5f, Color.FromArgb(0xC9, 0xCE, 0xD8)));
        if (h.AttNames.Length > 0)
        {
            var at = s.Add(Th.Row());
            foreach (var a in h.AttNames.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = a;
                var chip = Th.Chip("📎 " + name + "   👁", false, () => MailPreview.PreviewAttachment(h, name, FindForm()));
                chip.Font = Th.F(9);
                new ToolTip().SetToolTip(chip, "👁 Preview in Nova");
                at.Controls.Add(chip);
                var sv = Th.Chip("💾", false, () => MailPreview.SaveAttachmentAs(h, name, FindForm()));
                sv.Font = Th.F(9); sv.Margin = new Padding(0, 0, 10, 0);
                new ToolTip().SetToolTip(sv, "💾 Save this attachment");
                at.Controls.Add(sv);
            }
        }
        aiBox = s.Add(Th.Wrap(h.Summary.Length > 0 ? "✦ " + h.Summary : "", 10, Th.Text));
        aiBox.BackColor = Th.Bg; aiBox.Padding = new Padding(10); aiBox.Margin = new Padding(0, 8, 0, 8);
        aiBox.Visible = h.Summary.Length > 0;
        var btns = s.Add(Th.Row());
        btns.Controls.Add(Th.B("Open in Outlook", () => { try { Outlook.Open(h.Entry, h.StoreId); } catch (Exception e) { MessageBox.Show("Could not open it in Outlook: " + e.Message, "Nova Enhance"); } }));
        btns.Controls.Add(Th.B("Summary", () => OnSummary?.Invoke(h)));
        btns.Controls.Add(Th.B(h.Sent ? "✦ Draft follow-up" : "✦ Draft reply", () => OnDraft?.Invoke(h), true));
        btns.Controls.Add(Th.B("✦ Ask about this email", () => Screens.MailAsk(h)));
        if (!h.Sent) btns.Controls.Add(Th.B("✓ Mark as handled", () => OnHandled?.Invoke(h)));
        btns.Controls.Add(Th.B("💾 Save as…", () => MailFile.SaveAs(h, FindForm())));
        btns.Controls.Add(Th.B("⧉ Copy as file", () => MailFile.Copy(h, FindForm())));

        // Text | 👁 Preview (Preview first).
        var tabs = s.Add(Th.Row(false));
        tabs.Margin = new Padding(0, 6, 0, 0);
        tabText = Th.Chip("Text", !preview, () => { preview = false; Switch(); });
        tabPreview = Th.Chip("👁 Preview", preview, () => { preview = true; Switch(); });
        tabs.Controls.Add(tabText); tabs.Controls.Add(tabPreview);
        pics = Th.Chip("🖼 Load pictures from the internet", false, () => { allowRemote = true; pics.Visible = false; web?.CoreWebView2?.Reload(); });
        pics.Font = Th.F(9); pics.Visible = false;
        tabs.Controls.Add(pics);
        previewNote = Th.L("", 9, Th.Muted); previewNote.Margin = new Padding(8, 6, 0, 0);
        tabs.Controls.Add(previewNote);

        var text = (h.Body ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length > 20000) text = text[..20000] + "\n…";
        textView.Add(Th.Wrap(text.Length > 0 ? text : "(no text)", 10.5f, Th.Text));
        // Right-click on the header: the same menu as the email cards.
        foreach (Control c in s.Controls) if (c is not Button) EmailMenu.Hook(c, h, pt => { EmailMenu.Show(h, this, pt, OnSummary, OnDraft, OnHandled, _ => { }); return true; });
        s.ResumeLayout();
        textView.AutoScrollPosition = new Point(0, 0);
        Switch();
    }

    async void Switch()
    {
        Th.SetChip(tabText, !preview); Th.SetChip(tabPreview, preview);
        var h = Mail;
        if (!preview || h == null) { if (web != null) web.Visible = false; textView.Visible = true; return; }
        try
        {
            previewNote.Text = "Opening the formatted email…";
            var file = await MailPreview.Render(h);
            if (h != Mail || !preview) return;
            if (file == null) { if (web != null) web.Visible = false; previewNote.Text = "Open Outlook to see the formatted email — here is the text."; preview = false; Th.SetChip(tabText, true); Th.SetChip(tabPreview, false); textView.Visible = true; return; }
            await EnsureWeb();
            if (web == null) { textView.Visible = true; return; }
            web.Visible = true; textView.Visible = false; web.BringToFront();
            web.CoreWebView2.Navigate(new Uri(file).AbsoluteUri);
            previewNote.Text = "";
        }
        catch (Exception e) { previewNote.Text = "⚠ " + e.Message; textView.Visible = true; Log.Error("mail preview", e); }
    }

    async Task EnsureWeb()
    {
        if (web != null) return;
        var w = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White, Visible = false };
        body.Controls.Add(w);
        try { await w.EnsureCoreWebView2Async(await HubForm.Env()); }
        catch (Exception e) { body.Controls.Remove(w); w.Dispose(); previewNote.Text = "⚠ Preview needs Microsoft Edge WebView2: " + e.Message; return; }
        var core = w.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        // Like Outlook: pictures from the internet stay blocked until you allow them for this email.
        core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Image);
        core.WebResourceRequested += (_, e) =>
        {
            if (allowRemote || !e.Request.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
            e.Response = HubForm.Current.CreateWebResourceResponse(null, 403, "Blocked", "");
            BeginInvoke(new Action(() => { if (pics != null && !pics.IsDisposed) pics.Visible = true; }));
        };
        // Links open in your normal browser.
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) Shell.Open(e.Uri);
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; if ((e.Uri ?? "").StartsWith("http", StringComparison.OrdinalIgnoreCase)) Shell.Open(e.Uri); };
        web = w;
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

/// <summary>The status circle on each email card: red overdue · orange this week · grey FYI / answered — drawn, with a dark ring.</summary>
class Dot : Control
{
    readonly Color color;
    public Dot(Color c)
    {
        color = c;
        Size = new Size(18, 18); Margin = new Padding(0, 3, 8, 0);
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var b = new SolidBrush(color);
        using var ring = new Pen(Color.FromArgb(0x0E, 0x11, 0x17), 2f);
        var r = new Rectangle(1, 1, Width - 3, Height - 3);
        e.Graphics.FillEllipse(b, r);
        e.Graphics.DrawEllipse(ring, r);
    }
}

/// <summary>Ctrl+Shift+F: ask about your email in your own words.</summary>
class AskForm : NovaForm
{
    readonly Stack stack, listHost;
    readonly SplitContainer split;
    readonly ReadPane pane;
    readonly System.Windows.Forms.Timer layoutTimer;
    bool wide;
    int lastWidth;
    MailHit lastHandled;
    Button undoBtn;
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
    // ✦ Prompts
    readonly TextBox instr;
    readonly FlowLayoutPanel planBox, resultRow;
    readonly Label resultLbl, countBar;
    readonly LinkLabel hiddenLink, moreLink;
    readonly Button levelA, levelB, myPrompts;
    string level = Prompts.Level;
    PromptPlan plan;
    List<MailHit> rawHits = new();
    List<(string Why, MailHit Hit)> hidden = new();
    bool showHidden;
    string lastIntent = "", lastInstr = "", lastExport;
    DateTime lastCheck = DateTime.Now;
    readonly System.Windows.Forms.Timer liveTimer;
    bool rechecking;

    public AskForm()
    {
        Text = "Ask Nova · your email";
        Width = Math.Max(820, Store.Int("win_ask_w", 820)); Height = Math.Max(600, Store.Int("win_ask_h", 760));
        MinimumSize = new Size(620, 500);
        KeyPreview = true;
        // Top: question and status. Below: the list — and, in a wide window, the reading pane on the right.
        stack = new Stack { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 12, 16, 4) };
        split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Th.Bg, Panel2Collapsed = true, SplitterWidth = 6, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.BackColor = split.Panel2.BackColor = Th.Bg;
        listHost = new Stack { Padding = new Padding(16, 4, 8, 8) };
        split.Panel1.Controls.Add(listHost);
        pane = new ReadPane { Dock = DockStyle.Fill, Margin = new Padding(0) };
        split.Panel2.Padding = new Padding(0, 4, 12, 12);
        split.Panel2.Controls.Add(pane);
        Controls.Add(split);
        Controls.Add(stack);
        Controls.Add(new IndexLine { Dock = DockStyle.Bottom });

        var head = stack.Add(Th.Row(false));
        head.Controls.Add(Th.L("🔍 Ask Nova", 14, Th.Text, true));
        head.Controls.Add(Th.L("about your email", 10, Th.Muted));
        var fbtn = Th.B("📁 Search inside a folder instead", () => Screens.FolderSearch(null));
        fbtn.Margin = new Padding(16, 2, 0, 0);
        head.Controls.Add(fbtn);

        var qrow = stack.Add(new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = false, Height = 40, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 6) }, true);
        qrow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        qrow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        qrow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        question = Th.Prose(Th.Box(false));
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

        // ✎ More instructions for my AI · filter level · ⭐ My prompts
        var prow = stack.Add(Th.Row(false));
        moreLink = new LinkLabel { Text = Prompts.ShowMore ? "✎ More instructions for my AI ▴" : "✎ More instructions for my AI ▾", AutoSize = true, LinkColor = Th.AccentText, ActiveLinkColor = Th.Accent, Font = Th.F(9.5f, true), Margin = new Padding(0, 7, 14, 0) };
        prow.Controls.Add(moreLink);
        prow.Controls.Add(Th.L("Filter:", 9, Th.Muted));
        levelA = Th.Chip("A · PC rules", level == "A", () => SetLevel("A"));
        levelB = Th.Chip("B · + AI check", level != "A", () => SetLevel("B"));
        levelA.Font = levelB.Font = Th.F(9);
        prow.Controls.Add(levelA); prow.Controls.Add(levelB);
        myPrompts = Th.B("⭐ My prompts ▾", null);
        myPrompts.Click += (_, _) => Prompts.Menu(myPrompts, "ask", UsePrompt);
        myPrompts.Margin = new Padding(14, 0, 0, 0);
        prow.Controls.Add(myPrompts);
        instr = stack.Add(Th.Prose(Th.Box(true, 3)), true);
        instr.PlaceholderText = "Optional — e.g. Exclude emails that start with \"proceed\" · ignore reminders from Eric · only SLV and Nouran projects · keep only where the contractor asks for my approval · group by project · export to Excel   (Ctrl+Enter = Ask)";
        instr.Visible = Prompts.ShowMore;
        instr.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Run(question.Text, false); } };
        moreLink.LinkClicked += (_, _) =>
        {
            instr.Visible = !instr.Visible;
            moreLink.Text = instr.Visible ? "✎ More instructions for my AI ▴" : "✎ More instructions for my AI ▾";
            if (instr.Visible) instr.Focus();
            stack.FitAll();
        };

        chips = stack.Add(Th.Row());
        idx = stack.Add(Th.Wrap("", 8.5f));

        examples = stack.Add(Th.Row());
        examples.FlowDirection = FlowDirection.TopDown;
        examples.WrapContents = false;
        examples.Controls.Add(Th.Section("Try asking"));
        foreach (var ex in new[]
                 {
                     "Which emails addressed to me this week are still awaiting my reply?",
                     "Emails from the consultant about the façade drawings last month, not answered",
                     "Find the RFI where structural asked about the slab openings",
                     "Which of my emails got no answer after 3 days?"
                 })
        {
            var b = Th.B("“" + ex + "”", () => { question.Text = ex; Run(ex, false); });
            b.Font = Th.F(9.5f);
            b.ForeColor = Color.FromArgb(0xC9, 0xCE, 0xD8);
            b.BackColor = Th.Card;
            examples.Controls.Add(b);
        }

        understood = stack.Add(Th.Wrap("", 9.5f, Th.Text));
        planBox = stack.Add(new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), Visible = false });
        info = stack.Add(Th.Wrap("", 9, Th.AccentText));
        resultRow = stack.Add(Th.Row());
        resultRow.Visible = false;
        resultLbl = Th.L("", 9.5f, Th.Text);
        resultLbl.Margin = new Padding(0, 7, 8, 0);
        hiddenLink = new LinkLabel { AutoSize = true, LinkColor = Th.Muted, ActiveLinkColor = Th.Accent, Font = Th.F(9), Margin = new Padding(0, 7, 8, 0), Visible = false };
        hiddenLink.LinkClicked += (_, _) => { showHidden = !showHidden; ShowList(); UpdateHiddenLink(); };
        // ⏳ The remaining number: fixed at the top, updated on every change.
        countBar = stack.Add(Th.Wrap("", 10.5f, Th.Text), true);
        countBar.BackColor = Color.FromArgb(0x1A, 0x1F, 0x2C); countBar.Padding = new Padding(10, 6, 10, 6); countBar.Font = Th.F(10.5f, true); countBar.Visible = false;
        message = stack.Add(Th.Wrap("", 10, Th.Text));
        message.BackColor = Th.Card;
        message.Padding = new Padding(8);
        message.Visible = false;
        list = listHost.Add(new MailList());
        list.OnSummary = Summary;
        list.OnDraft = Draft;
        list.OnHandled = MarkHandled;
        list.OnSelect = h => pane.Show(h);
        pane.OnSummary = Summary;
        pane.OnDraft = Draft;
        pane.OnHandled = MarkHandled;

        bottom = listHost.Add(Th.Row());
        follow = Th.Prose(Th.Box(false));
        follow.Width = 360;
        follow.PlaceholderText = "Ask a follow-up… e.g. only with attachments";
        bottom.Controls.Add(follow);
        bottom.Controls.Add(Th.B("Ask", () => Run(follow.Text, true)));
        bottom.Controls.Add(Th.B("📌 Save as task", () =>
        {
            if (hits.Count == 0) return;
            var intent = lastFilter?["intent"]?.ToString() ?? "search";
            Screens.Saved(NovaEnhance.Work.FromMail(lastQuestion, lastFilter, intent, hits));
        }, true));
        bottom.Controls.Add(Th.B("⇩ Export list", Export));
        bottom.Controls.Add(Th.B("☀ Brief", Screens.Brief));
        bottom.Controls.Add(Th.B("📁 Search a folder", () => Screens.FolderSearch(null)));
        bottom.Controls.Add(Th.B("⚙", () => Screens.Settings("Email & AI")));
        undoBtn = Th.B("↩ Undo handled", UndoHandled);
        undoBtn.Visible = false;
        bottom.Controls.Add(undoBtn);
        bottom.Visible = false;
        listHost.Add(Th.Signature());

        layoutTimer = new System.Windows.Forms.Timer { Interval = 150 };
        layoutTimer.Tick += (_, _) => { layoutTimer.Stop(); Relayout(false); };
        Resize += (_, _) => { layoutTimer.Stop(); layoutTimer.Start(); };
        ResizeEnd += (_, _) => { if (WindowState == FormWindowState.Normal) { Store.SetInt("win_ask_w", Width); Store.SetInt("win_ask_h", Height); } };
        split.SplitterMoved += (_, _) => { if (wide) { Store.SetInt("win_ask_split", split.SplitterDistance); Relayout(true); } };

        question.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Run(question.Text, false); } };
        follow.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Run(follow.Text, true); } };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Hide();
            else if ((e.KeyCode == Keys.Apps || (e.Shift && e.KeyCode == Keys.F10)) && !question.Focused && !follow.Focused && !instr.Focused) { list.ShowMenu(); e.Handled = true; }
            else if (wide && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) && !question.Focused && !follow.Focused)
            { list.MoveBy(e.KeyCode == Keys.Down ? 1 : -1); e.Handled = true; }
        };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        VisibleChanged += (_, _) => { if (Visible) DrawChips(); };
        Shown += (_, _) => Relayout(true);
        idxTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        idxTimer.Tick += (_, _) => { if (Visible) idx.Text = "Index: " + MailIndex.Status; };
        idxTimer.Start();
        liveTimer = new System.Windows.Forms.Timer { Interval = 180_000 };
        liveTimer.Tick += (_, _) => { if (Visible) Recheck(); };
        liveTimer.Start();
        Activated += (_, _) => { if (DateTime.Now - lastCheck > TimeSpan.FromSeconds(45)) Recheck(); };
        DrawChips();
    }

    Conversation conv;

    /// <summary>🕘 Saves the question and the list it found (each email is a source you can open later).</summary>
    void SaveConv(bool followUp, string q, string explain)
    {
        if (!followUp) conv = null;
        var main = hits.Where(h => h.MaybeAnswered.Length == 0).Take(40).ToList();
        var sb = new System.Text.StringBuilder($"Understood as: {explain}\n{main.Count} email{(main.Count == 1 ? "" : "s")}:\n");
        var src = new List<ConvSrc>();
        for (int i = 0; i < main.Count; i++)
        {
            var h = main[i];
            sb.Append($"• {h.Subject} — {(h.Sent ? "you" : h.FromName)} · {h.Date:dd MMM yyyy}{(h.Status is "needs" or "week" ? $" · waiting {h.DaysWaiting} days" : "")} [E{i + 1}]\n");
            src.Add(new ConvSrc { Id = "E" + (i + 1), Title = h.Subject, Entry = h.Entry, Store = h.StoreId });
        }
        if (lastExport != null) sb.Append("\n⇩ " + lastExport);
        Conversations.Add(ref conv, "ask", Prompts.Split(q).Main, "", q, sb.ToString(), src);
    }

    /// <summary>Runs a question from elsewhere (🔍 Find related emails).</summary>
    public void AskNow(string q) { question.Text = q; Run(q, false); }

    void SetLevel(string l)
    {
        level = l;
        Th.SetChip(levelA, l == "A"); Th.SetChip(levelB, l != "A");
        if (plan != null && rawHits.Count > 0) _ = ApplyPlan(false);
    }

    void UsePrompt(SavedPrompt p)
    {
        question.Text = p.Question;
        instr.Text = p.Instructions;
        if (p.Instructions.Length > 0 && !instr.Visible) { instr.Visible = true; moreLink.Text = "✎ More instructions for my AI ▴"; stack.FitAll(); }
        SetLevel(p.Level);
        Run(p.Question, false);
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
        // Smart search: typos are corrected first (↩ under the box searches exactly as typed).
        var box = followUp ? follow : question;
        if (q == box.Text) { Speller.FixBeforeSearch(box, () => Run(box.Text, followUp)); q = box.Text; }
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
        // ✦ Prompts: the question goes to the search; "exclude … / only … / group … / export …" become your instructions.
        var (mainQ, extraQ) = Prompts.Split(q);
        var instructions = ((followUp ? lastInstr : instr.Text) + "\n" + extraQ).Trim();
        try
        {
            var ans = await Assistant.Ask(mainQ, followUp ? lastFilter : null, hits, my.Token);
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
            lastIntent = ans.Intent;
            lastInstr = instructions;
            rawHits = ans.Hits;
            hits = ans.Hits;
            hidden = new(); showHidden = false; lastExport = null;
            understood.Text = "Understood as: " + ans.Explain;
            lastCheck = DateTime.Now;
            if (instructions.Length > 0)
            {
                info.Text = "✦ Reading your instructions…";
                plan = await Prompts.Understand(mainQ, instructions, my.Token);
                if (my.IsCancellationRequested) return;
                await ApplyPlan(true);
                bottom.Visible = true;
                follow.Text = "";
                History.Add("ask", Ai.Active()?.Id ?? "", "", "ask-mail", q + " ✎ " + instructions, $"{hits.Count} results · {ans.Explain}");
                SaveConv(followUp, q + (instructions.Length > 0 ? "\n✎ " + instructions : ""), ans.Explain);
                return;
            }
            plan = null;
            planBox.Visible = resultRow.Visible = false;
            list.GroupKey = null; list.HiddenShown = null;
            var mainCount = hits.Count(h => h.MaybeAnswered.Length == 0);
            var maybeCount = hits.Count - mainCount;
            var count = hits.Count == 0 ? "No emails found" : $"{mainCount} email{(mainCount == 1 ? "" : "s")} found" + (maybeCount > 0 ? $" · {maybeCount} probably answered (grey, at the bottom)" : "");
            info.Text = $"{count} in {ans.Seconds:0.0} s" + (MailIndex.Count() == 0 ? " · the index is still empty — open Outlook and wait a few minutes" : "");
            if (ans.Intent == "outstanding" && !MailIndex.Complete)
                info.Text += Outstanding.LiveChecked
                    ? " · Index still building — your replies were checked live in Outlook"
                    : " · ⚠ Index still building and Outlook is closed — some of these may already be answered";
            ShowList();
            bottom.Visible = true;
            follow.Text = "";
            History.Add("ask", Ai.Active()?.Id ?? "", "", "ask-mail", q, $"{hits.Count} results · {ans.Explain}");
            SaveConv(followUp, q, ans.Explain);
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

    /// <summary>Your instructions on the list: rules on this PC, the AI check (level B), grouping, and the file.</summary>
    async Task ApplyPlan(bool export)
    {
        if (plan == null) return;
        var ct = cts?.Token ?? CancellationToken.None;
        var main = rawHits.Where(h => h.MaybeAnswered.Length == 0).ToList();
        var maybe = rawHits.Where(h => h.MaybeAnswered.Length > 0).ToList();
        var (kept, hid) = Prompts.Apply(main, plan);
        var afterRules = kept.Count;
        int read = 0; var note = plan.Note;
        if (level != "A" && plan.AiCheck.Length > 0)
        {
            info.Text = $"✦ {Ai.LabelOf(Ai.Current()) ?? "Your AI"} is checking {Math.Min(kept.Count, Prompts.MaxAi)} emails: “{plan.AiCheck}”…";
            try
            {
                var r = await Prompts.AiFilter(this, kept, plan.AiCheck, ct);
                kept = r.Kept; hid.AddRange(r.Hidden); read = r.Read;
                if (r.Note.Length > 0) note = (note.Length > 0 ? note + " · " : "") + r.Note;
            }
            catch (OperationCanceledException) { return; }
        }
        hidden = hid;
        hits = kept.Concat(Prompts.Apply(maybe, plan).Kept).ToList();
        list.GroupKey = plan.Group.Length > 0 ? (Func<MailHit, string>)(h => Prompts.GroupKey(h, plan)) : null;
        ShowList();
        var label = Ai.LabelOf(Ai.Current()) ?? "AI";
        resultLbl.Text = $"✓ {kept.Count} email{(kept.Count == 1 ? "" : "s")} · {main.Count} found → {afterRules} after your rules" +
                         (level != "A" && plan.AiCheck.Length > 0 ? $" → {kept.Count} after the AI check ({label} · {read} read)" : "") + (note.Length > 0 ? " · " + note : "");
        info.Text = "";
        if (export && plan.Export.Length > 0)
        {
            try { lastExport = Prompts.Export(kept, plan, lastIntent == "outstanding" ? "Waiting for my reply" : Prompts.Split(lastQuestion).Main, lastQuestion, lastInstr); }
            catch (Exception e) { lastExport = null; note = "⚠ file not saved: " + e.Message; }
        }
        resultRow.Controls.Clear();
        resultRow.Controls.Add(resultLbl);
        if (lastExport != null)
        {
            var fl = Th.L("⇩ " + Path.GetFileName(lastExport) + " saved", 9.5f, Color.FromArgb(0xFF, 0xD2, 0x7A), true);
            fl.Margin = new Padding(0, 7, 8, 0);
            resultRow.Controls.Add(fl);
            resultRow.Controls.Add(Th.B("Open file", () => Shell.Open(lastExport), true));
            resultRow.Controls.Add(Th.B("📁 Folder", () => Shell.Select(lastExport)));
        }
        else resultRow.Controls.Add(Th.B("⇩ Export (" + (plan.Export.Length > 0 ? plan.Export : Prompts.DefaultExport) + ")", () =>
        {
            var p2 = plan;
            if (p2.Export.Length == 0) p2.Export = Prompts.DefaultExport;
            lastExport = Prompts.Export(hits.Where(h => h.MaybeAnswered.Length == 0).ToList(), p2, lastIntent == "outstanding" ? "Waiting for my reply" : Prompts.Split(lastQuestion).Main, lastQuestion, lastInstr);
            Shell.Open(lastExport);
        }));
        resultRow.Controls.Add(Th.B("⭐ Save this prompt", () =>
        {
            var nm = Prompts.Split(lastQuestion).Main;
            if (nm.Length > 40) nm = nm[..40];
            var sp = Prompts.AskSave(this, new SavedPrompt { Name = nm, Question = lastQuestion, Instructions = instr.Text.Trim(), Level = level, Where = "ask" });
            if (sp != null) info.Text = $"⭐ Saved “{sp.Name}” · {Prompts.RunText(sp)} — find it under ⭐ My prompts";
        }));
        resultRow.Controls.Add(hiddenLink);
        UpdateHiddenLink();
        resultRow.Visible = true;
        DrawPlan();
        stack.FitAll();
    }

    void UpdateHiddenLink()
    {
        hiddenLink.Visible = hidden.Count > 0;
        hiddenLink.Text = "Hidden by your instructions: " + Prompts.Hidden(hidden) + (showHidden ? " · hide them" : " · show them");
    }

    /// <summary>✦ Nova understood: one chip per rule — ✕ removes it and the list updates at once.</summary>
    void DrawPlan()
    {
        planBox.SuspendLayout();
        foreach (Control c in planBox.Controls) c.Dispose();
        planBox.Controls.Clear();
        planBox.MaximumSize = new Size(Math.Max(400, stack.ClientSize.Width - 40), 0);
        if (plan == null || !Prompts.ShowChips) { planBox.Visible = false; planBox.ResumeLayout(); return; }
        planBox.Controls.Add(Th.L("✦ NOVA UNDERSTOOD", 8.5f, Th.Muted, true));
        Color pc = Color.FromArgb(0xB9, 0xB0, 0xFF), ai = Color.FromArgb(0x8F, 0xD6, 0xB4), outc = Color.FromArgb(0xFF, 0xD2, 0x7A);
        void Chip(string text, Color c, Action remove)
        {
            var b = Th.Chip(text + "  ✕", false, async () => { remove(); await ApplyPlan(false); });
            b.Font = Th.F(9); b.ForeColor = c;
            planBox.Controls.Add(b);
        }
        foreach (var x in plan.ExStarts.ToList()) Chip($"⛔ starts with “{x}”", pc, () => plan.ExStarts.Remove(x));
        foreach (var x in plan.ExContains.ToList()) Chip($"⛔ mentions “{x}”", pc, () => plan.ExContains.Remove(x));
        foreach (var x in plan.ExFrom.ToList()) Chip($"⛔ from {x}", pc, () => plan.ExFrom.Remove(x));
        foreach (var x in plan.OnlyFrom.ToList()) Chip($"👤 only from {x}", pc, () => plan.OnlyFrom.Remove(x));
        foreach (var x in plan.OnlyWords.ToList()) Chip($"🏷 only {x}", pc, () => plan.OnlyWords.Remove(x));
        if (plan.NeedAttach) Chip("📎 with attachments", pc, () => plan.NeedAttach = false);
        if (plan.MinDays > 0) Chip($"⏳ waiting {plan.MinDays}+ days", pc, () => plan.MinDays = 0);
        if (plan.AiCheck.Length > 0) Chip((level == "A" ? "✦ AI check (off — level A): " : "✦ AI check: ") + plan.AiCheck, ai, () => plan.AiCheck = "");
        if (plan.Group.Length > 0) Chip("▦ group by " + plan.Group, outc, () => plan.Group = "");
        if (plan.Export.Length > 0) Chip("⇩ " + (plan.Export == "xlsx" ? "Excel" : plan.Export == "docx" ? "Word" : "CSV"), outc, () => plan.Export = "");
        var add = Th.Chip("＋ add", false, () => { if (!instr.Visible) { instr.Visible = true; stack.FitAll(); } instr.Focus(); instr.SelectionStart = instr.TextLength; });
        add.Font = Th.F(9); add.ForeColor = Th.Muted;
        planBox.Controls.Add(add);
        planBox.Visible = true;
        planBox.ResumeLayout();
    }

    /// <summary>⏳ still waiting · 🔴 overdue · 🟠 this week · ⚪ FYI · ✓ handled today · ↻ checked — always at the top.</summary>
    void UpdateCounts()
    {
        var main = hits.Where(h => h.MaybeAnswered.Length == 0).ToList();
        var maybe = hits.Count - main.Count;
        if (lastQuestion.Length == 0) { countBar.Visible = false; return; }
        countBar.Text = lastIntent == "outstanding"
            ? $"⏳ {main.Count} still waiting · 🔴 {main.Count(h => h.Status == "needs")} overdue · 🟠 {main.Count(h => h.Status == "week")} this week · ⚪ {main.Count(h => h.Status == "fyi")} FYI" +
              (maybe > 0 ? $" · {maybe} probably answered" : "") + $" · ✓ {Outstanding.HandledToday()} handled today · ↻ checked {lastCheck:HH:mm}"
            : $"✉ {main.Count} email{(main.Count == 1 ? "" : "s")}" + (maybe > 0 ? $" · {maybe} probably answered" : "") + (hidden.Count > 0 ? $" · {hidden.Count} hidden by your instructions" : "");
        countBar.Visible = true;
    }

    /// <summary>Every 3 minutes (and when you come back to the window): emails you answered in Outlook leave the list.</summary>
    async void Recheck()
    {
        if (rechecking || lastIntent != "outstanding" || hits.Count == 0 || Outlook.Running() == null) return;
        rechecking = true;
        try
        {
            var before = hits.Where(h => h.MaybeAnswered.Length == 0).ToList();
            var after = await Task.Run(() => Outstanding.Check(before.ToList()));
            var still = after.Where(h => h.MaybeAnswered.Length == 0).ToHashSet();
            var gone = before.Where(h => !still.Contains(h)).ToList();
            foreach (var h in gone) if (h.MaybeAnswered.Length == 0) h.MaybeAnswered = Outstanding.IsHandled(h) ? "marked as handled" : "you replied (checked live in Outlook)";
            lastCheck = DateTime.Now;
            if (gone.Count > 0)
            {
                ShowList();
                info.Text = $"✓ {gone.Count} answered in Outlook — moved to “Probably answered” ({DateTime.Now:HH:mm})";
            }
            else UpdateCounts();
        }
        catch (Exception e) { Log.Warn("recheck", e.Message); }
        finally { rechecking = false; }
    }

    /// <summary>Medium window: cards stretch. Wide (≥ 1300 px) or maximised: list on the left, the email on the right.</summary>
    void Relayout(bool force)
    {
        bool w = ClientSize.Width >= 1300;
        if (w != wide)
        {
            wide = w;
            split.Panel2Collapsed = !wide;
            if (wide)
            {
                var d = Store.Int("win_ask_split", 560);
                try { split.SplitterDistance = Math.Clamp(d, 380, Math.Max(400, split.Width - 500)); } catch { }
            }
            force = true;
        }
        var width = ListWidth();
        if (!force && Math.Abs(width - lastWidth) < 24) return;
        ShowList();
    }

    int ListWidth() => Math.Max(300, (wide ? split.Panel1.ClientSize.Width : listHost.ClientSize.Width) - listHost.Padding.Horizontal - 24);

    void ShowList()
    {
        list.Compact = wide;
        lastWidth = ListWidth();
        var sel = list.Selected;
        list.HiddenShown = showHidden && hidden.Count > 0 ? hidden : null;
        list.Show(hits, lastWidth);
        listHost.FitAll();
        UpdateCounts();
        if (wide)
        {
            if (sel != null && hits.Contains(sel)) list.Select(sel);
            else if (list.Order.Count > 0) list.Select(list.Order[0]);
            else pane.Show(null);
        }
    }

    /// <summary>Shows an AI result next to the email when the reading pane shows it, otherwise above the list.</summary>
    void Say(MailHit h, string text)
    {
        if (wide && pane.Mail == h) pane.ShowAi(text);
        else ShowMessage(text);
    }

    void MarkHandled(MailHit h)
    {
        Outstanding.MarkHandled(h, true);
        NovaEnhance.Work.LogMail(h, "✓ Handled");
        lastHandled = h;
        var i = list.Order.IndexOf(h);
        hits.Remove(h); rawHits.Remove(h);
        ShowList();
        if (wide && list.Order.Count > 0) list.Select(list.Order[Math.Clamp(i, 0, list.Order.Count - 1)]);
        info.Text = $"✓ “{h.Subject}” marked as handled — it won't show as waiting again (↩ Undo handled at the bottom).";
        undoBtn.Visible = true;
    }

    void UndoHandled()
    {
        if (lastHandled == null) return;
        Outstanding.MarkHandled(lastHandled, false);
        hits.Insert(0, lastHandled); rawHits.Insert(0, lastHandled);
        info.Text = $"↩ “{lastHandled.Subject}” is back in the list.";
        lastHandled = null;
        undoBtn.Visible = false;
        ShowList();
    }

    async void Summary(MailHit h)
    {
        if (!Store.AiSeesFull)
        {
            Say(h, wide && pane.Mail == h ? "An AI summary of the full email needs Settings → Email & AI → C or D. The full text is below, read on this PC only."
                                         : $"“{h.Subject}”\n\n{h.FirstLines(600)}\n\n(An AI summary needs Settings → Email & AI → C or D. This is the start of the email, read on this PC only.)");
            return;
        }
        Say(h, $"✦ Summarising “{h.Subject}”…");
        try { Say(h, (wide && pane.Mail == h ? "✦ Summary\n\n" : $"✦ {h.Subject}\n\n") + await Assistant.Summarize(h, CancellationToken.None)); }
        catch (Exception e) { Say(h, "⚠ " + e.Message); }
    }

    async void Draft(MailHit h)
    {
        if (!Store.AiSeesFull)
        {
            ShowMessage("✦ Draft reply needs Settings → Email & AI → C or D: the full email goes to the AI only when you tap Draft.");
            return;
        }
        var instr = Prompt.AskLong(this, "✦ Draft reply", "✎ Instructions for your AI (optional) — what to say, tone, length, language, who to copy…", "e.g. Agree with the GA, but ask them to coordinate the lift door finishes with ID spec 14 21 00 and resubmit by Thursday. Firm but polite, short.");
        if (instr == null) return;
        ShowMessage($"✦ Writing a reply to “{h.Subject}”…");
        try
        {
            var draft = await Assistant.Draft(h, instr, CancellationToken.None);
            try
            {
                Outlook.Reply(h.Entry, h.StoreId, draft, false);
                NovaEnhance.Work.LogMail(h, "✦ Draft reply opened");
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
    readonly Stack top, one, colA, colB, colC;
    readonly Label head, info, needsTitle, followTitle, fyiTitle, message, calTitle;
    readonly FlowLayoutPanel calBox;
    readonly MailList needs, follow, fyi;
    readonly SplitContainer split;
    readonly TableLayoutPanel cols;
    readonly ReadPane pane;
    readonly Control sign;
    readonly System.Windows.Forms.Timer layoutTimer;
    List<MailHit> lastNeeds = new(), lastFollow = new();
    List<Meeting> lastMeetings;
    bool busy;
    int mode = -1;   // 0 = one column · 1 = list + reading pane (1300 px +) · 2 = columns + reading pane (1800 px +)
    int lastW;

    public BriefForm()
    {
        Text = "Nova Enhance · Morning brief";
        Width = Math.Max(820, Store.Int("win_brief_w", 820)); Height = Math.Max(600, Store.Int("win_brief_h", 780));
        MinimumSize = new Size(620, 500);
        KeyPreview = true;

        // Top: greeting, counts and buttons. Below: one column, or the list + reading pane, or columns + reading pane.
        top = new Stack { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 12, 16, 4) };
        split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Th.Bg, Panel2Collapsed = true, SplitterWidth = 6, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.BackColor = split.Panel2.BackColor = Th.Bg;
        one = new Stack { Padding = new Padding(16, 4, 8, 8) };
        cols = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Th.Bg, Visible = false, Padding = new Padding(8, 0, 0, 0) };
        for (int i = 0; i < 3; i++) cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        cols.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        colA = new Stack { Padding = new Padding(8, 4, 8, 8), Margin = new Padding(0) };
        colB = new Stack { Padding = new Padding(8, 4, 8, 8), Margin = new Padding(0) };
        colC = new Stack { Padding = new Padding(8, 4, 8, 8), Margin = new Padding(0) };
        cols.Controls.Add(colA, 0, 0); cols.Controls.Add(colB, 1, 0); cols.Controls.Add(colC, 2, 0);
        split.Panel1.Controls.Add(one);
        split.Panel1.Controls.Add(cols);
        pane = new ReadPane { Dock = DockStyle.Fill };
        split.Panel2.Padding = new Padding(0, 4, 12, 12);
        split.Panel2.Controls.Add(pane);
        Controls.Add(split);
        Controls.Add(top);

        head = top.Add(Th.L("☀ Good morning", 15, Th.Text, true));
        info = top.Add(Th.Wrap("", 9.5f), true);
        var row = top.Add(Th.Row());
        row.Controls.Add(Th.B("↻ Refresh", Reload, true));
        row.Controls.Add(Th.B("📌 Save as task", () => { if (lastNeeds.Count > 0) Screens.Saved(NovaEnhance.Work.FromMail("What is waiting for my reply (morning brief)", null, "outstanding", lastNeeds, "brief")); }));
        row.Controls.Add(Th.B("📌 My Work", () => Screens.Work(null)));
        row.Controls.Add(Th.B("⇩ Export", Export));
        row.Controls.Add(Th.B("📁 Briefs folder", () => Shell.Open(Paths.Briefs)));
        row.Controls.Add(Th.B("🔍 Ask Nova", Screens.Ask));
        row.Controls.Add(Th.B("⚙ Rules", () => Screens.Settings("Email & AI")));
        message = top.Add(Th.Wrap("", 10, Th.Text), true);
        message.BackColor = Th.Card; message.Padding = new Padding(8); message.Visible = false;

        calTitle = Th.Section("📅 Today and tomorrow");
        calBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0) };
        needsTitle = Th.Section("Waiting for your reply");
        needs = new MailList();
        fyiTitle = Th.Section("⚪ FYI");
        fyi = new MailList();
        followTitle = Th.Section("Your emails with no answer");
        follow = new MailList();
        sign = Th.Signature();

        foreach (var l in new[] { needs, follow, fyi })
        {
            var me = l;
            l.OnSummary = Summary;
            l.OnDraft = Draft;
            l.OnHandled = Handled;
            l.OnSelect = h =>
            {
                foreach (var o in new[] { needs, follow, fyi }) if (o != me) o.Unselect();
                pane.Show(h);
            };
        }
        pane.OnSummary = Summary;
        pane.OnDraft = Draft;
        pane.OnHandled = Handled;

        layoutTimer = new System.Windows.Forms.Timer { Interval = 180 };
        layoutTimer.Tick += (_, _) => { layoutTimer.Stop(); Relayout(false); };
        Resize += (_, _) => { layoutTimer.Stop(); layoutTimer.Start(); };
        ResizeEnd += (_, _) => { if (WindowState == FormWindowState.Normal) { Store.SetInt("win_brief_w", Width); Store.SetInt("win_brief_h", Height); } };
        split.SplitterMoved += (_, _) => { if (mode > 0) { Store.SetInt("win_brief_pane", split.Panel2.Width); Relayout(true); } };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Hide();
            else if (e.KeyCode == Keys.Apps || (e.Shift && e.KeyCode == Keys.F10)) { (new[] { needs, fyi, follow }.FirstOrDefault(x => x.Selected != null) ?? needs).ShowMenu(); e.Handled = true; }
            else if (mode > 0 && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up))
            {
                var l = new[] { needs, fyi, follow }.FirstOrDefault(x => x.Selected != null) ?? needs;
                l.MoveBy(e.KeyCode == Keys.Down ? 1 : -1); e.Handled = true;
            }
        };
        Shown += (_, _) => Relayout(true);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        var live = new System.Windows.Forms.Timer { Interval = 180_000 };
        live.Tick += (_, _) => { if (Visible) Recheck(); };
        live.Start();
        Activated += (_, _) => { if (DateTime.Now - lastCheck > TimeSpan.FromSeconds(45)) Recheck(); };
        Relayout(true);
    }

    /// <summary>Wider window = more detail: reading pane from 1300 px, columns from 1800 px.</summary>
    void Relayout(bool force)
    {
        var w = ClientSize.Width;
        int m = w >= 1800 ? 2 : w >= 1300 ? 1 : 0;
        if (m != mode)
        {
            mode = m;
            force = true;
            split.Panel2Collapsed = mode == 0;
            if (mode > 0)
            {
                var pw = Math.Clamp(Store.Int("win_brief_pane", mode == 2 ? 620 : 640), 420, Math.Max(440, w / 2));
                try { split.SplitterDistance = Math.Max(300, split.Width - pw - split.SplitterWidth); } catch { }
            }
            // Put each section where it belongs for this layout.
            foreach (var c in new Control[] { calTitle, calBox, needsTitle, needs, fyiTitle, fyi, followTitle, follow, sign }) c.Parent?.Controls.Remove(c);
            if (mode == 2)
            {
                colA.Add(calTitle); colA.Add(calBox); colA.Add(followTitle); colA.Add(follow);
                colB.Add(needsTitle); colB.Add(needs);
                colC.Add(fyiTitle); colC.Add(fyi); colC.Add(sign);
            }
            else
            {
                one.Add(calTitle); one.Add(calBox); one.Add(needsTitle); one.Add(needs); one.Add(fyiTitle); one.Add(fyi); one.Add(followTitle); one.Add(follow); one.Add(sign);
            }
            one.Visible = mode != 2;
            cols.Visible = mode == 2;
        }
        top.FitAll();
        var lw = ListWidth(colB);
        if (!force && Math.Abs(lw - lastW) < 24) return;
        Draw();
    }

    int ListWidth(Stack col) => mode == 2 ? Math.Max(280, col.ClientSize.Width - col.Padding.Horizontal - 22) : Math.Max(300, one.ClientSize.Width - one.Padding.Horizontal - 22);

    /// <summary>Draws the lists for the current layout (no new search).</summary>
    DateTime lastCheck = DateTime.Now;
    bool rechecking;

    /// <summary>⏳ The numbers at the top, recounted on every change (handled, answered in Outlook, refresh).</summary>
    void Counts()
    {
        var n = lastNeeds.Where(h => h.MaybeAnswered.Length == 0).ToList();
        int red = n.Count(h => h.Status == "needs"), orange = n.Count(h => h.Status == "week"), fy = n.Count(h => h.Status == "fyi");
        var wk = NovaEnhance.Work.All();
        int wDue = wk.Count(t => t.DueToday), wOver = wk.Count(t => t.Overdue), wWait = wk.Count(t => t.Status == "waiting");
        info.Text = $"⏳ {n.Count} still waiting · 🔴 {red} overdue · 🟠 {orange} this week · ⚪ {fy} FYI · ↩ {lastFollow.Count} follow-ups · ✓ {Outstanding.HandledToday()} handled today · ↻ checked {lastCheck:HH:mm}\n" +
                    (wDue + wOver + wWait > 0 ? $"📌 My Work: {wDue} due today · {wOver} overdue · {wWait} waiting for others · " : "") +
                    $"{DateTime.Now:dddd dd MMMM} · last {Store.BriefDays} days · Index: {MailIndex.Status}";
        info.Font = Th.F(10, true);
        top.FitAll();
    }

    /// <summary>Every 3 minutes while the brief is open (and when you come back to it): answered emails leave the list.</summary>
    async void Recheck()
    {
        if (rechecking || busy || lastNeeds.Count == 0 || Outlook.Running() == null) return;
        rechecking = true;
        try
        {
            var before = lastNeeds.Where(h => h.MaybeAnswered.Length == 0).ToList();
            var after = await Task.Run(() => Outstanding.Check(before.ToList()));
            var still = after.Where(h => h.MaybeAnswered.Length == 0).ToHashSet();
            var gone = before.Where(h => !still.Contains(h)).ToList();
            lastCheck = DateTime.Now;
            if (gone.Count > 0)
            {
                foreach (var h in gone) lastNeeds.Remove(h);
                Draw();
                message.Visible = true;
                message.Text = $"✓ {gone.Count} answered in Outlook — removed from the list ({DateTime.Now:HH:mm})";
                top.FitAll();
            }
            else Counts();
        }
        catch (Exception e) { Log.Warn("brief recheck", e.Message); }
        finally { rechecking = false; }
    }

    void Draw()
    {
        var w = ListWidth(colB);
        lastW = w;
        if (lastNeeds.Count + lastFollow.Count > 0 || !busy) Counts();
        var compact = mode > 0;
        needs.Compact = follow.Compact = fyi.Compact = compact;
        var sel = new[] { needs, fyi, follow }.Select(l => l.Selected).FirstOrDefault(x => x != null);
        // FYI gets its own section when the window is wide; otherwise it stays in the main list as before.
        var main = mode == 0 ? lastNeeds : lastNeeds.Where(h => h.Status != "fyi").ToList();
        var fy = mode == 0 ? new List<MailHit>() : lastNeeds.Where(h => h.Status == "fyi").ToList();
        needsTitle.Text = mode == 0 ? $"WAITING FOR YOUR REPLY ({lastNeeds.Count})" : $"🔴🟠 WAITING FOR YOUR REPLY ({main.Count})";
        fyiTitle.Text = $"⚪ FYI — FOR YOUR INFORMATION ({fy.Count})";
        fyiTitle.Visible = fyi.Visible = mode > 0 && fy.Count > 0;
        followTitle.Text = Store.FollowUps ? $"↩ YOUR EMAILS WITH NO ANSWER AFTER {Store.FollowDays} DAYS ({lastFollow.Count})" : "FOLLOW-UPS ARE OFF (SETTINGS → EMAIL & AI)";
        needs.Show(main, w);
        fyi.Show(fy, mode == 2 ? ListWidth(colC) : w);
        follow.Show(lastFollow, mode == 2 ? ListWidth(colA) : w);
        if (lastMeetings != null || calBox.Controls.Count > 0) _ = DrawCalendar(lastNeeds, false);
        foreach (var s in new[] { one, colA, colB, colC }) s.FitAll();
        if (mode > 0)
        {
            var target = sel != null ? new[] { needs, fyi, follow }.FirstOrDefault(l => l.Order.Contains(sel)) : null;
            if (target != null) target.Select(sel);
            else if (needs.Order.Count > 0) needs.Select(needs.Order[0]);
            else if (fyi.Order.Count > 0) fyi.Select(fyi.Order[0]);
            else if (follow.Order.Count > 0) follow.Select(follow.Order[0]);
            else pane.Show(null);
        }
    }

    void Say(MailHit h, string text)
    {
        if (mode > 0 && pane.Mail == h) { pane.ShowAi(text); return; }
        message.Visible = true; message.Text = text; top.FitAll();
    }

    async void Summary(MailHit h)
    {
        if (!Store.AiSeesFull) { Say(h, h.FirstLines(600) + "\n\n(AI summary needs Settings → Email & AI → C or D.)"); return; }
        Say(h, "✦ Summarising…");
        try { Say(h, "✦ " + h.Subject + "\n\n" + await Assistant.Summarize(h, CancellationToken.None)); } catch (Exception e) { Say(h, "⚠ " + e.Message); }
    }

    async void Draft(MailHit h)
    {
        if (!Store.AiSeesFull) { Say(h, "✦ Draft reply needs Settings → Email & AI → C or D."); return; }
        var instr = Prompt.AskLong(this, "✦ Draft", "✎ Instructions for your AI (optional) — what to say, tone, length, language…", "e.g. Polite reminder, ask for the revised shop drawing by Sunday.");
        if (instr == null) return;
        Say(h, "✦ Writing…");
        try
        {
            var d = await Assistant.Draft(h, (h.Sent ? "This is a polite follow-up to my own email that got no answer. " : "") + instr, CancellationToken.None);
            Outlook.Reply(h.Entry, h.StoreId, d, false);
            Say(h, "✓ The draft is open in Outlook — check it, then press Send.");
        }
        catch (Exception e) { Say(h, "⚠ " + e.Message); }
    }

    void Handled(MailHit h)
    {
        Outstanding.MarkHandled(h, true);
        NovaEnhance.Work.LogMail(h, "✓ Handled");
        lastNeeds.Remove(h); lastFollow.Remove(h);
        Draw();
        message.Visible = true; message.Text = $"✓ “{h.Subject}” marked as handled — it won't show as waiting again.";
        top.FitAll();
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
            await DrawCalendar(n, true);
            lastCheck = DateTime.Now;
            Draw();
            Counts();
            if (n.Count == 0 && f.Count == 0)
            {
                message.Visible = true;
                message.Text = MailIndex.Count() == 0
                    ? "The email index is still empty. Open Outlook and leave it running — Nova builds the index in the background (the first time can take a while)."
                    : "✓ Nothing is waiting for you. Enjoy your day!";
            }
            else message.Visible = false;
            top.FitAll();
        }
        catch (Exception e) { info.Text = "⚠ " + e.Message; Log.Error("brief", e); }
        finally { busy = false; }
    }

    /// <summary>Meetings today and tomorrow (with clashes, open emails, last file, advice), invitations waiting, your reminders.</summary>
    async Task DrawCalendar(List<MailHit> waiting, bool read)
    {
        calBox.SuspendLayout();
        foreach (Control c in calBox.Controls) c.Dispose();
        calBox.Controls.Clear();
        calTitle.Visible = calBox.Visible = Calendar.InBrief;
        if (!Calendar.InBrief) { calBox.ResumeLayout(); return; }
        var w = mode == 2 ? ListWidth(colA) : ListWidth(colB);
        void Line(string text, Color color, float size = 9.5f, bool bold = false, int indent = 0)
        {
            var l = Th.L(text, size, color, bold); l.MaximumSize = new Size(w - indent, 0); l.Margin = new Padding(indent, 2, 0, 1); calBox.Controls.Add(l);
        }
        try
        {
            var today = DateTime.Today;
            if (read || lastMeetings == null)
            {
                if (Outlook.Running() == null) { lastMeetings = null; Line("Open Outlook to see your calendar here.", Th.Muted); return; }
                lastMeetings = await Task.Run(() => Calendar.Read(today, today.AddDays(14)));
            }
            var all = lastMeetings;
            var two = all.Where(x => x.Start < today.AddDays(2)).ToList();
            Calendar.Prepare(two, waiting);
            foreach (var day in new[] { today, today.AddDays(1) })
            {
                var list = two.Where(x => x.Start.Date <= day && x.End > day && x.IsMeeting && !x.Cancelled && !x.Declined).OrderBy(x => x.Start).ToList();
                Line(day == today ? $"Today · {day:dddd dd MMM}" : $"Tomorrow · {day:dddd dd MMM}", Th.AccentText, 10, true);
                if (list.Count == 0) Line("No meetings", Th.Muted, 9.5f, false, 14);
                foreach (var m in list)
                {
                    var b = Th.B($"{m.Time}  ·  {m.Subject}" + (m.Location.Length > 0 ? "  ·  📍 " + m.Location : "") + (m.NeedsAnswer ? "  ·  ❔ not answered" : ""), () => Calendar.Open(m));
                    b.Font = Th.F(9.5f, true); b.TextAlign = ContentAlignment.MiddleLeft; b.AutoSize = false; b.AutoEllipsis = true; b.Width = w - 14; b.Height = 30; b.Margin = new Padding(14, 2, 0, 0);
                    calBox.Controls.Add(b);
                    foreach (var a in m.Advice) Line((a.StartsWith("⚠") ? "" : "⚠ ") + a, Th.Warn, 9, false, 30);
                    foreach (var h in m.Related)
                    {
                        var hb = Th.B($"✉ open: {h.FromName} · {h.Subject}", () => { try { Outlook.Open(h.Entry, h.StoreId); } catch { } });
                        hb.Font = Th.F(9); hb.TextAlign = ContentAlignment.MiddleLeft; hb.AutoSize = false; hb.AutoEllipsis = true; hb.Width = w - 30; hb.Height = 26; hb.Margin = new Padding(30, 1, 0, 0);
                        hb.BackColor = Th.Bg;
                        calBox.Controls.Add(hb);
                    }
                    if (m.LastFile.Length > 0) Line("📎 Last file: " + m.LastFile, Th.Muted, 9, false, 30);
                }
                var load = Calendar.DayLoad(two, day);
                if (load.Length > 0) Line("⚠ " + load, Th.Warn, 9, false, 14);
            }
            var invites = all.Where(x => x.NeedsAnswer && !x.Cancelled).OrderBy(x => x.Start).ToList();
            if (invites.Count > 0)
            {
                Line($"Invitations waiting for your answer ({invites.Count})", Th.AccentText, 10, true);
                foreach (var m in invites.Take(10))
                {
                    var b = Th.B($"❔ {m.Start:ddd dd MMM HH:mm}  ·  {m.Subject}  ·  {m.Organizer}", () => Calendar.Open(m));
                    b.Font = Th.F(9.5f); b.TextAlign = ContentAlignment.MiddleLeft; b.AutoSize = false; b.AutoEllipsis = true; b.Width = w - 14; b.Height = 28; b.Margin = new Padding(14, 2, 0, 0);
                    calBox.Controls.Add(b);
                }
            }
            var rem = Calendar.Reminders(all, today);
            if (rem.Count > 0)
            {
                Line("Your reminders today", Th.AccentText, 10, true);
                var doneKey = "win_rem_" + today.ToString("yyyyMMdd");
                var done = Store.Str(doneKey, "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                foreach (var m in rem)
                {
                    var key = m.Entry + m.Start.ToString("HHmm");
                    var cb = Th.Check($"{m.Time}  {m.Subject}", done.Contains(key), v =>
                    {
                        var d = Store.Str(doneKey, "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                        if (v) d.Add(key); else d.Remove(key);
                        Store.Set(doneKey, string.Join("|", d));
                    });
                    cb.Margin = new Padding(14, 2, 0, 0);
                    calBox.Controls.Add(cb);
                }
            }
        }
        catch (Exception e) { Line("⚠ Calendar: " + e.Message, Th.Warn); Log.Error("brief calendar", e); }
        finally { calBox.ResumeLayout(); }
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
