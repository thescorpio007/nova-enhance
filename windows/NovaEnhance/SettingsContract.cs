namespace NovaEnhance;

/// <summary>Settings → 📶 Indexing progress and 📜 Contract area. Designed by A.HAMROUNI</summary>
partial class SettingsForm
{
    // ------------------------------------------------------------------ 📶 Indexing progress

    static string idxTab = "All";
    static Dictionary<string, Projects.ReadAll.FolderStat> idxStats = new(StringComparer.OrdinalIgnoreCase);
    static DateTime idxStatsAt = DateTime.MinValue;
    static volatile bool idxCounting;
    static readonly Dictionary<string, Queue<(DateTime At, int Done)>> idxSamples = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Queue<(DateTime At, int Names)>> idxNameSamples = new(StringComparer.OrdinalIgnoreCase);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> idxReach = new(StringComparer.OrdinalIgnoreCase);

    static void IdxRefreshStats(bool force = false)
    {
        if (idxCounting || (!force && DateTime.Now - idxStatsAt < TimeSpan.FromSeconds(10))) return;
        idxCounting = true;
        Task.Run(() =>
        {
            try { idxStats = Projects.ReadAll.Stats(); idxStatsAt = DateTime.Now; } catch (Exception e) { Log.Warn("indexing page", e.Message); }
            foreach (var f in IdxFolders().Select(x => x.Folder)) { try { idxReach[f] = Directory.Exists(f); } catch { idxReach[f] = false; } }
            idxCounting = false;
        });
    }

    static List<(string Code, string Name, string Folder)> IdxFolders() =>
        Projects.All().SelectMany(p => p.Folders.Select(f => (p.Code, p.Name, f))).Concat(Projects.General.Select(g => ("General", "reference", g)))
            .GroupBy(x => x.Item3, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    static double Rate(Dictionary<string, Queue<(DateTime, int)>> all, string key, int now)
    {
        if (!all.TryGetValue(key, out var q)) all[key] = q = new();
        q.Enqueue((DateTime.Now, now));
        while (q.Count > 40) q.Dequeue();
        var (t0, n0) = q.Peek();
        var secs = (DateTime.Now - t0).TotalSeconds;
        return secs > 20 && now > n0 ? (now - n0) / secs : 0;
    }

    static string Eta(int left, double perSec)
    {
        if (left <= 0) return "";
        if (perSec <= 0) return "";
        var s = left / perSec;
        return s < 90 ? "about 1 min left" : s < 3600 ? $"about {s / 60:0} min left" : s < 86400 * 2 ? $"about {s / 3600:0.#} h left" : $"about {s / 86400:0} days left";
    }

    void IndexingPage()
    {
        Note("Each folder of your projects, on this PC and on the network: ① its file NAMES (listed in minutes, kept up to date) and ② the files READ INSIDE (slower — PDFs, Word, Excel, scans by OCR). Network folders are read on the rules below so the office network stays fast. 📜 Contract-area folders and ⚡ First are read before the others.");
        IdxRefreshStats(true);

        // the rules
        page.Add(Th.Section("Rules of the reading"));
        var r1 = page.Add(Th.Row(false));
        r1.Controls.Add(Th.L("🌐 Network folders are read:", 10, Th.Text));
        var net = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, BackColor = Th.Card, ForeColor = Th.Text, FlatStyle = FlatStyle.Flat };
        var nets = new[] { ("afterhours", "after working hours / when the PC is idle (recommended)"), ("always", "always (also while you work)"), ("never", "never — names only") };
        foreach (var (_, t) in nets) net.Items.Add(t);
        net.SelectedIndex = Math.Max(0, Array.FindIndex(nets, x => x.Item1 == Projects.ReadAll.NetRead));
        net.SelectedIndexChanged += (_, _) => Projects.ReadAll.NetRead = nets[net.SelectedIndex].Item1;
        r1.Controls.Add(net);
        page.Add(Th.Check("📄 Read only the LATEST revision of each document (older revisions: names only — much faster)", Projects.ReadAll.LatestOnly, v => Projects.ReadAll.LatestOnly = v));
        page.Add(Th.Check("🧬 Skip network files already read as a copy on this PC (same name and size)", Projects.ReadAll.NetSkipCopies, v => Projects.ReadAll.NetSkipCopies = v));
        page.Add(Th.Check("📖 Read inside the files in the background", Projects.ReadAll.On, v => { Projects.ReadAll.On = v; Draw("📶 Indexing progress"); }));
        var r2 = page.Add(Th.Row(false));
        r2.Controls.Add(Th.B("⚡ Read now at full speed", () => Projects.ReadAll.Now(), true));
        r2.Controls.Add(Th.B(Projects.ReadAll.Paused ? "▶ Continue all" : "⏸ Pause all", () => { Projects.ReadAll.Paused = !Projects.ReadAll.Paused; Draw("📶 Indexing progress"); }));
        r2.Controls.Add(Th.B("⟳ List all again", () => Projects.Indexer.Refresh()));
        var now = page.Add(Th.L("", 9.5f, Th.Muted));

        // tabs
        var tabs = page.Add(Th.Row(false));
        foreach (var t in new[] { "All", "This PC", "Network", "Problems" })
            tabs.Controls.Add(Th.B((idxTab == t ? "● " : "") + t, () => { idxTab = t; Draw("📶 Indexing progress"); }, idxTab == t));

        var list = IdxFolders();
        var cards = new List<Action>();
        var holder = page.Add(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Th.Bg }, true);
        foreach (var (code, name, folder) in list)
        {
            var isNet = Projects.ReadAll.IsNetwork(folder);
            if (idxTab == "This PC" && isNet) continue;
            if (idxTab == "Network" && !isNet) continue;
            var key = Projects.ReadAll.KeyOf(folder);
            if (idxTab == "Problems")
            {
                Projects.Status.TryGetValue(folder, out var s0);
                var bad = (s0?.Phase ?? "").StartsWith("⚠") || (idxReach.TryGetValue(folder, out var ok0) && !ok0) || (idxStats.TryGetValue(key, out var st0) && st0.Skipped > 0);
                if (!bad) continue;
            }
            var card = new Panel { Width = 720, Height = 168, BackColor = Color.FromArgb(0x18, 0x1C, 0x24), Margin = new Padding(0, 6, 0, 6), Padding = new Padding(12, 8, 12, 8) };
            var title = new Label { AutoSize = true, Font = Th.F(11, true), ForeColor = Th.Text, Location = new Point(12, 8), Text = $"{code} · {name}" + (isNet ? "   🌐 network" : "   💻 this PC") };
            var path = new Label { AutoSize = true, Font = Th.F(8.5f), ForeColor = Th.Muted, Location = new Point(12, 30), Text = folder };
            var pill = new Label { AutoSize = true, Font = Th.F(9, true), ForeColor = Th.Good, Location = new Point(520, 10) };
            var l1 = new Label { AutoSize = true, Font = Th.F(9.5f), ForeColor = Th.Text, Location = new Point(12, 52) };
            var (b1, f1) = Bar(Color.FromArgb(0x3F, 0xAF, 0x84)); b1.Location = new Point(12, 72);
            var l2 = new Label { AutoSize = true, Font = Th.F(9.5f), ForeColor = Th.Text, Location = new Point(12, 86) };
            var (b2, f2) = Bar(Th.Accent); b2.Location = new Point(12, 106);
            var cur = new Label { AutoSize = true, Font = Th.F(8.5f), ForeColor = Th.Muted, Location = new Point(12, 118) };
            var btns = new FlowLayoutPanel { AutoSize = true, Location = new Point(8, 134), WrapContents = false, BackColor = card.BackColor };
            var paused = Projects.ReadAll.PausedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase);
            var ff = folder;
            btns.Controls.Add(Th.B(paused ? "▶ Continue" : "⏸ Pause", () =>
            {
                var pl = Projects.ReadAll.PausedFolders;
                if (pl.Contains(ff, StringComparer.OrdinalIgnoreCase)) pl.RemoveAll(x => x.Equals(ff, StringComparison.OrdinalIgnoreCase)); else pl.Add(ff);
                Projects.ReadAll.PausedFolders = pl; Draw("📶 Indexing progress");
            }));
            var isFirst = Projects.ReadAll.FirstFolder.Equals(folder, StringComparison.OrdinalIgnoreCase);
            btns.Controls.Add(Th.B(isFirst ? "⚡ First ✓" : "⚡ First", () => { Projects.ReadAll.FirstFolder = isFirst ? "" : ff; Draw("📶 Indexing progress"); }, isFirst));
            btns.Controls.Add(Th.B("↻ List again", () => Projects.Indexer.Relist(ff)));
            btns.Controls.Add(Th.B("📂 Open", () => Shell.Open(ff)));
            card.Controls.AddRange(new Control[] { title, path, pill, l1, b1, l2, b2, cur, btns });
            holder.Controls.Add(card);
            cards.Add(() =>
            {
                if (card.IsDisposed) return;
                var w = Math.Max(300, holder.Parent?.ClientSize.Width - 70 ?? 680);
                card.Width = w + 24; b1.Width = b2.Width = w; pill.Left = Math.Max(300, w - 180);
                Projects.Status.TryGetValue(ff, out var s);
                idxStats.TryGetValue(key, out var st);
                var reach = !idxReach.TryGetValue(ff, out var rr) || rr;
                var names = st?.Names ?? s?.Files ?? 0;
                // ① names
                var listing = s != null && s.Phase == "listing";
                var nRate = Rate(idxNameSamples, key, s?.Files ?? names);
                if (!reach || (s?.Phase ?? "").StartsWith("⚠")) { l1.Text = "① Names   ⚠ " + (reach ? s?.Phase.TrimStart('⚠', ' ') : "not reachable now (network / VPN?) — Nova tries again by itself"); f1.Width = 0; pill.Text = "⚠ not reachable"; pill.ForeColor = Th.Warn; }
                else if (listing)
                {
                    var exp = Math.Max(s.Expected, s.Files);
                    var pc = exp > 0 ? Math.Min(99, 100 * s.Files / exp) : 50;
                    l1.Text = $"① Names   listing… {s.Files:N0}{(s.Expected > 0 ? " of ~" + s.Expected.ToString("N0") : "")} files · {pc}%{(nRate > 0 ? $" · {nRate:0}/s" : "")} {Eta(exp - s.Files, nRate)}";
                    f1.Width = w * pc / 100; pill.Text = $"listing {pc}%"; pill.ForeColor = Th.Accent;
                }
                else { l1.Text = $"① Names   ✓ {names:N0} files listed · kept up to date{(s != null && s.Last > DateTime.MinValue ? " · listed " + s.Last.ToString("dd MMM HH:mm") : "")}"; f1.Width = names > 0 ? w : 0; pill.Text = "✓ names up to date"; pill.ForeColor = Th.Good; }
                // ② read inside
                if (st == null || st.Readable == 0) { l2.Text = "② Read inside   " + (idxCounting && st == null ? "counting…" : "no readable files yet"); f2.Width = 0; }
                else
                {
                    var rate = Rate(idxSamples, key, st.Done);
                    var pc = (int)(100L * st.Done / st.Readable);
                    var state = paused ? "⏸ paused" : !Projects.ReadAll.On ? "off — read as you ask" : isNet && Projects.ReadAll.NetRead == "never" ? "network: names only" : st.Done >= st.Readable ? "✓ done" : rate > 0 ? $"{rate * 60:0} files/min · {Eta(st.Readable - st.Done, rate)}" : isNet && Projects.ReadAll.NetRead == "afterhours" ? "waits for after hours / idle" : "waiting its turn";
                    l2.Text = $"② Read inside   {st.Done:N0} of {st.Readable:N0} readable · {pc}% · {state}{(st.Skipped > 0 ? $" · {st.Skipped:N0} left out (older revisions / copies)" : "")}";
                    f2.Width = (int)(w * (double)st.Done / st.Readable);
                }
                var cp = Projects.ReadAll.CurrentPath;
                cur.Text = cp.Length > 0 && cp.StartsWith(ff.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? "📖 now: " + cp[(ff.TrimEnd('\\').Length + 1)..] : listing ? "📛 listing the names…" : "";
            });
        }
        if (cards.Count == 0) holder.Controls.Add(Th.Wrap(idxTab == "Problems" ? "✓ No problems." : "No folders yet — add them in 📁 Projects.", 10, Th.Muted));
        void Tick()
        {
            IdxRefreshStats();
            foreach (var c in cards) c();
            now.Text = "📖 " + Projects.ReadAll.Phase + (Projects.ReadAll.Current.Length > 0 ? " · " + Projects.ReadAll.Current : "") + (idxStatsAt > DateTime.MinValue ? " · counted " + idxStatsAt.ToString("HH:mm:ss") : "");
        }
        Tick();
        var tm = new System.Windows.Forms.Timer { Interval = 3000 };
        tm.Tick += (_, _) => { if (holder.IsDisposed) { tm.Stop(); tm.Dispose(); return; } Tick(); };
        tm.Start();
    }

    // ------------------------------------------------------------------ 📜 Contract area

    static string caProject = "";

    void ContractPage()
    {
        Note("The documents that make the contract of each project, and which one governs when two say different things (the order of precedence). Nova reads these folders FIRST and uses them for every review — a MAR, a method statement, an RFI — and for “Which document governs?”. ✦ Nova proposes the role of each folder from its name and from the FIRST PAGES of its documents (title, signed / draft, IFC / IFT, Addendum No.…); nothing is kept until you confirm. Kept in 🧠 Nova Brain → 📜 Contract area.");
        if (!Brain.Unlocked)
        {
            Note("🔒 Open 🧠 Nova Brain first (its password) — the contract area is kept inside it.", Th.Warn);
            page.Add(Th.B("🧠 Open Nova Brain", () => Go("🧠 Nova Brain"), true));
            return;
        }
        var all = Projects.All();
        if (all.Count == 0) { Note("Add your projects first in 📁 Projects."); return; }
        if (all.All(p => p.Code != caProject)) caProject = all.Any(p => p.Code == "SLV") ? "SLV" : all[0].Code;
        var proj = all.First(p => p.Code == caProject);
        var pr = page.Add(Th.Row(false));
        pr.Controls.Add(Th.L("Project:", 10, Th.Text));
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, BackColor = Th.Card, ForeColor = Th.Text, FlatStyle = FlatStyle.Flat };
        foreach (var p in all) cb.Items.Add($"{p.Code} · {p.Name}");
        cb.SelectedIndex = all.IndexOf(proj);
        cb.SelectedIndexChanged += (_, _) => { caProject = all[cb.SelectedIndex].Code; Draw("📜 Contract area"); };
        pr.Controls.Add(cb);

        var entries = ContractArea.Of(proj.Code);
        var order = ContractArea.Order(proj.Code);
        var status = Th.L("", 9.5f, Th.Muted);

        // the folders
        page.Add(Th.Section($"📁 Folders of the {proj.Code} contract area · click a cell to change it"));
        var g = Grid(300);
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Folder", ReadOnly = true, Width = 230 });
        var roleCol = new DataGridViewComboBoxColumn { HeaderText = "Role ✎", Width = 200, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton };
        roleCol.Items.AddRange(ContractArea.Roles);
        g.Columns.Add(roleCol);
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Rank", ReadOnly = true, Width = 50 });
        g.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Latest only ✎", Width = 80 });
        g.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Subfolders ✎", Width = 80 });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "For Nova: what these documents are ✎", Width = 340 });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Why (what Nova saw)", ReadOnly = true, Width = 300 });
        void Fill()
        {
            g.Rows.Clear();
            foreach (var e in entries.OrderBy(e => ContractArea.GroupOf(e.Role)).ThenBy(e => e.Rank == 0 ? 99 : e.Rank))
            {
                e.Rank = ContractArea.DefaultRank(e.Role, order);
                var i = g.Rows.Add(e.Path, ContractArea.Roles.Contains(e.Role) ? e.Role : "Reference only", e.Rank > 0 ? e.Rank.ToString() : "—", e.Latest, e.Subs, e.Desc, e.Evidence);
                g.Rows[i].Tag = e;
                if (!ContractArea.Governs(e.Role)) g.Rows[i].DefaultCellStyle.ForeColor = Th.Muted;
            }
        }
        Fill();
        page.Add(g, true);
        void Pull()
        {
            g.EndEdit();
            foreach (DataGridViewRow r in g.Rows)
            {
                if (r.Tag is not ContractEntry e) continue;
                e.Role = r.Cells[1].Value?.ToString() ?? e.Role; e.Latest = r.Cells[3].Value is true; e.Subs = r.Cells[4].Value is true; e.Desc = r.Cells[5].Value?.ToString() ?? "";
                e.Rank = ContractArea.DefaultRank(e.Role, order);
            }
        }
        var br = page.Add(Th.Row());
        CancellationTokenSource cts = null;
        void Review(List<ContractEntry> found)
        {
            if (found.Count == 0) { status.Text = "Nothing new to propose."; return; }
            using var f = new ContractProposeForm(proj.Code, found);
            if (f.ShowDialog(this) != DialogResult.OK) return;
            Pull();
            foreach (var e in f.Result)
            {
                entries.RemoveAll(x => x.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase));
                entries.Add(e);
            }
            if (f.NotThese.Count > 0) ContractArea.NotAgain(proj.Code, f.NotThese);
            Fill();
            status.Text = $"✓ {f.Result.Count} folder(s) added — press 💾 Save to keep them in 🧠 Nova Brain.";
        }
        void Busy(Func<CancellationToken, List<ContractEntry>> job)
        {
            cts?.Cancel(); var my = cts = new CancellationTokenSource();
            status.Text = "📖 Reading the first pages…";
            Task.Run(() => job(my.Token), my.Token).ContinueWith(t => Program.UiContext.Post(_ =>
            {
                if (status.IsDisposed) return;
                if (t.IsCanceled || my.IsCancellationRequested) { status.Text = "Stopped."; return; }
                if (t.IsFaulted) { status.Text = "⚠ " + t.Exception?.GetBaseException().Message; return; }
                status.Text = "";
                Review(t.Result);
            }, null));
        }
        void Say(string s) => Program.UiContext.Post(_ => { if (!status.IsDisposed) status.Text = s; }, null);
        br.Controls.Add(Th.B("✦ Nova proposes", () => Busy(ct => ContractArea.Candidates(proj, Say, ct)), true));
        br.Controls.Add(Th.B("➕ Add a folder…", () =>
        {
            using var d = new FolderBrowserDialog { Description = "A folder of the " + proj.Code + " contract (its subfolders get a role each)", SelectedPath = proj.Folders.FirstOrDefault(Directory.Exists) ?? "" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            if (Projects.Private.IsMatch(d.SelectedPath)) { MessageBox.Show(this, "🔒 That folder looks private — Nova never reads it.", "Nova Enhance"); return; }
            var path = d.SelectedPath;
            var subs = MessageBox.Show(this, "Give each SUBFOLDER its own role?\n\nYes = one role per subfolder (e.g. Vol 1 Agreement · Vol 2 Conditions · Vol 3 Specs)\nNo = one role for the whole folder", "📜 Contract area", MessageBoxButtons.YesNoCancel);
            if (subs == DialogResult.Cancel) return;
            Busy(ct => subs == DialogResult.Yes ? ContractArea.Propose(proj.Code, path, Say, ct) : new List<ContractEntry> { ContractArea.ProposeOne(proj.Code, path, ct) });
        }));
        br.Controls.Add(Th.B("🗑 Remove", () =>
        {
            if (g.CurrentRow?.Tag is not ContractEntry e) return;
            Pull(); entries.Remove(e); Fill();
            status.Text = "Removed — press 💾 Save to keep the change.";
        }));
        br.Controls.Add(Th.B("■ Stop", () => cts?.Cancel()));
        br.Controls.Add(Th.B("💾 Save", () =>
        {
            Pull();
            try { ContractArea.Save(proj.Code, entries, order); status.Text = $"✓ Saved in 🧠 Nova Brain · {entries.Count} folder(s) · Nova reads the governing ones first."; Projects.ReadAll.Now(); }
            catch (Exception e) { status.Text = "⚠ " + e.Message; }
        }, true));
        page.Add(status);

        // the order of precedence
        page.Add(Th.Section("⚖ Order of precedence · when two documents disagree, the higher one governs"));
        var ob = page.Add(Th.Row(false));
        var lb = new ListBox { Width = 380, Height = 200, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, Font = Th.F(10) };
        void Lines() { lb.Items.Clear(); for (int i = 0; i < order.Count; i++) lb.Items.Add($"{i + 1}   {order[i]}"); }
        Lines();
        ob.Controls.Add(lb);
        var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        void MoveRow(int d)
        {
            var i = lb.SelectedIndex; var j = i + d;
            if (i < 0 || j < 0 || j >= order.Count) return;
            (order[i], order[j]) = (order[j], order[i]); Lines(); lb.SelectedIndex = j;
            Pull(); Fill();
        }
        side.Controls.Add(Th.B("↑ Up", () => MoveRow(-1)));
        side.Controls.Add(Th.B("↓ Down", () => MoveRow(1)));
        side.Controls.Add(Th.B("📖 Read it from the contract", () =>
        {
            status.Text = "📖 Looking for the clause that ranks the documents…";
            var my = cts = new CancellationTokenSource();
            ContractArea.OrderFromContract(proj.Code, Say, my.Token).ContinueWith(t => Program.UiContext.Post(_ =>
            {
                if (status.IsDisposed) return;
                if (t.IsFaulted || t.IsCanceled) { status.Text = "⚠ " + (t.Exception?.GetBaseException().Message ?? "stopped"); return; }
                var (o, clause) = t.Result;
                if (o == null) { status.Text = clause; return; }
                if (MessageBox.Show(this, "Nova found the order in the contract (" + clause + "):\n\n" + string.Join("\n", o.Select((x, i) => $"{i + 1}  {x}")) + "\n\nUse this order?", "⚖ Order of precedence", MessageBoxButtons.YesNo) != DialogResult.Yes) { status.Text = ""; return; }
                order.Clear(); order.AddRange(o); Lines(); Pull(); Fill();
                try { ContractArea.Save(proj.Code, entries, order, clause); status.Text = "✓ Order saved · " + clause; } catch (Exception e) { status.Text = "⚠ " + e.Message; }
            }, null));
        }, true));
        side.Controls.Add(Th.B("↺ The usual order", () => { order.Clear(); order.AddRange(ContractArea.DefaultOrder); Lines(); Pull(); Fill(); }));
        ob.Controls.Add(side);
        var clauseNow = ContractArea.OrderClause(proj.Code);
        Note((clauseNow.Length > 0 ? "📌 " + clauseNow + " · " : "") + "After signing, a change (EI, VO, PMI, VE, an RFI answer) wins on its own item — the newest first. Drafts, tender (IFT) sets and reference folders never govern. Press 💾 Save after changing the order.");

        // 🧪 which document governs?
        page.Add(Th.Section("🧪 Which document governs? · try it (files already read — instant, no AI)"));
        var tr = page.Add(Th.Row(false));
        var q = Th.Box(false); q.Width = 420; q.Text = "waterproofing membrane thickness";
        tr.Controls.Add(q);
        var outBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        tr.Controls.Add(Th.B("⚖ Which governs?", () =>
        {
            outBox.Controls.Clear();
            var hits = ContractArea.GovernsTest(proj.Code, q.Text);
            if (hits.Count == 0) { outBox.Controls.Add(Th.Wrap("Nothing found in the contract-area files read so far. (Save the contract area, then let 📖 reading finish — 📶 Indexing progress shows how far it is.)", 9.5f, Th.Muted)); page.FitAll(); return; }
            var top = true;
            foreach (var h in hits)
            {
                var lab = Th.Wrap($"{(top ? "⚖ GOVERNS → " : "      ")}{h.Role}{(h.Rank > 0 ? " (rank " + h.Rank + ")" : "")} · {Path.GetFileName(h.Path)} · {h.Loc}\n      “{h.Text.Replace("\n", " ").Trim()}…”", 9.5f, top ? Th.Good : Th.Text);
                lab.Cursor = Cursors.Hand; var hp = h.Path; lab.Click += (_, _) => Shell.Open(hp);
                outBox.Controls.Add(lab);
                top = false;
            }
            page.FitAll();
        }, true));
        page.Add(outBox, true);
    }

    static DataGridView Grid(int height)
    {
        var g = new DataGridView
        {
            Height = height, BackgroundColor = Th.Card, ForeColor = Th.Text, GridColor = Color.FromArgb(0x2E, 0x35, 0x46), BorderStyle = BorderStyle.FixedSingle,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            EnableHeadersVisualStyles = false, Font = Th.F(9), ScrollBars = ScrollBars.Both, EditMode = DataGridViewEditMode.EditOnEnter
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(0x1D, 0x21, 0x30);
        g.ColumnHeadersDefaultCellStyle.ForeColor = Th.Text;
        g.DefaultCellStyle.BackColor = Th.Card; g.DefaultCellStyle.ForeColor = Th.Text;
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0x2E, 0x2A, 0x55);
        g.DataError += (_, e) => e.ThrowException = false;
        return g;
    }
}

/// <summary>✦ Nova's proposal for contract-area folders: tick, correct the role / description, then add.</summary>
class ContractProposeForm : NovaForm
{
    public List<ContractEntry> Result = new();
    public List<string> NotThese = new();

    public ContractProposeForm(string project, List<ContractEntry> found)
    {
        Text = $"✦ Nova proposes · {project} contract area";
        Width = 1180; Height = 620;
        var top = new Label { Dock = DockStyle.Top, Height = 54, Padding = new Padding(12, 8, 12, 0), ForeColor = Th.Text, Font = Th.F(10),
            Text = $"Nova read the folder names and the first pages of the largest documents. ✓ sure = clear title / name · ? please confirm = unclear (✦ the AI can look at the first pages of these). Untick what is not part of the contract." };
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = Th.Card, ForeColor = Th.Text, GridColor = Color.FromArgb(0x2E, 0x35, 0x46), BorderStyle = BorderStyle.None,
            AllowUserToAddRows = false, RowHeadersVisible = false, EnableHeadersVisualStyles = false, Font = Th.F(9), EditMode = DataGridViewEditMode.EditOnEnter
        };
        g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(0x1D, 0x21, 0x30); g.ColumnHeadersDefaultCellStyle.ForeColor = Th.Text;
        g.DefaultCellStyle.BackColor = Th.Card; g.DefaultCellStyle.ForeColor = Th.Text; g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0x2E, 0x2A, 0x55);
        g.DataError += (_, e) => e.ThrowException = false;
        g.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "✓", Width = 34 });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Folder", ReadOnly = true, Width = 220 });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Files", ReadOnly = true, Width = 60 });
        var rc = new DataGridViewComboBoxColumn { HeaderText = "Role ✎", Width = 200, FlatStyle = FlatStyle.Flat }; rc.Items.AddRange(ContractArea.Roles); g.Columns.Add(rc);
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "", ReadOnly = true, Width = 110 });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Why (what Nova saw)", ReadOnly = true, Width = 300 });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "For Nova: what these documents are ✎", Width = 320 });
        void Fill()
        {
            g.Rows.Clear();
            foreach (var e in found)
            {
                var i = g.Rows.Add(e.Picked, System.IO.Path.GetFileName(e.Path.TrimEnd('\\')), e.Files.ToString("N0"), e.Role, e.Sure ? "✓ sure" : "? please confirm", e.Why, e.Desc);
                g.Rows[i].Tag = e; g.Rows[i].Cells[1].ToolTipText = e.Path;
                g.Rows[i].Cells[4].Style.ForeColor = e.Sure ? Th.Good : Th.Warn;
            }
        }
        Fill();
        void Pull()
        {
            g.EndEdit();
            foreach (DataGridViewRow r in g.Rows)
                if (r.Tag is ContractEntry e)
                {
                    var role = r.Cells[3].Value?.ToString() ?? e.Role;
                    if (role != e.Role) { e.Role = role; e.Sure = true; }
                    e.Picked = r.Cells[0].Value is true; e.Desc = r.Cells[6].Value?.ToString() ?? "";
                }
        }
        var status = new Label { AutoSize = true, ForeColor = Th.Muted, Font = Th.F(9.5f), Margin = new Padding(8, 10, 8, 0) };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8), BackColor = Th.Bg };
        var unclear = found.Where(e => !e.Sure).ToList();
        var canAi = Ai.Active() != null && !Ai.QuotaOut && FolderAsk.Sees != "never";
        CancellationTokenSource cts = null;
        var ai = Th.B($"✦ Ask the AI for the {unclear.Count} unclear one(s)", () =>
        {
            Pull();
            var my = cts = new CancellationTokenSource();
            var list = found.Where(e => !e.Sure).ToList();
            if (list.Count == 0) return;
            if (FolderAsk.Sees == "ask" && MessageBox.Show(this, $"Send the folder name and the first ~1,500 characters of 1–2 documents of {list.Count} folder(s) to the AI? (Never whole documents; 🔒 private folders never.)", "✦ AI", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            ContractArea.AskAi(project, list, s => Program.UiContext.Post(_ => { if (!status.IsDisposed) status.Text = s; }, null), my.Token)
                .ContinueWith(t => Program.UiContext.Post(_ => { if (IsDisposed) return; status.Text = t.IsFaulted ? "⚠ " + t.Exception?.GetBaseException().Message : "✓ The AI answered — check the roles."; Fill(); }, null));
        });
        ai.Enabled = canAi && unclear.Count > 0;
        bottom.Controls.Add(Th.B("✓ Add the ticked", () =>
        {
            Pull();
            Result = found.Where(e => e.Picked).ToList();
            foreach (var e in Result) e.Rank = ContractArea.DefaultRank(e.Role, ContractArea.Order(project));
            NotThese = found.Where(e => !e.Picked).Select(e => e.Path).ToList();
            DialogResult = DialogResult.OK;
        }, true));
        bottom.Controls.Add(ai);
        bottom.Controls.Add(Th.B("Tick all", () => { foreach (DataGridViewRow r in g.Rows) r.Cells[0].Value = true; }));
        bottom.Controls.Add(Th.B("Cancel", () => { cts?.Cancel(); DialogResult = DialogResult.Cancel; }));
        bottom.Controls.Add(status);
        if (!canAi && unclear.Count > 0) status.Text = Ai.QuotaOut ? "💳 AI quota used up — confirm the unclear ones yourself." : "✦ AI off for folders (Settings → Email & AI) — confirm the unclear ones yourself.";
        Controls.Add(g); Controls.Add(bottom); Controls.Add(top);
        FormClosing += (_, _) => cts?.Cancel();
    }
}
