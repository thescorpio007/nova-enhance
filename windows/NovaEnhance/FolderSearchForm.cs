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

    public FolderSearchForm()
    {
        Text = "Nova Enhance · Search inside a folder";
        Width = 940; Height = 820;
        MinimumSize = new Size(700, 560);
        KeyPreview = true;
        stack = new Stack();
        Controls.Add(stack);

        stack.Add(Th.L("📁 Search inside a folder", 14, Th.Text, true));
        stack.Add(Th.Wrap("Finds a code, a reference or a sentence inside PDF, Word, PowerPoint, Excel, emails, CAD/BIM text, zip files and more. Everything is read on this PC.", 9.5f));

        var frow = stack.Add(new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = false, Height = 40, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 4) }, true);
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

        var qrow = stack.Add(new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = false, Height = 42, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 4) }, true);
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

        var orow = stack.Add(Th.Row());
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
        pump = new System.Windows.Forms.Timer { Interval = 350 };
        pump.Tick += (_, _) => Flush();
        pump.Start();
    }

    public void Begin(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) folder.Text = path.Trim('"');
        if (folder.Text.Length == 0) folder.Text = FolderIndex.Roots().FirstOrDefault().Path ?? "";
        query.Focus();
        query.SelectAll();
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
            m.Items.Add($"{p}   ·  {files:N0} files · {last:dd MMM}", null, (_, _) => folder.Text = pp);
        }
        return m;
    }

    void Start()
    {
        var root = folder.Text.Trim().Trim('"');
        var q = query.Text.Trim();
        if (q.Length == 0) { status.Text = "Type what you're looking for"; query.Focus(); return; }
        if (!Directory.Exists(root)) { status.Text = "⚠ This folder doesn't exist — choose it with Change…"; return; }
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
        else if (worker != null)
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
        btns.Controls.Add(Th.B("📁 Show in folder", () => Shell.Select(h.Path)));
        btns.Controls.Add(Th.B("⧉ Copy path", () => Clipboard.SetText(h.Path)));
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
