using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>One of your projects: its code, names, the codes in email subjects, and its folders.</summary>
class Project
{
    public string Id = Guid.NewGuid().ToString("N")[..8];
    public string Code = "", Name = "";
    public List<string> Codes = new();      // in email subjects and file names: SLV, SLV-EUW, MZA-OMN-22-1497 …
    public List<string> Folders = new();
    public int Order;
}

/// <summary>A line of the scan summary (a folder of your work folder).</summary>
class ProjectProposal
{
    public string Folder = "", Display = "", Kind = "project";   // project · general · other · locked · network
    public string Code = "", Name = "";
    public List<string> Codes = new();
    public long Emails, Files, Pdf, Office, Cad, Bytes;
    public DateTime Last;
    public List<string> Subfolders = new(), FileCodes = new();
    public bool Use;
    public int Order = 999;
}

/// <summary>
/// 📁 Projects: your work folder (e.g. F:\_MZA) read by name — numbered folders are projects, "_" folders are general reference,
/// private ones are never read — matched with your emails; the folders you confirm are kept indexed in the background like your email,
/// and every drawing / document number is found by its file name (PDF first, Word next, CAD last). Designed by A.HAMROUNI
/// </summary>
static class Projects
{
    static readonly JsonSerializerOptions J = new() { IncludeFields = true };
    static readonly string[] OfficeExt = { ".doc", ".docx", ".docm", ".xls", ".xlsx", ".xlsm", ".csv", ".ppt", ".pptx" };
    static readonly string[] CadExt = { ".dwg", ".dxf", ".dgn", ".rvt", ".rfa", ".ifc", ".nwd", ".nwc", ".skp", ".3dm", ".pln" };
    public static readonly Regex Private = new(@"wallet|password|passwd|bank|crypto|seed|private|personal|keepass|bitwarden|\bkeys?\b|passport|salary", RegexOptions.IgnoreCase);
    static readonly Regex Numbered = new(@"^\s*(\d{1,3})\s*[-_.)]\s*(.+)$");

    public static string WorkFolder { get => Store.Str("win_work_folder"); set => Store.Set("win_work_folder", value); }

    public static List<Project> All()
    {
        try { return JsonSerializer.Deserialize<List<Project>>(Store.Str("win_projects", "[]"), J) ?? new(); } catch { return new(); }
    }

    public static void SaveAll(List<Project> list) { Store.Set("win_projects", JsonSerializer.Serialize(list, J)); Indexer.Wake(); }

    /// <summary>"_" folders: general reference used with every project.</summary>
    public static List<string> General
    {
        get { try { return JsonSerializer.Deserialize<List<string>>(Store.Str("win_proj_general", "[]")) ?? new(); } catch { return new(); } }
        set => Store.Set("win_proj_general", JsonSerializer.Serialize(value ?? new()));
    }

    /// <summary>The project an email belongs to (codes in the subject first, then in the text).</summary>
    public static Project ForMail(MailHit h)
    {
        if (h == null) return null;
        var all = All();
        var subj = h.Subject ?? "";
        var body = h.FirstLines(1500);
        Project best = null; int bestScore = 0;
        foreach (var p in all)
        {
            int s = 0;
            foreach (var c in p.Codes.Append(p.Code).Where(c => c.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Regex.IsMatch(subj, @"(?<![A-Za-z0-9])" + Regex.Escape(c) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase)) s += 10 + c.Length;
                else if (Regex.IsMatch(body, @"(?<![A-Za-z0-9])" + Regex.Escape(c) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase)) s += 2;
            }
            if (p.Name.Length > 3 && subj.Contains(p.Name, StringComparison.OrdinalIgnoreCase)) s += 12;
            if (s > bestScore) { bestScore = s; best = p; }
        }
        return best;
    }

    /// <summary>All folders to read for a project: its own and the general reference folders.</summary>
    public static List<string> FoldersOf(Project p) => (p?.Folders ?? new()).Concat(General).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ------------------------------------------------------------------ 🔍 scanning the work folder (names only)

    public class ScanProgress { public volatile string Current = ""; public volatile int Folders, Files; }

    /// <summary>Reads the names (never the contents) of your work folder; network shortcuts are read only when network is true.</summary>
    public static List<ProjectProposal> Scan(string root, bool network, ScanProgress pg, CancellationToken ct)
    {
        var res = new List<ProjectProposal>();
        ScanRoot(root, res, pg, ct, "");
        // Network shortcuts (.lnk) at the top: listed, and scanned only when you ask.
        foreach (var l in Directory.GetFiles(root, "*.lnk"))
        {
            var target = Shortcut(l);
            if (target.Length == 0) continue;
            var label = Path.GetFileNameWithoutExtension(l);
            if (!network) { res.Add(new ProjectProposal { Folder = target, Display = "🌐 " + label, Kind = "network" }); continue; }
            pg.Current = "🌐 " + label + "…";
            try { if (Directory.Exists(target)) ScanRoot(target, res, pg, ct, "🌐 " + label + " › "); else res.Add(new ProjectProposal { Folder = target, Display = "🌐 " + label + " (not reachable)", Kind = "network" }); }
            catch (Exception e) when (e is not OperationCanceledException) { res.Add(new ProjectProposal { Folder = target, Display = "🌐 " + label + " — " + e.Message, Kind = "network" }); }
        }
        Match(res, pg, ct);
        return res.OrderBy(x => x.Kind switch { "project" => 0, "general" => 1, "network" => 2, "other" => 3, _ => 4 }).ThenBy(x => x.Order).ThenBy(x => x.Display).ToList();
    }

    static void ScanRoot(string root, List<ProjectProposal> res, ScanProgress pg, CancellationToken ct, string prefix)
    {
        foreach (var d in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(d);
            var p = new ProjectProposal { Folder = d, Display = prefix + name };
            var m = Numbered.Match(name);
            if (Private.IsMatch(name)) { p.Kind = "locked"; res.Add(p); continue; }
            if (m.Success)
            {
                p.Kind = "project"; p.Order = int.Parse(m.Groups[1].Value);
                var rest = m.Groups[2].Value.Trim();
                var br = Regex.Match(rest, @"\(([A-Za-z0-9\-]{2,12})\)");
                p.Name = Regex.Replace(rest, @"\s*\([^)]*\)\s*", " ").Trim();
                if (br.Success) p.Code = br.Groups[1].Value.ToUpperInvariant();
            }
            else if (name.StartsWith("_")) p.Kind = Regex.IsMatch(name, @"software|driver|font|\bhr\b|_hr$|install|setup", RegexOptions.IgnoreCase) ? "other" : "general";
            else p.Kind = "other";
            pg.Current = name; pg.Folders++;
            if (p.Kind is "project" or "general") Walk(p, pg, ct);
            res.Add(p);
        }
    }

    /// <summary>Matches each project with your emails: which codes appear in subjects, and how often.</summary>
    static void Match(List<ProjectProposal> res, ScanProgress pg, CancellationToken ct)
    {
        foreach (var p in res.Where(x => x.Kind == "project"))
        {
            ct.ThrowIfCancellationRequested();
            pg.Current = "Matching " + p.Display + " with your emails…";
            var cands = new List<string>();
            if (p.Code.Length > 0) cands.Add(p.Code);
            cands.AddRange(p.FileCodes.Select(c => c.Split('-')[0]).Distinct());
            cands.AddRange(p.FileCodes);
            foreach (var w in Regex.Split(p.Name, @"[\s\-_]+").Where(w => w.Length >= 4 && !Regex.IsMatch(w, "^(villa|school|project|tower|the|and)$", RegexOptions.IgnoreCase))) cands.Add(w);
            var counts = cands.Distinct(StringComparer.OrdinalIgnoreCase).Select(c => (c, n: MailIndex.CountSubject(c))).Where(x => x.n > 0).OrderByDescending(x => x.n).ToList();
            p.Emails = counts.Count > 0 ? counts[0].n : 0;
            if (p.Code.Length == 0) p.Code = counts.FirstOrDefault(x => Regex.IsMatch(x.c, "^[A-Z0-9-]{2,}$")).c ?? Acronym(p.Name);
            p.Codes = counts.Where(x => x.n >= 3).Select(x => x.c).Prepend(p.Code).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            p.Use = p.Emails >= 20 || p.Files > 500;
        }
        foreach (var p in res.Where(x => x.Kind == "general")) p.Use = true;
    }

    static string Acronym(string name)
    {
        var w = Regex.Split(name, @"[\s\-_]+").Where(x => x.Length > 0).ToList();
        return w.Count >= 2 ? string.Concat(w.Take(4).Select(x => char.ToUpperInvariant(x[0]))) : name.ToUpperInvariant();
    }

    static void Walk(ProjectProposal p, ScanProgress pg, CancellationToken ct)
    {
        var codes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var subs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(p.Folder);
        while (stack.Count > 0 && p.Files < 400_000)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try { foreach (var d in Directory.EnumerateDirectories(dir)) if (!Private.IsMatch(Path.GetFileName(d))) stack.Push(d); } catch { }
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(dir).EnumerateFiles().ToList(); } catch { continue; }
            var sub = Path.GetRelativePath(p.Folder, dir).Split(Path.DirectorySeparatorChar)[0];
            foreach (var f in files)
            {
                p.Files++; pg.Files++;
                var x = f.Extension.ToLowerInvariant();
                if (x == ".pdf") p.Pdf++; else if (OfficeExt.Contains(x)) p.Office++; else if (CadExt.Contains(x)) p.Cad++;
                try { p.Bytes += f.Length; if (f.LastWriteTime > p.Last) p.Last = f.LastWriteTime; } catch { }
                if (sub != ".") subs[sub] = subs.GetValueOrDefault(sub) + 1;
                var cm = Regex.Match(f.Name, @"^([A-Z][A-Z0-9]{1,6})-([A-Z0-9]{2,6})-");
                if (cm.Success) { var k = cm.Groups[1].Value + "-" + cm.Groups[2].Value; codes[k] = codes.GetValueOrDefault(k) + 1; }
            }
            if (p.Files % 2000 == 0) pg.Current = $"{p.Display} · {p.Files:N0} files…";
        }
        p.FileCodes = codes.OrderByDescending(c => c.Value).Take(5).Select(c => c.Key).ToList();
        p.Subfolders = subs.OrderByDescending(c => c.Value).Take(6).Select(c => c.Key).ToList();
    }

    static string Shortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic sh = Activator.CreateInstance(t);
            try { return (string)sh.CreateShortcut(lnk).TargetPath ?? ""; } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sh); }
        }
        catch { return ""; }
    }

    public static string Size(long b) => b > 1L << 30 ? $"{b / (double)(1L << 30):0} GB" : b > 1 << 20 ? $"{b / (double)(1 << 20):0} MB" : $"{b / 1024} KB";

    /// <summary>Saves the lines you ticked: projects, general reference folders and extra network folders.</summary>
    public static void Accept(List<ProjectProposal> lines)
    {
        var list = All();
        foreach (var p in lines.Where(l => l.Use && l.Kind == "project"))
        {
            var ex = list.FirstOrDefault(x => x.Folders.Contains(p.Folder, StringComparer.OrdinalIgnoreCase)) ?? list.FirstOrDefault(x => x.Code.Equals(p.Code, StringComparison.OrdinalIgnoreCase));
            if (ex == null) { ex = new Project(); list.Add(ex); }
            ex.Code = p.Code; ex.Name = p.Name; ex.Order = p.Order;
            foreach (var c in p.Codes) if (!ex.Codes.Contains(c, StringComparer.OrdinalIgnoreCase)) ex.Codes.Add(c);
            if (!ex.Folders.Contains(p.Folder, StringComparer.OrdinalIgnoreCase)) ex.Folders.Add(p.Folder);
        }
        var gen = General;
        foreach (var g in lines.Where(l => l.Use && l.Kind == "general")) if (!gen.Contains(g.Folder, StringComparer.OrdinalIgnoreCase)) gen.Add(g.Folder);
        foreach (var g in lines.Where(l => !l.Use && l.Kind == "general")) gen.RemoveAll(x => x.Equals(g.Folder, StringComparison.OrdinalIgnoreCase));
        General = gen;
        SaveAll(list.OrderBy(p => p.Order).ToList());
    }

    // ------------------------------------------------------------------ 📐 document register: a number → the files with it in their name

    public class DocHit { public string Path = "", Rev = "", Status = ""; public int Rank; public bool Cad; }

    /// <summary>Files whose name contains the number, best first: PDF, then Word/Excel, CAD last; latest revision first.</summary>
    public static List<DocHit> FindDoc(string number, IEnumerable<string> folders)
    {
        var res = new List<DocHit>();
        var num = number.Trim();
        if (num.Length < 4) return res;
        var roots = folders.Where(f => f.Length > 0).ToList();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var c = FolderIndex.Open();
            foreach (var root in roots)
            {
                var pre = FolderIndex.NormalRoot(root).TrimEnd('\\') + "\\";
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT path FROM files WHERE lower(substr(path,1,$n))=lower($p) AND path LIKE $q LIMIT 60";
                cmd.Parameters.AddWithValue("$n", pre.Length); cmd.Parameters.AddWithValue("$p", pre); cmd.Parameters.AddWithValue("$q", "%" + num + "%");
                using var r = cmd.ExecuteReader();
                while (r.Read()) { var p = r.GetString(0); if (System.IO.Path.GetFileName(p).Contains(num, StringComparison.OrdinalIgnoreCase)) paths.Add(p); }
            }
        }
        catch { }
        // Not indexed yet: look on the disk by name (a few seconds on a big folder).
        if (paths.Count == 0)
            foreach (var root in roots.Where(Directory.Exists))
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*" + num + "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(40))
                        if (!Private.IsMatch(f)) paths.Add(f);
                }
                catch { }
        foreach (var p in paths)
        {
            var x = System.IO.Path.GetExtension(p).ToLowerInvariant();
            var d = Revisions.Parse(p, File.Exists(p) ? File.GetLastWriteTime(p).Ticks : 0);
            res.Add(new DocHit { Path = p, Rev = d.Rev, Cad = CadExt.Contains(x), Rank = x == ".pdf" ? 0 : x is ".docx" or ".doc" ? 1 : OfficeExt.Contains(x) ? 2 : CadExt.Contains(x) ? 9 : 5 });
        }
        // Latest revision of the best kind first; the others are marked superseded.
        var ordered = res.OrderBy(h => h.Rank).ThenByDescending(h => Revisions.Parse(h.Path, 0).Rank.Item1).ThenByDescending(h => Revisions.Parse(h.Path, 0).Rank.Item2, StringComparer.Ordinal)
                         .ThenByDescending(h => File.Exists(h.Path) ? File.GetLastWriteTime(h.Path) : DateTime.MinValue).ToList();
        var first = ordered.FirstOrDefault(h => !h.Cad);
        foreach (var h in ordered) h.Status = h == first ? "latest" : h.Cad ? "CAD (not used for answers)" : h.Rank == first?.Rank ? "superseded" : "";
        return ordered;
    }

    public static readonly Regex DocNumber = new(@"\b[A-Z][A-Z0-9]{1,6}(?:-[A-Z0-9]{1,8}){3,10}\b");

    // ------------------------------------------------------------------ background indexing, like the email

    public class State { public string Folder = "", Phase = "waiting"; public int Done, Total; public DateTime Last; }

    public static readonly ConcurrentDictionary<string, State> Status = new(StringComparer.OrdinalIgnoreCase);

    public static class Indexer
    {
        static Thread thread;
        static readonly AutoResetEvent wake = new(false);
        static readonly ConcurrentDictionary<string, DateTime> dirty = new(StringComparer.OrdinalIgnoreCase);
        static readonly List<FileSystemWatcher> watchers = new();
        public static bool On { get => Store.Bool("win_proj_index", true); set { Store.SetBool("win_proj_index", value); Wake(); } }

        public static void Start()
        {
            if (thread != null) return;
            thread = new Thread(Loop) { IsBackground = true, Name = "Nova projects", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public static void Wake() => wake.Set();

        static IEnumerable<string> AllFolders() => All().SelectMany(p => p.Folders).Concat(General).Distinct(StringComparer.OrdinalIgnoreCase);

        static void Watch()
        {
            lock (watchers)
            {
                foreach (var w in watchers) w.Dispose();
                watchers.Clear();
                foreach (var f in AllFolders().Where(Directory.Exists))
                    try
                    {
                        var w = new FileSystemWatcher(f) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
                        var ff = f;
                        FileSystemEventHandler h = (_, e) => { if (!Private.IsMatch(e.FullPath)) dirty[ff] = DateTime.Now; };
                        w.Created += h; w.Changed += h; w.Renamed += (_, e) => dirty[ff] = DateTime.Now; w.Deleted += h;
                        w.EnableRaisingEvents = true;
                        watchers.Add(w);
                    }
                    catch (Exception e) { Log.Warn("projects", "watch " + f + ": " + e.Message); }
            }
        }

        /// <summary>Gentle: at full speed after working hours, when the PC is idle, or with ⚡ Index now.</summary>
        static bool MayRun() => On && (IndexProgress.Rushing || !Gentle.On || !Gentle.WorkingNow || Gentle.Idle > TimeSpan.FromMinutes(3));

        static void Loop()
        {
            Thread.Sleep(TimeSpan.FromSeconds(90));   // let Outlook and the email index start first
            var lastFull = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            string watched = "";
            while (true)
            {
                try
                {
                    var folders = AllFolders().ToList();
                    var key = string.Join("|", folders);
                    if (key != watched) { Watch(); watched = key; }
                    foreach (var f in folders)
                    {
                        var st = Status.GetOrAdd(f, x => new State { Folder = x });
                        var due = !lastFull.TryGetValue(f, out var lf) || DateTime.Now - lf > TimeSpan.FromHours(20) || (dirty.TryGetValue(f, out var dt) && dt > lf && DateTime.Now - dt > TimeSpan.FromMinutes(2));
                        if (!due) continue;
                        if (!MayRun()) { st.Phase = "waiting (gentle — after working hours or when the PC is idle)"; continue; }
                        if (!Directory.Exists(f) && !GDrive.IsRoot(f)) { st.Phase = "⚠ not reachable"; continue; }
                        var pg = new FolderIndex.Progress();
                        using var cts = new CancellationTokenSource();
                        st.Phase = "reading";
                        var t = new Thread(() => { try { FolderIndex.Search(f, null, null, _ => { }, null, pg, cts.Token); } catch { } }) { IsBackground = true };
                        t.SetApartmentState(ApartmentState.STA);
                        t.Start();
                        while (!t.Join(2000))
                        {
                            st.Done = pg.Done; st.Total = pg.Total;
                            if (!MayRun()) { cts.Cancel(); st.Phase = "paused (you are working)"; }
                        }
                        st.Done = pg.Done; st.Total = pg.Total;
                        if (!cts.IsCancellationRequested) { lastFull[f] = DateTime.Now; dirty.TryRemove(f, out _); st.Phase = "up to date"; st.Last = DateTime.Now; }
                    }
                }
                catch (Exception e) { Log.Warn("projects", e.Message); }
                wake.WaitOne(TimeSpan.FromMinutes(3));
            }
        }

        public static string Line()
        {
            var parts = new List<string>();
            foreach (var p in All())
            {
                var sts = p.Folders.Select(f => Status.TryGetValue(f, out var s) ? s : null).Where(s => s != null).ToList();
                if (sts.Count == 0) { parts.Add($"{p.Code} waiting"); continue; }
                if (sts.All(s => s.Phase == "up to date")) parts.Add($"{p.Code} ✓");
                else if (sts.Any(s => s.Phase == "reading")) { var s = sts.First(x => x.Phase == "reading"); parts.Add($"{p.Code} {(s.Total > 0 ? 100 * s.Done / s.Total : 0)}%"); }
                else parts.Add($"{p.Code} waiting");
            }
            return parts.Count == 0 ? "" : "📁 Projects: " + string.Join(" · ", parts);
        }
    }
}
