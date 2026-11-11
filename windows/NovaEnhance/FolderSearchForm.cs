using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>
/// Right-click a folder → "Search inside with Nova". Simple by default; ⚙ More options adds file names, skip folders, dates, sizes,
/// wildcards / regular expressions, subfolders and speed. Results as a table (or cards), hits of the selected file, ⚠ Problems,
/// and a big 👁 preview of the page with the words in yellow. ✦ Ask AI answers questions from the folder.
/// </summary>
class FolderSearchForm : NovaForm
{
    static readonly (string Key, string Label)[] KindChips =
    {
        ("A", "📝 Word"), ("B", "📽 PowerPoint"), ("C", "📊 Excel"), ("D", "📕 PDF"), ("E", "🖼 Images"),
        ("F", "✉ Emails"), ("G", "📄 Text"), ("H", "📐 CAD / BIM"), ("I", "🗜 Zip"), ("K", "☁ Google files"), ("J", "📁 Other files")
    };

    // ---------------------------------------------------------------- controls
    readonly Stack top;
    readonly Label intro, status, stats, termsLbl, hitsTitle;
    readonly TextBox folder, query, fNames, fSkip;
    readonly Button tabFind, tabAsk, searchBtn, pauseBtn, stopBtn, micBtn, moreBtn, tabFiles, tabProblems, viewBtn;
    readonly RadioButton rPhrase, rAll, rAny;
    readonly CheckBox cCase, cWhole, cSub;
    readonly ComboBox cbModified, cbSize, cbMode, cbSpeed;
    readonly DateTimePicker dAfter, dBefore;
    readonly NumericUpDown nMin, nMax;
    readonly FlowLayoutPanel optRow, kindRow, answerPanel;
    readonly TableLayoutPanel morePanel;
    readonly ProgressBar bar;
    readonly RichTextBox answerBox;
    readonly SplitContainer split;
    readonly ListView filesList, hitsList, problemsList;
    readonly FlowLayoutPanel cardsPanel;
    readonly PreviewPane preview;
    readonly HashSet<string> kinds = new();
    readonly Dictionary<string, Button> kindBtns = new();

    // ---------------------------------------------------------------- state
    readonly ConcurrentQueue<(FileHit Hit, string Removed)> inbox = new();
    string namesOnlyFor;
    TableLayoutPanel qrow;
    AttachBar attach;   // a drawing number found by name: the next press also searches inside the files
    readonly List<FileHit> all = new();
    readonly Dictionary<string, FileHit> byPath = new(StringComparer.OrdinalIgnoreCase);
    readonly List<FolderIndex.Problem> problems = new();
    readonly System.Windows.Forms.Timer pump;
    FolderIndex.Progress pg;
    CancellationTokenSource cts;
    Thread worker;
    Matcher matcher;
    SearchSyntax fsx;
    string driveRoot, lastRoot = "";
    bool askMode, asking, cards, dirty;
    int sortCol = -1; bool sortDesc;
    readonly Voice voice = new();
    List<FolderIndex.Passage> passages = new();
    string lastQuestion = "", lastAnswer = "", history = "";
    const int MaxRows = 3000;

    public FolderSearchForm()
    {
        Text = "Nova Enhance · Search inside a folder";
        Width = 1500; Height = 950;
        MinimumSize = new Size(900, 640);
        KeyPreview = true;

        // ============================================================ top: folder, words, options
        top = new Stack { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoScroll = false, Padding = new Padding(16, 10, 16, 4) };

        var head = top.Add(Th.Row(false));
        head.Controls.Add(Th.L("📁 Search inside a folder", 14, Th.Text, true));
        tabFind = Th.Chip("🔍 Find words", true, () => SetMode(false)); tabFind.Margin = new Padding(16, 2, 4, 0);
        tabAsk = Th.Chip("✦ Ask AI", false, () => SetMode(true)); tabAsk.Margin = new Padding(0, 2, 4, 0);
        head.Controls.Add(tabFind); head.Controls.Add(tabAsk);
        var saved = Th.Chip("★ Saved searches ▾", false, null); saved.Margin = new Padding(16, 2, 0, 0);
        saved.Click += (_, _) => SavedMenu().Show(saved, new Point(0, saved.Height));
        head.Controls.Add(saved);
        intro = top.Add(Th.Wrap("", 9.5f));

        var frow = top.Add(new TableLayoutPanel { ColumnCount = 4, RowCount = 1, AutoSize = false, Height = 38, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 2) }, true);
        frow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) frow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folder = Th.NoSpell(Th.Box(false)); folder.Dock = DockStyle.Fill; folder.PlaceholderText = "Folder to search";
        frow.Controls.Add(folder, 0, 0);
        var browse = Th.B("Change…", Browse); browse.Margin = new Padding(8, 0, 0, 0); frow.Controls.Add(browse, 1, 0);
        var recent = Th.B("Recent ▾", null); recent.Margin = new Padding(8, 0, 0, 0);
        recent.Click += (_, _) => RecentMenu().Show(recent, new Point(0, recent.Height));
        frow.Controls.Add(recent, 2, 0);
        var gd = Th.B("☁ Google Drive…", PickDrive); gd.Margin = new Padding(8, 0, 0, 0); frow.Controls.Add(gd, 3, 0);

        qrow = top.Add(new TableLayoutPanel { ColumnCount = 6, RowCount = 1, AutoSize = false, Height = 42, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2) }, true);
        qrow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) qrow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        query = Th.Prose(Th.Box(false)); query.Font = Th.F(12.5f); query.Dock = DockStyle.Fill;
        qrow.Controls.Add(query, 0, 0);
        searchBtn = Th.B("🔍 Search", Start, true); searchBtn.Margin = new Padding(8, 0, 0, 0); qrow.Controls.Add(searchBtn, 1, 0);
        micBtn = Th.B("🎤", ToggleMic); micBtn.Margin = new Padding(8, 0, 0, 0); micBtn.Visible = false; qrow.Controls.Add(micBtn, 2, 0);
        pauseBtn = Th.B("⏸ Pause", Pause); pauseBtn.Margin = new Padding(8, 0, 0, 0); pauseBtn.Enabled = false; qrow.Controls.Add(pauseBtn, 3, 0);
        stopBtn = Th.B("■ Stop", Stop); stopBtn.Margin = new Padding(8, 0, 0, 0); stopBtn.Enabled = false; qrow.Controls.Add(stopBtn, 4, 0);
        qrow.Controls.Add(SearchSyntax.HelpButton(query, false, () => Start()), 5, 0);

        // ✎ More instructions for my AI (Ask AI mode) · ⭐ My prompts
        instrRow = top.Add(Th.Row(false));
        var il = new LinkLabel { Text = "✎ More instructions for my AI ▾", AutoSize = true, LinkColor = Th.AccentText, ActiveLinkColor = Th.Accent, Font = Th.F(9.5f, true), Margin = new Padding(0, 7, 14, 0) };
        instrRow.Controls.Add(il);
        var fp = Th.B("⭐ My prompts ▾", null);
        fp.Click += (_, _) => Prompts.Menu(fp, "folder|" + CurrentRoot(), sp =>
        {
            if (sp.Where.StartsWith("folder|")) folder.Text = sp.Where[7..];
            query.Text = sp.Question; fInstr.Text = sp.Instructions;
            if (sp.Instructions.Length > 0) fInstr.Visible = true;
            top.FitAll(); AskAi();
        });
        instrRow.Controls.Add(fp);
        // 📎 Files for the question: attach, Ctrl+V or drop — read (OCR), check against this folder, or compare them in a table.
        attach = new AttachBar();
        instrRow.Controls.Add(attach);
        Shown += (_, _) => attach.Hook(query, this);
        fInstr = top.Add(Th.Prose(Th.Box(true, 3)), true);
        fInstr.PlaceholderText = "Optional — e.g. Only from the IFC specification and the BOQ · ignore superseded revisions · give a table: item, value, source · in Arabic · export to Word";
        fInstr.Visible = Prompts.ShowMore && Store.Str("win_fs_instr").Length > 0;
        fInstr.Text = Store.Str("win_fs_instr");
        fInstr.Leave += (_, _) => Store.Set("win_fs_instr", fInstr.Text);
        il.LinkClicked += (_, _) => { fInstr.Visible = !fInstr.Visible; il.Text = fInstr.Visible ? "✎ More instructions for my AI ▴" : "✎ More instructions for my AI ▾"; if (fInstr.Visible) fInstr.Focus(); top.FitAll(); };

        optRow = top.Add(Th.Row());
        var mode = Store.Str("win_fs_mode", "phrase");
        rPhrase = Th.Radio("Exact phrase", mode == "phrase", () => Store.Set("win_fs_mode", "phrase"));
        rAll = Th.Radio("All words", mode == "all", () => Store.Set("win_fs_mode", "all"));
        rAny = Th.Radio("Any word", mode == "any", () => Store.Set("win_fs_mode", "any"));
        cCase = Th.Check("Match case", Store.Bool("win_fs_case", false), v => Store.SetBool("win_fs_case", v));
        cWhole = Th.Check("Whole word", Store.Bool("win_fs_whole", false), v => Store.SetBool("win_fs_whole", v));
        foreach (Control c in new Control[] { rPhrase, rAll, rAny, cCase, cWhole }) optRow.Controls.Add(c);
        moreBtn = Th.B("", ToggleMore); moreBtn.BackColor = Th.Bg; moreBtn.ForeColor = Th.AccentText; moreBtn.Margin = new Padding(24, 0, 0, 0);
        optRow.Controls.Add(moreBtn);

        // ---- ⚙ More options
        morePanel = top.Add(new TableLayoutPanel { ColumnCount = 4, RowCount = 3, AutoSize = true, BackColor = Color.FromArgb(0x15, 0x19, 0x22), Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 4, 0, 6) }, true);
        for (int i = 0; i < 4; i++) morePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        Control Field(string label, Control c)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 12, 6), Dock = DockStyle.Fill };
            p.Controls.Add(Th.L(label, 8.5f, Th.Muted));
            p.Controls.Add(c);
            return p;
        }
        ComboBox Combo(string key, string[] items, int def)
        {
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, BackColor = Th.Card2, ForeColor = Th.Text, FlatStyle = FlatStyle.Flat, Font = Th.F(9.5f) };
            cb.Items.AddRange(items);
            cb.SelectedIndex = Math.Clamp(Store.Int(key, def), 0, items.Length - 1);
            cb.SelectedIndexChanged += (_, _) => { Store.SetInt(key, cb.SelectedIndex); ShowRanges(); };
            return cb;
        }
        fNames = Th.NoSpell(Th.Box(false)); fNames.Width = 260; fNames.PlaceholderText = "*.pdf; *.docx; *BOQ*  (empty = all)"; fNames.Text = Store.Str("win_fso_names");
        fNames.TextChanged += (_, _) => Store.Set("win_fso_names", fNames.Text);
        fSkip = Th.NoSpell(Th.Box(false)); fSkip.Width = 260; fSkip.PlaceholderText = "Superseded; Archive; _old"; fSkip.Text = Store.Str("win_fso_skip");
        fSkip.TextChanged += (_, _) => Store.Set("win_fso_skip", fSkip.Text);
        cbModified = Combo("win_fso_mod", new[] { "Any time", "Last 7 days", "Last 30 days", "Last 12 months", "Between dates…" }, 0);
        dAfter = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110, Value = DateTime.Today.AddMonths(-1) };
        dBefore = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110, Value = DateTime.Today };
        var dates = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        dates.Controls.Add(dAfter); dates.Controls.Add(Th.L("→", 10, Th.Muted)); dates.Controls.Add(dBefore);
        cbSize = Combo("win_fso_size", new[] { "Any size", "Smaller than 1 MB", "Smaller than 10 MB", "Larger than 10 MB", "Larger than 100 MB", "Between sizes…" }, 0);
        nMin = new NumericUpDown { Minimum = 0, Maximum = 100000, Value = 0, Width = 80, BackColor = Th.Card, ForeColor = Th.Text };
        nMax = new NumericUpDown { Minimum = 0, Maximum = 100000, Value = 50, Width = 80, BackColor = Th.Card, ForeColor = Th.Text };
        var sizes = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        sizes.Controls.Add(nMin); sizes.Controls.Add(Th.L("→", 10, Th.Muted)); sizes.Controls.Add(nMax); sizes.Controls.Add(Th.L("MB", 9.5f, Th.Muted));
        cbMode = Combo("win_fso_rx", new[] { "Normal", "Wildcards  * ?", "Regular expression" }, 0);
        cSub = Th.Check("Include subfolders", Store.Bool("win_fso_sub", true), v => Store.SetBool("win_fso_sub", v));
        cbSpeed = Combo("win_fso_speed", new[] { $"Fast · {Math.Clamp(System.Environment.ProcessorCount, 2, 8)} files at once", "Gentle · in the background while I work" }, 0);
        morePanel.Controls.Add(Field("File names (optional)", fNames), 0, 0);
        morePanel.Controls.Add(Field("Skip folders", fSkip), 0, 1);
        var modBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        modBox.Controls.Add(cbModified); modBox.Controls.Add(dates);
        morePanel.Controls.Add(Field("Modified", modBox), 1, 0);
        var sizeBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        sizeBox.Controls.Add(cbSize); sizeBox.Controls.Add(sizes);
        morePanel.Controls.Add(Field("Size", sizeBox), 1, 1);
        morePanel.Controls.Add(Field("Search mode", cbMode), 2, 0);
        morePanel.Controls.Add(Field("Subfolders", cSub), 2, 1);
        morePanel.Controls.Add(Field("Speed", cbSpeed), 3, 0);
        kindRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0), MaximumSize = new Size(1400, 0) };
        var allBtn = Th.Chip("All types", true, null);
        allBtn.Click += (_, _) => { kinds.Clear(); DrawKinds(); };
        kindBtns["*"] = allBtn; kindRow.Controls.Add(allBtn);
        foreach (var (k, l) in KindChips)
        {
            var kk = k;
            var b = Th.Chip(l, false, () => { if (!kinds.Remove(kk)) kinds.Add(kk); DrawKinds(); });
            b.Font = Th.F(9); kindBtns[k] = b; kindRow.Controls.Add(b);
        }
        var kindField = Field("File types", kindRow);
        morePanel.Controls.Add(kindField, 0, 2);
        morePanel.SetColumnSpan(kindField, 4);

        // ---- Ask AI answer
        answerPanel = top.Add(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 4), Visible = false });
        termsLbl = Th.L("", 9, Th.Muted);
        answerPanel.Controls.Add(termsLbl);
        answerBox = new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10.5f), ScrollBars = RichTextBoxScrollBars.Vertical, Height = 140, Margin = new Padding(0, 6, 0, 6), DetectUrls = false };
        answerPanel.Controls.Add(answerBox);
        var arow = Th.Row(false);
        arow.Controls.Add(Th.B("▾ More detail", () => Answer(true), true));
        arow.Controls.Add(Th.B("⧉ Copy", () => { if (answerBox.Text.Length > 0) Clipboard.SetText(answerBox.Text); }));
        arow.Controls.Add(Th.B("⇩ Save with sources", SaveAnswer));
        arow.Controls.Add(Th.B("⭐ Save prompt", () =>
        {
            if (lastQuestion.Length == 0) { status.Text = "Ask a question first"; return; }
            var sp = Prompts.AskSave(this, new SavedPrompt { Name = lastQuestion.Length > 40 ? lastQuestion[..40] : lastQuestion, Question = lastQuestion, Instructions = fInstr.Text.Trim(), Where = "folder|" + lastRoot });
            if (sp != null) status.Text = $"⭐ Saved “{sp.Name}” — find it under ⭐ My prompts";
        }));
        arow.Controls.Add(Th.B("📊 Table → Excel", () =>
        {
            try { var x = FileTalk.ExportXlsx(lastAnswer, lastQuestion.Length > 0 ? lastQuestion : "Comparison"); Shell.Open(x); status.Text = "📊 Saved: " + x; }
            catch (Exception ex) { status.Text = "⚠ " + ex.Message; }
        }));
        arow.Controls.Add(Th.B("👍", Good));
        arow.Controls.Add(Th.B("👎 Right file is…", Bad));
        arow.Controls.Add(Th.B("✚ New question", () => { history = ""; lastQuestion = ""; query.Text = ""; answerBox.Text = ""; query.Focus(); status.Text = "New question — the conversation was cleared"; }));
        answerPanel.Controls.Add(arow);

        // ---- counters
        stats = top.Add(Th.Wrap("", 9.5f, Th.Text));
        stats.BackColor = Color.FromArgb(0x15, 0x19, 0x22); stats.Padding = new Padding(10, 6, 10, 6);
        bar = top.Add(new ProgressBar { Height = 5, Style = ProgressBarStyle.Continuous, Visible = false, Margin = new Padding(0, 2, 0, 2) }, true);
        status = top.Add(Th.Wrap("", 9.5f, Th.AccentText));

        // ============================================================ results (left) · preview (right)
        split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Th.Line, SplitterWidth = 6, Orientation = Orientation.Vertical };
        split.Panel1.BackColor = Th.Bg; split.Panel2.BackColor = Th.Bg;
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Th.Bg, Padding = new Padding(12, 0, 6, 4) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        var tabs = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
        tabFiles = Th.Chip("Files (0)", true, () => ShowTab(false));
        tabProblems = Th.Chip("⚠ Problems (0)", false, () => ShowTab(true));
        viewBtn = Th.Chip("▦ Cards", false, ToggleCards);
        tabs.Controls.Add(tabFiles); tabs.Controls.Add(tabProblems); tabs.Controls.Add(viewBtn);
        left.Controls.Add(tabs, 0, 0);
        var mid = new Panel { Dock = DockStyle.Fill, BackColor = Th.Bg, Margin = new Padding(0) };
        filesList = NewList(("Name", 330), ("Type", 90), ("Hits", 60), ("Folder", 220), ("Modified", 100), ("Size", 80));
        filesList.SelectedIndexChanged += (_, _) => FileSelected();
        filesList.DoubleClick += (_, _) => { if (Selected() is { } h) Opener.Open(h, h.Pieces.FirstOrDefault(), matcher?.Query); };
        filesList.ColumnClick += (_, e) => { sortDesc = sortCol == e.Column ? !sortDesc : e.Column is 2 or 4 or 5; sortCol = e.Column; dirty = true; RefreshList(); };
        filesList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && Selected() is { } h) Opener.Open(h, h.Pieces.FirstOrDefault(), matcher?.Query); };
        var menu = new ContextMenuStrip { Font = Th.F(10) };
        menu.Items.Add("Open", null, (_, _) =>
        {
            var sel = SelectedAll();
            if (sel.Count > 5 && MessageBox.Show(this, $"Open {sel.Count} files?", "Nova Enhance", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            foreach (var h in sel.Take(30)) Opener.Open(h, h.Pieces.FirstOrDefault(), matcher?.Query);
        });
        menu.Items.Add("📁 Show in folder", null, (_, _) => { if (Selected() is { } h && string.IsNullOrEmpty(h.Remote)) Shell.Select(h.Path); });
        menu.Items.Add("⧉ Copy path", null, (_, _) => { var sel = SelectedAll(); if (sel.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(h => string.IsNullOrEmpty(h.Remote) ? h.Path : DriveLink(h)))); });
        menu.Items.Add("⧉ Pop out the preview", null, (_, _) => { if (Selected() is { } h) new PreviewWindow(h, matcher, 0, false).Show(); });
        // ☑ Several files (Ctrl+click · Shift+click · Ctrl+A): the actions below apply to all of them.
        filesList.MultiSelect = true;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("⧉ Copy the selected files (paste into an email or a folder)", null, (_, _) =>
        {
            var c = new System.Collections.Specialized.StringCollection();
            c.AddRange(SelectedAll().Where(h => string.IsNullOrEmpty(h.Remote)).Select(h => FileCheck.Resolve(h.Path)).ToArray());
            if (c.Count > 0) Clipboard.SetFileDropList(c);
        });
        menu.Items.Add("📎 Attach the selected files to a new email", null, (_, _) =>
        {
            try { Outlook.NewWithAttachments(SelectedAll().Where(h => string.IsNullOrEmpty(h.Remote)).Select(h => FileCheck.Resolve(h.Path)).ToList()); }
            catch (Exception e) { MessageBox.Show(this, "⚠ " + e.Message, "Nova Enhance"); }
        });
        menu.Items.Add("💬 Ask Nova about the selected files", null, (_, _) =>
        {
            var fs = SelectedAll().Where(h => string.IsNullOrEmpty(h.Remote) && !FileType.IsModel(h.Path)).Take(FileTalk.Max).ToList();
            if (askMode) { attach.Add(fs.Select(h => FileCheck.Resolve(h.Path))); query.Focus(); }
            else foreach (var h in fs) Screens.HubAttach(FileCheck.Resolve(h.Path));
        });
        menu.Items.Add("📊 Export the selected files (Excel)", null, (_, _) =>
        {
            var sel = SelectedAll();
            var head = new[] { "No.", "File", "Type", "Hits", "Folder", "Modified", "Size", "Path" };
            var data = sel.Select((h, i) => new[] { (i + 1).ToString(), Path.GetFileName(h.Path), FileType.Of(h.Path), h.Hits.ToString(), Path.GetDirectoryName(h.Path) ?? "",
                h.Mtime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(h.Mtime).LocalDateTime.ToString("dd MMM yyyy") : "", Projects.Size(h.Size), h.Path }).ToList();
            var x = Path.Combine(Prompts.Folder, $"{sel.Count} files {DateTime.Now:dd MMM yyyy HHmm}.xlsx");
            Prompts.Xlsx(x, $"{sel.Count} files", head, data);
            Shell.Open(x);
        });
        menu.Opening += (_, _) => { var n = filesList.SelectedItems.Count; menu.Items[0].Text = n > 1 ? $"Open ({n})" : "Open"; };
        filesList.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem i in filesList.Items) i.Selected = true; e.Handled = true; } };
        filesList.ContextMenuStrip = menu;
        problemsList = NewList(("File", 300), ("Problem", 330), ("What you can do", 240));
        problemsList.Visible = false;
        problemsList.DoubleClick += (_, _) => ProblemAction();
        cardsPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Th.Bg, Visible = false };
        mid.Controls.Add(filesList); mid.Controls.Add(problemsList); mid.Controls.Add(cardsPanel);
        left.Controls.Add(mid, 0, 1);
        hitsTitle = Th.L("HITS IN THIS FILE", 8.5f, Th.Muted, true); hitsTitle.Margin = new Padding(0, 8, 0, 4);
        left.Controls.Add(hitsTitle, 0, 2);
        hitsList = NewList(("Where", 150), ("Found", 600));
        hitsList.OwnerDraw = true;
        hitsList.DrawSubItem += DrawHit;
        hitsList.SelectedIndexChanged += (_, _) => HitSelected();
        hitsList.DoubleClick += (_, _) => { if (Selected() is { } h && hitsList.SelectedItems.Count > 0) Opener.Open(h, (PieceHit)hitsList.SelectedItems[0].Tag, matcher?.Query); };
        left.Controls.Add(hitsList, 0, 3);
        split.Panel1.Controls.Add(left);
        preview = new PreviewPane { Dock = DockStyle.Fill };
        preview.WindowRequested += full => { if (preview.Hit != null) new PreviewWindow(preview.Hit, preview.Matcher, preview.HitIndex, full).Show(); };
        split.Panel2.Controls.Add(preview);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Th.Bg, Padding = new Padding(14, 4, 14, 4) };
        bottom.Controls.Add(Th.B("📌 Save as task", () =>
        {
            if (all.Count == 0) return;
            Screens.Saved(Work.FromFiles(query.Text, folder.Text, all.ToList()));
        }));
        bottom.Controls.Add(Th.B("⇩ Export", Export));
        bottom.Controls.Add(Th.B("🖨 Report", Report));
        bottom.Controls.Add(Th.B("📁 Search Results", () => Shell.Open(Paths.SearchResults)));
        bottom.Controls.Add(Th.B("⚙ File types & OCR", () => Screens.Settings("Folder search")));
        var sig = Th.Signature(); sig.Margin = new Padding(20, 10, 0, 0);
        bottom.Controls.Add(sig);

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(top);

        query.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Start(); } };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { if (worker != null || asking) Stop(); else Hide(); } };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Stop(); Hide(); } };
        Shown += (_, _) =>
        {
            try { split.SplitterDistance = Math.Clamp(Store.Int("win_fs_split", (int)(split.Width * 0.42)), 380, Math.Max(400, split.Width - 420)); } catch { }
            split.SplitterMoved += (_, _) => Store.SetInt("win_fs_split", split.SplitterDistance);
        };
        Resize += (_, _) => answerBox.Width = Math.Max(300, top.ClientSize.Width - 40);
        morePanel.Visible = Store.Bool("win_fso_open", false);
        DrawMore();
        ShowRanges();
        SetMode(false);
        pump = new System.Windows.Forms.Timer { Interval = 300 };
        pump.Tick += (_, _) => Flush();
        pump.Start();
    }

    // ================================================================ list helpers (dark tables)

    static ListView NewList(params (string Name, int Width)[] cols)
    {
        var lv = new ListView
        {
            View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill,
            BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(9.5f), OwnerDraw = true, HeaderStyle = ColumnHeaderStyle.Clickable
        };
        foreach (var (n, w) in cols) lv.Columns.Add(n, w);
        lv.DrawColumnHeader += (_, e) =>
        {
            using var b = new SolidBrush(Color.FromArgb(0x15, 0x19, 0x22));
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), Th.F(8, true), new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), Th.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        lv.DrawItem += (_, e) => { };
        lv.DrawSubItem += (s, e) =>
        {
            var sel = e.Item.Selected;
            using var b = new SolidBrush(sel ? Th.Card2 : Th.Card);
            e.Graphics.FillRectangle(b, e.Bounds);
            var color = e.ColumnIndex == 0 ? Th.Text : Th.Muted;
            if (e.Item.ForeColor == Th.Warn) color = Th.Warn;
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, e.ColumnIndex == 0 ? Th.F(9.5f, sel) : Th.F(9.5f), new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        return lv;
    }

    void DrawHit(object s, DrawListViewSubItemEventArgs e)
    {
        var sel = e.Item.Selected;
        using (var b = new SolidBrush(sel ? Th.Card2 : Th.Card)) e.Graphics.FillRectangle(b, e.Bounds);
        if (e.ColumnIndex == 0)
        {
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Th.F(9.5f, true), new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height), Th.AccentText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }
        var p = (PieceHit)e.Item.Tag;
        var (pre, midText, post) = FolderIndex.Preview(p, 70);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        int x = e.Bounds.X + 6, right = e.Bounds.Right - 4;
        void Part(string t, Font f, Color c, Color? back)
        {
            if (x >= right || t.Length == 0) return;
            var sz = TextRenderer.MeasureText(e.Graphics, t, f, new Size(int.MaxValue, e.Bounds.Height), flags);
            var r = new Rectangle(x, e.Bounds.Y + 2, Math.Min(sz.Width, right - x), e.Bounds.Height - 4);
            if (back != null) using (var bb = new SolidBrush(back.Value)) e.Graphics.FillRectangle(bb, r);
            TextRenderer.DrawText(e.Graphics, t, f, r, c, flags | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            x += sz.Width;
        }
        Part(pre, Th.F(9.5f), Color.FromArgb(0xC9, 0xCE, 0xD8), null);
        Part(midText, Th.F(9.5f, true), Color.Black, Th.Yellow);
        Part(post, Th.F(9.5f), Color.FromArgb(0xC9, 0xCE, 0xD8), null);
    }

    FileHit Selected() => filesList.SelectedItems.Count > 0 ? filesList.SelectedItems[0].Tag as FileHit : null;
    List<FileHit> SelectedAll() => filesList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as FileHit).Where(h => h != null).ToList();

    static string DriveLink(FileHit h) { var p = h.Remote.Split('|'); return GDrive.WebLink(p[0], p.Length > 1 ? p[1] : ""); }

    static string FmtSize(long b) => b <= 0 ? "" : b < 1024 * 1024 ? $"{b / 1024.0:0} KB" : $"{b / 1048576.0:0.0} MB";

    void RefreshList()
    {
        if (!dirty) return;
        dirty = false;
        var sel = Selected()?.Path;
        IEnumerable<FileHit> q = all;
        q = sortCol switch
        {
            0 => sortDesc ? q.OrderByDescending(h => h.Name) : q.OrderBy(h => h.Name),
            1 => sortDesc ? q.OrderByDescending(h => FileType.Of(h.Path)).ThenBy(h => h.Name) : q.OrderBy(h => FileType.Of(h.Path)).ThenBy(h => h.Name),
            2 => sortDesc ? q.OrderByDescending(h => h.Hits) : q.OrderBy(h => h.Hits),
            3 => sortDesc ? q.OrderByDescending(h => h.Folder) : q.OrderBy(h => h.Folder),
            4 => sortDesc ? q.OrderByDescending(h => h.Mtime) : q.OrderBy(h => h.Mtime),
            5 => sortDesc ? q.OrderByDescending(h => h.Size) : q.OrderBy(h => h.Size),
            _ => q
        };
        filesList.BeginUpdate();
        filesList.Items.Clear();
        foreach (var h in q.Take(MaxRows))
        {
            var rel = h.Folder.StartsWith(h.Root, StringComparison.OrdinalIgnoreCase) ? "." + h.Folder[h.Root.Length..] : h.Folder;
            var it = new ListViewItem(new[]
            {
                $"{KindIcon(h.Kind)} {h.Name}", FileType.Of(h.Path), h.Hits.ToString("N0"), rel.Length <= 1 ? "." : rel,
                h.Mtime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(h.Mtime).LocalDateTime.ToString("dd MMM yyyy") : "", FmtSize(h.Size)
            }) { Tag = h };
            filesList.Items.Add(it);
            if (h.Path.Equals(sel, StringComparison.OrdinalIgnoreCase)) it.Selected = true;
        }
        filesList.EndUpdate();
        tabFiles.Text = $"Files ({all.Count:N0})";
        if (cards) DrawCards();
        if (filesList.SelectedItems.Count == 0 && filesList.Items.Count > 0 && preview.Hit == null) filesList.Items[0].Selected = true;
    }

    void FileSelected()
    {
        var h = Selected();
        hitsList.BeginUpdate();
        hitsList.Items.Clear();
        if (h != null)
        {
            hitsTitle.Text = $"HITS IN THIS FILE ({h.Hits:N0})";
            foreach (var p in h.Pieces.Take(1000))
                hitsList.Items.Add(new ListViewItem(new[] { FolderIndex.Where(p), "" }) { Tag = p });
        }
        else hitsTitle.Text = "HITS IN THIS FILE";
        hitsList.EndUpdate();
        if (h != null) preview.Show(h, matcher, 0);
    }

    void HitSelected()
    {
        var h = Selected();
        if (h == null || hitsList.SelectedItems.Count == 0) return;
        var p = (PieceHit)hitsList.SelectedItems[0].Tag;
        int start = 0;
        foreach (var x in h.Pieces) { if (x == p) break; start += Math.Max(1, x.Matches?.Count ?? 1); }
        preview.Show(h, matcher, start);
    }

    void ShowTab(bool problemsTab)
    {
        Th.SetChip(tabFiles, !problemsTab); Th.SetChip(tabProblems, problemsTab);
        problemsList.Visible = problemsTab;
        filesList.Visible = !problemsTab && !cards;
        cardsPanel.Visible = !problemsTab && cards;
        if (problemsTab) problemsList.BringToFront(); else if (cards) cardsPanel.BringToFront(); else filesList.BringToFront();
    }

    void ToggleCards()
    {
        cards = !cards;
        viewBtn.Text = cards ? "☰ Table" : "▦ Cards";
        if (cards) DrawCards();
        ShowTab(false);
    }

    void DrawCards()
    {
        cardsPanel.SuspendLayout();
        foreach (Control c in cardsPanel.Controls) c.Dispose();
        cardsPanel.Controls.Clear();
        foreach (var h in all.Take(150)) cardsPanel.Controls.Add(Card(h));
        cardsPanel.ResumeLayout();
    }

    // ================================================================ options

    void ToggleMore()
    {
        morePanel.Visible = !morePanel.Visible;
        Store.SetBool("win_fso_open", morePanel.Visible);
        DrawMore();
        top.FitAll();
    }

    void DrawMore()
    {
        var active = MoreActive();
        moreBtn.Text = (morePanel.Visible ? "⚙ More options ▴" : "⚙ More options ▾") + (active > 0 && !morePanel.Visible ? $"  ({active} on)" : "");
    }

    int MoreActive() =>
        (fNames.Text.Trim().Length > 0 ? 1 : 0) + (fSkip.Text.Trim().Length > 0 ? 1 : 0) + (cbModified.SelectedIndex > 0 ? 1 : 0) + (cbSize.SelectedIndex > 0 ? 1 : 0) +
        (cbMode.SelectedIndex > 0 ? 1 : 0) + (!cSub.Checked ? 1 : 0) + (kinds.Count > 0 ? 1 : 0);

    void ShowRanges()
    {
        dAfter.Parent.Visible = cbModified.SelectedIndex == 4;
        nMin.Parent.Visible = cbSize.SelectedIndex == 5;
        rPhrase.Enabled = rAll.Enabled = rAny.Enabled = cbMode.SelectedIndex == 0;
        DrawMore();
    }

    void DrawKinds()
    {
        Th.SetChip(kindBtns["*"], kinds.Count == 0);
        foreach (var (k, _) in KindChips) Th.SetChip(kindBtns[k], kinds.Contains(k));
        DrawMore();
    }

    FolderIndex.Options Options()
    {
        DateTime? after = cbModified.SelectedIndex switch { 1 => DateTime.Today.AddDays(-7), 2 => DateTime.Today.AddDays(-30), 3 => DateTime.Today.AddMonths(-12), 4 => dAfter.Value.Date, _ => null };
        DateTime? before = cbModified.SelectedIndex == 4 ? dBefore.Value.Date : null;
        const long MB = 1024 * 1024;
        (long min, long max) = cbSize.SelectedIndex switch
        {
            1 => (0L, MB), 2 => (0L, 10 * MB), 3 => (10 * MB, 0L), 4 => (100 * MB, 0L),
            5 => ((long)nMin.Value * MB, nMax.Value > 0 ? (long)nMax.Value * MB : 0L), _ => (0L, 0L)
        };
        return FolderIndex.Options.From(fNames.Text, fSkip.Text, after, before, min, max, cSub.Checked, cbSpeed.SelectedIndex == 1);
    }

    string MatchMode() => cbMode.SelectedIndex switch { 1 => "wildcard", 2 => "regex", _ => rAll.Checked ? "all" : rAny.Checked ? "any" : "phrase" };

    // ================================================================ folder

    public void Begin(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) SetRoot(path.Trim('"'));
        if (folder.Text.Length == 0) SetRoot(FolderIndex.Roots().FirstOrDefault().Path ?? "");
        query.Focus();
        query.SelectAll();
    }

    void SetRoot(string root)
    {
        if (GDrive.IsRoot(root)) { driveRoot = root; folder.Text = GDrive.Display(root); }
        else { driveRoot = null; folder.Text = root ?? ""; }
    }

    string CurrentRoot()
    {
        if (driveRoot != null && folder.Text == GDrive.Display(driveRoot)) return driveRoot;
        driveRoot = null;
        return folder.Text.Trim().Trim('"');
    }

    bool CheckRoot(string root)
    {
        if (GDrive.IsRoot(root))
        {
            if (GDrive.SignedIn) return true;
            status.Text = "⚠ Sign in to Google Drive first: ⚙ Settings → Google Drive";
            return false;
        }
        if (Directory.Exists(root)) return true;
        status.Text = "⚠ This folder doesn't exist — choose it with Change… or ☁ Google Drive…";
        return false;
    }

    void Browse()
    {
        using var d = new FolderBrowserDialog { Description = "Folder to search", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(folder.Text) ? folder.Text : "" };
        if (d.ShowDialog(this) == DialogResult.OK) SetRoot(d.SelectedPath);
    }

    void PickDrive()
    {
        if (!GDrive.SignedIn)
        {
            if (MessageBox.Show(this, "Google Drive isn't connected yet. Open Settings → Google Drive to sign in?", "Nova Enhance", MessageBoxButtons.YesNo) == DialogResult.Yes)
                Screens.Settings("Google Drive");
            return;
        }
        using var d = new DrivePicker();
        if (d.ShowDialog(this) == DialogResult.OK && d.Picked.Id != null) SetRoot(GDrive.RootKey(d.Picked.Id, d.Picked.Name));
    }

    ContextMenuStrip RecentMenu()
    {
        var m = new ContextMenuStrip { Font = Th.F(10) };
        var roots = FolderIndex.Roots();
        if (roots.Count == 0) m.Items.Add(new ToolStripMenuItem("No folder searched yet") { Enabled = false });
        foreach (var (p, last, files) in roots.Take(20))
        {
            var pp = p;
            m.Items.Add($"{GDrive.Display(p)}   ·  {files:N0} files · {last:dd MMM}", null, (_, _) => SetRoot(pp));
        }
        return m;
    }

    void SetMode(bool ask)
    {
        askMode = ask;
        Th.SetChip(tabFind, !ask); Th.SetChip(tabAsk, ask);
        optRow.Visible = !ask;
        instrRow.Visible = ask;
        if (!ask) fInstr.Visible = false;
        if (ask) morePanel.Visible = false; else morePanel.Visible = Store.Bool("win_fso_open", false);
        micBtn.Visible = ask;
        answerPanel.Visible = ask && answerBox.Text.Length > 0;
        searchBtn.Text = ask ? "✦ Ask" : "🔍 Search";
        // Ask AI: three lines to type the question (Enter = ask · Shift+Enter = new line).
        query.Multiline = ask; query.ScrollBars = ask ? ScrollBars.Vertical : ScrollBars.None; qrow.Height = ask ? 82 : 42;
        query.PlaceholderText = ask ? "e.g. What is the fire rating required for the curtain wall?" : "e.g. A-201 Rev C · RFI-044 · fire rating of the curtain wall";
        intro.Text = ask
            ? "Ask in your own words: the AI answers from the files of this folder, with the file and page of every point. You are asked before any extract is sent."
            : "Finds a code, a reference or a sentence inside PDF, Word, PowerPoint, Excel, emails, CAD/BIM text, zip files and more. Everything is read on this PC.";
        answerBox.Width = Math.Max(300, top.ClientSize.Width - 40);
        DrawMore();
        top.FitAll();
    }

    // ================================================================ search

    void ClearResults()
    {
        all.Clear(); byPath.Clear(); problems.Clear();
        while (inbox.TryDequeue(out _)) { }
        filesList.Items.Clear(); hitsList.Items.Clear(); problemsList.Items.Clear();
        foreach (Control c in cardsPanel.Controls) c.Dispose();
        cardsPanel.Controls.Clear();
        tabFiles.Text = "Files (0)"; tabProblems.Text = "⚠ Problems (0)";
        hitsTitle.Text = "HITS IN THIS FILE";
        preview.ShowMessage("Results appear on the left — select one to see its page here.");
    }

    void Start()
    {
        if (askMode) { AskAi(); return; }
        var root = CurrentRoot();
        Speller.FixBeforeSearch(query, Start);
        var q = query.Text.Trim();
        // 🔎 Windows search rules: "exact", -word, kind:, ext:, folder:, date: … checked on every file found.
        fsx = SearchSyntax.Parse(q);
        if (fsx.Any) q = fsx.Free.Trim();
        if (q.Length == 0) { status.Text = fsx.Any ? "Add a word to look for (the rules " + fsx.Describe() + " are applied to what is found)" : "Type what you're looking for"; query.Focus(); return; }
        if (!CheckRoot(root)) return;
        try { matcher = new Matcher(q, MatchMode(), cCase.Checked, cWhole.Checked); }
        catch (ArgumentException e) { status.Text = "⚠ " + e.Message; return; }
        Stop();
        lastRoot = root;
        answerPanel.Visible = false;
        ClearResults();
        pg = new FolderIndex.Progress();
        var my = cts = new CancellationTokenSource();
        var k = new HashSet<string>(kinds);
        var m = matcher; var p = pg; var opt = Options();
        bar.Visible = true; bar.Value = 0;
        // 📛 Names first (shared project file list): files whose NAME matches appear at once, before any file is opened.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var covered = Projects.Covers(root);
        var named = covered ? Projects.FindFiles(q, new List<string> { root }, true, false, 300) : new List<Projects.FileRow>();
        foreach (var f in named)
        {
            var idx = f.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            var nh = new FileHit
            {
                Path = f.Path, Root = root, Kind = FileKinds.KindOf(Path.GetExtension(f.Path).ToLowerInvariant()), Size = f.Size,
                Mtime = f.Modified > DateTime.MinValue ? new DateTimeOffset(f.Modified.ToUniversalTime()).ToUnixTimeMilliseconds() : 0
            };
            nh.Pieces.Add(new PieceHit { Loc = "📛 file name" + (f.Status.Length > 0 ? " · " + f.Status : "") + (f.Rev.Length > 0 ? " · Rev " + f.Rev : ""), Goto = "", Text = f.Name, Matches = idx >= 0 ? new() { (idx, q.Length) } : new() });
            inbox.Enqueue((nh, null));
        }
        var num = Revisions.Number(q);
        var looksNumber = num.Length > 0 && q.Trim().Length <= num.Length + 12;
        if (covered && named.Count > 0 && looksNumber && namesOnlyFor != q)
        {
            namesOnlyFor = q;
            pg = null;
            bar.Visible = false;
            stopBtn.Enabled = pauseBtn.Enabled = false;
            searchBtn.Text = "🔍 Also search inside files";
            status.Text = $"📛 {named.Count} file(s) found by name in {clock.ElapsedMilliseconds} ms — press “🔍 Also search inside files” to look for {num} inside the documents too (slower).";
            return;
        }
        namesOnlyFor = null;
        searchBtn.Text = "🔍 Search";
        stopBtn.Enabled = pauseBtn.Enabled = true; pauseBtn.Text = "⏸ Pause";
        status.Text = named.Count > 0 ? $"📛 {named.Count} found by name · now searching inside the files…" : "Searching…";
        worker = new Thread(() => FolderIndex.Search(root, m, k, h => inbox.Enqueue((h, null)), path => inbox.Enqueue((null, path)), p, my.Token, opt))
        { IsBackground = true, Name = "Nova folder search", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    void Pause()
    {
        if (pg == null || pg.Finished) return;
        if (pg.Gate.IsSet) { pg.Gate.Reset(); pauseBtn.Text = "▶ Continue"; status.Text = "⏸ Paused — files being read now finish first"; }
        else { pg.Gate.Set(); pauseBtn.Text = "⏸ Pause"; }
    }

    void Stop()
    {
        cts?.Cancel();
        pg?.Gate.Set();
        stopBtn.Enabled = false; pauseBtn.Enabled = false;
    }

    void Flush()
    {
        if (!Visible) return;
        int n = 0;
        while (n < 400 && inbox.TryDequeue(out var item))
        {
            n++;
            if (item.Removed != null)
            {
                if (byPath.Remove(item.Removed, out var old)) all.Remove(old);
                dirty = true;
                continue;
            }
            var h = item.Hit;
            if (fsx != null && fsx.Any && !fsx.PassFile(h.Path, h.Mtime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(h.Mtime).LocalDateTime : DateTime.Now, "",
                    string.Join("\n", h.Pieces.Select(x => x.Text ?? "")) + (byPath.TryGetValue(h.Path, out var pv) ? "\n" + string.Join("\n", pv.Pieces.Select(x => x.Text ?? "")) : ""))) continue;
            if (byPath.Remove(h.Path, out var prev))
            {
                all.Remove(prev);
                // Found by name first, then inside: keep the 📛 line on top.
                var np = prev.Pieces.FirstOrDefault(x => (x.Loc ?? "").StartsWith("📛"));
                if (np != null && !h.Pieces.Any(x => (x.Loc ?? "").StartsWith("📛"))) h.Pieces.Insert(0, np);
            }
            byPath[h.Path] = h;
            all.Add(h);
            dirty = true;
        }
        if (pg != null)
        {
            while (pg.Problems.TryDequeue(out var pr))
            {
                problems.Add(pr);
                problemsList.Items.Add(new ListViewItem(new[] { pr.Name, pr.Reason, pr.Action ?? "" }) { Tag = pr, ForeColor = Th.Warn });
            }
            tabProblems.Text = $"⚠ Problems ({problems.Count:N0})";
        }
        if (dirty && (n > 0 || all.Count == 0)) RefreshList();
        if (pg == null) return;
        var secs = pg.Clock.Elapsed.TotalSeconds;
        var speed = secs > 1 ? pg.Done / secs : 0;
        var hitsN = all.Sum(h => h.Hits);
        stats.Text = $"Found {all.Count:N0} files   ·   Hits {hitsN:N0}   ·   Searched {pg.Done:N0} / {pg.Total:N0} files ({pg.Bytes / 1073741824.0:0.00} GB)   ·   " +
                     $"Pending {Math.Max(0, pg.Total - pg.Done):N0}   ·   Speed {speed:0} files/s{(pg.Workers > 1 ? $" ({pg.Workers} at once)" : "")}   ·   Problems {problems.Count:N0}   ·   {TimeSpan.FromSeconds(secs):mm\\:ss}";
        if (!pg.Finished)
        {
            if (pg.Total > 0) { bar.Maximum = Math.Max(1, pg.Total); bar.Value = Math.Min(pg.Done, bar.Maximum); }
            if (pg.Gate.IsSet) status.Text = pg.Phase + (pg.Current.Length > 0 ? " " + Short(pg.Current, 70) : "");
        }
        else if (worker != null && !asking)
        {
            worker = null;
            bar.Visible = false;
            stopBtn.Enabled = pauseBtn.Enabled = false;
            var cancelled = cts?.IsCancellationRequested == true;
            status.Text = pg.Phase.StartsWith("⚠") ? pg.Phase :
                (cancelled ? "■ Stopped · " : "✓ ") + (all.Count == 0 ? $"Not found in {pg.Files:N0} files" : $"Found in {all.Count:N0} file{(all.Count == 1 ? "" : "s")} ({hitsN:N0} hits)") +
                $" · {pg.Seconds:0.0} s" + (pg.Read > 0 ? $" · {pg.Read:N0} new or changed files read and remembered — next search here is instant" : "") +
                (all.Count > MaxRows ? $" · showing {MaxRows:N0} files, ⇩ Export for all" : "");
            if (!cancelled) OfferVision(lastRoot, () => Start());
        }
    }

    static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    static string KindIcon(string kind) => kind switch { "A" => "📝", "B" => "📽", "C" => "📊", "D" => "📕", "E" => "🖼", "F" => "✉", "G" => "📄", "H" => "📐", "I" => "🗜", "K" => "☁", _ => "📁" };

    void ProblemAction()
    {
        if (problemsList.SelectedItems.Count == 0 || problemsList.SelectedItems[0].Tag is not FolderIndex.Problem pr) return;
        var a = pr.Action ?? "";
        if (a.StartsWith("read with OCR"))
        {
            if (MessageBox.Show(this, "Turn on text recognition (OCR) for scanned PDFs and images, and read this file again?", "Nova Enhance", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            FileKinds.Set("E", true);
            try { FolderIndex.ForgetFile(pr.Path); } catch { }
            Start();
        }
        else if (a.Contains("Settings")) Screens.Settings("Folder search");
        else if (!GDrive.IsRoot(pr.Path)) Shell.Open(pr.Path);
    }

    // ================================================================ saved searches

    ContextMenuStrip SavedMenu()
    {
        var m = new ContextMenuStrip { Font = Th.F(10) };
        m.Items.Add("★ Save this search…", null, (_, _) => SaveSearch());
        var list = SavedList();
        if (list.Count > 0) m.Items.Add(new ToolStripSeparator());
        foreach (var o in list)
        {
            var oo = o;
            m.Items.Add($"{o["name"]}   ·  {Short(GDrive.Display(o["root"]?.ToString() ?? ""), 40)}", null, (_, _) => LoadSearch(oo, true));
        }
        if (list.Count > 0)
        {
            var del = new ToolStripMenuItem("Delete a saved search");
            foreach (var o in list)
            {
                var name = o["name"]?.ToString();
                del.DropDownItems.Add(name, null, (_, _) => { Store.Set("win_saved", new JsonArray(SavedList().Where(x => x["name"]?.ToString() != name).Select(x => (JsonNode)x.DeepClone()).ToArray()).ToJsonString()); });
            }
            m.Items.Add(del);
        }
        return m;
    }

    static List<JsonObject> SavedList()
    {
        try { return (JsonNode.Parse(Store.Str("win_saved", "[]")) as JsonArray)?.OfType<JsonObject>().ToList() ?? new(); } catch { return new(); }
    }

    void SaveSearch()
    {
        var name = Prompt.Ask(this, "★ Save this search", "Name:", Short(query.Text, 40));
        if (string.IsNullOrWhiteSpace(name)) return;
        var o = new JsonObject
        {
            ["name"] = name.Trim(), ["root"] = CurrentRoot(), ["query"] = query.Text, ["ask"] = askMode, ["mode"] = MatchMode(),
            ["case"] = cCase.Checked, ["whole"] = cWhole.Checked, ["names"] = fNames.Text, ["skip"] = fSkip.Text, ["mod"] = cbModified.SelectedIndex,
            ["size"] = cbSize.SelectedIndex, ["rx"] = cbMode.SelectedIndex, ["sub"] = cSub.Checked, ["speed"] = cbSpeed.SelectedIndex, ["kinds"] = string.Join("", kinds)
        };
        var list = SavedList().Where(x => x["name"]?.ToString() != name.Trim()).Select(x => (JsonNode)x.DeepClone()).ToList();
        list.Insert(0, o);
        Store.Set("win_saved", new JsonArray(list.Take(30).ToArray()).ToJsonString());
        status.Text = "★ Saved: " + name.Trim();
    }

    void LoadSearch(JsonObject o, bool run)
    {
        SetRoot(o["root"]?.ToString() ?? "");
        query.Text = o["query"]?.ToString() ?? "";
        var mode = o["mode"]?.ToString() ?? "phrase";
        rPhrase.Checked = mode == "phrase"; rAll.Checked = mode == "all"; rAny.Checked = mode == "any";
        cCase.Checked = o["case"]?.GetValue<bool>() ?? false; cWhole.Checked = o["whole"]?.GetValue<bool>() ?? false;
        fNames.Text = o["names"]?.ToString() ?? ""; fSkip.Text = o["skip"]?.ToString() ?? "";
        cbModified.SelectedIndex = Math.Clamp(o["mod"]?.GetValue<int>() ?? 0, 0, cbModified.Items.Count - 1);
        cbSize.SelectedIndex = Math.Clamp(o["size"]?.GetValue<int>() ?? 0, 0, cbSize.Items.Count - 1);
        cbMode.SelectedIndex = Math.Clamp(o["rx"]?.GetValue<int>() ?? 0, 0, cbMode.Items.Count - 1);
        cSub.Checked = o["sub"]?.GetValue<bool>() ?? true;
        cbSpeed.SelectedIndex = Math.Clamp(o["speed"]?.GetValue<int>() ?? 0, 0, cbSpeed.Items.Count - 1);
        kinds.Clear(); foreach (var ch in o["kinds"]?.ToString() ?? "") kinds.Add(ch.ToString());
        DrawKinds();
        SetMode(o["ask"]?.GetValue<bool>() ?? false);
        if (run) Start();
    }

    // ================================================================ cards view (as before)

    Control Card(FileHit h)
    {
        int width = Math.Max(360, cardsPanel.ClientSize.Width - 30);
        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Th.Card, Padding = new Padding(10, 8, 10, 6), Margin = new Padding(0, 0, 0, 6), MinimumSize = new Size(width, 0), MaximumSize = new Size(width, 0)
        };
        var name = Th.L($"{KindIcon(h.Kind)} {h.Name}  ·  {h.Hits:N0} hit{(h.Hits == 1 ? "" : "s")}", 10.5f, Th.Text, true);
        name.MaximumSize = new Size(width - 24, 0);
        name.Cursor = Cursors.Hand;
        name.Click += (_, _) => preview.Show(h, matcher, 0);
        card.Controls.Add(name);
        var rel = h.Folder.StartsWith(h.Root, StringComparison.OrdinalIgnoreCase) ? "." + h.Folder[h.Root.Length..] : h.Folder;
        card.Controls.Add(Th.L(rel.Length <= 1 ? "(this folder)" : rel, 8.5f, Th.Muted));
        int start = 0;
        foreach (var p in h.Pieces.Take(3))
        {
            var s = start;
            var where = Th.L("↳ " + FolderIndex.Where(p) + "  · 👁 preview", 9, Th.AccentText, true);
            where.Cursor = Cursors.Hand;
            where.Click += (_, _) => preview.Show(h, matcher, s);
            card.Controls.Add(where);
            var (pre, mid, post) = FolderIndex.Preview(p, 90);
            var rt = new RichTextBox
            {
                ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(0x1E, 0x23, 0x30), ForeColor = Color.FromArgb(0xC9, 0xCE, 0xD8),
                Font = Th.F(9.5f), ScrollBars = RichTextBoxScrollBars.None, Width = width - 30, Height = 40, Margin = new Padding(14, 2, 0, 2), DetectUrls = false, TabStop = false
            };
            rt.Text = pre + mid + post;
            if (mid.Length > 0) { rt.Select(pre.Length, mid.Length); rt.SelectionBackColor = Th.Yellow; rt.SelectionColor = Color.Black; rt.SelectionFont = Th.F(9.5f, true); rt.Select(0, 0); }
            card.Controls.Add(rt);
            start += Math.Max(1, p.Matches?.Count ?? 1);
        }
        var btns = Th.Row(false);
        btns.Controls.Add(Th.B("Open", () => Opener.Open(h, h.Pieces.FirstOrDefault(), matcher?.Query), true));
        if (string.IsNullOrEmpty(h.Remote)) btns.Controls.Add(Th.B("📁 Show in folder", () => Shell.Select(h.Path)));
        btns.Controls.Add(Th.B("⧉ Copy path", () => Clipboard.SetText(string.IsNullOrEmpty(h.Remote) ? h.Path : DriveLink(h))));
        card.Controls.Add(btns);
        return card;
    }

    // ================================================================ export

    void Export()
    {
        if (all.Count == 0) return;
        try
        {
            var f = FolderIndex.Export($"Search {query.Text}", all);
            status.Text = "⇩ Saved for Excel and as a printable page in Documents\\Nova Enhance\\Search Results";
            Shell.Select(f);
        }
        catch (Exception e) { status.Text = "⚠ " + e.Message; }
    }

    void Report()
    {
        if (all.Count == 0) return;
        try { Shell.Open(Path.ChangeExtension(FolderIndex.Export($"Search {query.Text}", all), ".html")); }
        catch (Exception e) { status.Text = "⚠ " + e.Message; }
    }

    // ================================================================ ✦ Ask AI

    static Task RunSta(Action a)
    {
        var tcs = new TaskCompletionSource();
        var t = new Thread(() => { try { a(); tcs.SetResult(); } catch (Exception e) { tcs.SetException(e); } }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    async void ToggleMic()
    {
        try
        {
            if (!voice.On)
            {
                if (!Ai.HasKey("gemini")) { status.Text = "Voice needs a Gemini key (Settings → AI & API keys)"; return; }
                voice.Start(); micBtn.Text = "■ Stop"; micBtn.BackColor = Th.Bad; status.Text = "🎤 Listening… speak, then press Stop";
                return;
            }
            var wav = voice.Stop(); micBtn.Text = "🎤"; micBtn.BackColor = Th.Card2;
            status.Text = "Turning your voice into text…";
            query.Text = await Ai.Transcribe(wav);
            if (query.Text.Trim().Length > 0) AskAi();
        }
        catch (Exception e) { micBtn.Text = "🎤"; micBtn.BackColor = Th.Card2; status.Text = "⚠ Voice: " + e.Message; }
    }

    FlowLayoutPanel instrRow;
    Conversation folderConv;
    TextBox fInstr;
    List<string> lastTerms = new();
    string searchedNote = "";
    KnowCard remembered;

    async void AskAi()
    {
        var root = CurrentRoot();
        Speller.FixBeforeSearch(query, AskAi);
        var q = query.Text.Trim();
        if (q.Length == 0 && attach.Files.Count > 0) q = attach.Files.Count >= 2 ? "Compare the attached files in a comparison table, then the key differences and your recommendation." : "Read the attached file and check it against this folder.";
        if (q.Length == 0) { status.Text = "Type your question"; query.Focus(); return; }
        if (!CheckRoot(root)) return;
        if (asking) return;
        if (!root.Equals(lastRoot, StringComparison.OrdinalIgnoreCase)) history = "";
        lastRoot = root;
        Stop();
        asking = true;
        var my = cts = new CancellationTokenSource();
        stopBtn.Enabled = true;
        fsx = null;
        ClearResults();
        answerBox.Text = ""; answerPanel.Visible = false;
        remembered = null;
        try
        {
            using var doing = Log.Doing($"Ask AI “{q}” in {GDrive.Display(root)}");

            // 🧠 Asked before? Show the remembered answer at once, then check the folder again.
            var (card, changed) = Memory.Find(q, root);
            if (card != null)
            {
                remembered = card;
                answerPanel.Visible = true;
                ShowAnswer($"🧠 NOVA REMEMBERS (you liked this answer on {card.Saved:dd MMM yyyy})\n{card.A}\n" +
                           (changed.Count > 0 ? "⚠ Changed since then: " + string.Join(", ", changed.Take(4)) + " — re-checking now…" : "↻ Re-checking the folder now…"));
            }

            status.Text = Ai.Active() != null ? "✦ Understanding your question…" : "Searching with your words (no AI key)…";
            var terms = await FolderAsk.Expand(q, history.Length > 0 ? lastQuestion : null, my.Token);
            lastTerms = terms;
            termsLbl.Text = "🔍 What Nova searched: " + string.Join(" · ", terms);
            matcher = new Matcher(string.Join(" ", terms), "any", false, false);

            pg = new FolderIndex.Progress();
            var p = pg; var opt = Options();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var boosts = Memory.Boosts(terms);
            // 🔎 One Nova search: a project folder is NOT re-read — its file names point to the few files to open (remembered for all features);
            // your emails, 🧠 Nova Memory and past answers are searched too.
            var covered = Projects.Covers(root);
            if (!covered)
            {
                bar.Visible = true; bar.Value = 0;
                await RunSta(() => FolderIndex.Search(root, null, null, _ => { }, null, p, my.Token, opt));
                bar.Visible = false;
                if (my.IsCancellationRequested) { status.Text = "■ Stopped"; return; }
                await OfferVisionAsync(root);
            }
            var ui = SynchronizationContext.Current;
            var proj = Projects.OfFolder(root);
            var folders = new List<string> { root };
            if (proj != null) folders.AddRange(Projects.General);
            var ns = await Task.Run(() => NovaSearch.Run(q, terms, new NovaSearch.Opts { Folders = folders, Project = proj, Docs = covered, Mem = false },
                stMsg => ui?.Post(_ => { if (!status.IsDisposed) status.Text = stMsg; }, null), my.Token), my.Token);
            passages = covered ? ns.Drawings.Concat(ns.Docs).ToList() : await Task.Run(() => FolderIndex.Retrieve(root, terms, 14, 4, boosts));
            passages.AddRange(ns.Emails.Take(3).Concat(ns.History.Take(1)));
            // 📎 Attached files first.
            var att = new List<FolderIndex.Passage>();
            foreach (var f in attach.Files.ToList())
            {
                var txt = await FileTalk.Text(f, this, my.Token, stMsg => status.Text = stMsg, q);
                att.Add(new FolderIndex.Passage { Kind = "file", Path = f, Root = Path.GetDirectoryName(f) ?? "", Loc = "📎 attached", Goto = "", Text = txt.Length > 7000 ? txt[..7000] : txt });
            }
            passages.InsertRange(0, att);
            if (covered) p.Files = (int)Projects.Status.Values.Where(s => (root + "\\").StartsWith(s.Folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)).Sum(s => s.Files);
            bool deep = false;
            if (passages.Count == 0)
            {
                // Deep search: wider words, more passages, file and BOQ titles.
                status.Text = "✦ Nothing yet — deep search with wider words…";
                terms = await FolderAsk.Expand(q, history.Length > 0 ? lastQuestion : null, my.Token, true);
                lastTerms = terms; deep = true;
                matcher = new Matcher(string.Join(" ", terms), "any", false, false);
                passages = await Task.Run(() => FolderIndex.Retrieve(root, terms, 40, 6, Memory.Boosts(terms), true));
            }
            answerPanel.Visible = true;
            top.FitAll();
            var superseded = passages.Count(x => Revisions.Superseded(root, x.Path));
            searchedNote = $"🔍 What Nova searched: {string.Join(" · ", terms)}\n    {p.Files:N0} files in the folder · {passages.Count} passages from {passages.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()} files" +
                           (deep ? " · deep search ✓" : "") + (superseded > 0 ? $" · {superseded} from superseded revisions (ranked last)" : "") +
                           (boosts.Count > 0 ? " · 🧠 files you confirmed before ranked first" : "");
            termsLbl.Text = searchedNote;
            if (passages.Count == 0)
            {
                answerBox.Text = "Not found in this folder, even with the deep search. Try other words, or check that the file types are switched on (⚙ File types & OCR).";
                status.Text = $"✓ Folder read · {p.Files:N0} files · nothing matched";
                return;
            }
            var nFiles = passages.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            status.Text = $"✓ {passages.Count} passages found in {nFiles} file{(nFiles == 1 ? "" : "s")} · {sw.Elapsed.TotalSeconds:0.0} s";
            ShowSources(Enumerable.Range(0, passages.Count).ToList());
            lastQuestion = q;

            var sees = FolderAsk.Sees;
            if (sees == "never" || Ai.Active() == null)
            {
                answerBox.Text = Ai.Active() == null
                    ? "Passages only — add an AI key in Settings to get answers."
                    : "Passages only — Settings → Folder search: the AI doesn't read your files. The best passages are listed below.";
                return;
            }
            if (sees == "ask" && !Confirm(nFiles)) return;
            await Answer(att.Count > 0, my.Token);

            // The extracts didn't hold the answer: one deep search with wider words (asked again before sending more).
            if (!deep && FolderAsk.NotFound(lastAnswer) && !my.IsCancellationRequested)
            {
                status.Text = "✦ Not in those passages — deep search with wider words…";
                terms = await FolderAsk.Expand(q, null, my.Token, true);
                lastTerms = terms;
                matcher = new Matcher(string.Join(" ", terms), "any", false, false);
                var more = await Task.Run(() => FolderIndex.Retrieve(root, terms, 40, 6, Memory.Boosts(terms), true));
                if (more.Count > passages.Count / 2)
                {
                    passages = more;
                    nFiles = passages.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    termsLbl.Text = searchedNote = $"🔍 What Nova searched (deep search ✓): {string.Join(" · ", terms)}\n    {p.Files:N0} files in the folder · {passages.Count} passages from {nFiles} files, file and BOQ titles included";
                    ShowSources(Enumerable.Range(0, passages.Count).ToList());
                    if (sees != "ask" || Confirm(nFiles)) await Answer(false, my.Token);
                }
                status.Text = FolderAsk.NotFound(lastAnswer) ? "Not found, even with the deep search — 👎 tells Nova which file is right" : "✓ Found with the deep search";
            }
        }
        catch (Exception e)
        {
            if (!my.IsCancellationRequested) { status.Text = "⚠ " + e.Message; Log.Error("Ask AI · folder", e); }
        }
        finally { asking = false; stopBtn.Enabled = false; bar.Visible = false; }
    }

    bool Confirm(int nFiles)
    {
        var chars = passages.Sum(x => x.Text.Length);
        var byFile = passages.GroupBy(x => Path.GetFileName(x.Path)).Select(g => $"{g.Key} ({g.Count()})");
        var choice = ConfirmSend.Ask(this, $"✦ Send {passages.Count} extracts to {Ai.LabelOf(Ai.Current())}?",
            $"To answer, Nova sends {passages.Count} short passages (about {chars:N0} characters) from {nFiles} file{(nFiles == 1 ? "" : "s")} of this folder. Nothing else leaves the PC.\n" + string.Join(" · ", byFile.Take(6)) + (nFiles > 6 ? $" · +{nFiles - 6} files" : ""),
            FolderAsk.Extracts(passages), "Send and answer", true);
        if (choice == ConfirmSend.Choice.Send) return true;
        answerBox.Text = choice == ConfirmSend.Choice.PassagesOnly ? "Passages only — nothing was sent. The best passages are listed below." : "Cancelled — nothing was sent.";
        return false;
    }

    /// <summary>👍 Nova remembers this answer, its files and their words.</summary>
    void Good()
    {
        if (lastAnswer.Length == 0 || FolderAsk.NotFound(lastAnswer)) { status.Text = "Ask a question first"; return; }
        var cited = FolderAsk.CitedOrder(lastAnswer, passages.Count).Take(6).Select(i => passages[i]).ToList();
        Memory.Good(lastQuestion, FolderAsk.WithSources(lastAnswer, passages), lastRoot, cited.Select(x => x.Path), cited.Select(x => x.Text));
        status.Text = "👍 Thanks — 🧠 Nova remembers this answer and its files for similar questions (Settings → 🧠 What Nova knows)";
    }

    /// <summary>👎 Which file has the right answer? Nova ranks it first next time and answers again from it.</summary>
    async void Bad()
    {
        if (lastQuestion.Length == 0) { status.Text = "Ask a question first"; return; }
        var files = passages.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var menu = new ContextMenuStrip { Font = Th.F(10), ShowImageMargin = false };
        menu.Items.Add(new ToolStripLabel("👎 Which file has the right answer?") { Font = Th.F(9, true), ForeColor = Color.Gray });
        string pick = null;
        var done = new TaskCompletionSource<bool>();
        foreach (var f in files.Take(15)) { var ff = f; menu.Items.Add(Path.GetFileName(ff), null, (_, _) => { pick = ff; done.TrySetResult(true); }); }
        menu.Items.Add("📂 Another file… (browse)", null, (_, _) =>
        {
            using var d = new OpenFileDialog { InitialDirectory = Directory.Exists(lastRoot) ? lastRoot : "", Title = "The file with the right answer" };
            if (d.ShowDialog(this) == DialogResult.OK) pick = d.FileName;
            done.TrySetResult(true);
        });
        menu.Items.Add("Just forget this answer", null, (_, _) => done.TrySetResult(true));
        menu.Closed += (_, e) => { if (e.CloseReason != ToolStripDropDownCloseReason.ItemClicked) done.TrySetResult(false); };
        menu.Show(Cursor.Position);
        if (!await done.Task) return;
        Memory.Bad(lastQuestion, lastRoot, pick);
        if (pick == null) { status.Text = "👎 Noted — Nova won't offer that answer again"; return; }
        status.Text = $"👎 Noted — {Path.GetFileName(pick)} ranks first for questions like this. Answering again from it…";
        var mine = await Task.Run(() => FolderIndex.Retrieve(lastRoot, lastTerms, 40, 8, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [pick] = 50 }, true));
        var fromPick = mine.Where(x => x.Path.Equals(pick, StringComparison.OrdinalIgnoreCase)).ToList();
        if (fromPick.Count == 0) { status.Text = $"👎 Noted. {Path.GetFileName(pick)} has no readable passages with these words yet (search the folder once so Nova reads it)."; return; }
        passages = fromPick.Concat(passages.Where(x => !x.Path.Equals(pick, StringComparison.OrdinalIgnoreCase))).Take(18).ToList();
        ShowSources(Enumerable.Range(0, passages.Count).ToList());
        if (FolderAsk.Sees == "never" || Ai.Active() == null) return;
        if (FolderAsk.Sees == "ask" && !Confirm(passages.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count())) return;
        history = "";
        await Answer(false, CancellationToken.None);
    }

    async void Answer(bool detailed) => await Answer(detailed, CancellationToken.None);

    async Task Answer(bool detailed, CancellationToken ct)
    {
        if (passages.Count == 0 || lastQuestion.Length == 0) return;
        if (detailed && FolderAsk.Sees == "never") { status.Text = "The AI doesn't read your files (Settings → Folder search)"; return; }
        answerPanel.Visible = true;
        answerBox.Text = detailed ? "✦ Writing the detailed answer…" : "✦ Writing the answer…";
        try
        {
            var ins = fInstr.Text.Trim();
            var qq = lastQuestion + (ins.Length > 0 ? "\n\nInstructions from the user (follow them for what to include or exclude, the format, the length and the language): " + ins : "");
            if (passages.Any(x => x.Loc == "📎 attached")) qq += "\n\nThe extracts marked “📎 attached” are the files the user attached (in this order: F1, F2…)." + FileTalk.Rules;
            var fresh = history.Length == 0 && !detailed;
            var a = await FolderAsk.Answer(qq, passages, detailed, detailed ? "" : history, ct);
            lastAnswer = a;
            // 🕘 Saved in Chat history (with its sources).
            if (fresh) folderConv = null;
            var cited = FolderAsk.CitedOrder(a, passages.Count).Take(14).ToList();
            Conversations.Add(ref folderConv, "folder", lastQuestion, lastRoot, qq, FolderAsk.WithSources(a, passages),
                cited.Select((pi, k) => new ConvSrc { Id = "D" + (k + 1), Title = FolderAsk.Label(passages[pi]), Path = passages[pi].Path, Goto = passages[pi].Goto ?? "", Loc = passages[pi].Loc ?? "" }));
            var head = detailed ? "✦ DETAILED ANSWER\n" : "✦ ANSWER\n";
            if (remembered != null && !detailed) head = $"✦ ANSWER (checked again now — 🧠 Nova also remembered an answer from {remembered.Saved:dd MMM yyyy})\n";
            ShowAnswer(head + FolderAsk.WithSources(a, passages));
            if (!detailed) history += $"Q: {lastQuestion}\nA: {a}\n";
            ShowSources(FolderAsk.CitedOrder(a, passages.Count));
            // "… export to Word" in the question or the instructions: saved at once.
            if (Prompts.Local(ins + "\n" + Prompts.Split(lastQuestion).Extra).Export.Length > 0 && !FolderAsk.NotFound(a)) SaveAnswer();
        }
        catch (Exception e) { answerBox.Text = "⚠ " + e.Message; Log.Error("Ask AI · answer", e); }
    }

    void ShowAnswer(string text)
    {
        answerBox.Text = text.Replace("\r\n", "\n");
        int i = 0;
        while ((i = answerBox.Text.IndexOf('[', i)) >= 0)
        {
            int j = answerBox.Text.IndexOf(']', i);
            if (j < 0) break;
            answerBox.Select(i, j - i + 1);
            answerBox.SelectionColor = Th.AccentText;
            answerBox.SelectionFont = Th.F(9.5f, true);
            i = j + 1;
        }
        foreach (var line in answerBox.Lines.Select((l, n) => (l, n)).Where(x => x.l.TrimStart().StartsWith("⚠")))
        {
            var start = answerBox.GetFirstCharIndexFromLine(line.n);
            answerBox.Select(start, line.l.Length);
            answerBox.SelectionColor = Th.Warn;
        }
        // 🎓 Architect's advice: general knowledge, shown apart from the answer.
        var adv = answerBox.Text.IndexOf("🎓", StringComparison.Ordinal);
        if (adv >= 0)
        {
            answerBox.Select(adv, answerBox.TextLength - adv);
            answerBox.SelectionColor = Color.FromArgb(0x8F, 0xD6, 0xB4);
            answerBox.SelectionFont = Th.F(10f);
        }
        answerBox.Select(0, 0);
        var lines = answerBox.GetLineFromCharIndex(Math.Max(0, answerBox.TextLength - 1)) + 1;
        answerBox.Height = Math.Clamp(lines * (answerBox.Font.Height + 3) + 14, 60, 380);
        answerBox.Width = Math.Max(300, top.ClientSize.Width - 40);
        top.FitAll();
    }

    /// <summary>The passages the answer used, as files on the left (select one to see its page).</summary>
    void ShowSources(List<int> order)
    {
        all.Clear(); byPath.Clear();
        foreach (var grp in order.Select(i => passages[i]).Where(x => !string.IsNullOrEmpty(x.Path)).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            var first = grp.First();
            var h = new FileHit { Path = first.Path, Root = first.Root, Kind = first.Kind, Remote = first.Remote };
            foreach (var x in grp) h.Pieces.Add(new PieceHit { Loc = x.Loc, Goto = x.Goto, Text = x.Text, Matches = matcher?.Find(x.Text) ?? new() });
            all.Add(h); byPath[h.Path] = h;
        }
        dirty = true;
        sortCol = -1;
        RefreshList();
    }

    void SaveAnswer()
    {
        if (lastAnswer.Length == 0) { status.Text = "Ask a question first"; return; }
        try { var f = FolderAsk.Save(lastQuestion, lastAnswer, passages, lastRoot); status.Text = "⇩ Saved in Documents\\Nova Enhance\\Search Results"; Shell.Select(f); }
        catch (Exception e) { status.Text = "⚠ " + e.Message; }
    }

    // ================================================================ Gemini vision for unclear scanned pages (always asked first)

    async void OfferVision(string root, Action after)
    {
        if (await OfferVisionAsync(root)) after?.Invoke();
    }

    bool visionBusy;
    readonly HashSet<string> visionDeclined = new(StringComparer.OrdinalIgnoreCase);

    async Task<bool> OfferVisionAsync(string root)
    {
        if (visionBusy || Ocr.Mode == "pc" || string.IsNullOrEmpty(root) || visionDeclined.Contains(root)) return false;
        List<(string Path, int Page)> pending;
        try { pending = await Task.Run(() => FolderIndex.VisionPending(root)); } catch { return false; }
        if (pending.Count == 0) return false;
        if (!Ai.HasKey("gemini")) { status.Text += $" · {pending.Count} scanned page(s) could be read better with Gemini vision — add a Gemini key in Settings."; return false; }
        var files = pending.GroupBy(x => Path.GetFileName(x.Path)).Select(g => $"{g.Key} ({g.Count()} page{(g.Count() == 1 ? "" : "s")})").ToList();
        var why = Ocr.Mode == "gemini" ? "scanned page(s) to read with Gemini vision" : "scanned page(s) were unclear for the PC's text recognition";
        var choice = ConfirmSend.Ask(this, $"✦ Send {pending.Count} page image(s) to Gemini vision?",
            $"{pending.Count} {why}. Nova can send the page images to Gemini to read them better; the result is remembered, so each page is sent only once.",
            string.Join("\n", files), "Send the pages", false);
        if (choice != ConfirmSend.Choice.Send) { visionDeclined.Add(root); return false; }
        visionBusy = true;
        var my = new CancellationTokenSource();
        try
        {
            int done = 0;
            await RunSta(() => done = FolderIndex.VisionRun(pending, t => BeginInvoke(() => status.Text = t), my.Token));
            status.Text = $"✓ Gemini vision read {done} page(s)";
            return done > 0;
        }
        catch (Exception e) { status.Text = "⚠ " + e.Message; return false; }
        finally { visionBusy = false; }
    }
}
