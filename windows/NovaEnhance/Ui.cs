using System.Runtime.InteropServices;

namespace NovaEnhance;

/// <summary>Nova colours and small builders, so every window looks the same as the phone app.</summary>
static class Th
{
    public static readonly Color Bg = Color.FromArgb(0x12, 0x15, 0x1C);
    public static readonly Color Card = Color.FromArgb(0x18, 0x1C, 0x24);
    public static readonly Color Card2 = Color.FromArgb(0x23, 0x2A, 0x3A);
    public static readonly Color Text = Color.FromArgb(0xEC, 0xEE, 0xF3);
    public static readonly Color Muted = Color.FromArgb(0x9A, 0xA3, 0xB5);
    public static readonly Color Accent = Color.FromArgb(0x7C, 0x6C, 0xFF);
    public static readonly Color AccentText = Color.FromArgb(0xB9, 0xB0, 0xFF);
    public static readonly Color Good = Color.FromArgb(0x5E, 0xD6, 0x8A);
    public static readonly Color Bad = Color.FromArgb(0xFF, 0x6B, 0x6B);
    public static readonly Color Warn = Color.FromArgb(0xFF, 0xB8, 0x6B);
    public static readonly Color Yellow = Color.FromArgb(0xFF, 0xD2, 0x7A);
    public static readonly Color Line = Color.FromArgb(0x2A, 0x30, 0x40);

    static readonly Dictionary<string, Font> fonts = new();
    public static Font F(float size, bool bold = false)
    {
        var k = size + (bold ? "b" : "");
        lock (fonts)
        {
            if (!fonts.TryGetValue(k, out var f)) fonts[k] = f = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            return f;
        }
    }

    public static Label L(string s, float size = 10, Color? c = null, bool bold = false) =>
        new() { Text = s, AutoSize = true, Font = F(size, bold), ForeColor = c ?? Text, BackColor = Color.Transparent, Margin = new Padding(0, 2, 8, 2), UseMnemonic = false };

    /// <summary>A label that wraps to the panel width.</summary>
    public static Label Wrap(string s, float size = 9.5f, Color? c = null)
    {
        var l = L(s, size, c ?? Muted);
        l.Tag = "w";
        return l;
    }

    public static Label Section(string s) => new()
    {
        Text = s.ToUpperInvariant(), AutoSize = true, Font = F(8.5f, true), ForeColor = Muted, BackColor = Color.Transparent,
        Margin = new Padding(0, 14, 0, 6), UseMnemonic = false
    };

    public static Button B(string s, Action click, bool primary = false)
    {
        var b = new Button
        {
            Text = s, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Card2, ForeColor = primary ? Color.White : Text, Font = F(9.5f, true),
            MinimumSize = new Size(0, 32), Padding = new Padding(8, 2, 8, 2), Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false, Margin = new Padding(0, 0, 8, 6), UseMnemonic = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0x8F, 0x81, 0xFF) : Color.FromArgb(0x2E, 0x36, 0x4A);
        if (click != null) b.Click += (_, _) => click();
        return b;
    }

    public static Button Chip(string s, bool on, Action click)
    {
        var b = B(s, click);
        b.Font = F(9.5f);
        b.MinimumSize = new Size(0, 30);
        SetChip(b, on);
        return b;
    }

    public static void SetChip(Button b, bool on)
    {
        b.BackColor = on ? Accent : Card2;
        b.ForeColor = on ? Color.White : Text;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = on ? Accent : Line;
    }

    public static TextBox Box(bool multi, int lines = 4, bool password = false)
    {
        // Every box spell-checks as you type (red = spelling, blue = grammar), except passwords and keys.
        TextBox t = password ? new TextBox() : new SpellBox();
        t.Multiline = multi; t.BackColor = Card; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle; t.Font = F(10.5f);
        t.ScrollBars = multi ? ScrollBars.Vertical : ScrollBars.None; t.AcceptsReturn = multi; t.UseSystemPasswordChar = password;
        t.Margin = new Padding(0, 0, 0, 6); t.WordWrap = true;
        if (multi) t.Height = (int)(t.Font.Height * lines) + 12;
        return t;
    }

    /// <summary>A box for paths or file patterns: no spelling lines.</summary>
    public static TextBox NoSpell(TextBox t) { if (t is SpellBox s) s.Off = true; return t; }

    /// <summary>A question box: grammar is checked too when you pause.</summary>
    public static TextBox Prose(TextBox t) { if (t is SpellBox s) s.Prose = true; return t; }

    public static CheckBox Check(string s, bool on, Action<bool> changed)
    {
        var c = new CheckBox { Text = s, Checked = on, AutoSize = true, ForeColor = Text, Font = F(10), BackColor = Color.Transparent, Margin = new Padding(0, 3, 12, 3), UseMnemonic = false };
        c.CheckedChanged += (_, _) => changed?.Invoke(c.Checked);
        return c;
    }

    public static RadioButton Radio(string s, bool on, Action picked)
    {
        var r = new RadioButton { Text = s, Checked = on, AutoSize = true, ForeColor = Text, Font = F(10), BackColor = Color.Transparent, Margin = new Padding(0, 3, 12, 1), UseMnemonic = false };
        r.CheckedChanged += (_, _) => { if (r.Checked) picked?.Invoke(); };
        return r;
    }

    public static FlowLayoutPanel Row(bool wrap = true) => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight,
        WrapContents = wrap, Margin = new Padding(0, 0, 0, 4), BackColor = Color.Transparent, Padding = Padding.Empty, Tag = "w"
    };

    public static Control Signature() => new Label
    {
        Text = Program.Sign + " · build " + Program.Build, AutoSize = true, Font = F(8.5f), ForeColor = Color.FromArgb(0x6B, 0x73, 0x85),
        Margin = new Padding(0, 14, 0, 4), BackColor = Color.Transparent
    };

    static Icon icon;
    public static Icon AppIcon
    {
        get
        {
            if (icon != null) return icon;
            try { icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { }
            return icon ??= SystemIcons.Application;
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    public static void DarkTitle(Form f) { try { int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch { } }
}

/// <summary>Dark window with the Nova icon.</summary>
class NovaForm : Form
{
    public NovaForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Th.Bg; ForeColor = Th.Text; Font = Th.F(10);
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Th.AppIcon; } catch { }
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Th.DarkTitle(this); }
}

/// <summary>Top-to-bottom panel. Controls tagged "s" stretch to the width; "w" wrap at the width.</summary>
class Stack : FlowLayoutPanel
{
    public Stack()
    {
        Dock = DockStyle.Fill; FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoScroll = true;
        Padding = new Padding(16, 12, 16, 8); BackColor = Th.Bg;
    }

    public TC Add<TC>(TC c, bool stretch = false) where TC : Control
    {
        if (stretch) c.Tag = "s";
        Controls.Add(c);
        Fit(c);
        return c;
    }

    int Avail => ClientSize.Width - Padding.Horizontal - (VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - 2;

    protected override void OnResize(EventArgs e) { base.OnResize(e); FitAll(); }
    protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); Fit(e.Control); }

    public void FitAll() { SuspendLayout(); foreach (Control c in Controls) Fit(c); ResumeLayout(); }

    void Fit(Control c)
    {
        var w = Avail - c.Margin.Horizontal;
        if (w < 60) return;
        switch (c.Tag as string)
        {
            case "s": c.Width = w; break;
            case "w": c.MaximumSize = new Size(w, 0); break;
        }
    }
}

/// <summary>A one-line question box (used for "Type any language" and passwords).</summary>
static class Prompt
{
    public static string Ask(IWin32Window owner, string title, string label, string value = "", bool password = false)
    {
        using var f = new NovaForm { Text = title, Width = 460, Height = 210, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, TopMost = true };
        var s = new Stack();
        f.Controls.Add(s);
        s.Add(Th.Wrap(label, 10, Th.Text));
        var box = s.Add(Th.Box(false, 1, password), true);
        box.Text = value;
        var row = s.Add(Th.Row());
        string result = null;
        var ok = Th.B("OK", () => { result = box.Text; f.DialogResult = DialogResult.OK; }, true);
        row.Controls.Add(ok);
        row.Controls.Add(Th.B("Cancel", () => f.DialogResult = DialogResult.Cancel));
        f.AcceptButton = ok;
        f.Shown += (_, _) => box.Focus();
        return f.ShowDialog(owner) == DialogResult.OK ? result : null;
    }
}
