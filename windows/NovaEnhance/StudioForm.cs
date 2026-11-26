using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;

namespace NovaEnhance;

/// <summary>📘 The Studio guides (PDF, inside Nova): opened in Nova's own viewer.</summary>
static class Guides
{
    public static void Open(string name)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Guides." + name + ".pdf", StringComparison.OrdinalIgnoreCase));
            if (res == null) { MessageBox.Show("Guide not found: " + name, "Nova Enhance"); return; }
            var dir = Path.Combine(Paths.Cache, "guides"); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".pdf");
            using (var s = asm.GetManifestResourceStream(res)!) using (var f = File.Create(path)) s.CopyTo(f);
            FileMenu.OpenInNova(path);
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Nova Enhance"); }
    }

    public static Button Btn(string name, string label = "📘")
    {
        var b = Th.B(label, () => Open(name));
        b.BackColor = Color.FromArgb(0x3B, 0x2F, 0x12); b.ForeColor = Color.FromArgb(0xFF, 0xD2, 0x7A);
        return b;
    }
}

/// <summary>Settings → 🛠 Nova Studio: choose the mode, set it up step by step (each with its 📘 guide), the safety rules. Designed by A.HAMROUNI</summary>
partial class SettingsForm
{
    void StudioPage()
    {
        var head = page.Add(Th.Row(false));
        head.Controls.Add(Th.L("Develop Nova from inside Nova, with your own AI. Every change: you approve → tests pass → signed build → ⬆ update (⟲ back to the previous build in one click).", 9.5f, Th.Muted));
        var gr = page.Add(Th.Row());
        gr.Controls.Add(Th.B("💬 Open the Studio chat", () => Screens.ShowForm(new StudioForm()), true));
        gr.Controls.Add(Guides.Btn("studio-overview", "📘 Guide · Nova Studio"));
        gr.Controls.Add(Th.B("⟲ Back to the previous build", RollBack));

        // ① the mode
        var r1 = page.Add(Th.Row(false)); r1.Controls.Add(Th.Section("① Choose how Nova develops itself")); r1.Controls.Add(Guides.Btn("studio-which-mode", "📘 Which mode for me?"));
        var modes = page.Add(Th.Row());
        foreach (var (id, title, sub) in new[] { ("C", "⚡ C · Live extensions", "rules, prompts — instant, no build"), ("A", "🐙 A · GitHub pipeline", "real code; GitHub builds & tests"), ("B", "🤖 B · Coding agent on this PC", "strongest; builds here, then pushes") })
        {
            var on = Studio.Mode == id;
            var b = Th.B($"{(on ? "● " : "")}{title}\n{sub}", () => { Studio.Mode = id; Draw("🛠 Nova Studio"); }, on);
            b.AutoSize = false; b.Width = 300; b.Height = 54; b.TextAlign = ContentAlignment.MiddleLeft;
            modes.Controls.Add(b);
        }

        // ② the setup
        var r2 = page.Add(Th.Row(false)); r2.Controls.Add(Th.Section($"② Set up · mode {Studio.Mode}")); r2.Controls.Add(Guides.Btn("studio-overview", "📘 Setup step by step"));
        Label Step(string title, string guide, params Control[] buttons)
        {
            var row = page.Add(Th.Row(false));
            var l = Th.L("⏳ " + title, 10, Th.Text); l.AutoSize = true; l.MaximumSize = new Size(640, 0);
            row.Controls.Add(l);
            foreach (var b in buttons) row.Controls.Add(b);
            row.Controls.Add(Guides.Btn(guide));
            return l;
        }
        void Set(Label l, string title, string r) { if (l.IsDisposed) return; var ok = r.StartsWith("✓"); l.Text = (ok ? "✓ " : "⚠ ") + title + " · " + r.TrimStart('✓', '⚠', '⏳', ' '); l.ForeColor = ok ? Th.Good : Th.Warn; }
        void Show(Label l, string title, Func<Task<string>> f)
        {
            l.Text = "⏳ " + title + " · checking…"; l.ForeColor = Th.Text;
            Task.Run(f).ContinueWith(x => Program.UiContext.Post(_ => Set(l, title, x.IsFaulted ? "⚠ " + x.Exception?.GetBaseException().Message : x.Result), null));
        }

        // 🔑 AI key
        var aiBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, BackColor = Th.Card, ForeColor = Th.Text, FlatStyle = FlatStyle.Flat };
        foreach (var p in Ai.All) aiBox.Items.Add(p.Label);
        aiBox.SelectedIndex = Math.Max(0, Array.FindIndex(Ai.All, p => p.Id == Studio.AiId));
        aiBox.SelectedIndexChanged += (_, _) => { Studio.AiId = Ai.All[aiBox.SelectedIndex].Id; Draw("🛠 Nova Studio"); };
        var budget = new NumericUpDown { Minimum = 1, Maximum = 5000, Value = Math.Clamp(Studio.Budget, 1, 5000), Width = 70 };
        budget.ValueChanged += (_, _) => Studio.Budget = (int)budget.Value;
        Label lAi = null;
        lAi = Step("🔑 AI key", "studio-setup-ai-key", aiBox, Th.L(" budget USD / month", 9, Th.Muted), budget,
            Th.B("🔑 Keys…", () => Go("AI & API keys")), Th.B("Test", () => Show(lAi, "🔑 AI key", () => Studio.TestAi(CancellationToken.None))));
        Show(lAi, "🔑 AI key", () => Studio.TestAi(CancellationToken.None));

        if (Studio.Mode is "A" or "B")
        {
            // 🐙 GitHub
            var repo = Th.Box(false); repo.Width = 230; repo.Text = Studio.Repo; repo.Leave += (_, _) => Studio.Repo = repo.Text;
            var lGh = Step("🐙 GitHub", "studio-setup-github", repo,
                Th.B("Create a key…", () => Shell.Open("https://github.com/settings/personal-access-tokens/new")),
                Th.B("Paste the key", () =>
                {
                    var t = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
                    if (!Regex.IsMatch(t, @"^(github_pat_|ghp_)[A-Za-z0-9_]{20,}$")) { MessageBox.Show(this, "Copy the GitHub key first (it starts with github_pat_ or ghp_), then press Paste the key.", "🐙 GitHub"); return; }
                    Studio.Token = t; try { Clipboard.Clear(); } catch { }
                    Draw("🛠 Nova Studio");
                }),
                Th.B("Sign out", () => { Studio.Token = ""; Draw("🛠 Nova Studio"); }));
            Show(lGh, "🐙 GitHub", () => Studio.TestGitHub(CancellationToken.None));
            // ⚙ build check
            var lB = Step("⚙ Build check", "studio-setup-build", Th.B("Run again", () => Draw("🛠 Nova Studio")));
            if (Studio.Token.Length > 0) Show(lB, "⚙ Build check", () => Studio.BuildCheck(CancellationToken.None)); else lB.Text = "⏳ ⚙ Build check · after GitHub";
        }
        if (Studio.Mode == "B")
        {
            var lT = Step("🧰 Tools on this PC", "studio-setup-tools", Th.B("Check again", () => Draw("🛠 Nova Studio")));
            var tools = page.Add(Th.Row());
            Task.Run(Studio.Tools).ContinueWith(t => Program.UiContext.Post(_ =>
            {
                if (lT.IsDisposed || t.IsFaulted) return;
                var miss = t.Result.Where(x => x.Value.Length == 0).Select(x => x.Key).ToList();
                Set(lT, "🧰 Tools", (miss.Count == 0 ? "✓ " : "⚠ ") + string.Join(" · ", t.Result.Select(x => x.Key + " " + (x.Value.Length > 0 ? x.Value + " ✓" : "missing"))));
                foreach (var m in miss)
                {
                    var mm = m;
                    tools.Controls.Add(Th.B("Install " + mm, () => Studio.Install(mm, s => Program.UiContext.Post(__ => { if (!lT.IsDisposed) lT.Text = s; }, null))
                        .ContinueWith(r => Program.UiContext.Post(__ => Set(lT, "🧰 " + mm, r.Result), null)), true));
                }
            }, null));
            Label lS = null;
            lS = Step("🤖 Coding agent · its own copy of the source", "studio-setup-tools", Th.B("Prepare the copy", () =>
            {
                lS.Text = "⏳ copying…";
                Studio.Source(s => Program.UiContext.Post(_ => { if (!lS.IsDisposed) lS.Text = s; }, null), CancellationToken.None)
                    .ContinueWith(t => Program.UiContext.Post(_ => Set(lS, "🤖 Coding agent", t.IsFaulted ? "⚠ " + t.Exception?.GetBaseException().Message : "✓ source copy ready: " + t.Result), null));
            }));
            if (Directory.Exists(Path.Combine(Studio.SourceDir, ".git"))) Set(lS, "🤖 Coding agent", "✓ source copy ready: " + Studio.SourceDir);
            else lS.Text = "⏳ 🤖 Coding agent · press Prepare the copy (once) — it works only in " + Studio.SourceDir;
        }
        Step("🧪 First run — a small sample change goes all the way to ⬆ Updates", "studio-how-to-ask",
            Th.B("Try it", () => Screens.ShowForm(new StudioForm("First run: in the About page, add one line “Nova Studio is set up” under the build number. Done when the line shows after the update."))));

        // ③ the rules
        var r3 = page.Add(Th.Row(false)); r3.Controls.Add(Th.Section("③ What the Studio AI may use")); r3.Controls.Add(Guides.Btn("studio-safety", "📘 Safety rules"));
        Note("✓ Free (read-only): Nova's source code · error log · freezes · crashes · performance · jobs · settings without secrets · index counts · folder structure · add-ins", Th.Good);
        Note("⚠ Your OK each time: sending a document, an email or a screenshot to the AI · installing a change", Th.Warn);
        Note("✗ Never: passwords · keys · tokens · PIN · VPN · OTP · 🔒 private files · email content you did not attach", Color.FromArgb(0xFF, 0x7A, 0x7A));
    }

    void RollBack()
    {
        if (MessageBox.Show(this, "Go back to the previous signed build of Nova? Nova closes, installs it and opens again.", "⟲ Back to the previous build", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        Updater.RollBack().ContinueWith(t => Program.UiContext.Post(_ => MessageBox.Show(t.IsFaulted ? "⚠ " + t.Exception?.GetBaseException().Message : t.Result, "⟲ Nova"), null));
    }
}

/// <summary>💬 The Studio chat: ask for a feature or a fix; the AI shows the plan and the change; ✓ GO sends it to the build. Designed by A.HAMROUNI</summary>
class StudioForm : NovaForm
{
    readonly FlowLayoutPanel msgs;
    readonly TextBox input;
    readonly Label status;
    readonly List<string> attached = new();
    readonly Label attachLine;
    CancellationTokenSource cts;

    public StudioForm(string start = null)
    {
        Text = "🛠 Nova Studio · chat";
        Width = 1240; Height = 860;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(10, 8, 10, 0), BackColor = Th.Bg };
        top.Controls.Add(Th.L("🛠 Nova Studio · chat", 13, Th.Text, true));
        top.Controls.Add(Th.L($"   mode {Studio.Mode} · {Ai.LabelOf(Studio.AiProvider)} · ≈ {Studio.SpentUsd:0.00} / {Studio.Budget} USD this month", 9.5f, Th.Muted));
        top.Controls.Add(Guides.Btn("studio-how-to-ask", "📘 How to ask for a feature"));
        top.Controls.Add(Guides.Btn("studio-overview", "📘 Guide"));
        top.Controls.Add(Th.B("⚙ Setup", () => Screens.Settings("🛠 Nova Studio")));
        msgs = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14), BackColor = Th.Bg };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(10), BackColor = Color.FromArgb(0x0E, 0x11, 0x17) };
        input = Th.Box(true, 3); input.Dock = DockStyle.Fill; input.PlaceholderText = "Ask for a feature, a fix, or paste an error… (Enter = send · Shift+Enter = new line)";
        input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); } };
        var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, BackColor = bottom.BackColor };
        btns.Controls.Add(Th.B("➤ Send", Send, true));
        btns.Controls.Add(Th.B("📎 Files", AddFiles));
        btns.Controls.Add(Th.B("🖼 Screenshot", AddScreenshot));
        btns.Controls.Add(Th.B("🩺 Add today's errors", AddErrors));
        btns.Controls.Add(Th.B("📋 Paste a log", () => { if (Clipboard.ContainsText()) { attached.Add("PASTED:\n" + Clipboard.GetText()); ShowAttached(); } }));
        btns.Controls.Add(Th.B("■ Stop", () => cts?.Cancel()));
        btns.Controls.Add(Th.B("⟲ Back to the previous build", () =>
        {
            if (MessageBox.Show(this, "Go back to the previous signed build?", "⟲ Nova", MessageBoxButtons.YesNo) == DialogResult.Yes)
                Updater.RollBack().ContinueWith(t => Program.UiContext.Post(_ => Say(t.IsFaulted ? "⚠ " + t.Exception?.GetBaseException().Message : t.Result), null));
        }));
        attachLine = new Label { Dock = DockStyle.Bottom, Height = 20, ForeColor = Th.Muted, Font = Th.F(9) };
        status = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Th.Muted, Font = Th.F(9.5f), Padding = new Padding(14, 2, 0, 0) };
        bottom.Controls.Add(input); bottom.Controls.Add(attachLine); bottom.Controls.Add(btns);
        Controls.Add(msgs); Controls.Add(status); Controls.Add(bottom); Controls.Add(top);
        FormClosing += (_, _) => cts?.Cancel();
        Say($"👋 Mode {Studio.Mode}: " + (Studio.Mode == "C" ? "I make 🧠 rules and ⭐ prompts — applied at once, no build." : Studio.Mode == "A" ? "I read Nova's source on GitHub and propose exact edits; on ✓ GO, GitHub builds, tests and signs." : "The coding agent edits and builds its own copy of Nova here; on ✓ GO it is pushed and GitHub signs it.") +
            " Nothing changes until you press ✓.");
        if (!string.IsNullOrEmpty(start)) { input.Text = start; }
    }

    int W => Math.Max(500, msgs.ClientSize.Width - 60);
    Label Say(string text, bool me = false, Color? c = null)
    {
        var l = Th.Wrap(text, 10, c ?? Th.Text);
        l.MaximumSize = new Size(W, 0); l.AutoSize = true; l.Padding = new Padding(10, 8, 10, 8); l.Margin = new Padding(me ? 120 : 0, 4, me ? 0 : 120, 4);
        l.BackColor = me ? Color.FromArgb(0x2E, 0x2A, 0x55) : Color.FromArgb(0x1B, 0x20, 0x30);
        msgs.Controls.Add(l); msgs.ScrollControlIntoView(l);
        return l;
    }
    FlowLayoutPanel Buttons()
    {
        var f = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 8), BackColor = Th.Bg };
        msgs.Controls.Add(f); msgs.ScrollControlIntoView(f);
        return f;
    }
    void Busy(string s) => Program.UiContext.Post(_ => { if (!status.IsDisposed) status.Text = s; }, null);
    void ShowAttached() => attachLine.Text = attached.Count == 0 ? "" : "📎 attached: " + string.Join(" · ", attached.Select(a => a.Split('\n')[0]).Take(6));

    void AddFiles()
    {
        using var d = new OpenFileDialog { Multiselect = true, Filter = "Text, code, logs, CSV|*.txt;*.log;*.cs;*.js;*.html;*.json;*.csv;*.md;*.xml|All files|*.*" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (var f in d.FileNames)
        {
            if (Projects.Private.IsMatch(f)) { Say("🔒 " + Path.GetFileName(f) + " looks private — not attached.", false, Th.Warn); continue; }
            string text;
            try { text = File.ReadAllText(f); } catch { try { text = string.Join("\n", FileText.Extract(f).Select(s => s.Text)); } catch (Exception e) { Say("⚠ " + e.Message); continue; } }
            if (MessageBox.Show(this, $"Send {Path.GetFileName(f)} ({text.Length:N0} characters) to {Ai.LabelOf(Studio.AiProvider)} with your next message?", "📎 Your OK", MessageBoxButtons.YesNo) != DialogResult.Yes) continue;
            attached.Add($"FILE {Path.GetFileName(f)}:\n{(text.Length > 40000 ? text[..40000] + "\n…" : text)}");
        }
        ShowAttached();
    }

    void AddScreenshot()
    {
        if (!Clipboard.ContainsImage()) { Say("🖼 Copy a screenshot first (Win+Shift+S), then press 🖼 Screenshot.", false, Th.Warn); return; }
        if (MessageBox.Show(this, $"Send the screenshot in the clipboard to {Ai.LabelOf(Studio.AiProvider)}? (It is read once to describe what it shows.)", "🖼 Your OK", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        var img = Clipboard.GetImage();
        using var ms = new MemoryStream(); img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        Busy("✦ Reading the screenshot…");
        Ai.Vision(ms.ToArray(), "image/png", CancellationToken.None, "Describe this screenshot of the Nova Enhance app precisely: the window, every visible label, numbers, errors and what looks wrong.")
            .ContinueWith(t => Program.UiContext.Post(_ =>
            {
                Busy("");
                if (t.IsFaulted) { Say("⚠ " + t.Exception?.GetBaseException().Message, false, Th.Warn); return; }
                attached.Add("SCREENSHOT (described):\n" + t.Result); ShowAttached();
            }, null));
    }

    void AddErrors()
    {
        Busy("🩺 Reading today's errors…");
        Studio.Diagnostics().ContinueWith(t => Program.UiContext.Post(_ => { Busy(""); attached.Add("TODAY'S DIAGNOSTICS:\n" + (t.IsFaulted ? t.Exception?.GetBaseException().Message : t.Result)); ShowAttached(); }, null));
    }

    void Send()
    {
        var ask = input.Text.Trim();
        if (ask.Length == 0) return;
        input.Text = "";
        Say(ask + (attached.Count > 0 ? $"\n📎 {attached.Count} attachment(s)" : ""), true);
        var att = string.Join("\n\n", attached); attached.Clear(); ShowAttached();
        cts?.Cancel(); var my = cts = new CancellationTokenSource();
        var mode = Studio.Mode;
        Task.Run(async () =>
        {
            try
            {
                if (mode == "C") await DoC(ask, att, my.Token);
                else if (mode == "A") await DoA(ask, att, my.Token);
                else await DoB(ask, att, my.Token);
            }
            catch (OperationCanceledException) { Program.UiContext.Post(_ => Say("■ Stopped."), null); }
            catch (Exception e) { Program.UiContext.Post(_ => Say("⚠ " + e.Message, false, Th.Warn), null); }
            finally { Busy(""); }
        });
    }

    async Task DoC(string ask, string att, CancellationToken ct)
    {
        Busy("✦ Thinking…");
        var items = await Studio.ProposeExt(ask, att, ct);
        Program.UiContext.Post(_ =>
        {
            if (items.Count == 0) { Say("Nothing to add as a rule or a prompt — this wish needs code: switch to mode A or B (⚙ Setup) and send it again."); return; }
            foreach (var e in items)
            {
                if (e.Kind == "note") { Say("ℹ " + e.Text); continue; }
                Say(e.Kind == "rule" ? $"🧠 Rule for “{e.Section}”: {e.Title}\n{e.Text}\n— {e.Why}" : $"⭐ Prompt “{e.Name}”: {e.Question}\n— {e.Why}");
                var b = Buttons(); var ee = e;
                b.Controls.Add(Th.B("✓ GO — add it", () => { try { Say(Studio.ApplyExt(ee), false, Th.Good); } catch (Exception x) { Say("⚠ " + x.Message, false, Th.Warn); } }, true));
                b.Controls.Add(Th.B("✗ Drop", () => b.Enabled = false));
            }
        }, null);
    }

    async Task DoA(string ask, string att, CancellationToken ct)
    {
        var plan = await Studio.Propose(ask, att, Busy, ct);
        Program.UiContext.Post(_ =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("📋 " + plan.Summary);
            sb.AppendLine($"Files: {string.Join(" · ", plan.Edits.Select(x => x.Path).Distinct())}");
            foreach (var e in plan.Edits.Take(12))
            {
                sb.AppendLine($"\n── {e.Path} — {e.Why}");
                foreach (var l in e.Old.Split('\n').Take(6)) if (l.Trim().Length > 0) sb.AppendLine("- " + l.TrimEnd());
                foreach (var l in e.New.Split('\n').Take(10)) if (l.Trim().Length > 0) sb.AppendLine("+ " + l.TrimEnd());
            }
            if (plan.Note.Length > 0) sb.AppendLine("\nℹ " + plan.Note);
            var l2 = Say(sb.ToString()); l2.Font = new Font("Consolas", 9.5f);
            var b = Buttons();
            b.Controls.Add(Th.B("✓ GO — push & build", () =>
            {
                b.Enabled = false;
                Task.Run(async () =>
                {
                    try
                    {
                        Busy("🐙 Sending the change to GitHub…");
                        var sha = await Studio.Push(plan, ask.Length > 70 ? ask[..70] : ask, CancellationToken.None);
                        Program.UiContext.Post(__ => Say($"✓ Sent ({sha[..7]}). GitHub builds and runs the tests now — I'll tell you here."), null);
                        await Watch(sha);
                    }
                    catch (Exception e) { Program.UiContext.Post(__ => Say("⚠ " + e.Message, false, Th.Warn), null); }
                });
            }, true));
            b.Controls.Add(Th.B("✎ Change it", () => { input.Text = "Change the plan: "; input.Focus(); input.SelectionStart = input.Text.Length; }));
            b.Controls.Add(Th.B("✗ Drop", () => b.Enabled = false));
        }, null);
    }

    async Task Watch(string sha)
    {
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(30_000);
            if (IsDisposed) return;
            var (done, ok, line) = await Studio.BuildOf(sha, CancellationToken.None);
            Busy(line);
            if (done)
            {
                Program.UiContext.Post(_ => Say(line, false, ok ? Th.Good : Th.Warn), null);
                if (ok) { await Task.Delay(60_000); var s = await Updater.Check(); Program.UiContext.Post(_ => { Say("⬆ " + s); Updater.Offer(true); }, null); }
                return;
            }
        }
    }

    async Task DoB(string ask, string att, CancellationToken ct)
    {
        var log = new StringBuilder();
        Label live = null;
        Program.UiContext.Post(_ => live = Say("🤖 starting…"), null);
        var last = DateTime.MinValue;
        var (diff, built, errs) = await Studio.Agent(ask, att, l =>
        {
            lock (log) { log.AppendLine(l); }
            if (DateTime.Now - last < TimeSpan.FromMilliseconds(700)) return;
            last = DateTime.Now;
            string t; lock (log) { t = string.Join("\n", log.ToString().Split('\n').TakeLast(14)); }
            Program.UiContext.Post(_ => { if (live != null && !live.IsDisposed) live.Text = "🤖 " + t; }, null);
        }, ct);
        Program.UiContext.Post(_ =>
        {
            Say($"📋 Changes:\n{(diff.Length > 0 ? diff : "(none)")}\n\n{(built ? "🧪 Built here ✓" : "✗ The build failed here:\n" + errs)}", false, built ? Th.Text : Th.Warn);
            if (diff.Length == 0) return;
            var b = Buttons();
            var go = Th.B("✓ GO — push & build", () =>
            {
                b.Enabled = false;
                Task.Run(async () =>
                {
                    try
                    {
                        var sha = await Studio.PushLocal(ask.Length > 70 ? ask[..70] : ask, CancellationToken.None);
                        Program.UiContext.Post(__ => Say($"✓ Pushed ({sha[..Math.Min(7, sha.Length)]}). GitHub builds, tests and signs it now."), null);
                        await Watch(sha);
                    }
                    catch (Exception e) { Program.UiContext.Post(__ => Say("⚠ " + e.Message, false, Th.Warn), null); }
                });
            }, true);
            go.Enabled = built;
            b.Controls.Add(go);
            b.Controls.Add(Th.B("✎ Ask for more", () => { input.Text = built ? "Also: " : "The build failed — fix it."; input.Focus(); }));
            b.Controls.Add(Th.B("📂 Open the copy", () => Shell.Open(Studio.SourceDir)));
        }, null);
    }
}
