using System.Drawing.Drawing2D;

namespace NovaEnhance;

/// <summary>
/// 🟢 🟡 🔴 Status lights, the same everywhere in Nova (style C · raised button): a bevelled light with a thin ring —
/// green = working · yellow = something ongoing (gently pulsing) · red = not working (orange stays for "this week").
/// Windows draws these emoji as flat striped outlines; Nova paints its own light over them in every label, button and menu,
/// keeps the word next to it, and every light has a tooltip. Designed by A.HAMROUNI
/// </summary>
static class Lights
{
    public enum Kind { None, Green, Yellow, Red, Orange }

    static readonly string[] Marks = { "🟢", "🟡", "🔴", "🟠" };
    public static Kind Of(string mark) => mark switch { "🟢" => Kind.Green, "🟡" => Kind.Yellow, "🔴" => Kind.Red, "🟠" => Kind.Orange, _ => Kind.None };
    public static string Word(Kind k) => k switch { Kind.Green => "working", Kind.Yellow => "something ongoing", Kind.Red => "not working", Kind.Orange => "needs attention soon", _ => "" };
    public static bool Has(string t) => !string.IsNullOrEmpty(t) && Marks.Any(t.Contains);

    static (Color Hi, Color Mid, Color Lo) Colours(Kind k) => k switch
    {
        Kind.Green => (Color.FromArgb(0x7A, 0xF0, 0xAD), Color.FromArgb(0x18, 0xA9, 0x57), Color.FromArgb(0x0C, 0x6B, 0x37)),
        Kind.Yellow => (Color.FromArgb(0xFF, 0xE5, 0x8A), Color.FromArgb(0xF0, 0xB0, 0x00), Color.FromArgb(0x9A, 0x6A, 0x00)),
        Kind.Red => (Color.FromArgb(0xFF, 0x9A, 0x9A), Color.FromArgb(0xE0, 0x2A, 0x2A), Color.FromArgb(0x8E, 0x10, 0x10)),
        Kind.Orange => (Color.FromArgb(0xFF, 0xC0, 0x80), Color.FromArgb(0xF0, 0x7A, 0x10), Color.FromArgb(0x96, 0x46, 0x00)),
        _ => (Color.FromArgb(0xB0, 0xB6, 0xC4), Color.FromArgb(0x6B, 0x73, 0x85), Color.FromArgb(0x3A, 0x40, 0x4E)),
    };

    /// <summary>Pulse of the yellow light, 0..1 (one breath every ~1.6 s).</summary>
    public static double Pulse => 0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 1600.0 * 2 * Math.PI);

    /// <summary>Draws one light in the square r: dark gap, thin light ring, bevelled body, inner highlight and shade.</summary>
    public static void Draw(Graphics g, RectangleF r, Kind k, bool pulse = true) => Draw(g, r, k, Colours(k), pulse);
    /// <summary>The same light in any colour (e.g. the purple follow-up light on an email card).</summary>
    public static void Draw(Graphics g, RectangleF r, Color c) => Draw(g, r, Kind.None, (ControlPaint.Light(c, 0.6f), c, ControlPaint.Dark(c, 0.35f)), false);

    static void Draw(Graphics g, RectangleF r, Kind k, (Color, Color, Color) col, bool pulse)
    {
        if (r.Width < 4) return;
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (hi, mid, lo) = col;
        if (k == Kind.Yellow && pulse)
        {
            var p = Pulse;   // breathes between a little darker and full
            Color Mix(Color c) => Color.FromArgb(c.A, (int)(c.R * (0.72 + 0.28 * p)), (int)(c.G * (0.72 + 0.28 * p)), (int)(c.B * (0.72 + 0.28 * p)));
            hi = Mix(hi); mid = Mix(mid); lo = Mix(lo);
        }
        float ring = Math.Max(1f, r.Width / 14f);
        using (var o = new Pen(Color.FromArgb(70, 255, 255, 255), ring)) g.DrawEllipse(o, r.X + ring / 2, r.Y + ring / 2, r.Width - ring, r.Height - ring);
        var body = RectangleF.Inflate(r, -ring * 2f, -ring * 2f);
        using (var gap = new Pen(Color.FromArgb(0x0B, 0x0D, 0x12), ring)) g.DrawEllipse(gap, RectangleF.Inflate(body, ring / 2, ring / 2));
        using (var b = new LinearGradientBrush(new RectangleF(body.X - 1, body.Y - 1, body.Width + 2, body.Height + 2), hi, lo, 70f))
        {
            b.InterpolationColors = new ColorBlend { Colors = new[] { hi, mid, lo }, Positions = new[] { 0f, 0.55f, 1f } };
            g.FillEllipse(b, body);
        }
        // inner bevel: light on top, shade below
        using (var top = new Pen(Color.FromArgb(95, 255, 255, 255), Math.Max(1f, body.Width / 10f))) g.DrawArc(top, RectangleF.Inflate(body, -body.Width / 9f, -body.Height / 9f), 200, 140);
        using (var bot = new Pen(Color.FromArgb(110, 0, 0, 0), Math.Max(1f, body.Width / 9f))) g.DrawArc(bot, RectangleF.Inflate(body, -body.Width / 12f, -body.Height / 12f), 20, 140);
        g.SmoothingMode = old;
    }

    static readonly Dictionary<(Kind, int), Bitmap> bitmaps = new();
    /// <summary>A light as a picture (menus, buttons with images).</summary>
    public static Bitmap Image(Kind k, int size = 16)
    {
        lock (bitmaps)
        {
            if (bitmaps.TryGetValue((k, size), out var b)) return b;
            b = new Bitmap(size, size);
            using (var g = Graphics.FromImage(b)) { g.Clear(Color.Transparent); Draw(g, new RectangleF(0, 0, size, size), k, false); }
            return bitmaps[(k, size)] = b;
        }
    }

    // ------------------------------------------------------------------ every Nova window: labels and buttons

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, List<(RectangleF R, Kind K)>> spots = new();
    static readonly HashSet<Control> pulsing = new();
    static System.Windows.Forms.Timer pulseTimer;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, ToolTip> tips = new();

    public static void Attach(Form f)
    {
        var tip = tips.GetValue(f, _ => new ToolTip { InitialDelay = 400, ShowAlways = true });
        void Hook(Control c)
        {
            if (!First(c)) return;
            if (c is Label or ButtonBase)
            {
                c.Paint += (_, e) => { try { Paint(c, e.Graphics); } catch { } };
                c.TextChanged += (_, _) => { try { Tip(c, tip); } catch { } };
                Tip(c, tip);
            }
            c.ControlAdded += (_, e) => Hook(e.Control);
            foreach (Control k in c.Controls) Hook(k);
        }
        Hook(f);
        if (pulseTimer == null)
        {
            pulseTimer = new System.Windows.Forms.Timer { Interval = 110 };
            pulseTimer.Tick += (_, _) =>
            {
                foreach (var c in pulsing.ToList())
                {
                    if (c.IsDisposed || !c.Visible) { pulsing.Remove(c); continue; }
                    if (spots.TryGetValue(c, out var list))
                        foreach (var (r, k) in list) if (k == Kind.Yellow) c.Invalidate(Rectangle.Inflate(Rectangle.Round(r), 1, 1));
                }
            };
            pulseTimer.Start();
        }
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, string> ownTip = new();
    static void Tip(Control c, ToolTip tip)
    {
        var t = c.Text ?? "";
        var kinds = Marks.Where(t.Contains).Select(Of).ToList();
        ownTip.TryGetValue(c, out var mine);
        var cur = tip.GetToolTip(c);
        if (kinds.Count == 0) { if (mine != null && cur == mine) tip.SetToolTip(c, ""); ownTip.Remove(c); return; }
        if (!string.IsNullOrEmpty(cur) && cur != mine) return;   // the window gave its own tooltip
        var s = string.Join(" · ", kinds.Distinct().Select(k => k switch { Kind.Green => "🟢 green = working", Kind.Yellow => "🟡 yellow = something ongoing", Kind.Red => "🔴 red = not working", _ => "🟠 orange = needs attention soon" }));
        tip.SetToolTip(c, s);
        ownTip.AddOrUpdate(c, s);
    }

    static Color Behind(Control c)
    {
        for (var x = c; x != null; x = x.Parent) if (x.BackColor.A == 255) return x.BackColor;
        return Th.Bg;
    }

    const TextFormatFlags Np = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    /// <summary>Finds the emoji on the first line of the control's text and paints a light over each one.</summary>
    static void Paint(Control c, Graphics g)
    {
        var t = c.Text ?? "";
        if (!Has(t)) { spots.Remove(c); pulsing.Remove(c); return; }
        var font = c.Font;
        var line = t.Split('\n')[0].TrimEnd('\r');
        var full = TextRenderer.MeasureText(g, line, font, new Size(int.MaxValue, int.MaxValue), Np);
        int lineH = TextRenderer.MeasureText(g, "Ag", font, new Size(int.MaxValue, int.MaxValue), Np).Height;
        var inner = c.ClientRectangle;
        inner = new Rectangle(inner.X + c.Padding.Left, inner.Y + c.Padding.Top, inner.Width - c.Padding.Horizontal, inner.Height - c.Padding.Vertical);
        int pad = Math.Max(0, (TextRenderer.MeasureText(g, "x", font).Width - TextRenderer.MeasureText(g, "x", font, new Size(int.MaxValue, int.MaxValue), Np).Width) / 2);
        var align = c is Label l ? l.TextAlign : c is ButtonBase b ? b.TextAlign : ContentAlignment.TopLeft;
        bool wraps = full.Width + pad * 2 > inner.Width;
        float x0 = inner.X + pad, y0 = inner.Y;
        int lines = Math.Max(1, t.Split('\n').Length);
        int blockH = lineH * lines;
        if (!wraps)
        {
            if (align is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter) x0 = inner.X + (inner.Width - full.Width) / 2f;
            else if (align is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight) x0 = inner.Right - pad - full.Width;
        }
        if (align is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight) y0 = inner.Y + (inner.Height - blockH) / 2f;
        else if (align is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight) y0 = inner.Bottom - blockH;
        var list = new List<(RectangleF, Kind)>();
        using var bg = new SolidBrush(Behind(c));
        for (int i = 0; i < line.Length - 1; i++)
        {
            var m = line.Substring(i, 2);
            var k = Of(m);
            if (k == Kind.None) continue;
            if (wraps && i > 0) break;   // a wrapped text: only a light at the start is certain to be on the first line
            float x = x0 + (i == 0 ? 0 : TextRenderer.MeasureText(g, line[..i], font, new Size(int.MaxValue, int.MaxValue), Np).Width);
            float w = TextRenderer.MeasureText(g, m, font, new Size(int.MaxValue, int.MaxValue), Np).Width;
            g.FillRectangle(bg, x - 0.5f, y0, w + 1, lineH);
            float d = Math.Min(w, lineH) * 0.92f;
            var r = new RectangleF(x + (w - d) / 2, y0 + (lineH - d) / 2, d, d);
            Draw(g, r, k);
            list.Add((r, k));
            i++;
        }
        spots.AddOrUpdate(c, list);
        if (list.Any(x => x.Item2 == Kind.Yellow)) pulsing.Add(c); else pulsing.Remove(c);
    }

    // ------------------------------------------------------------------ menus: the light becomes the item's picture

    /// <summary>"🟢  Connect" → a light picture + "Connect" (also for items added later and sub-menus).</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> hooked = new();
    static bool First(object o) { if (hooked.TryGetValue(o, out _)) return false; hooked.Add(o, o); return true; }

    public static void Menu(ToolStrip m)
    {
        void Fix(ToolStripItem it)
        {
            var t = it.Text ?? "";
            if (t.Length >= 2 && Of(t[..2]) is var k && k != Kind.None)
            {
                it.Image = Image(k, 16);
                it.Text = t[2..].TrimStart();
                it.ToolTipText = "🟢 green = working · 🟡 yellow = something ongoing · 🔴 red = not working";
            }
            if (!First(it)) return;
            if (it is ToolStripDropDownItem dd) { foreach (ToolStripItem s in dd.DropDownItems) Fix(s); dd.DropDown.ItemAdded += (_, e) => Fix(e.Item); }
            it.TextChanged += (_, _) => { var x = it.Text ?? ""; if (x.Length >= 2 && Of(x[..2]) is var k2 && k2 != Kind.None) { it.Image = Image(k2, 16); it.Text = x[2..].TrimStart(); } };
        }
        if (!First(m)) return;
        foreach (ToolStripItem it in m.Items) Fix(it);
        m.ItemAdded += (_, e) => Fix(e.Item);
    }
}
