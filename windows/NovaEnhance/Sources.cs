using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>🗂 One folder you gave Nova for a project: its name (as Nova calls it), its kind (contract, drawings…), how deep Nova
/// reads it (names only, or the text inside) and whether a copy is kept on this PC for when the VPN is down.</summary>
class ProjectSource
{
    public string Path = "", Name = "", Kind = "Other", Depth = "names";   // names · text
    public bool Offline;
    public DateTime Added = DateTime.Now, Synced;
}

/// <summary>
/// 🗂 Project sources: the office folders (O: MZA-Projects, S: Site Projects, N: Archive…) and local ones you pick for each project,
/// named and typed. Their file names (and, if asked, the text inside) join Nova's project index — the same index as your emails,
/// so answers link emails, drawings, submittals and the contract together. ⚡ Offline copies are kept in step while the folder is
/// reachable (VPN up). Designed by A.HAMROUNI
/// </summary>
static class ProjectSources
{
    public static readonly string[] Kinds = { "Contract", "Specifications", "Drawings", "Submittals", "Correspondence", "Minutes", "BOQ / Cost", "Other" };

    /// <summary>Contractual weight: the contract first, then specifications, approved drawings, submittals, letters, minutes.</summary>
    public static int Rank(string kind) => kind switch
    {
        "Contract" => 0, "Specifications" => 1, "Drawings" => 2, "Submittals" => 3, "BOQ / Cost" => 4, "Correspondence" => 5, "Minutes" => 6, _ => 7
    };

    public static string GuessKind(string folderName)
    {
        var n = folderName.ToLowerInvariant();
        if (Regex.IsMatch(n, @"contract|agreement|conditions|tender|\bloa\b|award")) return "Contract";
        if (Regex.IsMatch(n, @"spec")) return "Specifications";
        if (Regex.IsMatch(n, @"draw|dwg|\bifc\b|\bcad\b|layout|plans?\b|sheets?\b|\bshd\b")) return "Drawings";
        if (Regex.IsMatch(n, @"submit|\bwir\b|\bmir\b|\bmar\b|\bsds\b|\bssm\b|\bmss\b|\brfi\b|approval|shop")) return "Submittals";
        if (Regex.IsMatch(n, @"letter|corresp|\bltr\b|email|mail|\bei\b|instruction|ncr|sor")) return "Correspondence";
        if (Regex.IsMatch(n, @"\bmom\b|minute|meeting")) return "Minutes";
        if (Regex.IsMatch(n, @"boq|cost|payment|\bipc\b|invoice|valuation|budget|\bvo\b|variation")) return "BOQ / Cost";
        return "Other";
    }

    public static IEnumerable<(Project P, ProjectSource S)> All() => Projects.All().SelectMany(p => (p.Sources ?? new()).Select(s => (p, s)));

    static string Norm(string p) => (p ?? "").TrimEnd('\\') + "\\";

    /// <summary>The source a file or folder belongs to (the deepest one).</summary>
    public static (Project P, ProjectSource S)? Of(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var n = Norm(path);
        var best = All().Where(x => n.StartsWith(Norm(x.S.Path), StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.S.Path.Length).FirstOrDefault();
        return best.S == null ? null : best;
    }

    /// <summary>“SLV · Contract” for a passage label, or "" when the file is not in a named source.</summary>
    public static string Tag(string path) => Of(path) is { } x ? $"{x.S.Name} · {x.S.Kind}" : "";

    /// <summary>Adds or updates a source; its folder joins the project's indexed folders.</summary>
    public static void Save(string projectCode, ProjectSource s)
    {
        var all = Projects.All();
        foreach (var p in all) { p.Sources ??= new(); p.Sources.RemoveAll(x => x.Path.Equals(s.Path, StringComparison.OrdinalIgnoreCase)); }
        var proj = all.FirstOrDefault(p => p.Code.Equals(projectCode, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
        if (proj == null) throw new Exception("Make a project first (Settings ▸ 📁 Projects)");
        proj.Sources.Add(s);
        if (!proj.Folders.Contains(s.Path, StringComparer.OrdinalIgnoreCase)) proj.Folders.Add(s.Path);
        Projects.SaveAll(all);
        Projects.Indexer.Refresh();
        if (s.Offline) Offline.Wake();
    }

    public static void Remove(string path, bool alsoFolder)
    {
        var all = Projects.All();
        foreach (var p in all)
        {
            p.Sources ??= new();
            p.Sources.RemoveAll(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (alsoFolder) p.Folders.RemoveAll(f => f.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
        Projects.SaveAll(all);
    }

    /// <summary>The folders Nova already had for each project become named sources (their names and kinds guessed; nothing re-read).</summary>
    public static void Migrate()
    {
        try
        {
            var all = Projects.All(); bool changed = false;
            foreach (var p in all)
            {
                p.Sources ??= new();
                foreach (var f in p.Folders.Where(f => !p.Sources.Any(s => s.Path.Equals(f, StringComparison.OrdinalIgnoreCase))))
                {
                    p.Sources.Add(new ProjectSource { Path = f, Name = $"{p.Code} · {System.IO.Path.GetFileName(f.TrimEnd('\\'))}", Kind = GuessKind(System.IO.Path.GetFileName(f.TrimEnd('\\'))), Depth = Projects.ReadAll.On ? "text" : "names" });
                    changed = true;
                }
            }
            if (changed) Projects.SaveAll(all);
        }
        catch (Exception e) { Log.Warn("project sources", "migrate: " + e.Message); }
    }

    /// <summary>The folders whose text Nova reads inside (Depth = text), for 📖 background reading.</summary>
    public static List<string> TextFolders() => All().Where(x => x.S.Depth == "text").Select(x => x.S.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Folders on the office network that are unreachable now (VPN down) — said with every answer that uses them.</summary>
    public static List<string> Unreachable() => All().Where(x => x.S.Path.StartsWith(@"\\") || IsNetworkDrive(x.S.Path)).Where(x => !Reach(x.S.Path)).Select(x => x.S.Name).Distinct().ToList();

    static readonly Dictionary<string, (DateTime At, bool Ok)> reach = new(StringComparer.OrdinalIgnoreCase);
    public static bool Reach(string path)
    {
        lock (reach) if (reach.TryGetValue(path, out var r) && DateTime.Now - r.At < TimeSpan.FromSeconds(20)) return r.Ok;
        var t = Task.Run(() => { try { return Directory.Exists(path); } catch { return false; } });
        var ok = t.Wait(2500) && t.Result;
        lock (reach) reach[path] = (DateTime.Now, ok);
        return ok;
    }

    public static bool IsNetworkDrive(string path)
    {
        try { return path.Length >= 2 && path[1] == ':' && new DriveInfo(path[..2]).DriveType == DriveType.Network; } catch { return false; }
    }

    // ------------------------------------------------------------------ ⚡ offline copies

    public static class Offline
    {
        public static string Root => System.IO.Path.Combine(Paths.Root, "Offline copies");
        static readonly AutoResetEvent wake = new(false);
        static Thread thread;
        public static volatile string Line = "";

        public static void Wake() => wake.Set();

        static string Safe(string s) => Regex.Replace(s, @"[\\/:*?""<>|]+", " ").Trim();

        public static string LocalRoot(Project p, ProjectSource s) => System.IO.Path.Combine(Root, Safe(p.Code), Safe(s.Name));

        /// <summary>The local copy of a network file (when that folder has an offline copy), else null.</summary>
        public static string LocalOf(string path)
        {
            if (Of(path) is not { } x || !x.S.Offline) return null;
            var rel = path.Length > x.S.Path.TrimEnd('\\').Length ? path[(x.S.Path.TrimEnd('\\').Length + 1)..] : "";
            var local = System.IO.Path.Combine(LocalRoot(x.P, x.S), rel);
            return File.Exists(local) || Directory.Exists(local) ? local : null;
        }

        public static void Start()
        {
            if (thread != null) return;
            thread = new Thread(() => Guard.Run("offline copies", Loop)) { IsBackground = true, Name = "Nova offline copies", Priority = ThreadPriority.Lowest };
            thread.Start();
        }

        static void Loop()
        {
            Thread.Sleep(TimeSpan.FromSeconds(90));
            while (true)
            {
                try
                {
                    foreach (var (p, s) in All().Where(x => x.S.Offline).ToList())
                    {
                        if (!Reach(s.Path)) { Line = $"⚡ {s.Name}: waiting for the folder (VPN down?)"; continue; }
                        if (DateTime.Now - s.Synced < TimeSpan.FromMinutes(30)) continue;
                        Line = $"⚡ copying {s.Name}…";
                        var dst = LocalRoot(p, s);
                        Directory.CreateDirectory(dst);
                        // robocopy: only new / changed files, deleted ones removed; private-looking folders never copied.
                        var args = $"\"{s.Path.TrimEnd('\\')}\" \"{dst}\" /MIR /R:1 /W:2 /MT:8 /XJ /FFT /NP /NFL /NDL /NJH /NJS " +
                                   "/XD *TrustWalletBackup* *wallet* *password* *passwd* *keepass* *bitwarden* *bank* *passport* *salary* *personal*";
                        using var pr = Process.Start(new ProcessStartInfo("robocopy.exe", args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                        if (pr != null && !pr.WaitForExit((int)TimeSpan.FromHours(3).TotalMilliseconds)) { try { pr.Kill(); } catch { } }
                        var code = pr?.ExitCode ?? 16;
                        if (code < 8)
                        {
                            var all = Projects.All();
                            var src = all.SelectMany(x => x.Sources ?? new()).FirstOrDefault(x => x.Path.Equals(s.Path, StringComparison.OrdinalIgnoreCase));
                            if (src != null) { src.Synced = DateTime.Now; Projects.SaveAll(all); }
                            Line = $"⚡ {s.Name} ✓ {DateTime.Now:HH:mm}";
                        }
                        else { Line = $"⚠ {s.Name}: copy failed (code {code})"; Log.Warn("offline copies", $"{s.Path}: robocopy {code}"); }
                    }
                }
                catch (Exception e) { Log.Warn("offline copies", e.Message); }
                wake.WaitOne(TimeSpan.FromMinutes(10));
            }
        }
    }

    // ------------------------------------------------------------------ the estimate before indexing

    public static (int Files, long Bytes, int Readable, bool Capped) Estimate(string folder, CancellationToken ct)
    {
        int files = 0, readable = 0; long bytes = 0;
        var stack = new Stack<string>(); stack.Push(folder);
        var sw = Stopwatch.StartNew();
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested || files > 300000 || sw.Elapsed > TimeSpan.FromSeconds(90)) return (files, bytes, readable, true);
            var d = stack.Pop();
            try
            {
                foreach (var i in new DirectoryInfo(d).EnumerateFileSystemInfos())
                {
                    if (i is DirectoryInfo di) { if (!Projects.Private.IsMatch(di.Name) && (di.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(di.FullName); continue; }
                    if (i is FileInfo f) { files++; try { bytes += f.Length; } catch { } if (Regex.IsMatch(f.Extension, @"^\.(pdf|docx?|xlsx?|xlsm|pptx?|rtf|txt|msg|eml|csv)$", RegexOptions.IgnoreCase)) readable++; }
                }
            }
            catch { }
        }
        return (files, bytes, readable, false);
    }
}

/// <summary>
/// 🗂 The small explorer: your office drives and local drives as a tree; pick a folder, give it a name, a project and a kind,
/// choose names-only or text, and an offline copy. The list on the right is everything Nova reads for your projects. Designed by A.HAMROUNI
/// </summary>
class SourcesForm : NovaForm
{
    readonly TreeView tree;
    readonly ListView list;
    readonly TextBox nameBox, pathBox;
    readonly ComboBox projBox, kindBox, depthBox;
    readonly CheckBox offBox;
    readonly Label info, est;
    CancellationTokenSource estCts;

    public SourcesForm()
    {
        Text = "Nova Enhance · 🗂 Project sources";
        Width = 1250; Height = 800; MinimumSize = new Size(900, 560);
        var top = new Stack { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 10, 16, 6) };
        top.Add(Th.L("🗂 Project sources", 14, Th.Text, true));
        top.Add(Th.Wrap("Pick the office or local folders Nova should know for each project. Give each one a name (how Nova calls it in answers) and a kind — the contract counts more than a letter. " +
                        "Names only = instant search by file name and drawing number · Names + text = Nova also reads inside PDF, Word and Excel (in the background). " +
                        "⚡ Offline copy = kept on this PC so it opens fast and still works when the VPN is down. 🔒 Private folders are never read.", 9.5f, Th.Muted), true);
        var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Th.Line, SplitterWidth = 5 };
        Flex.DoubleClickMax(split);   // ↔ double-click the divider: the first side takes almost all the room, again to go back
        Controls.Add(split); Controls.Add(top);

        // ◧ the folder tree (one level at a time: the office server is never listed in full)
        tree = new TreeView { Dock = DockStyle.Fill, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(10), HideSelection = false, ItemHeight = 24 };
        var treeTop = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Th.Bg, Padding = new Padding(4) };
        treeTop.Controls.Add(Th.B("＋ Add this folder ➜", AddSelected, true));
        treeTop.Controls.Add(Th.B("⌨ Type a path…", TypePath));
        treeTop.Controls.Add(Th.B("↻", Roots));
        split.Panel1.Controls.Add(tree); split.Panel1.Controls.Add(treeTop);
        tree.BeforeExpand += (_, e) => Expand(e.Node);
        tree.AfterSelect += (_, e) => { if (e.Node?.Tag is string p) Pick(p, null); };

        // ◨ chosen sources + the editor
        list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(9.5f), HideSelection = false };
        foreach (var (h, w) in new[] { ("Name", 200), ("Project", 70), ("Kind", 110), ("Reads", 90), ("Offline", 70), ("Status", 140), ("Folder", 380) }) list.Columns.Add(h, w);
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is ProjectSource s) Pick(s.Path, s); };
        var ed = new Stack { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 8, 10, 8), BackColor = Th.Bg };
        pathBox = new TextBox { ReadOnly = true, Width = 600, BackColor = Th.Card2, ForeColor = Th.Muted, BorderStyle = BorderStyle.FixedSingle };
        nameBox = new TextBox { Width = 260, BackColor = Th.Card2, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle };
        ComboBox Combo(IEnumerable<string> items, int w) { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = w, BackColor = Th.Card2, ForeColor = Th.Text, FlatStyle = FlatStyle.Flat }; c.Items.AddRange(items.ToArray()); return c; }
        projBox = Combo(Projects.All().Select(p => p.Code), 110);
        kindBox = Combo(ProjectSources.Kinds, 140);
        depthBox = Combo(new[] { "Names only", "Names + text inside" }, 170);
        offBox = new CheckBox { Text = "⚡ Offline copy on this PC", AutoSize = true, ForeColor = Th.Text };
        ed.Add(pathBox);
        var r1 = ed.Add(Th.Row(false));
        foreach (var (l, c) in new (string, Control)[] { ("Name", nameBox), ("Project", projBox), ("Kind", kindBox) }) { r1.Controls.Add(Th.L(l, 9.5f, Th.Muted)); r1.Controls.Add(c); }
        var r2 = ed.Add(Th.Row(false));
        r2.Controls.Add(Th.L("Nova reads", 9.5f, Th.Muted)); r2.Controls.Add(depthBox); r2.Controls.Add(offBox);
        var r3 = ed.Add(Th.Row(false));
        r3.Controls.Add(Th.B("💾 Save this source", SaveCurrent, true));
        r3.Controls.Add(Th.B("📏 Estimate", Estimate));
        r3.Controls.Add(Th.B("🗑 Remove from Nova", RemoveCurrent));
        r3.Controls.Add(Th.B("📂 Open the folder", () => { if (pathBox.Text.Length > 0) Shell.Open(pathBox.Text); }));
        est = ed.Add(Th.Wrap("", 9.5f, Th.AccentText), true);
        info = ed.Add(Th.Wrap("", 9.5f, Th.Muted), true);
        split.Panel2.Controls.Add(list); split.Panel2.Controls.Add(ed);
        Shown += (_, _) => { try { split.SplitterDistance = Math.Max(300, Width * 38 / 100); } catch { } Roots(); Fill(); };
        var t = new System.Windows.Forms.Timer { Interval = 3000 }; t.Tick += (_, _) => Statuses(); t.Start();
        FormClosed += (_, _) => { t.Dispose(); estCts?.Cancel(); };
    }

    // ------------------------------------------------------------------ the tree

    void Roots()
    {
        tree.Nodes.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Network or DriveType.Fixed).OrderBy(d => d.DriveType == DriveType.Network ? 0 : 1).ThenBy(d => d.Name))
        {
            string label;
            try { label = $"{(d.DriveType == DriveType.Network ? "🌐" : "💽")} {d.Name.TrimEnd('\\')}  {(d.IsReady ? d.VolumeLabel : "(not reachable — VPN?)")}"; }
            catch { label = $"{(d.DriveType == DriveType.Network ? "🌐" : "💽")} {d.Name.TrimEnd('\\')}"; }
            var n = new TreeNode(label) { Tag = d.RootDirectory.FullName };
            n.Nodes.Add(new TreeNode("⏳") { Tag = null });
            tree.Nodes.Add(n);
        }
        foreach (var p in Store.Str("win_src_paths", "").Split('|').Where(x => x.Length > 2))
        {
            var n = new TreeNode("📌 " + p) { Tag = p }; n.Nodes.Add(new TreeNode("⏳")); tree.Nodes.Add(n);
        }
        Mark(tree.Nodes);
    }

    void Expand(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Tag != null || node.Tag is not string path) return;
        node.Nodes[0].Text = "⏳ reading…";
        Task.Run(() =>
        {
            try
            {
                var t = Task.Run(() => new DirectoryInfo(path).EnumerateDirectories().Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                    .Select(d => d.FullName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(3000).ToList());
                if (!t.Wait(TimeSpan.FromSeconds(20))) throw new Exception("the folder did not answer in 20 s (VPN slow or down?)");
                return t.Result;
            }
            catch (Exception e) { throw new Exception(e.GetBaseException().Message); }
        }).ContinueWith(r => BeginInvoke(new Action(() =>
        {
            node.Nodes.Clear();
            if (r.IsFaulted) { node.Nodes.Add(new TreeNode("⚠ " + r.Exception?.GetBaseException().Message) { ForeColor = Th.Warn }); return; }
            foreach (var d in r.Result)
            {
                var name = Path.GetFileName(d);
                if (Projects.Private.IsMatch(name)) { node.Nodes.Add(new TreeNode("🔒 " + name + " (private — never read)") { ForeColor = Th.Muted }); continue; }
                var n = new TreeNode("📁 " + name) { Tag = d };
                n.Nodes.Add(new TreeNode("⏳"));
                node.Nodes.Add(n);
            }
            if (node.Nodes.Count == 0) node.Nodes.Add(new TreeNode("(no sub-folders)") { ForeColor = Th.Muted });
            Mark(node.Nodes);
        })));
    }

    /// <summary>Folders already chosen are shown ✓ in green.</summary>
    void Mark(TreeNodeCollection nodes)
    {
        var chosen = ProjectSources.All().Select(x => x.S.Path.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (TreeNode n in nodes)
            if (n.Tag is string p && chosen.Contains(p.TrimEnd('\\'))) { n.ForeColor = Th.Good; if (!n.Text.Contains('✓')) n.Text += "  ✓"; }
    }

    void TypePath()
    {
        var p = Prompt.Ask(this, "Nova · add a path", "A folder path (e.g. \\\\192.168.1.12\\MZA-Projects\\SLV or O:\\SLV):");
        if (string.IsNullOrWhiteSpace(p)) return;
        var paths = Store.Str("win_src_paths", "").Split('|').Where(x => x.Length > 2).Append(p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
        Store.Set("win_src_paths", string.Join("|", paths));
        Roots();
        Pick(p.Trim(), null);
    }

    // ------------------------------------------------------------------ the list and the editor

    void Fill()
    {
        list.BeginUpdate(); list.Items.Clear();
        foreach (var (p, s) in ProjectSources.All().OrderBy(x => x.P.Code).ThenBy(x => ProjectSources.Rank(x.S.Kind)))
        {
            var it = new ListViewItem(new[] { s.Name, p.Code, s.Kind, s.Depth == "text" ? "names + text" : "names", s.Offline ? "⚡ yes" : "", "", s.Path }) { Tag = s };
            list.Items.Add(it);
        }
        list.EndUpdate();
        Statuses();
        info.Text = $"{list.Items.Count} source(s) · " + Projects.Indexer.Line();
    }

    void Statuses()
    {
        foreach (ListViewItem it in list.Items)
        {
            if (it.Tag is not ProjectSource s) continue;
            var st = Projects.Status.TryGetValue(s.Path, out var x) ? x : null;
            var text = st == null ? "⏳ waiting" : st.Phase == "listed" ? $"✓ {st.Files:N0} files · {st.Last:HH:mm}" : st.Phase == "listing" ? $"⟳ {st.Files:N0}…" : st.Phase;
            if (s.Offline && s.Synced > DateTime.MinValue) text += $" · ⚡ {s.Synced:HH:mm}";
            if (it.SubItems[5].Text != text) it.SubItems[5].Text = text;
        }
        if (ProjectSources.Offline.Line.Length > 0) info.Text = $"{list.Items.Count} source(s) · {ProjectSources.Offline.Line}";
    }

    void Pick(string path, ProjectSource s)
    {
        pathBox.Text = path;
        var existing = s ?? ProjectSources.All().FirstOrDefault(x => x.S.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).S;
        var folder = Path.GetFileName(path.TrimEnd('\\'));
        var proj = existing != null ? ProjectSources.Of(path)?.P : Projects.OfFolder(path) ?? Projects.ForText(path);
        projBox.SelectedItem = proj?.Code ?? (projBox.Items.Count > 0 ? projBox.Items[0] : null);
        nameBox.Text = existing?.Name ?? $"{proj?.Code ?? "General"} · {folder}";
        kindBox.SelectedItem = existing?.Kind ?? ProjectSources.GuessKind(path);
        depthBox.SelectedIndex = existing?.Depth == "text" ? 1 : 0;
        offBox.Checked = existing?.Offline ?? false;
        est.Text = existing != null ? "✓ already a source — change it and 💾 Save" : "Not a source yet — check the name, project and kind, then 💾 Save (📏 Estimate shows how big it is).";
    }

    void AddSelected()
    {
        if (tree.SelectedNode?.Tag is not string p) { est.Text = "Select a folder in the tree first."; return; }
        Pick(p, null);
    }

    void SaveCurrent()
    {
        if (pathBox.Text.Length == 0) { est.Text = "Select a folder first."; return; }
        if (Projects.Private.IsMatch(pathBox.Text)) { est.Text = "🔒 This folder looks private — Nova never reads it."; return; }
        try
        {
            ProjectSources.Save(projBox.SelectedItem?.ToString() ?? "", new ProjectSource
            {
                Path = pathBox.Text.TrimEnd('\\'), Name = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : Path.GetFileName(pathBox.Text.TrimEnd('\\')),
                Kind = kindBox.SelectedItem?.ToString() ?? "Other", Depth = depthBox.SelectedIndex == 1 ? "text" : "names", Offline = offBox.Checked
            });
            if (depthBox.SelectedIndex == 1) Projects.ReadAll.Start();
            est.Text = $"✓ Saved — Nova is listing “{nameBox.Text}” now" + (depthBox.SelectedIndex == 1 ? " and will read inside it in the background" : "") + (offBox.Checked ? " · ⚡ the offline copy starts in a minute" : "") + ".";
            Fill(); Mark(tree.Nodes);
        }
        catch (Exception e) { est.Text = "⚠ " + e.Message; }
    }

    void RemoveCurrent()
    {
        if (pathBox.Text.Length == 0) return;
        if (MessageBox.Show(this, $"Remove “{nameBox.Text}” from Nova?\n\nIts files stay where they are; Nova stops listing and reading it.", "Nova · remove a source", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        ProjectSources.Remove(pathBox.Text.TrimEnd('\\'), true);
        est.Text = "Removed."; Fill(); Roots();
    }

    void Estimate()
    {
        var p = pathBox.Text;
        if (p.Length == 0) return;
        estCts?.Cancel(); var my = estCts = new CancellationTokenSource();
        est.Text = "📏 Counting… (up to 90 s on the office network)";
        Task.Run(() => ProjectSources.Estimate(p, my.Token)).ContinueWith(r => BeginInvoke(new Action(() =>
        {
            if (my.IsCancellationRequested) return;
            if (r.IsFaulted) { est.Text = "⚠ " + r.Exception?.GetBaseException().Message; return; }
            var (files, bytes, readable, capped) = r.Result;
            var namesMin = Math.Max(1, files / 4000);
            var textHours = readable * 3.0 / 3600;
            est.Text = $"📏 {(capped ? "at least " : "")}{files:N0} files · {Projects.Size(bytes)} · {readable:N0} PDF / Word / Excel  →  names in about {namesMin} min" +
                       $" · text inside about {(textHours < 1 ? Math.Max(1, (int)(textHours * 60)) + " min" : textHours.ToString("0.#") + " h")} in the background (only while the folder is reachable)" +
                       (bytes > 50L << 30 ? " · ⚡ offline copy not advised for this size" : "");
        })));
    }
}
