using System.Collections.Specialized;

namespace NovaEnhance;

/// <summary>
/// 📄 ONE file menu for every file line in Nova (right-click, ⋯, Menu key / Shift+F10): open in Nova or with its app, open the file
/// location (selected), copy the file itself / its path / name / number / folder, a new email with it attached, ⭐ send it to the phone,
/// use it in another Nova feature (Find related, Review vs contract, Ask Nova, Search inside its folder, Guided search, Submittal log,
/// Why these labels, Which governs), set its contract type, and its properties (labels · stamps · links · read inside?).
/// 🔒 Private files keep only Open and Open location. Designed by A.HAMROUNI
/// </summary>
static class FileMenu
{
    /// <summary>Where the line points (a page / a passage) — optional.</summary>
    public class At { public string Loc = "", Goto = "", Text = "", Term = ""; }

    public static void Show(string path, Control owner, Point? screen = null, At at = null)
    {
        if (string.IsNullOrEmpty(path)) return;
        var m = Build(path, at);
        var p = screen ?? Cursor.Position;
        if (owner != null && !owner.IsDisposed) m.Show(owner, owner.PointToClient(p)); else m.Show(p);
    }

    /// <summary>Right-click, Menu key and Shift+F10 on a control whose current line is a file.</summary>
    public static void Attach(Control c, Func<Point?, string> pathAt, Func<Point?, At> atAt = null)
    {
        c.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var p = pathAt(e.Location);
            if (!string.IsNullOrEmpty(p)) Show(p, c, c.PointToScreen(e.Location), atAt?.Invoke(e.Location));
        };
        c.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Apps || e.KeyCode == Keys.F10 && e.Shift)
            {
                var p = pathAt(null);
                if (!string.IsNullOrEmpty(p)) { e.SuppressKeyPress = true; Show(p, c, c.PointToScreen(new Point(40, 20)), atAt?.Invoke(null)); }
            }
        };
    }

    /// <summary>The same menu on a DataGridView: the file of the row under the mouse (or the current row).</summary>
    public static void AttachGrid(DataGridView g, Func<DataGridViewRow, string> pathOf, Func<DataGridViewRow, At> atOf = null)
    {
        DataGridViewRow RowAt(Point? p)
        {
            if (p == null) return g.CurrentRow;
            var hit = g.HitTest(p.Value.X, p.Value.Y);
            if (hit.RowIndex < 0) return null;
            var r = g.Rows[hit.RowIndex];
            if (!r.Selected) { g.ClearSelection(); r.Selected = true; try { g.CurrentCell = r.Cells[Math.Max(0, hit.ColumnIndex)]; } catch { } }
            return r;
        }
        Attach(g, p => RowAt(p) is { } r ? pathOf(r) : null, atOf == null ? null : p => RowAt(p) is { } r ? atOf(r) : null);
    }

    /// <summary>A small ⋯ button that opens the menu (no right-click needed).</summary>
    public static Button Dots(Func<string> path, At at = null)
    {
        var b = Th.B("⋯", () => { });
        b.AutoSize = false; b.Width = 34; b.Height = 26; b.Font = Th.F(10, true);
        b.Click += (_, _) => Show(path(), b, b.PointToScreen(new Point(0, b.Height)), at);
        return b;
    }

    static ContextMenuStrip Build(string path, At at)
    {
        var m = new ContextMenuStrip { ShowImageMargin = false, Font = Th.F(9.5f) };
        try { m.BackColor = Color.FromArgb(0x1D, 0x22, 0x32); m.ForeColor = Th.Text; m.Renderer = new ToolStripProfessionalRenderer(new DarkColors()); } catch { }
        var name = Path.GetFileName(path.TrimEnd('\\'));
        var isDir = Directory.Exists(path) && !File.Exists(path);
        var isMail = path.EndsWith(".msg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".eml", StringComparison.OrdinalIgnoreCase);
        var priv = Projects.Private.IsMatch(path) || path.Contains("TrustWalletBackup", StringComparison.OrdinalIgnoreCase);
        var proj = ProjectOf(path);
        var number = Revisions.Number(name);
        void Add(ToolStripItemCollection to, string text, Action a, string keys = "", bool enabled = true)
        {
            var it = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys, Enabled = enabled, ForeColor = Th.Text };
            it.Click += (_, _) => { try { a(); } catch (Exception e) { MessageBox.Show(e.Message, "Nova Enhance", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
            to.Add(it);
        }
        void Head(string text) => m.Items.Add(new ToolStripLabel(text) { ForeColor = Th.Muted, Font = Th.F(8.5f, true) });
        void Sep() => m.Items.Add(new ToolStripSeparator());

        Head((isDir ? "📁 " : isMail ? "✉ " : "📄 ") + (name.Length > 60 ? name[..57] + "…" : name));
        if (isDir)
        {
            Add(m.Items, "📂 Open the folder", () => Shell.Open(path), "Enter");
            if (!priv)
            {
                Add(m.Items, "🔎 Search inside this folder", () => Screens.FolderSearch(path));
                if (proj != null) Add(m.Items, "🎯 Guided search from here", () => Screens.Guided(proj));
            }
            Sep();
            Add(m.Items, "⧉ Copy the folder path", () => Clipboard.SetText(path), "Ctrl+Shift+C");
            return m;
        }
        if (!isMail) Add(m.Items, "📖 Open in Nova" + (at?.Loc.Length > 0 ? $" ({at.Loc})" : ""), () => OpenInNova(path, at), "Enter");
        Add(m.Items, isMail ? "✉ Open in Outlook" : "↗ Open with its app", () => Shell.Open(path), isMail ? "Enter" : "Ctrl+Enter");
        Add(m.Items, "📂 Open file location (file selected)", () => Shell.Select(path), "Ctrl+L");
        if (priv) { Head("🔒 private — only open"); return m; }
        Sep();
        Head("COPY");
        Add(m.Items, "⧉ Copy the file (paste in a folder or an email)", () => Clipboard.SetFileDropList(new StringCollection { path }), "Ctrl+C");
        Add(m.Items, "⧉ Copy the full path", () => Clipboard.SetText(path), "Ctrl+Shift+C");
        Add(m.Items, "⧉ Copy the name", () => Clipboard.SetText(name));
        if (number.Length > 0) Add(m.Items, $"⧉ Copy the number ({number})", () => Clipboard.SetText(number));
        Add(m.Items, "⧉ Copy the folder path", () => Clipboard.SetText(Path.GetDirectoryName(path) ?? ""));
        Sep();
        Add(m.Items, "✉ New email with this file attached", () => NewMail(path));
        var starred = PhoneLink.Stars.Any(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        Add(m.Items, starred ? "📱 Remove from my phone (⭐ starred)" : "📱 Send to my phone (⭐ star it — synced with the phone)", () =>
        {
            PhoneLink.ToggleStarFile(path);
            Program.Balloon("📱 Phone", starred ? "Removed from the phone: " + name : "⭐ Starred — goes to your phone with the next sync: " + name);
        }, "", PhoneLink.On);
        Sep();
        var use = new ToolStripMenuItem("✦ Use it in another Nova feature") { ForeColor = Th.Text };
        Add(use.DropDownItems, "🔗 Find related (revisions + emails)", () => Screens.Timeline(number.Length > 0 ? number : Path.GetFileNameWithoutExtension(name)));
        if (proj != null) Add(use.DropDownItems, "📋 Review vs contract", () => Screens.ShowForm(new ContractReviewForm(proj.Code, path)));
        Add(use.DropDownItems, "💬 Ask Nova about it (Nova Chat)", () => Screens.HubAttach(path));
        Add(use.DropDownItems, "🔎 Search inside its folder", () => Screens.FolderSearch(Path.GetDirectoryName(path) ?? path));
        if (proj != null) Add(use.DropDownItems, "🎯 Guided search from here", () => Screens.Guided(proj));
        if (number.Length > 0) Add(use.DropDownItems, "📑 Show in the Submittal log", () => Screens.Submittal(new SubmittalLog.Options { Projects = proj != null ? new() { proj.Code } : new(), Words = new() { number }, From = DateTime.Today.AddYears(-5) }));
        Add(use.DropDownItems, "📐 Why these labels? (Learn this project)", () => Labels(path), "", true);
        if (proj != null) Add(use.DropDownItems, "⚖ Which document governs it?", () => { SettingsForm.CaShow(proj.Code, number.Length > 0 ? number : Path.GetFileNameWithoutExtension(name)); Screens.Settings("📜 Contract area"); });
        foreach (ToolStripItem i in use.DropDownItems) i.BackColor = m.BackColor;
        m.Items.Add(use);
        if (proj != null && Brain.Unlocked)
        {
            var pin = new ToolStripMenuItem("📌 Set its type (contract register)") { ForeColor = Th.Text };
            foreach (var t in ContractRegister.BuiltIn.Concat(ContractRegister.Types(proj.Code).Select(x => x.Name)))
            {
                var tt = t;
                Add(pin.DropDownItems, tt, () => { ContractRegister.Decide(proj.Code, new[] { path }, tt); Program.Balloon("📌 " + proj.Code, $"{name} → {tt} (kept in 🧠 Nova Brain)"); });
            }
            foreach (ToolStripItem i in pin.DropDownItems) i.BackColor = m.BackColor;
            m.Items.Add(pin);
        }
        Sep();
        Add(m.Items, "ⓘ Properties: labels · stamps · links · read inside?", () => Properties(path, proj));
        return m;
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(0x2E, 0x2A, 0x55);
        public override Color MenuItemBorder => Color.FromArgb(0x4A, 0x53, 0x70);
        public override Color MenuBorder => Color.FromArgb(0x4A, 0x53, 0x70);
        public override Color ToolStripDropDownBackground => Color.FromArgb(0x1D, 0x22, 0x32);
        public override Color ImageMarginGradientBegin => Color.FromArgb(0x1D, 0x22, 0x32);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(0x1D, 0x22, 0x32);
        public override Color ImageMarginGradientEnd => Color.FromArgb(0x1D, 0x22, 0x32);
        public override Color SeparatorDark => Color.FromArgb(0x2F, 0x36, 0x50);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0x2E, 0x2A, 0x55);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0x2E, 0x2A, 0x55);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(0x2E, 0x2A, 0x55);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(0x2E, 0x2A, 0x55);
    }

    public static Project ProjectOf(string path)
    {
        try { return Projects.All().FirstOrDefault(p => p.Folders.Any(f => path.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))); } catch { return null; }
    }

    public static void OpenInNova(string path, At at = null)
    {
        try { Opener.Open(new FileHit { Path = path }, new PieceHit { Loc = at?.Loc ?? "", Goto = at?.Goto ?? "", Text = at?.Text ?? "", Matches = new() }, at?.Term ?? ""); }
        catch { Shell.Open(path); }
    }

    static void NewMail(string path)
    {
        dynamic app = Outlook.Running();
        if (app == null) { MessageBox.Show("Open Outlook first — Nova creates the email there.", "Nova Enhance"); return; }
        dynamic mail = app.CreateItem(0);
        mail.Subject = Path.GetFileNameWithoutExtension(path);
        mail.Attachments.Add(path);
        mail.Display(false);
    }

    static void Labels(string path)
    {
        var l = Filing.Classify(path);
        var tags = string.Join(" · ", new[] { l.Number, l.Kind, l.Source, l.Disc, l.Stage, l.Rev, l.Code, l.State }.Where(x => x.Length > 0));
        if (MessageBox.Show($"📐 {Path.GetFileName(path)}\n\n{(tags.Length > 0 ? tags : "no labels yet")}\n\nWhy:\n{l.WhyText}\n\nOpen 📐 Learn this project to correct a code?", "📐 Why these labels", MessageBoxButtons.YesNo) == DialogResult.Yes)
            Screens.FilingLearn(ProjectOf(path)?.Code);
    }

    static void Properties(string path, Project proj)
    {
        var sb = new System.Text.StringBuilder();
        FileInfo fi = null; try { fi = new FileInfo(path); } catch { }
        sb.AppendLine(path);
        if (fi?.Exists == true) sb.AppendLine($"{fi.Length / 1024.0:N0} KB · modified {fi.LastWriteTime:dd MMM yyyy HH:mm}");
        var l = Filing.Classify(path);
        sb.AppendLine("\n📐 " + string.Join(" · ", new[] { l.Number, l.Kind, l.Source, l.Disc, l.Stage, l.Rev, l.Code }.Where(x => x.Length > 0)));
        var known = FolderIndex.Known(path);
        sb.AppendLine(known == null ? "📖 Not read inside yet" : $"📖 Read inside · {known.Count} page(s) / passage(s) in the Nova index");
        if (fi?.Exists == true && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && proj != null)
        {
            var st = NovaIndex.Stamps(path, fi.Length, fi.LastWriteTime.Ticks, false);
            var roles = ContractRegister.StampRoles(proj.Code);
            if (st == null) sb.AppendLine("🔏 Stamps: not read yet");
            else
            {
                var named = st.Where(roles.ContainsKey).Select(h => roles[h]).Where(r => r.Role is not ("ignore" or "other")).Select(r => r.Name.Length > 0 ? r.Name : r.Role).Distinct().ToList();
                sb.AppendLine(named.Count > 0 ? "🔏 Stamps: " + string.Join(" · ", named) : $"🔏 {st.Count} picture(s), no named stamp");
            }
        }
        if (proj != null)
        {
            var d = ContractRegister.Get(proj.Code, false)?.Docs.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (d != null) sb.AppendLine($"📜 Contract register: {d.Type} · {(d.Status == "superseded" ? "superseded by " + d.By : d.Status)} · evidence {d.Evidence} {d.EvidenceText}");
        }
        var links = NovaIndex.Links(path);
        if (links.Count > 0) sb.AppendLine("🔗 " + string.Join("\n🔗 ", links.Take(8).Select(x => $"{x.Kind}: {Path.GetFileName(x.Other)} {x.Note} · {x.At:dd MMM}")));
        MessageBox.Show(sb.ToString(), "ⓘ " + Path.GetFileName(path), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
