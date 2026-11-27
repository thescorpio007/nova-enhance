using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// ⚡ Fast PDF reading for questions (submittals, catalogues, scans):
/// 1 · the pages that have text are read in seconds and answered from at once;
/// 2 · scanned pages that matter go first — the PDF's bookmarks, its contents page, the pages right after a page that names your
///     words (a section divider), the cover / transmittal;
/// 3 · scanned pages are read 3 at a time (a separate OCR helper each), A4 pages at 200 dpi (drawings keep 300 dpi);
/// 4 · the rest is read in the background and kept in the shared Nova index (read once = known by every feature).
/// Designed by A.HAMROUNI
/// </summary>
static class PdfFast
{
    public class Info
    {
        public int Pages;
        public List<Seg> Text = new();
        public List<int> Scanned = new();
        public List<(string Title, int Page)> Marks = new();
        public DateTime Stamp;
    }

    public class Quick
    {
        public List<Seg> Segs = new();
        public int Pages, TextPages, Scanned;
        public List<int> Left = new();
        public string How = "";
    }

    static readonly Dictionary<string, Info> infos = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Dictionary<int, Seg>> ocrDone = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The text pages + which pages are scans + the bookmarks (PdfPig, seconds). Kept while the file is unchanged.</summary>
    public static Info Scan(string path, Action<string> say = null)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (infos) if (infos.TryGetValue(path, out var known) && known.Stamp == stamp) return known;
        var info = new Info { Stamp = stamp };
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(LongPath.Usable(path)))
        {
            info.Pages = doc.NumberOfPages;
            try
            {
                if (doc.TryGetBookmarks(out var bm))
                    foreach (var n in bm.GetNodes())
                        if (n is UglyToad.PdfPig.Outline.DocumentBookmarkNode d && d.PageNumber > 0) info.Marks.Add((n.Title ?? "", d.PageNumber));
            }
            catch { }
            for (int i = 1; i <= info.Pages && i <= 3000; i++)
            {
                string t;
                try { t = string.Join(" ", doc.GetPage(i).GetWords().Select(w => w.Text)); } catch { t = ""; }
                if (i % 10 == 0) say?.Invoke($"📄 page {i} of {info.Pages} · text pages");
                if (t.Trim().Length < 20) info.Scanned.Add(i);
                else info.Text.Add(new Seg($"page {i}", FileText.CleanText(t), $"page:{i}"));
            }
        }
        info.Marks = info.Marks.OrderBy(m => m.Page).ToList();
        lock (infos) infos[path] = info;
        return info;
    }

    /// <summary>The scanned pages, the ones most likely to answer first.</summary>
    public static List<int> Priority(Info info, List<(string W, double Weight)> words)
    {
        var score = info.Scanned.ToDictionary(p => p, p => p <= 3 ? 1.0 : 0.0);
        void Add(int p, double s) { if (score.ContainsKey(p)) score[p] += s; }
        // 🔖 bookmarks whose title names your words: their pages (until the next bookmark, at most 8)
        for (int i = 0; i < info.Marks.Count; i++)
        {
            var (title, page) = info.Marks[i];
            var s = Alike.Score(title, words);
            if (s <= 0) continue;
            var end = i + 1 < info.Marks.Count ? info.Marks[i + 1].Page : page + 8;
            for (int p = page; p < Math.Min(end, page + 8); p++) Add(p, 5 + s);
        }
        foreach (var seg in info.Text)
        {
            var pg = PageOf(seg);
            var s = Alike.Score(seg.Text, words);
            if (s <= 0) continue;
            // a divider / cover page naming your words: the scanned pages right after it
            for (int p = pg + 1; p <= pg + 4; p++) Add(p, 3 + s / (p - pg));
            // a contents / index page: “Sliding door hardware ..... 23”
            if (Regex.IsMatch(seg.Text, @"\b(contents|index|table of content)", RegexOptions.IgnoreCase))
                foreach (var (w, _) in words)
                    foreach (Match m in Regex.Matches(seg.Text, Regex.Escape(w) + @"[^\d]{0,60}?\b(\d{1,3})\b", RegexOptions.IgnoreCase))
                        if (int.TryParse(m.Groups[1].Value, out var tp)) for (int p = tp; p <= tp + 2; p++) Add(p, 4);
        }
        return score.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => x.Key).ToList();
    }

    /// <summary>OCR of these pages, 3 at a time (each in its own OCR helper). Pages read before are not read again.</summary>
    public static List<Seg> OcrPages(string path, List<int> pages, Action<string> say = null)
    {
        Dictionary<int, Seg> have;
        lock (ocrDone) { if (!ocrDone.TryGetValue(path, out have)) ocrDone[path] = have = new(); }
        List<int> todo; lock (have) todo = pages.Where(p => !have.ContainsKey(p)).Distinct().ToList();
        if (todo.Count > 0)
        {
            int workers = Math.Clamp((todo.Count + 1) / 2, 1, 3), done = 0, total = todo.Count;
            var chunks = Enumerable.Range(0, workers).Select(w => todo.Where((_, i) => i % workers == w).ToList()).Where(c => c.Count > 0).ToList();
            var seen = new HashSet<(int, int)>();
            Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = workers }, (chunk, _, idx) =>
            {
                var got = Ocr.Pdf(path, chunk, chunk.Count, p => { lock (seen) if (seen.Add(((int)idx, p))) { done++; say?.Invoke($"🔤 scanned page {p} · {Math.Min(done, total)} of {total} · {workers} at a time"); } });
                lock (have)
                    foreach (var (pg, text) in got)
                        have[pg] = new Seg($"page {pg} (OCR)", FileText.CleanText(text), $"page:{pg}") { OcrPage = pg, Unclear = Ocr.IsUnclear(text) };
            });
        }
        lock (have) return pages.Where(have.ContainsKey).Select(p => have[p]).Where(s => (s.Text ?? "").Trim().Length > 0).ToList();
    }

    /// <summary>⚡ A question on a PDF: the text pages + up to `budget` scanned pages that matter, now. The rest is left for the background.</summary>
    public static Quick Read(string path, IEnumerable<string> terms, int budget, Action<string> say = null)
    {
        var info = Scan(path, say);
        var words = Alike.Expand(terms);
        var q = new Quick { Pages = info.Pages, TextPages = info.Text.Count, Scanned = info.Scanned.Count };
        var order = Priority(info, words);
        var now = order.Take(budget).ToList();
        if (now.Count > 0) say?.Invoke($"🔤 {now.Count} scanned page(s) that matter first · of {info.Scanned.Count} scanned");
        var ocr = now.Count > 0 ? OcrPages(path, now, say) : new List<Seg>();
        q.Segs = info.Text.Concat(ocr).OrderBy(PageOf).ToList();
        Dictionary<int, Seg> have; lock (ocrDone) ocrDone.TryGetValue(path, out have);
        q.Left = info.Scanned.Where(p => have == null || !have.ContainsKey(p)).ToList();
        q.How = $"{info.Pages} page(s): {info.Text.Count} with text" + (info.Scanned.Count > 0 ? $", {info.Scanned.Count} scanned ({info.Scanned.Count - q.Left.Count} read now" + (info.Marks.Count > 0 ? ", bookmarks used" : "") + ")" : "");
        return q;
    }

    public static int PageOf(Seg x)
    {
        var m = Regex.Match(x.Goto ?? "", @"page:(\d+)");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        m = Regex.Match(x.Loc ?? "", @"(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>The most useful parts for a question: scored with your words and their related words (not only exact words).</summary>
    public static List<Seg> Pick(List<Seg> segs, IEnumerable<string> terms, int take)
    {
        var words = Alike.Expand(terms);
        return segs.Select((s, i) => (s, i, sc: Alike.Score(s.Text, words))).OrderByDescending(x => x.sc).ThenBy(x => x.i).Take(take).OrderBy(x => x.i).Select(x => x.s).ToList();
    }
}

/// <summary>🔎 Related words: “sliding” also finds slide, track, rail, roller, pocket… Built-in groups for construction documents,
/// plus your own in the 🧠 Brain: [search.alike = sliding: slide track rail roller, hardware: ironmongery fittings]. Designed by A.HAMROUNI</summary>
static class Alike
{
    static readonly string[] Groups =
    {
        "sliding slide slides slider track tracks rail rails roller rollers pocket telescopic glide gliding",
        "door doors leaf leaves doorset doorsets",
        "hardware ironmongery fittings fixtures accessories handle handles lever lock locks lockset hinge hinges closer closers pull pulls stopper stoppers latch cylinder escutcheon bolt",
        "window windows glazing glass glazed pane",
        "fire fire-rated fr rated rating resistance",
        "waterproofing waterproof membrane bitumen",
        "tile tiles tiling porcelain ceramic",
        "paint painting coating coatings primer stucco",
        "ceiling ceilings gypsum board",
        "wardrobe wardrobes closet joinery",
        "kitchen kitchens cabinet cabinets worktop",
        "sanitary wc basin mixer tap shower bath",
        "aluminium aluminum profile profiles mullion transom",
        "stone marble granite limestone",
        "approval approved code status comment comments",
        "warranty guarantee",
        "finish finishes colour color ral"
    };

    public static List<(string W, double Weight)> Expand(IEnumerable<string> terms)
    {
        var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var list = (terms ?? Enumerable.Empty<string>()).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 1).Distinct().ToList();
        foreach (var t in list) res[t] = 2;
        var groups = Groups.Select(g => g.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        try
        {
            foreach (var line in Brain.ListOf("search.alike", ""))
            {
                var i = line.IndexOf(':');
                if (i > 0) groups.Add(new[] { line[..i].Trim() }.Concat(line[(i + 1)..].Split(new[] { ' ', '/', ';' }, StringSplitOptions.RemoveEmptyEntries)).ToArray());
            }
        }
        catch { }
        foreach (var t in list)
            foreach (var g in groups.Where(g => g.Any(w => w.Equals(t, StringComparison.OrdinalIgnoreCase) || t.Length > 4 && w.StartsWith(t[..^1], StringComparison.OrdinalIgnoreCase))))
                foreach (var w in g) if (!res.ContainsKey(w)) res[w] = 1;
        return res.Select(x => (x.Key, x.Value)).ToList();
    }

    public static double Score(string text, List<(string W, double Weight)> words)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double s = 0; int distinct = 0;
        foreach (var (w, wt) in words)
        {
            var n = Regex.Matches(text, @"(?<![\p{L}])" + Regex.Escape(w), RegexOptions.IgnoreCase).Count;
            if (n == 0) continue;
            s += wt * Math.Min(n, 3);
            if (wt >= 2) distinct++;
        }
        return s + (distinct > 1 ? distinct * 2 : 0);   // a page with several of YOUR words first
    }
}
