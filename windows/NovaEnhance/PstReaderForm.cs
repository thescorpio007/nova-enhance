using System.Text;
using MimeKit;
using X = XstReader;

namespace NovaEnhance;

/// <summary>
/// 📂 Archive reader: opens any Outlook archive (.pst) directly — folders, emails, attachments — without Outlook, read-only.
/// Search inside it with the same rules as everywhere in Nova, ➕ add it (or some folders) to the Nova index, export emails
/// (.eml), attachments and the list (Excel). Designed by A.HAMROUNI
/// </summary>
class PstReaderForm : NovaForm
{
    record Row(string Entry, DateTime Date, string From, string To, string Subject, string Folder, bool Att, X.Message M, X.Folder F);

    Pst.Opened o;
    readonly TreeView tree;
    readonly ListView list;
    readonly TextBox search, body;
    readonly Label info, status, head;
    readonly WorkDots dots;
    readonly FlowLayoutPanel files;
    readonly CheckBox deep;
    readonly SemaphoreSlim gate = new(1, 1);
    List<Row> rows = new();
    CancellationTokenSource cts;
    Pst.Mail current;
    Row currentRow;

    public PstReaderForm()
    {
        Text = "📂 Archive reader — Nova Enhance";
        Size = new Size(1500, 920);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Th.Bg;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 4), BackColor = Th.Bg, WrapContents = false };
        top.Controls.Add(Th.B("📂 Open an archive (.pst)…", OpenDialog, true));
        top.Controls.Add(Th.B("🕘 Recent ▾", RecentMenu));
        top.Controls.Add(Th.B("➕ Add to Nova index", AddToIndex));
        info = Th.L("Open a .pst file — Outlook is not needed, and the file is never changed.", 10, Th.Muted);
        info.Margin = new Padding(12, 8, 0, 0);
        top.Controls.Add(info);

        var srow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 5, Padding = new Padding(10, 2, 10, 2), BackColor = Th.Bg };
        srow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) srow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        search = Th.Box(false);
        search.Dock = DockStyle.Fill; search.Font = Th.F(11);
        search.PlaceholderText = "Search this archive — words, \"exact phrase\", from:Mark, subject:\"block wall\", received:2023, -word …  (Enter)";
        search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };
        srow.Controls.Add(search, 0, 0);
        srow.Controls.Add(SearchSyntax.HelpButton(search, true, Search), 1, 0);
        deep = new CheckBox { Text = "also inside the text (slower)", AutoSize = true, ForeColor = Th.Muted, Font = Th.F(9.5f), Margin = new Padding(10, 8, 0, 0) };
        srow.Controls.Add(deep, 2, 0);
        srow.Controls.Add(Th.B("🔍 Search", Search, true), 3, 0);
        srow.Controls.Add(Th.B("■ Stop", () => cts?.Cancel()), 4, 0);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30, BackColor = Th.Bg, Padding = new Padding(10, 4, 10, 0), WrapContents = false };
        dots = new WorkDots();
        status = Th.L("", 9.5f, Th.AccentText);
        bottom.Controls.Add(dots); bottom.Controls.Add(status);
        bottom.Controls.Add(Th.L("   " + Program.Sign + " · build " + Program.Build + " · reader: XstReader (Ms-PL)", 9, Th.Muted));

        var split1 = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 300, BackColor = Th.Bg, Panel1MinSize = 160 };
        tree = new TreeView { Dock = DockStyle.Fill, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(10), CheckBoxes = true, HideSelection = false };
        tree.AfterSelect += (_, e) => { if (e.Node?.Tag is X.Folder f) ShowFolder(f); };
        split1.Panel1.Controls.Add(tree);

        var split2 = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 640, BackColor = Th.Bg };
        list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, VirtualMode = true, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(9.5f) };
        list.Columns.Add("Date", 130); list.Columns.Add("From", 170); list.Columns.Add("Subject", 360); list.Columns.Add("📎", 30); list.Columns.Add("Folder", 160);
        list.RetrieveVirtualItem += (_, e) =>
        {
            var r = e.ItemIndex < rows.Count ? rows[e.ItemIndex] : null;
            e.Item = r == null ? new ListViewItem(new[] { "", "", "", "", "" })
                               : new ListViewItem(new[] { r.Date == DateTime.MinValue ? "" : r.Date.ToString("dd MMM yyyy HH:mm"), r.From, r.Subject, r.Att ? "📎" : "", r.Folder });
        };
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedIndices.Count == 1) ShowMail(rows[list.SelectedIndices[0]]); };
        list.ColumnClick += (_, e) =>
        {
            rows = e.Column switch
            {
                0 => rows.OrderByDescending(r => r.Date).ToList(), 1 => rows.OrderBy(r => r.From).ToList(), 2 => rows.OrderBy(r => r.Subject).ToList(), 3 => rows.OrderByDescending(r => r.Att).ToList(), _ => rows.OrderBy(r => r.Folder).ToList()
            };
            list.Invalidate();
        };
        list.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && list.SelectedIndices.Count > 0) ListMenu().Show(list, e.Location); };
        list.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.A) { for (int i = 0; i < rows.Count; i++) list.SelectedIndices.Add(i); e.Handled = true; } };
        split2.Panel1.Controls.Add(list);

        var pv = new Panel { Dock = DockStyle.Fill, BackColor = Th.Card, Padding = new Padding(10) };
        head = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 120, ForeColor = Th.Text, Font = Th.F(10), UseMnemonic = false };
        files = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(4000, 110), AutoScroll = true, BackColor = Th.Card };
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Th.Card };
        tools.Controls.Add(Th.B("🌐 Open as web page", () => { if (current == null) return; var f = Path.Combine(Paths.Cache, "pst-mail.html"); File.WriteAllText(f, current.Html.Length > 0 ? current.Html : "<pre>" + System.Net.WebUtility.HtmlEncode(current.Body) + "</pre>"); Shell.Open(f); }));
        tools.Controls.Add(Th.B("✉ Save as .eml", () => ExportEml(Selected())));
        tools.Controls.Add(Th.B("📎 Save attachments", () => SaveAttachments(Selected())));
        body = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Th.Bg, ForeColor = Th.Text, Font = Th.F(10.5f), BorderStyle = BorderStyle.None };
        pv.Controls.Add(body); pv.Controls.Add(tools); pv.Controls.Add(files); pv.Controls.Add(head);
        split2.Panel2.Controls.Add(pv);
        split1.Panel2.Controls.Add(split2);

        Controls.Add(split1);
        Controls.Add(bottom);
        Controls.Add(srow);
        Controls.Add(top);
        FormClosed += (_, _) => cts?.Cancel();
    }

    void Busy(string text, bool on) { if (IsDisposed) return; status.Text = text; dots.On = on; }
    void Ui(Action a) { if (IsDisposed) return; try { BeginInvoke(a); } catch { } }

    // ------------------------------------------------------------------ opening

    void OpenDialog()
    {
        using var d = new OpenFileDialog { Filter = "Outlook archive (*.pst)|*.pst|Outlook data file (*.pst;*.ost)|*.pst;*.ost", Title = "Open an Outlook archive" };
        if (d.ShowDialog(this) == DialogResult.OK) OpenFile(d.FileName);
    }

    void RecentMenu()
    {
        var m = new ContextMenuStrip { Font = Th.F(10) };
        foreach (var i in Pst.All.Values.OrderByDescending(x => x.Added))
        {
            var p = i.Path;
            m.Items.Add($"{(File.Exists(p) ? "📂" : "⚠")} {i.Name} · {i.Size / 1048576:N0} MB · {(i.Done ? "✓ in Nova" : i.Queued ? "being added" : i.Indexed > 0 ? "partly in Nova" : "not in Nova")}", null, (_, _) => OpenFile(p));
        }
        if (m.Items.Count == 0) m.Items.Add("(no archive opened yet)").Enabled = false;
        m.Show(this, PointToClient(Cursor.Position));
    }

    public async void OpenFile(string path, string selectEntry = null)
    {
        if (!File.Exists(path)) { MessageBox.Show(this, "File not found:\n" + path, Text); return; }
        if (Projects.Private.IsMatch(Path.GetFileName(path))) { MessageBox.Show(this, "🔒 This file is in your “never read” list.", Text); return; }
        Busy("Opening " + Path.GetFileName(path) + " (reading its folder list)…", true);
        await gate.WaitAsync();
        try
        {
            o = await Task.Run(() => Pst.Open(path));
            tree.Nodes.Clear();
            var rootNode = Node(o.Root);
            rootNode.Text = "📂 " + Path.GetFileName(path);
            tree.Nodes.Add(rootNode);
            rootNode.Expand();
            var total = Pst.Walk(o.Root).Sum(f => (long)f.ContentCount);
            var known = Pst.All.TryGetValue(o.Fp, out var inf) ? inf : null;
            info.Text = $"{Path.GetFileName(path)} · {new FileInfo(path).Length / 1048576:N0} MB · {total:N0} emails · " +
                        (known == null ? "not in Nova yet" : known.Done ? "✓ in the Nova index" : known.Queued ? "being added to Nova" : $"{known.Indexed:N0} in Nova");
            Busy(o.Relinked.Length > 0 ? $"🔗 Recognised: this is “{o.Relinked}” renamed or moved — everything Nova knew about it is kept." : "Ready — pick a folder, or search the whole archive.", false);
            Text = "📂 " + Path.GetFileName(path) + " — Archive reader";
        }
        catch (Exception e) { Busy("⚠ " + e.Message, false); Log.Error("archive reader · open", e); MessageBox.Show(this, "This file could not be read:\n" + e.Message + "\n\n(Password-protected or damaged archives cannot be read directly.)", Text); }
        finally { gate.Release(); }
        if (selectEntry != null) ShowEntry(selectEntry);
    }

    TreeNode Node(X.Folder f)
    {
        var n = new TreeNode((Pst.Private(f) ? "🔒 " : "") + $"{f.Name}{(f.ContentCount > 0 ? $"  ({f.ContentCount:N0})" : "")}") { Tag = f };
        foreach (var c in f.Folders) n.Nodes.Add(Node(c));
        return n;
    }

    async void ShowFolder(X.Folder f)
    {
        if (o == null) return;
        if (Pst.Private(f)) { rows = new(); list.VirtualListSize = 0; Busy("🔒 This folder is in your “never read” list.", false); return; }
        Busy($"Reading {f.Name}…", true);
        await gate.WaitAsync();
        try
        {
            var ms = f.Messages.Count > 0 ? f.Messages.ToList() : await Task.Run(() => o.File.ReadMessages(f));
            rows = ms.Select(m => new Row(Pst.EntryOf(o.Fp, m), m.Date ?? DateTime.MinValue, m.From ?? "", m.To ?? "", m.Subject ?? "", f.Name, m.HasAttachment, m, f))
                     .OrderByDescending(r => r.Date).ToList();
            list.VirtualListSize = rows.Count;
            list.Invalidate();
            Busy($"{rows.Count:N0} emails in {f.Path}", false);
        }
        catch (Exception e) { Busy("⚠ " + e.Message, false); }
        finally { gate.Release(); }
    }

    async void ShowMail(Row r)
    {
        currentRow = r;
        if (o == null) return;
        await gate.WaitAsync();
        try
        {
            var m = r.M; var f = r.F;
            if (m == null) { var found = await Task.Run(() => Pst.Find(r.Entry)); m = found.M; f = found.F; }
            if (m == null) { body.Text = "This email was not found in the archive."; return; }
            current = await Task.Run(() => Pst.Read(o, m, f));
            head.Text = $"{current.Subject}\nFrom: {current.FromName} {(current.FromAddr.Length > 0 ? "<" + current.FromAddr + ">" : "")}\nTo: {current.To}" +
                        (current.Cc.Length > 0 ? $"\nCc: {current.Cc}" : "") + $"\n{current.Date:dddd dd MMM yyyy HH:mm} · {current.Folder}";
            files.Controls.Clear();
            foreach (var name in current.Files)
            {
                var n = name; var mm = m;
                var b = Th.B("📎 " + n, async () =>
                {
                    var dir = Path.Combine(Paths.Cache, "pst-att", Guid.NewGuid().ToString("N")[..8]);
                    Directory.CreateDirectory(dir);
                    var p = await Task.Run(() => Pst.SaveAttachment(r.Entry, n, dir));
                    Shell.Open(p);
                });
                b.Font = Th.F(9);
                files.Controls.Add(b);
            }
            body.Text = current.Body.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        catch (Exception e) { body.Text = "⚠ " + e.Message; }
        finally { gate.Release(); }
    }

    /// <summary>Opens an email found by Nova's search ("pst:…").</summary>
    public async void ShowEntry(string entry)
    {
        var parts = entry.Split(':');
        if (parts.Length < 3 || !Pst.All.TryGetValue(parts[1], out var i)) { Busy("⚠ This archive is not known any more — open it with 📂 Open.", false); return; }
        if (o == null || o.Fp != parts[1]) { OpenFile(i.Path, entry); return; }
        Busy("Finding the email…", true);
        var (_, m, f) = await Task.Run(() => Pst.Find(entry));
        if (m == null) { Busy("⚠ Not found in this archive.", false); return; }
        rows = new() { new Row(entry, m.Date ?? DateTime.MinValue, m.From ?? "", m.To ?? "", m.Subject ?? "", f.Name, m.HasAttachment, m, f) };
        list.VirtualListSize = 1; list.Invalidate();
        list.SelectedIndices.Clear(); list.SelectedIndices.Add(0);
        Busy("", false);
    }

    // ------------------------------------------------------------------ searching inside the archive

    async void Search()
    {
        if (o == null) { OpenDialog(); return; }
        var q = search.Text.Trim();
        if (q.Length == 0) return;
        cts?.Cancel();
        var my = cts = new CancellationTokenSource();
        var sx = SearchSyntax.Parse(q);
        var words = (sx.Any ? sx.Free : q).Replace("\"", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToList();
        using var act = Activity.Begin("archive reader · search", q);
        var known = Pst.All.TryGetValue(o.Fp, out var inf) ? inf : null;
        var res = new List<Row>();
        try
        {
            // In the Nova index already: the fast full-text search (subject, text, attachments).
            if (known != null && known.Indexed > 0 && !deep.Checked)
            {
                Busy("Searching the indexed archive…", true);
                var mq = new MailQuery { Store = "pst:" + o.Fp, Words = words.Where(w => !sx.Phrases.Any(p => p.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList(), Phrase = sx.Phrases.FirstOrDefault() ?? "", Limit = 500, After = sx.After, Before = sx.Before };
                var hits = await Task.Run(() => MailIndex.Search(mq), my.Token);
                res = hits.Where(h => !sx.Any || sx.Pass(h)).Select(h => new Row(h.Entry, h.Date, h.FromName, h.To, h.Subject, h.Folder, (h.AttNames ?? "").Length > 0, null, null)).ToList();
            }
            // Otherwise straight from the file: subject / people / date (and the text when ticked).
            var folders = Pst.Walk(o.Root).Where(f => f.ContentCount > 0 && !Pst.Private(f)).ToList();
            if (res.Count == 0)
            {
                long total = folders.Sum(f => (long)f.ContentCount), done = 0;
                await gate.WaitAsync(my.Token);
                try
                {
                    foreach (var f in folders)
                    {
                        my.Token.ThrowIfCancellationRequested();
                        var ms = f.Messages.Count > 0 ? f.Messages.ToList() : await Task.Run(() => o.File.ReadMessages(f), my.Token);
                        foreach (var m in ms)
                        {
                            done++;
                            if (done % 500 == 0) { var p = (int)(100 * done / Math.Max(1, total)); Busy($"Searching {f.Path} · {p} % · {res.Count} found", true); await Task.Yield(); my.Token.ThrowIfCancellationRequested(); }
                            var h = new MailHit { Subject = m.Subject ?? "", FromName = m.From ?? "", To = m.To ?? "", Cc = m.Cc ?? "", Date = m.Date ?? DateTime.MinValue, Body = "", AttNames = m.HasAttachment ? "(attachment)" : "" };
                            var text = h.Subject + " " + h.FromName + " " + h.To;
                            bool ok = words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)) && (!sx.Any || PassLight(sx, h));
                            if (!ok && deep.Checked)
                            {
                                var r = await Task.Run(() => Pst.Read(o, m, f), my.Token);
                                h.Body = r.Body; h.AttNames = string.Join("; ", r.Files); h.FromAddr = r.FromAddr;
                                var full = h.Subject + " " + h.FromName + " " + h.To + " " + h.Body + " " + h.AttNames;
                                ok = words.All(w => full.Contains(w, StringComparison.OrdinalIgnoreCase)) && (!sx.Any || sx.Pass(h));
                                try { m.ClearContents(); } catch { }
                            }
                            if (ok) res.Add(new Row(Pst.EntryOf(o.Fp, m), h.Date, h.FromName, h.To, h.Subject, f.Name, m.HasAttachment, m, f));
                        }
                    }
                }
                finally { gate.Release(); }
            }
            rows = res.OrderByDescending(r => r.Date).ToList();
            list.VirtualListSize = rows.Count; list.Invalidate();
            Busy($"🔍 {rows.Count:N0} found" + (sx.Any ? " · " + sx.Describe() : "") + (known?.Indexed > 0 && !deep.Checked ? " · searched the Nova index of this archive" : ""), false);
            act.Result = $"{rows.Count} found";
        }
        catch (OperationCanceledException) { rows = res; list.VirtualListSize = rows.Count; list.Invalidate(); Busy($"■ Stopped · {res.Count} found so far", false); act.Fail("stopped"); }
        catch (Exception e) { Busy("⚠ " + e.Message, false); act.Fail(e.Message); }
    }

    /// <summary>The hard rules that can be checked from the list alone (no text yet).</summary>
    static bool PassLight(SearchSyntax sx, MailHit h)
    {
        var copy = SearchSyntax.Parse("");
        copy.Subject = sx.Subject; copy.Not = sx.Not; copy.From = sx.From; copy.To = sx.To; copy.Cc = sx.Cc; copy.After = sx.After; copy.Before = sx.Before; copy.HasAttach = sx.HasAttach;
        // Phrases: in the subject / people at this level.
        if (sx.Phrases.Any(p => !(h.Subject + " " + h.FromName + " " + h.To).Contains(p, StringComparison.OrdinalIgnoreCase))) return false;
        return copy.Pass(h);
    }

    // ------------------------------------------------------------------ ➕ add to Nova · export

    async void AddToIndex()
    {
        if (o == null) { OpenDialog(); return; }
        var ticked = new List<string>();
        void Collect(TreeNodeCollection ns) { foreach (TreeNode n in ns) { if (n.Checked && n.Tag is X.Folder f) ticked.Add(f.Path); Collect(n.Nodes); } }
        Collect(tree.Nodes);
        Busy("Checking whether this archive is already in Nova…", true);
        int already;
        await gate.WaitAsync();
        try { already = await Task.Run(() => Pst.AlreadyInNova(o)); } finally { gate.Release(); }
        Busy("", false);
        var msg = (ticked.Count > 0 ? $"Add the {ticked.Count} ticked folder(s) of this archive to the Nova index?" : "Add this whole archive to the Nova index?") +
                  "\n\nNova reads it directly (no Outlook), in the background, gently during working hours. The file is never changed." +
                  (already >= 80 ? $"\n\n⚠ About {already} % of its emails are ALREADY in Nova — it looks like a copy of an archive indexed before. They are skipped (not read again); only the missing ones are added."
                   : already > 0 ? $"\n\nAbout {already} % of its emails are already in Nova — they are skipped." : "");
        if (MessageBox.Show(this, msg, "➕ Add to Nova index", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var i = Pst.Add(o, ticked);
        info.Text = $"{i.Name} · being added to Nova — follow it in Settings → Email & AI";
        Busy($"➕ Adding {i.Total:N0} emails in the background", false);
    }

    List<Row> Selected() => list.SelectedIndices.Cast<int>().Where(i => i < rows.Count).Select(i => rows[i]).ToList();

    ContextMenuStrip ListMenu()
    {
        var m = new ContextMenuStrip { Font = Th.F(10) };
        var sel = Selected();
        m.Items.Add($"✉ Save {sel.Count} as .eml…", null, (_, _) => ExportEml(sel));
        m.Items.Add("📎 Save all their attachments…", null, (_, _) => SaveAttachments(sel));
        m.Items.Add("📊 List to Excel", null, (_, _) => ExportList(sel));
        m.Items.Add("📋 Copy the list", null, (_, _) => Clipboard.SetText(string.Join("\r\n", sel.Select(r => $"{r.Date:dd MMM yyyy HH:mm}\t{r.From}\t{r.Subject}\t{r.Folder}"))));
        return m;
    }

    async void ExportEml(List<Row> sel)
    {
        if (sel.Count == 0 || o == null) return;
        using var d = new FolderBrowserDialog { Description = "Save the emails as .eml in…" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var dir = d.SelectedPath; int n = 0;
        Busy("Saving…", true);
        await gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                foreach (var r in sel)
                {
                    var (_, m, f) = r.M != null ? (o, r.M, r.F) : Pst.Find(r.Entry);
                    if (m == null) continue;
                    var x = Pst.Read(o, m, f);
                    var mm = new MimeMessage();
                    mm.From.Add(new MailboxAddress(x.FromName, x.FromAddr.Length > 0 ? x.FromAddr : "unknown@archive"));
                    foreach (var a in x.ToA.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) mm.To.Add(MailboxAddress.Parse(a));
                    foreach (var a in x.CcA.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) mm.Cc.Add(MailboxAddress.Parse(a));
                    mm.Subject = x.Subject; mm.Date = x.Date;
                    var bb = new BodyBuilder { TextBody = x.Body };
                    if (x.Html.Length > 0) bb.HtmlBody = x.Html;
                    foreach (var a in m.Attachments.Where(a => a.IsFile && !string.IsNullOrEmpty(a.FileName)))
                        try { using var ms = new MemoryStream(); o.File.SaveAttachment(ms, a); bb.Attachments.Add(a.FileName, ms.ToArray()); } catch { }
                    mm.Body = bb.ToMessageBody();
                    var name = string.Concat((x.Date.ToString("yyyy-MM-dd HHmm") + " " + x.Subject).Split(Path.GetInvalidFileNameChars()));
                    if (name.Length > 120) name = name[..120];
                    mm.WriteTo(Path.Combine(dir, name + ".eml"));
                    try { m.ClearContents(); } catch { }
                    n++;
                }
            });
            Busy($"✓ {n} email(s) saved as .eml", false);
            Shell.Open(dir);
        }
        catch (Exception e) { Busy("⚠ " + e.Message, false); }
        finally { gate.Release(); }
    }

    async void SaveAttachments(List<Row> sel)
    {
        if (sel.Count == 0 || o == null) return;
        using var d = new FolderBrowserDialog { Description = "Save the attachments in…" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var dir = d.SelectedPath; int n = 0;
        Busy("Saving attachments…", true);
        try
        {
            await Task.Run(() =>
            {
                foreach (var r in sel)
                {
                    var (oo, m, _) = r.M != null ? (o, r.M, r.F) : Pst.Find(r.Entry);
                    if (m == null) continue;
                    oo.File.ReadMessageDetails(m);
                    foreach (var a in m.Attachments.Where(a => a.IsFile && !string.IsNullOrEmpty(a.FileName)))
                    {
                        var p = Path.Combine(dir, a.FileName);
                        for (int k = 2; File.Exists(p); k++) p = Path.Combine(dir, Path.GetFileNameWithoutExtension(a.FileName) + $" ({k})" + Path.GetExtension(a.FileName));
                        try { oo.File.SaveAttachment(p, m.Date, a); n++; } catch { }
                    }
                    try { m.ClearContents(); } catch { }
                }
            });
            Busy($"✓ {n} attachment(s) saved", false);
            Shell.Open(dir);
        }
        catch (Exception e) { Busy("⚠ " + e.Message, false); }
    }

    void ExportList(List<Row> sel)
    {
        var sb = new StringBuilder("| Date | From | To | Subject | 📎 | Folder |\n|---|---|---|---|---|---|\n");
        foreach (var r in sel) sb.Append($"| {r.Date:dd MMM yyyy HH:mm} | {Clean(r.From)} | {Clean(r.To)} | {Clean(r.Subject)} | {(r.Att ? "yes" : "")} | {Clean(r.Folder)} |\n");
        try { var p = FileTalk.ExportXlsx(sb.ToString(), "Archive emails"); Shell.Open(p); } catch (Exception e) { Busy("⚠ " + e.Message, false); }
        static string Clean(string s) => (s ?? "").Replace("|", "/").Replace("\n", " ");
    }
}
