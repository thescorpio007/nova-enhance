using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NovaEnhance;

/// <summary>Exact phrase · all words · any word, with optional match case and whole word.</summary>
class Matcher
{
    public readonly string Query, Mode;
    readonly List<Regex> rxs = new();
    public readonly List<string> Tokens;

    public Matcher(string q, string mode, bool matchCase, bool wholeWord)
    {
        Query = (q ?? "").Trim().Trim('"');
        Mode = mode;
        var opts = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        string Wrap(string core) => wholeWord ? $@"(?<![\p{{L}}\p{{N}}]){core}(?![\p{{L}}\p{{N}}])" : core;
        string Flex(string s) => Regex.Replace(Regex.Escape(s), @"(\\ |\\s|\\t)+", @"\s+");
        if (mode == "phrase" || Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 1)
            rxs.Add(new Regex(Wrap(Flex(Query)), opts));
        else
            foreach (var w in Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct())
                rxs.Add(new Regex(Wrap(Regex.Escape(w)), opts));
        Tokens = Regex.Matches(Query, @"[\p{L}\p{N}]+").Select(m => m.Value).ToList();
    }

    /// <summary>The places in the text that match, or none.</summary>
    public List<(int Index, int Length)> Find(string text)
    {
        var res = new List<(int, int)>();
        if (string.IsNullOrEmpty(text) || Query.Length == 0) return res;
        if (Mode == "all" && rxs.Count > 1)
        {
            foreach (var r in rxs) { var m = r.Match(text); if (!m.Success) return new(); }
        }
        foreach (var r in rxs)
            foreach (Match m in r.Matches(text)) { res.Add((m.Index, m.Length)); if (res.Count > 50) break; }
        return res.OrderBy(x => x.Item1).ToList();
    }

    /// <summary>Full-text query used to pick candidate pieces quickly; the regex then decides.</summary>
    public string Fts()
    {
        if (Tokens.Count == 0) return null;
        var q = Tokens.Select(t => "\"" + t + "\"").ToList();
        if (Mode == "phrase") return "\"" + string.Join(" ", Tokens) + "\"";
        return string.Join(Mode == "any" ? " OR " : " AND ", q);
    }
}

class FileHit
{
    public string Path, Root, Kind;
    public List<PieceHit> Pieces = new();
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

class PieceHit
{
    public string Loc, Goto, Text;
    public List<(int Index, int Length)> Matches;
}

/// <summary>
/// Remembered folders: the text of their files is kept in an encrypted index (Documents\Nova Enhance\Folder Index),
/// so the next search in the same folder is instant and only new or changed files are read again.
/// </summary>
static class FolderIndex
{
    static readonly object InitLock = new();
    static bool inited, batteries;
    public static bool Fts { get; private set; } = true;
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nova Enhance folder index");

    public static bool Local { get => Store.Bool("win_fs_local", false); set => Store.SetBool("win_fs_local", value); }
    public static int MaxMb { get => Math.Max(1, Store.Int("win_fs_maxmb", 200)); set => Store.SetInt("win_fs_maxmb", value); }
    public static bool Menu { get => Store.Bool("win_fs_menu", true); set => Store.SetBool("win_fs_menu", value); }

    static string Dir
    {
        get
        {
            var p = Local
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova Enhance", "Folder Index")
                : Path.Combine(Paths.Root, "Folder Index");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    public static string DirPath => Dir;

    static string KeyHex()
    {
        var f = Path.Combine(Dir, "key.bin");
        byte[] key;
        if (File.Exists(f)) key = ProtectedData.Unprotect(File.ReadAllBytes(f), Entropy, DataProtectionScope.CurrentUser);
        else { key = RandomNumberGenerator.GetBytes(32); File.WriteAllBytes(f, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser)); }
        return Convert.ToHexString(key);
    }

    public static SqliteConnection Open()
    {
        lock (InitLock) { if (!batteries) { SQLitePCL.Batteries_V2.Init(); batteries = true; } }
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Dir, "folders.db"), Password = KeyHex(), Pooling = false }.ToString());
        c.Open();
        Exec(c, "PRAGMA busy_timeout=8000;");
        lock (InitLock)
        {
            if (!inited)
            {
                Exec(c, "PRAGMA journal_mode=WAL;");
                Exec(c, "CREATE TABLE IF NOT EXISTS files(id INTEGER PRIMARY KEY, path TEXT UNIQUE, size INTEGER, mtime INTEGER, kind TEXT, note TEXT);");
                Exec(c, "CREATE TABLE IF NOT EXISTS segs(id INTEGER PRIMARY KEY, file_id INTEGER, loc TEXT, go TEXT, text TEXT);");
                Exec(c, "CREATE INDEX IF NOT EXISTS segs_file ON segs(file_id);");
                Exec(c, "CREATE TABLE IF NOT EXISTS roots(path TEXT PRIMARY KEY, last INTEGER, files INTEGER);");
                try
                {
                    Exec(c, "CREATE VIRTUAL TABLE IF NOT EXISTS segs_fts USING fts5(text, content='segs', content_rowid='id', tokenize='unicode61 remove_diacritics 2');");
                    Fts = true;
                }
                catch { Fts = false; }
                inited = true;
            }
        }
        return c;
    }

    static void Exec(SqliteConnection c, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static object Scalar(SqliteConnection c, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    public static List<(string Path, DateTime Last, long Files)> Roots()
    {
        var res = new List<(string, DateTime, long)>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path,last,files FROM roots ORDER BY last DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read()) res.Add((r.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).LocalDateTime, r.GetInt64(2)));
        }
        catch (Exception e) { Log.Error("folder roots", e); }
        return res;
    }

    static string Prefix(string root) => root.TrimEnd('\\') + "\\";

    /// <summary>Forgets a folder: its files leave the index (unless another remembered folder holds them).</summary>
    public static void Forget(string root)
    {
        using var c = Open();
        var pre = Prefix(root);
        var others = Roots().Select(r => r.Path).Where(p => !p.Equals(root, StringComparison.OrdinalIgnoreCase)).Select(Prefix).ToList();
        using var tx = c.BeginTransaction();
        var ids = new List<(long, string)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id,path FROM files WHERE lower(substr(path,1,$n))=lower($p)";
            cmd.Parameters.AddWithValue("$n", pre.Length); cmd.Parameters.AddWithValue("$p", pre);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add((r.GetInt64(0), r.GetString(1)));
        }
        foreach (var (id, path) in ids)
            if (!others.Any(o => path.StartsWith(o, StringComparison.OrdinalIgnoreCase))) DeleteFile(c, id);
        Exec(c, "DELETE FROM roots WHERE path=$p", ("$p", root));
        tx.Commit();
    }

    public static void ForgetAll()
    {
        lock (InitLock) inited = false;
        foreach (var f in new[] { "folders.db", "folders.db-wal", "folders.db-shm" })
            try { var p = Path.Combine(Dir, f); if (File.Exists(p)) File.Delete(p); } catch (Exception e) { Log.Error("forget all", e); }
    }

    public static long SizeMb()
    {
        try { return Directory.GetFiles(Dir, "folders.db*").Sum(f => new FileInfo(f).Length) / (1024 * 1024); } catch { return 0; }
    }

    static void DeleteFile(SqliteConnection c, long id)
    {
        if (Fts)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id,text FROM segs WHERE file_id=$f";
            cmd.Parameters.AddWithValue("$f", id);
            var rows = new List<(long, string)>();
            using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1)));
            foreach (var (sid, text) in rows)
                Exec(c, "INSERT INTO segs_fts(segs_fts,rowid,text) VALUES('delete',$id,$t)", ("$id", sid), ("$t", text));
        }
        Exec(c, "DELETE FROM segs WHERE file_id=$f", ("$f", id));
        Exec(c, "DELETE FROM files WHERE id=$f", ("$f", id));
    }

    public class Progress
    {
        public int Total, Done, Read, Files;
        public string Current = "", Phase = "";
        public bool Finished;
        public double Seconds;
    }

    static long Ms(DateTime d) => new DateTimeOffset(d.ToUniversalTime()).ToUnixTimeMilliseconds();

    /// <summary>
    /// Search a folder: first instant results from the index, then new and changed files are read (and remembered), results
    /// arriving as they are found. Runs on a background thread (Office and OCR need it).
    /// </summary>
    public static void Search(string root, Matcher m, HashSet<string> kinds, Action<FileHit> onHit, Action<string> onRemove, Progress pg, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        root = Path.GetFullPath(root).TrimEnd('\\');
        if (root.Length == 2 && root[1] == ':') root += "\\";
        var pre = Prefix(root);
        using var c = Open();

        // 1 · instant results from what is already remembered
        pg.Phase = "Searching what Nova already knows…";
        var known = new Dictionary<string, (long Id, long Size, long Mtime)>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id,path,size,mtime FROM files WHERE lower(substr(path,1,$n))=lower($p)";
            cmd.Parameters.AddWithValue("$n", pre.Length); cmd.Parameters.AddWithValue("$p", pre);
            using var r = cmd.ExecuteReader();
            while (r.Read()) known[r.GetString(1)] = (r.GetInt64(0), r.GetInt64(2), r.GetInt64(3));
        }
        if (known.Count > 0) FromIndex(c, pre, m, kinds, onHit, ct);

        // 2 · read new and changed files
        pg.Phase = "Listing files…";
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, ReturnSpecialDirectories = false };
        List<string> files;
        try { files = Directory.EnumerateFiles(root, "*", opts).Where(f => !Path.GetFileName(f).StartsWith("~$")).ToList(); }
        catch (Exception e) { pg.Phase = "⚠ " + e.Message; pg.Finished = true; return; }
        pg.Total = files.Count;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tx = c.BeginTransaction();
        int batch = 0;
        long maxBytes = (long)MaxMb * 1024 * 1024;
        pg.Phase = "Reading files…";
        try
        {
            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) break;
                pg.Done++;
                seen.Add(f);
                FileInfo fi;
                try { fi = new FileInfo(f); } catch { continue; }
                var mt = Ms(fi.LastWriteTime);
                if (known.TryGetValue(f, out var k) && k.Size == fi.Length && k.Mtime == mt) continue;
                pg.Current = fi.Name;
                var ext = fi.Extension.ToLowerInvariant();
                var segs = new List<Seg> { new("file name", fi.Name) };
                var note = "";
                if (FileKinds.On("J"))
                {
                    var props = ext is ".docx" or ".docm" or ".pptx" or ".pptm" or ".xlsx" or ".xlsm" ? FileText.OfficeProps(f)
                        : ext == ".pdf" ? FileText.PdfProps(f)
                        : !FileKinds.ReadContent(ext) ? ShellProps.Get(f) : "";
                    if (props.Length > 0) segs.Add(new Seg("file properties", props));
                }
                if (FileKinds.ReadContent(ext))
                {
                    if (fi.Length > maxBytes) note = $"skipped: larger than {MaxMb} MB";
                    else
                    {
                        var content = FileText.Extract(f);
                        segs.AddRange(content);
                        pg.Read++;
                    }
                }
                if (k.Id > 0) { DeleteFile(c, k.Id); onRemove?.Invoke(f); }
                Exec(c, "INSERT INTO files(path,size,mtime,kind,note) VALUES($p,$s,$m,$k,$n)", ("$p", f), ("$s", fi.Length), ("$m", mt), ("$k", FileKinds.KindOf(ext)), ("$n", note));
                var fid = Convert.ToInt64(Scalar(c, "SELECT last_insert_rowid()"));
                foreach (var s in segs)
                {
                    Exec(c, "INSERT INTO segs(file_id,loc,go,text) VALUES($f,$l,$g,$t)", ("$f", fid), ("$l", s.Loc), ("$g", s.Goto), ("$t", s.Text));
                    if (Fts) Exec(c, "INSERT INTO segs_fts(rowid,text) VALUES(last_insert_rowid(),$t)", ("$t", s.Text));
                }
                var hit = Match(f, root, FileKinds.KindOf(ext), segs, m, kinds);
                if (hit != null) onHit(hit);
                if (++batch >= 15) { tx.Commit(); tx.Dispose(); tx = c.BeginTransaction(); batch = 0; }
            }
            // 3 · files that were deleted from the folder leave the index
            if (!ct.IsCancellationRequested)
                foreach (var (path, k) in known)
                    if (!seen.Contains(path)) { DeleteFile(c, k.Id); onRemove?.Invoke(path); }
            Exec(c, "INSERT INTO roots(path,last,files) VALUES($p,$l,$f) ON CONFLICT(path) DO UPDATE SET last=$l, files=$f",
                ("$p", root), ("$l", DateTimeOffset.Now.ToUnixTimeMilliseconds()), ("$f", files.Count));
            tx.Commit(); tx.Dispose();
        }
        finally
        {
            try { OfficeConvert.CloseAll(); } catch { }
            pg.Files = files.Count;
            pg.Seconds = sw.Elapsed.TotalSeconds;
            pg.Finished = true;
        }
    }

    static bool KindOk(HashSet<string> kinds, string kind) => kinds == null || kinds.Count == 0 || kinds.Contains(kind);

    static FileHit Match(string path, string root, string kind, List<Seg> segs, Matcher m, HashSet<string> kinds)
    {
        if (!KindOk(kinds, kind)) return null;
        FileHit hit = null;
        foreach (var s in segs)
        {
            var found = m.Find(s.Text);
            if (found.Count == 0) continue;
            hit ??= new FileHit { Path = path, Root = root, Kind = kind };
            hit.Pieces.Add(new PieceHit { Loc = s.Loc, Goto = s.Goto, Text = s.Text, Matches = found });
            if (hit.Pieces.Count >= 200) break;
        }
        return hit;
    }

    static void FromIndex(SqliteConnection c, string pre, Matcher m, HashSet<string> kinds, Action<FileHit> onHit, CancellationToken ct)
    {
        var fts = Fts ? m.Fts() : null;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT f.path,f.kind,s.loc,s.go,s.text FROM segs s JOIN files f ON f.id=s.file_id WHERE lower(substr(f.path,1,$n))=lower($p)"
                          + (fts != null ? " AND s.id IN (SELECT rowid FROM segs_fts WHERE segs_fts MATCH $q)" : " AND s.text LIKE $like")
                          + " ORDER BY f.path, s.id LIMIT 20000";
        cmd.Parameters.AddWithValue("$n", pre.Length); cmd.Parameters.AddWithValue("$p", pre);
        if (fts != null) cmd.Parameters.AddWithValue("$q", fts);
        else cmd.Parameters.AddWithValue("$like", "%" + (m.Tokens.FirstOrDefault() ?? m.Query) + "%");
        using var r = cmd.ExecuteReader();
        FileHit cur = null;
        while (r.Read() && !ct.IsCancellationRequested)
        {
            var path = r.GetString(0);
            if (cur != null && !cur.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) { if (cur.Pieces.Count > 0) onHit(cur); cur = null; }
            var kind = r.IsDBNull(1) ? "J" : r.GetString(1);
            if (!KindOk(kinds, kind)) continue;
            var text = r.IsDBNull(4) ? "" : r.GetString(4);
            var found = m.Find(text);
            if (found.Count == 0) continue;
            cur ??= new FileHit { Path = path, Root = pre.TrimEnd('\\'), Kind = kind };
            if (cur.Pieces.Count < 200) cur.Pieces.Add(new PieceHit { Loc = r.GetString(2), Goto = r.IsDBNull(3) ? "" : r.GetString(3), Text = text, Matches = found });
        }
        if (cur != null && cur.Pieces.Count > 0) onHit(cur);
    }

    public static string Export(string title, IEnumerable<FileHit> hits)
    {
        var dir = Path.Combine(Paths.Root, "Search Results");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm");
        var csv = Path.Combine(dir, $"{History.SafeName(title, 40)} {stamp}.csv");
        string C(string s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        var sb = new StringBuilder("File,Folder,Where,Preview\r\n");
        var hb = new StringBuilder($"<!doctype html><meta charset=utf-8><title>{System.Net.WebUtility.HtmlEncode(title)}</title><style>body{{font-family:Segoe UI,Arial;margin:24px}}td,th{{border-bottom:1px solid #ddd;padding:6px 8px;text-align:left;font-size:13px;vertical-align:top}}th{{background:#f3f1ff}}mark{{background:#ffe27a}}</style><h2>{System.Net.WebUtility.HtmlEncode(title)}</h2><p>{DateTime.Now:dddd dd MMMM yyyy HH:mm} · {Program.Sign}</p><table><tr><th>File</th><th>Where</th><th>Preview</th></tr>");
        foreach (var h in hits)
            foreach (var p in h.Pieces)
            {
                var (pre, mid, post) = Preview(p, 90);
                sb.Append(string.Join(",", C(h.Name), C(h.Folder), C(Where(p)), C(pre + mid + post))).Append("\r\n");
                hb.Append($"<tr><td><b>{Enc(h.Name)}</b><br><small>{Enc(h.Folder)}</small></td><td>{Enc(Where(p))}</td><td>{Enc(pre)}<mark>{Enc(mid)}</mark>{Enc(post)}</td></tr>");
            }
        File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.ChangeExtension(csv, ".html"), hb.Append("</table>").ToString(), new UTF8Encoding(false));
        return csv;
    }

    static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    /// <summary>"page 12", "slide 4", "Sheet "BOQ" · D45", "line 120"…</summary>
    public static string Where(PieceHit p)
    {
        var first = p.Matches.Count > 0 ? p.Matches[0].Index : 0;
        if (p.Goto.StartsWith("cell:"))
        {
            var cell = FileText.CellAt(p.Text, first);
            var sheet = p.Goto[5..p.Goto.LastIndexOf('!')];
            return cell.Length > 0 ? $"Sheet \"{sheet}\" · {cell}" : p.Loc;
        }
        if (p.Goto.StartsWith("line:"))
        {
            var line = FileText.LineAt(new Seg(p.Loc, p.Text, p.Goto), first);
            return line > 0 ? $"line {line}" : p.Loc;
        }
        return p.Loc;
    }

    /// <summary>Text before, the match, and text after — for the small preview.</summary>
    public static (string Pre, string Mid, string Post) Preview(PieceHit p, int around)
    {
        if (p.Matches.Count == 0) return (p.Text.Length > around * 2 ? p.Text[..(around * 2)] + "…" : p.Text, "", "");
        var (i, len) = p.Matches[0];
        int a = Math.Max(0, i - around), b = Math.Min(p.Text.Length, i + len + around);
        string Flat(string s) => Regex.Replace(s, @"\s+", " ");
        return ((a > 0 ? "…" : "") + Flat(p.Text[a..i]), Flat(p.Text.Substring(i, len)), Flat(p.Text[(i + len)..b]) + (b < p.Text.Length ? "…" : ""));
    }
}

/// <summary>"🔍 Search inside with Nova" when right-clicking a folder in Explorer (per user, no admin).</summary>
static class ExplorerMenu
{
    static readonly string[] Keys =
    {
        @"Software\Classes\Directory\shell\NovaEnhance.Search",
        @"Software\Classes\Directory\Background\shell\NovaEnhance.Search",
        @"Software\Classes\Drive\shell\NovaEnhance.Search"
    };

    public static void Apply()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            for (int i = 0; i < Keys.Length; i++)
            {
                if (!FolderIndex.Menu) { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Keys[i], false); continue; }
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Keys[i]);
                k.SetValue("MUIVerb", "Search inside with Nova");
                k.SetValue("Icon", "\"" + exe + "\",0");
                using var cmd = k.CreateSubKey("command");
                cmd.SetValue("", $"\"{exe}\" --search \"{(i == 1 ? "%V" : "%1")}\"");
            }
        }
        catch (Exception e) { Log.Error("explorer menu", e); }
    }
}

/// <summary>Opens a result where it was found: PDF page, Word page, PowerPoint slide, Excel cell (when the program allows).</summary>
static class Opener
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int str, string assoc, string extra, StringBuilder outStr, ref uint outLen);

    static string ExeFor(string ext)
    {
        try { uint n = 1024; var sb = new StringBuilder(1024); return AssocQueryString(0, 2, ext, null, sb, ref n) == 0 ? sb.ToString() : ""; }
        catch { return ""; }
    }

    public static void Open(FileHit h, PieceHit p, string term)
    {
        var path = h.Path;
        var go = p?.Goto ?? "";
        try
        {
            if (go.StartsWith("zip:"))
            {
                var rest = go[4..];
                var entry = rest.Split('|')[0];
                go = rest.Contains('|') ? rest[(rest.IndexOf('|') + 1)..] : "";
                path = ExtractFromZip(path, entry);
                if (path == null) { Shell.Open(h.Path); return; }
            }
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (go.StartsWith("page:") && int.TryParse(go[5..], out var page))
            {
                if (ext == ".pdf") { OpenPdf(path, page); return; }
                if (ext is ".docx" or ".docm") { if (Word(path, page, term)) return; }
            }
            if (go.StartsWith("slide:") && int.TryParse(go[6..], out var slide) && ext is ".pptx" or ".pptm" or ".ppt")
                if (PowerPoint(path, slide)) return;
            if (go.StartsWith("cell:") && p != null && ext is ".xlsx" or ".xlsm" or ".xls")
            {
                var sheet = go[5..go.LastIndexOf('!')];
                var cell = FileText.CellAt(p.Text, p.Matches.Count > 0 ? p.Matches[0].Index : 0);
                if (Excel(path, sheet, cell.Length > 0 ? cell : go[(go.LastIndexOf('!') + 1)..])) return;
            }
            if (ext is ".doc" or ".rtf" or ".odt" or ".docx" or ".docm" && Word(path, 0, term)) return;
            Shell.Open(path);
        }
        catch (Exception e) { Log.Error("open result", e); Shell.Open(path); }
    }

    static string ExtractFromZip(string zip, string entry)
    {
        try
        {
            using var z = System.IO.Compression.ZipFile.OpenRead(zip);
            var e = z.GetEntry(entry);
            if (e == null) return null;
            var dir = Path.Combine(Paths.Cache, "opened", History.SafeName(Path.GetFileNameWithoutExtension(zip)));
            Directory.CreateDirectory(dir);
            var dst = Path.Combine(dir, Path.GetFileName(entry));
            e.ExtractToFile(dst, true);
            return dst;
        }
        catch { return null; }
    }

    static void OpenPdf(string path, int page)
    {
        var exe = ExeFor(".pdf");
        var low = exe.ToLowerInvariant();
        if (low.Contains("acro")) { Process.Start(exe, $"/A \"page={page}\" \"{path}\""); return; }
        if (low.Contains("msedge") || low.Contains("chrome") || low.Contains("brave"))
        {
            Process.Start(exe, "\"file:///" + path.Replace('\\', '/').Replace(" ", "%20").Replace("#", "%23") + "#page=" + page + "\"");
            return;
        }
        if (low.Contains("sumatra")) { Process.Start(exe, $"-page {page} \"{path}\""); return; }
        if (low.Contains("foxit")) { Process.Start(exe, $"\"{path}\" /A page={page}"); return; }
        Shell.Open(path);
    }

    static dynamic Office(string progId)
    {
        var t = Type.GetTypeFromProgID(progId);
        if (t == null) return null;
        return Activator.CreateInstance(t);
    }

    static bool Word(string path, int page, string term)
    {
        try
        {
            dynamic w = Office("Word.Application");
            if (w == null) return false;
            w.Visible = true;
            dynamic d = w.Documents.Open(path);
            if (page > 0) w.Selection.GoTo(1, 1, page);
            if (!string.IsNullOrWhiteSpace(term))
            {
                var t = term.Length > 250 ? term[..250] : term;
                dynamic f = w.Selection.Find;
                f.ClearFormatting();
                f.Execute(t);
            }
            w.Activate();
            return true;
        }
        catch (Exception e) { Log.Error("word open", e); return false; }
    }

    static bool PowerPoint(string path, int slide)
    {
        try
        {
            dynamic pp = Office("PowerPoint.Application");
            if (pp == null) return false;
            pp.Visible = -1;
            dynamic p = pp.Presentations.Open(path);
            p.Windows.Item(1).View.GotoSlide(slide);
            pp.Activate();
            return true;
        }
        catch (Exception e) { Log.Error("powerpoint open", e); return false; }
    }

    static bool Excel(string path, string sheet, string cell)
    {
        try
        {
            dynamic x = Office("Excel.Application");
            if (x == null) return false;
            x.Visible = true;
            dynamic wb = x.Workbooks.Open(path);
            dynamic sheets = wb.Worksheets;
            dynamic ws;
            try { ws = sheets[sheet]; } catch { ws = sheets.Item(sheet); }
            ws.Activate();
            dynamic rng;
            try { rng = ws.Range[cell]; } catch { rng = ws.Range(cell); }
            rng.Select();
            return true;
        }
        catch (Exception e) { Log.Error("excel open", e); return false; }
    }
}
