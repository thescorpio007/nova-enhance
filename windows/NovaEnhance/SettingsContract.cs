using System.Text.RegularExpressions;
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
        page.Add(Th.Check("🧬 Skip network files that are the same file on this PC (same name and size — read once, there)", Projects.ReadAll.NetSkipCopies, v => Projects.ReadAll.NetSkipCopies = v));
        Note("⚡ How the background reads: first a quick pass over every file (the PDF's text + its first 3 scanned pages — seconds per file); the whole file is read when you open it or ask about it, and the 📜 contract area is always read whole. Order: latest submittal revisions → files changed this year → contract area → the rest. Folders named Old · Superseded · Archive · Backup keep their names only. Two files at a time when the PC is idle after working hours. When the server is not connected, its files wait (never saved as empty).");
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
            btns.Controls.Add(FileMenu.Dots(() => ff));
            foreach (var cc in new Control[] { card, title, path }) FileMenu.Attach(cc, _ => ff);
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

    // ------------------------------------------------------------------ 📜 Contract area (by document)

    static string caProject = "", caTab = "All governing", caFilter = "";
    /// <summary>Opens the register of a project filtered on a number or name (from the 📄 file menu: ⚖ Which document governs it?).</summary>
    public static void CaShow(string project, string filter) { caProject = project; caTab = "All governing"; caFilter = filter ?? ""; }

    void ContractPage()
    {
        Note("You give the contract AREAS (folders). Nova reads and labels every DOCUMENT inside by itself — from its number and name, from what page 1 says (title, “SECTION 08 71 00”, “Bill No. 5”, “ADDENDUM No. 3”, signed / draft) and from the folder as a clue — keeps the latest revision as the one that governs and re-reads new or changed files. You only check the exceptions (⚠ Needs your eye). Used by every review against the contract. Kept in 🧠 Nova Brain → 📜 Contract area.");
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
        var P = proj.Code;
        void Redraw() => Draw("📜 Contract area");

        var pr = page.Add(Th.Row(false));
        pr.Controls.Add(Th.L("Project:", 10, Th.Text));
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, BackColor = Th.Card, ForeColor = Th.Text, FlatStyle = FlatStyle.Flat };
        foreach (var p in all) cb.Items.Add($"{p.Code} · {p.Name}");
        cb.SelectedIndex = all.IndexOf(proj);
        cb.SelectedIndexChanged += (_, _) => { caProject = all[cb.SelectedIndex].Code; caTab = "All governing"; Redraw(); };
        pr.Controls.Add(cb);
        pr.Controls.Add(Th.B("📋 Review a submittal vs contract…", () => new ContractReviewForm(P, null).Show(), true));

        var reg = ContractRegister.Get(P);
        var status = Th.L("", 9.5f, Th.Muted);
        void Say(string s) => Program.UiContext.Post(_ => { if (!status.IsDisposed) status.Text = s; }, null);

        // ① the contract areas
        page.Add(Th.Section("① Contract areas — everything inside is read and labelled"));
        var areas = ContractRegister.Areas(P);
        var lbA = new ListBox { Width = 980, Height = Math.Max(50, Math.Min(8, areas.Count + 1) * 22), BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, Font = Th.F(10) };
        IdxRefreshStats();
        foreach (var a in areas)
        {
            var n = reg?.Docs.Count(d => d.Path.StartsWith(a.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) ?? 0;
            idxStats.TryGetValue(Projects.ReadAll.KeyOf(a), out var st);
            var read = st == null || st.Readable == 0 ? "" : st.Done >= st.Readable ? " · ✓ read 100%" : $" · 📖 read {100L * st.Done / st.Readable}%";
            lbA.Items.Add($"📁 {a}   ·   {n:N0} files{read}{(Projects.ReadAll.IsNetwork(a) ? " · 🌐" : "")}");
        }
        if (areas.Count == 0) lbA.Items.Add("(no contract area yet — ➕ Add a contract area: the folders that hold the contract, the specs, the BOQ, the drawings…)");
        page.Add(lbA);
        var ra = page.Add(Th.Row());
        ra.Controls.Add(Th.B("➕ Add a contract area…", () =>
        {
            using var d = new FolderBrowserDialog { Description = "A folder that holds contract documents of " + P + " (specs, BOQ, drawings, PTC, addenda… — all of it is read)", SelectedPath = proj.Folders.FirstOrDefault(Directory.Exists) ?? "" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            if (Projects.Private.IsMatch(d.SelectedPath)) { MessageBox.Show(this, "🔒 That folder looks private — Nova never reads it.", "Nova Enhance"); return; }
            try { ContractRegister.SetAreas(P, areas.Append(d.SelectedPath).ToList()); Projects.ReadAll.Now(); Redraw(); } catch (Exception e) { status.Text = "⚠ " + e.Message; }
        }, true));
        ra.Controls.Add(Th.B("🗑 Remove", () =>
        {
            if (lbA.SelectedIndex < 0 || lbA.SelectedIndex >= areas.Count) return;
            try { ContractRegister.SetAreas(P, areas.Where((_, i) => i != lbA.SelectedIndex).ToList()); Redraw(); } catch (Exception e) { status.Text = "⚠ " + e.Message; }
        }));
        ra.Controls.Add(Th.B("🚫 Leave a subfolder out…", () =>
        {
            using var d = new FolderBrowserDialog { Description = "A subfolder Nova should never use (renders, old backups…)", SelectedPath = areas.FirstOrDefault() ?? "" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var c = ContractArea.Cfg(P);
            var ex = c["exclude"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
            ex.Add(d.SelectedPath.TrimEnd('\\')); c["exclude"] = ex;
            try { ContractArea.SaveCfg(P, c); ContractRegister.Invalidate(P); Redraw(); } catch (Exception e) { status.Text = "⚠ " + e.Message; }
        }));
        ra.Controls.Add(Th.B("⟳ Label everything again", () => { ContractRegister.Invalidate(P); status.Text = "🏷 labelling…"; }));
        var stamps = ContractRegister.StampRoles(P);
        var sr = page.Add(Th.Row());
        sr.Controls.Add(Th.L(stamps.Count == 0 ? "🔏 Stamps: none named yet — the strongest evidence (Contract Document · Client · Contractor · Consultant)."
            : "🔏 Stamps: " + string.Join(" · ", stamps.Values.Where(v => v.Role is not ("ignore" or "other")).Select(v => v.Name.Length > 0 ? v.Name : ContractRegister.StampRolesList.First(x => x.Role == v.Role).Label)), 10, Th.Text));
        sr.Controls.Add(Th.B("🔏 Find and name the stamps…", () =>
        {
            using var f = new StampsForm(P, reg);
            if (f.ShowDialog(this) == DialogResult.OK) Redraw();
        }, stamps.Count == 0));

        // ② the register
        var built = reg == null ? "not built yet" : $"updated {reg.Built:dd MMM HH:mm}";
        page.Add(Th.Section($"② Contract register — built by Nova · {reg?.Files ?? 0:N0} files → {reg?.Docs.Count(d => d.Governing) ?? 0:N0} governing documents · {built}"));
        if (reg == null) { Note(ContractRegister.Building(P) ? "🏷 Nova is labelling the documents… this page refreshes by itself." : "Add a contract area above."); }
        else
        {
            var types = ContractRegister.Types(P);
            var cards = page.Add(new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(1000, 0), BackColor = Th.Bg });
            var gov = reg.Docs.Where(d => d.Governing).ToList();
            var counts = new List<(string Label, int N, string Tab)>
            {
                ("Agreement & LoA", gov.Count(d => d.Type == "Agreement & LoA"), "Agreement & LoA"), ("PTC", gov.Count(d => d.Type == "PTC"), "PTC"),
                ("Conditions", gov.Count(d => d.Type.StartsWith("Conditions")), "Conditions"), ("Employer's req.", gov.Count(d => d.Type == "Employer's requirements"), "Employer's requirements"),
                ("Spec documents", gov.Count(d => d.Type == "Specifications"), "Specifications"), ("BOQ", gov.Count(d => d.Type == "BOQ"), "BOQ"),
                ("Addenda", gov.Count(d => d.Type == "Addenda / bulletins"), "Addenda / bulletins"), ("Drawings · latest", gov.Count(d => d.Type.StartsWith("Drawings")), "Drawings"),
                ("EI · VO · VE · RFI", gov.Count(d => d.Type.StartsWith("Changes")), "Changes"),
            };
            foreach (var t in types) counts.Add((t.Name + (t.Acts == "check" ? " ✓" : ""), reg.Docs.Count(d => d.Type == t.Name && d.Governing), t.Name));
            counts.Add(("Letters · reference", reg.Docs.Count(d => d.Status == "reference"), "Reference"));
            foreach (var (label, n, tab) in counts)
            {
                var card = new Panel { Width = 104, Height = 54, BackColor = Color.FromArgb(0x1B, 0x20, 0x30), Margin = new Padding(0, 0, 6, 6), Cursor = Cursors.Hand };
                var top = new Panel { Dock = DockStyle.Top, Height = 3, BackColor = Th.Accent };
                var nl = new Label { Text = n.ToString("N0"), Font = Th.F(14, true), ForeColor = Th.Text, Location = new Point(8, 6), AutoSize = true };
                var tl = new Label { Text = label, Font = Th.F(8), ForeColor = Th.Muted, Location = new Point(8, 32), AutoSize = true, MaximumSize = new Size(96, 0) };
                card.Controls.AddRange(new Control[] { top, nl, tl });
                var tb = tab;
                foreach (Control c in new Control[] { card, nl, tl }) c.Click += (_, _) => { caTab = tb; Redraw(); };
                cards.Controls.Add(card);
            }
            var eyeN = reg.Eyes.Sum(e => e.Paths.Count);
            var tabs = page.Add(Th.Row());
            foreach (var t in new[] { "All governing", "🔏 Stamped", "Specifications", "BOQ", "Drawings", "Changes", "📋 Vendors", "Superseded", "Reference", "Never governs", "⚠ Needs your eye" })
                tabs.Controls.Add(Th.B((caTab == t ? "● " : "") + (t == "⚠ Needs your eye" ? $"⚠ Needs your eye ({eyeN})" : t), () => { caTab = t; Redraw(); }, caTab == t));
            if (caTab == "⚠ Needs your eye") EyePanel(P, reg, types, status, Say);
            else if (caTab == "📋 Vendors") VendorPanel(P, reg, status);
            else
            {
                var fr = page.Add(Th.Row(false));
                fr.Controls.Add(Th.L("🔎 Filter:", 10, Th.Text));
                var fb = Th.Box(false); fb.Width = 380; fb.Text = caFilter; fr.Controls.Add(fb);
                var g = Grid(330);
                foreach (var (h, w) in new[] { ("Document", 300), ("Type", 170), ("Disc.", 50), ("Rev", 60), ("Date", 90), ("Status", 160), ("Evidence (click to sort)", 250), ("How Nova knows", 300), ("Folder", 240) })
                    g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, Width = w, ReadOnly = true });
                IEnumerable<RegDoc> Pick() => caTab switch
                {
                    "All governing" => reg.Docs.Where(d => d.Governing),
                    "🔏 Stamped" => reg.Docs.Where(d => d.Evidence >= 3),
                    "Superseded" => reg.Docs.Where(d => d.Status == "superseded"),
                    "Reference" => reg.Docs.Where(d => d.Status == "reference"),
                    "Never governs" => reg.Docs.Where(d => d.Status == "never governs"),
                    "Conditions" => reg.Docs.Where(d => d.Type.StartsWith("Conditions") && d.Governing),
                    "Drawings" => reg.Docs.Where(d => d.Type.StartsWith("Drawings") && d.Governing),
                    "Changes" => reg.Docs.Where(d => d.Type.StartsWith("Changes") && d.Governing),
                    _ => reg.Docs.Where(d => d.Type == caTab && d.Status != "superseded"),
                };
                void Fill()
                {
                    g.Rows.Clear();
                    var f = fb.Text.Trim();
                    var rows = Pick().Where(d => f.Length == 0 || d.Path.Contains(f, StringComparison.OrdinalIgnoreCase) || d.Type.Contains(f, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(d => caTab == "🔏 Stamped" ? -d.Evidence : 0).ThenBy(d => ContractRegister.Level(P, d.Type) is var l and > 0 ? l : 50).ThenBy(d => d.Type).ThenBy(d => d.Name).Take(3000).ToList();
                    foreach (var d in rows)
                    {
                        var stt = d.Status == "superseded" ? "superseded by " + d.By : d.Governing ? "⚖ " + d.Status : d.Status;
                        var i = g.Rows.Add(d.Name, d.Type + (d.Custom ? " ➕" : ""), d.Disc, d.Rev, d.Date.ToString("dd MMM yyyy"), stt, $"{d.Evidence} · {d.EvidenceText}", (d.Sure ? "" : "? ") + d.How, Path.GetFileName(Path.GetDirectoryName(d.Path) ?? ""));
                        g.Rows[i].Tag = d; g.Rows[i].Cells[0].ToolTipText = d.Path;
                        g.Rows[i].Cells[5].Style.ForeColor = d.Governing ? Th.Good : Th.Muted;
                        if (d.Evidence >= 4) g.Rows[i].Cells[6].Style.ForeColor = Th.Good; else if (d.Evidence == 3) g.Rows[i].Cells[6].Style.ForeColor = Th.Warn;
                        if (!d.Governing) g.Rows[i].DefaultCellStyle.ForeColor = Th.Muted;
                    }
                    status.Text = $"{rows.Count:N0} shown{(rows.Count == 3000 ? " (first 3,000 — filter to narrow)" : "")} · double-click opens · select rows and ✎ Set the type to correct Nova";
                }
                fb.TextChanged += (_, _) => { caFilter = fb.Text; Fill(); };
                Fill();
                g.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && g.Rows[e.RowIndex].Tag is RegDoc d) Shell.Open(d.Path); };
                FileMenu.AttachGrid(g, r => (r.Tag as RegDoc)?.Path);
                page.Add(g, true);
                var gb = page.Add(Th.Row());
                gb.Controls.Add(Th.B("✎ Set the type…", () =>
                {
                    var sel = g.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as RegDoc).Where(d => d != null).Select(d => d.Path).ToList();
                    if (sel.Count == 0) return;
                    TypeMenu(P, types, t => { try { ContractRegister.Decide(P, sel, t); status.Text = $"✓ {sel.Count} file(s) set to {t} — Nova remembers it."; } catch (Exception e) { status.Text = "⚠ " + e.Message; } }).Show(Cursor.Position);
                }));
                gb.Controls.Add(Th.B("📂 Open the folder", () => { if (g.CurrentRow?.Tag is RegDoc d) Shell.Open(Path.GetDirectoryName(d.Path) ?? ""); }));
            }
        }
        page.Add(status);

        // ③ the order of precedence (with your own types and “= same level”)
        page.Add(Th.Section("③ Order of precedence · when two documents disagree, the higher one governs"));
        var order = ContractArea.Order(P).ToList();
        var ob = page.Add(Th.Row(false));
        var lb = new ListBox { Width = 470, Height = 260, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, Font = Th.F(10) };
        void Lines()
        {
            var sel = lb.SelectedIndex; lb.Items.Clear();
            int lvl = 0;
            var ts = ContractRegister.Types(P);
            foreach (var o in order)
            {
                var same = o.StartsWith("=") && lvl > 0; if (!same) lvl++;
                var t = ts.FirstOrDefault(x => x.Name == ContractRegister.Bare(o));
                lb.Items.Add($"{(same ? "= " + lvl : lvl.ToString()),-5} {ContractRegister.Bare(o)}{(t != null ? $"   ➕ yours · {(t.Acts == "check" ? "check list" : t.Acts)}" : "")}");
            }
            if (sel >= 0 && sel < lb.Items.Count) lb.SelectedIndex = sel;
        }
        Lines();
        ob.Controls.Add(lb);
        var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        void Saved(string msg = "✓ Order saved.") { try { ContractArea.Save(P, ContractArea.Of(P), order); ContractRegister.Invalidate(P); status.Text = msg; } catch (Exception e) { status.Text = "⚠ " + e.Message; } }
        void MoveRow(int d)
        {
            var i = lb.SelectedIndex; var j = i + d;
            if (i < 0 || j < 0 || j >= order.Count) return;
            (order[i], order[j]) = (order[j], order[i]); lb.SelectedIndex = -1; Lines(); lb.SelectedIndex = j; Saved();
        }
        side.Controls.Add(Th.B("↑ Up", () => MoveRow(-1)));
        side.Controls.Add(Th.B("↓ Down", () => MoveRow(1)));
        side.Controls.Add(Th.B("= Same level as above", () =>
        {
            var i = lb.SelectedIndex; if (i <= 0) return;
            order[i] = order[i].StartsWith("=") ? ContractRegister.Bare(order[i]) : "=" + order[i]; Lines(); Saved();
        }));
        side.Controls.Add(Th.B("➕ Add a document type…", () =>
        {
            using var f = new DocTypeForm(P, null, order);
            if (f.ShowDialog(this) == DialogResult.OK) Redraw();
        }, true));
        side.Controls.Add(Th.B("✎ Edit", () =>
        {
            if (lb.SelectedIndex < 0) return;
            var name = ContractRegister.Bare(order[lb.SelectedIndex]);
            var t = ContractRegister.Types(P).FirstOrDefault(x => x.Name == name);
            if (t == null) { status.Text = "Built-in types keep their name — move them with ↑ ↓, or add your own type."; return; }
            using var f = new DocTypeForm(P, t, order);
            if (f.ShowDialog(this) == DialogResult.OK) Redraw();
        }));
        side.Controls.Add(Th.B("🗑 Remove", () =>
        {
            if (lb.SelectedIndex < 0) return;
            var name = ContractRegister.Bare(order[lb.SelectedIndex]);
            if (ContractRegister.Types(P).Any(x => x.Name == name))
            {
                if (MessageBox.Show(this, $"Remove your type “{name}”? Its documents are labelled again without it.", "📜 Contract area", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                try { ContractRegister.RemoveType(P, name); Redraw(); } catch (Exception e) { status.Text = "⚠ " + e.Message; }
            }
            else { order.RemoveAt(lb.SelectedIndex); Lines(); Saved("✓ Removed from the order (it no longer governs by rank)."); }
        }));
        side.Controls.Add(Th.B("📖 Read it from the contract", () =>
        {
            status.Text = "📖 Looking for the clause that ranks the documents…";
            ContractArea.OrderFromContract(P, Say, CancellationToken.None).ContinueWith(t => Program.UiContext.Post(_ =>
            {
                if (status.IsDisposed) return;
                if (t.IsFaulted || t.IsCanceled) { status.Text = "⚠ " + (t.Exception?.GetBaseException().Message ?? "stopped"); return; }
                var (o, clause) = t.Result;
                if (o == null) { status.Text = clause; return; }
                foreach (var ty in ContractRegister.Types(P)) if (!o.Contains(ty.Name)) o.Add(ty.Name);
                if (MessageBox.Show(this, "Nova found the order in the contract (" + clause + "):\n\n" + string.Join("\n", o.Select((x, i) => $"{i + 1}  {x}")) + "\n\nUse this order?", "⚖ Order of precedence", MessageBoxButtons.YesNo) != DialogResult.Yes) { status.Text = ""; return; }
                order.Clear(); order.AddRange(o); Lines();
                try { ContractArea.Save(P, ContractArea.Of(P), order, clause); ContractRegister.Invalidate(P); status.Text = "✓ Order saved · " + clause; } catch (Exception e) { status.Text = "⚠ " + e.Message; }
            }, null));
        }));
        side.Controls.Add(Th.B("↺ The usual order", () => { order.Clear(); order.AddRange(ContractArea.DefaultOrder); foreach (var ty in ContractRegister.Types(P)) order.Add(ty.Name); Lines(); Saved(); }));
        ob.Controls.Add(side);
        var clauseNow = ContractArea.OrderClause(P);
        Note((clauseNow.Length > 0 ? "📌 " + clauseNow + " · " : "") + "After signing, a change (EI, VO, PMI, VE, an RFI answer) wins on its own item — the newest first. Drafts and tender (IFT) never govern; letters are reference only. Every change is saved at once.");

        // ④ which document governs?
        page.Add(Th.Section("④ 🧪 Which document governs? · files already read — instant, no AI"));
        var tr = page.Add(Th.Row(false));
        var q = Th.Box(false); q.Width = 420; q.Text = "waterproofing membrane thickness";
        tr.Controls.Add(q);
        var outBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        tr.Controls.Add(Th.B("⚖ Which governs?", () =>
        {
            outBox.Controls.Clear();
            var words = System.Text.RegularExpressions.Regex.Split(q.Text, @"[^\p{L}\p{N}\.\-]+").Where(w => w.Length > 2).ToList();
            var (hits, notUsed) = ContractRegister.Governs(P, words);
            if (hits.Count == 0) { outBox.Controls.Add(Th.Wrap("Nothing found in the contract documents read so far (📶 Indexing progress shows how far the reading is).", 9.5f, Th.Muted)); page.FitAll(); return; }
            var top = true;
            foreach (var h in hits)
            {
                var lab = Th.Wrap($"{(top ? "⚖ GOVERNS → " : "      ")}{h.Doc.Type}{(h.Level is > 0 and < 50 ? " (level " + h.Level + ")" : "")} · {h.Doc.Name}{(h.Doc.Rev.Length > 0 ? " · " + h.Doc.Rev : "")} · {h.Loc}\n      “{h.Text.Replace("\n", " ").Trim()[..Math.Min(240, h.Text.Replace("\n", " ").Trim().Length)]}…”", 9.5f, top ? Th.Good : Th.Text);
                lab.Cursor = Cursors.Hand; var hp = h.Doc.Path; lab.Click += (_, _) => Shell.Open(hp);
                outBox.Controls.Add(lab);
                top = false;
            }
            if (notUsed.Count > 0) outBox.Controls.Add(Th.Wrap("Not used: " + string.Join(" · ", notUsed), 9, Th.Muted));
            page.FitAll();
        }, true));
        page.Add(outBox, true);

        // live: the register finished → refresh the page
        Action<string> upd = null;
        upd = p => Program.UiContext.Post(_ => { if (status.IsDisposed) { ContractRegister.Updated -= upd; return; } if (p == P) Redraw(); }, null);
        ContractRegister.Updated += upd;
        var tm = new System.Windows.Forms.Timer { Interval = 1500 };
        tm.Tick += (_, _) => { if (status.IsDisposed) { tm.Stop(); tm.Dispose(); ContractRegister.Updated -= upd; return; } var ph = ContractRegister.Phase(P); if (ph.Length > 0) status.Text = ph; };
        tm.Start();
    }

    // ------------------------------------------------------------------ 🗄 Nova index

    void IndexPage()
    {
        Note("ONE shared, growing store on this PC (encrypted): every feature — search, Guided search, Nova Chat, the contract register, reviews, the phone, Claude's checks — reads it first and saves back what it worked out. A file is read once; its labels, stamps and links are worked out once. Every write waits its turn. 🔒 private files are never stored.");
        page.Add(Th.Section("What is inside"));
        var g = Grid(330);
        foreach (var (h, w) in new[] { ("Layer", 220), ("Count", 110), ("What it holds", 640) })
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, Width = w, ReadOnly = true });
        page.Add(g, true);
        var info = page.Add(Th.L("counting…", 9.5f, Th.Muted));
        void Fill()
        {
            Task.Run(() => (NovaIndex.Inside(), FolderIndex.SizeMb())).ContinueWith(t => Program.UiContext.Post(_ =>
            {
                if (g.IsDisposed || t.IsFaulted) return;
                g.Rows.Clear();
                foreach (var (layer, n, what) in t.Result.Item1) g.Rows.Add(layer, n < 0 ? "—" : n.ToString("N0"), what);
                info.Text = $"🗄 files index: {t.Result.Item2:N0} MB · emails: their own database, linked by document number · 💡 learned answers: memory file (linked) · updated {DateTime.Now:HH:mm:ss}";
            }, null));
        }
        Fill();
        page.Add(Th.Section("Keep it healthy"));
        var r = page.Add(Th.Row());
        r.Controls.Add(Th.B("⟳ Refresh", Fill));
        r.Controls.Add(Th.B("🧹 Tidy the index (safe)", () => { Task.Run(NovaIndex.Tidy).ContinueWith(_ => Program.UiContext.Post(__ => { if (!info.IsDisposed) info.Text = "✓ Tidied."; Fill(); }, null)); }));
        void Reset(string layer, string label)
        {
            if (MessageBox.Show(this, $"Work out “{label}” again from scratch? Nothing is lost: the next use works it out again and saves it.", "🗄 Nova index", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { NovaIndex.Reset(layer); foreach (var p in Projects.All()) ContractRegister.Invalidate(p.Code); Fill(); } catch (Exception e) { info.Text = "⚠ " + e.Message; }
        }
        r.Controls.Add(Th.B("📐 Labels again", () => Reset("labels", "📐 document labels")));
        r.Controls.Add(Th.B("🔏 Stamps again", () => Reset("stamps", "🔏 stamps")));
        r.Controls.Add(Th.B("📋 Vendor rows again", () => Reset("vendors", "📋 vendor rows")));
        r.Controls.Add(Th.B("📜 Registers again", () => Reset("register", "📜 contract registers")));
        Note("Labels are worked out again by themselves when a file changes or when your 📐 filing / 📁 project rules in 🧠 Nova Brain change.");
    }

    void VendorPanel(string P, ContractReg reg, Label status)
    {
        var checkDocs = reg.Docs.Where(d => d.Governing && d.Acts == "check").ToList();
        Note(checkDocs.Count == 0 ? "No check-list documents yet — add a document type “Vendors List” (What it does: ✓ Check list) in ③ below." :
            $"📋 The rows of your check lists ({string.Join(" · ", checkDocs.Select(d => d.Name).Take(4))}) — read from the documents that govern. Click a header to sort; double-click opens the page.");
        var fr = page.Add(Th.Row(false));
        fr.Controls.Add(Th.L("🔎 Filter (brand, product, division):", 10, Th.Text));
        var fb = Th.Box(false); fb.Width = 380; fr.Controls.Add(fb);
        var g = Grid(380);
        foreach (var (h, w) in new[] { ("Division / list", 220), ("Product", 220), ("Row (brand · country · agent …)", 620), ("Page", 50), ("Document", 280) })
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, Width = w, ReadOnly = true });
        var paths = checkDocs.Select(d => d.Path).ToList();
        void Fill()
        {
            g.Rows.Clear();
            var rows = NovaIndex.Vendors(P, paths, fb.Text.Trim(), 4000);
            foreach (var r in rows) { var i = g.Rows.Add(r.Division, r.Product, r.Row, r.Page, Path.GetFileName(r.Path)); g.Rows[i].Tag = r; }
            status.Text = $"{rows.Count:N0} row(s){(rows.Count == 4000 ? " (first 4,000 — filter to narrow)" : "")}";
        }
        fb.TextChanged += (_, _) => Fill();
        g.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || g.Rows[e.RowIndex].Tag is not NovaIndex.VendorRow r) return;
            try { Opener.Open(new FileHit { Path = r.Path }, new PieceHit { Loc = "page " + r.Page, Goto = "page:" + r.Page, Text = r.Row, Matches = new() }, fb.Text.Trim()); } catch { Shell.Open(r.Path); }
        };
        FileMenu.AttachGrid(g, r => (r.Tag as NovaIndex.VendorRow)?.Path, r => r.Tag is NovaIndex.VendorRow vr ? new FileMenu.At { Loc = "page " + vr.Page, Goto = "page:" + vr.Page, Text = vr.Row } : null);
        Fill();
        page.Add(g, true);
    }

    ContextMenuStrip TypeMenu(string P, List<DocType> types, Action<string> pick)
    {
        var m = new ContextMenuStrip();
        foreach (var t in ContractRegister.BuiltIn.Concat(types.Select(x => x.Name))) { var tt = t; m.Items.Add(tt, null, (_, _) => pick(tt)); }
        return m;
    }

    void EyePanel(string P, ContractReg reg, List<DocType> types, Label status, Action<string> say)
    {
        Note("Only what Nova could not settle by itself. Each answer is kept in 🧠 Nova Brain, so the same case is not asked again.");
        if (reg.Eyes.Count == 0) { Note("✓ Nothing to check — Nova settled every document.", Th.Good); return; }
        foreach (var e in reg.Eyes)
        {
            var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.FromArgb(0x1B, 0x20, 0x30), Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 4, 0, 4) };
            box.Controls.Add(Th.L(e.Title, 10.5f, e.Kind == "scan" ? Th.Warn : Th.Text));
            box.Controls.Add(Th.Wrap(string.Join(" · ", e.Paths.Take(8).Select(Path.GetFileName)) + (e.Paths.Count > 8 ? $" · … and {e.Paths.Count - 8} more" : ""), 9.5f, Th.Text));
            box.Controls.Add(Th.Wrap(e.Detail, 9, Th.Muted));
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(980, 0) };
            var paths = e.Paths;
            void Done(string answer, IEnumerable<string> which = null) { try { ContractRegister.Decide(P, which ?? paths, answer); status.Text = "✓ Kept — Nova labels again…"; } catch (Exception x) { status.Text = "⚠ " + x.Message; } }
            switch (e.Kind)
            {
                case "scan":
                    row.Controls.Add(Th.B("✦ Read with Gemini OCR (page images, your consent)", () =>
                    {
                        if (MessageBox.Show(this, $"Send the PAGE IMAGES of {paths.Count} scanned file(s) to Google Gemini to read them? (🔒 private folders never; nothing else is sent.)", "✦ Gemini OCR", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                        Task.Run(() => ContractRegister.ReadScans(paths, say, CancellationToken.None)).ContinueWith(t => say(t.IsFaulted ? "⚠ " + t.Exception?.GetBaseException().Message : $"✓ {t.Result} page(s) read with Gemini — labelling again…"));
                    }, true));
                    row.Controls.Add(Th.B("⏳ Try local OCR again (up to 10 min each, when idle)", () => { Ocr.Forgive(paths, true); Projects.ReadAll.Now(); Done("!skip"); status.Text = "✓ Nova tries Windows OCR again with more patience."; }));
                    row.Controls.Add(Th.B("Skip", () => Done("!skip")));
                    break;
                case "dup":
                    row.Controls.Add(Th.B("Use the newest of each", () =>
                    {
                        var older = reg.Docs.Where(d => paths.Contains(d.Path)).GroupBy(d => d.Type + "|" + d.Key).SelectMany(g => g.OrderByDescending(d => d.Date).Skip(1)).Select(d => d.Path).ToList();
                        Done("!old", older);
                    }, true));
                    row.Controls.Add(Th.B("Open them", () => { foreach (var p in paths.Take(6)) Shell.Open(p); }));
                    break;
                case "stamps":
                    row.Controls.Add(Th.B("Valid — it governs", () => Done("!valid"), true));
                    row.Controls.Add(Th.B("Not a contract document", () => Done(ContractRegister.Letters)));
                    row.Controls.Add(Th.B("Open", () => { foreach (var p in paths.Take(4)) Shell.Open(p); }));
                    break;
                case "draft":
                    row.Controls.Add(Th.B("Signed — they govern", () => Done("!signed"), true));
                    row.Controls.Add(Th.B("Draft — never governs", () => Done("Draft")));
                    row.Controls.Add(Th.B("Open", () => { foreach (var p in paths.Take(4)) Shell.Open(p); }));
                    break;
                default:
                    var canAi = Ai.Active() != null && !Ai.QuotaOut && FolderAsk.Sees != "never";
                    var ab = Th.B("✦ Ask the AI (first 1,500 characters only)", () =>
                    {
                        if (FolderAsk.Sees == "ask" && MessageBox.Show(this, $"Send the file name and the first ~1,500 characters of up to 30 documents to the AI to know their type? (Never whole documents; 🔒 private folders never.)", "✦ AI", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                        ContractRegister.AskAi(P, paths.Take(30).ToList(), say, CancellationToken.None).ContinueWith(t => say(t.IsFaulted ? "⚠ " + t.Exception?.GetBaseException().Message : $"✓ The AI labelled {t.Result} — labelling again…"));
                    }, true);
                    ab.Enabled = canAi;
                    row.Controls.Add(ab);
                    row.Controls.Add(Th.B("✎ Set the type…", () => TypeMenu(P, types, t => Done(t)).Show(Cursor.Position)));
                    row.Controls.Add(Th.B("Reference only", () => Done(ContractRegister.Letters)));
                    row.Controls.Add(Th.B("Open the first", () => Shell.Open(paths[0])));
                    break;
            }
            box.Controls.Add(row);
            page.Add(box);
        }
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

/// <summary>➕ Your own kind of contract document (e.g. Approved Vendor List): name, how Nova recognizes it, where it ranks, what it does.</summary>
class DocTypeForm : NovaForm
{
    public DocTypeForm(string project, DocType edit, List<string> order)
    {
        Text = (edit == null ? "➕ Add a document type · " : "✎ Edit a document type · ") + project;
        Width = 820; Height = 880; StartPosition = FormStartPosition.CenterParent;
        var t = edit ?? new DocType();
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14), BackColor = Th.Bg };
        Label H(string s) { var l = Th.L(s, 10.5f, Th.Text); l.Font = Th.F(10.5f, true); l.Margin = new Padding(0, 10, 0, 2); return l; }
        TextBox Box(string v, int w = 740) { var b = Th.Box(false); b.Width = w; b.Text = v; return b; }
        flow.Controls.Add(Th.Wrap("Nova finds these documents in your contract areas by itself and keeps the latest revision.", 9.5f, Th.Muted));
        flow.Controls.Add(H("1 · Name"));
        var name = Box(t.Name); flow.Controls.Add(name);
        flow.Controls.Add(H("2 · How Nova recognizes it — words separated by commas (Nova proposes them; edit freely)"));
        flow.Controls.Add(Th.L("In the file name:", 9.5f, Th.Muted));
        var nw = Box(string.Join(", ", t.NameWords)); flow.Controls.Add(nw);
        flow.Controls.Add(Th.L("Inside page 1:", 9.5f, Th.Muted));
        var pw = Box(string.Join(", ", t.PageWords)); flow.Controls.Add(pw);
        flow.Controls.Add(Th.L("Document code (optional), e.g. AVL in SLV-ZUC-AVL-001:", 9.5f, Th.Muted));
        var code = Box(t.Code, 160); flow.Controls.Add(code);
        flow.Controls.Add(H("3 · Where it ranks"));
        var levels = ContractRegister.Levels(project).Where(l => l.Type != t.Name).ToList();
        var rank = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520, BackColor = Th.Card, ForeColor = Th.Text, FlatStyle = FlatStyle.Flat };
        var choices = new List<(int At, bool Same)>();
        for (int i = 0; i < levels.Count; i++)
        {
            rank.Items.Add($"above {levels[i].Type} (level {levels[i].Level})"); choices.Add((i, false));
            rank.Items.Add($"= same level as {levels[i].Type} (level {levels[i].Level})"); choices.Add((i, true));
        }
        rank.Items.Add("at the end"); choices.Add((levels.Count, false));
        var cur = order.FindIndex(o => ContractRegister.Bare(o) == t.Name);
        var specs = levels.FindIndex(l => l.Type == "Specifications");
        rank.SelectedIndex = cur >= 0 ? Math.Min(rank.Items.Count - 1, Math.Max(0, (order[cur].StartsWith("=") ? (cur - 1) * 2 + 1 : cur * 2))) : t.Acts == "check" && specs >= 0 ? specs * 2 + 1 : rank.Items.Count - 1;
        flow.Controls.Add(rank);
        flow.Controls.Add(H("4 · What it does in a review"));
        var acts = new[] { ("governs", "⚖ Governs — its text wins like any contract document"), ("check", "✓ Check list — the brand / manufacturer / product in a submittal must be on it"), ("reference", "📎 Reference only — shown, never decides") };
        var radios = acts.Select(a => new RadioButton { Text = a.Item2, AutoSize = true, ForeColor = Th.Text, Font = Th.F(10), Checked = t.Acts == a.Item1 }).ToList();
        foreach (var r in radios) flow.Controls.Add(r);
        void SetActs(string a) { for (int i = 0; i < acts.Length; i++) radios[i].Checked = acts[i].Item1 == a; }
        flow.Controls.Add(H("For Nova (one sentence: what these documents are, how to use them)"));
        var desc = Box(t.Desc); flow.Controls.Add(desc);
        name.Leave += (_, _) =>
        {
            if (edit != null || name.Text.Trim().Length < 3 || nw.Text.Trim().Length > 0) return;
            var s = ContractRegister.Suggest(name.Text);
            nw.Text = string.Join(", ", s.NameWords); pw.Text = string.Join(", ", s.PageWords); code.Text = s.Code;
            if (s.Desc.Length > 0 && t.Desc.Length == 0) desc.Text = s.Desc;
            SetActs(s.Acts);
        };
        var found = Th.Wrap("", 9.5f, Th.Good); found.MaximumSize = new Size(760, 0);
        DocType Read() => new()
        {
            Name = name.Text.Trim(), Code = code.Text.Trim(), Desc = desc.Text.Trim(),
            Acts = acts[Math.Max(0, radios.FindIndex(r => r.Checked))].Item1,
            NameWords = nw.Text.Split(',').Select(x => x.Trim()).Where(x => x.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PageWords = pw.Text.Split(',').Select(x => x.Trim()).Where(x => x.Length > 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
        // ✓ the checklist: what Nova found + what you chose (📌) — untick what is wrong
        var chk = new CheckedListBox { Width = 760, Height = 170, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, Font = Th.F(9), CheckOnClick = true, HorizontalScrollbar = true };
        var items = new List<(string Path, bool Chosen, bool Folder)>();
        var areasNow = ContractRegister.Areas(project);
        var addAreas = new List<string>(); var extra = new List<string>(); var folders = new List<string>();
        bool Inside(string p) => areasNow.Concat(addAreas).Any(a => p.StartsWith(a.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || p.Equals(a, StringComparison.OrdinalIgnoreCase));
        void AddLine(string path, string text, bool tick, bool chosen, bool folder)
        {
            var i = items.FindIndex(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { chk.Items[i] = text; chk.SetItemChecked(i, tick); items[i] = (path, chosen || items[i].Chosen, folder); return; }
            items.Add((path, chosen, folder)); chk.Items.Add(text, tick);
        }
        string Line(RegDoc x, bool chosen) => $"{(chosen ? "📌 " : "")}{x.Name}  ·  {(chosen ? "your choice · " : "")}{x.How}  ·  {x.Evidence} {x.EvidenceText}  ·  {(x.Status == "superseded" ? "superseded by " + x.By : x.Status)}";
        // ✦ words Nova learns from your picks (their names and page 1)
        void Learn(List<string> paths)
        {
            var titles = new List<string>();
            foreach (var p in paths.Take(6))
            {
                var t1 = FolderIndex.FirstText(p, 1200);
                if (t1.Length < 20) try { t1 = ContractArea.FirstPages(p, CancellationToken.None); } catch { }
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(t1, @"LIST OF [A-Z][A-Z /&,\-]{3,60}|APPROVED [A-Z][A-Z /&\-]{3,40}|[A-Z][A-Z /&\-]{6,40} SCHEDULE"))
                    titles.Add(System.Text.RegularExpressions.Regex.Replace(m.Value, @"\s+(FOR|THE|AND)?\s*$", "").Trim());
            }
            var have = pw.Text.Split(',').Select(x => x.Trim().ToUpperInvariant()).ToHashSet();
            var newT = titles.Select(x => x.Length > 50 ? x[..50].Trim() : x).Where(x => !have.Contains(x.ToUpperInvariant())).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
            var nameW = ContractRegister.Norm(name.Text);
            var haveN = nw.Text.Split(',').Select(x => ContractRegister.Norm(x)).ToHashSet();
            var newN = nameW.Length > 2 && !haveN.Contains(nameW) ? new List<string> { name.Text.Trim().ToLowerInvariant() } : new();
            if (newT.Count + newN.Count == 0) return;
            if (MessageBox.Show(this, "✦ Nova learned from the files you chose:\n\n" + string.Join("\n", newT.Select(x => "• page 1: " + x).Concat(newN.Select(x => "• name: " + x))) + "\n\nKeep these words, so similar documents are found by themselves?", "✦ Nova learned", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            if (newT.Count > 0) pw.Text = string.Join(", ", pw.Text.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Concat(newT));
            if (newN.Count > 0) nw.Text = string.Join(", ", nw.Text.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Concat(newN));
        }
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        row.Controls.Add(Th.B("🔎 Find them now", () =>
        {
            var d = Read(); if (d.Name.Length == 0) return;
            found.Text = "🔎 looking in your contract areas (loose match: vendor list = Vendors_List)…";
            Task.Run(() => ContractRegister.Preview(project, d)).ContinueWith(tk => Program.UiContext.Post(_ =>
            {
                if (found.IsDisposed) return;
                var l = tk.IsFaulted ? new List<RegDoc>() : tk.Result;
                foreach (var x in l.OrderByDescending(x => x.Governing).ThenByDescending(x => x.Evidence)) AddLine(x.Path, Line(x, false), x.Governing, false, false);
                found.Text = l.Count == 0 ? "Nothing found with these words — 📌 Choose the files or the folder yourself." : $"🔎 {l.Count}{(l.Count > 60 ? "+" : "")} found · ticked = this type · untick what is wrong (Nova remembers).";
            }, null));
        }));
        var choose = Th.B("📌 Choose files or a folder… ▾", () => { });
        var menu = new ContextMenuStrip();
        menu.Items.Add("📄 Files…  (one or more documents)", null, (_, _) =>
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Filter = "Documents|*.pdf;*.docx;*.doc;*.xlsx;*.xls;*.msg|All files|*.*", InitialDirectory = areasNow.FirstOrDefault(Directory.Exists) ?? "" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var picked = new List<string>();
            foreach (var f in dlg.FileNames)
            {
                if (Projects.Private.IsMatch(f)) { found.Text = "🔒 " + Path.GetFileName(f) + " looks private — left out."; continue; }
                if (!Inside(f))
                {
                    var r = MessageBox.Show(this, $"📌 This file is outside your contract areas:\n{f}\n\nYes = add its folder to the contract areas\nNo = use only this file", "📌 Outside the contract areas", MessageBoxButtons.YesNoCancel);
                    if (r == DialogResult.Cancel) continue;
                    if (r == DialogResult.Yes) addAreas.Add(Path.GetDirectoryName(f) ?? f); else extra.Add(f);
                }
                picked.Add(f);
                var fi = new FileInfo(f);
                var doc = ContractRegister.Label(project, new Projects.FileRow { Path = f, Name = fi.Name, Size = fi.Length, Modified = fi.LastWriteTime }, new List<DocType> { Read() }, new() { [f] = Read().Name }, areasNow.Concat(addAreas).ToList());
                AddLine(f, Line(doc, true), true, true, false);
            }
            if (picked.Count > 0) { found.Text = $"📌 {picked.Count} file(s) chosen."; Learn(picked); }
        });
        menu.Items.Add("📁 A folder…  (everything inside = this type, also new files later)", null, (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Everything in this folder becomes “" + name.Text.Trim() + "” (also new files later)", SelectedPath = areasNow.FirstOrDefault(Directory.Exists) ?? "" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var f = dlg.SelectedPath;
            if (Projects.Private.IsMatch(f)) { found.Text = "🔒 That folder looks private — Nova never reads it."; return; }
            if (!Inside(f + "\\x") && MessageBox.Show(this, $"📌 This folder is outside your contract areas:\n{f}\n\nAdd it to the contract areas?", "📌 Outside the contract areas", MessageBoxButtons.YesNo) == DialogResult.Yes) addAreas.Add(f);
            folders.Add(f);
            int n = 0; try { n = Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories).Take(5000).Count(); } catch { }
            AddLine(f, $"📁 {f}  ·  your folder ({n:N0} files): new files there get this type too  ·  rule", true, true, true);
            try { Learn(Directory.EnumerateFiles(f, "*.pdf", SearchOption.AllDirectories).Take(4).ToList()); } catch { }
        });
        choose.Click += (_, _) => menu.Show(choose, new Point(0, choose.Height));
        row.Controls.Add(choose);
        row.Controls.Add(Th.B(edit == null ? "💾 Add to " + project : "💾 Save", () =>
        {
            var d = Read();
            if (d.Name.Length < 2) { found.Text = "⚠ Give it a name."; return; }
            if (ContractRegister.BuiltIn.Contains(d.Name)) { found.Text = "⚠ That name is a built-in type — choose another."; return; }
            if (d.NameWords.Count == 0 && d.PageWords.Count == 0 && d.Code.Length == 0 && items.Count == 0) { found.Text = "⚠ Give at least one word or a code, or 📌 choose the files, so Nova can recognize it."; return; }
            var o = order.Where(x => ContractRegister.Bare(x) != t.Name && ContractRegister.Bare(x) != d.Name).ToList();
            var (at, same) = choices[Math.Max(0, rank.SelectedIndex)];
            var idx = Math.Min(at, o.Count);
            if (same) { idx = Math.Min(at + 1, o.Count); while (idx < o.Count && o[idx].StartsWith("=")) idx++; o.Insert(idx, "=" + d.Name); }
            else o.Insert(idx, d.Name);
            try
            {
                ContractRegister.SaveType(project, d, edit?.Name);
                ContractArea.Save(project, ContractArea.Of(project), o);
                // 📌 your choices and unticks: kept as answers (Nova never re-guesses them)
                var c = ContractArea.Cfg(project);
                var dec = c["decide"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                var fo = c["folders"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                var ex = c["extra"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
                for (int i = 0; i < items.Count; i++)
                {
                    var (p, chosen, folder) = items[i];
                    var on = chk.GetItemChecked(i);
                    if (folder) { if (on) fo[p] = d.Name; continue; }
                    if (on && chosen) dec[p] = d.Name;
                    else if (!on) dec[p] = "!not:" + d.Name;
                }
                foreach (var x in extra) if (!ex.Any(e => e?.ToString() == x)) ex.Add(x);
                c["decide"] = dec; c["folders"] = fo; c["extra"] = ex;
                if (addAreas.Count > 0) c["areas"] = new System.Text.Json.Nodes.JsonArray(ContractRegister.Areas(project).Concat(addAreas).Distinct(StringComparer.OrdinalIgnoreCase).Select(a => (System.Text.Json.Nodes.JsonNode)a).ToArray());
                ContractArea.SaveCfg(project, c);
                ContractRegister.Invalidate(project);
                DialogResult = DialogResult.OK;
            }
            catch (Exception e) { found.Text = "⚠ " + e.Message; }
        }, true));
        row.Controls.Add(Th.B("Cancel", () => DialogResult = DialogResult.Cancel));
        flow.Controls.Add(row);
        flow.Controls.Add(found);
        flow.Controls.Add(chk);
        Controls.Add(flow);
    }
}


/// <summary>🔏 Name the stamps once: the pictures that come back on many contract documents (by fingerprint) → Contract document · Client · Contractor · Consultant.</summary>
class StampsForm : NovaForm
{
    public StampsForm(string project, ContractReg reg)
    {
        Text = "🔏 Stamps · " + project;
        Width = 980; Height = 720; StartPosition = FormStartPosition.CenterParent;
        var roles = ContractRegister.StampRoles(project);
        var top = Th.Wrap("Nova looks at the pictures on page 1 and the last page of your contract documents. Stamps are the same picture every time, so once you name them, Nova recognizes them on every document in milliseconds — no OCR, no AI. Name each one (or mark it “Not a stamp” for logos).", 9.5f, Th.Muted);
        top.Dock = DockStyle.Top; top.Padding = new Padding(12, 8, 12, 8); top.MaximumSize = new Size(940, 0); top.AutoSize = true;
        var list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12), BackColor = Th.Bg };
        var status = Th.L("", 9.5f, Th.Muted);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 8), BackColor = Th.Bg };
        var rows = new List<(string Hash, ComboBox Role, TextBox Name)>();
        var cts = new CancellationTokenSource();
        FormClosing += (_, _) => cts.Cancel();
        bottom.Controls.Add(Th.B("💾 Save the stamps", () =>
        {
            var d = new Dictionary<string, (string, string)>(roles);
            foreach (var (h, cb, nb) in rows) if (cb.SelectedIndex >= 0) d[h] = (ContractRegister.StampRolesList[cb.SelectedIndex].Role, nb.Text.Trim());
            try { ContractRegister.SaveStamps(project, d); DialogResult = DialogResult.OK; } catch (Exception e) { status.Text = "⚠ " + e.Message; }
        }, true));
        bottom.Controls.Add(Th.B("Cancel", () => DialogResult = DialogResult.Cancel));
        bottom.Controls.Add(status);
        Controls.Add(list); Controls.Add(bottom); Controls.Add(top);
        // the PDFs to look at: “executed / stamped” folders and governing documents first
        var pdfs = (reg?.Docs ?? new()).Where(d => d.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => Regex.IsMatch(d.Path, "executed|stamped", RegexOptions.IgnoreCase)).ThenByDescending(d => d.Governing).ThenByDescending(d => d.Date)
            .Select(d => d.Path).Take(250).ToList();
        Shown += (_, _) =>
        {
            status.Text = "🔏 looking at the pictures…";
            Task.Run(() => NovaIndex.FindStamps(pdfs, s => Program.UiContext.Post(_ => { if (!status.IsDisposed) status.Text = s; }, null), cts.Token)).ContinueWith(t => Program.UiContext.Post(_ =>
            {
                if (IsDisposed) return;
                if (t.IsFaulted || t.IsCanceled) { status.Text = "⚠ " + (t.Exception?.GetBaseException().Message ?? "stopped"); return; }
                status.Text = t.Result.Count == 0 ? "No repeated pictures found in these documents." : $"{t.Result.Count} repeated picture(s) — name them:";
                foreach (var (hash, docs, w, h, png, sample) in t.Result)
                {
                    var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.FromArgb(0x1B, 0x20, 0x30), Margin = new Padding(0, 0, 0, 6), Padding = new Padding(6) };
                    var pb = new PictureBox { Width = 200, Height = 110, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
                    try { if (png != null) pb.Image = Image.FromStream(new MemoryStream(png)); } catch { }
                    row.Controls.Add(pb);
                    var info = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = row.BackColor };
                    info.Controls.Add(Th.L($"on {docs} document(s) · {w}×{h} px · e.g. {Path.GetFileName(sample)}", 9, Th.Muted));
                    var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, BackColor = Th.Card, ForeColor = Th.Text, FlatStyle = FlatStyle.Flat };
                    foreach (var (_, label) in ContractRegister.StampRolesList) cb.Items.Add(label);
                    var nb = Th.Box(false); nb.Width = 360; nb.PlaceholderText = "name (e.g. Nine Yards — Client)";
                    if (roles.TryGetValue(hash, out var have)) { cb.SelectedIndex = Array.FindIndex(ContractRegister.StampRolesList, x => x.Role == have.Role); nb.Text = have.Name; }
                    info.Controls.Add(cb); info.Controls.Add(nb);
                    row.Controls.Add(info);
                    list.Controls.Add(row);
                    rows.Add((hash, cb, nb));
                }
            }, null));
        };
    }
}
