using System.Drawing.Drawing2D;

namespace NovaEnhance;

/// <summary>
/// The email index in one line: 62% · 48,210 of 77,900 · about 25 min left · Archive.pst › Projects · gentle / ⚡ full speed.
/// ⚡ Index now asks "until complete" or "for 1 hour"; Outlook-busy pauses still apply. Designed by A.HAMROUNI
/// </summary>
static class IndexProgress
{
    public class Snap
    {
        public long Done, Total;
        public int Percent;
        public TimeSpan? Left;
        public bool Complete, Waiting;
        public string Where = "", Mode = "";
        public double Rate;
    }

    public static volatile Snap Now = new() { Waiting = true };
    static readonly Queue<(DateTime At, long Done)> samples = new();
    static System.Threading.Timer timer;
    static int busy;

    // ------------------------------------------------------------------ ⚡ full speed
    static DateTime rushUntil = DateTime.MinValue;
    static bool rushToComplete;
    public static bool Rushing => rushToComplete || DateTime.Now < rushUntil;

    public static string RushText => rushToComplete ? "⚡ full speed until complete" : Rushing ? $"⚡ full speed until {rushUntil:HH:mm}" : "";

    public static void Rush(bool untilComplete)
    {
        rushToComplete = untilComplete;
        rushUntil = untilComplete ? DateTime.MinValue : DateTime.Now.AddHours(1);
        samples.Clear();
        MailIndex.Now();
    }

    public static void StopRush()
    {
        rushToComplete = false; rushUntil = DateTime.MinValue;
        samples.Clear();
    }

    /// <summary>Called when a pass ends: "until complete" ends by itself once everything is indexed.</summary>
    public static void PassEnded()
    {
        if (rushToComplete && MailIndex.Complete && !MailIndex.Deferred) rushToComplete = false;
    }

    /// <summary>Emails per second at full speed (learned; 20/s until Nova has measured it).</summary>
    static double FullRate { get => Math.Max(2, Store.Int("win_index_rate", 20)); set => Store.SetInt("win_index_rate", (int)Math.Round(value)); }

    public static void Start()
    {
        timer ??= new System.Threading.Timer(_ => Measure(), null, 1500, 4000);
    }

    static void Measure()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            var (done, total) = MailIndex.Totals();
            var s = new Snap { Done = done, Total = Math.Max(total, done) };
            s.Complete = MailIndex.Complete && !MailIndex.Deferred && !Rushing;
            s.Percent = s.Total == 0 ? 0 : (int)Math.Min(s.Complete ? 100 : 99, Math.Floor(100.0 * done / s.Total));
            if (s.Complete) s.Percent = 100;
            s.Waiting = total == 0;
            var st = MailIndex.Status ?? "";
            s.Where = st.StartsWith("Checking ") ? st[9..].TrimEnd('…', '.') : st.StartsWith("Indexing ") ? st[9..Math.Max(9, st.IndexOf('…') < 0 ? st.Length : st.IndexOf('…'))] : "";
            s.Mode = Rushing ? RushText : Gentle.On && Gentle.WorkingNow ? "gentle (working hours)" : Gentle.On ? "gentle" : "";

            var now = DateTime.Now;
            lock (samples)
            {
                samples.Enqueue((now, done));
                while (samples.Count > 2 && now - samples.Peek().At > TimeSpan.FromMinutes(5)) samples.Dequeue();
                var first = samples.Peek();
                var secs = (now - first.At).TotalSeconds;
                if (secs >= 40 && done > first.Done) s.Rate = (done - first.Done) / secs;
            }
            if (Rushing && s.Rate > 0 && (now - samples.Peek().At).TotalSeconds > 90) FullRate = Math.Max(FullRate * 0.5, s.Rate);
            var left = Math.Max(0, s.Total - done);
            if (!s.Complete && left > 0 && s.Rate > 0.05) s.Left = TimeSpan.FromSeconds(Math.Min(left / s.Rate, TimeSpan.FromDays(6).TotalSeconds));
            Now = s;
        }
        catch { }
        finally { busy = 0; }
    }

    public static string Time(TimeSpan t) =>
        t.TotalMinutes < 1.5 ? "about 1 min" : t.TotalHours < 1 ? $"about {Math.Round(t.TotalMinutes)} min" :
        t.TotalHours < 24 ? $"about {(int)t.TotalHours} h {t.Minutes:00} min" : $"about {Math.Round(t.TotalDays, 1)} days";

    /// <summary>"62% · 48,210 of 77,900 · about 25 min left · Archive.pst › Projects · gentle"</summary>
    public static string Line(bool withWhere = true)
    {
        var s = Now;
        if (s.Waiting) return MailIndex.Status;
        if (s.Complete) return $"✓ Email index complete · {s.Done:N0} emails";
        var parts = new List<string> { $"{s.Percent}%", $"{s.Done:N0} of {s.Total:N0}" };
        parts.Add(s.Left is TimeSpan l ? Time(l) + " left" : "measuring time left…");
        if (withWhere && s.Where.Length > 0) parts.Add(s.Where);
        if (s.Mode.Length > 0) parts.Add(s.Mode);
        return string.Join(" · ", parts);
    }

    public static string Tray()
    {
        var s = Now;
        if (s.Waiting || s.Complete) return "Nova Enhance";
        var t = $"Nova Enhance\n⏳ Email index {s.Percent}%" + (s.Left is TimeSpan l ? " · " + Time(l) + " left" : "") + (Rushing ? " · ⚡ full speed" : "");
        return t.Length > 127 ? t[..127] : t;
    }

    /// <summary>⚡ Index now: until complete, or for 1 hour.</summary>
    public static void Ask(IWin32Window owner)
    {
        var s = Now;
        var left = Math.Max(0, s.Total - s.Done);
        var full = TimeSpan.FromSeconds(left / FullRate);
        var gentle = s.Rate > 0.05 && !Rushing ? TimeSpan.FromSeconds(left / s.Rate) : (TimeSpan?)null;
        using var f = new Form
        {
            Text = "⚡ Index now", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent, BackColor = Th.Bg, ForeColor = Th.Text,
            Font = Th.F(10), ClientSize = new Size(520, 250), Padding = new Padding(16), Icon = Th.AppIcon
        };
        var st = new Stack { Dock = DockStyle.Fill };
        f.Controls.Add(st);
        st.Add(Th.L("⚡ Index now at full speed", 13, Th.Text, true));
        st.Add(Th.Wrap(s.Complete ? "The index is complete — Nova checks for new mail now." :
            left > 0 ? $"{left:N0} emails left · {Time(full)} at full speed" + (gentle is TimeSpan g ? $" ({Time(g)} gently)" : "") + ". Nova still pauses for a moment whenever Outlook is busy."
                     : "Nova checks every mailbox and archive now.", 9.5f, Th.Muted), true);
        var a = new RadioButton { Text = "Until the index is complete — then gentle again by itself", Checked = true, AutoSize = true, ForeColor = Th.Text, Margin = new Padding(0, 8, 0, 2) };
        var b = new RadioButton { Text = $"For 1 hour — gentle again at {DateTime.Now.AddHours(1):HH:mm}", AutoSize = true, ForeColor = Th.Text, Margin = new Padding(0, 2, 0, 8) };
        st.Add(a); st.Add(b);
        var row = st.Add(Th.Row(false));
        bool go = false;
        row.Controls.Add(Th.B("⚡ Start", () => { go = true; f.Close(); }, true));
        row.Controls.Add(Th.B("Cancel", f.Close));
        f.ShowDialog(owner);
        if (!go) return;
        Rush(a.Checked);
    }
}

/// <summary>The progress line: bar · text · ⚡ Index now (or ■ Stop while at full speed). Hidden when the index is complete.</summary>
/// <summary>The "● ● ●" that moves while Nova is working (like Claude's typing dots). Stops (and hides) when the work is done.</summary>
class WorkDots : Control
{
    readonly System.Windows.Forms.Timer t;
    int frame;
    bool on;
    public Color Dot = Color.FromArgb(0x9F, 0x94, 0xFF);

    public WorkDots()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent; Size = new Size(34, 14); Margin = new Padding(4, 4, 4, 0);
        t = new System.Windows.Forms.Timer { Interval = 380 };
        t.Tick += (_, _) => { frame = (frame + 1) % 3; Invalidate(); };
        Disposed += (_, _) => t.Dispose();
    }

    public bool On
    {
        get => on;
        set { if (on == value) return; on = value; if (on) t.Start(); else t.Stop(); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!on) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < 3; i++)
        {
            var big = i == frame;
            var d = big ? 8 : 6;
            using var b = new SolidBrush(big ? Dot : Color.FromArgb(110, Dot));
            g.FillEllipse(b, 2 + i * 11 + (8 - d) / 2, (Height - d) / 2, d, d);
        }
    }
}

class IndexLine : Panel
{
    readonly Label text;
    readonly Button go;
    readonly System.Windows.Forms.Timer t;
    readonly bool alwaysShow;
    readonly WorkDots dots;
    int percent;

    public IndexLine(bool alwaysShow = false)
    {
        this.alwaysShow = alwaysShow;
        Height = 30; BackColor = Color.FromArgb(0x0E, 0x11, 0x17); Padding = new Padding(10, 4, 8, 4);
        text = new Label { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(0xC9, 0xCE, 0xD8), Font = Th.F(9), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(250, 0, 0, 0), UseMnemonic = false, AutoEllipsis = true };
        // ● ● ● while Nova is indexing.
        dots = new WorkDots { Location = new Point(206, 8) };
        text.Controls.Add(dots);
        text.Paint += (_, e) => Bar(e.Graphics);
        go = Th.B("⚡ Index now", () => { if (IndexProgress.Rushing) IndexProgress.StopRush(); else IndexProgress.Ask(FindForm()); Refresh2(); });
        go.Dock = DockStyle.Right; go.Font = Th.F(9, true); go.ForeColor = Color.FromArgb(0xFF, 0xD2, 0x7A); go.AutoSize = true; go.Margin = new Padding(0);
        Controls.Add(text);
        Controls.Add(go);
        IndexProgress.Start();
        t = new System.Windows.Forms.Timer { Interval = 2000 };
        t.Tick += (_, _) => Refresh2();
        t.Start();
        Disposed += (_, _) => t.Dispose();
        Refresh2();
    }

    void Bar(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(8, text.Height / 2 - 4, 120, 8);
        using var back = new SolidBrush(Color.FromArgb(0x2A, 0x30, 0x40));
        using var fill = new SolidBrush(IndexProgress.Rushing ? Color.FromArgb(0xFF, 0xD2, 0x7A) : Color.FromArgb(0x7C, 0x6C, 0xFF));
        g.FillRectangle(back, r);
        if (percent > 0) g.FillRectangle(fill, new Rectangle(r.X, r.Y, Math.Max(2, r.Width * percent / 100), r.Height));
        // The percentage, big and clear (✓ when complete).
        var done = IndexProgress.Now.Complete;
        TextRenderer.DrawText(g, done ? "✓ 100 %" : $"{percent} %", Th.F(10.5f, true), new Point(r.Right + 8, text.Height / 2 - 10), done ? Color.FromArgb(0x8F, 0xD6, 0xB4) : Color.FromArgb(0xEC, 0xEE, 0xF3));
    }

    void Refresh2()
    {
        if (IsDisposed) return;
        var s = IndexProgress.Now;
        percent = s.Percent;
        Visible = alwaysShow || !s.Complete;
        text.Text = IndexProgress.Line();
        dots.On = !s.Complete && !s.Waiting;
        go.Text = IndexProgress.Rushing ? "■ Stop full speed" : "⚡ Index now";
        text.Invalidate();
    }
}

/// <summary>One clear progress row: title · ● ● ● while working · big % · full-width bar · details. Refreshes itself.</summary>
class ProgressRow : Panel
{
    readonly Label title, pct, detail;
    readonly WorkDots dots;
    readonly Panel back, fill;
    readonly Func<(string Title, int Percent, string Detail, bool Working)> read;
    readonly System.Windows.Forms.Timer t;

    public ProgressRow(Func<(string Title, int Percent, string Detail, bool Working)> read, int everyMs = 3000)
    {
        this.read = read;
        Height = 62; BackColor = Color.FromArgb(0x18, 0x1C, 0x24); Margin = new Padding(0, 3, 0, 3);
        title = new Label { AutoSize = true, Font = Th.F(10.5f, true), ForeColor = Th.Text, Location = new Point(12, 7), UseMnemonic = false };
        dots = new WorkDots();
        pct = new Label { AutoSize = true, Font = Th.F(12, true), ForeColor = Th.Text, UseMnemonic = false };
        back = new Panel { Height = 8, BackColor = Color.FromArgb(0x2A, 0x30, 0x40), Location = new Point(12, 32) };
        fill = new Panel { Height = 8, Width = 0, BackColor = Th.Accent, Dock = DockStyle.Left };
        back.Controls.Add(fill);
        detail = new Label { AutoSize = true, Font = Th.F(9), ForeColor = Th.Muted, Location = new Point(12, 43), UseMnemonic = false };
        Controls.AddRange(new Control[] { title, dots, pct, back, detail });
        t = new System.Windows.Forms.Timer { Interval = everyMs };
        t.Tick += (_, _) => Update2();
        t.Start();
        Disposed += (_, _) => t.Dispose();
        Resize += (_, _) => Lay();
        Update2();
    }

    void Lay()
    {
        dots.Location = new Point(title.Right + 6, title.Top + 4);
        pct.Location = new Point(Math.Max(dots.Right + 8, Width - pct.Width - 14), 4);
        back.Width = Math.Max(100, Width - 24);
    }

    void Update2()
    {
        if (IsDisposed) return;
        try
        {
            var (ti, p, de, w) = read();
            p = Math.Clamp(p, 0, 100);
            title.Text = ti;
            pct.Text = p >= 100 ? "✓ 100 %" : p + " %";
            pct.ForeColor = p >= 100 ? Th.Good : Th.Text;
            fill.BackColor = p >= 100 ? Th.Good : Th.Accent;
            detail.Text = de;
            dots.On = w && p < 100;
            Lay();
            fill.Width = (int)(back.Width * p / 100.0);
        }
        catch { }
    }
}
