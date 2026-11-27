using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🎯 Guided search: Nova narrows the area step by step, always with numbered options, then finds the file, the drawing or the clause
/// fast — 1 Where (the project) › 2 What (contract, specifications, drawings, submittals, emails, minutes, BOQ) › 3 Narrow (spec division,
/// drawing discipline, submittal type, folder) › 4 Find (by name first, then inside the text: open at the clause). Only Nova's own index
/// is used (no AI, nothing leaves the PC); 🔒 private folders are never listed. ⏱ the time limit of guided mode. Designed by A.HAMROUNI
/// </summary>
class GuidedSearchForm : NovaForm
{
    record Cat(string Key, string Icon, string Label, Regex Match);

    static readonly string[] Cad = { ".dwg", ".dxf", ".dgn", ".rvt", ".rfa", ".ifc", ".nwd", ".nwc", ".skp", ".3dm", ".pln" };
    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    static readonly Cat[] Cats =
    {
        new("contract", "📜", "Contract & conditions", new(@"contract|agreement|conditions|tender|\bloa\b|letter of (award|acceptance|intent)|addend|appendi|annex|\bfidic\b|particular|employer'?s? req", I)),
        new("spec", "📘", "Specifications", new(@"spec|(^|\\)(section[ _-]?)?\d{2}[ _]?\d{2}[ _]?\d{2}\b[^\\]*$|\bdiv(ision)?[ _-]?\d", I)),
        new("dwg", "📐", "Drawings", new(@"drawing|\bdwgs?\b|\bdrg|shop ?dr|\bsdw?\b|layout|\bplans?\b|elevation|\bifc\b|for construction|as[ -]?built|\.(dwg|dxf|dgn)$", I)),
        new("sub", "📦", "Submittals & approvals", new(@"submittal|transmittal|\bmas\b|\bmar\b|material (approval|submittal)|\brfi\b|\bw?ir\b|inspection|\bncr\b|method statement|\bms\b|pre-?qual|\bpq\b|sample|technical submit", I)),
        new("mail", "✉", "Emails", null),
        new("mom", "🗒", "Minutes of meeting", new(@"minute|\bmom\b|meeting", I)),
        new("ltr", "📨", "Letters & instructions", new(@"letter|instruction|notice|\bltr\b", I)),
        new("boq", "💰", "BOQ & pricing", new(@"\bboq\b|bills? of quant|pricing|priced|quantit|\bvo\b|variation|valuation|payment cert|\bipc\b", I)),
        new("all", "🗂", "Everything", new(@".", I)),
    };

    static readonly Dictionary<string, string> Divisions = new()
    {
        ["00"] = "Procurement & contracting", ["01"] = "General requirements", ["02"] = "Existing conditions", ["03"] = "Concrete", ["04"] = "Masonry",
        ["05"] = "Metals", ["06"] = "Wood & plastics", ["07"] = "Thermal & moisture", ["08"] = "Openings (doors, windows, glazing)", ["09"] = "Finishes",
        ["10"] = "Specialties", ["11"] = "Equipment", ["12"] = "Furnishings", ["13"] = "Special construction", ["14"] = "Conveying (lifts)",
        ["21"] = "Fire suppression", ["22"] = "Plumbing", ["23"] = "HVAC", ["25"] = "Integrated automation", ["26"] = "Electrical",
        ["27"] = "Communications", ["28"] = "Electronic safety & security", ["31"] = "Earthwork", ["32"] = "Exterior improvements", ["33"] = "Utilities",
    };

    static readonly Dictionary<string, string> Disciplines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AR"] = "Architecture", ["ARC"] = "Architecture", ["ARCH"] = "Architecture", ["A"] = "Architecture",
        ["ST"] = "Structure", ["STR"] = "Structure", ["S"] = "Structure",
        ["ME"] = "Mechanical / MEP", ["MEP"] = "Mechanical / MEP", ["MEC"] = "Mechanical / MEP", ["MECH"] = "Mechanical / MEP", ["HVAC"] = "Mechanical / MEP", ["M"] = "Mechanical / MEP",
        ["EL"] = "Electrical", ["ELE"] = "Electrical", ["ELEC"] = "Electrical", ["E"] = "Electrical",
        ["PL"] = "Plumbing & drainage", ["PLU"] = "Plumbing & drainage", ["PD"] = "Plumbing & drainage", ["P"] = "Plumbing & drainage",
        ["FF"] = "Fire & life safety", ["FP"] = "Fire & life safety", ["FA"] = "Fire & life safety", ["FLS"] = "Fire & life safety",
        ["ID"] = "Interior design", ["INT"] = "Interior design", ["LA"] = "Landscape", ["LS"] = "Landscape", ["LND"] = "Landscape",
        ["CV"] = "Civil & infrastructure", ["CIV"] = "Civil & infrastructure", ["C"] = "Civil & infrastructure", ["FC"] = "Façade", ["FAC"] = "Façade",
    };

    static readonly (string Label, Regex Rx)[] SubTypes =
    {
        ("Material approvals (MAS / MAR)", new(@"\bma[sr]\b|material", I)), ("Shop drawings", new(@"\bsdw?s?\b|shop ?dr", I)),
        ("RFI", new(@"\brfi\b", I)), ("Inspection requests (IR / WIR)", new(@"\b[wm]?ir\b|inspection", I)), ("NCR", new(@"\bncr\b|non-?conform", I)),
        ("Method statements", new(@"\bms\b|method", I)), ("Prequalification", new(@"\bpq\b|pre-?qual", I)), ("Samples & mock-ups", new(@"sample|mock", I)),
        ("Transmittals", new(@"transmittal|\btrn\b", I)), ("Site instructions / variations", new(@"\bsi\b|instruction|\bvo\b|variation", I)),
    };

    // ------------------------------------------------------------------ the state of the search

    int step;                                  // 0 where · 1 what · 2 narrow · 3 find
    string whereLabel = "", whatLabel = "", narrowLabel = "";
    Project project;                           // null = general / all
    List<string> folders = new();
    Cat cat;
    List<Projects.FileRow> inWhat = new(), inNarrow = new();
    readonly Guided.Clock clock = new();
    CancellationTokenSource cts;

    readonly Label crumb, note;
    readonly FlowLayoutPanel body;
    readonly Panel findBar;
    readonly TextBox query;
    readonly CheckBox latestOnly, readMore;
    readonly Button backBtn, timerBtn;
    readonly List<Action> numbered = new();
    readonly List<(Button B, string Title)> opts = new();
    readonly TextBox optFilter;
    readonly Button stopBtn;
    // 🔎 the hint (“what are you looking for?”): every choice is searched at once while you choose
    readonly TextBox hint;
    // ⚡ Quick find (4.3 🎯 Find a file): a number or words → file names at once, the same index as the file finder, no AI
    readonly TextBox qBox;
    readonly Label qInfo;
    readonly ListView qList;
    readonly Panel quick;
    int qRun;
    readonly System.Windows.Forms.Timer qTimer = new() { Interval = 450 };
    readonly Label hintInfo;
    readonly CheckBox novaOnly;
    readonly Panel resPanel;
    readonly FlowLayoutPanel resList;
    readonly Label resHead;
    readonly List<List<Projects.FileRow>> optSets = new();
    readonly List<string> optBase = new();
    readonly System.Windows.Forms.Timer hintTimer = new() { Interval = 600 };
    CancellationTokenSource hintCts, readCts;
    static readonly Dictionary<string, (List<string> Words, string Shown)> aiWords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>⏱ Nova is working: the time counts in the line under the steps, and ■ Stop shows.</summary>
    void Working(bool on) { if (!stopBtn.IsDisposed) stopBtn.Visible = on; }

    void QuickShow(bool on)
    {
        qInfo.Visible = on; qList.Visible = on;
        quick.Height = on ? Math.Clamp(ClientSize.Height * 2 / 5, 220, 420) : 46;
    }

    async void QuickFind()
    {
        var q = qBox.Text.Trim();
        var my = ++qRun;
        if (q.Length < 2) { QuickShow(false); return; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        qInfo.Text = "⚡ Finding…"; qInfo.Visible = true;
        List<Projects.FileRow> found;
        try
        {
            var folders = Projects.All().SelectMany(p => p.Folders).Concat(Projects.General).ToList();
            found = await Task.Run(() => Projects.FindFiles(q, folders, true, false, 3000, false));
        }
        catch (Exception e) { qInfo.Text = "⚠ " + e.Message; return; }
        if (my != qRun || IsDisposed) return;
        // 🖧 the server (official · MZA DC) first; a local copy of the same file is “same as the server ✓” or “⚠ older than the server”
        bool Net(string p) => Projects.ReadAll.IsNetwork(p);
        var serverCopy = found.Where(x => Net(x.Path)).GroupBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Max(x => x.Modified), StringComparer.OrdinalIgnoreCase);
        var list = found.OrderBy(x => x.Status == "superseded" ? 1 : 0).ThenBy(x => Net(x.Path) ? 0 : 1).ThenByDescending(x => x.Modified).Take(400).ToList();
        qList.BeginUpdate(); qList.Items.Clear();
        foreach (var f in list)
        {
            var latest = f.Status != "superseded";
            var net = Net(f.Path);
            var src = net ? "🖧 Server · official" : "💻 Local copy";
            Color? srcColor = null;
            if (!net && serverCopy.TryGetValue(Path.GetFileName(f.Path), out var sm))
            {
                if (f.Modified < sm.AddMinutes(-2)) { src += " · ⚠ older"; srcColor = Th.Warn; }
                else src += " · same ✓";
            }
            var dir = Path.GetDirectoryName(f.Path) ?? "";
            var it = new ListViewItem(new[] { src, Path.GetFileName(f.Path), (f.Rev.Length > 0 ? f.Rev + " · " : "") + (latest ? "✓ latest" : "superseded"), dir.Length > 64 ? "…" + dir[^62..] : dir, f.Modified == default ? "" : f.Modified.ToString("dd MMM yyyy") })
            { Tag = f.Path, ForeColor = latest ? Th.Text : Th.Muted, ToolTipText = f.Path, UseItemStyleForSubItems = false };
            it.SubItems[0].ForeColor = srcColor ?? (net ? Color.FromArgb(0x9F, 0xD0, 0xFF) : Th.Muted);
            if (latest && f.Rev.Length > 0) it.SubItems[2].ForeColor = Th.Good;
            qList.Items.Add(it);
        }
        qList.EndUpdate();
        var rec = SubmittalRecord.From(q, found.Select(x => (x.Path, x.Modified)));
        qInfo.Text = found.Count == 0
            ? $"⚡ No file name has “{q}” · try fewer words, or the guided steps below (Nova also reads inside files there)"
            : (rec.Length > 0 ? rec + "   ·   " : "") + $"⚡ {found.Count:N0} file(s) · {clock.Elapsed.TotalSeconds:0.0} s · server first · double-click = open · right-click = every option · ▦ Full list";
        QuickShow(true);
    }

    public void Quick(string q) { qBox.Text = q ?? ""; qBox.Focus(); qBox.SelectionStart = qBox.Text.Length; if (qBox.Text.Trim().Length >= 2) QuickFind(); }

    public GuidedSearchForm()
    {
        Text = "4.3 🎯 Find a file";
        Width = Math.Max(820, Store.Int("win_guided_w", 980)); Height = Math.Max(560, Store.Int("win_guided_h", 760)); MinimumSize = new Size(640, 460);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14, 10, 14, 4), BackColor = Th.Bg, WrapContents = true };
        top.Controls.Add(Th.L("🎯 Find a file", 13, Th.Text, true));
        backBtn = Th.B("← Back", Back);
        top.Controls.Add(backBtn);
        top.Controls.Add(Th.B("⟲ Start again", () => Go(0)));
        timerBtn = Guided.TimerButton(() => clock, ShowWrapIfUp);
        timerBtn.Visible = true;
        top.Controls.Add(timerBtn);
        top.Controls.Add(Th.B("💬 Free chat", () => Screens.Hub("")));
        top.Controls.Add(Th.B("▦ Full list", () => Screens.FindFileList(qBox.Text.Trim())));
        top.Controls.Add(Th.B("📐 Learn", () => Screens.FilingLearn(project?.Code)));
        top.Controls.Add(Th.B("⚙", () => Screens.Settings("🎯 Guided mode")));
        stopBtn = Th.B("■ Stop", () => { cts?.Cancel(); readCts?.Cancel(); Working(false); note.Text = "■ Stopped — pick again, or ← Back."; });
        stopBtn.Visible = false;
        top.Controls.Add(stopBtn);
        var crumbs = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Th.Bg, Padding = new Padding(16, 2, 16, 2) };
        crumb = new Label { Dock = DockStyle.Top, Height = 26, Font = Th.F(11, true), ForeColor = Th.AccentText, AutoEllipsis = true, UseMnemonic = false };
        note = new Label { Dock = DockStyle.Top, Height = 22, Font = Th.F(9), ForeColor = Th.Muted, AutoEllipsis = true, UseMnemonic = false };
        crumbs.Controls.Add(note); crumbs.Controls.Add(crumb);
        Thinking.Watch(note);   // ⏱ 0:03 while Nova counts, reads or searches
        // 🔎 narrow the options as you type (all of them stay available; nothing is removed)
        crumbs.Height = 90;
        optFilter = Th.Box(false); if (optFilter is SpellBox ofs) ofs.Off = true;
        optFilter.Dock = DockStyle.Top; optFilter.Font = Th.F(10); optFilter.PlaceholderText = "🔎 Filter these options — type part of a name (e.g. 08, door, MEP, RFI)…";
        optFilter.TextChanged += (_, _) => FilterOpts();
        optFilter.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; var first = opts.FirstOrDefault(o => o.B.Visible && !o.B.IsDisposed); first.B?.PerformClick(); } };
        crumbs.Controls.Add(optFilter); crumbs.Controls.SetChildIndex(optFilter, 0);
        // 🔎 the hint box: on every step; Nova searches every choice at once (names + what it already read), the AI only reads the hint
        crumbs.Height = 156;
        var hintRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.FromArgb(0x1B, 0x20, 0x30), Padding = new Padding(8, 4, 8, 4) };
        var hl = Th.L("🔎 What are you looking for?", 10, Th.AccentText, true); hl.Dock = DockStyle.Left; hl.AutoSize = true; hl.Padding = new Padding(0, 4, 8, 0);
        hint = Th.Box(false); if (hint is SpellBox hs) hs.Off = true;
        hint.Dock = DockStyle.Fill; hint.Font = Th.F(11); hint.PlaceholderText = "optional — e.g. baby changing station · fire rated door · 08 71 00 (Nova counts it in every choice below)";
        novaOnly = Th.Check("⚡ Nova only (no AI)", Store.Bool("win_guided_novaonly", false), v => { Store.SetBool("win_guided_novaonly", v); ApplyHint(); });
        novaOnly.Dock = DockStyle.Right;
        hintRow.Controls.Add(hint); hintRow.Controls.Add(novaOnly); hintRow.Controls.Add(hl);
        hintInfo = new Label { Dock = DockStyle.Top, Height = 22, Font = Th.F(9), ForeColor = Th.Muted, AutoEllipsis = true, UseMnemonic = false };
        crumbs.Controls.Add(hintInfo); crumbs.Controls.SetChildIndex(hintInfo, 0);
        crumbs.Controls.Add(hintRow); crumbs.Controls.SetChildIndex(hintRow, 0);
        crumbs.Controls.SetChildIndex(optFilter, 0);
        hint.TextChanged += (_, _) => { hintTimer.Stop(); hintTimer.Start(); };
        hint.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; hintTimer.Stop(); ApplyHint(); } };
        hintTimer.Tick += (_, _) => { hintTimer.Stop(); ApplyHint(); };
        resPanel = new Panel { Dock = DockStyle.Right, Width = 460, BackColor = Color.FromArgb(0x17, 0x1B, 0x26), Padding = new Padding(10, 8, 10, 8), Visible = false };
        resHead = new Label { Dock = DockStyle.Top, Height = 26, Font = Th.F(10.5f, true), ForeColor = Th.Text, Text = "📌 Best results so far", UseMnemonic = false };
        resList = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = resPanel.BackColor };
        resPanel.Controls.Add(resList); resPanel.Controls.Add(resHead);

        findBar = new Panel { Dock = DockStyle.Top, Height = 120, BackColor = Color.FromArgb(0x0E, 0x11, 0x17), Padding = new Padding(16, 8, 16, 6), Visible = false };
        // ✎ A real typing box: starts at the lines of Settings → 🎯 Guided mode, grows while you type; drag the bar under it to make it bigger.
        query = Th.Box(true, TypeBox.Lines);
        query.PlaceholderText = "Number, words or a clause — e.g. 08 71 00 · fire rating · clause 14.2 · SLV-ZUC-AR-MAR-00092   (Enter = find · Shift+Enter = new line)";
        query.Dock = DockStyle.Fill; query.Font = TypeBox.Font; query.ScrollBars = ScrollBars.Vertical;
        query.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Find(); } };
        query.TextChanged += (_, _) => FitFind();
        var fr = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 0) };
        fr.Controls.Add(Th.B("🔎 Find", Find, true));
        latestOnly = Th.Check("Latest revision only", Store.Bool("win_guided_latest", true), v => Store.SetBool("win_guided_latest", v));
        readMore = Th.Check("📖 Also read files not read yet (slower)", false, _ => { });
        fr.Controls.Add(latestOnly); fr.Controls.Add(readMore);
        findBar.Controls.Add(query); findBar.Controls.Add(fr);
        findGrip = new Splitter { Dock = DockStyle.Top, Height = 6, BackColor = Th.Line, MinSize = 70, MinExtra = 120, Cursor = Cursors.HSplit, Visible = false };
        findGrip.SplitterMoved += (_, _) => { dragged = findBar.Height; Store.SetInt("win_guided_inh", dragged); };
        findGrip.DoubleClick += (_, _) => { dragged = 0; Store.SetInt("win_guided_inh", 0); FitFind(); };

        // ⚡ Quick find on top: type a number or words → files at once; empty = the guided steps below
        quick = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(0x14, 0x17, 0x22), Padding = new Padding(14, 6, 14, 4) };
        var qRow = new Panel { Dock = DockStyle.Top, Height = 34 };
        qBox = Th.Box(false); if (qBox is SpellBox qsb) qsb.Off = true;
        qBox.Dock = DockStyle.Fill; qBox.Font = Th.F(11.5f);
        qBox.PlaceholderText = "⚡ Quick find — a number or words (e.g. SLV-ZUC-AR-MAR-00124 · fire door) · Enter · leave empty for the guided steps below";
        var qBtns = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
        qBtns.Controls.Add(Th.B("⚡ Find", QuickFind, true));
        qBtns.Controls.Add(Th.B("▦ Full list", () => Screens.FindFileList(qBox.Text.Trim())));
        qBtns.Controls.Add(Th.B("✕", () => { qBox.Text = ""; QuickShow(false); }));
        qRow.Controls.Add(qBox); qRow.Controls.Add(qBtns);
        qInfo = new Label { Dock = DockStyle.Top, Height = 22, Font = Th.F(9), ForeColor = Th.Muted, AutoEllipsis = true, UseMnemonic = false, Visible = false };
        qList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(9.5f), HeaderStyle = ColumnHeaderStyle.Nonclickable, Visible = false };
        qList.Columns.Add("Source", 150); qList.Columns.Add("File", 360); qList.Columns.Add("Revision", 160); qList.Columns.Add("Folder", 330); qList.Columns.Add("Date", 100);
        qList.DoubleClick += (_, _) => { if (qList.SelectedItems.Count > 0 && qList.SelectedItems[0].Tag is string qp) FileMenu.OpenInNova(qp); };
        qList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && qList.SelectedItems.Count > 0 && qList.SelectedItems[0].Tag is string qp) { e.SuppressKeyPress = true; FileMenu.OpenInNova(qp); } };
        FileMenu.Attach(qList, pt =>
        {
            ListViewItem it = pt is Point sp ? qList.GetItemAt(qList.PointToClient(sp).X, qList.PointToClient(sp).Y) : qList.SelectedItems.Count > 0 ? qList.SelectedItems[0] : null;
            return it?.Tag as string;
        });
        quick.Controls.Add(qList); quick.Controls.Add(qInfo); quick.Controls.Add(qRow);
        qBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; qTimer.Stop(); if (qList.Visible && qList.Items.Count > 0 && e.Control && qList.Items[0].Tag is string fp) FileMenu.OpenInNova(fp); else QuickFind(); }
            else if (e.KeyCode == Keys.Down && qList.Visible && qList.Items.Count > 0) { e.SuppressKeyPress = true; qList.Focus(); qList.Items[0].Selected = true; }
        };
        qBox.TextChanged += (_, _) => { qTimer.Stop(); if (qBox.Text.Trim().Length == 0) QuickShow(false); else if (qBox.Text.Trim().Length >= 3) qTimer.Start(); };
        qTimer.Tick += (_, _) => { qTimer.Stop(); QuickFind(); };

        body = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14, 6, 14, 14), BackColor = Th.Bg };
        Controls.Add(body); Controls.Add(resPanel); Controls.Add(findGrip); Controls.Add(findBar); Controls.Add(crumbs); Controls.Add(quick); Controls.Add(top);
        Resize += (_, _) => resPanel.Width = Math.Max(320, ClientSize.Width * 2 / 5);
        dragged = Store.Int("win_guided_inh", 0);
        findBar.Resize += (_, _) => FitFind();
        Resize += (_, _) => { foreach (Control c in body.Controls) c.Width = RowWidth; };
        ResizeEnd += (_, _) => { if (WindowState == FormWindowState.Normal) { Store.SetInt("win_guided_w", Width); Store.SetInt("win_guided_h", Height); } };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { if (step > 0) Back(); else Close(); return; }
            if (query.Focused || optFilter.Focused || hint.Focused) return;
            var n = e.KeyCode is >= Keys.D1 and <= Keys.D9 ? e.KeyCode - Keys.D0 : e.KeyCode is >= Keys.NumPad1 and <= Keys.NumPad9 ? e.KeyCode - Keys.NumPad0 : 0;
            if (n > 0 && n <= numbered.Count) { e.SuppressKeyPress = true; numbered[n - 1](); }   // type the number = pick it
            if (e.KeyCode == Keys.Back && step > 0) { e.SuppressKeyPress = true; Back(); }
        };
        FormClosed += (_, _) => { cts?.Cancel(); hintCts?.Cancel(); readCts?.Cancel(); hintTimer.Dispose(); };
        Shown += (_, _) => Go(0);
    }

    readonly Splitter findGrip;
    int dragged;
    bool fitting;

    /// <summary>The find bar: the typing box (start lines → grows with the text, or the height you dragged) + the Find row.</summary>
    void FitFind()
    {
        if (fitting || findBar == null) return;
        fitting = true;
        try
        {
            var row = findBar.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
            var rowH = row == null ? 40 : row.GetPreferredSize(new Size(Math.Max(200, findBar.ClientSize.Width - findBar.Padding.Horizontal), 0)).Height + 4;
            var want = findBar.Padding.Vertical + rowH + query.Font.Height * TypeBox.Need(query) + 12;
            var h = Math.Max(want, dragged);
            h = Math.Min(h, Math.Max(want, ClientSize.Height - 220));
            if (findBar.Height != h) findBar.Height = h;
        }
        finally { fitting = false; }
    }

    int RowWidth => Math.Max(420, body.ClientSize.Width - body.Padding.Horizontal - 22);

    // ------------------------------------------------------------------ drawing a step

    void Crumb()
    {
        var parts = new List<string> { "🎯" };
        if (step >= 1) parts.Add(whereLabel);
        if (step >= 2) parts.Add(whatLabel);
        if (step >= 3 && narrowLabel.Length > 0) parts.Add(narrowLabel);
        var names = new[] { "1 Where", "2 What", "3 Narrow", "4 Find" };
        crumb.Text = string.Join("  ›  ", parts) + (step < 4 ? "  ›  " + names[Math.Min(step, 3)] : "");
        backBtn.Enabled = step > 0;
    }

    void Clear(string hint)
    {
        body.SuspendLayout();
        foreach (Control c in body.Controls.Cast<Control>().ToList()) c.Dispose();
        body.Controls.Clear();
        numbered.Clear(); opts.Clear(); optSets.Clear(); optBase.Clear();
        if (optFilter != null && optFilter.Text.Length > 0) optFilter.Text = "";
        note.Text = hint;
        Crumb();
        body.ResumeLayout();
    }

    Label Head(string t)
    {
        var l = Th.L(t, 10.5f, Th.Text, true); l.Margin = new Padding(0, 8, 0, 6); body.Controls.Add(l); return l;
    }

    /// <summary>A numbered option: click it, or type its number.</summary>
    Button Opt(string title, string sub, Action pick, List<Projects.FileRow> files = null)
    {
        var n = numbered.Count + 1;
        numbered.Add(pick);
        var text = $"{n}   {title}" + (sub.Length > 0 ? "   ·   " + sub : "");
        var b = Th.B(text, pick);
        b.AutoSize = false; b.Width = RowWidth; b.Height = 34; b.TextAlign = ContentAlignment.MiddleLeft; b.Font = Th.F(10);   // compact: 20+ choices fit
        b.Margin = new Padding(0, 0, 0, 3);
        body.Controls.Add(b);
        opts.Add((b, title + " " + sub));
        optSets.Add(files); optBase.Add(text);
        return b;
    }

    void FilterOpts()
    {
        var f = optFilter.Text.Trim();
        body.SuspendLayout();
        foreach (var (b, t) in opts) if (!b.IsDisposed) b.Visible = f.Length == 0 || t.Contains(f, StringComparison.OrdinalIgnoreCase);
        body.ResumeLayout();
    }

    void Go(int to)
    {
        cts?.Cancel();
        step = to;
        findBar.Visible = step == 3; findGrip.Visible = step == 3;
        optFilter.Visible = step < 3; Working(false);
        if (step == 3) FitFind();
        if (step == 0) Where();
        else if (step == 1) What();
        else if (step == 2) Narrow();
        else
        {
            Clear($"{inNarrow.Count:N0} files in this area · type a number, words or a clause, then Enter. Names first, then inside the text.");
            query.Focus();
            if (query.Text.Trim().Length == 0 && hint.Text.Trim().Length > 1) { query.Text = hint.Text.Trim(); Find(); return; }
            NextOptions(null);
            ApplyHint();
        }
    }

    void Back()
    {
        if (step == 3 && cat?.Key == "mail") { Go(1); return; }
        if (step == 3 && narrowLabel.Length == 0) { Go(1); return; }
        Go(Math.Max(0, step - 1));
    }

    // ------------------------------------------------------------------ 1 · where

    async void Where()
    {
        Clear("Pick the project — type its number, or click. Only Nova's own index is used: nothing leaves your PC.");
        clock.Go();
        var all = Projects.All().OrderBy(p => p.Order).ToList();
        if (all.Count == 0 && Projects.General.Count == 0)
        {
            Head("No project folders yet");
            Opt("📁 Set up my projects", "Settings → 📁 Projects: your work folder, read by name", () => Screens.Settings("📁 Projects"));
            return;
        }
        Head("1 · Where is it?");
        var my = cts = new CancellationTokenSource();
        Working(true);
        var counts = await Task.Run(() => all.Select(p => Projects.CountUnder(p.Folders)).ToList());
        if (IsDisposed) return; Working(false); if (my.IsCancellationRequested) return;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i]; var c = counts[i];
            Opt($"{p.Code}{(p.Name.Length > 0 ? " · " + p.Name : "")}", c > 0 ? $"{c:N0} files listed" : "not listed yet — Settings → 📁 Projects", () =>
            {
                project = p; folders = p.Folders.ToList(); whereLabel = p.Code; Go(1);
            });
        }
        if (Projects.General.Count > 0)
            Opt("📚 General reference (standards, codes, templates)", string.Join(" · ", Projects.General.Select(Path.GetFileName)), () => { project = null; folders = Projects.General.ToList(); whereLabel = "General"; Go(1); });
        if (all.Count > 1)
            Opt("🗂 All my projects", "slower — better to pick one", () => { project = null; folders = all.SelectMany(p => p.Folders).Concat(Projects.General).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); whereLabel = "All projects"; Go(1); });
    }

    // ------------------------------------------------------------------ 2 · what

    async void What()
    {
        Clear("What are you looking for? Nova counts the files of each kind in " + whereLabel + "…");
        Head("2 · What is it?");
        var my = cts = new CancellationTokenSource();
        Working(true);
        var files = await Task.Run(() => Projects.Catalogue(folders));
        if (IsDisposed) return; Working(false); if (my.IsCancellationRequested) return;
        note.Text = $"{files.Count:N0} files in {whereLabel} · pick the kind (type its number).";
        // 📐 Each file's kind comes from the filing concept (🧠 Nova Brain codes: number → folders → words); the folder words only when it has none.
        note.Text = $"📐 Nova labels {files.Count:N0} files (kind, source, discipline, stage)…";
        Working(true);
        var groups = await Task.Run(() =>
        {
            // 📐 from the shared Nova index: worked out once, saved — only new or changed files are labelled now
            Dictionary<string, FileLabels> known;
            try { known = NovaIndex.Labels(folders, files, my.Token, s => { try { BeginInvoke(new Action(() => { if (!note.IsDisposed) note.Text = s; })); } catch { } }); }
            catch (OperationCanceledException) { known = new(StringComparer.OrdinalIgnoreCase); }
            labels = new Dictionary<string, FileLabels>(known, StringComparer.OrdinalIgnoreCase);
            var res = Cats.ToDictionary(c => c.Key, _ => new List<Projects.FileRow>());
            foreach (var f in files)
            {
                if (my.IsCancellationRequested) break;
                if (!labels.TryGetValue(f.Path, out var lb)) labels[f.Path] = lb = Filing.Classify(f.Path);
                var k = lb.Kind.Length > 0 && KindCat.TryGetValue(lb.Kind, out var kc) ? kc : null;
                if (k != null) res[k].Add(f);
                else foreach (var c in Cats) if (c.Match != null && c.Key != "all" && c.Match.IsMatch(Rel(f.Path))) { res[c.Key].Add(f); break; }
                res["all"].Add(f);
            }
            return res;
        });
        note.Text = $"{files.Count:N0} files in {whereLabel} · pick the kind (type its number).";
        if (IsDisposed) return; Working(false); if (my.IsCancellationRequested) return;
        body.SuspendLayout();
        foreach (var c in Cats)
        {
            var list = groups[c.Key];
            if (c.Key == "mail") { Opt($"{c.Icon} {c.Label}", "search your emails of " + whereLabel, () => { cat = c; whatLabel = c.Label; inNarrow = new(); narrowLabel = ""; Go(3); }); continue; }
            var bySrc = list.Select(x => labels.TryGetValue(x.Path, out var l) ? l.Source : "").Where(x => x.Length > 0).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Count():N0} {g.Key.ToLowerInvariant()}");
            var ob = Opt($"{c.Icon} {c.Label}", $"{list.Count:N0} files" + (bySrc.Any() ? " · " + string.Join(" · ", bySrc) : ""), () =>
            {
                cat = c; whatLabel = c.Label; inWhat = list;
                if (list.Count <= 40 || c.Key == "all" && list.Count <= 80) { inNarrow = list; narrowLabel = ""; Go(3); }
                else Go(2);
            }, list);
            if (list.Count == 0) ob.ForeColor = Th.Muted;   // ☑ always offered, greyed when this area has none
        }
        body.ResumeLayout();
        ApplyHint();
    }

    /// <summary>The path inside the chosen folders (the project folder itself is not a hint).</summary>
    string Rel(string path)
    {
        foreach (var f in folders)
        {
            var pre = f.TrimEnd('\\') + "\\";
            if (path.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return path[pre.Length..];
        }
        return path;
    }

    // ------------------------------------------------------------------ 3 · narrow

    async void Narrow()
    {
        Clear($"{inWhat.Count:N0} files · narrow it down (type its number).");
        Head("3 · Narrow it");
        var my = cts = new CancellationTokenSource();
        Working(true);
        var groups = await Task.Run(() => Groups(cat.Key, inWhat));
        if (IsDisposed) return; Working(false); if (my.IsCancellationRequested) return;
        body.SuspendLayout();
        Opt($"All of them", $"{inWhat.Count:N0} files", () => { inNarrow = inWhat; narrowLabel = "all"; Go(3); }, inWhat);
        foreach (var (label, list) in groups)
        {
            var l = list;
            var b = Opt(label, $"{l.Count:N0} files", () => { inNarrow = l; narrowLabel = label; Go(3); }, l);
            if (l.Count == 0) b.ForeColor = Th.Muted;
        }
        body.ResumeLayout();
        ApplyHint();
    }

    /// <summary>📐 Filing kind → the "What" choice.</summary>
    static readonly Dictionary<string, string> KindCat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Contract document"] = "contract", ["Specification"] = "spec", ["Design drawing"] = "dwg", ["Model / CAD"] = "dwg", ["Sketch"] = "dwg",
        ["Shop drawing"] = "sub", ["Material submittal"] = "sub", ["Prequalification"] = "sub", ["Request for information (RFI)"] = "sub", ["Inspection request"] = "sub",
        ["Site MEP inspection clearance"] = "sub", ["Method statement"] = "sub", ["Non-conformance (NCR)"] = "sub", ["Site observation report (SOR)"] = "sub", ["Transmittal"] = "sub",
        ["Minutes"] = "mom", ["BOQ"] = "boq", ["Variation"] = "boq", ["Payment"] = "boq", ["Letter"] = "ltr", ["Engineer's instruction"] = "ltr", ["Site instruction"] = "ltr",
    };
    Dictionary<string, FileLabels> labels = new(StringComparer.OrdinalIgnoreCase);
    FileLabels Lab(Projects.FileRow f) => labels.TryGetValue(f.Path, out var l) ? l : labels[f.Path] = Filing.Classify(f.Path);

    List<(string Label, List<Projects.FileRow> Files)> Groups(string key, List<Projects.FileRow> files)
    {
        var res = new Dictionary<string, List<Projects.FileRow>>();
        void Add(string k, Projects.FileRow f) { if (!res.TryGetValue(k, out var l)) res[k] = l = new(); l.Add(f); }
        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f.Name);
            string g = null;
            var lb = Lab(f);
            // 📐 drawings: by discipline (+ stage when the set has one) · submittals and letters: by kind · contract: by stage
            if (key == "dwg" && lb.Disc.Length > 0) g = lb.Disc;
            else if (key is "sub" or "ltr" && lb.Kind.Length > 0) g = lb.Kind;
            else if (key == "contract" && lb.Stage.Length > 0) g = "Stage " + lb.Stage;
            if (g != null) { Add(g, f); continue; }
            if (key == "spec")
            {
                var m = Regex.Match(name, @"(?<!\d)(\d{2})[ _\-]?(\d{2})[ _\-]?(\d{2})(?!\d)");
                if (m.Success && Divisions.TryGetValue(m.Groups[1].Value, out var dv)) g = $"Division {m.Groups[1].Value} · {dv}";
            }
            else if (key == "dwg")
            {
                var t = Regex.Split(name, @"[-_ .]+").Where(x => x.Length > 0).ToList();
                for (int i = 0; i < t.Count && g == null; i++)
                    if (Disciplines.TryGetValue(t[i], out var d) && (t[i].Length > 1 || i + 1 < t.Count && char.IsDigit(t[i + 1][0]))) g = d;
                if (g == null && Cad.Contains(Path.GetExtension(f.Name).ToLowerInvariant())) g = "CAD files";
            }
            else if (key == "sub")
            {
                var rel = Rel(f.Path);
                g = SubTypes.FirstOrDefault(s => s.Rx.IsMatch(rel)).Label;
            }
            g ??= FirstFolder(f.Path);
            Add(g, f);
        }
        // ☑ Every division / discipline / type is offered, even with 0 files (greyed); folders: the 20 biggest.
        var known = key switch
        {
            "spec" => Divisions.Select(kv => $"Division {kv.Key} · {kv.Value}").ToList(),
            "dwg" => Filing.Disciplines.Append("CAD files").ToList(),
            "sub" or "ltr" => KindCat.Where(kv => kv.Value == key).Select(kv => kv.Key).ToList(),
            _ => new List<string>(),
        };
        foreach (var k in known) if (!res.ContainsKey(k)) res[k] = new();
        var named = res.Where(kv => known.Contains(kv.Key)).OrderBy(kv => key == "spec" ? kv.Key : "").ThenByDescending(kv => kv.Value.Count).ToList();
        var other = res.Where(kv => !known.Contains(kv.Key) && kv.Value.Count > 0).OrderByDescending(kv => kv.Value.Count).Take(20).ToList();
        return named.Concat(other).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    string FirstFolder(string path)
    {
        var rel = Rel(path);
        var parts = rel.Split('\\');
        return parts.Length > 1 ? "📁 " + parts[0] + (parts.Length > 2 ? " › " + parts[1] : "") : "📁 (top folder)";
    }

    // ------------------------------------------------------------------ 4 · find

    record Hit(string Path, string Name, string Rev, string Status, double Score, DateTime Modified);
    record Clause(string Path, Seg Seg, int Score, List<(int, int)> Marks);

    static readonly Regex ClauseNo = new(@"^(?:(?:sub-?)?clause|cl\.?|section|art(?:icle)?\.?|§)?\s*(\d{1,3}(?:\.\d{1,3}){1,4})\s*$", RegexOptions.IgnoreCase);

    async void Find()
    {
        var q = query.Text.Trim();
        if (cat?.Key == "mail")
        {
            if (q.Length == 0) { note.Text = "Type what the email is about, then Enter."; return; }
            Screens.AskFor((project != null ? project.Code + " " : "") + q);
            NextOptions(null);
            return;
        }
        Clear($"🔎 Finding “{q}” in {inNarrow.Count:N0} files…");
        Head("4 · Found");
        var my = cts = new CancellationTokenSource();
        Working(true);
        var latest = latestOnly.Checked; var more = readMore.Checked;
        var set = inNarrow;
        var ui = SynchronizationContext.Current;
        var (hits, clauses, read) = await Task.Run(() => Search(set, q, latest, more, msg => ui?.Post(_ => { if (!note.IsDisposed) note.Text = msg; }, null), my.Token));
        if (IsDisposed) return; Working(false); if (my.IsCancellationRequested) return;
        body.SuspendLayout();
        foreach (Control c in body.Controls.Cast<Control>().ToList()) c.Dispose();
        body.Controls.Clear(); numbered.Clear(); opts.Clear();
        note.Text = $"{hits.Count} file(s) by name · {clauses.Count} passage(s) inside the text · {read} file(s) searched inside · ⚡ on this PC, no AI" + (q.Length == 0 ? "" : $" · “{q}”");
        if (hits.Count == 0 && clauses.Count == 0)
        {
            Head("Nothing found here");
            var tip = Th.Wrap("Try fewer words, the number only, or go ← Back and pick a wider area. Tick 📖 to read the files not read yet.", 9.5f, Th.Muted);
            tip.MaximumSize = new Size(RowWidth, 0);
            body.Controls.Add(tip);
        }
        if (clauses.Count > 0)
        {
            Head($"§ Inside the text ({clauses.Count})");
            foreach (var c in clauses.Take(12)) ClauseCard(c, q);
        }
        if (hits.Count > 0)
        {
            Head($"📄 By name ({hits.Count})");
            foreach (var h in hits.Take(30)) FileCard(h);
        }
        body.ResumeLayout();
        NextOptions(hits.FirstOrDefault()?.Path ?? clauses.FirstOrDefault()?.Path);
    }

    static List<string> Words(string q) =>
        Regex.Split(q.ToLowerInvariant(), @"[^\p{L}\p{N}\-\.]+").Select(w => w.Trim('.', '-')).Where(w => w.Length >= 2 && !Regex.IsMatch(w, "^(the|and|for|with|of|in|on|to|clause|section|cl|article)$")).Distinct().Take(10).ToList();

    (List<Hit>, List<Clause>, int) Search(List<Projects.FileRow> set, string q, bool latest, bool readNew, Action<string> status, CancellationToken ct)
    {
        var words = Words(q);
        var cm = ClauseNo.Match(q);
        Regex clauseRx = cm.Success ? new Regex(@"(?<![\d.])" + Regex.Escape(cm.Groups[1].Value) + @"(?![\d])") : null;
        if (clauseRx != null) words = new List<string>();
        var num = Revisions.Number(q);
        // 📄 by name
        var hits = new List<Hit>();
        foreach (var f in set)
        {
            var name = f.Name.ToLowerInvariant();
            var rel = Rel(f.Path).ToLowerInvariant();
            double s = 0;
            var numHit = num.Length > 0 && name.Contains(num.ToLowerInvariant());
            if (numHit) s += 10;
            foreach (var w in words) if (name.Contains(w)) s += 3 + Math.Min(w.Length, 12) / 6.0; else if (rel.Contains(w)) s += 1;
            var inPath = words.Count(w => rel.Contains(w));
            if (q.Length == 0) s = 1;
            else if (!numHit && inPath < Math.Min(2, Math.Max(1, words.Count))) continue;   // most of the words must be in its name or folders
            var ext = Path.GetExtension(f.Name).ToLowerInvariant();
            s += ext == ".pdf" ? 2 : ext is ".docx" or ".doc" ? 1.5 : Cad.Contains(ext) ? -3 : 0;
            hits.Add(new Hit(f.Path, f.Name, "", "", s, f.Modified));
        }
        // revisions: only the latest of each document is current
        var withRev = new List<Hit>();
        foreach (var g in hits.GroupBy(h => (Revisions.Parse(h.Path, 0).Key, Path.GetExtension(h.Path).ToLowerInvariant())))
        {
            var docs = g.Select(h => (h, d: Revisions.Parse(h.Path, h.Modified.Ticks))).ToList();
            var best = docs.OrderByDescending(x => x.d.Rank.Item1).ThenByDescending(x => x.d.Rank.Item2, StringComparer.Ordinal).ThenByDescending(x => x.d.MTime).First();
            foreach (var (h, d) in docs)
            {
                var st = docs.Count < 2 ? "" : h == best.h ? "latest" : "superseded";
                if (latest && st == "superseded") continue;
                withRev.Add(h with { Rev = d.Rev, Status = st, Score = h.Score - (st == "superseded" ? 4 : 0) });
            }
        }
        hits = withRev.OrderByDescending(h => h.Score).ThenByDescending(h => h.Modified).ToList();

        // § inside the text: files Nova already read (instant), then — if asked or the area is small — the best files by name.
        var clauses = new List<Clause>();
        int read = 0;
        if (words.Count > 0 || clauseRx != null)
        {
            var candidates = set.Where(f => !Cad.Contains(Path.GetExtension(f.Name).ToLowerInvariant())).ToList();
            var order = hits.Select(h => h.Path).Concat(candidates.Select(c => c.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(1500).ToList();
            var toRead = readNew || set.Count <= 25 ? 12 : 0;
            foreach (var path in order)
            {
                ct.ThrowIfCancellationRequested();
                List<Seg> segs = null;
                try { segs = FolderIndex.Known(path); } catch { }
                if (segs == null && toRead > 0)
                {
                    toRead--;
                    status?.Invoke("📖 Reading " + Path.GetFileName(path) + "…");
                    try { segs = Projects.Read(path, ct).Segs; } catch (OperationCanceledException) { throw; } catch { }
                }
                if (segs == null) continue;
                read++;
                foreach (var s in segs)
                {
                    var t = s.Text ?? "";
                    var marks = new List<(int, int)>();
                    int sc = 0;
                    if (clauseRx != null)
                    {
                        foreach (Match m in clauseRx.Matches(t)) { marks.Add((m.Index, m.Length)); sc += m.Index < 8 || t.LastIndexOf('\n', Math.Max(0, m.Index - 1)) == m.Index - 1 ? 6 : 2; }
                    }
                    else
                        foreach (var w in words)
                        {
                            var i = t.IndexOf(w, StringComparison.OrdinalIgnoreCase);
                            if (i >= 0) { sc += 2; marks.Add((i, w.Length)); }
                        }
                    if (sc == 0 || clauseRx == null && words.Count > 1 && marks.Count < Math.Min(2, words.Count)) continue;
                    clauses.Add(new Clause(path, s, sc + marks.Count, marks));
                }
                if (clauses.Count > 400) break;
            }
            status?.Invoke($"§ {clauses.Count} passage(s) found");
        }
        clauses = clauses.OrderByDescending(c => c.Score).ThenBy(c => hits.FindIndex(h => h.Path.Equals(c.Path, StringComparison.OrdinalIgnoreCase)) is var k && k >= 0 ? k : 9999).ToList();
        return (hits, clauses, read);
    }

    Panel Card(int height)
    {
        var p = new Panel { Width = RowWidth, Height = height, BackColor = Th.Card, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(10, 6, 10, 6) };
        body.Controls.Add(p);
        return p;
    }

    void FileCard(Hit h)
    {
        var p = Card(72);
        var t = Th.L($"📄 {h.Name}" + (h.Rev.Length > 0 ? $"  · rev {h.Rev}" : "") + (h.Status == "latest" ? "  ✓ latest" : h.Status == "superseded" ? "  ⚠ superseded" : ""), 10, h.Status == "superseded" ? Th.Warn : Th.Text, true);
        t.Location = new Point(10, 6); t.AutoSize = false; t.Width = p.Width - 20; t.AutoEllipsis = true; t.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        var lb = labels.TryGetValue(h.Path, out var l0) ? l0 : Filing.Classify(h.Path);
        var tags = string.Join(" · ", new[] { lb.Kind, lb.Source, lb.Disc, lb.Stage, lb.Code, lb.State }.Where(x => x.Length > 0));
        var f = Th.L((tags.Length > 0 ? "📐 " + tags + "  ·  " : "") + $"{Path.GetDirectoryName(Rel(h.Path))} · {(h.Modified > DateTime.MinValue ? h.Modified.ToString("dd MMM yyyy") : "")}", 8.5f, Th.Muted);
        if (lb.Why.Count > 0) { var tip = new ToolTip { AutoPopDelay = 20000 }; tip.SetToolTip(t, "📐 Why:\n" + lb.WhyText); tip.SetToolTip(f, "📐 Why:\n" + lb.WhyText); }
        f.Location = new Point(10, 26); f.AutoSize = false; f.Width = p.Width - 20; f.AutoEllipsis = true; f.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        var bt = new FlowLayoutPanel { Location = new Point(6, 40), AutoSize = true, BackColor = Color.Transparent };
        bt.Controls.Add(Th.B("Open", () => Open(h.Path, null, "")));
        bt.Controls.Add(Th.B("📂 Folder", () => Shell.Select(h.Path)));
        bt.Controls.Add(Th.B("✦ Ask about it", () => Screens.HubAttach(h.Path)));
        bt.Controls.Add(FileMenu.Dots(() => h.Path));
        foreach (Control c in bt.Controls) { c.MinimumSize = new Size(0, 26); c.Font = Th.F(8.5f, true); c.Margin = new Padding(0, 0, 6, 0); }
        p.Controls.AddRange(new Control[] { t, f, bt });
        foreach (var cc in new Control[] { p, t, f }) FileMenu.Attach(cc, _ => h.Path);
    }

    void ClauseCard(Clause c, string q)
    {
        var p = Card(98);
        var t = Th.L($"§ {Path.GetFileName(c.Path)} · {c.Seg.Loc}", 10, Th.AccentText, true);
        t.Location = new Point(10, 6); t.AutoSize = false; t.Width = p.Width - 20; t.AutoEllipsis = true; t.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        var text = (c.Seg.Text ?? "").Replace("\r", " ").Replace("\n", " ");
        var at = c.Marks.Count > 0 ? Math.Max(0, c.Marks.Min(m => m.Item1) - 60) : 0;
        var snip = (at > 0 ? "…" : "") + text.Substring(Math.Min(at, text.Length), Math.Min(260, Math.Max(0, text.Length - at))) + (text.Length - at > 260 ? "…" : "");
        var s = Th.L(snip, 9, Th.Text); s.Location = new Point(10, 26); s.AutoSize = false; s.Width = p.Width - 20; s.Height = 36; s.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        var bt = new FlowLayoutPanel { Location = new Point(6, 64), AutoSize = true, BackColor = Color.Transparent };
        bt.Controls.Add(Th.B("📖 Open at this clause", () => Open(c.Path, c.Seg, q), true));
        bt.Controls.Add(Th.B("⧉ Copy the text", () => { try { Clipboard.SetText(c.Seg.Text ?? " "); note.Text = "⧉ Copied"; } catch { } }));
        bt.Controls.Add(Th.B("✦ Ask about it", () => Screens.HubAttach(c.Path)));
        var fat = new FileMenu.At { Loc = c.Seg.Loc ?? "", Goto = c.Seg.Goto ?? "", Text = c.Seg.Text ?? "", Term = q };
        bt.Controls.Add(FileMenu.Dots(() => c.Path, fat));
        foreach (Control x in bt.Controls) { x.MinimumSize = new Size(0, 26); x.Font = Th.F(8.5f, true); x.Margin = new Padding(0, 0, 6, 0); }
        p.Controls.AddRange(new Control[] { t, s, bt });
        foreach (var cc in new Control[] { p, t, s }) FileMenu.Attach(cc, _ => c.Path, _ => fat);
    }

    void Open(string path, Seg seg, string term)
    {
        try
        {
            var marks = new List<(int Index, int Length)>();
            Opener.Open(new FileHit { Path = path }, new PieceHit { Loc = seg?.Loc ?? "", Goto = seg?.Goto ?? "", Text = seg?.Text ?? "", Matches = marks }, term);
        }
        catch (Exception e) { note.Text = "⚠ " + e.Message; }
    }

    /// <summary>▶ Next: numbered options after the results.</summary>
    void NextOptions(string best)
    {
        if (step != 3) return;
        Head("▶ Next");
        if (best != null) Opt("Open the best match", Path.GetFileName(best), () => Open(best, null, query.Text));
        if (best != null) Opt("✦ Ask Nova about the best match", "opens 💬 Nova Chat with this file", () => Screens.HubAttach(best));
        if (cat?.Key != "mail" && !readMore.Checked) Opt("📖 Search again, reading the files not read yet", "slower, but goes deeper", () => { readMore.Checked = true; Find(); });
        if (narrowLabel.Length > 0) Opt("Narrow differently", "back to 3 · Narrow", () => Go(2));
        Opt("Look for another kind of document", "back to 2 · What", () => Go(1));
        Opt("Another project", "back to 1 · Where", () => Go(0));
    }

    void ShowWrapIfUp()
    {
        if (!clock.Up || IsDisposed) return;
        Head("⏱ Time is up");
        Opt("+10 min — keep going", "", () => { clock.Add(10); timerBtn.Text = clock.Label; Go(step); });
        Opt("📋 Copy my trail (where I searched and what I found)", "", () =>
        {
            try { Clipboard.SetText(crumb.Text + "\n" + note.Text + (query.Text.Length > 0 ? "\nSearched: " + query.Text : "")); note.Text = "⧉ Copied"; } catch { }
        });
        Opt("End here", "closes this window", () => { clock.Ended = true; Close(); });
        body.ScrollControlIntoView(body.Controls[body.Controls.Count - 1]);
    }

    // ------------------------------------------------------------------ 🔎 the hint: every choice searched at once, while you choose

    record HintRes(Dictionary<int, int> Counts, List<(string Path, string Name, string Loc, string Goto, string Text, string Rev, string Tag)> Best, List<string> Unread, int Hits, int Choices);
    static readonly string[] ReadableExt = { ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".xlsm", ".pptx", ".msg", ".rtf", ".txt" };

    async void ApplyHint()
    {
        if (hint == null || IsDisposed) return;
        hintCts?.Cancel();
        var q = hint.Text.Trim();
        if (q.Length < 2)
        {
            for (int i = 0; i < opts.Count && i < optBase.Count; i++) if (!opts[i].B.IsDisposed) opts[i].B.Text = optBase[i];
            hintInfo.Text = ""; resPanel.Visible = false;
            return;
        }
        if (step == 0) { hintInfo.Text = $"✓ Pick the project — Nova then counts “{q}” in every choice."; resPanel.Visible = false; return; }
        var my = hintCts = new CancellationTokenSource();
        var sets = optSets.ToList(); var buttons = opts.Select(o => o.B).ToList(); var bases = optBase.ToList();
        var scope = step == 3 ? inNarrow.ToList()
            : sets.Where(x => x != null).SelectMany(x => x).GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var fold = folders.ToList();
        var words = Words(q);
        var t0 = DateTime.Now;
        hintInfo.Text = "⏳ Nova searches every choice for “" + q + "”…";
        // ✦ the AI reads the HINT only (never your files), once per hint — other words your documents may use
        var extra = new List<string>(); var shown = "";
        var useAi = !novaOnly.Checked && Ai.Active() != null && !Ai.QuotaOut && FolderAsk.Sees != "never" && !Projects.Private.IsMatch(q);
        if (useAi)
        {
            bool have;
            lock (aiWords) { have = aiWords.TryGetValue(q, out var c0); if (have) { extra = c0.Words; shown = c0.Shown; } }
            if (!have)
            {
                try
                {
                    var sys = "A construction project's document search. Give the other words and codes the project documents may use for what the user looks for " +
                              "(synonyms, abbreviations, spec section numbers like 10 28 00, typical document words) and the likely disciplines. Reply JSON only: {\"words\":[\"…\"],\"disciplines\":[\"…\"]} — at most 8 words.";
                    var j = Ai.ParseJson(await SmartSearch.CloudChat(sys, q, 8, my.Token));
                    extra = (j["words"] as System.Text.Json.Nodes.JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 1 && !Projects.Private.IsMatch(x)).Take(8).ToList() ?? new();
                    var disc = (j["disciplines"] as System.Text.Json.Nodes.JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).Take(3).ToList() ?? new();
                    shown = string.Join(" · ", extra) + (disc.Count > 0 ? " · disciplines: " + string.Join(", ", disc) : "");
                    lock (aiWords) aiWords[q] = (extra, shown);
                }
                catch (Exception e) { shown = Ai.QuotaOut ? "💳 AI quota used up — Nova only" : ""; if (!(e is OperationCanceledException)) Log.Warn("guided hint", e.Message); }
            }
        }
        if (my.IsCancellationRequested || IsDisposed) return;
        HintRes r;
        try { r = await Task.Run(() => HintCount(q, scope, sets, fold, words, extra, my.Token)); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { hintInfo.Text = "⚠ " + e.Message; return; }
        if (my.IsCancellationRequested || IsDisposed) return;

        // the choices: their hits, the best first (the first “All of them” stays on top), 0 = faded
        body.SuspendLayout();
        var withSets = Enumerable.Range(0, Math.Min(buttons.Count, sets.Count)).Where(i => sets[i] != null && !buttons[i].IsDisposed).ToList();
        int maxN = r.Counts.Count == 0 ? 0 : withSets.Where(i => !bases[i].Contains("All of them")).Select(i => r.Counts.GetValueOrDefault(i)).DefaultIfEmpty(0).Max();
        foreach (var i in withSets)
        {
            var n = r.Counts.GetValueOrDefault(i);
            buttons[i].Text = bases[i] + $"   ·   {(n > 0 ? n.ToString("N0") + (n == 1 ? " hit" : " hits") : "0")}{(n > 0 && n == maxN && !bases[i].Contains("All of them") ? "  ⭐" : "")}";
            buttons[i].ForeColor = n > 0 ? Th.Text : Th.Muted;
        }
        var movable = withSets.Where(i => !(step == 2 && bases[i].Contains("All of them"))).ToList();
        var slots = movable.Select(i => body.Controls.GetChildIndex(buttons[i])).OrderBy(x => x).ToList();
        var sorted = movable.OrderByDescending(i => r.Counts.GetValueOrDefault(i)).ThenBy(i => i).ToList();
        for (int k = 0; k < sorted.Count; k++) body.Controls.SetChildIndex(buttons[sorted[k]], slots[k]);
        body.ResumeLayout();

        hintInfo.Text = $"✓ {r.Hits:N0} hit(s) · searched {(r.Choices > 0 ? r.Choices + " choices" : scope.Count.ToString("N0") + " files")} at once · {(DateTime.Now - t0).TotalSeconds:0.0} s" +
                        (shown.Length > 0 ? "   ·   ✦ AI understood (the hint only, no files): " + shown : "") +
                        (r.Unread.Count > 0 ? $"   ·   📖 {r.Unread.Count:N0} files here are not read inside yet" : "");
        // 📌 best results so far
        resPanel.Visible = true;
        resList.SuspendLayout();
        foreach (Control c in resList.Controls.Cast<Control>().ToList()) c.Dispose();
        resList.Controls.Clear();
        resHead.Text = $"📌 Best results so far · {r.Hits:N0} hit(s) · newest revision first";
        var w = Math.Max(260, resPanel.ClientSize.Width - 34);
        foreach (var b in r.Best)
        {
            var t = Th.L($"{b.Name}{(b.Rev.Length > 0 ? " · " + b.Rev : "")}", 9.5f, Th.Text, true); t.MaximumSize = new Size(w, 0); t.Cursor = Cursors.Hand;
            var sub = Th.L((b.Tag.Length > 0 ? b.Tag + "  ·  " : "") + (b.Text.Length > 0 ? $"📄 {b.Loc} “{b.Text}”" : "📛 by its name"), 8.5f, Th.Muted); sub.MaximumSize = new Size(w, 0); sub.Margin = new Padding(3, 0, 3, 8); sub.Cursor = Cursors.Hand;
            var bb = b;
            void Go_() => Open(bb.Path, bb.Text.Length > 0 ? new Seg(bb.Loc, bb.Text, bb.Goto) : null, q);
            t.Click += (_, _) => Go_(); sub.Click += (_, _) => Go_();
            var bat = new FileMenu.At { Loc = bb.Loc, Goto = bb.Goto, Text = bb.Text, Term = q };
            FileMenu.Attach(t, _ => bb.Path, _ => bat); FileMenu.Attach(sub, _ => bb.Path, _ => bat);
            resList.Controls.Add(t); resList.Controls.Add(sub);
        }
        if (r.Best.Count == 0) resList.Controls.Add(Th.Wrap("Nothing yet in the names and the files already read.", 9.5f, Th.Muted));
        if (r.Unread.Count > 0)
        {
            var note2 = Th.Wrap($"📖 {r.Unread.Count:N0} files of this choice are not read inside yet (names only). Read them now so nothing is missed? Scanned PDFs: Nova gives up after 40 s without progress.", 9, Th.Warn);
            note2.MaximumSize = new Size(w, 0);
            resList.Controls.Add(note2);
            var unread = r.Unread;
            resList.Controls.Add(Th.B($"📖 Read these {unread.Count:N0} now", () => ReadNow(unread), true));
        }
        resList.ResumeLayout();
    }

    HintRes HintCount(string q, List<Projects.FileRow> scope, List<List<Projects.FileRow>> sets, List<string> fold, List<string> words, List<string> extra, CancellationToken ct)
    {
        var inScope = new HashSet<string>(scope.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        // inside: one full-text query per folder (what Nova already read) — the hint and the AI's words
        var inside = new Dictionary<string, FolderIndex.Passage>(StringComparer.OrdinalIgnoreCase);
        var terms = words.Concat(extra.SelectMany(e => e.Contains(' ') ? new[] { e } : new[] { e })).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in fold)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var p in FolderIndex.Retrieve(f, terms, 600, 1))
                    if ((inScope.Count == 0 || inScope.Contains(p.Path)) && !Projects.Private.IsMatch(p.Path) && (!inside.TryGetValue(p.Path, out var o) || o.Score < p.Score)) inside[p.Path] = p;
            }
            catch { }
        }
        var num = Revisions.Number(q);
        bool NameHit(Projects.FileRow f)
        {
            var name = f.Name.ToLowerInvariant(); var rel = Rel(f.Path).ToLowerInvariant();
            if (num.Length > 0 && name.Contains(num.ToLowerInvariant())) return true;
            if (words.Count > 0 && words.Count(w => name.Contains(w) || rel.Contains(w)) >= Math.Min(2, words.Count) && words.Any(w => name.Contains(w))) return true;
            return extra.Any(e => e.Length >= 3 && name.Contains(e.ToLowerInvariant()));
        }
        var hitSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in scope) { ct.ThrowIfCancellationRequested(); if (inside.ContainsKey(f.Path) || NameHit(f)) hitSet.Add(f.Path); }
        var counts = new Dictionary<int, int>();
        int choices = 0;
        for (int i = 0; i < sets.Count; i++) if (sets[i] != null) { counts[i] = sets[i].Count(f => hitSet.Contains(f.Path)); choices++; }
        // the best: inside passages first, then names; only the latest revision of each document
        var rows = scope.Where(f => hitSet.Contains(f.Path)).Select(f =>
        {
            inside.TryGetValue(f.Path, out var p);
            var d = Revisions.Parse(f.Path, f.Modified.Ticks);
            return (f, p, d, score: (p?.Score ?? 0) + (NameHit(f) ? 5 : 0));
        }).ToList();
        var latest = rows.GroupBy(x => x.d.Key).Select(g => g.OrderByDescending(x => x.d.Rank.Item1).ThenByDescending(x => x.d.Rank.Item2, StringComparer.Ordinal).ThenByDescending(x => x.f.Modified).First());
        var best = latest.OrderByDescending(x => x.p != null).ThenByDescending(x => x.score).ThenByDescending(x => x.f.Modified).Take(25).Select(x =>
        {
            var lb = labels.TryGetValue(x.f.Path, out var l0) ? l0 : Filing.Classify(x.f.Path);
            var tag = string.Join(" · ", new[] { lb.Disc, lb.Kind, lb.Stage }.Where(v => v.Length > 0));
            var text = x.p == null ? "" : Snip(x.p.Text, terms);
            return (x.f.Path, x.f.Name, x.p?.Loc ?? "", x.p?.Goto ?? "", text, x.d.Rev, tag);
        }).ToList();
        // 📖 not read inside yet (readable files of this choice)
        var read = FolderIndex.ReadPaths(fold);
        var unread = scope.Where(f => ReadableExt.Contains(Path.GetExtension(f.Name).ToLowerInvariant()) && !read.Contains(f.Path) && !Projects.Private.IsMatch(f.Path)).Select(f => f.Path).ToList();
        return new HintRes(counts, best, unread, hitSet.Count, choices);
    }

    static string Snip(string t, List<string> terms)
    {
        t = Regex.Replace(t ?? "", @"\s+", " ");
        var i = terms.Select(w => t.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(x => x >= 0).DefaultIfEmpty(0).Min();
        var a = Math.Max(0, i - 50);
        var s = t.Substring(a, Math.Min(170, t.Length - a));
        return (a > 0 ? "…" : "") + s + (a + 170 < t.Length ? "…" : "");
    }

    /// <summary>📖 Reads the files of this choice not read yet (one after the other, in the background), then counts again.</summary>
    void ReadNow(List<string> paths)
    {
        readCts?.Cancel(); var my = readCts = new CancellationTokenSource();
        Working(true);
        var ui = SynchronizationContext.Current;
        Task.Run(() =>
        {
            int n = 0;
            foreach (var p in paths)
            {
                if (my.IsCancellationRequested) break;
                n++;
                ui?.Post(_ => { if (!hintInfo.IsDisposed) hintInfo.Text = $"📖 Reading {n:N0} of {paths.Count:N0} · {Path.GetFileName(p)}  (■ Stop to end)"; }, null);
                try { Projects.Read(p, my.Token); } catch (OperationCanceledException) { break; } catch { }
            }
        }).ContinueWith(_ => ui?.Post(_ => { if (IsDisposed) return; Working(false); ApplyHint(); }, null));
    }

    /// <summary>Starts at a project already known (from an email or a folder).</summary>
    public void StartAt(Project p)
    {
        if (p == null) return;
        project = p; folders = p.Folders.ToList(); whereLabel = p.Code; Go(1);
    }
}
