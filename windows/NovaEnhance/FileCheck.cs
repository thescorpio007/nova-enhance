using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>The kind of a file, in words (Type column).</summary>
static class FileType
{
    public static string Of(string path) => Path.GetExtension(path ?? "").ToLowerInvariant() switch
    {
        ".pdf" => "PDF",
        ".doc" or ".docx" or ".docm" or ".rtf" or ".odt" => "Word",
        ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".csv" or ".ods" => "Excel",
        ".ppt" or ".pptx" or ".pptm" or ".odp" => "PowerPoint",
        ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".bmp" or ".gif" or ".webp" or ".heic" => "Image",
        ".msg" or ".eml" => "Email",
        ".txt" or ".log" or ".md" or ".xml" or ".json" or ".html" or ".htm" => "Text",
        ".zip" or ".rar" or ".7z" => "Zip",
        ".dwg" or ".dxf" or ".dgn" or ".rvt" or ".rfa" or ".rte" or ".ifc" or ".nwd" or ".nwc" or ".skp" or ".3dm" or ".pln" or ".max" => "CAD / BIM",
        "" => "",
        var x => x.TrimStart('.').ToUpperInvariant()
    };

    public static bool IsModel(string path) => Of(path) == "CAD / BIM";
}

/// <summary>
/// ✓ One file check for all of Nova (search results, preview, open, show in folder, sources of answers, chat history,
/// My Work, Nova Memory): before a file is shown or opened, Nova makes sure it is really there. A folder that was renamed
/// or moved (e.g. "Sea La vie" → "1-Sea La vie") is followed, and Nova's own records are moved with it, so nothing is
/// read twice. A folder name Windows can't open (ending with a space or a dot) is opened through its exact path.
/// When a file is really gone, the message says which folder level is wrong. Designed by A.HAMROUNI
/// </summary>
static class FileCheck
{
    static readonly ConcurrentDictionary<string, string> movedFiles = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<(string Old, string New)> movedFolders = new();

    static bool IsLocal(string p) => !string.IsNullOrEmpty(p) && (Regex.IsMatch(p, @"^[A-Za-z]:\\") || p.StartsWith(@"\\")) && !p.StartsWith(@"\\?\");

    /// <summary>True when the file is there, also when a folder name ends with a space or a dot (exact path).</summary>
    public static bool Exists(string p)
    {
        if (string.IsNullOrEmpty(p)) return false;
        try { return File.Exists(p) || (IsLocal(p) && File.Exists(Exact(p))); } catch { return false; }
    }

    /// <summary>The \\?\ form: Windows keeps every character of every folder name.</summary>
    public static string Exact(string p) => p.StartsWith(@"\\?\") ? p : p.StartsWith(@"\\") ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;

    /// <summary>Has a folder or file name that Windows apps can't open (ends with a space or a dot, or a double space).</summary>
    public static string OddName(string p)
    {
        foreach (var seg in (p ?? "").Split('\\').Skip(1))
            if (seg.Length > 0 && (seg.EndsWith(" ") || seg.EndsWith(".") || seg.StartsWith(" "))) return seg;
        return null;
    }

    /// <summary>
    /// The real place of a file: the same path when it is there; the new place when its folder was renamed or moved;
    /// null when it is gone (note says why).
    /// </summary>
    public static string Resolve(string path, out string note)
    {
        note = "";
        if (string.IsNullOrEmpty(path) || !IsLocal(path)) return path;
        try
        {
            if (File.Exists(path) || Directory.Exists(path)) return path;
            if (File.Exists(Exact(path)))
            {
                var odd = OddName(path);
                note = odd != null ? $"⚠ The folder “{odd.Trim()}” has a space or a dot at the end of its name — Windows apps can't open files inside it. Rename it (remove the space) to fix it for good." : "";
                return path;
            }
            if (movedFiles.TryGetValue(path, out var mf) && File.Exists(mf)) return mf;
            lock (movedFolders)
                foreach (var (o, n) in movedFolders)
                    if (path.StartsWith(o, StringComparison.OrdinalIgnoreCase))
                    {
                        var cand = n + path[o.Length..];
                        if (File.Exists(cand)) return cand;
                    }

            // A folder renamed or moved: follow the path level by level ("Sea La vie" → "1-Sea La vie").
            var walked = Walk(path, out var badSeg, out var badParent);
            if (walked != null) { Relink(path, walked); return walked; }

            // The same file elsewhere in your projects (same name, same size when known).
            var other = ByName(path);
            if (other != null) { Relink(path, other); return other; }

            note = badSeg != null
                ? $"Not found: the folder “{badSeg}” no longer exists in {badParent} — it was probably renamed, moved or deleted."
                : $"Not found: {Path.GetFileName(path)} is no longer in {Path.GetDirectoryName(path)}.";
            return null;
        }
        catch (Exception e) { note = "⚠ " + e.Message; return File.Exists(path) ? path : null; }
    }

    public static string Resolve(string path) => Resolve(path, out _) ?? path;

    static string Norm(string s)
    {
        s = Regex.Replace((s ?? "").Trim().Trim('.').ToLowerInvariant(), @"^\d{1,3}\s*[-_.)]\s*", "");
        return Regex.Replace(s, @"[^\p{L}\p{N}]", "");
    }

    static string Walk(string path, out string badSeg, out string badParent)
    {
        badSeg = null; badParent = null;
        var root = Path.GetPathRoot(path) ?? "";
        if (root.Length == 0 || !Directory.Exists(root)) { badSeg = root; badParent = "this PC (drive not connected?)"; return null; }
        var segs = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var cur = root;
        bool changed = false;
        for (int i = 0; i < segs.Length; i++)
        {
            var last = i == segs.Length - 1;
            var direct = Path.Combine(cur, segs[i]);
            if (last ? File.Exists(direct) : Directory.Exists(direct)) { cur = direct; continue; }
            string found = null;
            try
            {
                var want = Norm(segs[i]);
                var names = last ? Directory.EnumerateFiles(cur) : Directory.EnumerateDirectories(cur);
                if (want.Length > 0) found = names.FirstOrDefault(n => Norm(Path.GetFileName(n)) == want);
            }
            catch { }
            if (found == null) { badSeg = segs[i]; badParent = cur; return null; }
            cur = found; changed = true;
        }
        return changed && File.Exists(cur) ? cur : null;
    }

    static string ByName(string path)
    {
        long size = -1;
        try
        {
            using var c = FolderIndex.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT size FROM files WHERE path=$p";
            cmd.Parameters.AddWithValue("$p", path);
            var v = cmd.ExecuteScalar();
            if (v != null && v != DBNull.Value) size = Convert.ToInt64(v);
        }
        catch { }
        var hits = Projects.ByFileName(Path.GetFileName(path));
        var same = hits.Where(h => size < 0 || h.Size == size).Select(h => h.Path).Where(File.Exists).ToList();
        return same.Count == 1 || (same.Count > 1 && size >= 0) ? same[0] : null;
    }

    /// <summary>Moves Nova's records (what was read, revisions) to the new place — the whole folder when a folder was renamed.</summary>
    static void Relink(string oldPath, string newPath)
    {
        movedFiles[oldPath] = newPath;
        // The deepest folder that changed: everything under it moved the same way.
        var o = oldPath.Split('\\'); var n = newPath.Split('\\');
        string oldDir = null, newDir = null;
        if (o.Length == n.Length)
        {
            int k = Enumerable.Range(0, o.Length - 1).FirstOrDefault(i => !o[i].Equals(n[i], StringComparison.OrdinalIgnoreCase), -1);
            if (k >= 0)
            {
                oldDir = string.Join("\\", o.Take(k + 1)) + "\\";
                newDir = string.Join("\\", n.Take(k + 1)) + "\\";
                lock (movedFolders) if (!movedFolders.Any(x => x.Old.Equals(oldDir, StringComparison.OrdinalIgnoreCase))) movedFolders.Add((oldDir, newDir));
            }
        }
        var od = oldDir; var nd = newDir;
        Task.Run(() => FolderIndex.Relink(oldPath, newPath, od, nd));
    }
}
