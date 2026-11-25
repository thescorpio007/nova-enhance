namespace NovaEnhance;

/// <summary>
/// 🧠 Learn this project: Nova reads the NAMES of a project's files (nothing leaves the PC, 🔒 private folders skipped), finds how its
/// document numbers are built (the template) and lists the codes it does not know yet — most used first, with examples and a suggestion.
/// You answer with a click (Kind · Discipline · Source · not a code); your answers go into 🧠 Nova Brain › 📐 Filing & document codes and
/// every part of Nova uses them at once (Find a project file, Guided search, the AI). A code you skip is not asked again. Designed by A.HAMROUNI
/// </summary>
class FilingLearnForm : NovaForm
{
    readonly ComboBox proj;
    readonly TextBox template;
    readonly Label note, tplNote;
    readonly FlowLayoutPanel body;
    readonly Button stopBtn, saveBtn;
    readonly CheckBox showSkipped;
    readonly List<(Filing.Unknown U, ComboBox Answer)> rows = new();
    CancellationTokenSource cts;
    string tplStart = "";

    const string SkipItem = "— skip for now (not asked again) —";
    const string IgnoreItem = "Ignore — not a code (zone, building, part, title word)";

    public FilingLearnForm(string project = null)
    {
        Text = "🧠 Nova · Learn this project";
        Width = 1000; Height = 780; MinimumSize = new Size(700, 480);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14, 10, 14, 4), BackColor = Th.Bg };
        top.Controls.Add(Th.L("🧠 Learn this project", 13, Th.Text, true));
        proj = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10), FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 6, 8, 0) };
        foreach (var p in Projects.All()) proj.Items.Add($"{p.Code} · {p.Name}");
        top.Controls.Add(proj);
        top.Controls.Add(Th.B("🔎 Read the names", Scan, true));
        stopBtn = Th.B("■ Stop", () => { cts?.Cancel(); stopBtn.Visible = false; note.Text = "■ Stopped."; });
        stopBtn.Visible = false;
        top.Controls.Add(stopBtn);
        showSkipped = Th.Check("Show the codes I skipped", false, _ => Scan());
        top.Controls.Add(showSkipped);
        top.Controls.Add(Th.B("🧠 Open Nova Brain", Screens.Brain));

        var info = new Panel { Dock = DockStyle.Top, Height = 118, BackColor = Th.Bg, Padding = new Padding(16, 2, 16, 4) };
        note = new Label { Dock = DockStyle.Top, Height = 40, Font = Th.F(9.5f), ForeColor = Th.Muted, UseMnemonic = false };
        note.Text = "Nova reads only the names of the files (nothing is sent anywhere), finds how the numbers are built and asks you about the codes it does not know. " +
                    "Your answers are saved in 🧠 Nova Brain › 📐 Filing & document codes.";
        Thinking.Watch(note);
        var tr = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, BackColor = Th.Bg, WrapContents = false };
        tr.Controls.Add(new Label { Text = "Number template", AutoSize = false, Width = 130, Height = 28, ForeColor = Th.Text, Font = Th.F(9.5f, true), TextAlign = ContentAlignment.MiddleLeft });
        template = Th.Box(false); if (template is SpellBox sb) sb.Off = true;
        template.Width = 520; template.Font = Th.F(10);
        template.PlaceholderText = "e.g.  project from discipline type serial";
        tr.Controls.Add(template);
        tplNote = new Label { Dock = DockStyle.Top, Height = 34, Font = Th.F(8.5f), ForeColor = Th.Muted, UseMnemonic = false,
            Text = "Words for each part of the number, in order: project · from · to · zone · level · discipline · type · serial · rev · stage · year · x (= ignore). Nova fills its best guess; correct it if needed." };
        info.Controls.Add(tplNote); info.Controls.Add(tr); info.Controls.Add(note);

        body = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14, 6, 14, 14), BackColor = Th.Bg };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(14, 6, 14, 8), BackColor = Color.FromArgb(0x0E, 0x11, 0x16) };
        saveBtn = Th.B("💾 Save my answers to 🧠 Nova Brain", Save, true);
        bottom.Controls.Add(saveBtn);
        bottom.Controls.Add(Th.L("   Nothing is changed until you save · a version of the brain is kept before every change.", 9, Th.Muted));
        Controls.Add(body); Controls.Add(bottom); Controls.Add(info); Controls.Add(top);
        Resize += (_, _) => { foreach (Control c in body.Controls) c.Width = RowW; };

        var start = Projects.All().FindIndex(p => p.Code.Equals(project ?? "", StringComparison.OrdinalIgnoreCase));
        proj.SelectedIndex = proj.Items.Count == 0 ? -1 : Math.Max(0, start);
        proj.SelectedIndexChanged += (_, _) => Scan();
        Shown += (_, _) => Scan();
        FormClosed += (_, _) => cts?.Cancel();
    }

    int RowW => Math.Max(500, body.ClientSize.Width - body.Padding.Horizontal - 22);
    Project Current => proj.SelectedIndex >= 0 && proj.SelectedIndex < Projects.All().Count ? Projects.All()[proj.SelectedIndex] : null;

    async void Scan()
    {
        var p = Current;
        if (p == null) { note.Text = "Add your projects first: Settings → 📁 Projects."; return; }
        cts?.Cancel();
        var my = cts = new CancellationTokenSource();
        foreach (Control c in body.Controls.Cast<Control>().ToList()) c.Dispose();
        body.Controls.Clear(); rows.Clear();
        stopBtn.Visible = true;
        note.Text = $"🔎 Reading the names of {p.Code}…";
        List<Filing.Unknown> unknown; string guess; int total;
        try
        {
            (unknown, guess, total) = await Task.Run(() =>
            {
                var files = Projects.Catalogue(p.Folders.Concat((p.Sources ?? new()).Select(s => s.Path)), 150000).Select(x => x.Path).ToList();
                var u = Filing.Learn(p.Code, files, my.Token);
                return (u, Guess(p.Code, files), files.Count);
            }, my.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { note.Text = "⚠ " + e.Message; stopBtn.Visible = false; return; }
        if (my.IsCancellationRequested || IsDisposed) return;
        stopBtn.Visible = false;
        var have = Filing.TemplateOf(p.Code);
        tplStart = have != null ? string.Join(" ", have) : guess;
        template.Text = tplStart;
        tplNote.Text = (have != null ? "✓ Saved template. " : guess.Length > 0 ? "✦ Nova's guess from the numbers — check it. " : "") +
                       "Words for each part, in order: project · from · to · zone · level · discipline · type · serial · rev · stage · year · x (= ignore).";
        var shown = unknown.Where(u => showSkipped.Checked || !Store.Bool(SkipKey(p.Code, u.Code), false)).ToList();
        if (!showSkipped.Checked) Filing.Pending = Math.Max(0, Filing.Pending);   // refreshed by the daily count
        note.Text = shown.Count == 0
            ? $"✓ {total:N0} names read — Nova knows every code of {p.Code}" + (unknown.Count > shown.Count ? $" ({unknown.Count - shown.Count} skipped)." : ".")
            : $"{total:N0} names read · {shown.Count} code(s) Nova does not know yet — most used first. Pick a meaning for each (or skip), then 💾 Save.";
        body.SuspendLayout();
        int n = 0;
        foreach (var u in shown) Row(++n, u);
        body.ResumeLayout();
    }

    static string SkipKey(string p, string c) => "win_filing_skip_" + p + "_" + c;

    void Row(int n, Filing.Unknown u)
    {
        var card = new Panel { Width = RowW, Height = 104, BackColor = Th.Card, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(10, 6, 10, 6) };
        var t = Th.L($"{n}   {u.Code}   · {u.Count:N0} files · seen as {(u.Role == "?" ? "a code" : u.Role)}", 10.5f, Th.Text, true);
        t.Location = new Point(10, 6);
        var ex = Th.L("e.g.  " + string.Join("\n        ", u.Examples.Take(2)), 8.5f, Th.Muted);
        ex.Location = new Point(10, 30); ex.AutoSize = false; ex.Width = card.Width - 20; ex.Height = 34; ex.AutoEllipsis = true; ex.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; ex.UseMnemonic = false;
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 420, Location = new Point(10, 68), BackColor = Th.Card2, ForeColor = Th.Text, Font = Th.F(9.5f), FlatStyle = FlatStyle.Flat,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
        cb.Items.Add(SkipItem);
        if (u.Suggest.Length > 0) cb.Items.Add("✦ " + u.Suggest);
        foreach (var k in Filing.Kinds) cb.Items.Add("Kind: " + k);
        foreach (var d in Filing.Disciplines) cb.Items.Add("Discipline: " + d);
        foreach (var s in Filing.Sources) cb.Items.Add("Source: " + s);
        cb.Items.Add(IgnoreItem);
        cb.SelectedIndex = 0;
        var hint = Th.L("or type e.g.  Kind: Inspection request · Discipline: Lighting · Source: Contractor", 8.5f, Th.Muted);
        hint.Location = new Point(440, 72);
        card.Controls.AddRange(new Control[] { t, ex, cb, hint });
        body.Controls.Add(card);
        rows.Add((u, cb));
    }

    void Save()
    {
        var p = Current;
        if (p == null) return;
        if (!Brain.Unlocked) { note.Text = "🔒 Open 🧠 Nova Brain first (its password) — the codes are kept inside it. Then press 💾 Save again."; Screens.Brain(); return; }
        var answers = new List<(string, string)>();
        foreach (var (u, cb) in rows)
        {
            var a = (cb.Text ?? "").Trim().TrimStart('✦').Trim();
            if (a.Length == 0 || a == SkipItem) { Store.SetBool(SkipKey(p.Code, u.Code), true); continue; }
            if (a == IgnoreItem) { answers.Add((u.Code, "Ignore")); continue; }
            if (!a.StartsWith("Kind: ") && !a.StartsWith("Discipline: ") && !a.StartsWith("Source: ")) a = "Kind: " + a;   // a plain word = a kind of document
            answers.Add((u.Code, a));
        }
        var tpl = template.Text.Trim();
        var tplChanged = tpl.Length > 0 && !tpl.Equals(tplStart, StringComparison.OrdinalIgnoreCase) || tpl.Length > 0 && Filing.TemplateOf(p.Code) == null;
        if (answers.Count == 0 && !tplChanged) { note.Text = "Nothing to save — the codes you left on “skip” will not be asked again."; Scan(); return; }
        try
        {
            Filing.SaveAnswers(p.Code, answers, tplChanged ? tpl : null);
            note.Text = $"✓ Saved in 🧠 Nova Brain: {answers.Count} code(s){(tplChanged ? " + the number template" : "")} for {p.Code}. Nova uses them now.";
            Scan();
        }
        catch (Exception e) { note.Text = "⚠ " + e.Message; }
    }

    /// <summary>Nova's guess of the number template: the most common number length, and what most codes at each place mean.</summary>
    static string Guess(string project, List<string> files)
    {
        var nums = files.Select(f => Revisions.Number(Path.GetFileNameWithoutExtension(f))).Where(n => n.Length > 0 && n.StartsWith(project.Split('-')[0], StringComparison.OrdinalIgnoreCase)).Take(20000).ToList();
        if (nums.Count < 20) return "";
        var pparts = project.Count(c => c == '-') + 1;
        var common = nums.Select(n => n.Split('-')).GroupBy(s => s.Length).OrderByDescending(g => g.Count()).First();
        var len = common.Key;
        var roles = new List<string> { "project" };
        for (int i = pparts; i < len; i++)
        {
            var vals = common.Select(s => s[i]).ToList();
            int src = vals.Count(v => Filing.SourceOf(project, v) != null), kind = vals.Count(v => Filing.KindOf(project, v) != null),
                disc = vals.Count(v => Filing.DiscOf(project, v) != null), dig = vals.Count(v => v.All(char.IsDigit) || System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+[A-Z]?$"));
            var best = new[] { ("from", src), ("type", kind), ("discipline", disc), ("serial", dig) }.OrderByDescending(x => x.Item2).First();
            roles.Add(best.Item2 >= vals.Count / 3 ? best.Item1 : "x");
        }
        // the type usually comes after the discipline: if both guessed the same, keep the order discipline → type
        return string.Join(" ", roles);
    }
}
