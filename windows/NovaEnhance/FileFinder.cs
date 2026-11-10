using System.Collections.Specialized;

namespace NovaEnhance;

/// <summary>
/// 📂 Find a project file: type a drawing number or a few words — every file NAME of your projects is searched as you type
/// (latest revision first, superseded ones hidden or marked). Enter opens it. Designed by A.HAMROUNI
/// </summary>
class FileFinderForm : NovaForm
{
    readonly TextBox box;
    readonly ComboBox proj;
    readonly CheckBox latest;
    readonly ListView lv;
    readonly Label info;
    readonly System.Windows.Forms.Timer debounce;
    List<Projects.FileRow> rows = new();
    int run;

    public FileFinderForm()
    {
        Text = "Nova Enhance · 📂 Find a project file";
        Width = 1180; Height = 720;
        MinimumSize = new Size(760, 420);
        KeyPreview = true;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(10, 8, 10, 4), BackColor = Th.Bg, WrapContents = false };
        box = Th.Box(false);
        if (box is SpellBox sb) sb.Off = true;
        box.Width = 520; box.Font = Th.F(12);
        box.PlaceholderText = "Drawing number or words — e.g.  SLV-MZA-AR-DD-1203   ·   door schedule   ·   fire strategy";
        proj = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10), FlatStyle = FlatStyle.Flat };
        proj.Items.Add("All projects");
        foreach (var p in Projects.All()) proj.Items.Add($"{p.Code} · {p.Name}");
        proj.Items.Add("General reference");
        proj.SelectedIndex = 0;
        latest = new CheckBox { Text = "Latest revision only", Checked = true, AutoSize = true, ForeColor = Th.Text, Font = Th.F(10), Margin = new Padding(10, 8, 4, 0) };
        top.Controls.Add(box);
        top.Controls.Add(proj);
        top.Controls.Add(latest);

        lv = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, BackColor = Th.Card, ForeColor = Th.Text,
            BorderStyle = BorderStyle.None, Font = Th.F(10), HideSelection = false
        };
        foreach (var (h, w) in new[] { ("File", 430), ("Project", 80), ("Rev", 60), ("Status", 110), ("Modified", 110), ("Size", 80), ("Folder", 520) })
            lv.Columns.Add(h, w);
        lv.DoubleClick += (_, _) => OpenSel();
        lv.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenSel(); e.Handled = true; } };
        lv.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && Sel().Count > 0) Menu().Show(lv, e.Location); };

        info = new Label { Dock = DockStyle.Bottom, Height = 28, ForeColor = Th.Muted, Font = Th.F(9.5f), Padding = new Padding(10, 6, 10, 0), BackColor = Th.Bg };
        Controls.Add(lv);
        Controls.Add(top);
        Controls.Add(info);

        debounce = new System.Windows.Forms.Timer { Interval = 220 };
        debounce.Tick += (_, _) => { debounce.Stop(); Find(); };
        box.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        proj.SelectedIndexChanged += (_, _) => Find();
        latest.CheckedChanged += (_, _) => Find();
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && lv.Items.Count > 0) { lv.Focus(); lv.Items[0].Selected = true; lv.Items[0].Focused = true; e.Handled = true; }
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; debounce.Stop(); Find(true); }
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Shown += (_, _) => { box.Focus(); Hint(); };
    }

    void Hint()
    {
        var n = Projects.Status.Values.Sum(s => s.Files);
        info.Text = Projects.All().Count == 0
            ? "No projects yet — Settings → 📁 Projects → 🔍 Scan my work folder."
            : $"{n:N0} file names ready · type to search · Enter or double-click opens · right-click for more · Esc closes";
    }

    /// <summary>Opens with a search already typed (e.g. a drawing number).</summary>
    public void Search(string q) { box.Text = q ?? ""; box.SelectionStart = box.Text.Length; box.Focus(); Find(true); }

    List<string> Folders()
    {
        var all = Projects.All();
        if (proj.SelectedIndex <= 0) return all.SelectMany(p => p.Folders).Concat(Projects.General).ToList();
        if (proj.SelectedIndex == proj.Items.Count - 1) return Projects.General;
        var p = all.ElementAtOrDefault(proj.SelectedIndex - 1);
        return p?.Folders ?? new();
    }

    async void Find(bool openIfOne = false)
    {
        var q = box.Text.Trim();
        var my = ++run;
        if (q.Length < 2) { lv.Items.Clear(); rows.Clear(); Hint(); return; }
        var folders = Folders();
        var only = latest.Checked;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = await Task.Run(() => Projects.FindFiles(q, folders, true, only, 400));
        if (my != run || IsDisposed) return;
        rows = r;
        lv.BeginUpdate();
        lv.Items.Clear();
        foreach (var f in rows)
        {
            var it = new ListViewItem(new[]
            {
                f.Name, f.Project, f.Rev, f.Status == "latest" ? "✓ latest" : f.Status == "superseded" ? "superseded" : f.Status,
                f.Modified > DateTime.MinValue ? f.Modified.ToString("dd MMM yyyy") : "", Projects.Size(f.Size), Path.GetDirectoryName(f.Path) ?? ""
            }) { Tag = f };
            if (f.Status == "superseded" || f.Status.StartsWith("in an old")) it.ForeColor = Th.Muted;
            lv.Items.Add(it);
        }
        lv.EndUpdate();
        info.Text = rows.Count == 0
            ? $"No file name matches “{q}” — try fewer words, part of the number, or untick “Latest revision only”."
            : $"{rows.Count}{(rows.Count >= 400 ? "+" : "")} file(s) · {clock.ElapsedMilliseconds} ms · Enter or double-click opens · right-click: show in folder, copy, attach to an email";
        if (openIfOne && rows.Count == 1) Shell.Open(rows[0].Path);
        else if (openIfOne && rows.Count > 0) { lv.Focus(); lv.Items[0].Selected = true; lv.Items[0].Focused = true; }
    }

    List<Projects.FileRow> Sel() => lv.SelectedItems.Cast<ListViewItem>().Select(i => (Projects.FileRow)i.Tag).ToList();

    void OpenSel() { foreach (var f in Sel().Take(5)) Shell.Open(f.Path); }

    ContextMenuStrip Menu()
    {
        var s = Sel();
        var m = new ContextMenuStrip { Font = Th.F(10), ShowImageMargin = false };
        m.Items.Add("Open", null, (_, _) => OpenSel());
        m.Items.Add("Show in folder", null, (_, _) => Shell.Select(s[0].Path));
        m.Items.Add("⧉ Copy the file(s) — paste into an email or a folder", null, (_, _) =>
        {
            var c = new StringCollection();
            c.AddRange(s.Select(x => x.Path).ToArray());
            Clipboard.SetFileDropList(c);
        });
        m.Items.Add("⧉ Copy the path", null, (_, _) => Clipboard.SetText(string.Join(Environment.NewLine, s.Select(x => x.Path))));
        m.Items.Add("✉ Attach to a new email", null, (_, _) =>
        {
            try { Outlook.NewWithAttachments(s.Select(x => x.Path).ToList()); }
            catch (Exception e) { MessageBox.Show(this, "⚠ " + e.Message, "Nova Enhance"); }
        });
        m.Items.Add("🕘 Show all revisions of this document", null, (_, _) =>
        {
            latest.Checked = false;
            box.Text = Revisions.Parse(s[0].Path, 0).Key;
        });
        return m;
    }
}
