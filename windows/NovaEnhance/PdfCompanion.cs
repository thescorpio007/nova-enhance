using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// ✦ Nova next to your PDF viewer (PDF-XChange, Acrobat, Bluebeam, Foxit, Sumatra, Edge): Ask about this PDF (answers with
/// page numbers), Summary / Check this drawing, Search, OCR, Translate a page, Send to Nova Chat.
/// Opens with Ctrl+Shift+D, the tray menu, or the small floating ✦ button on the viewer. Designed by A.HAMROUNI
/// </summary>
static class PdfCompanion
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static bool Button { get => Store.Bool("win_pdf_button", true); set { Store.SetBool("win_pdf_button", value); if (!value) floating?.Hide(); } }

    static readonly string[] Viewers = { "pdfxedit", "pdfxcview", "acrobat", "acrord32", "revu", "foxitpdfeditor", "foxitphantompdf", "foxitreader", "foxitpdfreader", "sumatrapdf", "msedge" };

    public static (IntPtr Window, string Process, string Title) Front()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return (IntPtr.Zero, "", "");
        try
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return (IntPtr.Zero, "", "");
            var name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            return (h, name, sb.ToString());
        }
        catch { return (IntPtr.Zero, "", ""); }
    }

    public static bool IsViewer(string process, string title) =>
        Viewers.Contains(process) && (process != "msedge" || title.Contains(".pdf", StringComparison.OrdinalIgnoreCase));

    /// <summary>The document name in the viewer's title bar.</summary>
    public static string NameFromTitle(string process, string title)
    {
        var t = title.Trim().TrimStart('*').Trim();
        foreach (var suffix in new[] { " - PDF-XChange Editor", " - PDF-XChange Viewer", " - Foxit PDF Editor", " - Foxit PhantomPDF", " - Foxit Reader", " - Foxit PDF Reader", " - SumatraPDF" })
        {
            var i = t.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (i > 0) { t = t[..i]; break; }
        }
        t = Regex.Replace(t, @"^Bluebeam Revu[^-]*-\s*", "", RegexOptions.IgnoreCase);
        var m = Regex.Match(t, @"^(.*?\.pdf)\b", RegexOptions.IgnoreCase);
        if (m.Success) t = m.Groups[1].Value;
        else
        {
            var a = t.IndexOf(" - Adobe Acrobat", StringComparison.OrdinalIgnoreCase);
            if (a > 0) t = t[..a];
        }
        return t.Trim().TrimEnd('*').Trim();
    }

    /// <summary>The full path of the open PDF: Windows' recent files, then the folder index.</summary>
    public static string FindPath(string name)
    {
        if (name.Length == 0) return null;
        var bare = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        try
        {
            var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            var links = new DirectoryInfo(recent).GetFiles("*.lnk")
                .Where(f => f.Name.StartsWith(bare, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(10).ToList();
            if (links.Count > 0)
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t != null)
                {
                    dynamic sh = Activator.CreateInstance(t);
                    foreach (var l in links)
                    {
                        string target = "";
                        try { target = (string)sh.CreateShortcut(l.FullName).TargetPath; } catch { }
                        if (target.Length > 0 && File.Exists(target) && Path.GetFileNameWithoutExtension(target).Equals(bare, StringComparison.OrdinalIgnoreCase)) return target;
                    }
                }
            }
        }
        catch { }
        try { return FolderIndex.FindByName(bare + ".pdf"); } catch { return null; }
    }

    static (IntPtr Window, string Process, string Title) lastSeen;
    static DateTime lastSeenAt;
    static PdfPanel panel;
    static FloatButton floating;

    /// <summary>Ctrl+Shift+D / tray / ✦ button: the panel for the PDF in front (or the one picked).</summary>
    public static void Open(IntPtr window = default, string process = null, string title = null)
    {
        try
        {
            if (window == IntPtr.Zero) (window, process, title) = Front();
            // From the tray menu the viewer is no longer in front: use the one seen last (within 10 minutes).
            if ((window == IntPtr.Zero || !IsViewer(process ?? "", title ?? "")) && lastSeen.Window != IntPtr.Zero && DateTime.Now - lastSeenAt < TimeSpan.FromMinutes(10))
                (window, process, title) = lastSeen;
            string path = null, name = "";
            if (window != IntPtr.Zero && IsViewer(process, title))
            {
                name = NameFromTitle(process, title);
                path = FindPath(name);
            }
            if (path == null)
            {
                using var d = new OpenFileDialog { Filter = "PDF|*.pdf|All files|*.*", Title = name.Length > 0 ? $"Where is “{name}”?" : "Which PDF?", FileName = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name : (name.Length > 0 ? name + ".pdf" : "") };
                var last = Store.Str("win_pdf_last_dir");
                if (last.Length > 0 && Directory.Exists(last)) d.InitialDirectory = last;
                if (d.ShowDialog() != DialogResult.OK) return;
                path = d.FileName;
            }
            Store.Set("win_pdf_last_dir", Path.GetDirectoryName(path) ?? "");
            if (panel == null || panel.IsDisposed) panel = new PdfPanel();
            panel.OpenFile(path, window);
            Screens.Show(panel);
        }
        catch (Exception e) { Log.Error("PDF panel", e); }
    }

    /// <summary>Every half second: show the small ✦ button on a PDF viewer in front, hide it otherwise.</summary>
    public static void Watch()
    {
        var t = new System.Windows.Forms.Timer { Interval = 500 };
        t.Tick += (_, _) =>
        {
            try
            {
                var (h, p, title) = Front();
                if (h == IntPtr.Zero) return;   // Nova itself (or the button) is in front: leave it as it is
                if (IsViewer(p, title)) { lastSeen = (h, p, title); lastSeenAt = DateTime.Now; }
                if (!Button) return;
                if (!IsViewer(p, title) || IsIconic(h) || !GetWindowRect(h, out var r) || r.Right - r.Left < 400) { floating?.Hide(); return; }
                if (floating == null || floating.IsDisposed) floating = new FloatButton();
                floating.Target = (h, p, title);
                var x = Math.Max(r.Left, r.Right - 260);
                var y = Math.Max(r.Top, 0) + 4;
                if (floating.Location != new Point(x, y)) floating.Location = new Point(x, y);
                if (!floating.Visible) floating.Show();
            }
            catch { }
        };
        t.Start();
    }

    /// <summary>The small round ✦ on the viewer's title bar. Never takes the focus.</summary>
    class FloatButton : Form
    {
        public (IntPtr, string, string) Target;
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; return cp; }   // NOACTIVATE · TOOLWINDOW · TOPMOST
        }

        public FloatButton()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            Size = new Size(30, 26); BackColor = Th.Accent;
            var l = new Label { Text = "✦", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = Th.F(11, true), Cursor = Cursors.Hand };
            new ToolTip().SetToolTip(l, "Nova: ask about this PDF, summary, check drawing, OCR, search (Ctrl+Shift+D)");
            l.Click += (_, _) => { var (h, p, t) = Target; PdfCompanion.Open(h, p, t); };
            Controls.Add(l);
            Region = new Region(new Rectangle(0, 0, 30, 26));
        }
    }
}

/// <summary>The ✦ Nova panel for one PDF.</summary>
class PdfPanel : NovaForm
{
    public override bool AllowedWhileLocked => true;   // 🔐 works while locked, on this PDF only

    // 🔐 Buttons that lead to other features (Nova Chat, folders, tasks, memory) are hidden while Nova is locked.
    readonly List<Control> linked = new();
    Control Lk(Control c) { linked.Add(c); return c; }
    protected override void OnActivated(EventArgs e) { base.OnActivated(e); foreach (var c in linked) c.Visible = !AdminLock.Locked; }
    readonly Stack s;
    readonly Label title, where, status;
    readonly TextBox ask, find;
    readonly NumericUpDown page;
    readonly FlowLayoutPanel convo;
    sealed class Turn { public string Q = "", A = "", When = ""; public LinkLabel Ans; public FlowLayoutPanel Box, Row; public Label QL; }
    readonly List<Turn> turns = new();
    Turn pending;
    readonly TextBox pInstr;
    Conversation pdfConv;
    readonly FlowLayoutPanel hits;
    string path = "";
    List<Seg> segs;
    string lastAnswer = "", lastQuestion = "";
    AttachBar attach;
    CancellationTokenSource cts;

    public PdfPanel()
    {
        Text = "Nova Enhance · this PDF";
        Width = 470; Height = Math.Min(900, Screen.PrimaryScreen!.WorkingArea.Height - 40);
        MinimumSize = new Size(380, 420);
        StartPosition = FormStartPosition.Manual;
        s = new Stack();
        Controls.Add(s);
        title = s.Add(Th.Wrap("", 12, Th.Text));
        title.Font = Th.F(12, true);
        where = s.Add(Th.Wrap("", 8.5f, Th.Muted));
        where.Cursor = Cursors.Hand;
        where.Click += (_, _) => { if (File.Exists(path)) Shell.Select(path); };
        status = s.Add(Th.Wrap("", 9, Th.AccentText));
        Thinking.Watch(status);   // ● ● ● dots + timer while it works

        s.Add(Th.Section("Ask about this PDF"));
        ask = s.Add(Th.Prose(Th.Box(true, 3)), true);   // three lines · Enter = ask · Shift+Enter = new line
        ask.PlaceholderText = "e.g. What is the fixing detail of the glass balustrade?";
        ask.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Ask(ask.Text, false); } };
        pInstr = s.Add(Th.Prose(Th.Box(true, 3)), true);
        pInstr.PlaceholderText = "✎ More instructions (optional) — e.g. answer as a table: item · value · page · only the IFC notes · in Arabic";
        pInstr.Visible = false;
        var r0 = s.Add(Th.Row());
        r0.Controls.Add(Th.B("🔍 Ask", () => Ask(ask.Text, false), true));
        r0.Controls.Add(Th.B("✦ Summary / check drawing", Summary));
        r0.Controls.Add(Th.B("✎", () => { pInstr.Visible = !pInstr.Visible; if (pInstr.Visible) pInstr.Focus(); s.FitAll(); }));
        // 📎 Other files to read, check or compare with this PDF (attach, Ctrl+V, drop).
        attach = s.Add(new AttachBar(), true);
        attach.Changed += () => s.FitAll();
        Shown += (_, _) => attach.Hook(ask, this);

        var r1 = s.Add(Th.Row());
        r1.Controls.Add(Th.B("🔤 OCR this PDF", Ocr));
        r1.Controls.Add(Lk(Th.B("💬 Send to Nova Chat", () => { if (File.Exists(path)) Screens.HubAttach(path); })));
        r1.Controls.Add(Lk(Th.B("📁 Search the project folder", () => Screens.FolderSearch(Path.GetDirectoryName(path)))));

        var r2 = s.Add(Th.Row(false));
        r2.Controls.Add(Th.B("🌐 Translate page", TranslatePage));
        page = new NumericUpDown { Minimum = 1, Maximum = 9999, Value = 1, Width = 64, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        r2.Controls.Add(page);
        r2.Controls.Add(Th.L("→ " + Store.TrLang, 9.5f, Th.Muted));

        // 💬 Questions & answers, one after the other, in their own scrolling area (kept per PDF)
        var ch = s.Add(Th.Row(false));
        ch.Controls.Add(Th.L("💬 Questions & answers", 10, Th.Muted, true));
        ch.Controls.Add(Th.B("✕ Clear", ClearConvo));
        convo = s.Add(new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.FromArgb(0x12, 0x15, 0x1E),
            Padding = new Padding(8), Margin = new Padding(0, 2, 0, 8), Height = 380, Tag = "s"
        }, true);
        convo.Resize += (_, _) => FitTurns();
        Resize += (_, _) => { var h = Math.Clamp((int)(ClientSize.Height * 0.55), 260, 1400); if (convo.Height != h) { convo.Height = h; s.FitAll(); } };

        s.Add(Th.Section("Search in this PDF"));
        find = s.Add(Th.Box(false), true);
        find.PlaceholderText = "A word, code or reference — Enter";
        find.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Find(find.Text); } };
        hits = s.Add(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent });
        s.Add(Th.Wrap("The text of this PDF goes to the AI only when you press Ask, Summary or Translate. Search and OCR run on this PC.", 8.5f, Th.Muted));
        s.Add(Th.Signature());
    }

    public void OpenFile(string file, IntPtr viewer)
    {
        bool other = !file.Equals(path, StringComparison.OrdinalIgnoreCase);
        if (other) { segs = null; hits.Controls.Clear(); lastAnswer = lastQuestion = ""; }
        path = file;
        if (other) LoadConvo();
        title.Text = "📄 " + Path.GetFileName(file);
        where.Text = Path.GetDirectoryName(file) + "   (click to show the file)";
        status.Text = "Ready — ask, summarise, search or OCR this PDF.";
        // Next to the viewer: on its right if there is room, otherwise at the right edge of the screen.
        var wa = Screen.FromHandle(viewer == IntPtr.Zero ? Handle : viewer).WorkingArea;
        Location = new Point(wa.Right - Width - 8, wa.Top + 20);
        Height = Math.Min(Height, wa.Height - 40);
        _ = Text_();
    }

    // ⚡ scanned pages still being read in the background (the question reads the ones that matter first)
    List<int> left = new();
    Task fullRead;

    async Task<List<Seg>> Text_()
    {
        if (segs != null) return segs;
        var p = path;
        // 1 · read before by any Nova feature: instant (the shared Nova index)
        var known = FolderIndex.Known(p);
        if (FileText.OldPdfText(known)) { FolderIndex.ForgetFile(p); known = null; }   // kept before tables were read row by row: read again once
        if (known != null)
        {
            segs = known.Where(x => (x.Text ?? "").Trim().Length > 0).ToList();
            left = new();
            ShowRead(segs, "✓ from the Nova index (read before)");
            return segs;
        }
        // 2 · the pages with text in seconds; the scanned pages are read in the background, 3 at a time
        status.Text = "Reading the PDF…";
        PdfFast.Quick q = null;
        await RunSta(() => q = PdfFast.Read(p, Array.Empty<string>(), 0, s => BeginInvoke(new Action(() => { if (!IsDisposed && p == path) status.Text = s + "…"; }))));
        if (p != path) return null;
        segs = q.Segs.Where(x => (x.Text ?? "").Trim().Length > 0).ToList();
        left = q.Left;
        page.Maximum = Math.Max(1, q.Pages);
        if (left.Count > 0)
        {
            fullRead = Task.Run(() =>
            {
                List<Seg> all = null;
                try { all = Projects.Read(p, CancellationToken.None).Segs; } catch (Exception e) { Log.Warn("PDF", "background read: " + e.Message); }
                if (all == null) return;
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || p != path) return;
                    segs = all.Where(x => (x.Text ?? "").Trim().Length > 0).ToList(); left = new();
                    ShowRead(segs, "✓ every page read, scanned pages included");
                }));
            });
            status.Text = $"✓ {q.TextPages} text page(s) ready · reading the {left.Count} scanned page(s) in the background, 3 at a time…";
        }
        else ShowRead(segs, "✓");
        return segs;
    }

    void ShowRead(List<Seg> s, string how)
    {
        var pages = s.Select(PageOf).Where(x => x > 0).DefaultIfEmpty(1).Max();
        page.Maximum = Math.Max(page.Maximum, pages);
        status.Text = s.Count == 0 ? "⚠ No readable text — try 🔤 OCR this PDF" : $"{how} · {pages} page{(pages == 1 ? "" : "s")} · {s.Sum(x => x.Text.Length):N0} characters";
    }

    static Task RunSta(Action a)
    {
        var tcs = new TaskCompletionSource();
        var t = new Thread(() => { try { a(); tcs.SetResult(); } catch (Exception e) { tcs.SetException(e); } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    static int PageOf(Seg x)
    {
        var m = Regex.Match(x.Goto ?? "", @"page:(\d+)");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        m = Regex.Match(x.Loc ?? "", @"(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    void GoTo(int p) => Opener.Open(new FileHit { Path = path }, new PieceHit { Loc = "page " + p, Goto = "page:" + p, Text = "", Matches = new() }, "");

    /// <summary>Shows an answer; [p.3] / [p.3, 5] become links that open the PDF at that page.</summary>
    /// <summary>The answer under its question: the open question when there is one, else a new pair (Summary, Translate…).</summary>
    void ShowAnswer(string text, string q = null, bool keep = false)
    {
        lastAnswer = text;          // the AI's own text (Table → Excel and Copy keep its table)
        if (q != null && pending != null) { if (pending.A.Length == 0) Fill(pending, "■ Stopped"); pending = null; }   // Summary / Translate: their own pair
        var t = q != null ? AddTurn(q) : pending ?? AddTurn(lastQuestion.Length > 0 ? lastQuestion : "✦");
        Fill(t, text);
        if (!keep) { pending = null; SaveConvo(); }
    }

    Turn AddTurn(string q, string when = null)
    {
        var t = new Turn { Q = q, When = when ?? DateTime.Now.ToString("dd MMM HH:mm") };
        t.Box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 14) };
        t.QL = new Label { AutoSize = true, Text = $"🧑 {t.When}\n{q}", BackColor = Color.FromArgb(0x3B, 0x35, 0x70), ForeColor = Color.White, Padding = new Padding(10, 6, 10, 6), Font = Th.F(10), UseMnemonic = false, Margin = new Padding(36, 0, 0, 4) };
        t.Ans = new LinkLabel
        {
            AutoSize = true, Font = Th.F(10), ForeColor = Th.Text, LinkColor = Th.AccentText, ActiveLinkColor = Th.Accent, BackColor = Color.FromArgb(0x1B, 0x20, 0x30),
            Padding = new Padding(10, 8, 10, 8), UseMnemonic = false, Text = "⏳ Nova is reading the PDF…", Margin = new Padding(0, 0, 36, 4)
        };
        t.Ans.LinkClicked += (_, e) => { if (e.Link?.LinkData is int p) GoTo(p); };
        t.Row = Th.Row();
        t.Row.Controls.Add(Th.B("⧉ Copy", () => { if (t.A.Length > 0) Clipboard.SetText(t.A); }));
        t.Row.Controls.Add(Th.B("📊 Table → Excel", () =>
        {
            try { var x = FileTalk.ExportXlsx(t.A, Path.GetFileNameWithoutExtension(path)); Shell.Open(x); status.Text = "📊 Saved: " + x; }
            catch (Exception ex) { status.Text = "⚠ " + ex.Message; }
        }));
        t.Row.Controls.Add(Th.B("More detail", () => { if (t.Q.Length > 1 && !t.Q.StartsWith("✦") && !t.Q.StartsWith("🌐")) Ask(t.Q, true); else Summary(); }));
        t.Row.Controls.Add(Lk(Th.B("👍", () =>
        {
            if (t.A.Length == 0) return;
            Memory.Good(t.Q, t.A, path, new[] { path }, new[] { t.A }, "pdf");
            status.Text = "👍 🧠 Nova remembers this answer for similar questions on this PDF";
        })));
        t.Row.Controls.Add(Lk(Th.B("👎", () =>
        {
            Memory.Bad(t.Q, path, null, "pdf");
            status.Text = "👎 Noted — Nova forgets that answer. Try More detail, or 📁 Search the project folder for the right file.";
        })));
        t.Row.Controls.Add(Lk(Th.B("📌 Save as task", () =>
        {
            if (t.A.Length == 0) return;
            var link = new WorkItem { Kind = "file", Path = path, Subject = Path.GetFileName(path) };
            Screens.Saved(Work.FromText(t.Q + " · " + Path.GetFileNameWithoutExtension(path), t.Q, t.A, "pdf", new[] { link }));
        })));
        foreach (Control c in t.Row.Controls) if (linked.Contains(c)) c.Visible = !AdminLock.Locked;
        t.Box.Controls.Add(t.QL); t.Box.Controls.Add(t.Ans); t.Box.Controls.Add(t.Row);
        convo.Controls.Add(t.Box);
        turns.Add(t);
        FitTurns();
        convo.ScrollControlIntoView(t.Box);
        return t;
    }

    void Fill(Turn t, string a)
    {
        t.A = a ?? "";
        var text = Readable(t.A);
        t.Ans.Links.Clear();
        t.Ans.Text = text;
        foreach (Match m in Regex.Matches(text, @"\[(?:p\.?|page\s*)\s*(\d+)(?:\s*[,–-]\s*(?:p\.?\s*)?\d+)*\]", RegexOptions.IgnoreCase))
            t.Ans.Links.Add(m.Index, m.Length, int.Parse(m.Groups[1].Value));
        FitTurns();
        convo.ScrollControlIntoView(t.Box);
    }

    void FitTurns()
    {
        var w = Math.Max(200, convo.ClientSize.Width - convo.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
        convo.SuspendLayout();
        foreach (var t in turns)
        {
            t.QL.MaximumSize = new Size(w - 36, 0);
            t.Ans.MaximumSize = new Size(w - 36, 0);
            t.Row.MaximumSize = new Size(w, 0);
        }
        convo.ResumeLayout();
    }

    // 💾 kept per PDF on this PC (not while Nova is locked)
    string ConvoFile()
    {
        var h = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16];
        return Path.Combine(Paths.Cache, "pdfconv", h + ".json");
    }

    void SaveConvo()
    {
        if (AdminLock.Locked || path.Length == 0) return;
        try
        {
            var arr = new System.Text.Json.Nodes.JsonArray(turns.Where(t => t.A.Length > 0).TakeLast(40)
                .Select(t => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["q"] = t.Q, ["a"] = t.A, ["when"] = t.When }).ToArray());
            var f = ConvoFile(); Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, arr.ToJsonString());
        }
        catch (Exception e) { Log.Write("pdf conversation: " + e.Message); }
    }

    void LoadConvo()
    {
        convo.Controls.Clear(); turns.Clear(); pending = null;
        if (AdminLock.Locked) return;
        try
        {
            var f = ConvoFile();
            if (!File.Exists(f)) return;
            foreach (var x in (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f)) as System.Text.Json.Nodes.JsonArray ?? new()).OfType<System.Text.Json.Nodes.JsonObject>())
                Fill(AddTurn(x["q"]?.ToString() ?? "", x["when"]?.ToString()), x["a"]?.ToString() ?? "");
            if (turns.Count > 0) { lastQuestion = turns[^1].Q; lastAnswer = turns[^1].A; }
        }
        catch (Exception e) { Log.Write("pdf conversation: " + e.Message); }
    }

    void ClearConvo()
    {
        convo.Controls.Clear(); turns.Clear(); pending = null; lastAnswer = lastQuestion = "";
        try { File.Delete(ConvoFile()); } catch { }
        pdfConv = null;
    }

    /// <summary>The last questions and answers on this PDF, for a follow-up (“and for GL-02?”).</summary>
    string Earlier()
    {
        var done = turns.Where(t => t != pending && t.A.Length > 0 && !t.A.StartsWith("⚠")).TakeLast(3).ToList();
        if (done.Count == 0) return "";
        var sb = new StringBuilder("Earlier in this conversation about this document:\n");
        foreach (var t in done) sb.Append("Q: ").Append(t.Q).Append("\nA: ").Append(t.A.Length > 1200 ? t.A[..1200] + "…" : t.A).Append("\n");
        return sb.Append('\n').ToString();
    }

    /// <summary>The answer made readable in this window: **bold** marks removed, a table shown as its column names then one line per
    /// row (“• GL-01 · Narrow lock · IN.20.400.30 · JNF”), emoji this window cannot draw replaced by a plain mark.</summary>
    internal static string Readable(string md)
    {
        var sb = new StringBuilder();
        foreach (var raw in (md ?? "").Replace("\r", "").Split('\n'))
        {
            var l = raw.TrimEnd();
            var t = l.Trim();
            if (t.StartsWith("|") && t.EndsWith("|") && t.Length > 1)
            {
                var cells = t.Trim('|').Split('|').Select(c => c.Trim()).ToList();
                if (cells.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$"))) continue;          // the |---|---| line
                bool header = sb.Length == 0 || !sb.ToString().TrimEnd().Split('\n').Last().StartsWith("•") && !sb.ToString().TrimEnd().Split('\n').Last().StartsWith("▦");
                sb.Append(header ? "▦ " : "• ").AppendLine(string.Join(header ? "  ·  " : " · ", cells.Where(c => c.Length > 0)));
                continue;
            }
            l = Regex.Replace(l, @"^#{1,6}\s*", "");
            l = Regex.Replace(l, @"^\s*[\*\-]\s+", "• ");
            sb.AppendLine(l);
        }
        var s = sb.ToString().Replace("**", "").Replace("__", "");
        s = Regex.Replace(s, @"\p{Cs}{2}|\p{So}\uFE0F?", m => m.Value is "⚠" or "✓" or "✗" or "★" or "▦" or "◆" ? m.Value : "◆");
        return Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
    }

    static string Extracts(List<Seg> list, int max)
    {
        var sb = new StringBuilder();
        foreach (var g in list.GroupBy(PageOf).OrderBy(g => g.Key))
        {
            var t = string.Join("\n", g.Select(x => x.Text.Trim()));
            sb.Append("[p.").Append(g.Key <= 0 ? 1 : g.Key).Append("]\n").Append(t).Append("\n\n");
            if (sb.Length > max) break;
        }
        return sb.Length > max ? sb.ToString(0, max) : sb.ToString();
    }

    async void Ask(string q, bool detailed)
    {
        AdminLock.Sandbox();   // 🔐 locked: this PDF only — no Brain
        using var privateWork = Ai.Private();   // your emails / documents: the routing rule may keep them on the local AI
        if (q == ask.Text) { Speller.FixBeforeSearch(ask, () => Ask(ask.Text, detailed)); q = ask.Text; }
        q = (q ?? "").Trim();
        var files = attach?.Files.ToList() ?? new List<string>();
        if (q.Length == 0 && files.Count > 0) q = "Compare this document with the attached file(s) in a comparison table, then the key differences.";
        if (q.Length == 0) { ask.Focus(); return; }
        if (files.Count > 0) detailed = true;
        cts?.Cancel(); var my = cts = new CancellationTokenSource();
        var earlier = Earlier();
        var prevQ = turns.LastOrDefault(t => t.A.Length > 0)?.Q ?? "";
        if (pending != null && pending.A.Length == 0) Fill(pending, "■ Replaced by your next question");
        pending = AddTurn(q + (detailed ? "  (more detail)" : ""));
        ask.Text = "";
        var (card, changed) = detailed || AdminLock.InSandbox ? ((KnowCard)null, (List<string>)null) : Memory.Find(q, path, "pdf");
        if (card != null) ShowAnswer($"🧠 Nova remembers (you liked this on {card.Saved:dd MMM yyyy}):\n{card.A}" + (changed.Count > 0 ? "\n⚠ Changed since: " + string.Join(", ", changed) : "") + "\n↻ Checking again…", keep: true);
        try
        {
            var all = await Text_();
            if (all == null || all.Count == 0) { status.Text = "⚠ No readable text in this PDF — press 🔤 OCR this PDF first"; ShowAnswer("⚠ No readable text in this PDF — press 🔤 OCR this PDF first"); return; }
            var prov = Ai.Current() ?? throw new AiException("Add an AI key in Settings → AI & API keys");
            lastQuestion = q;
            status.Text = $"Asking {prov.Label}…";
            // a short follow-up (“and for GL-02?”) also uses the words of the question before it
            var terms = FolderAsk.LocalTerms(q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 7 && prevQ.Length > 0 ? q + " " + prevQ : q);
            if (left.Count > 0)
            {
                // ⚡ scanned pages not read yet: the ones that matter for THIS question first (bookmarks, contents, section pages), 3 at a time
                var p0 = path; List<Seg> more = null;
                status.Text = "Reading the scanned pages that matter for your question…";
                await RunSta(() => more = PdfFast.Read(p0, terms, 12, s => BeginInvoke(new Action(() => { if (!IsDisposed) status.Text = s + "…"; }))).Segs);
                if (p0 != path) return;
                var hadPages = all.Select(PageOf).ToHashSet();
                all = all.Concat(more.Where(x => !hadPages.Contains(PageOf(x)))).OrderBy(PageOf).ToList();
                segs = all;
                status.Text = $"Asking {prov.Label}…";
            }
            // 🔎 your words and their related words (sliding → track, rail, roller, pocket…)
            var pick = all.Sum(x => x.Text.Length) <= 40000 ? all : PdfFast.Pick(all, terms, 25);
            var sys = "You answer questions about ONE construction document (specification, drawing, report, letter). Use ONLY the page texts given; each starts with its page as [p.N]. " +
                      "After each fact put its page in square brackets, e.g. [p.3]. Quote exact values (dimensions, materials, ratings, references, revisions). " +
                      (detailed ? "Give a complete, structured answer. " : "Answer in 2 to 6 short lines, the direct answer first. ") +
                      "If the document does not say, write so plainly. Reply in the language of the question." + FolderAsk.AdviceRule + Modes.ProfileNote();
            var attached = new System.Text.StringBuilder();
            int fi = 1;
            foreach (var f in files)
            {
                var txt = await FileTalk.Text(f, this, my.Token, stMsg => status.Text = stMsg, q);
                attached.Append($"[F{fi++}] {Path.GetFileName(f)}\n{(txt.Length > 9000 ? txt[..9000] : txt)}\n\n");
            }
            if (files.Count > 0) sys += " The main document's pages are cited [p.N]; the user also attached other files, cited [F1], [F2]… (the main document is the first column when comparing)." + FileTalk.Rules;
            using var _aiFor16 = Ai.For("pdf", Ai.Picked(prov, "pdf"));   // ⚙ AI for each feature
            var a = await Ai.Chat(sys, $"Document: {Path.GetFileName(path)}\n\n{Extracts(pick, 60000)}\n" + earlier + (attached.Length > 0 ? "ATTACHED FILES:\n" + attached + "\n" : "") + $"Question: {q}" + (pInstr.Text.Trim().Length > 0 ? "\nInstructions from the user (follow them for content, format, length and language): " + pInstr.Text.Trim() : ""), !detailed, false, prov, my.Token);
            if (my.IsCancellationRequested) return;
            if (!AdminLock.InSandbox)   // 🔐 locked: nothing goes to your history or chats
            {
                History.Add("ask", prov.Id, Store.Model(prov.Id), "pdf", Path.GetFileName(path) + " · " + q, a);
                if (pdfConv?.Ref != path) pdfConv = null;
                Conversations.Add(ref pdfConv, "pdf", Path.GetFileName(path), path, q, a);
            }
            ShowAnswer(a);
            status.Text = $"✓ Answer from this PDF · {prov.Label} — click a [p.N] to open that page" + Newer();
        }
        catch (Exception e) { if (!my.IsCancellationRequested) { status.Text = "⚠ " + e.Message; ShowAnswer("⚠ " + e.Message); } }
    }

    /// <summary>"⚠ a newer revision is in the folder: …" when the folder index knows one.</summary>
    string Newer()
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Revisions.Superseded(dir, path)) return "";
            return " · ⚠ " + Revisions.Tag(dir, path);
        }
        catch { return ""; }
    }

    async void Summary()
    {
        AdminLock.Sandbox();   // 🔐 locked: this PDF only — no Brain
        using var privateWork = Ai.Private();   // your emails / documents: the routing rule may keep them on the local AI
        cts?.Cancel(); var my = cts = new CancellationTokenSource();
        try
        {
            var all = await Text_();
            if (all == null || all.Count == 0) { status.Text = "⚠ No readable text in this PDF — press 🔤 OCR this PDF first"; return; }
            var prov = Ai.Current() ?? throw new AiException("Add an AI key in Settings → AI & API keys");
            lastQuestion = "";
            status.Text = $"✦ Reading and checking with {prov.Label}…";
            var sys = "You are a " + Persona.Expert + " checking a document for the user, " + Persona.Who + ". From the page texts (each starts with [p.N]) write:\n" +
                      "1. WHAT IT IS — document/drawing number, title, revision, date, scale, discipline, project (from the title block) [p.N].\n" +
                      "2. KEY CONTENT — what it shows or requires: main elements, materials, dimensions, ratings, standards, in short bullets with [p.N].\n" +
                      "3. NOTES & REFERENCES — referenced drawings, specs, RFIs, details.\n" +
                      "4. CHECK — possible issues for the user to verify: missing or unclear dimensions/notes, references to other revisions, contradictions, items marked TBC/hold, missing approvals or stamps. Advice only, never invent; say 'none found' if nothing.\n" +
                      "Keep it compact. Use the language of the document (English unless it is in Arabic or French)." + Modes.ProfileNote();
            using var _aiFor15 = Ai.For("pdf", Ai.Picked(prov, "pdf"));   // ⚙ AI for each feature
            var a = await Ai.Chat(sys, $"Document: {Path.GetFileName(path)}\n\n{Extracts(all, 60000)}", false, false, prov, my.Token);
            if (my.IsCancellationRequested) return;
            if (!AdminLock.InSandbox) History.Add("ask", prov.Id, Store.Model(prov.Id), "pdf-check", Path.GetFileName(path), a);
            ShowAnswer(a, "✦ Summary / check drawing");
            status.Text = $"✓ Summary and check · {prov.Label}" + Newer();
        }
        catch (Exception e) { if (!my.IsCancellationRequested) status.Text = "⚠ " + e.Message; }
    }

    async void TranslatePage()
    {
        AdminLock.Sandbox();   // 🔐 locked: this PDF only — no Brain
        try
        {
            var all = await Text_();
            var p = (int)page.Value;
            var text = string.Join("\n", (all ?? new()).Where(x => PageOf(x) == p || (p == 1 && PageOf(x) == 0)).Select(x => x.Text));
            if (text.Trim().Length == 0) { status.Text = $"⚠ Page {p} has no readable text — try 🔤 OCR this PDF"; return; }
            if (text.Length > 20000) text = text[..20000];
            status.Text = $"🌐 Translating page {p} into {Store.TrLang}…";
            var prov = Ai.Current() ?? throw new AiException("Add an AI key in Settings → AI & API keys");
            using var _aiFor14 = Ai.For("pdf");   // ⚙ AI for each feature
            var r = await Ai.Enhance(prov, "translate", text, CancellationToken.None);
            lastQuestion = "";
            ShowAnswer($"[p.{p}] → {Store.TrLang}\n\n" + r, $"🌐 Translate page {p} → {Store.TrLang}");
            status.Text = $"✓ Page {p} translated";
        }
        catch (Exception e) { status.Text = "⚠ " + e.Message; }
    }

    async void Ocr()
    {
        AdminLock.Sandbox();   // 🔐 locked: this PDF only — no Brain
        try
        {
            status.Text = "🔤 OCR on this PC — big drawings take a moment per page…";
            var p = path;
            List<(int, string)> pages = null;
            await Task.Run(() => pages = NovaEnhance.Ocr.Pdf(p, null, 200));
            if (pages == null || pages.Count == 0) { status.Text = "⚠ OCR found no text (or no OCR language is installed in Windows)"; return; }
            var dir = Path.Combine(Paths.Root, "OCR");
            Directory.CreateDirectory(dir);
            var outFile = Path.Combine(dir, Path.GetFileNameWithoutExtension(p) + " — OCR.txt");
            File.WriteAllText(outFile, string.Join("\n\n", pages.Select(x => $"===== page {x.Item1} =====\n{x.Item2}")), new UTF8Encoding(false));
            // Use the OCR text for Ask/Summary/Search too.
            segs = pages.Select(x => new Seg($"page {x.Item1}", x.Item2, "page:" + x.Item1)).Where(x => x.Text.Trim().Length > 0).ToList();
            status.Text = $"✓ OCR done · {pages.Count} page{(pages.Count == 1 ? "" : "s")} · saved in Documents\\Nova Enhance\\OCR";
            var unclear = pages.Count(x => NovaEnhance.Ocr.IsUnclear(x.Item2));
            if (unclear > 0 && Ai.HasKey("gemini") &&
                MessageBox.Show(this, $"{unclear} page(s) are hard to read for the PC OCR (drawings, stamps, handwriting, Arabic).\n\nSend those page images to Gemini vision for a better reading?", "Nova Enhance", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                foreach (var (pg, _) in pages.Where(x => NovaEnhance.Ocr.IsUnclear(x.Item2)).ToList())
                {
                    status.Text = $"Gemini vision · page {pg}…";
                    var pic = await Task.Run(() => NovaEnhance.Ocr.Picture(p, pg));
                    var txt = await Ai.Vision(pic.Data, pic.Mime);
                    segs.RemoveAll(x => PageOf(x) == pg);
                    segs.Add(new Seg($"page {pg}", txt, "page:" + pg));
                }
                File.WriteAllText(outFile, string.Join("\n\n", segs.OrderBy(PageOf).Select(x => $"===== page {PageOf(x)} =====\n{x.Text}")), new UTF8Encoding(false));
                status.Text = "✓ OCR done with Gemini vision for the unclear pages · saved in Documents\\Nova Enhance\\OCR";
            }
            Shell.Select(outFile);
        }
        catch (Exception e) { status.Text = "⚠ OCR: " + e.Message; Log.Error("PDF OCR", e); }
    }

    async void Find(string q)
    {
        AdminLock.Sandbox();   // 🔐 locked: this PDF only — no Brain
        q = (q ?? "").Trim();
        hits.Controls.Clear();
        if (q.Length == 0) return;
        // ❓ a question typed in Search (it finds exact words only): asked with ✦ Ask instead
        if (q.EndsWith("?") || Regex.IsMatch(q, @"^(what|which|who|where|when|why|how|did|does|do|is|are|was|were|can|could|give|list|show|find me|tell)\b", RegexOptions.IgnoreCase) && q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4)
        {
            ask.Text = q;
            status.Text = "↪ That is a question — Search finds exact words, so it was sent to ✦ Ask";
            Ask(q, false);
            return;
        }
        var all = await Text_();
        int n = 0;
        foreach (var x in all ?? new())
        {
            var i = x.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            while (i >= 0 && n < 60)
            {
                var a = Math.Max(0, i - 60);
                var b = Math.Min(x.Text.Length, i + q.Length + 60);
                var snip = Regex.Replace(x.Text[a..b], @"\s+", " ").Trim();
                var pg = PageOf(x);
                var btn = Th.B($"p.{(pg <= 0 ? 1 : pg)}  ·  …{snip}…", () => GoTo(pg <= 0 ? 1 : pg));
                btn.Font = Th.F(9); btn.TextAlign = ContentAlignment.MiddleLeft; btn.AutoSize = false; btn.AutoEllipsis = true; btn.Width = Math.Max(300, s.ClientSize.Width - 50); btn.Height = 28;
                hits.Controls.Add(btn);
                n++;
                i = x.Text.IndexOf(q, i + q.Length, StringComparison.OrdinalIgnoreCase);
            }
        }
        if (n == 0) hits.Controls.Add(Th.L($"“{q}” not found in this PDF" + (all?.Count == 0 ? " (no readable text — try OCR)" : ""), 9.5f, Th.Muted));
        else status.Text = $"{n}{(n >= 60 ? "+" : "")} place{(n == 1 ? "" : "s")} found — click one to open that page";
    }
}
