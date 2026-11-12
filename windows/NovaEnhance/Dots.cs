using System.Drawing.Drawing2D;

namespace NovaEnhance;

/// <summary>
/// ● ○ ○ → ○ ● ○ → ○ ○ ● Nova is thinking — everywhere, by one rule: any status text in any Nova window that ends with "…"
/// gets three moving dots until the text changes. The tray icon shows the same dots while an AI job runs in the background.
/// Designed by A.HAMROUNI
/// </summary>
static class Dots
{
    static readonly string[] Frames = { "  ● ○ ○", "  ○ ● ○", "  ○ ○ ●" };
    static System.Windows.Forms.Timer anim, scan;
    static int frame;
    /// <summary>label → (the text Nova set, the text with dots that we set last)</summary>
    static readonly Dictionary<Label, (string Base, string Shown)> live = new();

    /// <summary>AI calls running now (any window or background job).</summary>
    public static int Busy;
    public static void Enter() => Interlocked.Increment(ref Busy);
    public static void Leave() => Interlocked.Decrement(ref Busy);

    static Icon baseIcon;
    static Icon[] trayFrames;
    static bool trayAnimating;

    public static void Start()
    {
        if (anim != null) return;
        anim = new System.Windows.Forms.Timer { Interval = 380 };
        anim.Tick += (_, _) => { try { Tick(); } catch { } };
        anim.Start();
        scan = new System.Windows.Forms.Timer { Interval = 1000 };
        scan.Tick += (_, _) => { try { Scan(); } catch { } };
        scan.Start();
    }

    static bool Thinking(string t)
    {
        t = (t ?? "").TrimEnd();
        return t.EndsWith("…") || t.EndsWith("...");
    }

    /// <summary>Finds the status labels that say "…something…" in every open Nova window (once a second).</summary>
    static void Scan()
    {
        foreach (Form f in Application.OpenForms.Cast<Form>().ToList())
        {
            if (!f.Visible || f.WindowState == FormWindowState.Minimized) continue;
            Walk(f);
        }
        foreach (var l in live.Keys.ToList()) if (l.IsDisposed || !l.Visible) live.Remove(l);
    }

    static void Walk(Control c)
    {
        foreach (Control k in c.Controls)
        {
            if (!k.Visible) continue;
            if (k is Label l && !live.ContainsKey(l) && Thinking(l.Text) && l.Text.Length < 400) live[l] = (l.Text, l.Text);
            if (k.HasChildren) Walk(k);
        }
    }

    static void Tick()
    {
        frame = (frame + 1) % Frames.Length;
        foreach (var (l, st) in live.ToList())
        {
            if (l.IsDisposed) { live.Remove(l); continue; }
            var b = st.Base;
            if (l.Text != st.Shown) b = l.Text;              // Nova changed the text
            if (!Thinking(b)) { live.Remove(l); continue; }  // done — the result is shown, no dots
            var shown = b.TrimEnd() + Frames[frame];
            l.Text = shown;
            live[l] = (b, shown);
        }
        Tray();
    }

    // ------------------------------------------------------------------ the tray icon while an AI job runs

    static void Tray()
    {
        var t = Program.Tray;
        if (t == null) return;
        if (Busy > 0)
        {
            if (trayFrames == null) { baseIcon = t.Icon; trayFrames = Enumerable.Range(0, 3).Select(MakeFrame).ToArray(); }
            t.Icon = trayFrames[frame];
            trayAnimating = true;
        }
        else if (trayAnimating && baseIcon != null) { t.Icon = baseIcon; trayAnimating = false; }
    }

    static Icon MakeFrame(int lit)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            if (baseIcon != null) g.DrawIcon(new Icon(baseIcon, 32, 32), new Rectangle(0, 0, 32, 32));
            using var bg = new SolidBrush(Color.FromArgb(230, 0x14, 0x17, 0x22));
            g.FillRectangle(bg, 0, 22, 32, 10);
            for (int i = 0; i < 3; i++)
            {
                using var br = new SolidBrush(i == lit ? Color.FromArgb(0xB4, 0x9C, 0xFF) : Color.FromArgb(0x55, 0x5A, 0x70));
                g.FillEllipse(br, 3 + i * 10, 23, 7, 7);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
