using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

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
    public static readonly Regex Private = new(@"wallet|password|passwd|keepass|bitwarden|\b(bank|banking|crypto|seed|passport|salary|personal)\b", RegexOptions.IgnoreCase);
    static readonly Regex PrivateFile = new(@"wallet|password|passwd|keepass|bitwarden|seed phrase|recovery phrase", RegexOptions.IgnoreCase);
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
    public static Project ForMail(MailHit h) => h == null ? null : ForText(h.Subject ?? "", h.FirstLines(1500));

    /// <summary>The project a question, a subject or a file path is about (null when none is named).</summary>
    public static Project ForText(string subj, string body = "")
    {
        subj ??= ""; body ??= "";
        var all = All();
        foreach (var p in all) if (p.Folders.Any(f => subj.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return p;   // a path
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

    // ------------------------------------------------------------------ 📇 the file catalogue: every file NAME, kept up to date (no contents)

    static readonly object dbInit = new();
    static volatile bool dbReady;

    static SqliteConnection Db()
    {
        var c = FolderIndex.Open();
        Exec(c, "PRAGMA busy_timeout=8000;");
        if (!dbReady)
            lock (dbInit)
                if (!dbReady)
                {
                    Exec(c, "CREATE TABLE IF NOT EXISTS pnames(path TEXT PRIMARY KEY, root TEXT, name TEXT, ext TEXT, size INTEGER, mtime INTEGER, seen INTEGER);");
                    Exec(c, "CREATE INDEX IF NOT EXISTS pnames_root ON pnames(root);");
                    Exec(c, "DROP TABLE IF EXISTS pread;");   // build 36's separate store: now one store for all of Nova
                    dbReady = true;
                }
        return c;
    }

    static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    static string RootKey(string root) => root.TrimEnd('\\').ToLowerInvariant();

    static bool Skip(string fullName)
    {
        var n = Path.GetFileName(fullName);
        return n.StartsWith("~$") || n.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || PrivateFile.IsMatch(n);
    }

    /// <summary>Lists every file name under a folder (minutes for 30,000 files). Private folders are never entered.</summary>
    static void ListFolder(string root, State st, CancellationToken ct)
    {
        var stamp = DateTime.Now.Ticks;
        var rk = RootKey(root);
        st.Phase = "listing"; st.Files = 0;
        using var c = Db();
        var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO pnames(path,root,name,ext,size,mtime,seen) VALUES($p,$r,$n,$e,$s,$m,$t)";
        var pp = cmd.Parameters.Add("$p", SqliteType.Text); var pr = cmd.Parameters.Add("$r", SqliteType.Text); var pn = cmd.Parameters.Add("$n", SqliteType.Text);
        var pe = cmd.Parameters.Add("$e", SqliteType.Text); var ps = cmd.Parameters.Add("$s", SqliteType.Integer); var pm = cmd.Parameters.Add("$m", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$t", stamp);
        pr.Value = rk;
        cmd.Transaction = tx;
        int batch = 0, files = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> items;
            try { items = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); } catch { continue; }
            foreach (var i in items)
            {
                if (i is DirectoryInfo d) { if (!Private.IsMatch(d.Name) && (d.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(d.FullName); continue; }
                if (i is not FileInfo f || Skip(f.FullName)) continue;
                pp.Value = f.FullName; pn.Value = f.Name; pe.Value = f.Extension.ToLowerInvariant();
                try { ps.Value = f.Length; pm.Value = f.LastWriteTime.Ticks; } catch { ps.Value = 0L; pm.Value = 0L; }
                cmd.ExecuteNonQuery();
                files++;
                if (++batch >= 3000) { tx.Commit(); tx.Dispose(); tx = c.BeginTransaction(); cmd.Transaction = tx; batch = 0; st.Files = files; }
            }
        }
        tx.Commit(); tx.Dispose();
        using (var del = c.CreateCommand())
        {
            del.CommandText = "DELETE FROM pnames WHERE root=$r AND seen<>$t";
            del.Parameters.AddWithValue("$r", rk); del.Parameters.AddWithValue("$t", stamp);
            del.ExecuteNonQuery();
        }
        st.Files = files; st.Read = CountRead(root); st.Last = DateTime.Now; st.Phase = "listed";
    }

    static void Upsert(string root, string path)
    {
        try
        {
            using var c = Db();
            if (File.Exists(path))
            {
                if (Skip(path)) return;
                var f = new FileInfo(path);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO pnames(path,root,name,ext,size,mtime,seen) VALUES($p,$r,$n,$e,$s,$m,0)";
                cmd.Parameters.AddWithValue("$p", f.FullName); cmd.Parameters.AddWithValue("$r", RootKey(root)); cmd.Parameters.AddWithValue("$n", f.Name);
                cmd.Parameters.AddWithValue("$e", f.Extension.ToLowerInvariant()); cmd.Parameters.AddWithValue("$s", f.Length); cmd.Parameters.AddWithValue("$m", f.LastWriteTime.Ticks);
                cmd.ExecuteNonQuery();
            }
            else if (!Directory.Exists(path))
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM pnames WHERE path=$p OR substr(path,1,$n)=$d";
                cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$d", path.TrimEnd('\\') + "\\"); cmd.Parameters.AddWithValue("$n", path.TrimEnd('\\').Length + 1);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception e) { Log.Warn("projects", "update " + Path.GetFileName(path) + ": " + e.Message); }
    }

    static long Count(string sql, string root)
    {
        try { using var c = Db(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("$r", RootKey(root)); return Convert.ToInt64(cmd.ExecuteScalar()); }
        catch { return 0; }
    }

    static int CountRead(string root) => FolderIndex.CountRead(root);

    /// <summary>True when the folder's file names are in the catalogue.</summary>
    public static bool Listed(string root) => root.Length > 0 && Count("SELECT COUNT(*) FROM (SELECT 1 FROM pnames WHERE root=$r LIMIT 1)", root) > 0;

    // ------------------------------------------------------------------ 📂 finding files by name

    public class FileRow
    {
        public string Path = "", Name = "", Project = "", Rev = "", Status = "";
        public long Size; public DateTime Modified; public double Score;
    }

    static string Stem(string w) => w.Length > 7 && Regex.IsMatch(w, @"^\p{L}+$") ? w[..7] : w;

    static double KindScore(string ext) => ext switch
    {
        ".pdf" => 2, ".docx" or ".doc" or ".docm" or ".rtf" => 2, ".xlsx" or ".xls" or ".xlsm" or ".csv" => 1.5, ".pptx" or ".ppt" => 1, ".msg" or ".eml" or ".txt" => 1,
        ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".bmp" => -1.5, ".zip" or ".rar" or ".7z" => -2, _ when CadExt.Contains(ext) => -6, _ => 0
    };

    static string ProjectOf(string path, List<Project> all)
    {
        foreach (var p in all) if (p.Folders.Any(f => path.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return p.Code;
        return General.Any(f => path.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) ? "General" : "";
    }

    /// <summary>
    /// Files whose path holds the words: all of them (finder) or any of them (questions). Ranked by where the words are (file name first,
    /// folder names next), the kind of file (PDF and Word first, CAD last), and revision (older revisions marked superseded).
    /// </summary>
    /// <summary>SQL: the path is inside one of these folders (a project folder or any subfolder of it).</summary>
    static string Under(SqliteCommand cmd, List<string> roots)
    {
        var parts = new List<string>();
        for (int i = 0; i < roots.Count; i++)
        {
            var pre = roots[i].TrimEnd('\\') + "\\";
            parts.Add($"substr(lower(path),1,$un{i})=$up{i}");
            cmd.Parameters.AddWithValue("$un" + i, pre.Length);
            cmd.Parameters.AddWithValue("$up" + i, pre.ToLowerInvariant());
        }
        return parts.Count == 0 ? "0" : string.Join(" OR ", parts);
    }

    /// <summary>Every listed file under a folder (for the revision map of any feature).</summary>
    public static List<(string Path, long Ticks)> NamesUnder(string folder)
    {
        var res = new List<(string, long)>();
        if (string.IsNullOrEmpty(folder) || GDrive.IsRoot(folder)) return res;
        try
        {
            using var c = Db();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT path,mtime FROM pnames WHERE {Under(cmd, new List<string> { RootKey(folder) })} LIMIT 200000";
            using var r = cmd.ExecuteReader();
            while (r.Read()) res.Add((r.GetString(0), r.IsDBNull(1) ? 0 : r.GetInt64(1)));
        }
        catch { }
        return res;
    }

    /// <summary>True when the folder (or the project folder it is in) has its file names listed.</summary>
    public static bool Covers(string folder) =>
        !string.IsNullOrEmpty(folder) && All().SelectMany(p => p.Folders).Concat(General)
            .Any(f => (folder.TrimEnd('\\') + "\\").StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && Listed(f));

    /// <summary>The project a folder (or file) belongs to.</summary>
    public static Project OfFolder(string path) =>
        string.IsNullOrEmpty(path) ? null : All().FirstOrDefault(p => p.Folders.Any(f => (path.TrimEnd('\\') + "\\").StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));

    public static List<FileRow> FindFiles(string query, IEnumerable<string> folders, bool all, bool latestOnly, int max)
    {
        var words = Regex.Split((query ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}\-\.]+").Select(w => w.Trim('.', '-')).Where(w => w.Length >= 2).Distinct().Take(10).ToList();
        var roots = folders.Where(f => f.Length > 0).Select(RootKey).Distinct().ToList();
        var res = new List<FileRow>();
        var boosts = Memory.Boosts(words);
        if (words.Count == 0 || roots.Count == 0) return res;
        var stems = words.Select(Stem).ToList();
        var rows = new List<(string Path, string Name, long Size, long M)>();
        try
        {
            using var c = Db();
            using var cmd = c.CreateCommand();
            var cond = string.Join(all ? " AND " : " OR ", stems.Select((_, i) => "path LIKE $w" + i));
            cmd.CommandText = $"SELECT path,name,size,mtime FROM pnames WHERE ({Under(cmd, roots)}) AND ({cond}) LIMIT 6000";
            for (int i = 0; i < stems.Count; i++) cmd.Parameters.AddWithValue("$w" + i, "%" + stems[i] + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? 0 : r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt64(3)));
        }
        catch (Exception e) { Log.Warn("projects", "find: " + e.Message); }
        var projects = All();
        var recent = DateTime.Now.AddDays(-180).Ticks;
        foreach (var x in rows)
        {
            var name = x.Name.ToLowerInvariant();
            var dir = (System.IO.Path.GetDirectoryName(x.Path) ?? "").ToLowerInvariant();
            double s = 0;
            for (int i = 0; i < words.Count; i++)
            {
                if (name.Contains(words[i])) s += 3 + Math.Min(words[i].Length, 12) / 6.0;
                else if (name.Contains(stems[i])) s += 2.5;
                else if (dir.Contains(stems[i])) s += 1;
            }
            var ext = System.IO.Path.GetExtension(x.Path).ToLowerInvariant();
            s += KindScore(ext) + (x.M > recent ? 0.5 : 0) + (boosts.TryGetValue(x.Path, out var bo) ? Math.Min(6, bo) : 0);   // 🧠 files you confirmed before
            res.Add(new FileRow { Path = x.Path, Name = x.Name, Size = x.Size, Modified = x.M > 0 ? new DateTime(x.M) : DateTime.MinValue, Score = s, Project = ProjectOf(x.Path, projects) });
        }
        // Revisions: within one document (same number, same kind of file), only the latest is current.
        foreach (var g in res.GroupBy(f => (Revisions.Parse(f.Path, 0).Key, System.IO.Path.GetExtension(f.Path).ToLowerInvariant())))
        {
            var docs = g.Select(f => (f, d: Revisions.Parse(f.Path, f.Modified.Ticks))).ToList();
            foreach (var (f, d) in docs) f.Rev = d.Rev;
            if (docs.Count < 2) { if (docs[0].d.Rank.Item1 < 0) docs[0].f.Status = "in an old / superseded folder"; continue; }
            var best = docs.OrderByDescending(x => x.d.Rank.Item1).ThenByDescending(x => x.d.Rank.Item2, StringComparer.Ordinal).ThenByDescending(x => x.d.MTime).First();
            foreach (var (f, d) in docs) { if (f == best.f) f.Status = "latest"; else { f.Status = "superseded"; f.Score -= 4; } }
        }
        return res.Where(f => !latestOnly || f.Status != "superseded").OrderByDescending(f => f.Score).ThenByDescending(f => f.Modified).Take(max).ToList();
    }

    // ------------------------------------------------------------------ 📐 a drawing / document number → its files

    public class DocHit { public string Path = "", Rev = "", Status = ""; public int Rank; public bool Cad; }

    /// <summary>Files whose name contains the number, best first: PDF, then Word/Excel, CAD last; latest revision first.</summary>
    public static List<DocHit> FindDoc(string number, IEnumerable<string> folders)
    {
        var res = new List<DocHit>();
        var num = Revisions.Number(number) is { Length: > 0 } nn ? nn : number.Trim();
        if (num.Length < 4) return res;
        var roots = folders.Where(f => f.Length > 0).ToList();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var c = Db();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT path FROM pnames WHERE ({Under(cmd, roots.Select(RootKey).ToList())}) AND name LIKE $q LIMIT 80";
            cmd.Parameters.AddWithValue("$q", "%" + num + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read()) paths.Add(r.GetString(0));
        }
        catch { }
        // A folder not listed yet: look on the disk by name.
        if (paths.Count == 0)
            foreach (var root in roots.Where(f => Directory.Exists(f) && !Covers(f)))
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*" + num + "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(40))
                        if (!Private.IsMatch(f)) paths.Add(f);
                }
                catch { }
        foreach (var p in paths)
        {
            var x = System.IO.Path.GetExtension(p).ToLowerInvariant();
            var d = Revisions.Parse(p, 0);
            res.Add(new DocHit { Path = p, Rev = d.Rev, Cad = CadExt.Contains(x), Rank = x == ".pdf" ? 0 : x is ".docx" or ".doc" ? 1 : OfficeExt.Contains(x) ? 2 : CadExt.Contains(x) ? 9 : 5 });
        }
        var ordered = res.OrderBy(h => h.Rank).ThenByDescending(h => Revisions.Parse(h.Path, 0).Rank.Item1).ThenByDescending(h => Revisions.Parse(h.Path, 0).Rank.Item2, StringComparer.Ordinal)
                         .ThenByDescending(h => File.Exists(h.Path) ? File.GetLastWriteTime(h.Path) : DateTime.MinValue).ToList();
        var first = ordered.FirstOrDefault(h => !h.Cad);
        foreach (var h in ordered) h.Status = h == first ? "latest" : h.Cad ? "CAD (not used for answers)" : h.Rank == first?.Rank ? "superseded" : "";
        return ordered;
    }

    public static readonly Regex DocNumber = new(@"\b[A-Z][A-Z0-9]{1,6}(?:-[A-Z0-9]{1,8}){3,10}\b");

    // ------------------------------------------------------------------ 📖 reading inside a file only when it is needed (remembered after)

    /// <summary>The text of a file: from memory when it was read before and hasn't changed, otherwise read now (OCR included) and remembered.</summary>
    public static (List<Seg> Segs, bool Remembered) Read(string path, CancellationToken ct)
    {
        // One store for all Nova: read once by any feature = known by all.
        var known = FolderIndex.Known(path);
        if (known != null) return (known, true);
        List<Seg> got = null; Exception err = null;
        var t = new Thread(() => { try { got = FileText.Extract(path); } catch (Exception e) { err = e; } }) { IsBackground = true, Name = "Nova read" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        while (!t.Join(200)) ct.ThrowIfCancellationRequested();
        if (err != null) throw err;
        got = (got ?? new()).Where(s => (s.Text ?? "").Trim().Length > 0).Take(800).ToList();
        FolderIndex.Remember(path, got);
        foreach (var st in Status.Values.Where(s => path.StartsWith(s.Folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) st.Read++;
        return (got, false);
    }

    public class Passage { public string Path = "", Loc = "", Goto = "", Text = "", Status = ""; public double Score; }

    /// <summary>
    /// A question about the project's documents: the likely files are found by NAME in a second, only those are opened
    /// (from memory when read before), and their best passages are returned.
    /// </summary>
    public static List<Passage> Ask(IEnumerable<string> folders, List<string> words, int maxFiles, int maxPassages, Action<string> status, CancellationToken ct)
    {
        var w = words.Where(x => x.Length >= 3 && !Regex.IsMatch(x, "^(the|and|for|with|this|that|from|please|email|what|which|about|check|find|send|give|nova)$")).Distinct().Take(10).ToList();
        var files = FindFiles(string.Join(" ", w), folders, false, true, maxFiles * 3).Where(f => f.Score >= 3 && !CadExt.Contains(System.IO.Path.GetExtension(f.Path).ToLowerInvariant())).Take(maxFiles).ToList();
        var res = new List<Passage>();
        var stems = w.Select(Stem).ToList();
        int i = 0, mem = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            status?.Invoke($"📂 {files.Count} likely file(s) found by name · reading {i} of {files.Count}: {f.Name}");
            List<Seg> segs;
            try { var (s, r) = Read(f.Path, ct); segs = s; if (r) mem++; }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            var scored = segs.Select(s => (s, sc: stems.Count(x => s.Text.Contains(x, StringComparison.OrdinalIgnoreCase)) + (s.Text.Length > 60 ? 0.1 : 0))).Where(x => x.sc >= 1).OrderByDescending(x => x.sc).Take(3).ToList();
            if (scored.Count == 0) scored = segs.Take(1).Select(s => (s, sc: 0.5)).ToList();   // the name matched: its first page still helps
            foreach (var (s, sc) in scored) res.Add(new Passage { Path = f.Path, Loc = s.Loc, Goto = s.Goto, Text = s.Text, Status = f.Status, Score = sc + f.Score / 4 });
        }
        status?.Invoke($"📂 {files.Count} file(s) used · {mem} from memory, {files.Count - mem} read now");
        return res.OrderByDescending(p => p.Score).Take(maxPassages).ToList();
    }

    // ------------------------------------------------------------------ keeping the catalogue up to date

    public class State { public string Folder = "", Phase = "waiting"; public int Files, Read; public DateTime Last; }

    public static readonly ConcurrentDictionary<string, State> Status = new(StringComparer.OrdinalIgnoreCase);

    public static class Indexer
    {
        static Thread thread;
        static readonly AutoResetEvent wake = new(false);
        static readonly ConcurrentQueue<(string Root, string Path)> changes = new();
        static readonly ConcurrentDictionary<string, bool> relist = new(StringComparer.OrdinalIgnoreCase);
        static readonly List<FileSystemWatcher> watchers = new();
        public static bool On { get => Store.Bool("win_proj_index", true); set { Store.SetBool("win_proj_index", value); Wake(); } }

        public static void Start()
        {
            if (thread != null) return;
            thread = new Thread(Loop) { IsBackground = true, Name = "Nova projects", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        public static void Wake() => wake.Set();

        /// <summary>⟳ List again now (all projects).</summary>
        public static void Refresh() { foreach (var f in AllFolders()) relist[f] = true; Wake(); }

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
                        var w = new FileSystemWatcher(f) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
                        var ff = f;
                        w.Created += (_, e) => Changed(ff, e.FullPath);
                        w.Changed += (_, e) => Changed(ff, e.FullPath);
                        w.Deleted += (_, e) => Changed(ff, e.FullPath);
                        w.Renamed += (_, e) => { Changed(ff, e.OldFullPath); Changed(ff, e.FullPath); };
                        w.Error += (_, _) => { relist[ff] = true; Wake(); };   // too many changes at once: list the folder again
                        w.EnableRaisingEvents = true;
                        watchers.Add(w);
                    }
                    catch (Exception e) { Log.Warn("projects", "watch " + f + ": " + e.Message); }
            }
        }

        static void Changed(string root, string path)
        {
            if (Private.IsMatch(path)) return;
            if (Directory.Exists(path)) { relist[root] = true; return; }   // a folder added or renamed: list again
            changes.Enqueue((root, path));
        }

        static void Loop()
        {
            Thread.Sleep(TimeSpan.FromSeconds(20));
            string watched = "";
            while (true)
            {
                try
                {
                    var folders = On ? AllFolders().ToList() : new List<string>();
                    var key = string.Join("|", folders);
                    if (key != watched) { Watch(); watched = key; }
                    // Single files added, changed, renamed or deleted: updated in a second.
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while (changes.TryDequeue(out var ch)) if (seen.Add(ch.Path)) Upsert(ch.Root, ch.Path);
                    foreach (var f in folders)
                    {
                        var st = Status.GetOrAdd(f, x => new State { Folder = x });
                        var due = st.Last == DateTime.MinValue || DateTime.Now - st.Last > TimeSpan.FromHours(20) || relist.TryRemove(f, out _);
                        if (!due) continue;
                        if (!Directory.Exists(f)) { st.Phase = "⚠ not reachable"; continue; }
                        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                        try { ListFolder(f, st, cts.Token); }
                        catch (Exception e) { st.Phase = "⚠ " + e.Message; Log.Warn("projects", "list " + f + ": " + e.Message); }
                    }
                }
                catch (Exception e) { Log.Warn("projects", e.Message); }
                wake.WaitOne(TimeSpan.FromSeconds(changes.IsEmpty ? 60 : 3));
            }
        }

        public static string Line()
        {
            var parts = new List<string>();
            foreach (var p in All())
            {
                var sts = p.Folders.Select(f => Status.TryGetValue(f, out var s) ? s : null).Where(s => s != null).ToList();
                if (sts.Count == 0) parts.Add($"{p.Code} waiting");
                else if (sts.Any(s => s.Phase == "listing")) parts.Add($"{p.Code} listing {sts.Sum(s => s.Files):N0}…");
                else if (sts.All(s => s.Phase == "listed")) parts.Add($"{p.Code} ✓ {sts.Sum(s => s.Files):N0} files · {sts.Sum(s => s.Read)} read");
                else parts.Add($"{p.Code} {sts[0].Phase}");
            }
            return parts.Count == 0 ? "" : "📁 " + string.Join("   ·   ", parts);
        }
    }
}
