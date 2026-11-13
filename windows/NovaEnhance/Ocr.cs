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

    // ------------------------------------------------------------------ 🛡 quarantine: Windows OCR runs in a helper process

    /// <summary>True inside the helper ("NovaEnhance.exe --ocr …"): OCR runs here directly.</summary>
    static readonly bool InHelper = Environment.GetCommandLineArgs().Contains("--ocr");
    static string BadPath => Path.Combine(Paths.Logs, "ocr-bad.txt");
    static readonly object BadLock = new();

    /// <summary>Files Windows OCR crashed on: never read with Windows OCR again.</summary>
    public static bool IsBad(string path)
    {
        try { lock (BadLock) return File.Exists(BadPath) && File.ReadAllLines(BadPath).Any(l => l.Equals(path, StringComparison.OrdinalIgnoreCase)); } catch { return false; }
    }
    public static void MarkBad(string path, string why)
    {
        try { lock (BadLock) File.AppendAllLines(BadPath, new[] { path }); } catch { }
        Log.Warn("OCR", $"⚠ Windows OCR crashed on “{path}” ({why}) — Nova skips it from now on (Gemini OCR can still read it if you allow it)");
    }

    /// <summary>Runs the OCR in a helper process: if Windows OCR crashes, only the helper dies and the file is skipped from then on.</summary>
    static List<(int, string)> Isolated(string kind, string path, List<int> only, int maxPages)
    {
        var res = new List<(int, string)>();
        if (IsBad(path)) return res;
        var outFile = Path.Combine(Path.GetTempPath(), "nova-ocr-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath)
            { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "--ocr", kind, path, only == null ? "all" : string.Join(",", only), maxPages.ToString(), outFile }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            try { p.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }
            if (!p.WaitForExit(10 * 60 * 1000)) { try { p.Kill(true); } catch { } MarkBad(path, "no answer after 10 minutes"); return res; }
            if (p.ExitCode != 0 || !File.Exists(outFile)) { MarkBad(path, $"exit code {p.ExitCode}"); return res; }
            foreach (var x in System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(outFile))!.AsArray())
                res.Add(((int)x!["p"]!, x["t"]?.ToString() ?? ""));
        }
        catch (Exception e) { Log.Write($"OCR helper {Path.GetFileName(path)}: {e.Message}"); }
        finally { try { File.Delete(outFile); } catch { } }
        return res;
    }

    /// <summary>"NovaEnhance.exe --ocr image|pdf &lt;path&gt; all|1,2,3 &lt;maxPages&gt; &lt;out.json&gt;" — the helper itself.</summary>
    public static int Helper(string[] args)
    {
        var i = Array.IndexOf(args, "--ocr");
        if (i < 0 || args.Length < i + 6) return 2;
        string kind = args[i + 1], path = args[i + 2], pages = args[i + 3], outFile = args[i + 5];
        int.TryParse(args[i + 4], out var max);
        var only = pages == "all" ? null : pages.Split(',').Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
        var r = kind == "image" ? new List<(int, string)> { (0, ImageHere(path)) } : PdfHere(path, only, max);
        var arr = new System.Text.Json.Nodes.JsonArray(r.Select(x => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["p"] = x.Item1, ["t"] = x.Item2 }).ToArray());
        File.WriteAllText(outFile, arr.ToJsonString());
        return 0;
    }

    public static string Image(string path) => InHelper ? ImageHere(path) : Isolated("image", path, null, 0).FirstOrDefault().Item2 ?? "";

    public static List<(int, string)> Pdf(string path, List<int> only, int maxPages) => InHelper ? PdfHere(path, only, maxPages) : Isolated("pdf", path, only, maxPages);

    static string ImageHere(string path)
    {
        if (Engines().Count == 0) return "";
        try
        {
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(LongPath.Usable(path)).AsTask().Result;
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
    static List<(int, string)> PdfHere(string path, List<int> only, int maxPages)
    {
        var res = new List<(int, string)>();
        if (Engines().Count == 0) return res;
        try
        {
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(LongPath.Usable(path)).AsTask().Result;
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
        var file = Windows.Storage.StorageFile.GetFileFromPathAsync(LongPath.Usable(path)).AsTask().Result;
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
