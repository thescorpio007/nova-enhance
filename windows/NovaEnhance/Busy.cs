using System.Drawing.Drawing2D;

namespace NovaEnhance;

/// <summary>
/// ● ● ● What Nova is working on right now (any window): the tray icon gets an amber dot and its hover text says what and for how long;
/// the “Nova is thinking” cards ask it what Nova is waiting for (“Waiting for Gemini · 25 s”). Designed by A.HAMROUNI
/// </summary>
static class Busy
{
    sealed class Job : IDisposable
    {
        public string What; public DateTime Start = DateTime.Now; public bool Ai;
        public void Dispose() { lock (jobs) jobs.Remove(this); }
    }

    static readonly List<Job> jobs = new();

    /// <summary>using var _ = Busy.Begin("Enhancing your text"); — shown in the tray while it runs.</summary>
    public static IDisposable Begin(string what, bool ai = false)
    {
        var j = new Job { What = what, Ai = ai };
        lock (jobs) jobs.Add(j);
        return j;
    }

    public static bool Any { get { lock (jobs) return jobs.Count > 0; } }

    static string T(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00}" : $"{(int)t.TotalSeconds} s";

    /// <summary>What Nova waits for now — the newest AI call first (“Waiting for Gemini · 25 s”).</summary>
    public static string Line()
    {
        lock (jobs)
        {
            var j = jobs.LastOrDefault(x => x.Ai) ?? jobs.LastOrDefault();
            return j == null ? "" : $"{j.What} · {T(DateTime.Now - j.Start)}";
        }
    }

    /// <summary>For the tray: “working on 2 tasks · Waiting for Gemini · 0:14”.</summary>
    public static string Tray()
    {
        lock (jobs)
        {
            if (jobs.Count == 0) return "";
            var oldest = jobs.Min(x => x.Start);
            return $"⏱ {T(DateTime.Now - oldest)} · working on {jobs.Count} task{(jobs.Count > 1 ? "s" : "")} · {jobs[^1].What}";
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    static Icon timeIcon; static string timeText = "";

    /// <summary>Seconds since the oldest running task started (0 = nothing runs).</summary>
    public static int Seconds { get { lock (jobs) return jobs.Count == 0 ? 0 : (int)(DateTime.Now - jobs.Min(x => x.Start)).TotalSeconds; } }

    /// <summary>⏱ The Nova icon with the running time on it (“0:14”, “3m”), amber after 30 s.</summary>
    public static Icon TimeIcon(Icon baseIcon, int secs)
    {
        var text = secs < 600 ? $"{secs / 60}:{secs % 60:00}" : $"{secs / 60}m";
        if (timeIcon != null && text == timeText) return timeIcon;
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var ic = new Icon(baseIcon, 32, 32)) g.DrawIcon(ic, new Rectangle(0, 0, 32, 32));
                var r = new Rectangle(0, 17, 32, 15);
                using var back = new SolidBrush(secs > 30 ? Color.FromArgb(0xFB, 0xBF, 0x24) : Color.FromArgb(0x7C, 0x6C, 0xFF));
                g.FillRectangle(back, r);
                using var f = new Font("Segoe UI", text.Length > 4 ? 10f : 11f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var fore = new SolidBrush(secs > 30 ? Color.FromArgb(0x1A, 0x13, 0x00) : Color.White);
                g.DrawString(text, f, fore, r, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
            var old = timeIcon;
            timeIcon = Icon.FromHandle(bmp.GetHicon()); timeText = text;
            if (old != null) { var h = old.Handle; old.Dispose(); DestroyIcon(h); }
        }
        catch { return baseIcon; }
        return timeIcon;
    }

    static Icon dotIcon;
    /// <summary>The Nova icon with a small amber dot (while something runs).</summary>
    public static Icon DotIcon(Icon baseIcon)
    {
        if (dotIcon != null) return dotIcon;
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var ic = new Icon(baseIcon, 32, 32)) g.DrawIcon(ic, new Rectangle(0, 0, 32, 32));
                using var edge = new SolidBrush(Color.FromArgb(0x14, 0x14, 0x18));
                g.FillEllipse(edge, 17, 17, 15, 15);
                using var amber = new SolidBrush(Color.FromArgb(0xFB, 0xBF, 0x24));
                g.FillEllipse(amber, 19, 19, 11, 11);
            }
            dotIcon = Icon.FromHandle(bmp.GetHicon());
        }
        catch { dotIcon = baseIcon; }
        return dotIcon;
    }
}

/// <summary>
/// ⏱ For a window's status line: while its text ends with “…” (Nova's “in progress” lines: “Reading the PDF…”, “✦ Checking grammar…”),
/// a timer counts up in front of it (“⏱ 0:06  Reading the PDF…”), the line turns amber after 30 s, and after 20 quiet seconds it says
/// what Nova is waiting for (“still working, not stuck”). Nothing else in the window changes. Designed by A.HAMROUNI
/// </summary>
static class Thinking
{
    static readonly Color Amber = Color.FromArgb(0xFF, 0xD2, 0x7A);
    static readonly System.Text.RegularExpressions.Regex Mine = new(@"^⏱ \d+:\d\d  (.*?)(?: · (?:⏳|⚠)[^\n]*?(?:not stuck|still working))?(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);

    public static void Watch(Label l)
    {
        if (l == null) return;
        string baseText = l.Text; bool self = false;
        Color baseColor = l.ForeColor;
        DateTime started = DateTime.Now, changed = DateTime.Now;
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        bool Working(string s) => s.TrimEnd().EndsWith("…") || s.TrimEnd().EndsWith("...");
        void Draw()
        {
            var el = DateTime.Now - started;
            var quiet = (DateTime.Now - changed).TotalSeconds;
            var wait = quiet > 20 && Busy.Line() is { Length: > 0 } b ? $" · ⏳ {b} — still working, not stuck" : quiet > 60 ? " · ⚠ taking longer than usual — still working" : "";
            self = true;
            try
            {
                l.Text = $"⏱ {(int)el.TotalMinutes}:{el.Seconds:00}  {baseText}{wait}";
                l.ForeColor = el.TotalSeconds > 30 ? Amber : baseColor;
            }
            finally { self = false; }
        }
        void Stop() { t.Stop(); self = true; try { l.ForeColor = baseColor; } finally { self = false; } }
        t.Tick += (_, _) => { if (l.IsDisposed) { t.Dispose(); return; } Draw(); };
        l.ForeColorChanged += (_, _) => { if (!self) baseColor = l.ForeColor; };
        l.TextChanged += (_, _) =>
        {
            if (self) return;
            var now = l.Text ?? "";
            bool wasOn = t.Enabled;
            // “info.Text += …” read the line with the timer: keep only the words.
            var m = Mine.Match(now);
            if (m.Success) { now = m.Groups[1].Value + m.Groups[2].Value; self = true; try { l.Text = now; } finally { self = false; } }
            baseText = now; changed = DateTime.Now;
            if (Working(now))
            {
                if (!wasOn) { started = DateTime.Now; t.Start(); }
                Draw();
            }
            else if (wasOn) Stop();
        };
        l.Disposed += (_, _) => t.Dispose();
    }
}
