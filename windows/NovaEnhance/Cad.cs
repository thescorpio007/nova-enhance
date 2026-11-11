using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 📐 CAD in a conversation: a DWG is turned into DXF on this PC (the free ODA File Converter, or AutoCAD's console when installed),
/// then Nova reads its texts, dimensions, layers and blocks, and draws it as a picture so the AI can LOOK at it like a PDF.
/// DXF files are read directly. Designed by A.HAMROUNI
/// </summary>
static class Cad
{
    public static string Converter()
    {
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(x => x.Length > 0))
        {
            foreach (var root in new[] { Path.Combine(pf, "ODA"), Path.Combine(pf, "Open Design Alliance") })
                try { if (Directory.Exists(root)) { var f = Directory.EnumerateFiles(root, "ODAFileConverter.exe", SearchOption.AllDirectories).OrderByDescending(x => x).FirstOrDefault(); if (f != null) return f; } } catch { }
            try
            {
                var ad = Path.Combine(pf, "Autodesk");
                if (Directory.Exists(ad))
                {
                    var f = Directory.EnumerateDirectories(ad, "AutoCAD*").OrderByDescending(x => x).Select(d => Path.Combine(d, "accoreconsole.exe")).FirstOrDefault(File.Exists);
                    if (f != null) return f;
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>The DXF of a drawing (the file itself for a .dxf), or null when no converter is installed.</summary>
    public static string ToDxf(string path, CancellationToken ct)
    {
        if (path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)) return path;
        var conv = Converter();
        if (conv == null) return null;
        var fi = new FileInfo(path);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant() + "|" + fi.LastWriteTimeUtc.Ticks)))[..16];
        var dir = Path.Combine(Paths.Cache, "cad", key);
        var outDxf = Path.Combine(dir, "out", Path.GetFileNameWithoutExtension(path) + ".dxf");
        if (File.Exists(outDxf)) return outDxf;
        Directory.CreateDirectory(Path.Combine(dir, "in")); Directory.CreateDirectory(Path.Combine(dir, "out"));
        var inDwg = Path.Combine(dir, "in", Path.GetFileName(path));
        File.Copy(LongPath.Usable(path), inDwg, true);
        ProcessStartInfo psi;
        if (conv.EndsWith("accoreconsole.exe", StringComparison.OrdinalIgnoreCase))
        {
            var scr = Path.Combine(dir, "out.scr");
            File.WriteAllText(scr, "FILEDIA\n0\n_.DXFOUT\n\"" + outDxf + "\"\n16\n");
            psi = new ProcessStartInfo(conv, $"/i \"{inDwg}\" /s \"{scr}\" /l en-US");
        }
        else psi = new ProcessStartInfo(conv, $"\"{Path.Combine(dir, "in")}\" \"{Path.Combine(dir, "out")}\" \"ACAD2018\" \"DXF\" \"0\" \"1\" \"{Path.GetFileName(path)}\"");
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WindowStyle = ProcessWindowStyle.Hidden;
        using var p = Process.Start(psi);
        var sw = Stopwatch.StartNew();
        while (!p.WaitForExit(500))
        {
            if (ct.IsCancellationRequested || sw.Elapsed > TimeSpan.FromMinutes(3)) { try { p.Kill(true); } catch { } ct.ThrowIfCancellationRequested(); return null; }
        }
        return File.Exists(outDxf) ? outDxf : Directory.EnumerateFiles(Path.Combine(dir, "out"), "*.dxf").FirstOrDefault();
    }

    // ------------------------------------------------------------------ reading the DXF

    class Ent { public string Type = ""; public List<(int C, string V)> G = new(); public string Get(int c) => G.FirstOrDefault(x => x.C == c).V ?? ""; public double D(int c) => double.TryParse(Get(c), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0; }

    public class Drawing
    {
        public List<string> Texts = new(), Layers = new(), Blocks = new();
        public byte[] Png;
        public int Entities;
    }

    public static Drawing Read(string dxf, int maxSide = 2200)
    {
        var lines = File.ReadAllLines(dxf, Encoding.Latin1);
        var blocks = new Dictionary<string, (double X, double Y, List<Ent> E)>(StringComparer.OrdinalIgnoreCase);
        var ents = new List<Ent>();
        string section = "", curBlock = null; double bx = 0, by = 0; List<Ent> bl = null;
        Ent cur = null;
        void Close() { if (cur == null) return; if (curBlock != null) bl?.Add(cur); else if (section == "ENTITIES") ents.Add(cur); cur = null; }
        for (int i = 0; i + 1 < lines.Length; i += 2)
        {
            if (!int.TryParse(lines[i].Trim(), out var code)) continue;
            var v = lines[i + 1].TrimEnd('\r');
            if (code == 0)
            {
                Close();
                var t = v.Trim();
                if (t == "SECTION") { section = ""; continue; }
                if (t == "ENDSEC") { section = ""; continue; }
                if (section == "BLOCKS" && t == "BLOCK") { curBlock = ""; bl = new(); cur = new Ent { Type = "BLOCK" }; continue; }
                if (section == "BLOCKS" && t == "ENDBLK")
                {
                    if (bl != null && bl.Count > 0 && bl[0].Type == "BLOCK") { var hdr = bl[0]; blocks[hdr.Get(2)] = (hdr.D(10), hdr.D(20), bl.Skip(1).ToList()); }
                    curBlock = null; bl = null; continue;
                }
                if (section is "ENTITIES" or "BLOCKS") cur = new Ent { Type = t };
                continue;
            }
            if (code == 2 && section == "" && cur == null) { section = v.Trim(); continue; }
            cur?.G.Add((code, v));
        }
        Close();
        var d = new Drawing();
        // Texts, layers, blocks used.
        var texts = new List<string>();
        void Collect(IEnumerable<Ent> list, int depth)
        {
            foreach (var e in list)
            {
                if (e.Type is "TEXT" or "ATTRIB" or "ATTDEF") texts.Add(e.Get(1));
                else if (e.Type == "MTEXT") texts.Add(MText(string.Concat(e.G.Where(g => g.C is 3 or 1).Select(g => g.V))));
                else if (e.Type == "DIMENSION") { var t = e.Get(1); var m = e.D(42); texts.Add(t.Length > 0 && t != "<>" ? t.Replace("<>", m.ToString("0.###", CultureInfo.InvariantCulture)) : m > 0 ? m.ToString("0.###", CultureInfo.InvariantCulture) : ""); }
                else if (e.Type == "INSERT" && depth < 3 && blocks.TryGetValue(e.Get(2), out var b)) { d.Blocks.Add(e.Get(2)); Collect(b.E, depth + 1); }
            }
        }
        Collect(ents, 0);
        d.Texts = texts.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Take(3000).ToList();
        d.Layers = ents.Select(e => e.Get(8)).Where(x => x.Length > 0).Distinct().OrderBy(x => x).Take(300).ToList();
        d.Blocks = d.Blocks.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}").Take(80).ToList();
        d.Entities = ents.Count;
        d.Png = Render(ents, blocks, maxSide);
        return d;
    }

    static string MText(string s)
    {
        s = Regex.Replace(s, @"\\P", "\n");
        s = Regex.Replace(s, @"\\[ACcFfHhLlOoKkQqTtWw][^;\\{}]*;?", "");
        s = Regex.Replace(s, @"[{}]", "");
        s = s.Replace("%%c", "Ø").Replace("%%d", "°").Replace("%%p", "±");
        return s.Trim();
    }

    // ------------------------------------------------------------------ drawing it

    readonly record struct Ln(PointF A, PointF B);

    static byte[] Render(List<Ent> ents, Dictionary<string, (double X, double Y, List<Ent> E)> blocks, int maxSide)
    {
        var segs = new List<Ln>();
        var labels = new List<(PointF P, string T, float H)>();
        void Walk(IEnumerable<Ent> list, Matrix m, int depth)
        {
            foreach (var e in list)
            {
                if (segs.Count > 600_000) return;
                PointF P(double x, double y) { var a = new[] { new PointF((float)x, (float)y) }; m.TransformPoints(a); return a[0]; }
                switch (e.Type)
                {
                    case "LINE": segs.Add(new Ln(P(e.D(10), e.D(20)), P(e.D(11), e.D(21)))); break;
                    case "LWPOLYLINE":
                    {
                        var xs = e.G.Where(g => g.C == 10).Select(g => double.Parse(g.V, CultureInfo.InvariantCulture)).ToList();
                        var ys = e.G.Where(g => g.C == 20).Select(g => double.Parse(g.V, CultureInfo.InvariantCulture)).ToList();
                        var n = Math.Min(xs.Count, ys.Count);
                        for (int i = 1; i < n; i++) segs.Add(new Ln(P(xs[i - 1], ys[i - 1]), P(xs[i], ys[i])));
                        if ((int)e.D(70) % 2 == 1 && n > 2) segs.Add(new Ln(P(xs[n - 1], ys[n - 1]), P(xs[0], ys[0])));
                        break;
                    }
                    case "CIRCLE": case "ARC":
                    {
                        double cx = e.D(10), cy = e.D(20), r = e.D(40), a0 = e.Type == "ARC" ? e.D(50) : 0, a1 = e.Type == "ARC" ? e.D(51) : 360;
                        if (a1 < a0) a1 += 360;
                        int steps = Math.Clamp((int)((a1 - a0) / 10), 6, 36);
                        for (int i = 0; i < steps; i++)
                        {
                            double t0 = (a0 + (a1 - a0) * i / steps) * Math.PI / 180, t1 = (a0 + (a1 - a0) * (i + 1) / steps) * Math.PI / 180;
                            segs.Add(new Ln(P(cx + r * Math.Cos(t0), cy + r * Math.Sin(t0)), P(cx + r * Math.Cos(t1), cy + r * Math.Sin(t1))));
                        }
                        break;
                    }
                    case "TEXT": case "MTEXT": case "ATTRIB":
                    {
                        var t = e.Type == "MTEXT" ? MText(string.Concat(e.G.Where(g => g.C is 3 or 1).Select(g => g.V))) : e.Get(1);
                        if (t.Length > 0) { var p = P(e.D(10), e.D(20)); var h = P(e.D(10), e.D(20) + Math.Max(e.D(40), 0.1)); labels.Add((p, t, Math.Abs(h.Y - p.Y) + Math.Abs(h.X - p.X))); }
                        break;
                    }
                    case "INSERT":
                        if (depth < 4 && blocks.TryGetValue(e.Get(2), out var b))
                        {
                            var sx = e.Get(41).Length > 0 ? e.D(41) : 1; var sy = e.Get(42).Length > 0 ? e.D(42) : 1;
                            var mm = m.Clone();
                            mm.Translate((float)e.D(10), (float)e.D(20));
                            mm.Rotate((float)e.D(50));
                            mm.Scale((float)sx, (float)sy);
                            mm.Translate((float)-b.X, (float)-b.Y);
                            Walk(b.E, mm, depth + 1);
                        }
                        break;
                }
            }
        }
        Walk(ents, new Matrix(), 0);
        var pts = segs.SelectMany(s => new[] { s.A, s.B }).Concat(labels.Select(l => l.P)).Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y)).ToList();
        if (pts.Count < 2) return null;
        // Bounds without the far-away strays (1 % each side).
        var xs2 = pts.Select(p => p.X).OrderBy(x => x).ToList(); var ys2 = pts.Select(p => p.Y).OrderBy(y => y).ToList();
        int lo = (int)(xs2.Count * 0.005), hi = Math.Max(lo, xs2.Count - 1 - lo);
        float minX = xs2[lo], maxX = xs2[hi], minY = ys2[lo], maxY = ys2[hi];
        float w = Math.Max(maxX - minX, 1e-6f), h = Math.Max(maxY - minY, 1e-6f);
        float sc = (maxSide - 40) / Math.Max(w, h);
        int W = (int)(w * sc) + 40, H = (int)(h * sc) + 40;
        using var bmp = new Bitmap(Math.Max(W, 200), Math.Max(H, 200));
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PointF S(PointF p) => new(20 + (p.X - minX) * sc, H - 20 - (p.Y - minY) * sc);
            using var pen = new Pen(Color.Black, 1f);
            foreach (var s in segs) { var a = S(s.A); var b = S(s.B); if (Math.Abs(a.X) < 1e6 && Math.Abs(b.X) < 1e6) g.DrawLine(pen, a, b); }
            foreach (var (p, t, hh) in labels.Take(4000))
            {
                var px = Math.Clamp(hh * sc, 5f, 40f);
                using var f = new Font("Arial", px, GraphicsUnit.Pixel);
                var q = S(p);
                g.DrawString(t.Length > 120 ? t[..120] : t, f, Brushes.DarkBlue, q.X, q.Y - px);
            }
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

/// <summary>🎙 Voice notes and videos: the sound is taken on this PC (Windows Media Foundation), then transcribed (Gemini), 8 minutes at a time.</summary>
static class Media
{
    public static readonly string[] Ext = { ".mp3", ".m4a", ".wav", ".aac", ".wma", ".amr", ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".3gp", ".m4v" };
    public static bool Is(string path) => Ext.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

    public static TimeSpan Length(string path)
    {
        try { using var r = new NAudio.Wave.MediaFoundationReader(path); return r.TotalTime; } catch { return TimeSpan.Zero; }
    }

    public static async Task<string> Transcribe(string path, Action<string> status, CancellationToken ct)
    {
        var chunks = new List<byte[]>();
        await Task.Run(() =>
        {
            using var reader = new NAudio.Wave.MediaFoundationReader(path);
            using var res = new NAudio.Wave.MediaFoundationResampler(reader, new NAudio.Wave.WaveFormat(16000, 16, 1)) { ResamplerQuality = 40 };
            var per = 16000 * 2 * 60 * 8;   // 8 minutes of 16 kHz mono
            var buf = new byte[32000];
            MemoryStream ms = null; NAudio.Wave.WaveFileWriter w = null; int inChunk = 0;
            void Start() { ms = new MemoryStream(); w = new NAudio.Wave.WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(ms), res.WaveFormat); inChunk = 0; }
            void End() { if (w == null) return; w.Flush(); w.Dispose(); if (inChunk > 16000) chunks.Add(ms.ToArray()); w = null; }
            Start();
            int n;
            while ((n = res.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                w.Write(buf, 0, n); inChunk += n;
                if (inChunk >= per) { End(); Start(); }
                if (chunks.Count >= 8) break;   // 64 minutes at most
            }
            End();
        }, ct);
        var sb = new StringBuilder();
        for (int i = 0; i < chunks.Count; i++)
        {
            status?.Invoke($"🎙 Transcribing {Path.GetFileName(path)} · part {i + 1} of {chunks.Count}…");
            var t = await Ai.Transcribe(chunks[i], ct);
            sb.Append($"[{i * 8:00}:00] ").Append(t.Trim()).Append("\n\n");
        }
        return sb.ToString().Trim();
    }
}
