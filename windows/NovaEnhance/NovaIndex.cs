using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NovaEnhance;

/// <summary>
/// 🗄 The Nova index: ONE shared, growing store (inside folders.db, encrypted) that every feature reads first and writes back to —
/// the text of every file read, every file name, and now also the 📐 labels of every document, the 🔏 stamps found on it, the
/// 📜 contract register, the vendor lists row by row and the links between documents (a submittal reviewed against the contract…).
/// Worked out once, reused everywhere; redone only when the file or your 🧠 rules change. Every write waits its turn (never “locked”).
/// 🔒 private files are never stored. Designed by A.HAMROUNI
/// </summary>
static class NovaIndex
{
    // ------------------------------------------------------------------ 📐 labels (kind · source · discipline · stage · revision), cached

    static string ver;
    static NovaIndex() { Brain.Changed += () => ver = null; }

    /// <summary>The version of your filing rules: labels are worked out again only when 📐 Filing or 📁 Projects rules change.</summary>
    public static string LabelVer
    {
        get
        {
            if (ver != null) return ver;
            try
            {
                var sb = new StringBuilder("v1|");
                foreach (var (sec, r) in Brain.Rules(Brain.Snapshot()))
                {
                    var id = sec["id"]?.ToString() ?? "";
                    if (id is "filing" or "projects") sb.Append(r["id"]).Append(r["off"]).Append(r["text"]).Append('\n');
                }
                ver = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..12];
            }
            catch { ver = "v1"; }
            return ver;
        }
    }

    static string Pack(FileLabels l) => new JsonObject
    {
        ["p"] = l.Project, ["n"] = l.Number, ["s"] = l.Source, ["k"] = l.Kind, ["d"] = l.Disc, ["l"] = l.Level, ["st"] = l.Stage, ["r"] = l.Rev, ["c"] = l.Code, ["x"] = l.State,
        ["w"] = string.Join("\n", l.Why),
    }.ToJsonString();
    static FileLabels Unpack(string j)
    {
        var o = JsonNode.Parse(j) as JsonObject;
        var l = new FileLabels
        {
            Project = o?["p"]?.ToString() ?? "", Number = o?["n"]?.ToString() ?? "", Source = o?["s"]?.ToString() ?? "", Kind = o?["k"]?.ToString() ?? "", Disc = o?["d"]?.ToString() ?? "",
            Level = o?["l"]?.ToString() ?? "", Stage = o?["st"]?.ToString() ?? "", Rev = o?["r"]?.ToString() ?? "", Code = o?["c"]?.ToString() ?? "", State = o?["x"]?.ToString() ?? "",
        };
        var w = o?["w"]?.ToString() ?? "";
        if (w.Length > 0) l.Why.AddRange(w.Split('\n'));
        return l;
    }

    /// <summary>The labels of these files: from the index when the file and the rules did not change, else worked out now and saved.</summary>
    public static Dictionary<string, FileLabels> Labels(IEnumerable<string> roots, IList<Projects.FileRow> files, CancellationToken ct, Action<string> say = null)
    {
        var res = new Dictionary<string, FileLabels>(StringComparer.OrdinalIgnoreCase);
        var v = LabelVer;
        var want = files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        try
        {
            using var c = FolderIndex.Open();
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var pre = FolderIndex.Prefix(root);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT path,size,mtime,j FROM labels WHERE ver=$v AND lower(substr(path,1,$n))=lower($p)";
                cmd.Parameters.AddWithValue("$v", v); cmd.Parameters.AddWithValue("$n", pre.Length); cmd.Parameters.AddWithValue("$p", pre);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var p = r.GetString(0);
                    if (!want.TryGetValue(p, out var f) || f.Size != r.GetInt64(1) || f.Modified.Ticks != r.GetInt64(2)) continue;
                    try { res[p] = Unpack(r.GetString(3)); } catch { }
                }
            }
        }
        catch (Exception e) { Log.Warn("Nova index", "labels: " + e.Message); }
        var missing = want.Values.Where(f => !res.ContainsKey(f.Path)).ToList();
        if (missing.Count == 0) return res;
        var fresh = new List<(Projects.FileRow F, FileLabels L)>();
        int n = 0;
        foreach (var f in missing)
        {
            ct.ThrowIfCancellationRequested();
            if (++n % 2000 == 0) say?.Invoke($"📐 labelling {n:N0} of {missing.Count:N0} new or changed files (saved for next time)…");
            FileLabels l;
            try { l = Filing.Classify(f.Path); } catch { l = new FileLabels(); }
            res[f.Path] = l; fresh.Add((f, l));
        }
        Save(fresh, v);
        return res;
    }

    /// <summary>One file's labels (cached when the index has them for this size and date).</summary>
    public static FileLabels Label(Projects.FileRow f)
    {
        try
        {
            using var c = FolderIndex.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT j FROM labels WHERE path=$p AND ver=$v AND size=$s AND mtime=$m";
            cmd.Parameters.AddWithValue("$p", f.Path); cmd.Parameters.AddWithValue("$v", LabelVer); cmd.Parameters.AddWithValue("$s", f.Size); cmd.Parameters.AddWithValue("$m", f.Modified.Ticks);
            if (cmd.ExecuteScalar() is string j) return Unpack(j);
        }
        catch { }
        var l = Filing.Classify(f.Path);
        Save(new() { (f, l) }, LabelVer);
        return l;
    }

    static void Save(List<(Projects.FileRow F, FileLabels L)> rows, string v)
    {
        if (rows.Count == 0) return;
        Task.Run(() =>
        {
            try
            {
                using var c = FolderIndex.Open();
                foreach (var chunk in rows.Chunk(1500))
                {
                    using var tx = FolderIndex.BeginWait(c, CancellationToken.None);
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO labels(path,size,mtime,ver,j) VALUES($p,$s,$m,$v,$j)";
                    var pp = cmd.Parameters.Add("$p", SqliteType.Text); var ps = cmd.Parameters.Add("$s", SqliteType.Integer); var pm = cmd.Parameters.Add("$m", SqliteType.Integer);
                    var pj = cmd.Parameters.Add("$j", SqliteType.Text); cmd.Parameters.AddWithValue("$v", v);
                    foreach (var (f, l) in chunk)
                    {
                        if (Projects.Private.IsMatch(f.Path)) continue;
                        pp.Value = f.Path; ps.Value = f.Size; pm.Value = f.Modified.Ticks; pj.Value = Pack(l); cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
            catch (Exception e) { Log.Warn("Nova index", "save labels: " + e.Message); }
        });
    }

    // ------------------------------------------------------------------ 🔏 stamps: the pictures pasted on the pages, by fingerprint

    /// <summary>The fingerprints of the pictures on page 1 and the last page of a PDF (the stamps are the same picture file every time).</summary>
    public static List<string> Stamps(string path, long size, long mtime, bool compute = true)
    {
        try
        {
            using (var c = FolderIndex.Open())
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT stamps FROM evid WHERE path=$p AND size=$s AND mtime=$m";
                cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$s", size); cmd.Parameters.AddWithValue("$m", mtime);
                if (cmd.ExecuteScalar() is string st) return st.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            if (!compute || Projects.Private.IsMatch(path)) return null;
            var (hashes, pages) = ReadPictures(path, null);
            using (var c = FolderIndex.Open())
            using (var tx = FolderIndex.BeginWait(c, CancellationToken.None))
            {
                FolderIndex.Exec(c, "INSERT OR REPLACE INTO evid(path,size,mtime,stamps,pages,at) VALUES($p,$s,$m,$t,$n,$a)",
                    ("$p", path), ("$s", size), ("$m", mtime), ("$t", string.Join(",", hashes.Keys)), ("$n", pages), ("$a", DateTimeOffset.Now.ToUnixTimeSeconds()));
                tx.Commit();
            }
            return hashes.Keys.ToList();
        }
        catch { return null; }
    }

    /// <summary>The pictures of page 1 and the last page: fingerprint → (width, height, a PNG of it when asked).</summary>
    public static (Dictionary<string, (int W, int H, byte[] Png)> Hashes, int Pages) ReadPictures(string path, HashSet<string> wantPng)
    {
        var res = new Dictionary<string, (int, int, byte[])>();
        int pages = 0;
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        pages = doc.NumberOfPages;
        foreach (var pn in new[] { 1, pages }.Distinct().Where(p => p >= 1))
        {
            UglyToad.PdfPig.Content.Page page;
            try { page = doc.GetPage(pn); } catch { continue; }
            foreach (var img in page.GetImages())
            {
                try
                {
                    var raw = img.RawBytes.ToArray();
                    if (raw.Length < 400) continue;   // tiny bullets / lines
                    var h = Convert.ToHexString(SHA1.HashData(raw))[..16];
                    if (res.ContainsKey(h)) continue;
                    byte[] png = null;
                    if (wantPng == null || wantPng.Contains(h)) { if (!img.TryGetPng(out png)) png = IsJpeg(raw) ? raw : null; }
                    res[h] = (img.WidthInSamples, img.HeightInSamples, wantPng == null ? null : png);
                }
                catch { }
            }
        }
        return (res, pages);
    }
    static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8;

    /// <summary>The pictures that come back on many contract documents — the stamps to name once (Contract document · Client · Contractor · Consultant).</summary>
    public static List<(string Hash, int Docs, int W, int H, byte[] Png, string Sample)> FindStamps(IEnumerable<string> pdfs, Action<string> say, CancellationToken ct, int max = 300)
    {
        var count = new Dictionary<string, (int N, int W, int H, string Sample)>();
        int n = 0;
        var list = pdfs.Where(p => !Projects.Private.IsMatch(p)).Take(max).ToList();
        foreach (var p in list)
        {
            ct.ThrowIfCancellationRequested();
            say?.Invoke($"🔏 looking at the pictures of {++n} of {list.Count} documents…");
            try
            {
                var (h, _) = ReadPictures(p, new HashSet<string>());
                foreach (var (k, v) in h) count[k] = count.TryGetValue(k, out var o) ? (o.N + 1, o.W, o.H, o.Sample) : (1, v.W, v.H, p);
            }
            catch { }
        }
        var res = new List<(string, int, int, int, byte[], string)>();
        foreach (var (k, v) in count.Where(x => x.Value.N >= Math.Min(3, Math.Max(1, list.Count / 2))).OrderByDescending(x => x.Value.N).Take(16))
        {
            byte[] png = null;
            try { var (h, _) = ReadPictures(v.Sample, new HashSet<string> { k }); png = h.TryGetValue(k, out var x) ? x.Png : null; } catch { }
            res.Add((k, v.N, v.W, v.H, png, v.Sample));
        }
        return res;
    }

    // ------------------------------------------------------------------ 📋 vendor lists, row by row

    static readonly Regex DivRx = new(@"^\s*DIVISION\s+(\d+)", RegexOptions.IgnoreCase);
    static readonly Regex ListRx = new(@"LIST OF (?:ACCEPTABLE |APPROVED )?(?:VENDORS|MANUFACTURERS|SUPPLIERS)[^·]*?FOR\s+(.+)$", RegexOptions.IgnoreCase);
    static readonly Regex HeadRx = new(@"^\s*(\d{1,3})\s*(?:·\s*)?([A-Z][A-Z0-9 &/\-\(\),\.']{2,80})\s*$");
    static readonly Regex Skip = new(@"S\.?\s?NO|Brand\s*(?:/|Name)|CONTACT DETAILS|Country of Origin|Local agent|E-?mail Address|VENDOR NAME|^\s*ITEM\s|Page \d+ of \d+|^\s*\d+\s*$", RegexOptions.IgnoreCase);

    /// <summary>The rows of a vendor list (division / product / the row as written) — read from the PDF's own text (Excel-made lists need no OCR).</summary>
    public static List<(int Page, string Division, string Product, string Row)> ParseVendors(string path)
    {
        var rows = new List<(int, string, string, string)>();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        string div = "", prod = "";
        for (int pn = 1; pn <= doc.NumberOfPages; pn++)
        {
            List<UglyToad.PdfPig.Content.Word> words;
            try { words = doc.GetPage(pn).GetWords().ToList(); } catch { continue; }
            // lines: words at the same height, left to right; a wide gap = a new cell
            var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
            foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
            {
                var line = lines.LastOrDefault();
                if (line != null && Math.Abs(line[0].BoundingBox.Bottom - w.BoundingBox.Bottom) < 3.5) line.Add(w); else lines.Add(new() { w });
            }
            foreach (var line in lines)
            {
                var sb = new StringBuilder(); double right = -1;
                foreach (var w in line.OrderBy(w => w.BoundingBox.Left))
                {
                    if (right >= 0) sb.Append(w.BoundingBox.Left - right > 9 ? " · " : " ");
                    sb.Append(w.Text); right = w.BoundingBox.Right;
                }
                var t = sb.ToString().Trim();
                if (t.Length < 3) continue;
                var dm = DivRx.Match(t); if (dm.Success) { div = "Division " + dm.Groups[1].Value; continue; }
                var lm = ListRx.Match(t); if (lm.Success) { div = Regex.Replace(lm.Groups[1].Value, @"\s+", " ").Trim().TrimEnd('.'); prod = ""; continue; }
                if (Skip.IsMatch(t)) continue;
                var hm = HeadRx.Match(t.Replace(" · ", " "));
                if (hm.Success && !t.Any(char.IsLower) && t.Count(ch => ch == '·') == 0) { prod = hm.Groups[2].Value.Trim(); continue; }
                if (!Regex.IsMatch(t, @"^\d{1,3}\b") || t.Count(ch => ch == '·') < 1) continue;   // a data row starts with its number and has cells
                var cells = t.Split(" · ").Select(x => x.Trim()).ToList();
                // lists like the MEP one: “1 · Filtration Pump · PAPF · USA · Pentair …” — the product is the 2nd cell when no heading is set
                var p = prod;
                if (cells.Count >= 3 && Regex.IsMatch(cells[0], @"^\d{1,3}$") && cells[1].Any(char.IsLetter) && (prod.Length == 0 || div.Length > 0 && !div.StartsWith("Division"))) p = cells[1];
                rows.Add((pn, div, p, t));
            }
        }
        return rows;
    }

    /// <summary>Keeps the rows of a vendor list in the index (again only when the file changed).</summary>
    public static int IndexVendors(string project, RegDoc d)
    {
        try
        {
            using var c = FolderIndex.Open();
            if (FolderIndex.Scalar(c, "SELECT rows FROM vendsrc WHERE path=$p AND size=$s AND mtime=$m", ("$p", d.Path), ("$s", d.Size), ("$m", d.Date.Ticks)) is long known) return (int)known;
            var rows = d.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? ParseVendors(d.Path) : new();
            using var tx = FolderIndex.BeginWait(c, CancellationToken.None);
            FolderIndex.Exec(c, "DELETE FROM vendors WHERE path=$p", ("$p", d.Path));
            foreach (var r in rows)
                FolderIndex.Exec(c, "INSERT INTO vendors(project,path,page,division,product,row) VALUES($j,$p,$g,$d,$r,$w)", ("$j", project), ("$p", d.Path), ("$g", r.Page), ("$d", r.Division), ("$r", r.Product), ("$w", r.Row));
            FolderIndex.Exec(c, "INSERT OR REPLACE INTO vendsrc(path,size,mtime,rows) VALUES($p,$s,$m,$n)", ("$p", d.Path), ("$s", d.Size), ("$m", d.Date.Ticks), ("$n", rows.Count));
            tx.Commit();
            return rows.Count;
        }
        catch (Exception e) { Log.Warn("Nova index", "vendor list " + Path.GetFileName(d.Path) + ": " + e.Message); return 0; }
    }

    public class VendorRow { public string Path = "", Division = "", Product = "", Row = ""; public int Page; }

    public static List<VendorRow> Vendors(string project, IEnumerable<string> governingPaths = null, string filter = "", int max = 5000)
    {
        var res = new List<VendorRow>();
        var keep = governingPaths == null ? null : new HashSet<string>(governingPaths, StringComparer.OrdinalIgnoreCase);
        try
        {
            using var c = FolderIndex.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path,page,division,product,row FROM vendors WHERE project=$j" + (filter.Length > 0 ? " AND (row LIKE $f OR product LIKE $f OR division LIKE $f)" : "") + " ORDER BY id";
            cmd.Parameters.AddWithValue("$j", project);
            if (filter.Length > 0) cmd.Parameters.AddWithValue("$f", "%" + filter + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read() && res.Count < max)
            {
                var p = r.GetString(0);
                if (keep != null && !keep.Contains(p)) continue;
                res.Add(new VendorRow { Path = p, Page = r.GetInt32(1), Division = r.IsDBNull(2) ? "" : r.GetString(2), Product = r.IsDBNull(3) ? "" : r.GetString(3), Row = r.IsDBNull(4) ? "" : r.GetString(4) });
            }
        }
        catch { }
        return res;
    }

    // ------------------------------------------------------------------ 🔗 links between documents

    public static void Link(string a, string b, string kind, string note = "")
    {
        if (Projects.Private.IsMatch(a + " " + b)) return;
        Task.Run(() =>
        {
            try
            {
                using var c = FolderIndex.Open();
                using var tx = FolderIndex.BeginWait(c, CancellationToken.None);
                FolderIndex.Exec(c, "INSERT OR REPLACE INTO links(a,b,kind,note,at) VALUES($a,$b,$k,$n,$t)", ("$a", a), ("$b", b), ("$k", kind), ("$n", note), ("$t", DateTimeOffset.Now.ToUnixTimeSeconds()));
                tx.Commit();
            }
            catch { }
        });
    }

    public static List<(string Other, string Kind, string Note, DateTime At)> Links(string path)
    {
        var res = new List<(string, string, string, DateTime)>();
        try
        {
            using var c = FolderIndex.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT b,kind,note,at FROM links WHERE a=$p UNION ALL SELECT a,kind,note,at FROM links WHERE b=$p ORDER BY 4 DESC LIMIT 50";
            cmd.Parameters.AddWithValue("$p", path);
            using var r = cmd.ExecuteReader();
            while (r.Read()) res.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)).LocalDateTime));
        }
        catch { }
        return res;
    }

    // ------------------------------------------------------------------ 🗄 what is inside (Settings → 🗄 Nova index)

    public static List<(string Layer, long Count, string What)> Inside()
    {
        var res = new List<(string, long, string)>();
        try
        {
            using var c = FolderIndex.Open();
            long N(string sql) { try { return Convert.ToInt64(FolderIndex.Scalar(c, sql) ?? 0L); } catch { return -1; } }
            res.Add(("📇 File names", N("SELECT COUNT(*) FROM pnames"), "every file of your project folders, kept up to date"));
            res.Add(("📖 Files read inside", N("SELECT COUNT(*) FROM files"), "PDF, Word, Excel, emails, scans (OCR) — read once, used by every feature"));
            res.Add(("📄 Pages / passages", N("SELECT COUNT(*) FROM segs"), "the text, page by page, sheet by sheet"));
            res.Add(("📐 Document labels", N("SELECT COUNT(*) FROM labels"), "kind · source · discipline · stage · revision — worked out once"));
            res.Add(("🔏 Stamps read", N("SELECT COUNT(*) FROM evid"), "the pictures on page 1 / last page of PDFs (fingerprints)"));
            res.Add(("📜 Contract register", N("SELECT COUNT(*) FROM creg"), "every document of your contract areas with its type and status"));
            res.Add(("📋 Vendor rows", N("SELECT COUNT(*) FROM vendors"), "your vendor lists, row by row"));
            res.Add(("🔗 Links", N("SELECT COUNT(*) FROM links"), "submittal ↔ contract document reviewed against, and more"));
            res.Add(("🧭 Meaning search", N("SELECT COUNT(*) FROM embseg"), "passages with a meaning vector (search by meaning)"));
            res.Add(("⏭ Left out on purpose", N("SELECT COUNT(*) FROM pskip"), "older revisions and copies already read"));
        }
        catch (Exception e) { res.Add(("⚠", 0, e.Message)); }
        try
        {
            using var m = MailIndex.Open();
            res.Add(("✉ Emails (linked index)", Convert.ToInt64(FolderIndex.Scalar(m, "SELECT COUNT(*) FROM mail") ?? 0L), "your mailboxes and archives — linked to the documents by number"));
        }
        catch { }
        return res;
    }

    /// <summary>Starts one layer again from scratch (the next use works it out again and saves it).</summary>
    public static void Reset(string layer)
    {
        using var c = FolderIndex.Open();
        using var tx = FolderIndex.BeginWait(c, CancellationToken.None);
        switch (layer)
        {
            case "labels": FolderIndex.Exec(c, "DELETE FROM labels"); break;
            case "stamps": FolderIndex.Exec(c, "DELETE FROM evid"); break;
            case "vendors": FolderIndex.Exec(c, "DELETE FROM vendors"); FolderIndex.Exec(c, "DELETE FROM vendsrc"); break;
            case "register": FolderIndex.Exec(c, "DELETE FROM creg"); FolderIndex.Exec(c, "DELETE FROM cregmeta"); break;
        }
        tx.Commit();
    }

    /// <summary>Tidies the index file (WAL folded back, statistics refreshed) — safe, nothing removed.</summary>
    public static void Tidy()
    {
        using var c = FolderIndex.Open();
        FolderIndex.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        FolderIndex.Exec(c, "PRAGMA optimize;");
    }
}
