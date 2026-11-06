using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace NovaEnhance;

/// <summary>
/// Text recognition for scanned PDFs and images.
///  · On this PC (Windows OCR): big sheets are read in high-resolution tiles, with a second pass turned 90° for vertical text,
///    in every installed language among English, Arabic and French.
///  · Optional Gemini vision for unclear pages (Settings → Folder search → OCR), always after the user agrees.
/// </summary>
static class Ocr
{
    /// <summary>pc · pc+gemini · gemini</summary>
    public static string Mode { get => Store.Str("win_ocr_mode", "pc"); set => Store.Set("win_ocr_mode", value); }

    const int Tile = 2400, Overlap = 220, MaxTiles = 80;
    // Each reading thread gets its own engines: one engine used by several files at once can silently return nothing.
    [ThreadStatic] static List<OcrEngine> engines;
    static readonly object L = new();

    public static List<OcrEngine> Engines()
    {
        lock (L)
        {
            if (engines != null) return engines;
            engines = new();
            try
            {
                foreach (var two in new[] { "en", "ar", "fr" })
                {
                    var lang = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith(two, StringComparison.OrdinalIgnoreCase));
                    if (lang == null) continue;
                    var e = OcrEngine.TryCreateFromLanguage(lang);
                    if (e != null) engines.Add(e);
                }
                if (engines.Count == 0)
                {
                    var e = OcrEngine.TryCreateFromUserProfileLanguages();
                    if (e != null) engines.Add(e);
                }
            }
            catch (Exception ex) { Log.Write("OCR not available: " + ex.Message); }
            return engines;
        }
    }

    public static string Languages => Engines().Count == 0 ? "none installed" : string.Join(", ", Engines().Select(e => e.RecognizerLanguage.DisplayName));

    public static bool Has(string two)
    {
        try { return OcrEngine.AvailableRecognizerLanguages.Any(l => l.LanguageTag.StartsWith(two, StringComparison.OrdinalIgnoreCase)); } catch { return false; }
    }

    /// <summary>Too little text to trust (or "Gemini for every scanned page" is chosen).</summary>
    public static bool IsUnclear(string text) => Mode == "gemini" || (Mode == "pc+gemini" && (text ?? "").Count(char.IsLetterOrDigit) < 60);

    static void ReadInto(SoftwareBitmap bmp, List<string> lines, HashSet<string> seen)
    {
        foreach (var e in Engines())
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var r = e.RecognizeAsync(bmp).AsTask().Result;
                    foreach (var line in r.Lines)
                    {
                        var t = line.Text.Trim();
                        if (t.Length > 0 && seen.Add(t)) lines.Add(t);
                    }
                    break;
                }
                catch (Exception ex) { if (attempt == 1) Log.Write("OCR engine: " + ex.Message); else Thread.Sleep(150); }
            }
        }
    }

    /// <summary>Reads a decoded picture in tiles (and turned 90° when asked).</summary>
    static void ReadTiles(BitmapDecoder dec, bool rotated, List<string> lines, HashSet<string> seen)
    {
        double w = dec.PixelWidth, h = dec.PixelHeight;
        double scale = Math.Min(1.0, Math.Sqrt(140_000_000.0 / Math.Max(1, w * h)));
        uint sw = (uint)Math.Max(1, w * scale), sh = (uint)Math.Max(1, h * scale);
        uint W = rotated ? sh : sw, H = rotated ? sw : sh;
        int tiles = 0;
        for (uint y = 0; y < H && tiles < MaxTiles; y += Tile - Overlap)
            for (uint x = 0; x < W && tiles < MaxTiles; x += Tile - Overlap)
            {
                tiles++;
                var tr = new BitmapTransform
                {
                    ScaledWidth = sw, ScaledHeight = sh,
                    Rotation = rotated ? BitmapRotation.Clockwise90Degrees : BitmapRotation.None,
                    Bounds = new BitmapBounds { X = x, Y = y, Width = Math.Min((uint)Tile, W - x), Height = Math.Min((uint)Tile, H - y) }
                };
                using var bmp = dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tr,
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().Result;
                ReadInto(bmp, lines, seen);
                if (x + Tile >= W) break;
            }
    }

    public static string Image(string path)
    {
        if (Engines().Count == 0) return "";
        try
        {
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().Result;
            using var stream = file.OpenAsync(Windows.Storage.FileAccessMode.Read).AsTask().Result;
            var dec = BitmapDecoder.CreateAsync(stream).AsTask().Result;
            var lines = new List<string>(); var seen = new HashSet<string>();
            ReadTiles(dec, false, lines, seen);
            bool big = Math.Max(dec.PixelWidth, dec.PixelHeight) > 3000;
            if (big || lines.Sum(l => l.Length) < 40) ReadTiles(dec, true, lines, seen);
            return string.Join("\n", lines);
        }
        catch (Exception e) { Log.Write($"OCR {Path.GetFileName(path)}: {e.Message}"); return ""; }
    }

    /// <summary>OCR of PDF pages (all, or only the given ones): each part of the page is rendered at about 300 dpi. Returns (page, text).</summary>
    public static List<(int, string)> Pdf(string path, List<int> only, int maxPages)
    {
        var res = new List<(int, string)>();
        if (Engines().Count == 0) return res;
        try
        {
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().Result;
            var doc = Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask().Result;
            var pages = only ?? Enumerable.Range(1, (int)doc.PageCount).ToList();
            foreach (var p in pages.Take(maxPages))
            {
                if (p < 1 || p > doc.PageCount) continue;
                using var page = doc.GetPage((uint)(p - 1));
                double pw = page.Size.Width, ph = page.Size.Height;        // in 1/96 inch
                double scale = 300.0 / 96.0;                                 // ~300 dpi
                if (pw * ph * scale * scale > 140_000_000) scale = Math.Sqrt(140_000_000 / (pw * ph));
                bool big = Math.Max(pw, ph) > 1300;                          // A3 and larger: drawings
                var lines = new List<string>(); var seen = new HashSet<string>();
                double tileDip = Tile / scale, step = (Tile - Overlap) / scale;
                int tiles = 0;
                for (double y = 0; y < ph && tiles < MaxTiles; y += step)
                    for (double x = 0; x < pw && tiles < MaxTiles; x += step)
                    {
                        tiles++;
                        double tw = Math.Min(tileDip, pw - x), th = Math.Min(tileDip, ph - y);
                        using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                        page.RenderToStreamAsync(ras, new Windows.Data.Pdf.PdfPageRenderOptions
                        {
                            SourceRect = new Windows.Foundation.Rect(x, y, tw, th),
                            DestinationWidth = (uint)Math.Max(1, tw * scale), DestinationHeight = (uint)Math.Max(1, th * scale)
                        }).AsTask().Wait();
                        var dec = BitmapDecoder.CreateAsync(ras).AsTask().Result;
                        using (var bmp = dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask().Result)
                            ReadInto(bmp, lines, seen);
                        if (big || lines.Sum(l => l.Length) < 40)
                        {
                            var tr = new BitmapTransform { Rotation = BitmapRotation.Clockwise90Degrees };
                            using var rot = dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tr,
                                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().Result;
                            ReadInto(rot, lines, seen);
                        }
                        if (x + tileDip >= pw) break;
                    }
                res.Add((p, string.Join("\n", lines)));
            }
        }
        catch (Exception e) { Log.Write($"OCR {Path.GetFileName(path)}: {e.Message}"); }
        return res;
    }

    /// <summary>A page or image as a picture for Gemini vision (PNG/JPEG, at most ~3000 px wide).</summary>
    public static (byte[] Data, string Mime) Picture(string path, int page)
    {
        var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().Result;
        using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        if (page > 0)
        {
            var doc = Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask().Result;
            using var pg = doc.GetPage((uint)(page - 1));
            double pw = pg.Size.Width;
            uint width = (uint)Math.Clamp(pw * 2.5, 1200, 3200);
            pg.RenderToStreamAsync(ras, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = width }).AsTask().Wait();
            return (ToBytes(ras), "image/png");
        }
        var info = new FileInfo(path);
        var ext = info.Extension.ToLowerInvariant();
        if (info.Length < 6_000_000 && ext is ".jpg" or ".jpeg" or ".png") return (File.ReadAllBytes(path), ext == ".png" ? "image/png" : "image/jpeg");
        using var src = file.OpenAsync(Windows.Storage.FileAccessMode.Read).AsTask().Result;
        var dec = BitmapDecoder.CreateAsync(src).AsTask().Result;
        double f = Math.Min(1.0, 3200.0 / Math.Max(dec.PixelWidth, dec.PixelHeight));
        var tr = new BitmapTransform { ScaledWidth = (uint)(dec.PixelWidth * f), ScaledHeight = (uint)(dec.PixelHeight * f) };
        using var bmp = dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tr, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().Result;
        var enc = BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ras).AsTask().Result;
        enc.SetSoftwareBitmap(bmp);
        enc.FlushAsync().AsTask().Wait();
        return (ToBytes(ras), "image/jpeg");
    }

    static byte[] ToBytes(Windows.Storage.Streams.IRandomAccessStream ras)
    {
        ras.Seek(0);
        using var s = ras.AsStreamForRead();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
