using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace NovaEnhance;

/// <summary>A piece of a file: where it is (page 12, slide 4, Sheet "BOQ" row 45…) and its text.</summary>
class Seg
{
    public string Loc = "";
    /// <summary>How to open it there: page:12 · slide:4 · cell:BOQ!D45 · line:120 · zip:entry|page:3 · "".</summary>
    public string Goto = "";
    public string Text = "";
    /// <summary>Scanned page or image read by OCR: (page, kind) for Gemini vision when the reading is unclear.</summary>
    public int OcrPage = -1;
    public bool Unclear;
    public Seg() { }
    public Seg(string loc, string text, string go = "") { Loc = loc; Text = text; Goto = go; }
}

/// <summary>Which kinds of files are read inside (Settings → Folder search). Letters match the list the user chose from.</summary>
static class FileKinds
{
    public static readonly (string Key, string Label, string[] Ext)[] All =
    {
        ("A", "Word: .docx, .doc, .rtf, .odt", new[] { ".docx", ".docm", ".doc", ".rtf", ".odt" }),
        ("B", "PowerPoint: .pptx, .ppt (slides and speaker notes)", new[] { ".pptx", ".pptm", ".ppt" }),
        ("C", "Excel: .xlsx, .xls, .csv", new[] { ".xlsx", ".xlsm", ".xls", ".csv" }),
        ("D", "PDF with text", new[] { ".pdf" }),
        ("E", "Scanned PDFs and images (.jpg, .png, .tif) — text recognition (OCR)", new[] { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" }),
        ("F", "Saved emails: .msg, .eml (with attachments)", new[] { ".msg", ".eml" }),
        ("G", "Text and data: .txt, .xml, .json, .html, .md, .log, .ini", new[] { ".txt", ".xml", ".json", ".html", ".htm", ".md", ".log", ".ini", ".cfg", ".yaml", ".yml" }),
        ("H", "CAD and BIM text: .dxf, .ifc", new[] { ".dxf", ".ifc" }),
        ("I", "Zip files (searches the files inside)", new[] { ".zip" }),
        ("K", "Google Docs, Sheets and Slides on Google Drive for desktop (.gdoc, .gsheet, .gslides) — needs the Google sign-in", new[] { ".gdoc", ".gsheet", ".gslides" }),
        ("J", "File names and properties of every file (also .dwg, .rvt)", Array.Empty<string>())
    };

    public static bool On(string key) => Store.Bool("win_fs_" + key, true);
    public static void Set(string key, bool v) => Store.SetBool("win_fs_" + key, v);

    public static string KindOf(string ext)
    {
        ext = ext.ToLowerInvariant();
        foreach (var k in All) if (k.Ext.Contains(ext)) return k.Key;
        return "J";
    }

    /// <summary>Content of this file is read (otherwise only its name and properties, if J is on).</summary>
    public static bool ReadContent(string ext)
    {
        var k = KindOf(ext);
        if (k == "J") return false;
        if (k == "D") return On("D") || On("E");
        return On(k);
    }
}

/// <summary>
/// Reads the text inside documents, on this PC only. New Office files are read directly; old .doc/.ppt/.xls/.rtf/.odt are
/// converted through the Office installed on the PC (in the background, read-only).
/// </summary>
static class FileText
{
    public static int MaxSegChars = 1200;

    public static List<Seg> Extract(string path, int depth = 0)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var segs = new List<Seg>();
        try
        {
            switch (ext)
            {
                case ".docx": case ".docm": segs.AddRange(Docx(path)); break;
                case ".pptx": case ".pptm": segs.AddRange(Pptx(path)); break;
                case ".xlsx": case ".xlsm": segs.AddRange(Xlsx(path)); break;
                case ".doc": case ".rtf": case ".odt": segs.AddRange(ViaOffice(path, "word")); break;
                case ".ppt": segs.AddRange(ViaOffice(path, "powerpoint")); break;
                case ".xls": segs.AddRange(ViaOffice(path, "excel")); break;
                case ".csv": segs.AddRange(TextFile(path, false)); break;
                case ".pdf": segs.AddRange(Pdf(path)); break;
                case ".jpg": case ".jpeg": case ".png": case ".tif": case ".tiff": case ".bmp":
                    if (!FileKinds.On("E")) break;
                    var t = Ocr.Image(path);
                    segs.Add(new Seg("image text (OCR)", Clean(t)) { OcrPage = 0, Unclear = Ocr.IsUnclear(t) });
                    break;
                case ".msg": segs.AddRange(Msg(path, depth)); break;
                case ".eml": segs.AddRange(Eml(path, depth)); break;
                case ".html": case ".htm": segs.AddRange(TextFile(path, true)); break;
                case ".txt": case ".xml": case ".json": case ".md": case ".log": case ".ini": case ".cfg": case ".yaml": case ".yml":
                    segs.AddRange(TextFile(path, false)); break;
                case ".dxf": segs.AddRange(Dxf(path)); break;
                case ".ifc": segs.AddRange(Ifc(path)); break;
                case ".zip": if (depth < 2) segs.AddRange(Zip(path, depth)); break;
                case ".gdoc": case ".gsheet": case ".gslides": segs.AddRange(GoogleShortcut(path, ext)); break;
            }
        }
        catch (Exception e) { Log.Write($"read {Path.GetFileName(path)}: {e.GetType().Name}: {e.Message}"); }
        return segs;
    }

    /// <summary>Google Drive for desktop shortcut → the real Google Doc/Sheet/Slides, exported through the Google sign-in.</summary>
    static IEnumerable<Seg> GoogleShortcut(string path, string ext)
    {
        if (!GDrive.SignedIn) return Array.Empty<Seg>();
        var id = GDrive.IdFromShortcut(File.ReadAllText(path));
        if (string.IsNullOrEmpty(id)) return Array.Empty<Seg>();
        var mime = GDrive.MimeForShortcut(ext);
        var tmp = Path.Combine(Paths.Cache, Guid.NewGuid().ToString("N") + (ext == ".gdoc" ? ".docx" : ext == ".gsheet" ? ".xlsx" : ".pptx"));
        try
        {
            GDrive.Download(id, mime, tmp).GetAwaiter().GetResult();
            return Extract(tmp).ToList();
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ------------------------------------------------------------------ helpers

    static string Clean(string s) => Regex.Replace(s ?? "", @"[ \t ]+", " ").Trim();

    /// <summary>Joins small pieces into segments of about MaxSegChars, keeping the location of the first piece.</summary>
    static IEnumerable<Seg> Chunk(IEnumerable<(string Loc, string Go, string Text)> parts)
    {
        var sb = new StringBuilder();
        string loc = null, go = null;
        foreach (var (l, g, t) in parts)
        {
            var text = Clean(t);
            if (text.Length == 0) continue;
            if (sb.Length > 0 && (l != loc || sb.Length + text.Length > MaxSegChars))
            {
                yield return new Seg(loc, sb.ToString(), go);
                sb.Clear();
            }
            if (sb.Length == 0) { loc = l; go = g; }
            else sb.Append('\n');
            sb.Append(text);
        }
        if (sb.Length > 0) yield return new Seg(loc, sb.ToString(), go);
    }

    static XmlReader Xml(Stream s) => XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, XmlResolver = null });

    /// <summary>Text of the current element; leaves the reader on its end tag (so the next Read() skips nothing).</summary>
    static string InnerText(XmlReader x)
    {
        if (x.IsEmptyElement) return "";
        int depth = x.Depth;
        var sb = new StringBuilder();
        while (x.Read())
        {
            if (x.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace) sb.Append(x.Value);
            else if (x.NodeType == XmlNodeType.EndElement && x.Depth == depth) break;
        }
        return sb.ToString();
    }

    static string ReadEntry(ZipArchive z, string name)
    {
        var e = z.GetEntry(name);
        if (e == null) return null;
        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }

    /// <summary>Relationship id → target path (relative to the part's folder).</summary>
    static Dictionary<string, string> Rels(ZipArchive z, string relsPath, string baseDir)
    {
        var map = new Dictionary<string, string>();
        var e = z.GetEntry(relsPath);
        if (e == null) return map;
        using var s = e.Open();
        using var x = Xml(s);
        while (x.Read())
            if (x.NodeType == XmlNodeType.Element && x.LocalName == "Relationship")
            {
                var id = x.GetAttribute("Id"); var target = x.GetAttribute("Target") ?? "";
                if (id == null) continue;
                map[id] = target.StartsWith("/") ? target.TrimStart('/') : Normalize(baseDir + "/" + target);
            }
        return map;
    }

    static string Normalize(string p)
    {
        var parts = new List<string>();
        foreach (var seg in p.Split('/'))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg.Length > 0 && seg != ".") parts.Add(seg);
        }
        return string.Join("/", parts);
    }

    /// <summary>Title, subject, author, keywords and comments from Office files.</summary>
    public static string OfficeProps(string path)
    {
        try
        {
            using var z = ZipFile.OpenRead(path);
            var e = z.GetEntry("docProps/core.xml");
            if (e == null) return "";
            using var s = e.Open();
            using var x = Xml(s);
            var sb = new StringBuilder();
            while (x.Read())
                if (x.NodeType == XmlNodeType.Element && x.LocalName is "title" or "subject" or "creator" or "keywords" or "description" or "category")
                {
                    var name = x.LocalName;
                    var v = InnerText(x).Trim();
                    if (v.Length > 0) sb.Append(name == "creator" ? "Author" : char.ToUpper(name[0]) + name[1..]).Append(": ").Append(v).Append(" · ");
                }
            return sb.ToString().TrimEnd(' ', '·');
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ Word
    static IEnumerable<Seg> Docx(string path)
    {
        var parts = new List<(string, string, string)>();
        using (var z = ZipFile.OpenRead(path))
        {
            var e = z.GetEntry("word/document.xml");
            if (e == null) return Array.Empty<Seg>();
            using var s = e.Open();
            using var x = Xml(s);
            int page = 1;
            var para = new StringBuilder();
            while (x.Read())
            {
                if (x.NodeType == XmlNodeType.Element)
                {
                    switch (x.LocalName)
                    {
                        case "t": para.Append(InnerText(x)); break;
                        case "tab": para.Append(' '); break;
                        case "br":
                            if (x.GetAttribute("type", "http://schemas.openxmlformats.org/wordprocessingml/2006/main") == "page") page++;
                            else para.Append(' ');
                            break;
                        case "lastRenderedPageBreak": page++; break;
                    }
                }
                else if (x.NodeType == XmlNodeType.EndElement && x.LocalName is "p" or "tc")
                {
                    if (para.Length > 0) { parts.Add(($"page {page}", $"page:{page}", para.ToString())); para.Clear(); }
                }
            }
            if (para.Length > 0) parts.Add(($"page {page}", $"page:{page}", para.ToString()));
            // Headers and footers
            foreach (var h in z.Entries.Where(en => Regex.IsMatch(en.FullName, @"^word/(header|footer)\d*\.xml$")))
            {
                using var hs = h.Open();
                using var hx = Xml(hs);
                var sb = new StringBuilder();
                while (hx.Read()) if (hx.NodeType == XmlNodeType.Element && hx.LocalName == "t") sb.Append(InnerText(hx)).Append(' ');
                if (sb.Length > 0) parts.Add((h.FullName.Contains("header") ? "header" : "footer", "", sb.ToString()));
            }
        }
        return Chunk(parts).ToList();
    }

    // ------------------------------------------------------------------ PowerPoint
    static IEnumerable<Seg> Pptx(string path)
    {
        var res = new List<Seg>();
        using var z = ZipFile.OpenRead(path);
        var rels = Rels(z, "ppt/_rels/presentation.xml.rels", "ppt");
        var order = new List<string>();
        var pres = z.GetEntry("ppt/presentation.xml");
        if (pres != null)
        {
            using var s = pres.Open();
            using var x = Xml(s);
            while (x.Read())
                if (x.NodeType == XmlNodeType.Element && x.LocalName == "sldId")
                {
                    var rid = x.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    if (rid != null && rels.TryGetValue(rid, out var t)) order.Add(t);
                }
        }
        if (order.Count == 0)
            order = z.Entries.Select(en => en.FullName).Where(n => Regex.IsMatch(n, @"^ppt/slides/slide\d+\.xml$"))
                .OrderBy(n => int.Parse(Regex.Match(n, @"\d+").Value)).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            var slide = order[i];
            var text = DrawingText(z, slide);
            if (text.Length > 0) res.Add(new Seg($"slide {i + 1}", Clean(text), $"slide:{i + 1}"));
            var sr = Rels(z, Path.GetDirectoryName(slide)!.Replace('\\', '/') + "/_rels/" + Path.GetFileName(slide) + ".rels", Path.GetDirectoryName(slide)!.Replace('\\', '/'));
            var notes = sr.Values.FirstOrDefault(v => v.Contains("notesSlide"));
            if (notes != null)
            {
                var nt = DrawingText(z, notes);
                nt = Regex.Replace(nt, @"^\s*\d+\s*$", "", RegexOptions.Multiline).Trim();
                if (nt.Length > 0) res.Add(new Seg($"slide {i + 1} · speaker notes", Clean(nt), $"slide:{i + 1}"));
            }
        }
        return res;
    }

    static string DrawingText(ZipArchive z, string part)
    {
        var e = z.GetEntry(part);
        if (e == null) return "";
        using var s = e.Open();
        using var x = Xml(s);
        var sb = new StringBuilder();
        while (x.Read())
        {
            if (x.NodeType == XmlNodeType.Element && x.LocalName == "t") sb.Append(InnerText(x));
            else if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "p") sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    // ------------------------------------------------------------------ Excel
    static IEnumerable<Seg> Xlsx(string path)
    {
        var res = new List<Seg>();
        using var z = ZipFile.OpenRead(path);
        var shared = new List<string>();
        var ss = z.GetEntry("xl/sharedStrings.xml");
        if (ss != null)
        {
            using var s = ss.Open();
            using var x = Xml(s);
            StringBuilder cur = null;
            while (x.Read())
            {
                if (x.NodeType == XmlNodeType.Element && x.LocalName == "si") cur = new StringBuilder();
                else if (x.NodeType == XmlNodeType.Element && x.LocalName == "t" && cur != null) cur.Append(InnerText(x));
                else if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "si" && cur != null) { shared.Add(cur.ToString()); cur = null; }
            }
        }
        var rels = Rels(z, "xl/_rels/workbook.xml.rels", "xl");
        var sheets = new List<(string Name, string Part)>();
        var wb = z.GetEntry("xl/workbook.xml");
        if (wb != null)
        {
            using var s = wb.Open();
            using var x = Xml(s);
            while (x.Read())
                if (x.NodeType == XmlNodeType.Element && x.LocalName == "sheet")
                {
                    var name = x.GetAttribute("name") ?? "Sheet";
                    var rid = x.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    if (rid != null && rels.TryGetValue(rid, out var t)) sheets.Add((name, t));
                }
        }
        foreach (var (name, part) in sheets)
        {
            var e = z.GetEntry(part);
            if (e == null) continue;
            using var s = e.Open();
            using var x = Xml(s);
            string row = "", cellRef = "", type = "";
            var rowText = new StringBuilder();
            var val = new StringBuilder();
            bool inCell = false;
            int rows = 0;
            while (x.Read())
            {
                if (x.NodeType == XmlNodeType.Element)
                {
                    switch (x.LocalName)
                    {
                        case "row": row = x.GetAttribute("r") ?? row; rowText.Clear(); break;
                        case "c": cellRef = x.GetAttribute("r") ?? ""; type = x.GetAttribute("t") ?? ""; val.Clear(); inCell = !x.IsEmptyElement; break;
                        case "v": if (inCell) val.Append(InnerText(x)); break;
                        case "t": if (inCell) val.Append(InnerText(x)); break;
                    }
                }
                else if (x.NodeType == XmlNodeType.EndElement)
                {
                    if (x.LocalName == "c" && inCell)
                    {
                        var v = val.ToString();
                        if (type == "s" && int.TryParse(v, out var si) && si >= 0 && si < shared.Count) v = shared[si];
                        if (v.Trim().Length > 0) rowText.Append(cellRef).Append(": ").Append(Clean(v)).Append(" | ");
                        inCell = false;
                    }
                    else if (x.LocalName == "row")
                    {
                        if (rowText.Length > 0) res.Add(new Seg($"Sheet \"{name}\" · row {row}", rowText.ToString().TrimEnd(' ', '|'), $"cell:{name}!A{row}"));
                        if (++rows > 200_000) break;
                    }
                }
            }
        }
        return res;
    }

    /// <summary>In an Excel row segment ("D45: text | E45: text"), the cell that holds the match.</summary>
    public static string CellAt(string segText, int index)
    {
        var start = segText.LastIndexOf(" | ", Math.Max(0, Math.Min(index, segText.Length - 1)), StringComparison.Ordinal);
        var piece = start < 0 ? segText : segText[(start + 3)..];
        var m = Regex.Match(piece, @"^([A-Z]{1,3}\d+):");
        return m.Success ? m.Groups[1].Value : "";
    }

    // ------------------------------------------------------------------ PDF (+ OCR for pages without text)
    static IEnumerable<Seg> Pdf(string path)
    {
        var res = new List<Seg>();
        var noText = new List<int>();
        int pages = 0;
        if (FileKinds.On("D"))
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
            pages = doc.NumberOfPages;
            for (int i = 1; i <= pages && i <= 3000; i++)
            {
                string t;
                try { t = string.Join(" ", doc.GetPage(i).GetWords().Select(w => w.Text)); } catch { t = ""; }
                if (t.Trim().Length < 20) noText.Add(i);
                else res.Add(new Seg($"page {i}", Clean(t), $"page:{i}"));
            }
        }
        if (FileKinds.On("E") && (!FileKinds.On("D") || noText.Count > 0))
        {
            var todo = FileKinds.On("D") ? noText : null;
            foreach (var (pg, text) in Ocr.Pdf(path, todo, 300))
                res.Add(new Seg($"page {pg} (OCR)", Clean(text), $"page:{pg}") { OcrPage = pg, Unclear = Ocr.IsUnclear(text) });
        }
        return res;
    }

    public static string PdfProps(string path)
    {
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
            var i = doc.Information;
            return string.Join(" · ", new[] { ("Title", i.Title), ("Author", i.Author), ("Subject", i.Subject), ("Keywords", i.Keywords) }
                .Where(p => !string.IsNullOrWhiteSpace(p.Item2)).Select(p => p.Item1 + ": " + p.Item2.Trim()));
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ text files
    static IEnumerable<Seg> TextFile(string path, bool html)
    {
        var res = new List<Seg>();
        string all;
        using (var r = new StreamReader(path, Encoding.UTF8, true)) all = r.ReadToEnd();
        if (all.Length > 30_000_000) all = all[..30_000_000];
        if (html)
        {
            all = Regex.Replace(all, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            all = Regex.Replace(all, @"<br\s*/?>|</p>|</div>|</tr>|</h\d>", "\n", RegexOptions.IgnoreCase);
            all = System.Net.WebUtility.HtmlDecode(Regex.Replace(all, "<[^>]+>", " "));
        }
        var lines = all.Replace("\r\n", "\n").Split('\n');
        const int per = 25;
        for (int i = 0; i < lines.Length; i += per)
        {
            var chunk = string.Join("\n", lines.Skip(i).Take(per));
            if (chunk.Trim().Length > 0) res.Add(new Seg($"line {i + 1}", chunk, $"line:{i + 1}"));
        }
        return res;
    }

    /// <summary>For text files: the exact line of a match inside a 25-line segment.</summary>
    public static int LineAt(Seg s, int index)
    {
        if (!s.Goto.StartsWith("line:") || !int.TryParse(s.Goto[5..], out var start)) return 0;
        return start + s.Text[..Math.Min(index, s.Text.Length)].Count(c => c == '\n');
    }

    // ------------------------------------------------------------------ CAD / BIM
    static IEnumerable<Seg> Dxf(string path)
    {
        var parts = new List<(string, string, string)>();
        using var r = new StreamReader(path, Encoding.UTF8, true);
        string ent = "", layer = "0";
        var text = new StringBuilder();
        var keep = new HashSet<string> { "TEXT", "MTEXT", "ATTRIB", "ATTDEF", "DIMENSION", "MULTILEADER", "MLEADER" };
        void Flush()
        {
            if (text.Length > 0 && keep.Contains(ent))
            {
                var t = text.ToString();
                t = t.Replace("\\P", "\n");
                t = Regex.Replace(t, @"\\[ACFHQTWLlOoKk][^;\\]*;?", "");
                t = t.Replace("{", "").Replace("}", "").Replace("%%c", "Ø").Replace("%%d", "°").Replace("%%p", "±");
                parts.Add(($"layer {layer} · {ent}", "", t));
            }
            text.Clear();
        }
        string code;
        int n = 0;
        while ((code = r.ReadLine()) != null)
        {
            var val = r.ReadLine();
            if (val == null) break;
            if (++n > 5_000_000) break;
            switch (code.Trim())
            {
                case "0": Flush(); ent = val.Trim(); layer = "0"; break;
                case "8": layer = val.Trim(); break;
                case "1": case "3": if (keep.Contains(ent)) text.Append(val); break;
                case "304": if (ent is "MULTILEADER" or "MLEADER") text.Append(val); break;
            }
        }
        Flush();
        return Chunk(parts).ToList();
    }

    static readonly Regex IfcLine = new(@"^#(\d+)\s*=\s*(IFC[A-Z0-9_]+)\s*\((.*)\);\s*$", RegexOptions.Compiled);
    static readonly Regex IfcStr = new(@"'((?:[^']|'')*)'", RegexOptions.Compiled);

    static IEnumerable<Seg> Ifc(string path)
    {
        var parts = new List<(string, string, string)>();
        using var r = new StreamReader(path, Encoding.UTF8, true);
        string line;
        int n = 0;
        var pending = new StringBuilder();
        while ((line = r.ReadLine()) != null && n < 3_000_000)
        {
            pending.Append(line);
            if (!line.TrimEnd().EndsWith(";")) continue;
            var full = pending.ToString(); pending.Clear(); n++;
            var m = IfcLine.Match(full);
            if (!m.Success) continue;
            var type = m.Groups[2].Value;
            if (type is "IFCCARTESIANPOINT" or "IFCDIRECTION" or "IFCPOLYLOOP" or "IFCFACE" or "IFCFACEOUTERBOUND" or "IFCOWNERHISTORY") continue;
            var strs = IfcStr.Matches(m.Groups[3].Value).Select(x => Decode(x.Groups[1].Value.Replace("''", "'")))
                .Where(s => s.Length > 0 && !Regex.IsMatch(s, @"^[0-9A-Za-z_$]{22}$")).ToList();
            if (strs.Count > 0) parts.Add(($"{type} #{m.Groups[1].Value}", "", string.Join(" · ", strs)));
        }
        return Chunk(parts).ToList();
    }

    /// <summary>IFC string escapes: \X2\0645\X0\ (UTF-16) and \S\ (upper half of Latin-1).</summary>
    static string Decode(string s)
    {
        s = Regex.Replace(s, @"\\X2\\([0-9A-Fa-f]+)\\X0\\", m =>
        {
            var hex = m.Groups[1].Value;
            var sb = new StringBuilder();
            for (int i = 0; i + 4 <= hex.Length; i += 4) sb.Append((char)Convert.ToInt32(hex.Substring(i, 4), 16));
            return sb.ToString();
        });
        s = Regex.Replace(s, @"\\S\\(.)", m => ((char)(m.Groups[1].Value[0] + 128)).ToString());
        return s;
    }

    // ------------------------------------------------------------------ emails
    /// <summary>Outlook .msg files, read through the Outlook installed on this PC.</summary>
    static IEnumerable<Seg> Msg(string path, int depth)
    {
        var res = new List<Seg>();
        if (!Outlook.Installed) return res;
        dynamic app = Outlook.Get();
        dynamic item = app.Session.OpenSharedItem(path);
        try
        {
            string subject = Outlook.S(() => item.Subject), from = Outlook.S(() => item.SenderName), addr = Outlook.S(() => item.SenderEmailAddress);
            string to = Outlook.S(() => item.To), body = Outlook.S(() => item.Body);
            DateTime when = DateTime.MinValue;
            try { when = (DateTime)item.SentOn; } catch { }
            res.Add(new Seg("email", Clean($"From: {from} <{addr}> · {(when.Year > 1990 ? when.ToString("dd MMM yyyy HH:mm") : "")}\nTo: {to}\nSubject: {subject}\n{body}")));
            dynamic atts = item.Attachments;
            int n = (int)atts.Count;
            for (int i = 1; i <= n && i <= 30; i++)
            {
                dynamic a = atts.Item(i);
                try
                {
                    string name = Outlook.S(() => a.FileName);
                    if (name.Length == 0) continue;
                    var tmp = Path.Combine(Paths.Cache, Guid.NewGuid().ToString("N") + Path.GetExtension(name));
                    try
                    {
                        a.SaveAsFile(tmp);
                        res.AddRange(Inner(name, File.ReadAllBytes(tmp), "attachment", depth));
                    }
                    catch { res.Add(new Seg($"attachment: {name}", "File name: " + name)); }
                    finally { try { File.Delete(tmp); } catch { } }
                }
                finally { Outlook.Release(a); }
            }
            Outlook.Release(atts);
        }
        finally
        {
            try { item.Close(1); } catch { }
            Outlook.Release(item);
        }
        return res;
    }

    static IEnumerable<Seg> Eml(string path, int depth)
    {
        var res = new List<Seg>();
        var m = MimeKit.MimeMessage.Load(path);
        var body = m.TextBody;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrEmpty(m.HtmlBody))
            body = System.Net.WebUtility.HtmlDecode(Regex.Replace(m.HtmlBody, "<[^>]+>", " "));
        res.Add(new Seg("email", Clean($"From: {m.From} · {m.Date:dd MMM yyyy HH:mm}\nSubject: {m.Subject}\n{body}")));
        foreach (var a in m.Attachments)
        {
            if (a is not MimeKit.MimePart part || string.IsNullOrEmpty(part.FileName) || part.Content == null) continue;
            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            res.AddRange(Inner(part.FileName, ms.ToArray(), "attachment", depth));
        }
        return res;
    }

    /// <summary>Reads a file found inside an email or a zip; the location says where it came from.</summary>
    static IEnumerable<Seg> Inner(string name, byte[] data, string kind, int depth)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (data.Length > 60_000_000 || !FileKinds.ReadContent(ext) || depth >= 2) return new[] { new Seg($"{kind}: {name}", "File name: " + name) };
        var tmp = Path.Combine(Paths.Cache, Guid.NewGuid().ToString("N") + ext);
        try
        {
            File.WriteAllBytes(tmp, data);
            var inner = Extract(tmp, depth + 1);
            var list = new List<Seg> { new($"{kind}: {name}", "File name: " + name) };
            list.AddRange(inner.Select(s => new Seg($"{kind}: {name} · {s.Loc}", s.Text, "")));
            return list;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static IEnumerable<Seg> Zip(string path, int depth)
    {
        var res = new List<Seg>();
        using var z = ZipFile.OpenRead(path);
        int n = 0;
        foreach (var e in z.Entries)
        {
            if (e.FullName.EndsWith("/") || ++n > 2000) continue;
            var ext = Path.GetExtension(e.Name).ToLowerInvariant();
            res.Add(new Seg($"› {e.FullName}", "File name: " + e.FullName, "zip:" + e.FullName));
            if (!FileKinds.ReadContent(ext) || e.Length > 60_000_000 || e.Length == 0) continue;
            using var ms = new MemoryStream();
            using (var s = e.Open()) s.CopyTo(ms);
            var tmp = Path.Combine(Paths.Cache, Guid.NewGuid().ToString("N") + ext);
            try
            {
                File.WriteAllBytes(tmp, ms.ToArray());
                foreach (var s in Extract(tmp, depth + 1)) res.Add(new Seg($"› {e.FullName} · {s.Loc}", s.Text, "zip:" + e.FullName + "|" + s.Goto));
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        return res;
    }

    // ------------------------------------------------------------------ old Office formats, through the Office on this PC
    static IEnumerable<Seg> ViaOffice(string path, string app)
    {
        var target = Path.Combine(Paths.Cache, Guid.NewGuid().ToString("N") + app switch { "word" => ".docx", "powerpoint" => ".pptx", _ => ".xlsx" });
        try
        {
            if (!OfficeConvert.Convert(path, target, app)) return Array.Empty<Seg>();
            return app switch { "word" => Docx(target), "powerpoint" => Pptx(target), _ => Xlsx(target) };
        }
        finally { try { File.Delete(target); } catch { } }
    }
}

/// <summary>Old .doc/.ppt/.xls → new format with the Office installed here (hidden, read-only). Only Office instances Nova started are closed.</summary>
static class OfficeConvert
{
    [DllImport("ole32.dll")] static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);
    [DllImport("oleaut32.dll")] static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    static readonly Dictionary<string, (object App, bool Ours)> apps = new();
    public static bool Missing(string app) => Type.GetTypeFromProgID(ProgId(app)) == null;
    static string ProgId(string app) => app switch { "word" => "Word.Application", "powerpoint" => "PowerPoint.Application", _ => "Excel.Application" };

    static dynamic App(string app)
    {
        if (apps.TryGetValue(app, out var a)) return a.App;
        var t = Type.GetTypeFromProgID(ProgId(app));
        if (t == null) return null;
        bool ours = true;
        object o = null;
        if (app == "powerpoint" && CLSIDFromProgID(ProgId(app), out var clsid) == 0 && GetActiveObject(ref clsid, IntPtr.Zero, out var running) == 0) { o = running; ours = false; }
        o ??= Activator.CreateInstance(t);
        dynamic d = o;
        try { if (app != "powerpoint") d.Visible = false; } catch { }
        try { d.DisplayAlerts = app switch { "excel" => (object)false, "powerpoint" => 1, _ => 0 }; } catch { }
        apps[app] = (o, ours);
        return d;
    }

    public static bool Convert(string src, string dst, string app)
    {
        try
        {
            dynamic a = App(app);
            if (a == null) return false;
            switch (app)
            {
                case "word":
                {
                    dynamic d = a.Documents.Open(src, false, true, false, "nova", "nova", false, "nova", "nova", Type.Missing, Type.Missing, false);
                    try { d.SaveAs2(dst, 16); } finally { d.Close(false); Outlook.Release(d); }
                    break;
                }
                case "powerpoint":
                {
                    dynamic p = a.Presentations.Open(src, -1, 0, 0);
                    try { p.SaveAs(dst, 24); } finally { p.Close(); Outlook.Release(p); }
                    break;
                }
                default:
                {
                    dynamic w = a.Workbooks.Open(src, 0, true, Type.Missing, "nova");
                    try { w.SaveAs(dst, 51); } finally { w.Close(false); Outlook.Release(w); }
                    break;
                }
            }
            return File.Exists(dst);
        }
        catch (Exception e) { Log.Write($"Office could not read {Path.GetFileName(src)}: {e.Message}"); return false; }
    }

    /// <summary>Closes the Office programs Nova opened for converting.</summary>
    public static void CloseAll()
    {
        foreach (var kv in apps.ToList())
        {
            var (o, ours) = kv.Value;
            try { if (ours) ((dynamic)o).Quit(); } catch { }
            Outlook.Release(o);
        }
        apps.Clear();
    }
}

/// <summary>Title, author, tags and comments Windows knows for any file (used for .dwg, .rvt and other files Nova can't open).</summary>
static class ShellProps
{
    static dynamic shell;

    public static string Get(string path)
    {
        try
        {
            shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            dynamic folder = shell.NameSpace(Path.GetDirectoryName(path));
            if (folder == null) return "";
            dynamic item = folder.ParseName(Path.GetFileName(path));
            if (item == null) return "";
            var sb = new StringBuilder();
            foreach (var (k, label) in new[] { ("System.Title", "Title"), ("System.Subject", "Subject"), ("System.Author", "Author"), ("System.Keywords", "Tags"), ("System.Comment", "Comments"), ("System.Company", "Company") })
            {
                object v = null;
                try { v = item.ExtendedProperty(k); } catch { }
                var s = v is Array arr ? string.Join("; ", arr.Cast<object>()) : Convert.ToString(v);
                if (!string.IsNullOrWhiteSpace(s)) sb.Append(label).Append(": ").Append(s.Trim()).Append(" · ");
            }
            return sb.ToString().TrimEnd(' ', '·');
        }
        catch { return ""; }
    }
}
