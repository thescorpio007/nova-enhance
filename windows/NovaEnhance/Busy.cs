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
            return $"● Working on {jobs.Count} task{(jobs.Count > 1 ? "s" : "")} ({T(DateTime.Now - oldest)}) · {jobs[^1].What}";
        }
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
/// ● ● ● for a window's status line: while its text ends with “…” (Nova's “in progress” lines: “Reading the PDF…”, “✦ Checking grammar…”),
/// moving dots and a timer are added, and after 20 quiet seconds what Nova is waiting for (“still working, not stuck”).
/// Nothing else in the window changes. Designed by A.HAMROUNI
/// </summary>
static class Thinking
{
    static readonly string[] Frames = { "●∙∙", "∙●∙", "∙∙●" };

    public static void Watch(Label l)
    {
        if (l == null) return;
        string baseText = l.Text; bool self = false; int frame = 0;
        DateTime started = DateTime.Now, changed = DateTime.Now;
        var t = new System.Windows.Forms.Timer { Interval = 400 };
        bool Working(string s) => s.TrimEnd().EndsWith("…") || s.TrimEnd().EndsWith("...");
        void Draw()
        {
            var el = DateTime.Now - started;
            var quiet = (DateTime.Now - changed).TotalSeconds;
            var wait = quiet > 20 && Busy.Line() is { Length: > 0 } b ? $" · ⏳ {b} — still working, not stuck" : quiet > 60 ? " · ⚠ taking longer than usual — still working" : "";
            self = true;
            try { l.Text = $"{Frames[frame]}  {baseText}   {(int)el.TotalMinutes}:{el.Seconds:00}{wait}"; }
            finally { self = false; }
        }
        t.Tick += (_, _) => { if (l.IsDisposed) { t.Dispose(); return; } frame = (frame + 1) % 3; Draw(); };
        l.TextChanged += (_, _) =>
        {
            if (self) return;
            var now = l.Text ?? "";
            bool wasOn = t.Enabled;
            // “info.Text += …” read the line with the dots: keep only the words.
            var m = System.Text.RegularExpressions.Regex.Match(now, @"^[●∙]{3}  (.*?)   \d+:\d\d(?: · (?:⏳|⚠).*?(?:not stuck|still working))?(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (m.Success) { now = m.Groups[1].Value + m.Groups[2].Value; self = true; try { l.Text = now; } finally { self = false; } }
            baseText = now; changed = DateTime.Now;
            if (Working(now))
            {
                if (!wasOn) { started = DateTime.Now; frame = 0; t.Start(); }
                Draw();
            }
            else if (wasOn) t.Stop();
        };
        l.Disposed += (_, _) => t.Dispose();
    }
}
