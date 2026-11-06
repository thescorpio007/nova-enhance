using System.IO.Compression;
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
    /// <summary>Google Drive file: "id|mimeType" (opened in the browser).</summary>
    public string Remote;
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
                try { Exec(c, "ALTER TABLE files ADD COLUMN remote TEXT;"); } catch { }
                Exec(c, "CREATE TABLE IF NOT EXISTS vision(path TEXT, page INTEGER, PRIMARY KEY(path, page));");
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
        public int Total, Done, Read, Files, Unclear;
        public string Current = "", Phase = "";
        public bool Finished;
        public double Seconds;
    }

    static long Ms(DateTime d) => new DateTimeOffset(d.ToUniversalTime()).ToUnixTimeMilliseconds();

    /// <summary>One file of a folder on this PC or in Google Drive.</summary>
    public class Item
    {
        public string Key, Name, Ext, Remote, RemoteMime, RemoteId;
        public long Size, Mtime;
        public string LocalPath;   // files on this PC
    }

    /// <summary>All files of a folder: on this PC, or (root "gdrive:…") in Google Drive.</summary>
    public static IEnumerable<Item> Items(string root, Progress pg, CancellationToken ct)
    {
        if (GDrive.IsRoot(root))
        {
            var (id, name) = GDrive.ParseRoot(root);
            var queue = new Queue<(string Id, string Rel)>();
            queue.Enqueue((id, ""));
            while (queue.Count > 0 && !ct.IsCancellationRequested)
            {
                var (fid, rel) = queue.Dequeue();
                pg.Phase = "Listing Google Drive… " + (rel.Length > 0 ? rel : name);
                List<DriveFile> kids;
                try { kids = GDrive.Children(fid, ct).GetAwaiter().GetResult(); }
                catch (Exception e) { Log.Error("drive list", e); continue; }
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in kids)
                {
                    var safe = string.Join("_", (k.Name ?? "").Split(Path.GetInvalidFileNameChars()));
                    if (!used.Add(safe)) safe += $" ({k.Id[..Math.Min(6, k.Id.Length)]})";
                    if (k.IsFolder) { queue.Enqueue((k.Id, rel.Length == 0 ? safe : rel + "\\" + safe)); continue; }
                    var ext = GDrive.ReadExt(k);
                    yield return new Item
                    {
                        Key = root + "\\" + (rel.Length == 0 ? "" : rel + "\\") + safe, Name = safe, Ext = ext.Length > 0 ? ext : Path.GetExtension(safe).ToLowerInvariant(),
                        Size = k.Size, Mtime = k.Modified.ToUnixTimeMilliseconds(), Remote = k.Id + "|" + k.Mime, RemoteId = k.Id, RemoteMime = k.Mime
                    };
                }
            }
            yield break;
        }
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, ReturnSpecialDirectories = false };
        pg.Phase = "Listing files…";
        foreach (var f in Directory.EnumerateFiles(root, "*", opts))
        {
            var n = Path.GetFileName(f);
            if (n.StartsWith("~$")) continue;
            FileInfo fi;
            try { fi = new FileInfo(f); } catch { continue; }
            yield return new Item { Key = f, Name = n, Ext = fi.Extension.ToLowerInvariant(), Size = fi.Length, Mtime = Ms(fi.LastWriteTime), LocalPath = f };
        }
    }

    public static string NormalRoot(string root)
    {
        if (GDrive.IsRoot(root)) return root;
        root = Path.GetFullPath(root).TrimEnd('\\');
        if (root.Length == 2 && root[1] == ':') root += "\\";
        return root;
    }

    /// <summary>
    /// Search a folder: first instant results from the index, then new and changed files are read (and remembered), results
    /// arriving as they are found. With m == null it only brings the index up to date (used by ✦ Ask AI).
    /// Runs on a background STA thread (Office, Outlook and OCR need it).
    /// </summary>
    public static void Search(string root, Matcher m, HashSet<string> kinds, Action<FileHit> onHit, Action<string> onRemove, Progress pg, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        root = NormalRoot(root);
        var pre = Prefix(root);
        using var c = Open();

        pg.Phase = "Searching what Nova already knows…";
        var known = new Dictionary<string, (long Id, long Size, long Mtime)>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id,path,size,mtime FROM files WHERE lower(substr(path,1,$n))=lower($p)";
            cmd.Parameters.AddWithValue("$n", pre.Length); cmd.Parameters.AddWithValue("$p", pre);
            using var r = cmd.ExecuteReader();
            while (r.Read()) known[r.GetString(1)] = (r.GetInt64(0), r.GetInt64(2), r.GetInt64(3));
        }
        if (known.Count > 0 && m != null) FromIndex(c, pre, m, kinds, onHit, ct);

        List<Item> files;
        try { files = Items(root, pg, ct).ToList(); }
        catch (Exception e) { pg.Phase = "⚠ " + e.Message; pg.Finished = true; return; }
        pg.Total = files.Count;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tx = c.BeginTransaction();
        int batch = 0;
        long maxBytes = (long)MaxMb * 1024 * 1024;
        pg.Phase = GDrive.IsRoot(root) ? "Reading files from Google Drive…" : "Reading files…";
        try
        {
            foreach (var it in files)
            {
                if (ct.IsCancellationRequested) break;
                pg.Done++;
                seen.Add(it.Key);
                if (known.TryGetValue(it.Key, out var k) && k.Size == it.Size && k.Mtime == it.Mtime) continue;
                pg.Current = it.Name;
                var segs = new List<Seg> { new("file name", it.Name) };
                var note = "";
                string local = it.LocalPath, temp = null;
                try
                {
                    bool content = FileKinds.ReadContent(it.Ext);
                    if (content && it.Size > maxBytes) { note = $"skipped: larger than {MaxMb} MB"; content = false; }
                    if (it.RemoteId != null && (content || FileKinds.On("J")))
                    {
                        if (it.Ext.Length > 0 && (content || it.Ext is ".docx" or ".pptx" or ".xlsx" or ".pdf"))
                        {
                            temp = Path.Combine(Paths.Cache, Guid.NewGuid().ToString("N") + it.Ext);
                            try { GDrive.Download(it.RemoteId, it.RemoteMime, temp, ct).GetAwaiter().GetResult(); local = temp; }
                            catch (Exception e) { note = "download failed: " + e.Message; local = null; }
                        }
                    }
                    if (local != null && FileKinds.On("J"))
                    {
                        var props = it.Ext is ".docx" or ".docm" or ".pptx" or ".pptm" or ".xlsx" or ".xlsm" ? FileText.OfficeProps(local)
                            : it.Ext == ".pdf" ? FileText.PdfProps(local)
                            : !content && it.RemoteId == null ? ShellProps.Get(local) : "";
                        if (props.Length > 0) segs.Add(new Seg("file properties", props));
                    }
                    if (content && local != null)
                    {
                        segs.AddRange(FileText.Extract(local));
                        pg.Read++;
                    }
                }
                finally { if (temp != null) try { File.Delete(temp); } catch { } }

                if (k.Id > 0) { DeleteFile(c, k.Id); onRemove?.Invoke(it.Key); }
                Exec(c, "INSERT INTO files(path,size,mtime,kind,note,remote) VALUES($p,$s,$m,$k,$n,$r)",
                    ("$p", it.Key), ("$s", it.Size), ("$m", it.Mtime), ("$k", FileKinds.KindOf(it.Ext)), ("$n", note), ("$r", it.Remote));
                var fid = Convert.ToInt64(Scalar(c, "SELECT last_insert_rowid()"));
                foreach (var sg in segs)
                {
                    Exec(c, "INSERT INTO segs(file_id,loc,go,text) VALUES($f,$l,$g,$t)", ("$f", fid), ("$l", sg.Loc), ("$g", sg.Goto), ("$t", sg.Text));
                    if (Fts) Exec(c, "INSERT INTO segs_fts(rowid,text) VALUES(last_insert_rowid(),$t)", ("$t", sg.Text));
                    if (sg.Unclear && sg.OcrPage >= 0)
                    {
                        Exec(c, "INSERT OR IGNORE INTO vision(path,page) VALUES($p,$g)", ("$p", it.Key), ("$g", sg.OcrPage));
                        pg.Unclear++;
                    }
                }
                if (m != null)
                {
                    var hit = Match(it.Key, root, FileKinds.KindOf(it.Ext), segs, m, kinds);
                    if (hit != null) { hit.Remote = it.Remote; onHit(hit); }
                }
                if (++batch >= 15) { tx.Commit(); tx.Dispose(); tx = c.BeginTransaction(); batch = 0; }
            }
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

    // ================================================================== Gemini vision for unclear scanned pages

    public static List<(string Path, int Page)> VisionPending(string root)
    {
        var res = new List<(string, int)>();
        var pre = Prefix(NormalRoot(root));
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path,page FROM vision WHERE lower(substr(path,1,$n))=lower($p) ORDER BY path,page LIMIT 500";
        cmd.Parameters.AddWithValue("$n", pre.Length); cmd.Parameters.AddWithValue("$p", pre);
        using var r = cmd.ExecuteReader();
        while (r.Read()) res.Add((r.GetString(0), r.GetInt32(1)));
        return res;
    }

    public static void VisionClear(string root)
    {
        var pre = Prefix(NormalRoot(root));
        using var c = Open();
        Exec(c, "DELETE FROM vision WHERE lower(substr(path,1,$n))=lower($p)", ("$n", pre.Length), ("$p", pre));
    }

    /// <summary>Sends the given pages to Gemini vision (the user agreed) and replaces their OCR text in the index.</summary>
    public static int VisionRun(List<(string Path, int Page)> pages, Action<string> progress, CancellationToken ct)
    {
        int done = 0;
        using var c = Open();
        foreach (var g in pages.GroupBy(p => p.Path))
        {
            if (ct.IsCancellationRequested) break;
            string local = g.Key, temp = null;
            try
            {
                var remote = Scalar(c, "SELECT remote FROM files WHERE path=$p", ("$p", g.Key)) as string;
                if (!string.IsNullOrEmpty(remote))
                {
                    var parts = remote.Split('|');
                    temp = Path.Combine(Paths.Cache, Guid.NewGuid().ToString("N") + Path.GetExtension(g.Key));
                    GDrive.Download(parts[0], parts.Length > 1 ? parts[1] : "", temp, ct).GetAwaiter().GetResult();
                    local = temp;
                }
                var fid = Scalar(c, "SELECT id FROM files WHERE path=$p", ("$p", g.Key));
                foreach (var (_, page) in g)
                {
                    if (ct.IsCancellationRequested) break;
                    progress?.Invoke($"Gemini vision · {Path.GetFileName(g.Key)}{(page > 0 ? " · page " + page : "")} ({done + 1}/{pages.Count})");
                    try
                    {
                        var (data, mime) = Ocr.Picture(local, page);
                        var text = Ai.Vision(data, mime, ct).GetAwaiter().GetResult();
                        var oldLoc = page > 0 ? $"page {page} (OCR)" : "image text (OCR)";
                        var newLoc = page > 0 ? $"page {page} (Gemini vision)" : "image text (Gemini vision)";
                        var go = page > 0 ? $"page:{page}" : "";
                        if (fid != null)
                        {
                            using (var cmd = c.CreateCommand())
                            {
                                cmd.CommandText = "SELECT id,text FROM segs WHERE file_id=$f AND loc=$l";
                                cmd.Parameters.AddWithValue("$f", fid); cmd.Parameters.AddWithValue("$l", oldLoc);
                                var rows = new List<(long, string)>();
                                using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1)));
                                foreach (var (sid, t) in rows)
                                {
                                    if (Fts) Exec(c, "INSERT INTO segs_fts(segs_fts,rowid,text) VALUES('delete',$id,$t)", ("$id", sid), ("$t", t));
                                    Exec(c, "DELETE FROM segs WHERE id=$id", ("$id", sid));
                                }
                            }
                            Exec(c, "INSERT INTO segs(file_id,loc,go,text) VALUES($f,$l,$g,$t)", ("$f", fid), ("$l", newLoc), ("$g", go), ("$t", text));
                            if (Fts) Exec(c, "INSERT INTO segs_fts(rowid,text) VALUES(last_insert_rowid(),$t)", ("$t", text));
                        }
                        Exec(c, "DELETE FROM vision WHERE path=$p AND page=$g", ("$p", g.Key), ("$g", page));
                        done++;
                    }
                    catch (Exception e) { Log.Error("vision " + Path.GetFileName(g.Key), e); progress?.Invoke("⚠ " + e.Message); if (e is AiException) return done; }
                }
            }
            catch (Exception e) { Log.Error("vision file", e); }
            finally { if (temp != null) try { File.Delete(temp); } catch { } }
        }
        return done;
    }

    // ================================================================== ✦ Ask AI: best passages for a question

    public class Passage
    {
        public string Path, Kind, Remote, Loc, Goto, Text, Root;
        public double Score;
    }

    /// <summary>The most relevant pieces of the folder for these words (full-text ranking), at most 4 per file.</summary>
    public static List<Passage> Retrieve(string root, List<string> words, int max = 14)
    {
        root = NormalRoot(root);
        var pre = Prefix(root);
        var terms = words.Select(w => Regex.Replace(w ?? "", @"[^\p{L}\p{N}\s\-\.]", " ").Trim()).Where(w => w.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
        if (terms.Count == 0) return new();
        var res = new List<Passage>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (Fts)
        {
            var q = string.Join(" OR ", terms.Select(t =>
            {
                var toks = Regex.Matches(t, @"[\p{L}\p{N}]+").Select(x => x.Value).ToList();
                return toks.Count == 0 ? null : "\"" + string.Join(" ", toks) + "\"";
            }).Where(x => x != null).Distinct());
            cmd.CommandText = "SELECT f.path,f.kind,f.remote,s.loc,s.go,s.text,bm25(segs_fts) AS sc FROM segs_fts JOIN segs s ON s.id=segs_fts.rowid JOIN files f ON f.id=s.file_id " +
                              "WHERE segs_fts MATCH $q AND lower(substr(f.path,1,$n))=lower($p) ORDER BY sc LIMIT 300";
            cmd.Parameters.AddWithValue("$q", q);
        }
        else
        {
            cmd.CommandText = "SELECT f.path,f.kind,f.remote,s.loc,s.go,s.text,0 FROM segs s JOIN files f ON f.id=s.file_id WHERE lower(substr(f.path,1,$n))=lower($p) AND (" +
                              string.Join(" OR ", terms.Select((t, i) => $"s.text LIKE $w{i}")) + ") LIMIT 300";
            for (int i = 0; i < terms.Count; i++) cmd.Parameters.AddWithValue("$w" + i, "%" + terms[i] + "%");
        }
        cmd.Parameters.AddWithValue("$n", pre.Length); cmd.Parameters.AddWithValue("$p", pre);
        var rows = new List<Passage>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                rows.Add(new Passage
                {
                    Path = r.GetString(0), Kind = r.IsDBNull(1) ? "J" : r.GetString(1), Remote = r.IsDBNull(2) ? null : r.GetString(2),
                    Loc = r.GetString(3), Goto = r.IsDBNull(4) ? "" : r.GetString(4), Text = r.IsDBNull(5) ? "" : r.GetString(5), Score = r.GetDouble(6), Root = root
                });
        // Prefer pieces that contain more of the different words; at most 4 per file.
        foreach (var p in rows) p.Score -= terms.Count(t => p.Text.Contains(t, StringComparison.OrdinalIgnoreCase)) * 2.0;
        var perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in rows.Where(p => p.Loc != "file name").OrderBy(p => p.Score))
        {
            perFile.TryGetValue(p.Path, out var n);
            if (n >= 4) continue;
            perFile[p.Path] = n + 1;
            p.Text = Window(p.Text, terms, 750);
            res.Add(p);
            if (res.Count >= max) break;
        }
        return res;
    }

    static string Window(string text, List<string> terms, int size)
    {
        if (text.Length <= size) return text;
        var first = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var a = Math.Max(0, first - size / 3);
        var b = Math.Min(text.Length, a + size);
        return (a > 0 ? "…" : "") + text[a..b] + (b < text.Length ? "…" : "");
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
        cmd.CommandText = "SELECT f.path,f.kind,s.loc,s.go,s.text,f.remote FROM segs s JOIN files f ON f.id=s.file_id WHERE lower(substr(f.path,1,$n))=lower($p)"
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
            cur ??= new FileHit { Path = path, Root = pre.TrimEnd('\\'), Kind = kind, Remote = r.IsDBNull(5) ? null : r.GetString(5) };
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
        if (p.Goto.StartsWith("zip:") && p.Goto.Contains('|'))
        {
            var entry = p.Goto[4..p.Goto.IndexOf('|')];
            var inner = new PieceHit { Loc = p.Loc, Goto = p.Goto[(p.Goto.IndexOf('|') + 1)..], Text = p.Text, Matches = p.Matches };
            var w = Where(inner);
            return w == p.Loc ? p.Loc : $"› {entry} · {w}";
        }
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
        if (!string.IsNullOrEmpty(h.Remote))
        {
            var parts = h.Remote.Split('|');
            Shell.Open(GDrive.WebLink(parts[0], parts.Length > 1 ? parts[1] : ""));
            return;
        }
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
