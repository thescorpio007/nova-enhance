using System.Diagnostics;

namespace NovaEnhance;

/// <summary>Ctrl+Shift+E: the selected text, enhanced, and put back into the same program.</summary>
class EnhanceForm : NovaForm
{
    TextBox eInstr;
    IntPtr target;
    string targetName = "";
    readonly Stack stack;
    readonly ComboBox aiBox;
    readonly Label from, status;
    readonly TextBox orig, result;
    readonly FlowLayoutPanel modeRow;
    readonly Button replaceBtn;
    readonly Dictionary<string, Button> modeBtns = new();
    CancellationTokenSource cts;

    public EnhanceForm()
    {
        Text = "Nova Enhance";
        Width = 620; Height = 700;
        MinimumSize = new Size(500, 520);
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;

        stack = new Stack();
        Controls.Add(stack);

        var head = stack.Add(Th.Row(false));
        head.Controls.Add(Th.L("✦ Nova Enhance", 14, Th.Text, true));
        aiBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, BackColor = Th.Card2, ForeColor = Th.Text, FlatStyle = FlatStyle.Flat, Font = Th.F(9.5f), Margin = new Padding(12, 4, 6, 0) };
        aiBox.SelectedIndexChanged += (_, _) => { if (aiBox.SelectedItem is AiItem a) Store.Target = a.P.Id; };
        head.Controls.Add(aiBox);
        head.Controls.Add(Th.B("⚙", () => Screens.Settings()));

        from = stack.Add(Th.Wrap("", 9.5f));

        stack.Add(Th.Section("Original"));
        orig = stack.Add(Th.Box(true, 5), true);

        modeRow = stack.Add(Th.Row());
        foreach (var id in new[] { "grammar", "shorten", "professional", "informal", "expand" })
        {
            var mid = id;
            var b = Th.Chip(Modes.ById(id).Label, false, () => Run(mid));
            modeBtns[id] = b;
            modeRow.Controls.Add(b);
        }
        var tr = Th.Chip("", false, () => Run("translate"));
        modeBtns["translate"] = tr;
        modeRow.Controls.Add(tr);
        var langBtn = Th.Chip("▾", false, null);
        langBtn.Click += (_, _) => LangMenu().Show(langBtn, new Point(0, langBtn.Height));
        modeRow.Controls.Add(langBtn);

        // ✎ More instructions for my AI (kept until you change them)
        eInstr = stack.Add(Th.Prose(Th.Box(true, 2)), true);
        eInstr.PlaceholderText = "✎ More instructions (optional) — e.g. British English · keep it under 80 words · formal, to a client · keep the bullet points";
        eInstr.Text = Store.Str("win_enh_instr");
        eInstr.Visible = eInstr.Text.Length > 0;
        eInstr.Leave += (_, _) => Store.Set("win_enh_instr", eInstr.Text);

        var go = stack.Add(Th.Row(false));
        go.Controls.Add(Th.B("✦ Enhance again", () => Run(Store.Mode), true));
        go.Controls.Add(Th.B("✎", () => { eInstr.Visible = !eInstr.Visible; if (eInstr.Visible) eInstr.Focus(); stack.FitAll(); }));
        status = Th.L("", 9.5f, Th.Muted);
        status.Margin = new Padding(4, 8, 0, 0);
        go.Controls.Add(status);

        stack.Add(Th.Section("Enhanced"));
        result = stack.Add(Th.Box(true, 8), true);
        result.BackColor = Color.FromArgb(0x1A, 0x1D, 0x2B);

        var act = stack.Add(Th.Row());
        replaceBtn = Th.B("⇄ Replace", Replace, true);
        act.Controls.Add(replaceBtn);
        act.Controls.Add(Th.B("⧉ Copy", () => { if (result.Text.Length > 0) { Clipboard.SetText(result.Text); Status("⧉ Copied", Th.Good); } }));
        var origBtn = Th.B("📄 Originals ▾", null);
        origBtn.ForeColor = Th.Yellow;
        origBtn.Click += (_, _) => OriginalsMenu().Show(origBtn, new Point(0, origBtn.Height));
        act.Controls.Add(origBtn);
        act.Controls.Add(Th.B("✕", Hide));

        stack.Add(Th.Wrap("Ctrl+Enter = Replace · Esc = close · the text before each enhancement is saved in Documents\\Nova Enhance\\Originals", 8.5f));
        stack.Add(Th.Signature());

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; }
            if (e.KeyCode == Keys.Enter && e.Control) { Replace(); e.Handled = true; e.SuppressKeyPress = true; }
        };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    class AiItem { public Provider P; public override string ToString() => P.Label + " ▾"; }

    void FillAi()
    {
        aiBox.Items.Clear();
        foreach (var p in Ai.WithKeys) aiBox.Items.Add(new AiItem { P = p });
        var act = Ai.Active();
        for (int i = 0; i < aiBox.Items.Count; i++) if (((AiItem)aiBox.Items[i]).P.Id == act?.Id) aiBox.SelectedIndex = i;
        aiBox.Visible = aiBox.Items.Count > 0;
    }

    void DrawModes()
    {
        modeBtns["translate"].Text = "🌐 → " + Modes.LangLabel();
        foreach (var (id, b) in modeBtns) Th.SetChip(b, id == Store.Mode);
    }

    public void Start(IntPtr h, string text, string mode = null)
    {
        target = h;
        targetName = h == IntPtr.Zero ? "" : Selection.AppName(h);
        FillAi();
        DrawModes();
        text ??= "";
        from.Text = text.Trim().Length > 0
            ? $"Selected in {targetName} · {Words(text)} words"
            : (targetName.Length > 0 ? $"Nothing was selected in {targetName} — type or paste text here" : "Type or paste text here");
        replaceBtn.Text = targetName.Length > 0 ? "⇄ Replace in " + Short(targetName) : "⇄ Copy result";
        orig.Text = Crlf(text);
        result.Text = "";
        Status("", Th.Muted);
        PlaceNearCursor();
        Screens.Show(this);
        if (Ai.Active() == null) { Status("⚠ Add an AI key first: ⚙ → AI & API keys", Th.Warn); return; }
        if (text.Trim().Length > 0) Run(mode != null && Modes.All.Any(m => m.Id == mode) ? mode : "grammar"); else orig.Focus();
    }

    void PlaceNearCursor()
    {
        var p = Cursor.Position;
        var wa = Screen.FromPoint(p).WorkingArea;
        int x = Math.Min(Math.Max(wa.Left, p.X - Width / 2), wa.Right - Width);
        int y = Math.Min(Math.Max(wa.Top, p.Y + 20), wa.Bottom - Height);
        Location = new Point(x, y);
    }

    static int Words(string s) => s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
    static string Short(string s) => s.Length > 22 ? s[..22] + "…" : s;
    static string Crlf(string s) => (s ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");

    void Status(string s, Color c) { status.Text = s; status.ForeColor = c; }

    async void Run(string mode)
    {
        var text = orig.Text;
        if (text.Trim().Length == 0) { Status("Type or paste text first", Th.Warn); orig.Focus(); return; }
        var p = (aiBox.SelectedItem as AiItem)?.P ?? Ai.Active();
        if (p == null) { Status("⚠ Add an AI key first: ⚙ → AI & API keys", Th.Warn); return; }
        Store.Mode = mode;
        DrawModes();
        cts?.Cancel();
        var my = cts = new CancellationTokenSource();
        Status($"✦ {Modes.Label(mode)} with {p.Label}…", Th.AccentText);
        var sw = Stopwatch.StartNew();
        try
        {
            Store.Set("win_enh_instr", eInstr.Text);
            var outText = await Ai.Enhance(p, mode, text, my.Token, eInstr.Visible ? eInstr.Text.Trim() : "");
            if (my.IsCancellationRequested) return;
            result.Text = Crlf(outText);
            History.SaveOriginal(mode, text, outText);
            History.Add("enhance", p.Id, Store.Model(p.Id), mode, text, outText);
            Status($"✓ {Modes.Label(mode)} · {sw.Elapsed.TotalSeconds:0.0} s · original saved", Th.Good);
            result.Focus();
            result.SelectionStart = 0;
        }
        catch (Exception e)
        {
            if (my.IsCancellationRequested) return;
            Status("⚠ " + e.Message, Th.Bad);
            Log.Error("enhance", e);
        }
    }

    async void Replace()
    {
        var t = result.Text;
        if (t.Trim().Length == 0) { Status("Nothing to put back yet", Th.Warn); return; }
        if (target == IntPtr.Zero) { Clipboard.SetText(t); Status("⧉ Copied — paste it where you need it", Th.Good); return; }
        Hide();
        await Selection.Paste(target, t);
        Program.Balloon("✓ Replaced in " + targetName, "Press Ctrl+Z there to undo. The original is saved in 📄 Originals.");
    }

    ContextMenuStrip LangMenu()
    {
        var m = new ContextMenuStrip { Font = Th.F(10) };
        void Add(ToolStripItemCollection items, string v, string label)
        {
            var it = new ToolStripMenuItem(label) { Checked = v.Equals(Store.TrLang, StringComparison.OrdinalIgnoreCase) };
            it.Click += (_, _) => { Store.TrLang = v; DrawModes(); Run("translate"); };
            items.Add(it);
        }
        foreach (var (v, l) in Modes.MainLangs) Add(m.Items, v, l);
        var others = new ToolStripMenuItem("Others");
        foreach (var (v, l) in Modes.OtherLangs) Add(others.DropDownItems, v, l);
        m.Items.Add(others);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("✎ Type any language…", null, (_, _) =>
        {
            var v = Prompt.Ask(this, "Translate to", "Any language, e.g. Japanese, Dutch, Greek:");
            if (!string.IsNullOrWhiteSpace(v)) { Store.TrLang = v.Trim(); DrawModes(); Run("translate"); }
        });
        return m;
    }

    ContextMenuStrip OriginalsMenu()
    {
        var m = new ContextMenuStrip { Font = Th.F(10) };
        var list = History.LastOriginals(20);
        if (list.Count == 0) m.Items.Add(new ToolStripMenuItem("No original saved yet") { Enabled = false });
        foreach (var o in list)
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(o.Ts).LocalDateTime;
            var first = (o.Text ?? "").Replace("\r", " ").Replace("\n", " ");
            if (first.Length > 60) first = first[..60] + "…";
            var item = new ToolStripMenuItem($"{when:dd MMM HH:mm} · {Modes.Label(o.Mode)} · {first}");
            var copy = o;
            item.DropDownItems.Add("↩ Put this original in the box", null, (_, _) => { orig.Text = Crlf(copy.Text); result.Text = Crlf(copy.Text); Status("↩ Original loaded — ⇄ Replace puts it back", Th.Yellow); });
            item.DropDownItems.Add("⧉ Copy the original", null, (_, _) => { Clipboard.SetText(copy.Text ?? ""); Status("⧉ Original copied", Th.Good); });
            m.Items.Add(item);
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("📁 Open the Originals folder", null, (_, _) => Shell.Open(Paths.Originals));
        return m;
    }
}
