using System.Drawing.Drawing2D;
using System.Security.Cryptography;
using System.Text;

namespace NovaEnhance;

/// <summary>A document page that can be drawn, with the found words as yellow marks (positions 0..1 of the page).</summary>
interface IPreviewDoc : IDisposable
{
    int Pages { get; }
    /// <summary>Page size in 1/96 inch.</summary>
    SizeF PageSize(int page);
    Bitmap Render(int page, int pixelWidth);
    List<RectangleF> Marks(int page, Matcher m);
}

/// <summary>PDF pages drawn by Windows; word positions from the PDF text (or from OCR on scanned pages).</summary>
sealed class PdfPreview : IPreviewDoc
{
    readonly Windows.Data.Pdf.PdfDocument doc;
    readonly UglyToad.PdfPig.PdfDocument pig;
    readonly object L = new();

    public PdfPreview(string path)
    {
        var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().Result;
        doc = Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask().Result;
        try { pig = UglyToad.PdfPig.PdfDocument.Open(path); } catch { pig = null; }
    }

    public int Pages => (int)doc.PageCount;

    public SizeF PageSize(int page)
    {
        lock (L)
        {
            using var p = doc.GetPage((uint)(Math.Clamp(page, 1, Pages) - 1));
            return new SizeF((float)p.Size.Width, (float)p.Size.Height);
        }
    }

    public Bitmap Render(int page, int pixelWidth)
    {
        lock (L)
        {
            using var p = doc.GetPage((uint)(Math.Clamp(page, 1, Pages) - 1));
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            p.RenderToStreamAsync(ras, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)Math.Clamp(pixelWidth, 50, 9000) }).AsTask().Wait();
            ras.Seek(0);
            using var s = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }
    }

    public List<RectangleF> Marks(int page, Matcher m)
    {
        var res = new List<RectangleF>();
        if (m == null) return res;
        if (pig != null)
        {
            try
            {
                UglyToad.PdfPig.Content.Page pp;
                lock (L) pp = pig.GetPage(Math.Clamp(page, 1, Pages));
                var words = pp.GetWords().ToList();
                if (words.Count > 3)
                {
                    var boxes = words.Select(w => w.BoundingBox).ToList();
                    res = PreviewMarks.FromWords(words.Select(w => w.Text).ToList(), i =>
                    {
                        var b = boxes[i];
                        return new RectangleF((float)(b.Left / pp.Width), (float)(1 - b.Top / pp.Height), (float)(b.Width / pp.Width), (float)(b.Height / pp.Height));
                    }, m);
                    return res;
                }
            }
            catch { }
        }
        // Scanned page: read it with OCR to know where the words are.
        try
        {
            using var bmp = Render(page, 2400);
            res = PreviewMarks.FromOcr(bmp, m);
        }
        catch { }
        return res;
    }

    public void Dispose() { try { pig?.Dispose(); } catch { } }
}

/// <summary>Pictures (scans, photos): one page; word positions from OCR.</summary>
sealed class ImagePreview : IPreviewDoc
{
    readonly Bitmap img;
    public ImagePreview(string path) { using var src = Image.FromFile(path); img = new Bitmap(src); }
    public int Pages => 1;
    public SizeF PageSize(int page) => new(img.Width * 96f / Math.Max(1, img.HorizontalResolution), img.Height * 96f / Math.Max(1, img.VerticalResolution));
    public Bitmap Render(int page, int pixelWidth)
    {
        var w = Math.Clamp(pixelWidth, 50, 9000);
        var h = (int)(img.Height * (double)w / img.Width);
        var b = new Bitmap(w, Math.Max(1, h));
        using var g = Graphics.FromImage(b);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, 0, 0, w, h);
        return b;
    }
    public List<RectangleF> Marks(int page, Matcher m) => m == null ? new() : PreviewMarks.FromOcr(img, m);
    public void Dispose() => img.Dispose();
}

static class PreviewMarks
{
    /// <summary>Joins the words of a page, finds the matches and turns them into one rectangle per line.</summary>
    public static List<RectangleF> FromWords(List<string> words, Func<int, RectangleF> box, Matcher m)
    {
        var sb = new StringBuilder();
        var spans = new List<(int Start, int End)>();
        foreach (var w in words) { var s = sb.Length; sb.Append(w); spans.Add((s, sb.Length)); sb.Append(' '); }
        var text = sb.ToString();
        var res = new List<RectangleF>();
        foreach (var (idx, len) in m.Find(text))
        {
            var hit = new List<RectangleF>();
            for (int i = 0; i < spans.Count; i++)
                if (spans[i].End > idx && spans[i].Start < idx + len) hit.Add(box(i));
            // one rectangle per line
            foreach (var line in hit.GroupBy(r => Math.Round((r.Top + r.Height / 2) * 200)))
            {
                var l = line.Min(r => r.Left); var t = line.Min(r => r.Top);
                var rr = line.Max(r => r.Right); var b = line.Max(r => r.Bottom);
                res.Add(RectangleF.FromLTRB(l - 0.002f, t - 0.002f, rr + 0.002f, b + 0.002f));
            }
        }
        return res;
    }

    public static List<RectangleF> FromOcr(Bitmap bmp, Matcher m)
    {
        var res = new List<RectangleF>();
        var engines = Ocr.Engines();
        if (engines.Count == 0) return res;
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        using var ras = ms.AsRandomAccessStream();
        var dec = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras).AsTask().Result;
        double f = Math.Min(1.0, (double)Windows.Media.Ocr.OcrEngine.MaxImageDimension / Math.Max(dec.PixelWidth, dec.PixelHeight));
        var tr = new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = (uint)(dec.PixelWidth * f), ScaledHeight = (uint)(dec.PixelHeight * f) };
        using var sb = dec.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, tr,
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage).AsTask().Result;
        double W = tr.ScaledWidth, H = tr.ScaledHeight;
        foreach (var e in engines)
        {
            var r = e.RecognizeAsync(sb).AsTask().Result;
            var words = new List<string>(); var rects = new List<RectangleF>();
            foreach (var line in r.Lines)
                foreach (var w in line.Words)
                {
                    words.Add(w.Text);
                    var b = w.BoundingRect;
                    rects.Add(new RectangleF((float)(b.X / W), (float)(b.Y / H), (float)(b.Width / W), (float)(b.Height / H)));
                }
            res = FromWords(words, i => rects[i], m);
            if (res.Count > 0) break;
        }
        return res;
    }
}

/// <summary>Word and PowerPoint pages are shown through the Office on this PC (saved once as PDF, then remembered).</summary>
static class OfficePdf
{
    public static string Get(string path)
    {
        var fi = new FileInfo(path);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant() + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length)))[..20];
        var dir = Path.Combine(Paths.Cache, "preview");
        Directory.CreateDirectory(dir);
        var pdf = Path.Combine(dir, key + ".pdf");
        if (File.Exists(pdf)) return pdf;
        var ext = fi.Extension.ToLowerInvariant();
        Exception err = null;
        var t = new Thread(() =>
        {
            try
            {
                if (ext is ".docx" or ".docm" or ".doc" or ".rtf" or ".odt")
                {
                    dynamic w = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application") ?? throw new InvalidOperationException("Word isn't installed"));
                    try
                    {
                        w.Visible = false; w.DisplayAlerts = 0;
                        dynamic d = w.Documents.Open(path, false, true, false, "nova", "nova", false, "nova", "nova", Type.Missing, Type.Missing, false);
                        try { d.ExportAsFixedFormat(pdf, 17); } finally { d.Close(false); }
                    }
                    finally { try { w.Quit(false); } catch { } Outlook.Release(w); }
                }
                else if (ext is ".pptx" or ".pptm" or ".ppt")
                {
                    dynamic pp = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application") ?? throw new InvalidOperationException("PowerPoint isn't installed"));
                    dynamic p = pp.Presentations.Open(path, -1, 0, 0);
                    try { p.SaveAs(pdf, 32); } finally { p.Close(); Outlook.Release(p); }
                }
            }
            catch (Exception e) { err = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(TimeSpan.FromMinutes(3));
        if (err != null) throw err;
        if (!File.Exists(pdf)) throw new InvalidOperationException("Office could not show this file");
        return pdf;
    }
}

/// <summary>
/// 👁 Preview: the real page with the found words in yellow. Zoom (− + · Ctrl+wheel · Fit width · Fit page · 100%), drag to move,
/// ◀ hit ▶ / ← →, ‹ page › / PgUp PgDn, page thumbnails, double-click to zoom on the mark, full screen and pop-out.
/// </summary>
class PreviewPane : UserControl
{
    readonly Label title, hitLbl, pageOf, hint;
    readonly TextBox pageBox;
    readonly Label zoomLbl;
    readonly FlowLayoutPanel thumbs;
    readonly PageCanvas canvas;
    readonly RichTextBox textView;
    readonly Button fitW, fitP, fullBtn, popBtn;

    FileHit hit;
    Matcher matcher;
    IPreviewDoc doc;
    string tempFile;
    readonly List<(int Page, int K)> hits = new();
    int hitIndex, page = 1;
    int loadId;
    readonly Dictionary<int, List<RectangleF>> marks = new();
    public bool IsFullScreen;
    public event Action<bool> WindowRequested;   // true = full screen, false = pop out

    public FileHit Hit => hit;
    public Matcher Matcher => matcher;
    public int HitIndex => hitIndex;

    public PreviewPane()
    {
        BackColor = Color.FromArgb(0x0E, 0x11, 0x16);
        DoubleBuffered = true;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8, 6, 8, 2), BackColor = BackColor };
        title = Th.L("👁 Preview", 10.5f, Th.Text, true);
        title.AutoEllipsis = true; title.MaximumSize = new Size(420, 22);
        top.Controls.Add(title);
        top.Controls.Add(Small("◀ hit", () => GoHit(hitIndex - 1)));
        hitLbl = Th.L("", 10, Th.Text, true); hitLbl.Margin = new Padding(2, 7, 2, 0);
        top.Controls.Add(hitLbl);
        top.Controls.Add(Small("hit ▶", () => GoHit(hitIndex + 1)));
        top.Controls.Add(Small("‹ page", () => GoPage(page - 1)));
        pageBox = new TextBox { Width = 52, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Center, Margin = new Padding(2, 5, 2, 0) };
        pageBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && int.TryParse(pageBox.Text, out var p)) { e.SuppressKeyPress = true; GoPage(p); } };
        top.Controls.Add(pageBox);
        pageOf = Th.L("", 9.5f, Th.Muted); pageOf.Margin = new Padding(2, 8, 2, 0);
        top.Controls.Add(pageOf);
        top.Controls.Add(Small("page ›", () => GoPage(page + 1)));

        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8, 0, 8, 4), BackColor = BackColor };
        tools.Controls.Add(Small("−", () => canvas.ZoomBy(1 / 1.25)));
        zoomLbl = Th.L("100%", 10, Th.Text, true); zoomLbl.Margin = new Padding(2, 7, 2, 0); zoomLbl.MinimumSize = new Size(48, 0); zoomLbl.TextAlign = ContentAlignment.MiddleCenter;
        tools.Controls.Add(zoomLbl);
        tools.Controls.Add(Small("+", () => canvas.ZoomBy(1.25)));
        fitW = Small("Fit width", () => canvas.Fit(true)); tools.Controls.Add(fitW);
        fitP = Small("Fit page", () => canvas.Fit(false)); tools.Controls.Add(fitP);
        tools.Controls.Add(Small("100%", () => canvas.SetZoom(1.0)));
        fullBtn = Small("⤢ Full screen", () => WindowRequested?.Invoke(true)); tools.Controls.Add(fullBtn);
        popBtn = Small("⧉ Pop out", () => WindowRequested?.Invoke(false)); tools.Controls.Add(popBtn);
        var open = Th.B("Open at this page", OpenHere, true); open.Margin = new Padding(6, 2, 0, 0); open.MinimumSize = new Size(0, 28);
        tools.Controls.Add(open);

        thumbs = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 104, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.FromArgb(0x0B, 0x0D, 0x12), Padding = new Padding(8, 6, 4, 6) };
        canvas = new PageCanvas { Dock = DockStyle.Fill };
        canvas.ZoomChanged += z => zoomLbl.Text = $"{z * 100:0}%";
        canvas.PageNeeded += RenderCurrent;
        canvas.DoubleClicked += () => { var r = CurrentMark(); if (r != null) canvas.ZoomTo(r.Value, 2.5); };
        textView = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(11), Visible = false, DetectUrls = false };
        hint = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = Th.Muted, BackColor = BackColor, Font = Th.F(8.5f), TextAlign = ContentAlignment.MiddleCenter,
            Text = "Ctrl + wheel = zoom · drag = move · ← → = hits · PgUp/PgDn = pages · double-click = zoom on the yellow mark" };
        Controls.Add(canvas);
        Controls.Add(textView);
        Controls.Add(thumbs);
        Controls.Add(hint);
        Controls.Add(tools);
        Controls.Add(top);
        canvas.KeyDown += (_, e) => Keys_(e);
        textView.KeyDown += (_, e) => Keys_(e);
        ShowMessage("Click 👁 preview on a result to see the page here.");
    }

    static Button Small(string s, Action a)
    {
        var b = Th.B(s, a);
        b.Font = Th.F(9); b.MinimumSize = new Size(0, 28); b.Margin = new Padding(2, 2, 2, 0);
        return b;
    }

    void Keys_(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Right: GoHit(hitIndex + 1); e.Handled = true; break;
            case Keys.Left: GoHit(hitIndex - 1); e.Handled = true; break;
            case Keys.PageDown: GoPage(page + 1); e.Handled = true; break;
            case Keys.PageUp: GoPage(page - 1); e.Handled = true; break;
            case Keys.Add: case Keys.Oemplus: canvas.ZoomBy(1.25); e.Handled = true; break;
            case Keys.Subtract: case Keys.OemMinus: canvas.ZoomBy(0.8); e.Handled = true; break;
        }
    }

    public void ShowMessage(string s)
    {
        canvas.Visible = true; textView.Visible = false;
        canvas.SetPage(null, new SizeF(816, 1056), new());
        canvas.Message = s;
        canvas.Invalidate();
    }

    static int PageOf(string go)
    {
        if (go == null) return 0;
        if (go.StartsWith("zip:") && go.Contains('|')) go = go[(go.IndexOf('|') + 1)..];
        if (go.StartsWith("page:") && int.TryParse(go[5..], out var p)) return p;
        if (go.StartsWith("slide:") && int.TryParse(go[6..], out var s)) return s;
        return 0;
    }

    /// <summary>Shows a result: its file, all its hits, starting at the given hit.</summary>
    public async void Show(FileHit h, Matcher m, int startHit = 0)
    {
        if (hit != null && doc != null && h.Path == hit.Path && m == matcher && h.Pieces.Count == hit.Pieces.Count)
        {
            GoHit(startHit);
            return;
        }
        var my = ++loadId;
        hit = h; matcher = m;
        hits.Clear(); marks.Clear();
        foreach (var p in h.Pieces)
        {
            var pg = PageOf(p.Goto);
            int n = Math.Max(1, p.Matches?.Count ?? 1);
            for (int k = 0; k < n; k++) hits.Add((pg, k));
        }
        title.Text = "👁 " + h.Name;
        doc?.Dispose(); doc = null;
        if (tempFile != null) { try { File.Delete(tempFile); } catch { } tempFile = null; }
        thumbs.Controls.Clear();
        var paged = hits.Any(x => x.Page > 0);
        if (!paged) { ShowText(h, m); return; }
        ShowMessage("Opening the page…");
        try
        {
            var d = await Task.Run(() => OpenDoc(h));
            if (my != loadId) { d?.Dispose(); return; }
            doc = d;
            if (doc == null) { ShowText(h, m); return; }
            canvas.Visible = true; textView.Visible = false;
            pageOf.Text = $"of {doc.Pages:N0}";
            hitIndex = Math.Clamp(startHit, 0, hits.Count - 1);
            await GoHitAsync(hitIndex, true);
            LoadThumbs(my);
        }
        catch (Exception e)
        {
            if (my != loadId) return;
            Log.Error("Preview · " + h.Name, e);
            ShowText(h, m, "The page can't be drawn (" + e.Message + ") — here is the text that was found:\n\n");
        }
    }

    /// <summary>The file to draw: local, inside a zip, from Google Drive, or Word/PowerPoint through Office.</summary>
    IPreviewDoc OpenDoc(FileHit h)
    {
        var path = h.Path;
        var go = h.Pieces.FirstOrDefault()?.Goto ?? "";
        if (!string.IsNullOrEmpty(h.Remote))
        {
            var parts = h.Remote.Split('|');
            var ext = GDrive.ReadExt(new DriveFile { Name = h.Name, Mime = parts.Length > 1 ? parts[1] : "" });
            if (ext.Length == 0) ext = Path.GetExtension(h.Name);
            tempFile = Path.Combine(Paths.Cache, Guid.NewGuid().ToString("N") + ext);
            GDrive.Download(parts[0], parts.Length > 1 ? parts[1] : "", tempFile).GetAwaiter().GetResult();
            path = tempFile;
        }
        else if (go.StartsWith("zip:"))
        {
            var entry = go[4..].Split('|')[0];
            using var z = System.IO.Compression.ZipFile.OpenRead(path);
            var e = z.GetEntry(entry);
            if (e == null) return null;
            tempFile = Path.Combine(Paths.Cache, Guid.NewGuid().ToString("N") + Path.GetExtension(entry));
            System.IO.Compression.ZipFileExtensions.ExtractToFile(e, tempFile, true);
            path = tempFile;
        }
        var x = Path.GetExtension(path).ToLowerInvariant();
        if (x == ".pdf") return new PdfPreview(path);
        if (x is ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".bmp") return new ImagePreview(path);
        if (x is ".docx" or ".docm" or ".doc" or ".rtf" or ".odt" or ".pptx" or ".pptm" or ".ppt") return new PdfPreview(OfficePdf.Get(path));
        return null;
    }

    /// <summary>Excel, text, emails, CAD/BIM: the found places as text with the words in yellow.</summary>
    void ShowText(FileHit h, Matcher m, string intro = "")
    {
        canvas.Visible = false; textView.Visible = true;
        pageOf.Text = ""; pageBox.Text = "";
        hitLbl.Text = $"{h.Pieces.Sum(p => p.Matches?.Count ?? 0)} hits";
        textView.Clear();
        void Add(string s, Color c, bool bold = false, Color? back = null)
        {
            textView.SelectionStart = textView.TextLength;
            textView.SelectionColor = c;
            textView.SelectionFont = Th.F(bold ? 11 : 10.5f, bold);
            textView.SelectionBackColor = back ?? Th.Card;
            textView.AppendText(s);
        }
        if (intro.Length > 0) Add(intro, Th.Warn);
        foreach (var p in h.Pieces.Take(200))
        {
            Add("↳ " + FolderIndex.Where(p) + "\n", Th.AccentText, true);
            var text = p.Text ?? "";
            if (p.Goto.StartsWith("cell:")) text = text.Replace(" | ", "\n");
            int pos = 0;
            var found = m?.Find(text) ?? new();
            foreach (var (i, len) in found)
            {
                if (i < pos) continue;
                Add(text[pos..i], Th.Text);
                Add(text.Substring(i, len), Color.Black, true, Th.Yellow);
                pos = i + len;
            }
            Add(text[pos..] + "\n\n", Th.Text);
        }
        textView.SelectionStart = 0;
        textView.ScrollToCaret();
    }

    async void GoHit(int i) { if (doc != null && hits.Count > 0) await GoHitAsync(((i % hits.Count) + hits.Count) % hits.Count, false); }

    async Task GoHitAsync(int i, bool fit)
    {
        hitIndex = i;
        var (pg, k) = hits[i];
        hitLbl.Text = $"{i + 1} / {hits.Count}";
        await ShowPage(pg > 0 ? pg : 1, fit);
        var list = await GetMarks(page);
        // Word page numbers can differ by a page or two once printed: look at the neighbours.
        if (list.Count == 0 && hits[i].Page > 0)
            foreach (var d in new[] { 1, -1, 2, -2 })
            {
                var p2 = pg + d;
                if (p2 < 1 || p2 > doc.Pages) continue;
                var l2 = await GetMarks(p2);
                if (l2.Count > 0) { await ShowPage(p2, false); list = l2; break; }
            }
        canvas.SetMarks(list, Math.Min(k, Math.Max(0, list.Count - 1)));
        var r = CurrentMark();
        if (r != null) canvas.Reveal(r.Value);
        canvas.Focus();
    }

    RectangleF? CurrentMark() => canvas.Current;

    async Task<List<RectangleF>> GetMarks(int p)
    {
        lock (marks) if (marks.TryGetValue(p, out var known)) return known;
        var d = doc; var m = matcher;
        var l = await Task.Run(() => { try { return d.Marks(p, m); } catch { return new List<RectangleF>(); } });
        lock (marks) marks[p] = l;
        return l;
    }

    async void GoPage(int p)
    {
        if (doc == null) return;
        p = Math.Clamp(p, 1, doc.Pages);
        await ShowPage(p, false);
        canvas.SetMarks(await GetMarks(p), 0);
        var idx = hits.FindIndex(x => x.Page == p);
        if (idx >= 0) { hitIndex = idx; hitLbl.Text = $"{idx + 1} / {hits.Count}"; }
    }

    async Task ShowPage(int p, bool fit)
    {
        page = p;
        pageBox.Text = p.ToString();
        var d = doc;
        var size = await Task.Run(() => d.PageSize(p));
        canvas.SetPage(null, size, new());
        canvas.Message = "";
        if (fit || canvas.Zoom <= 0) canvas.Fit(true);
        else RenderCurrent();
    }

    int renderSeq;

    /// <summary>Draws the page sharp at the current zoom (called after zoom changes).</summary>
    async void RenderCurrent()
    {
        if (doc == null) return;
        var seq = ++renderSeq;
        var d = doc; var p = page;
        var width = (int)Math.Min(9000, canvas.PageSizeDip.Width * canvas.Zoom * DeviceDpi / 96.0);
        try
        {
            var bmp = await Task.Run(() => d.Render(p, Math.Max(200, width)));
            if (seq != renderSeq || p != page) { bmp.Dispose(); return; }
            canvas.SetBitmap(bmp);
        }
        catch (Exception e) { Log.Warn("Preview", "page render: " + e.Message); }
    }

    async void LoadThumbs(int my)
    {
        var pages = hits.Where(h => h.Page > 0).Select(h => h.Page).Distinct().Take(40).ToList();
        thumbs.Controls.Add(Th.L("PAGES WITH HITS", 7.5f, Th.Muted, true));
        foreach (var p in pages)
        {
            if (my != loadId) return;
            var d = doc; var m = matcher;
            Bitmap thumb = null;
            try
            {
                thumb = await Task.Run(() =>
                {
                    var b = d.Render(p, 80);
                    List<RectangleF> ms;
                    lock (marks) { if (!marks.TryGetValue(p, out ms)) { ms = d.Marks(p, m); marks[p] = ms; } }
                    using var g = Graphics.FromImage(b);
                    using var br = new SolidBrush(Color.FromArgb(170, 255, 205, 60));
                    foreach (var r in ms) g.FillRectangle(br, r.X * b.Width, r.Y * b.Height, Math.Max(3, r.Width * b.Width), Math.Max(3, r.Height * b.Height));
                    return b;
                });
            }
            catch { }
            if (my != loadId || thumb == null) { thumb?.Dispose(); return; }
            var pb = new PictureBox { Image = thumb, SizeMode = PictureBoxSizeMode.Zoom, Width = 80, Height = (int)(80.0 * thumb.Height / thumb.Width), Cursor = Cursors.Hand, Margin = new Padding(0, 4, 0, 0), BackColor = Color.White };
            var pp = p;
            pb.Click += (_, _) => { var idx = hits.FindIndex(x => x.Page == pp); if (idx >= 0) GoHit(idx); else GoPage(pp); };
            thumbs.Controls.Add(pb);
            thumbs.Controls.Add(Th.L(pp.ToString("N0"), 8.5f, Th.Text, true));
        }
    }

    void OpenHere()
    {
        if (hit == null) return;
        var piece = hit.Pieces.FirstOrDefault(p => PageOf(p.Goto) == page) ?? hit.Pieces.FirstOrDefault();
        var copy = piece == null ? null : new PieceHit { Loc = piece.Loc, Goto = piece.Goto.StartsWith("zip:") ? piece.Goto : (page > 0 && PageOf(piece.Goto) > 0 ? (piece.Goto.StartsWith("slide:") ? "slide:" : "page:") + page : piece.Goto), Text = piece.Text, Matches = piece.Matches };
        Opener.Open(hit, copy, matcher?.Query);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { doc?.Dispose(); if (tempFile != null) try { File.Delete(tempFile); } catch { } }
        base.Dispose(disposing);
    }
}

/// <summary>The drawn page: zoom, move, marks.</summary>
class PageCanvas : Control
{
    Bitmap bmp;
    public SizeF PageSizeDip = new(816, 1056);
    List<RectangleF> marks = new();
    int current;
    public double Zoom = 0;
    PointF offset = new(20, 20);
    Point? dragFrom;
    PointF dragOffset;
    public string Message = "";
    readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 160 };
    public event Action<double> ZoomChanged;
    public event Action PageNeeded;
    public event Action DoubleClicked;

    public PageCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(0x1A, 0x1D, 0x24);
        TabStop = true;
        Cursor = Cursors.Hand;
        renderTimer.Tick += (_, _) => { renderTimer.Stop(); PageNeeded?.Invoke(); };
    }

    public RectangleF? Current => marks.Count == 0 ? null : marks[Math.Clamp(current, 0, marks.Count - 1)];

    float PxScale => (float)(Zoom * DeviceDpi / 96.0);
    SizeF Drawn => new(PageSizeDip.Width * PxScale, PageSizeDip.Height * PxScale);

    public void SetPage(Bitmap b, SizeF size, List<RectangleF> m)
    {
        var old = bmp; bmp = b; old?.Dispose();
        PageSizeDip = size; marks = m; current = 0;
        Invalidate();
    }

    public void SetBitmap(Bitmap b) { var old = bmp; bmp = b; old?.Dispose(); Invalidate(); }
    public void SetMarks(List<RectangleF> m, int cur) { marks = m ?? new(); current = cur; Invalidate(); }

    public void SetZoom(double z, PointF? anchor = null)
    {
        z = Math.Clamp(z, 0.1, 8.0);
        var a = anchor ?? new PointF(Width / 2f, Height / 2f);
        if (Zoom > 0)
        {
            // keep the point under the mouse in place
            var px = (a.X - offset.X) / PxScale; var py = (a.Y - offset.Y) / PxScale;
            Zoom = z;
            offset = new PointF(a.X - px * PxScale, a.Y - py * PxScale);
        }
        else Zoom = z;
        Clamp();
        ZoomChanged?.Invoke(Zoom);
        Invalidate();
        renderTimer.Stop(); renderTimer.Start();
    }

    public void ZoomBy(double f) => SetZoom((Zoom <= 0 ? 1 : Zoom) * f);

    public void Fit(bool width)
    {
        if (PageSizeDip.Width <= 0) return;
        var dpi = DeviceDpi / 96.0;
        var zw = (Width - 40) / (PageSizeDip.Width * dpi);
        var zh = (Height - 40) / (PageSizeDip.Height * dpi);
        Zoom = Math.Clamp(width ? zw : Math.Min(zw, zh), 0.1, 8);
        offset = new PointF((float)Math.Max(20, (Width - Drawn.Width) / 2), 20);
        ZoomChanged?.Invoke(Zoom);
        Invalidate();
        renderTimer.Stop(); renderTimer.Start();
    }

    /// <summary>Zoom onto a mark (double-click).</summary>
    public void ZoomTo(RectangleF r, double z)
    {
        Zoom = Math.Clamp(z, 0.1, 8);
        Reveal(r, true);
        ZoomChanged?.Invoke(Zoom);
        renderTimer.Stop(); renderTimer.Start();
    }

    /// <summary>Scrolls so the mark is visible (centred).</summary>
    public void Reveal(RectangleF r, bool center = false)
    {
        var d = Drawn;
        var cx = r.X * d.Width + r.Width * d.Width / 2; var cy = r.Y * d.Height + r.Height * d.Height / 2;
        var sx = offset.X + cx; var sy = offset.Y + cy;
        if (center || sx < 30 || sx > Width - 30 || sy < 30 || sy > Height - 30)
            offset = new PointF(Width / 2f - cx, Height / 3f - cy);
        Clamp();
        Invalidate();
    }

    void Clamp()
    {
        var d = Drawn;
        float minX = Math.Min(20, Width - d.Width - 20), maxX = Math.Max(20, (Width - d.Width) / 2);
        float minY = Math.Min(20, Height - d.Height - 20), maxY = 20;
        if (d.Width + 40 <= Width) offset.X = (Width - d.Width) / 2; else offset.X = Math.Clamp(offset.X, minX, maxX);
        offset.Y = Math.Clamp(offset.Y, minY, maxY);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (Zoom > 0) { Clamp(); Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Message.Length > 0)
        {
            TextRenderer.DrawText(g, Message, Th.F(11), ClientRectangle, Th.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }
        if (Zoom <= 0) return;
        var d = Drawn;
        var page = new RectangleF(offset.X, offset.Y, d.Width, d.Height);
        using (var sh = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(sh, page.X + 4, page.Y + 4, page.Width, page.Height);
        g.FillRectangle(Brushes.White, page);
        if (bmp != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(bmp, page);
        }
        else TextRenderer.DrawText(g, "Drawing the page…", Th.F(10), Rectangle.Round(page), Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using var fill = new SolidBrush(Color.FromArgb(105, 255, 205, 40));
        using var edge = new Pen(Color.FromArgb(240, 180, 0), 1.5f);
        using var cur = new Pen(Color.FromArgb(255, 110, 0), 3f);
        for (int i = 0; i < marks.Count; i++)
        {
            var r = marks[i];
            var rr = new RectangleF(page.X + r.X * d.Width - 2, page.Y + r.Y * d.Height - 2, Math.Max(6, r.Width * d.Width + 4), Math.Max(6, r.Height * d.Height + 4));
            g.FillRectangle(fill, rr);
            g.DrawRectangle(i == current ? cur : edge, rr.X, rr.Y, rr.Width, rr.Height);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (e.Button == MouseButtons.Left) { dragFrom = e.Location; dragOffset = offset; Cursor = Cursors.SizeAll; } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragFrom = null; Cursor = Cursors.Hand; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragFrom == null) return;
        offset = new PointF(dragOffset.X + e.X - dragFrom.Value.X, dragOffset.Y + e.Y - dragFrom.Value.Y);
        Clamp();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0) { SetZoom(Zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), e.Location); return; }
        if ((ModifierKeys & Keys.Shift) != 0) offset.X += e.Delta / 2f;
        else offset.Y += e.Delta / 2f;
        Clamp();
        Invalidate();
    }

    protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); DoubleClicked?.Invoke(); }

    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown || base.IsInputKey(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.KeyCode == Keys.Down) { offset.Y -= 60; Clamp(); Invalidate(); }
        if (e.KeyCode == Keys.Up) { offset.Y += 60; Clamp(); Invalidate(); }
    }

    protected override void Dispose(bool disposing) { if (disposing) { bmp?.Dispose(); renderTimer.Dispose(); } base.Dispose(disposing); }
}

/// <summary>Full screen or separate window with the same preview.</summary>
class PreviewWindow : NovaForm
{
    public PreviewWindow(FileHit h, Matcher m, int hitIndex, bool fullScreen)
    {
        Text = "Nova Enhance · " + h.Name;
        Width = 1100; Height = 850;
        KeyPreview = true;
        var pane = new PreviewPane { Dock = DockStyle.Fill, IsFullScreen = fullScreen };
        Controls.Add(pane);
        if (fullScreen) { FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Maximized; TopMost = true; }
        pane.WindowRequested += full =>
        {
            if (full && !fullScreen) { FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Normal; WindowState = FormWindowState.Maximized; fullScreen = true; }
            else if (fullScreen) { FormBorderStyle = FormBorderStyle.Sizable; WindowState = FormWindowState.Normal; TopMost = false; fullScreen = false; }
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { if (fullScreen && !pane.IsFullScreen) { } Close(); } };
        Shown += (_, _) => pane.Show(h, m, hitIndex);
    }
}
