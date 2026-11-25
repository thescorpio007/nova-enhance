namespace NovaEnhance;

/// <summary>
/// ☑ A filter that always offers ALL its choices (not only those in the results): each with how many files have it now,
/// the ones with none greyed but still pickable, several ticked at once, and a 🔎 box at the top that narrows the list as you type.
/// Used by 📂 Find a project file (Type, Document, Discipline, Level). Designed by A.HAMROUNI
/// </summary>
class FacetButton : Button
{
    readonly string name;
    readonly Func<IEnumerable<string>> all;
    readonly Func<Dictionary<string, int>> counts;
    public HashSet<string> Selected = new(StringComparer.OrdinalIgnoreCase);
    public event Action Changed;

    public FacetButton(string name, int width, Func<IEnumerable<string>> all, Func<Dictionary<string, int>> counts)
    {
        this.name = name; this.all = all; this.counts = counts;
        FlatStyle = FlatStyle.Flat; BackColor = Th.Card; ForeColor = Th.Text; Font = Th.F(9.5f); Width = width; Height = 26;
        TextAlign = ContentAlignment.MiddleLeft; Margin = new Padding(2, 4, 8, 2); UseMnemonic = false; Cursor = Cursors.Hand;
        FlatAppearance.BorderColor = Th.Line;
        Refresh2();
        Click += (_, _) => Open();
    }

    public void Set(HashSet<string> v) { Selected = new HashSet<string>(v ?? new(), StringComparer.OrdinalIgnoreCase); Refresh2(); }

    public void Refresh2()
    {
        Text = (Selected.Count == 0 ? "Any" : string.Join(", ", Selected)) + "  ▾";
        BackColor = Selected.Count == 0 ? Th.Card : Color.FromArgb(0x2E, 0x2A, 0x55);
    }

    void Open()
    {
        var cnt = counts() ?? new();
        var values = all().Concat(cnt.Keys).Concat(Selected).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => cnt.TryGetValue(v, out var n) ? n : 0).ThenBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        var pop = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, BackColor = Th.Line,
            Padding = new Padding(1), Size = new Size(Math.Max(280, Width + 60), Math.Min(460, 96 + values.Count * 22)), KeyPreview = true, TopMost = FindForm()?.TopMost == true
        };
        var inner = new Panel { Dock = DockStyle.Fill, BackColor = Th.Card, Padding = new Padding(6) };
        var find = Th.Box(false); if (find is SpellBox sb) sb.Off = true;
        find.Dock = DockStyle.Top; find.Font = Th.F(9.5f); find.PlaceholderText = $"🔎 Filter the {name.ToLowerInvariant()} list…";
        var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(9.5f) };
        lv.Columns.Add("", pop.Width - 40);
        var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Th.Card, Padding = new Padding(0, 4, 0, 0) };
        bool filling = false;
        void Fill()
        {
            filling = true;
            lv.BeginUpdate(); lv.Items.Clear();
            var f = find.Text.Trim();
            foreach (var v in values.Where(v => f.Length == 0 || v.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                var n = cnt.TryGetValue(v, out var x) ? x : 0;
                var it = new ListViewItem($"{v}   ({n})") { Tag = v, Checked = Selected.Contains(v), ForeColor = n > 0 ? Th.Text : Th.Muted };
                lv.Items.Add(it);
            }
            lv.EndUpdate();
            filling = false;
        }
        lv.ItemChecked += (_, e) =>
        {
            if (filling) return;
            var v = (string)e.Item.Tag;
            if (e.Item.Checked) Selected.Add(v); else Selected.Remove(v);
            Refresh2(); Changed?.Invoke();
        };
        find.TextChanged += (_, _) => Fill();
        find.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && lv.Items.Count > 0) { lv.Focus(); lv.Items[0].Selected = true; e.Handled = true; }
            if (e.KeyCode == Keys.Enter && lv.Items.Count > 0) { e.SuppressKeyPress = true; lv.Items[0].Checked = !lv.Items[0].Checked; }   // Enter ticks the first match
        };
        var any = Th.B("Any (clear)", () => { Selected.Clear(); Refresh2(); Changed?.Invoke(); Fill(); });
        var done = Th.B("✓ Done", () => pop.Close(), true);
        foreach (var b in new[] { any, done }) { b.MinimumSize = new Size(0, 26); b.Font = Th.F(8.5f, true); }
        btns.Controls.Add(any); btns.Controls.Add(done);
        inner.Controls.Add(lv); inner.Controls.Add(btns); inner.Controls.Add(find);
        pop.Controls.Add(inner);
        pop.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) pop.Close(); };
        pop.Deactivate += (_, _) => { try { pop.Close(); } catch { } };
        pop.FormClosed += (_, _) => pop.Dispose();
        var at = PointToScreen(new Point(0, Height));
        var wa = Screen.FromControl(this).WorkingArea;
        pop.Location = new Point(Math.Min(at.X, wa.Right - pop.Width), at.Y + pop.Height > wa.Bottom ? Math.Max(wa.Top, PointToScreen(Point.Empty).Y - pop.Height) : at.Y);
        Fill();
        pop.Show(FindForm());
        find.Focus();
    }
}
