using System.Collections.Concurrent;

namespace NovaEnhance;

/// <summary>Right-click a folder → "Search inside with Nova": codes, references or sentences inside PDF, Word, PowerPoint, Excel and more.</summary>
class FolderSearchForm : NovaForm
{
    static readonly (string Key, string Label)[] KindChips =
    {
        ("A", "📝 Word"), ("B", "📽 PowerPoint"), ("C", "📊 Excel"), ("D", "📕 PDF"), ("E", "🖼 Images"),
        ("F", "✉ Emails"), ("G", "📄 Text"), ("H", "📐 CAD / BIM"), ("I", "🗜 Zip"), ("J", "📁 Other files")
    };

    readonly Stack stack;
    readonly TextBox folder, query;
    readonly Label status, summary;
    readonly ProgressBar bar;
    readonly FlowLayoutPanel results, kindRow;
    readonly Button stopBtn, searchBtn;
    readonly RadioButton rPhrase, rAll, rAny;
    readonly CheckBox cCase, cWhole;
    readonly HashSet<string> kinds = new();
    readonly Dictionary<string, Button> kindBtns = new();
    readonly ConcurrentQueue<(FileHit Hit, string Removed)> inbox = new();
    readonly Dictionary<string, (FileHit Hit, Control Card)> shown = new(StringComparer.OrdinalIgnoreCase);
    readonly List<FileHit> all = new();
    readonly System.Windows.Forms.Timer pump;
    FolderIndex.Progress pg;
    CancellationTokenSource cts;
    Thread worker;
    Matcher matcher;
    const int MaxCards = 300;
    string driveRoot;
    bool askMode, asking;
    readonly Button tabFind, tabAsk, micBtn;
    readonly FlowLayoutPanel optRow, answerPanel;
    readonly Label termsLbl, intro;
    readonly RichTextBox answerBox;
    readonly Voice voice = new();
    List<FolderIndex.Passage> passages = new();
    string lastQuestion = "", lastAnswer = "", history = "", lastRoot = "";

    public FolderSearchForm()
    {
        Text = "Nova Enhance · Search inside a folder";
        Width = 940; Height = 820;
        MinimumSize = new Size(700, 560);
        KeyPreview = true;
        stack = new Stack();
        Controls.Add(stack);

        stack.Add(Th.L("📁 Search inside a folder", 14, Th.Text, true));
        intro = stack.Add(Th.Wrap("", 9.5f));
        var tabs = stack.Add(Th.Row(false));
        tabFind = Th.Chip("🔍 Find words", true, () => SetMode(false));
        tabAsk = Th.Chip("✦ Ask AI", false, () => SetMode(true));
        tabs.Controls.Add(tabFind); tabs.Controls.Add(tabAsk);

        var frow = stack.Add(new TableLayoutPanel { ColumnCount = 4, RowCount = 1, AutoSize = false, Height = 40, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 4) }, true);
        frow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        frow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folder = Th.Box(false); folder.Dock = DockStyle.Fill; folder.PlaceholderText = "Folder to search";
        frow.Controls.Add(folder, 0, 0);
        var browse = Th.B("Change…", Browse); browse.Margin = new Padding(8, 0, 0, 0);
        frow.Controls.Add(browse, 1, 0);
        var recent = Th.B("Recent ▾", null); recent.Margin = new Padding(8, 0, 0, 0);
        recent.Click += (_, _) => RecentMenu().Show(recent, new Point(0, recent.Height));
        frow.Controls.Add(recent, 2, 0);
        frow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var gd = Th.B("☁ Google Drive…", PickDrive); gd.Margin = new Padding(8, 0, 0, 0);
        frow.Controls.Add(gd, 3, 0);

        var qrow = stack.Add(new TableLayoutPanel { ColumnCount = 4, RowCount = 1, AutoSize = false, Height = 42, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 4) }, true);
        qrow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        qrow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        qrow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        query = Th.Box(false); query.Font = Th.F(12); query.Dock = DockStyle.Fill;
        query.PlaceholderText = "e.g. A-201 Rev C · RFI-044 · fire rating of the curtain wall";
        qrow.Controls.Add(query, 0, 0);
        searchBtn = Th.B("🔍 Search", Start, true); searchBtn.Margin = new Padding(8, 0, 0, 0);
        qrow.Controls.Add(searchBtn, 1, 0);
        stopBtn = Th.B("■ Stop", Stop); stopBtn.Margin = new Padding(8, 0, 0, 0); stopBtn.Enabled = false;
        qrow.Controls.Add(stopBtn, 2, 0);
        qrow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        micBtn = Th.B("🎤", ToggleMic); micBtn.Margin = new Padding(8, 0, 0, 0); micBtn.Visible = false;
        qrow.Controls.Add(micBtn, 3, 0);

        var orow = optRow = stack.Add(Th.Row());
        var mode = Store.Str("win_fs_mode", "phrase");
        rPhrase = Th.Radio("Exact phrase", mode == "phrase", () => Store.Set("win_fs_mode", "phrase"));
        rAll = Th.Radio("All words", mode == "all", () => Store.Set("win_fs_mode", "all"));
        rAny = Th.Radio("Any word", mode == "any", () => Store.Set("win_fs_mode", "any"));
        cCase = Th.Check("Match case", Store.Bool("win_fs_case", false), v => Store.SetBool("win_fs_case", v));
        cWhole = Th.Check("Whole word", Store.Bool("win_fs_whole", false), v => Store.SetBool("win_fs_whole", v));
        foreach (Control c in new Control[] { rPhrase, rAll, rAny, cCase, cWhole }) orow.Controls.Add(c);

        kindRow = stack.Add(Th.Row());
        var allBtn = Th.Chip("All types", true, null);
        allBtn.Click += (_, _) => { kinds.Clear(); DrawKinds(); };
        kindBtns["*"] = allBtn;
        kindRow.Controls.Add(allBtn);
        foreach (var (k, l) in KindChips)
        {
            var kk = k;
            var b = Th.Chip(l, false, () => { if (!kinds.Remove(kk)) kinds.Add(kk); DrawKinds(); });
            kindBtns[k] = b;
            kindRow.Controls.Add(b);
        }

        answerPanel = stack.Add(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 4), Visible = false });
        termsLbl = Th.L("", 9, Th.Muted);
        answerPanel.Controls.Add(termsLbl);
        answerBox = new RichTextBox
        {
            ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10.5f),
            ScrollBars = RichTextBoxScrollBars.Vertical, Height = 150, Margin = new Padding(0, 6, 0, 6), DetectUrls = false
        };
        answerPanel.Controls.Add(answerBox);
        var arow = Th.Row(false);
        arow.Controls.Add(Th.B("▾ More detail", () => Answer(true), true));
        arow.Controls.Add(Th.B("⧉ Copy", () => { if (answerBox.Text.Length > 0) Clipboard.SetText(answerBox.Text); }));
        arow.Controls.Add(Th.B("⇩ Save with sources", SaveAnswer));
        arow.Controls.Add(Th.B("✚ New question", () => { history = ""; lastQuestion = ""; query.Text = ""; answerBox.Text = ""; query.Focus(); status.Text = "New question — the conversation was cleared"; }));
        answerPanel.Controls.Add(arow);
        bar = stack.Add(new ProgressBar { Height = 6, Style = ProgressBarStyle.Continuous, Visible = false, Margin = new Padding(0, 6, 0, 2) }, true);
        status = stack.Add(Th.Wrap("", 9.5f, Th.AccentText));
        summary = stack.Add(Th.Wrap("", 9.5f, Th.Muted));
        results = stack.Add(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0) });

        var bottom = stack.Add(Th.Row(false));
        bottom.Controls.Add(Th.B("⇩ Export list", Export));
        bottom.Controls.Add(Th.B("📁 Search Results folder", () => Shell.Open(Path.Combine(Paths.Root, "Search Results"))));
        bottom.Controls.Add(Th.B("⚙ File types", () => Screens.Settings("Folder search")));
        stack.Add(Th.Signature());

        query.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Start(); } };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { if (worker != null) Stop(); else Hide(); } };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Stop(); Hide(); } };
        Resize += (_, _) => { answerBox.Width = Math.Max(300, stack.ClientSize.Width - 52); };
        SetMode(false);
        pump = new System.Windows.Forms.Timer { Interval = 350 };
        pump.Tick += (_, _) => Flush();
        pump.Start();
    }

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

    void SetMode(bool ask)
    {
        askMode = ask;
        Th.SetChip(tabFind, !ask); Th.SetChip(tabAsk, ask);
        optRow.Visible = !ask; kindRow.Visible = !ask; micBtn.Visible = ask;
        answerPanel.Visible = ask && answerBox.Text.Length > 0;
        searchBtn.Text = ask ? "✦ Ask" : "🔍 Search";
        query.PlaceholderText = ask ? "e.g. What is the fire rating required for the curtain wall?" : "e.g. A-201 Rev C · RFI-044 · fire rating of the curtain wall";
        intro.Text = ask
            ? "Ask in your own words: the AI answers from the files of this folder, with the file and page of every point. You are asked before any extract is sent."
            : "Finds a code, a reference or a sentence inside PDF, Word, PowerPoint, Excel, emails, CAD/BIM text, zip files and more. Everything is read on this PC.";
        answerBox.Width = Math.Max(300, stack.ClientSize.Width - 52);
        stack.FitAll();
    }

    void DrawKinds()
    {
        Th.SetChip(kindBtns["*"], kinds.Count == 0);
        foreach (var (k, _) in KindChips) Th.SetChip(kindBtns[k], kinds.Contains(k));
    }

    void Browse()
    {
        using var d = new FolderBrowserDialog { Description = "Folder to search", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(folder.Text) ? folder.Text : "" };
        if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath;
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

    void Start()
    {
        if (askMode) { AskAi(); return; }
        var root = CurrentRoot();
        var q = query.Text.Trim();
        if (q.Length == 0) { status.Text = "Type what you're looking for"; query.Focus(); return; }
        if (!CheckRoot(root)) return;
        lastRoot = root;
        answerPanel.Visible = false;
        Stop();
        var mode = rAll.Checked ? "all" : rAny.Checked ? "any" : "phrase";
        matcher = new Matcher(q, mode, cCase.Checked, cWhole.Checked);
        foreach (var v in shown.Values) v.Card.Dispose();
        shown.Clear(); all.Clear(); results.Controls.Clear();
        while (inbox.TryDequeue(out _)) { }
        pg = new FolderIndex.Progress();
        var my = cts = new CancellationTokenSource();
        var k = new HashSet<string>(kinds);
        var m = matcher;
        bar.Visible = true; bar.Value = 0;
        stopBtn.Enabled = true;
        status.Text = "Searching…";
        summary.Text = "";
        worker = new Thread(() =>
        {
            try { FolderIndex.Search(root, m, k, h => inbox.Enqueue((h, null)), p => inbox.Enqueue((null, p)), pg, my.Token); }
            catch (Exception e) { pg.Phase = "⚠ " + e.Message; pg.Finished = true; Log.Error("folder search", e); }
        }) { IsBackground = true, Name = "Nova folder search", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    void Stop()
    {
        cts?.Cancel();
        stopBtn.Enabled = false;
    }

    void Flush()
    {
        if (!Visible) return;
        int added = 0;
        results.SuspendLayout();
        while (added < 40 && inbox.TryDequeue(out var item))
        {
            if (item.Removed != null)
            {
                if (shown.TryGetValue(item.Removed, out var old)) { old.Card.Dispose(); shown.Remove(item.Removed); all.Remove(old.Hit); }
                continue;
            }
            var h = item.Hit;
            if (shown.TryGetValue(h.Path, out var prev)) { prev.Card.Dispose(); all.Remove(prev.Hit); shown.Remove(h.Path); }
            all.Add(h);
            if (shown.Count < MaxCards)
            {
                var card = Card(h);
                results.Controls.Add(card);
                shown[h.Path] = (h, card);
            }
            added++;
        }
        results.ResumeLayout();
        if (pg == null) return;
        int pieces = all.Sum(h => h.Pieces.Count);
        if (!pg.Finished)
        {
            if (pg.Total > 0) { bar.Maximum = Math.Max(1, pg.Total); bar.Value = Math.Min(pg.Done, bar.Maximum); }
            status.Text = pg.Total > 0
                ? $"{pg.Phase} {pg.Done:N0} / {pg.Total:N0} files · {pg.Read:N0} read now · {all.Count:N0} files found" + (pg.Current.Length > 0 ? $" · {Short(pg.Current, 40)}" : "")
                : pg.Phase;
        }
        else if (worker != null && !asking)
        {
            worker = null;
            bar.Visible = false;
            stopBtn.Enabled = false;
            var cancelled = cts?.IsCancellationRequested == true;
            status.Text = (cancelled ? "■ Stopped · " : "✓ ") + (all.Count == 0
                ? $"Not found in {pg.Files:N0} files"
                : $"Found in {all.Count:N0} file{(all.Count == 1 ? "" : "s")} ({pieces:N0} place{(pieces == 1 ? "" : "s")})") + $" · {pg.Seconds:0.0} s";
            summary.Text = (pg.Read > 0 ? $"{pg.Read:N0} new or changed files read and remembered · " : "") +
                           "next search in this folder is instant" + (all.Count > MaxCards ? $" · showing the first {MaxCards} files — ⇩ Export for all" : "");
            if (pg.Phase.StartsWith("⚠")) status.Text = pg.Phase;
            if (!cancelled) OfferVision(lastRoot, () => Start());
        }
    }

    static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    static string KindIcon(string kind) => kind switch { "A" => "📝", "B" => "📽", "C" => "📊", "D" => "📕", "E" => "🖼", "F" => "✉", "G" => "📄", "H" => "📐", "I" => "🗜", _ => "📁" };

    Control Card(FileHit h)
    {
        int width = Math.Max(400, stack.ClientSize.Width - 52);
        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Th.Card, Padding = new Padding(10, 8, 10, 6), Margin = new Padding(0, 0, 0, 6), MinimumSize = new Size(width, 0), MaximumSize = new Size(width, 0)
        };
        var top = Th.Row(false);
        var name = Th.L($"{KindIcon(h.Kind)} {h.Name}", 10.5f, Th.Text, true);
        name.MaximumSize = new Size(width - 160, 0);
        name.Cursor = Cursors.Hand;
        name.Click += (_, _) => Opener.Open(h, h.Pieces.FirstOrDefault(), matcher?.Query);
        top.Controls.Add(name);
        top.Controls.Add(Th.L($"{h.Pieces.Count} place{(h.Pieces.Count == 1 ? "" : "s")}", 9, Th.AccentText));
        card.Controls.Add(top);
        var rel = h.Folder.StartsWith(h.Root, StringComparison.OrdinalIgnoreCase) ? "." + h.Folder[h.Root.Length..] : h.Folder;
        var fl = Th.L(rel.Length <= 1 ? "(this folder)" : rel, 8.5f, Th.Muted);
        fl.MaximumSize = new Size(width - 24, 0);
        card.Controls.Add(fl);
        var piecesBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        card.Controls.Add(piecesBox);
        void ShowPieces(int n)
        {
            piecesBox.SuspendLayout();
            piecesBox.Controls.Clear();
            foreach (var p in h.Pieces.Take(n)) piecesBox.Controls.Add(PieceRow(h, p, width - 24));
            if (h.Pieces.Count > n)
            {
                var more = Th.B($"+ {h.Pieces.Count - n} more in this file", () => ShowPieces(h.Pieces.Count));
                more.Font = Th.F(9);
                piecesBox.Controls.Add(more);
            }
            piecesBox.ResumeLayout();
        }
        ShowPieces(3);
        var btns = Th.Row(false);
        btns.Margin = new Padding(0, 4, 0, 0);
        btns.Controls.Add(Th.B("Open", () => Opener.Open(h, h.Pieces.FirstOrDefault(), matcher?.Query), true));
        if (string.IsNullOrEmpty(h.Remote))
        {
            btns.Controls.Add(Th.B("📁 Show in folder", () => Shell.Select(h.Path)));
            btns.Controls.Add(Th.B("⧉ Copy path", () => Clipboard.SetText(h.Path)));
        }
        else
        {
            var parts = h.Remote.Split('|');
            btns.Controls.Add(Th.B("⧉ Copy link", () => Clipboard.SetText(GDrive.WebLink(parts[0], parts.Length > 1 ? parts[1] : ""))));
        }
        card.Controls.Add(btns);
        return card;
    }

    Control PieceRow(FileHit h, PieceHit p, int width)
    {
        var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2) };
        var where = Th.L("↳ " + FolderIndex.Where(p) + "  · open here", 9, Th.AccentText, true);
        where.Cursor = Cursors.Hand;
        where.Click += (_, _) => Opener.Open(h, p, matcher?.Query);
        box.Controls.Add(where);
        var (pre, mid, post) = FolderIndex.Preview(p, 110);
        var rt = new RichTextBox
        {
            ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(0x1E, 0x23, 0x30), ForeColor = Color.FromArgb(0xC9, 0xCE, 0xD8),
            Font = Th.F(9.5f), ScrollBars = RichTextBoxScrollBars.None, Width = width, Height = 44, Margin = new Padding(14, 2, 0, 2),
            DetectUrls = false, TabStop = false, Cursor = Cursors.Default
        };
        rt.Text = pre + mid + post;
        if (mid.Length > 0)
        {
            rt.Select(pre.Length, mid.Length);
            rt.SelectionBackColor = Th.Yellow;
            rt.SelectionColor = Color.Black;
            rt.SelectionFont = Th.F(9.5f, true);
            rt.Select(0, 0);
        }
        var lines = rt.GetLineFromCharIndex(Math.Max(0, rt.TextLength - 1)) + 1;
        rt.Height = Math.Min(4, Math.Max(1, lines)) * (rt.Font.Height + 2) + 6;
        box.Controls.Add(rt);
        return box;
    }

    // ================================================================== ✦ Ask AI

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

    async void AskAi()
    {
        var root = CurrentRoot();
        var q = query.Text.Trim();
        if (q.Length == 0) { status.Text = "Type your question"; query.Focus(); return; }
        if (!CheckRoot(root)) return;
        if (asking) return;
        if (!root.Equals(lastRoot, StringComparison.OrdinalIgnoreCase)) history = "";
        lastRoot = root;
        Stop();
        asking = true;
        var my = cts = new CancellationTokenSource();
        stopBtn.Enabled = true;
        foreach (var v in shown.Values) v.Card.Dispose();
        shown.Clear(); all.Clear(); results.Controls.Clear();
        summary.Text = ""; answerBox.Text = ""; answerPanel.Visible = false;
        try
        {
            status.Text = Ai.Active() != null ? "✦ Understanding your question…" : "Searching with your words (no AI key)…";
            var terms = await FolderAsk.Expand(q, history.Length > 0 ? lastQuestion : null, my.Token);
            termsLbl.Text = "Searched for: " + string.Join(" · ", terms);

            pg = new FolderIndex.Progress();
            bar.Visible = true; bar.Value = 0;
            var p = pg;
            await RunSta(() => FolderIndex.Search(root, null, null, _ => { }, null, p, my.Token));
            bar.Visible = false;
            if (my.IsCancellationRequested) { status.Text = "■ Stopped"; return; }
            await OfferVisionAsync(root);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            passages = await Task.Run(() => FolderIndex.Retrieve(root, terms, 14));
            answerPanel.Visible = true;
            if (passages.Count == 0)
            {
                answerBox.Text = "Not found in this folder. Try other words, or check that the file types are switched on (⚙ File types).";
                status.Text = $"✓ Folder read · {p.Files:N0} files · nothing matched";
                return;
            }
            var nFiles = passages.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            status.Text = $"✓ {passages.Count} passages found in {nFiles} file{(nFiles == 1 ? "" : "s")} · {sw.Elapsed.TotalSeconds:0.0} s";
            ShowSources(Enumerable.Range(0, passages.Count).ToList(), terms);
            lastQuestion = q;

            var sees = FolderAsk.Sees;
            if (sees == "never" || Ai.Active() == null)
            {
                answerBox.Text = Ai.Active() == null
                    ? "Passages only — add an AI key in Settings to get answers."
                    : "Passages only — Settings → Folder search: the AI doesn't read your files. The best passages are listed below.";
                return;
            }
            if (sees == "ask")
            {
                var chars = passages.Sum(x => x.Text.Length);
                var byFile = passages.GroupBy(x => Path.GetFileName(x.Path)).Select(g => $"{g.Key} ({g.Count()})");
                var choice = ConfirmSend.Ask(this, $"✦ Send {passages.Count} extracts to {Ai.Active().Label}?",
                    $"To answer, Nova sends {passages.Count} short passages (about {chars:N0} characters) from {nFiles} file{(nFiles == 1 ? "" : "s")} of this folder. Nothing else leaves the PC.\n" + string.Join(" · ", byFile.Take(6)) + (nFiles > 6 ? $" · +{nFiles - 6} files" : ""),
                    FolderAsk.Extracts(passages), "Send and answer", true);
                if (choice != ConfirmSend.Choice.Send)
                {
                    answerBox.Text = choice == ConfirmSend.Choice.PassagesOnly ? "Passages only — nothing was sent. The best passages are listed below." : "Cancelled — nothing was sent.";
                    return;
                }
            }
            await Answer(false, my.Token);
        }
        catch (Exception e)
        {
            if (!my.IsCancellationRequested) { status.Text = "⚠ " + e.Message; Log.Error("ask folder", e); }
        }
        finally { asking = false; stopBtn.Enabled = false; bar.Visible = false; }
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
            var a = await FolderAsk.Answer(lastQuestion, passages, detailed, detailed ? "" : history, ct);
            lastAnswer = a;
            ShowAnswer((detailed ? "✦ DETAILED ANSWER\n" : "✦ ANSWER\n") + FolderAsk.WithSources(a, passages));
            if (!detailed) history += $"Q: {lastQuestion}\nA: {a}\n";
            ShowSources(FolderAsk.CitedOrder(a, passages.Count), FolderAsk.LocalTerms(lastQuestion));
        }
        catch (Exception e) { answerBox.Text = "⚠ " + e.Message; }
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
        answerBox.Select(0, 0);
        var lines = answerBox.GetLineFromCharIndex(Math.Max(0, answerBox.TextLength - 1)) + 1;
        answerBox.Height = Math.Clamp(lines * (answerBox.Font.Height + 3) + 14, 60, 420);
        answerBox.Width = Math.Max(300, stack.ClientSize.Width - 52);
        stack.FitAll();
    }

    void ShowSources(List<int> order, List<string> terms)
    {
        foreach (var v in shown.Values) v.Card.Dispose();
        shown.Clear(); all.Clear(); results.Controls.Clear();
        var m = new Matcher(string.Join(" ", terms), "any", false, false);
        results.SuspendLayout();
        foreach (var grp in order.Select(i => passages[i]).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            var first = grp.First();
            var h = new FileHit { Path = first.Path, Root = first.Root, Kind = first.Kind, Remote = first.Remote };
            foreach (var x in grp) h.Pieces.Add(new PieceHit { Loc = x.Loc, Goto = x.Goto, Text = x.Text, Matches = m.Find(x.Text) });
            all.Add(h);
            var card = Card(h);
            results.Controls.Add(card);
            shown[h.Path] = (h, card);
        }
        results.ResumeLayout();
    }

    void SaveAnswer()
    {
        if (lastAnswer.Length == 0) { status.Text = "Ask a question first"; return; }
        try { var f = FolderAsk.Save(lastQuestion, lastAnswer, passages, lastRoot); status.Text = "⇩ Saved in Documents\\Nova Enhance\\Search Results"; Shell.Select(f); }
        catch (Exception e) { status.Text = "⚠ " + e.Message; }
    }

    // ================================================================== Gemini vision for unclear scanned pages (always asked first)

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
        if (!Ai.HasKey("gemini")) { summary.Text = $"{pending.Count} scanned page(s) could be read better with Gemini vision — add a Gemini key in Settings."; return false; }
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
}
