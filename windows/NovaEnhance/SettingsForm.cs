namespace NovaEnhance;

/// <summary>Settings, split into sections like the phone app.</summary>
class SettingsForm : NovaForm
{
    static readonly string[] AllSections = { "AI & API keys", "About me", "Enhancement", "Email & AI", "📥 Email index", "🗂 Mailboxes & archives", "📂 Folders in the index", "📁 Email projects", "Folder search", "📁 Projects", "✦ Prompts", "🧠 What Nova knows", "Spelling & grammar", "Google Drive", "Office tab", "Browser add-on", "AI window", "Shortcuts", "Backup & restore", "Updates", "Error log", "🧪 Activity history", "🔌 Test connector", "📱 Phone link", "About" };
    /// <summary>🧪 Activity history and 🔌 Test connector are advanced tools: shown only when switched on (About → Advanced tools).</summary>
    static readonly string[] AdvancedSections = { "🧪 Activity history", "🔌 Test connector" };
    static string[] Sections = Visible();
    static string[] Visible() => AllSections.Where(s => Store.Bool("win_advanced", false) || !AdvancedSections.Contains(s)).ToArray();
    readonly ListBox nav;
    readonly Panel host;
    Stack page;
    readonly System.Windows.Forms.Timer live;
    Label liveIndex, liveUpdate;

    public SettingsForm()
    {
        Text = "Nova Enhance · Settings";
        Width = 960; Height = 760;
        MinimumSize = new Size(760, 520);
        nav = new ListBox
        {
            Dock = DockStyle.Left, Width = 210, BackColor = Color.FromArgb(0x0E, 0x11, 0x16), ForeColor = Th.Text, BorderStyle = BorderStyle.None,
            Font = Th.F(10.5f), DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 38, IntegralHeight = false
        };
        Sections = Visible(); nav.Items.AddRange(Sections);
        nav.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var sel = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(sel ? Th.Card2 : nav.BackColor);
            e.Graphics.FillRectangle(bg, e.Bounds);
            var subPage = e.Index > 0 && Array.IndexOf(Sections, "Email & AI") is var ea && e.Index > ea && e.Index <= ea + 4;
            var ind = subPage ? 34 : 16;
            TextRenderer.DrawText(e.Graphics, Sections[e.Index], Th.F(subPage ? 9.8f : 10.5f, sel), new Rectangle(e.Bounds.X + ind, e.Bounds.Y, e.Bounds.Width - ind, e.Bounds.Height),
                sel ? Color.White : Color.FromArgb(0xC9, 0xCE, 0xD8), TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        };
        nav.SelectedIndexChanged += (_, _) => Draw(Sections[Math.Max(0, nav.SelectedIndex)]);
        host = new Panel { Dock = DockStyle.Fill, BackColor = Th.Bg };
        Controls.Add(host);
        Controls.Add(nav);
        live = new System.Windows.Forms.Timer { Interval = 2000 };
        live.Tick += (_, _) =>
        {
            if (liveIndex != null && !liveIndex.IsDisposed) liveIndex.Text = MailIndex.Status;
            if (liveUpdate != null && !liveUpdate.IsDisposed) liveUpdate.Text = UpdateText();
        };
        live.Start();
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        // 🛡 If a page did not finish opening last time, start on the simple About page.
        var stuck = Store.Str("win_set_drawing", "");
        if (stuck.Length > 0) { Store.Set("win_set_drawing", ""); Log.Warn("settings", $"the page “{stuck}” did not open last time — starting on About"); nav.SelectedIndex = Array.IndexOf(Sections, "About"); }
        else nav.SelectedIndex = Ai.WithKeys.Length == 0 ? 0 : 3;
    }

    public void Go(string section)
    {
        var i = Array.IndexOf(Sections, section);
        if (i >= 0) nav.SelectedIndex = i;
        else if (AdvancedSections.Contains(section)) Draw(section);
    }

    void Draw(string section)
    {
        host.SuspendLayout();
        page?.Dispose();
        page = new Stack { Padding = new Padding(24, 14, 24, 12) };
        host.Controls.Add(page);
        page.Add(Th.L(section, 15, Th.Text, true));
        liveIndex = null; liveUpdate = null;
        Store.Set("win_set_drawing", section);
        UiWatch.Step("Settings · " + section);
        using var act = Activity.Begin("settings page", section);
        try
        {
            switch (section)
            {
                case "AI & API keys": AiKeys(); break;
                case "About me": AboutMe(); break;
                case "Enhancement": Enhancement(); break;
                case "Email & AI": EmailAi(); break;
                case "📥 Email index": EmailIndexPage(); break;
                case "🗂 Mailboxes & archives": MailboxesPage(); break;
                case "📂 Folders in the index": FoldersPage(); break;
                case "📁 Email projects": EmailProjectsSection(); break;
                case "Folder search": FolderSearch(); break;
                case "📁 Projects": ProjectsPage(); break;
                case "Google Drive": GoogleDrive(); break;
                case "Shortcuts": Shortcuts(); break;
                case "Browser add-on": BrowserPage(); break;
                case "AI window": AiWindowPage(); break;
                case "🧠 What Nova knows": KnowsPage(); break;
                case "✦ Prompts": PromptsPage(); break;
                case "Spelling & grammar": SpellPage(); break;
                case "Backup & restore": BackupPage(); break;
                case "Updates": Updates(); break;
                case "Error log": ErrorLog(); break;
                case "🧪 Activity history": ActivityPage(); break;
                case "🔌 Test connector": TestConnectorPage(); break;
                case "📱 Phone link": PhoneLinkPage(); break;
                case "Office tab": OfficeTab(); break;
                default: About(); break;
            }
        }
        catch (Exception e) { page.Add(Th.Wrap("⚠ " + e.Message, 10, Th.Bad)); Log.Error("settings " + section, e); }
        Store.Set("win_set_drawing", "");
        act.Mark("page drawn");
        page.Add(Th.Signature());
        host.ResumeLayout();
        page.FitAll();
    }

    Label Note(string s, Color? c = null) => page.Add(Th.Wrap(s, 9.5f, c));

    // ------------------------------------------------------------------ AI & API keys
    void AiKeys()
    {
        Note("Your keys stay on this PC, locked to your Windows account. Gemini is first, as on the phone. Tip: restore your phone backup (Backup & restore) to bring the keys over.");
        page.Add(Th.Section("Default AI"));
        var row = page.Add(Th.Row());
        foreach (var p in Ai.All)
        {
            var pp = p;
            var r = Th.Radio(p.Label + (Ai.HasKey(p.Id) ? " ✓" : ""), Store.Target == p.Id, () => Store.Target = pp.Id);
            row.Controls.Add(r);
        }
        // How Nova chooses between your cloud AI and the local AI.
        page.Add(Th.Section("💻 Local or ☁ cloud — which AI does the work"));
        Opt(new[] { ("manual", "☁ My default AI above for everything (you can pick Local AI there too)"),
                    ("private", "💻 Local AI for my emails and documents (they stay on this PC) · ☁ default AI for the rest"),
                    ("local-first", "💻 Local AI first for everything · ☁ default AI when the local one is not running or fails") }, Ai.Route, v => Ai.Route = v);
        page.Add(Th.Check("Don't ask \"Send to the AI?\" when the local AI does the work (nothing leaves this PC)", Ai.LocalNoAsk, v => Ai.LocalNoAsk = v));
        page.Add(Th.Section("👁 Reading scanned pages and drawings (vision)"));
        Opt(new[] { ("gemini", "☁ Gemini vision (cloud, best quality)"), ("local", "💻 Local AI vision on this PC (a vision model, e.g. Gemma 4 · Qwen-VL · LLaVA)") }, Ai.VisionBy, v => Ai.VisionBy = v);

        LocalSection();
        ServerSection();

        foreach (var p in Ai.All.Where(x => x.Id is not ("local" or "server")))
        {
            var pp = p;
            page.Add(Th.Section(p.Label));
            var grid = page.Add(new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) }, true);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.Controls.Add(Th.L("API key", 9.5f, Th.Muted), 0, 0);
            var key = Th.Box(false, 1, true); key.Text = Store.Key(p.Id); key.Dock = DockStyle.Fill;
            grid.Controls.Add(key, 1, 0);
            grid.Controls.Add(Th.L("Model", 9.5f, Th.Muted), 0, 1);
            var model = Th.NoSpell(Th.Box(false)); model.Text = Store.Model(p.Id); model.Dock = DockStyle.Fill;
            grid.Controls.Add(model, 1, 1);
            var btns = page.Add(Th.Row(false));
            var status = Th.L("", 9.5f, Th.Muted);
            btns.Controls.Add(Th.B("Save & test", async () =>
            {
                Store.SetKey(pp.Id, key.Text);
                Store.SetModel(pp.Id, model.Text.Trim().Length == 0 ? pp.DefaultModel : model.Text);
                if (key.Text.Trim().Length == 0) { status.Text = "Key removed"; return; }
                status.Text = "Testing…"; status.ForeColor = Th.Muted;
                try { var s = await Ai.Test(pp); status.Text = $"✓ Works · {s:0.0} s"; status.ForeColor = Th.Good; }
                catch (Exception e) { status.Text = "⚠ " + e.Message; status.ForeColor = Th.Bad; }
            }, true));
            btns.Controls.Add(Th.B("Get a key", () => Shell.Open(pp.KeyUrl)));
            status.Margin = new Padding(4, 8, 0, 0);
            btns.Controls.Add(status);
        }
    }

    ComboBox Combo(IEnumerable<string> items, string value, int width)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = width, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10), FlatStyle = FlatStyle.Flat };
        foreach (var i in items) c.Items.Add(i);
        c.Text = value;
        return c;
    }

    /// <summary>💻 Local AI: LM Studio, Ollama, Jan, GPT4All, llama.cpp, KoboldCpp — any OpenAI-compatible server on this PC (or on your network).</summary>
    void LocalSection()
    {
        page.Add(Th.Section("💻 Local AI (on this PC)"));
        Note("Runs on your own computer with LM Studio, Ollama, Jan, GPT4All, llama.cpp or KoboldCpp — no key, and nothing leaves this PC. Start the server in the app (LM Studio: Developer → Local server → Start), then 🔍 Detect. Tip: set the model's context length to 16,000 or more in LM Studio so long emails and documents fit.", Th.Muted);
        var on = page.Add(Th.Check("Use the local AI (it appears as \"Local AI\" in Default AI above)", Ai.LocalOn, v => Ai.LocalOn = v));
        var r1 = page.Add(Th.Row(false));
        r1.Controls.Add(Th.L("Server", 9.5f, Th.Muted));
        var url = Combo(Ai.LocalPresets.Select(p => p.Url), Ai.LocalUrl, 300);
        r1.Controls.Add(url);
        var preset = Combo(Ai.LocalPresets.Select(p => p.Name), "", 130);
        preset.DropDownStyle = ComboBoxStyle.DropDownList;
        preset.SelectedIndexChanged += (_, _) => { if (preset.SelectedIndex >= 0) url.Text = Ai.LocalPresets[preset.SelectedIndex].Url; };
        r1.Controls.Add(preset);
        var r2 = page.Add(Th.Row(false));
        r2.Controls.Add(Th.L("Model", 9.5f, Th.Muted));
        var model = Combo(new string[0], Store.Model("local"), 300);
        r2.Controls.Add(model);
        r2.Controls.Add(Th.L("Vision model", 9.5f, Th.Muted));
        var vmodel = Combo(new string[0], Store.Str("win_local_vision_model"), 220);
        r2.Controls.Add(vmodel);
        var r3 = page.Add(Th.Row(false));
        r3.Controls.Add(Th.L("Context length (tokens) set in your local app", 9.5f, Th.Muted));
        var ctx = new NumericUpDown { Minimum = 2048, Maximum = 262144, Increment = 1024, Value = Math.Clamp(Ai.LocalContext, 2048, 262144), Width = 90, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        ctx.ValueChanged += (_, _) => Ai.LocalContext = (int)ctx.Value;
        r3.Controls.Add(ctx);
        var btns = page.Add(Th.Row(false));
        var st = Th.L("", 9.5f, Th.Muted); st.Margin = new Padding(4, 8, 0, 0);
        btns.Controls.Add(Th.B("🔍 Detect", async () =>
        {
            st.Text = "Looking for a local AI server…"; st.ForeColor = Th.Muted;
            var d = await Ai.Detect();
            if (d == null) { st.Text = "⚠ No local AI server found — start it in LM Studio / Ollama first"; st.ForeColor = Th.Bad; return; }
            url.Text = d.Value.Url;
            model.Items.Clear(); vmodel.Items.Clear();
            foreach (var m in d.Value.Models) { model.Items.Add(m); vmodel.Items.Add(m); }
            if (d.Value.Models.Count > 0 && (model.Text.Length == 0 || !d.Value.Models.Contains(model.Text)))
                model.Text = d.Value.Models.FirstOrDefault(m => m.Contains("gemma", StringComparison.OrdinalIgnoreCase)) ?? d.Value.Models[0];
            st.Text = $"✓ {d.Value.Name} found · {d.Value.Models.Count} model(s)"; st.ForeColor = Th.Good;
        }, true));
        btns.Controls.Add(Th.B("Save & test", async () =>
        {
            Ai.LocalUrl = url.Text; Store.SetModel("local", model.Text); Store.Set("win_local_vision_model", vmodel.Text.Trim());
            if (model.Text.Trim().Length == 0)
                try { var ms = await Ai.Models(url.Text); if (ms.Count > 0) { model.Text = ms[0]; Store.SetModel("local", ms[0]); } } catch { }
            Ai.LocalOn = true; on.Checked = true;
            st.Text = "Testing (the first answer can take a while while the model loads)…"; st.ForeColor = Th.Muted;
            try { var s = await Ai.Test(Ai.ById("local")); st.Text = $"✓ Works · {s:0.0} s · {model.Text}"; st.ForeColor = Th.Good; }
            catch (Exception e) { st.Text = "⚠ " + e.Message; st.ForeColor = Th.Bad; }
        }));
        btns.Controls.Add(Th.B("Get LM Studio", () => Shell.Open("https://lmstudio.ai")));
        btns.Controls.Add(st);
    }

    /// <summary>🌐 Any OpenAI-compatible service: Groq, OpenRouter, Mistral, Together, Perplexity or a company server.</summary>
    void ServerSection()
    {
        page.Add(Th.Section("🌐 Other server (web or company)"));
        Note("Groq (free and very fast), OpenRouter (hundreds of models with one key), Mistral, Together, Perplexity, or your office server — anything that speaks the OpenAI format.", Th.Muted);
        var on = page.Add(Th.Check("Use this server (it appears as \"Other server\" in Default AI above)", Ai.ServerOn, v => Ai.ServerOn = v));
        var r0 = page.Add(Th.Row(false));
        r0.Controls.Add(Th.L("Service", 9.5f, Th.Muted));
        var preset = Combo(Ai.ServerPresets.Select(p => p.Name), Store.Str("win_server_name", ""), 280);
        preset.DropDownStyle = ComboBoxStyle.DropDownList;
        var i0 = Array.FindIndex(Ai.ServerPresets, p => p.Name == Store.Str("win_server_name", ""));
        if (i0 >= 0) preset.SelectedIndex = i0;
        r0.Controls.Add(preset);
        var grid = page.Add(new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) }, true);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.Controls.Add(Th.L("Address", 9.5f, Th.Muted), 0, 0);
        var url = Th.NoSpell(Th.Box(false)); url.Text = Ai.ServerUrl; url.Dock = DockStyle.Fill; grid.Controls.Add(url, 1, 0);
        grid.Controls.Add(Th.L("API key", 9.5f, Th.Muted), 0, 1);
        var key = Th.Box(false, 1, true); key.Text = Store.Key("server"); key.Dock = DockStyle.Fill; grid.Controls.Add(key, 1, 1);
        grid.Controls.Add(Th.L("Model", 9.5f, Th.Muted), 0, 2);
        var model = Th.NoSpell(Th.Box(false)); model.Text = Store.Model("server"); model.Dock = DockStyle.Fill; grid.Controls.Add(model, 1, 2);
        preset.SelectedIndexChanged += (_, _) =>
        {
            if (preset.SelectedIndex < 0) return;
            var p = Ai.ServerPresets[preset.SelectedIndex];
            if (p.Url.Length > 0) url.Text = p.Url;
            if (p.Model.Length > 0) model.Text = p.Model;
        };
        var btns = page.Add(Th.Row(false));
        var st = Th.L("", 9.5f, Th.Muted); st.Margin = new Padding(4, 8, 0, 0);
        btns.Controls.Add(Th.B("Save & test", async () =>
        {
            Ai.ServerUrl = url.Text; Store.SetKey("server", key.Text); Store.SetModel("server", model.Text);
            Store.Set("win_server_name", preset.SelectedIndex >= 0 ? Ai.ServerPresets[preset.SelectedIndex].Name.Split(" (")[0] : "Server");
            Ai.ServerOn = url.Text.Trim().Length > 0; on.Checked = Ai.ServerOn;
            if (!Ai.ServerOn) { st.Text = "Add the server address first"; return; }
            st.Text = "Testing…"; st.ForeColor = Th.Muted;
            try { var s = await Ai.Test(Ai.ById("server")); st.Text = $"✓ Works · {s:0.0} s"; st.ForeColor = Th.Good; }
            catch (Exception e) { st.Text = "⚠ " + e.Message; st.ForeColor = Th.Bad; }
        }, true));
        btns.Controls.Add(Th.B("Get a key", () => { var i = preset.SelectedIndex; var u = i >= 0 ? Ai.ServerPresets[i].KeyUrl : ""; Shell.Open(u.Length > 0 ? u : "https://console.groq.com/keys"); }));
        btns.Controls.Add(st);
    }

    // ------------------------------------------------------------------ About me
    void AboutMe()
    {
        Note("Your field and role. Every enhancement, search, brief and draft follows them — Nova has nothing personal built in (same profile as the phone).");
        page.Add(Th.Section("🌐 My field · Nova's words, codes and examples"));
        var fieldRow = page.Add(Th.Row());
        var fieldNow = Store.Str("win_field", "general");
        foreach (var pk in Packs.All) { var id = pk.Id; fieldRow.Controls.Add(Th.Radio($"{pk.Icon} {pk.Name}", fieldNow == id, () => fieldNow = id)); }
        var rr = page.Add(Th.Row(false));
        rr.Controls.Add(Th.L("My role", 10, Th.Text));
        var roleBox = Th.Box(false); roleBox.Width = 340; roleBox.Text = Persona.Role; roleBox.PlaceholderText = "e.g. " + Packs.Current.Person;
        rr.Controls.Add(roleBox);
        rr.Controls.Add(Th.L("Country", 10, Th.Text));
        var countryBox = Th.Box(false); countryBox.Width = 220; countryBox.Text = Persona.Country; countryBox.PlaceholderText = "optional";
        rr.Controls.Add(countryBox);
        page.Add(Th.Section("Also good at · pick any"));
        var chosen = Modes.ChosenFields().ToHashSet();
        var row = page.Add(Th.Row());
        foreach (var f in Modes.Fields.Concat(chosen.Where(c => !Modes.Fields.Contains(c))))
        {
            var ff = f;
            row.Controls.Add(Th.Check(f, chosen.Contains(f), on => { if (on) chosen.Add(ff); else chosen.Remove(ff); }));
        }
        var other = page.Add(Th.Row(false));
        var otherBox = Th.Box(false); otherBox.Width = 260; otherBox.PlaceholderText = "Other field";
        other.Controls.Add(otherBox);
        page.Add(Th.Section("About me"));
        var about = page.Add(Th.Box(true, 4), true);
        about.Text = Store.ProfileAbout;
        about.PlaceholderText = "e.g. " + (Packs.Current.ExAbout.Length > 0 ? Packs.Current.ExAbout : "what you do and what you write about") + "…";
        page.Add(Th.Section("My terms · kept exactly as written"));
        var terms = page.Add(Th.NoSpell(Th.Box(true, 2)), true);
        terms.Text = Store.ProfileTerms;
        terms.PlaceholderText = "e.g. " + (Packs.Current.ExTerms.Length > 0 ? Packs.Current.ExTerms : "words and short forms you use");
        var save = page.Add(Th.Row(false));
        var st = Th.L("", 9.5f, Th.Good);
        save.Controls.Add(Th.B("Save", () =>
        {
            if (otherBox.Text.Trim().Length > 0) chosen.Add(otherBox.Text.Trim().Replace("|", " "));
            Store.ProfileFields = string.Join("|", chosen);
            Store.Set("win_field", fieldNow); Persona.Role = roleBox.Text; Persona.Country = countryBox.Text;
            Store.ProfileAbout = about.Text.Trim();
            Store.ProfileTerms = terms.Text.Trim();
            Store.SetBool("p_asked", true);
            st.Text = "✓ Saved — enhancements will use your field's terms";
        }, true));
        save.Controls.Add(st);
    }

    // ------------------------------------------------------------------ Enhancement
    void Enhancement()
    {
        Note("Ctrl+Shift+E always starts with Fix grammar; the other modes are one click away in the window.");
        page.Add(Th.Section("Translate to"));
        var row = page.Add(Th.Row());
        foreach (var (v, l) in Modes.MainLangs.Concat(Modes.OtherLangs))
        {
            var vv = v;
            row.Controls.Add(Th.Radio(l, v.Equals(Store.TrLang, StringComparison.OrdinalIgnoreCase), () => Store.TrLang = vv));
        }
        var any = page.Add(Th.Row(false));
        var anyBox = Th.Box(false); anyBox.Width = 220; anyBox.PlaceholderText = "Any other language";
        any.Controls.Add(anyBox);
        any.Controls.Add(Th.B("Use it", () => { if (anyBox.Text.Trim().Length > 0) { Store.TrLang = anyBox.Text.Trim(); Draw("Enhancement"); } }));
        page.Add(Th.Section("Custom instruction"));
        var custom = page.Add(Th.Box(true, 2), true);
        custom.Text = Store.Custom;
        custom.PlaceholderText = "e.g. Make it friendlier and add a short greeting";
        page.Add(Th.Row(false)).Controls.Add(Th.B("Save", () => Store.Custom = custom.Text.Trim(), true));
        page.Add(Th.Section("Files"));
        Note("History: Documents\\Nova Enhance\\History · Originals: Documents\\Nova Enhance\\Originals");
        var r2 = page.Add(Th.Row(false));
        r2.Controls.Add(Th.B("📁 History", () => Shell.Open(Paths.History)));
        r2.Controls.Add(Th.B("📁 Originals", () => Shell.Open(Paths.Originals)));
    }

    // ------------------------------------------------------------------ Email & AI
    void EmailAi()
    {
        page.Add(Th.Section("Email source"));
        Opt("Outlook on this PC  (default)", "Uses the classic Outlook installed and signed in here — no extra login. Keep Outlook open; the index updates every 10 minutes.", Store.EmailSource == "pc", () => { Store.EmailSource = "pc"; MailIndex.Now(); });
        Opt("Microsoft 365 (company server)", "All mail, any age — your IT admin approves the app once. Coming in build 2.", Store.EmailSource == "m365", () => Store.EmailSource = "m365");
        Opt("Both", "Mail on this PC now; older mail from the Microsoft 365 server once IT approves (build 2).", Store.EmailSource == "both", () => { Store.EmailSource = "both"; MailIndex.Now(); });
        Note((Outlook.Installed ? "✓ Outlook is installed" : "⚠ Outlook (classic) isn't installed on this PC") + " · Microsoft 365: not connected yet (build 2)", Outlook.Installed ? Th.Good : Th.Warn);

        page.Add(Th.Section("What the AI may see"));
        Opt("A · Only my question  (safest)", "The search runs on this PC; email content never leaves it.", Store.AiSees == "A", () => Store.AiSees = "A");
        Opt("B · Question + subject and first lines of found emails", "About 300 characters per email, for one-line summaries and better \"needs reply\" sorting.", Store.AiSees == "B", () => Store.AiSees = "B");
        Opt("C · Question + the full email when I tap it  (default)", "Only the emails you open with Summary or ✦ Draft reply are sent.", Store.AiSees == "C", () => Store.AiSees = "C");
        Opt("D · B and C together", "", Store.AiSees == "D", () => Store.AiSees = "D");
        Note("Check your company's IT / data policy before letting an outside AI read work email.", Th.Warn);

        page.Add(Th.Section("Search range"));
        var rr = page.Add(Th.Row());
        foreach (var (v, l) in new[] { ("3m", "3 months"), ("12m", "12 months"), ("pc", "All on this PC  (default)"), ("all365", "All + Microsoft 365") })
        {
            var vv = v;
            rr.Controls.Add(Th.Radio(l, Store.Range == v, () => { Store.Range = vv; MailIndex.Now(); }));
        }

        page.Add(Th.Section("✦ AI search"));
        page.Add(Th.Check("✦ AI search in every search (email, project files, conversations)", SmartSearch.On, v => SmartSearch.On = v));
        page.Add(Th.Check("✦ AI search may see the subject + first lines of the candidate emails, to pick the best (your cloud AI, even when “What the AI may see” is C)", SmartSearch.RankSees, v => SmartSearch.RankSees = v));
        Note("Search planning and ranking use your cloud AI (" + Ai.LabelOf(SmartSearch.Cloud()) + ") — a local model is too slow for it; your local AI stays for files and conversations. Words in \"quotes\", subject: and -words are hard rules (the ? button next to each search box shows all the Windows search tricks).", Th.Muted);
        Note("Nova understands the question, splits it into ideas, adds their short forms (FHC = fire hose cabinet, BOH = back of house, SS = stainless steel, SSM / SDS / WIR…) and your 🧠 Memory words, " +
             "searches in several passes (every idea → most ideas → any), scores the results (subject and attachment names count more, newer counts more) and lets the AI keep the best few with a reason each. " +
             "Off = plain word search (every word must be there).");
        page.Add(Th.Section("🧠 Meaning search (optional)"));
        Note("Finds emails and document parts that say the same thing in other words (e.g. \"hose reel cupboard near the kitchen\" finds \"FHC in BOH\"). Each email (subject + first lines) and the first parts of each read file get a \"meaning fingerprint\", built gently in the background and kept encrypted on this PC.");
        foreach (var (v, l, d) in new[]
                 {
                     ("off", "Off  (default)", "Word search + ✦ AI search only."),
                     ("local", "💻 My local AI  (nothing leaves this PC)", $"Needs an embedding model in LM Studio / Ollama at {Ai.LocalUrl} (e.g. nomic-embed-text)."),
                     ("gemini", "☁ Gemini", "Sends the subject and first lines of each email to Google once (free tier is enough for most mailboxes)."),
                     ("openai", "☁ OpenAI", "Sends the subject and first lines of each email to OpenAI once (about $0.02 per 10,000 emails).")
                 })
        {
            var vv = v;
            Opt(l, d, Semantic.By == v, () => { Semantic.By = vv; Semantic.Start(); BeginInvoke(() => Draw("Email & AI")); });
        }
        if (Semantic.By == "local")
        {
            var mr = page.Add(Th.Row(false));
            mr.Controls.Add(Th.L("Local embedding model:", 10, Th.Text));
            var mb = Th.Box(false); mb.Width = 220; mb.Text = Semantic.LocalModel;
            mb.Leave += (_, _) => Semantic.LocalModel = mb.Text.Trim();
            mr.Controls.Add(mb);
        }
        if (Semantic.On)
        {
            var sl = Note(Semantic.Line(), Th.Muted);
            var st = new System.Windows.Forms.Timer { Interval = 3000 };
            st.Tick += (_, _) => { if (sl.IsDisposed) { st.Stop(); st.Dispose(); return; } sl.Text = Semantic.Line(); };
            st.Start();
        }
        Note("Check your company's IT / data policy before sending work email to a cloud AI. Folders marked 🔒 never read are never sent.", Th.Warn);

        page.Add(Th.Section("\"Needs a response\" list (morning brief)"));
        page.Add(Th.Check("I'm in To", Store.NeedTo, v => Store.NeedTo = v));
        page.Add(Th.Check("I'm in Cc and my name is mentioned", Store.NeedCcNamed, v => Store.NeedCcNamed = v));
        page.Add(Th.Check("AI checks for a question / request (needs B or D)", Store.NeedAi, v => Store.NeedAi = v));
        var fu = page.Add(Th.Row(false));
        fu.Controls.Add(Th.Check("My sent emails with no answer after", Store.FollowUps, v => Store.FollowUps = v));
        var days = new NumericUpDown { Minimum = 1, Maximum = 30, Value = Store.FollowDays, Width = 56, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        days.ValueChanged += (_, _) => Store.FollowDays = (int)days.Value;
        fu.Controls.Add(days);
        fu.Controls.Add(Th.L("days", 10, Th.Text));
        var lb = page.Add(Th.Row(false));
        lb.Controls.Add(Th.L("Look back", 10, Th.Text));
        var back = new NumericUpDown { Minimum = 1, Maximum = 90, Value = Store.BriefDays, Width = 56, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        back.ValueChanged += (_, _) => Store.BriefDays = (int)back.Value;
        lb.Controls.Add(back);
        lb.Controls.Add(Th.L("days", 10, Th.Text));
        var br = page.Add(Th.Row(false));
        br.Controls.Add(Th.Check("Show the morning brief once a day from", Store.BriefOn, v => Store.BriefOn = v));
        var hour = new NumericUpDown { Minimum = 0, Maximum = 23, Value = Store.BriefHour, Width = 56, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        hour.ValueChanged += (_, _) => Store.BriefHour = (int)hour.Value;
        br.Controls.Add(hour);
        br.Controls.Add(Th.L(":00", 10, Th.Text));
        var bri = page.Add(Th.Row(false));
        bri.Controls.Add(Th.B("☀ Open the brief now", Screens.Brief));
        bri.Controls.Add(Th.B("✦ My brief instructions…", () => BriefRules.Edit(this, () => Draw("Email & AI")), true));
        bri.Controls.Add(Th.B("📏 Rules…", () => RulesForm.Open(this, "brief", () => Draw("Email & AI"))));
        if (BriefRules.Any) Note("📏 Your brief rules are in use:\n" + RuleBooks.Describe("brief", BriefRules.Current), Th.Muted);

        page.Add(Th.Section("Calendar"));
        Note("Read from Outlook on this PC only — nothing from your calendar goes to the AI.");
        page.Add(Th.Check("Today and tomorrow in the morning brief: meetings, clashes, invitations waiting, your reminders, what is still open for each meeting", Calendar.InBrief, v => Calendar.InBrief = v));
        var pp = page.Add(Th.Row(false));
        pp.Controls.Add(Th.Check("Pop-up before each meeting:", Calendar.Popup, v => Calendar.Popup = v));
        var lead = new NumericUpDown { Minimum = 5, Maximum = 60, Increment = 5, Value = Calendar.Lead, Width = 56, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        lead.ValueChanged += (_, _) => Calendar.Lead = (int)lead.Value;
        pp.Controls.Add(lead);
        pp.Controls.Add(Th.L("minutes before", 10, Th.Text));

        page.Add(Th.Section("📌 My Work"));
        Note("Saved searches and answers become live tasks (Documents\\Nova Enhance\\My Work), included in your backup.");
        page.Add(Th.Check("Also create an Outlook task for each (shows in Outlook To-Do and Microsoft To Do on your phone; done there = done in Nova)", Work.OutlookSync, v => Work.OutlookSync = v));
        page.Add(Th.Check("Write the 📊 weekly report every Thursday from 17:00", Work.WeeklyAuto, v => Work.WeeklyAuto = v));
        page.Add(Th.Row(false)).Controls.Add(Th.B("📌 Open My Work", () => Screens.Work(null)));

    }

    // ------------------------------------------------------------------ 📥 Email index · 🗂 Mailboxes & archives · 📂 Folders (own pages: Settings always opens at once)

    void EmailIndexPage()
    {
        page.Add(Th.Section("Email index"));
        var line = page.Add(new IndexLine(true), true);
        line.Height = 32;
        liveIndex = Note(MailIndex.Status, Th.Muted);
        page.Add(Th.Check("Index attachments (PDF, Word, text) so Ask Nova finds words inside them", Store.IndexAttachments, v => Store.IndexAttachments = v));
        page.Add(Th.Check("Keep the index on this PC only (not in Documents / OneDrive)", Store.IndexLocal, v => MailIndex.Move(v)));
        Note("The index holds copies of your email text, encrypted and locked to your Windows account.");
        var ir = page.Add(Th.Row(false));
        ir.Controls.Add(Th.B("⚡ Index now", () => IndexProgress.Ask(this), true));
        ir.Controls.Add(Th.B("↻ Update now", MailIndex.Now));
        ir.Controls.Add(Th.B("Rebuild index", () =>
        {
            if (MessageBox.Show("Delete the email index and build it again from Outlook?", "Nova Enhance", MessageBoxButtons.YesNo) == DialogResult.Yes) MailIndex.Rebuild();
        }));
        ir.Controls.Add(Th.B("📁 Index folder", () => Shell.Open(Store.IndexLocal ? Paths.MailIndexLocal : Paths.MailIndexDocs)));

        page.Add(Th.Section("Gentle indexing"));
        Note("Outlook stays smooth: in working hours only new mail (and the last 14 days) is indexed, gently; the big first index and \"Rebuild index\" run outside working hours — full speed when the PC is idle, slower while you work, slowest on battery. Nova pauses whenever Outlook is busy.");
        page.Add(Th.Check("Gentle indexing with working hours", Gentle.On, v => Gentle.On = v));
        var wh = page.Add(Th.Row(false));
        wh.Controls.Add(Th.L("Working hours from", 10, Th.Text));
        var ws = new NumericUpDown { Minimum = 0, Maximum = 23, Value = Gentle.StartHour, Width = 56, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        ws.ValueChanged += (_, _) => Gentle.StartHour = (int)ws.Value;
        wh.Controls.Add(ws);
        wh.Controls.Add(Th.L(":00  to", 10, Th.Text));
        var we = new NumericUpDown { Minimum = 1, Maximum = 24, Value = Gentle.EndHour, Width = 56, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        we.ValueChanged += (_, _) => Gentle.EndHour = (int)we.Value;
        wh.Controls.Add(we);
        wh.Controls.Add(Th.L(":00", 10, Th.Text));
        var wd = page.Add(Th.Row());
        var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        for (int d = 0; d < 7; d++)
        {
            var dd = d;
            wd.Controls.Add(Th.Check(names[d], Gentle.Days.Split(',').Contains(d.ToString()), v =>
            {
                var set = Gentle.Days.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                if (v) set.Add(dd.ToString()); else set.Remove(dd.ToString());
                Gentle.Days = string.Join(",", set.OrderBy(x => x));
            }));
        }

    }

    void MailboxesPage()
    {
        page.Add(Th.Section("Mailboxes and folders"));
        Note("Everything is indexed: your mailbox, shared mailboxes, Online Archive, Groups and .pst archives (public folders and Search Folders are skipped). Untick a folder to leave it out of the index and of the answers.");
        // How far back each mailbox / archive is searchable.
        // Counted in the background: the page opens at once.
        var statsBox = page.Add(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent }, true);
        statsBox.Controls.Add(Th.L("Counting the emails of each mailbox and archive… ● ● ●", 9.5f, Th.Muted));
        List<(string Store, int Indexed, int Total, DateTime? Oldest, DateTime? Newest)> stats = new();
        var statsAt = DateTime.MinValue;
        Task.Run(MailIndex.StoreStats).ContinueWith(t =>
        {
            try
            {
                if (statsBox.IsDisposed) return;
                BeginInvoke(() =>
                {
                    if (statsBox.IsDisposed) return;
                    stats = t.Result; statsAt = DateTime.Now;
                    statsBox.Controls.Clear();
                    StatRows2();
                    page?.FitAll();
                });
            }
            catch { }
        });
        void StatRows2()
        {
            var files = Archives.StoreFiles();
            foreach (var sst0 in stats.Where(x => x.Total > 0 || x.Indexed > 0))   // old names whose file is gone (0 of 0) are hidden
            {
                var stName = sst0.Store;
                var stFile = files.TryGetValue(stName, out var sf) ? sf : "";
                statsBox.Controls.Add(new ProgressRow(() =>
                {
                    if (DateTime.Now - statsAt > TimeSpan.FromSeconds(10)) { stats = MailIndex.StoreStats(); statsAt = DateTime.Now; }
                    var stX = stats.FirstOrDefault(y => y.Store == stName);
                    var stPc = stX.Total > 0 ? (int)Math.Min(100, 100L * stX.Indexed / stX.Total) : 100;
                    var stSpan = (stX.Oldest is { } o1 ? o1.ToString("dd MMM yyyy") : "—") + " → " + (stX.Newest is { } n1 ? n1.ToString("dd MMM yyyy") : "—");
                    return ("✉ " + stName, stPc, $"{stX.Indexed:N0} of {stX.Total:N0} emails · {stSpan}" + (stFile.Length > 0 && stFile.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) ? $" · via Outlook · {stFile}" : ""), !IndexProgress.Now.Complete && stPc < 100);
                }) { Width = Math.Max(500, statsBox.Parent?.ClientSize.Width - 70 ?? 900) });
            }
        }
        // 📂 Archive files not open in Outlook (option B: opened for indexing, closed again after).
        page.Add(Th.Section("📂 Archive files (.pst) not open in Outlook"));
        Note("Nova opens each archive you add, indexes it, then closes it again so your Outlook folder list stays short. Its emails stay searchable in Nova; “Open in Outlook” re-opens the archive when needed.");
        var arow = page.Add(Th.Row(false));
        var alist = page.Add(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent }, true);
        void ShowArchives(List<(string Path, long Size, bool Added, bool Indexed)> found)
        {
            alist.Controls.Clear();
            if (found.Count == 0) { alist.Controls.Add(Th.L("No other archive files found next to your Outlook files.", 9.5f, Th.Muted)); return; }
            foreach (var a0 in found)
            {
                var a1 = a0;
                var viaOutlook = Archives.StoreFiles().Values.Contains(a1.Path, StringComparer.OrdinalIgnoreCase);
                var arow2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.FromArgb(0x18, 0x1C, 0x24), Padding = new Padding(6, 3, 6, 3), Margin = new Padding(0, 2, 0, 2) };
                var spanLbl = Th.L("", 9.5f, Th.Muted);
                arow2.Controls.Add(Th.L($"{(viaOutlook ? "✉" : a1.Indexed ? "✓" : a1.Added ? "⏳" : "＋")} {a1.Path}   ·   {a1.Size / 1048576.0:N0} MB   ·   {(viaOutlook ? "open in Outlook — indexed via Outlook (see above)" : a1.Indexed ? "indexed" : a1.Added ? "waiting to be indexed" : "not in Nova yet")}", 9.5f, viaOutlook || a1.Indexed ? Th.Good : Th.Text));
                arow2.Controls.Add(spanLbl);
                // Which years this file holds (read directly, in the background).
                _ = Task.Run(() => { try { var oo = Pst.Open(a1.Path, false); return oo.Span; } catch { return ""; } }).ContinueWith(t => { try { if (!spanLbl.IsDisposed) spanLbl.BeginInvoke(() => spanLbl.Text = t.Result.Length > 0 ? "   ·   " + t.Result : ""); } catch { } });
                // ⚡ Read directly (no Outlook, much faster) — replaces the slow Outlook way for this file.
                if (!viaOutlook) arow2.Controls.Add(Th.B("⚡ Add directly", () => AddDirect(a1.Path)));
                arow2.Controls.Add(Th.B("📂 Open in reader", () => Screens.PstReader(a1.Path)));
                if (a1.Added) arow2.Controls.Add(Th.B("Remove", () => { Archives.Remove(a1.Path); ShowArchives(Archives.Find()); }));
                alist.Controls.Add(arow2);
            }
        }
        async void AddDirect(string path)
        {
            Archives.Remove(path);   // no more through Outlook
            try
            {
                var o = await Task.Run(() => Pst.Open(path, false));
                var already = await Task.Run(() => Pst.AlreadyInNova(o));
                if (already >= 80 && MessageBox.Show(this, $"About {already} % of the emails of {Path.GetFileName(path)} are already in Nova — it looks like a copy of an archive indexed before.\n\nAdd it anyway? (Emails already in Nova are skipped, only the missing ones are added.)", "⚡ Add directly", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                Pst.Add(o, null);
                Draw("🗂 Mailboxes & archives");
            }
            catch (Exception ex) { MessageBox.Show(this, "This file could not be read directly:\n" + ex.Message, "Nova Enhance"); }
        }
        // 📂 Archives read directly: one progress row each.
        foreach (var px in Pst.All.Values.Where(x => (x.Queued || x.Done || x.Indexed > 0) && !x.Status.Contains("via Outlook")))
        {
            var fpx = px.Fp;
            page.Add(new ProgressRow(() =>
            {
                var i = Pst.All.TryGetValue(fpx, out var v) ? v : px;
                var pc = i.Done ? 100 : i.Total > 0 ? (int)Math.Min(99, 100L * (i.Indexed + i.Skipped) / i.Total) : 0;
                var rate = Pst.Current == fpx && Pst.Rate > 0 ? $" · {Pst.Rate:N0} emails/min" : "";
                return ($"📂 {i.Name} (read directly)", pc, $"{i.Indexed:N0} added · {i.Skipped:N0} already in Nova · of {i.Total:N0}{rate} · {i.Status}", Pst.Current == fpx);
            }), true);
        }
        arow.Controls.Add(Th.B("🔍 Find archive files", FindArchivesLater, true));
        arow.Controls.Add(Th.B("📂 Archive reader…", () => Screens.PstReader(null)));
        arow.Controls.Add(Th.B("📂 Add an archive file…", () =>
        {
            using var pstDlg = new OpenFileDialog { Filter = "Outlook archive (*.pst)|*.pst", Multiselect = true, Title = "Add Outlook archive files" };
            if (pstDlg.ShowDialog(this) != DialogResult.OK) return;
            foreach (var fn in pstDlg.FileNames) AddDirect(fn);
            ShowArchives(Archives.Find());
        }));
        arow.Controls.Add(Th.B("➕ Add all found (directly)", () => { foreach (var x in Archives.Find().Where(fz => !fz.Indexed)) AddDirect(x.Path); ShowArchives(Archives.Find()); }));
        // Asked in the background with a time limit: a busy Outlook can't freeze Settings.
        async void FindArchivesLater()
        {
            alist.Controls.Clear();
            alist.Controls.Add(Th.L("Looking for archive files… ● ● ●", 9.5f, Th.Muted));
            try
            {
                var found = await Guard.Limit(Task.Run(Archives.Find), 15, "Outlook");
                if (!alist.IsDisposed) ShowArchives(found);
            }
            catch (Exception ex) { if (!alist.IsDisposed) { alist.Controls.Clear(); alist.Controls.Add(Th.L("⚠ " + ex.Message, 9.5f, Th.Warn)); } }
        }
        if (Archives.Extra.Count > 0 || Pst.All.Count > 0) FindArchivesLater();
        Note("A big archive takes several hours the first time (Outlook reads it email by email). Gentle indexing: older mail is read after working hours or when the PC is idle — ⚡ Index now reads at full speed.", Th.Muted);
    }

    void FoldersPage()
    {
        page.Add(Th.Section("Folders"));
        Note("Every Outlook folder in the index. Untick a folder to leave it out of the index and of the answers.");
        var box = page.Add(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent }, true);
        box.Controls.Add(Th.L("Reading the folder list… ● ● ●", 9.5f, Th.Muted));
        Task.Run(() => (MailIndex.Folders(), MailIndex.LeftOut())).ContinueWith(t =>
        {
            try { if (!box.IsDisposed) BeginInvoke(() => { if (box.IsDisposed) return; box.Controls.Clear(); FolderList(box, t.Result.Item1, t.Result.Item2); page?.FitAll(); }); } catch { }
        });
    }

    void FolderList(FlowLayoutPanel box, List<(string Fid, string StoreName, string Path, int Total, int Indexed)> folders, HashSet<string> left)
    {
        // One list with tick boxes (not hundreds of separate controls): the page scrolls smoothly.
        var fl = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Height = 360, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(9.5f), ShowGroups = true, Width = Math.Max(700, page.ClientSize.Width - 70) };
        box.Controls.Add(fl);
        fl.Columns.Add("Folder", 520); fl.Columns.Add("Indexed", 110, HorizontalAlignment.Right); fl.Columns.Add("In Outlook", 110, HorizontalAlignment.Right);
        var filling = true;
        foreach (var g in folders.Where(f => f.Total > 0 || f.Indexed > 0).GroupBy(f => f.StoreName))
        {
            var grp = new ListViewGroup(g.Key.Length > 0 ? g.Key : "Mailbox");
            fl.Groups.Add(grp);
            foreach (var f in g) fl.Items.Add(new ListViewItem(new[] { f.Path, f.Indexed.ToString("N0"), f.Total.ToString("N0") }, grp) { Checked = !left.Contains(f.Fid), Tag = f.Fid });
        }
        filling = false;
        fl.ItemChecked += (_, e) => { if (!filling && e.Item.Tag is string fid) MailIndex.SetLeftOut(fid, !e.Item.Checked); };
    }

    // Options on one page share one parent, and Windows would treat them all as ONE group (picking an email source
    // un-ticked "What the AI may see"). Each run of options under a heading is its own group instead.
    List<RadioButton> optGroup;
    Control optPage;
    int optEnd = -1;

    void Opt(string title, string sub, bool on, Action pick)
    {
        if (optPage != page || page.Controls.Count != optEnd) { optGroup = new List<RadioButton>(); optPage = page; }
        var group = optGroup;
        var r = page.Add(Th.Radio(title, on, null));
        r.AutoCheck = false;
        r.Margin = new Padding(0, 6, 0, 0);
        r.Click += (_, _) =>
        {
            if (r.Checked) return;
            foreach (var o in group) o.Checked = o == r;
            pick?.Invoke();
        };
        group.Add(r);
        if (sub.Length > 0)
        {
            var l = Note(sub);
            l.Margin = new Padding(20, 0, 0, 2);
        }
        optEnd = page.Controls.Count;
    }

    // ------------------------------------------------------------------ 📁 Projects
    static List<ProjectProposal> lastScan;
    static Projects.ScanProgress scanPg;
    Action ReadAllLive;

    /// <summary>A thin progress bar: the back panel and its fill.</summary>
    static (Panel Back, Panel Fill) Bar(Color c)
    {
        var back = new Panel { Height = 9, Width = 600, BackColor = Color.FromArgb(0x23, 0x28, 0x38), Margin = new Padding(0, 0, 0, 4) };
        var fill = new Panel { Height = 9, Width = 0, BackColor = c, Dock = DockStyle.Left };
        back.Controls.Add(fill);
        return (back, fill);
    }
    static CancellationTokenSource scanCts;
    Label projLive;

    void ProjectsPage()
    {
        ReadAllLive = null;
        Note("Give Nova your work folder. It reads only the folder and file NAMES (never the contents) and proposes your projects: a folder that starts with a number (1-, 2-, 3-…) is a project — the name after the number is the project, a bracket like (SAH) is its code. Folders that start with \"_\" are general reference used with every project. Private-looking folders (wallet, password, bank…) are 🔒 never read. Nothing is indexed until you tick ✓ Use and press Save.");
        var wr = page.Add(Th.Row(false));
        var wf = Th.Box(false);
        wf.Text = Projects.WorkFolder.Length > 0 ? Projects.WorkFolder : @"D:\Projects";
        if (wf is SpellBox sb) sb.Off = true;
        wf.Width = 300;
        wr.Controls.Add(wf);
        wr.Controls.Add(Th.B("📁 Choose…", () => { using var d = new FolderBrowserDialog { SelectedPath = Directory.Exists(wf.Text) ? wf.Text : "" }; if (d.ShowDialog(this) == DialogResult.OK) wf.Text = d.SelectedPath; }));
        var scanStatus = Th.L("", 9.5f, Th.Muted);
        void Run(bool network)
        {
            var root = wf.Text.Trim();
            if (!Directory.Exists(root)) { scanStatus.Text = "⚠ Folder not found: " + root; return; }
            Projects.WorkFolder = root;
            scanCts?.Cancel();
            var my = scanCts = new CancellationTokenSource();
            scanPg = new Projects.ScanProgress();
            var pg = scanPg;
            var t = new System.Windows.Forms.Timer { Interval = 600 };
            t.Tick += (_, _) => { if (!scanStatus.IsDisposed) scanStatus.Text = $"🔍 Scanning… {pg.Folders} folders · {pg.Files:N0} files · {pg.Current}"; };
            t.Start();
            Task.Run(() => Projects.Scan(root, network, pg, my.Token), my.Token).ContinueWith(r =>
            {
                Program.UiContext.Post(_ =>
                {
                    t.Stop(); t.Dispose();
                    if (r.IsCanceled || my.IsCancellationRequested) { if (!scanStatus.IsDisposed) scanStatus.Text = "Scan stopped."; return; }
                    if (r.IsFaulted) { if (!scanStatus.IsDisposed) scanStatus.Text = "⚠ " + r.Exception?.GetBaseException().Message; return; }
                    lastScan = r.Result;
                    if (!IsDisposed) Draw("📁 Projects");
                }, null);
            });
        }
        wr.Controls.Add(Th.B("🔍 Scan my work folder", () => Run(false), true));
        wr.Controls.Add(Th.B("🌐 + network shortcuts", () => Run(true)));
        wr.Controls.Add(Th.B("■ Stop", () => scanCts?.Cancel()));
        page.Add(scanStatus);

        // 📊 The overall line: all your project folders together.
        var ov = page.Add(new Panel { Height = 112, BackColor = Color.FromArgb(0x18, 0x1C, 0x24), Padding = new Padding(12, 8, 12, 8) }, true);
        var ovTitle = new Label { AutoSize = true, Font = Th.F(12, true), ForeColor = Th.Text, Location = new Point(12, 8) };
        var ovNames = new Label { AutoSize = true, Font = Th.F(10), ForeColor = Th.Text, Location = new Point(12, 36) };
        var ovRead = new Label { AutoSize = true, Font = Th.F(10), ForeColor = Th.Text, Location = new Point(12, 70) };
        var (nb, nbFill) = Bar(Color.FromArgb(0x3F, 0xAF, 0x84)); nb.Location = new Point(12, 58);
        var (rb, rbFill) = Bar(Th.Accent); rb.Location = new Point(12, 92);
        ov.Controls.AddRange(new Control[] { ovTitle, ovNames, nb, ovRead, rb });
        void Overall()
        {
            var o = Projects.Overall();
            var root = Projects.WorkFolder.Length > 0 ? Projects.WorkFolder : "your projects";
            ovTitle.Text = $"📁 {root} · {o.Names:N0} files in your projects";
            ovNames.Text = o.Listing ? "📛 File names  " + o.ListingText : $"📛 File names  ✓ 100%  ·  {o.Names:N0} listed  ·  kept up to date (new, renamed, deleted within seconds)";
            var ra = Projects.ReadAll.Counts; var rt = ra.Sum(x => x.Total); var rd = ra.Sum(x => x.Done);
            ovRead.Text = Projects.ReadAll.On && rt > 0
                ? $"📖 Read inside  {rd:N0} of {rt:N0} readable files · {100L * rd / rt}%  ·  {Projects.ReadAll.Phase}"
                : $"📖 Read inside  {o.Read:N0} files · {(o.Names > 0 ? 100L * o.Read / o.Names : 0)}%  ·  grows as you ask (or switch on 📖 Read everything below)";
            var w = Math.Max(200, ov.ClientSize.Width - 24);
            nb.Width = rb.Width = w;
            nbFill.Width = o.Listing ? w / 2 : w;
            rbFill.Width = (int)(w * (Projects.ReadAll.On && rt > 0 ? (double)rd / rt : o.Names > 0 ? (double)o.Read / o.Names : 0));
        }
        Overall();
        // ● ● ● and a clear % for the two jobs on your folders.
        page.Add(new ProgressRow(() =>
        {
            var o2 = Projects.Overall();
            var m = System.Text.RegularExpressions.Regex.Match(o2.ListingText ?? "", @"(\d+)\s*%");
            var p2 = o2.Listing ? (m.Success ? int.Parse(m.Groups[1].Value) : 50) : 100;
            return ("📛 File names of your projects", p2, o2.Listing ? o2.ListingText : $"{o2.Names:N0} files listed · kept up to date", o2.Listing);
        }), true);
        page.Add(new ProgressRow(() =>
        {
            var o3 = Projects.Overall();
            var ra3 = Projects.ReadAll.Counts; var t3 = ra3.Sum(x => x.Total); var d3 = ra3.Sum(x => x.Done);
            var on3 = Projects.ReadAll.On && t3 > 0;
            var p3 = on3 ? (int)(100L * d3 / t3) : o3.Names > 0 ? (int)(100L * o3.Read / o3.Names) : 0;
            var working = on3 && d3 < t3 && !Projects.ReadAll.Paused && !Projects.ReadAll.Phase.StartsWith("⏸") && !Projects.ReadAll.Phase.StartsWith("✓");
            return ("📖 Read inside the files", p3, on3 ? $"{d3:N0} of {t3:N0} readable files · {Projects.ReadAll.Phase}" : $"{o3.Read:N0} files read · grows as you ask (or switch on 📖 Read everything below)", working);
        }), true);

        if (lastScan != null)
        {
            page.Add(Th.Section($"What Nova found in {Projects.WorkFolder} · tick ✓ Use, correct the name / code / email codes (click a cell), then Save"));
            var g = new DataGridView
            {
                Height = 420, BackgroundColor = Th.Card, ForeColor = Th.Text, GridColor = Color.FromArgb(0x2E, 0x35, 0x46), BorderStyle = BorderStyle.FixedSingle,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, EnableHeadersVisualStyles = false, Font = Th.F(9),
                ScrollBars = ScrollBars.Both
            };
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(0x1D, 0x21, 0x30);
            g.ColumnHeadersDefaultCellStyle.ForeColor = Th.Text;
            g.DefaultCellStyle.BackColor = Th.Card; g.DefaultCellStyle.ForeColor = Th.Text;
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0x2E, 0x2A, 0x55);
            g.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "✓ Use", Width = 50 });
            foreach (var h in new[] { "Folder", "Type", "Project name ✎", "Code ✎", "Email codes ✎", "Emails", "Files", "PDF", "Word/Excel", "CAD", "Size", "Last change", "Codes in file names", "Main subfolders" })
                g.Columns.Add(h, h);
            foreach (DataGridViewColumn c in g.Columns) c.ReadOnly = !(c.Index == 0 || c.HeaderText.EndsWith("✎"));
            foreach (var p in lastScan)
            {
                var kind = p.Kind switch { "project" => "Project", "general" => "General reference", "network" => "🌐 Network (not scanned)", "locked" => "🔒 never read", _ => "Not a project" };
                var i = g.Rows.Add(p.Use, p.Display, kind, p.Name, p.Code, string.Join(", ", p.Codes),
                    p.Kind == "project" ? p.Emails.ToString("N0") : "", p.Files > 0 ? p.Files.ToString("N0") : "", p.Pdf > 0 ? p.Pdf.ToString("N0") : "", p.Office > 0 ? p.Office.ToString("N0") : "", p.Cad > 0 ? p.Cad.ToString("N0") : "",
                    p.Bytes > 0 ? Projects.Size(p.Bytes) : "", p.Last > DateTime.MinValue ? p.Last.ToString("dd MMM yyyy") : "", string.Join(", ", p.FileCodes), string.Join(", ", p.Subfolders));
                var row = g.Rows[i];
                row.Tag = p;
                if (p.Kind == "locked") { row.ReadOnly = true; row.DefaultCellStyle.ForeColor = Th.Muted; row.Cells[0].Value = false; }
                else if (p.Kind is "other" or "network") row.DefaultCellStyle.ForeColor = Th.Muted;
                if (p.Kind != "project") { row.Cells[3].ReadOnly = row.Cells[4].ReadOnly = row.Cells[5].ReadOnly = true; }
            }
            page.Add(g, true);
            var nP = lastScan.Count(p => p.Kind == "project"); var nG = lastScan.Count(p => p.Kind == "general");
            Note($"{nP} projects · {nG} general reference folders · {lastScan.Count(p => p.Kind == "other")} not projects · {lastScan.Count(p => p.Kind == "locked")} 🔒 never read · {lastScan.Count(p => p.Kind == "network")} network. Emails = emails whose subject has the project's code. Pre-ticked: projects with 20+ emails or 500+ files.");
            var br = page.Add(Th.Row(false));
            br.Controls.Add(Th.B("💾 Save the ticked lines", () =>
            {
                g.EndEdit();
                foreach (DataGridViewRow r in g.Rows)
                {
                    if (r.Tag is not ProjectProposal p || p.Kind == "locked") continue;
                    p.Use = r.Cells[0].Value is true;
                    if (p.Kind == "network" && p.Use) p.Kind = "general";   // a network folder you tick is used as general reference
                    if (p.Kind == "other" && p.Use) p.Kind = "project";
                    p.Name = (r.Cells[3].Value?.ToString() ?? "").Trim();
                    p.Code = (r.Cells[4].Value?.ToString() ?? "").Trim().ToUpperInvariant();
                    p.Codes = (r.Cells[5].Value?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (p.Kind == "project" && p.Use && p.Code.Length == 0) p.Code = p.Name.Length > 0 ? p.Name.ToUpperInvariant() : Path.GetFileName(p.Folder);
                    if (p.Kind == "project" && p.Name.Length == 0) p.Name = Path.GetFileName(p.Folder);
                }
                Projects.Accept(lastScan);
                lastScan = null;
                Draw("📁 Projects");
            }, true));
            br.Controls.Add(Th.B("✓ Tick all projects", () => { foreach (DataGridViewRow r in g.Rows) if (r.Tag is ProjectProposal { Kind: "project" }) r.Cells[0].Value = true; }));
            br.Controls.Add(Th.B("✗ Untick all", () => { foreach (DataGridViewRow r in g.Rows) r.Cells[0].Value = false; }));
            br.Controls.Add(Th.B("Close the list", () => { lastScan = null; Draw("📁 Projects"); }));
        }

        page.Add(Th.Section("My projects · every file name listed and kept up to date"));
        var list = Projects.All();
        var lb = page.Add(new ListBox { Height = 200, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, Font = Th.F(9.5f), IntegralHeight = false, HorizontalScrollbar = true }, true);
        string Line(Project p)
        {
            var st = p.Folders.Select(f => Projects.Status.TryGetValue(f, out var s) ? s : null).FirstOrDefault(s => s != null);
            var sts = p.Folders.Select(f => Projects.Status.TryGetValue(f, out var s) ? s : null).Where(s => s != null).ToList();
            var nf = sts.Sum(s => s.Files); var nr = sts.Sum(s => s.Read);
            var stt = st == null ? "waiting" : sts.Any(s => s.Phase == "listing") ? $"listing names… {nf:N0}" : sts.All(s => s.Phase == "listed") ? $"✓ {nf:N0} names · 📖 {nr:N0} read ({(nf > 0 ? 100L * nr / nf : 0)}%) · {st.Last:dd MMM HH:mm}" : st.Phase;
            return $"{p.Code}  ·  {p.Name}  ·  email codes: {string.Join(", ", p.Codes)}  ·  {string.Join(" + ", p.Folders)}  ·  {stt}";
        }
        foreach (var p in list) lb.Items.Add(Line(p));
        if (list.Count == 0) lb.Items.Add("(none yet — scan your work folder above)");
        projLive = page.Add(Th.L(Projects.Indexer.Line(), 9.5f, Th.Muted));
        var lt = new System.Windows.Forms.Timer { Interval = 2500 };
        lt.Tick += (_, _) =>
        {
            if (lb.IsDisposed) { lt.Stop(); lt.Dispose(); return; }
            var all = Projects.All();
            for (int i = 0; i < all.Count && i < lb.Items.Count; i++) { var s = Line(all[i]); if ((string)lb.Items[i] != s) lb.Items[i] = s; }
            projLive.Text = Projects.Indexer.Line();
            Overall();
            ReadAllLive?.Invoke();
        };
        lt.Start();
        Project Sel() => lb.SelectedIndex >= 0 && lb.SelectedIndex < list.Count ? list[lb.SelectedIndex] : null;
        var pr = page.Add(Th.Row());
        pr.Controls.Add(Th.B("📂 Find a file…", () => Screens.FindFile(null), true));
        pr.Controls.Add(Th.B("⟳ List again now", () => Projects.Indexer.Refresh()));
        pr.Controls.Add(Th.B("✎ Edit", () =>
        {
            var p = Sel(); if (p == null) return;
            var name = Prompt.Ask(this, "✎ Project", "Project name", p.Name); if (name == null) return;
            var code = Prompt.Ask(this, "✎ Project", "Project code", p.Code); if (code == null) return;
            var codes = Prompt.Ask(this, "✎ Project", "Codes in email subjects and file names (comma-separated)", string.Join(", ", p.Codes)); if (codes == null) return;
            p.Name = name.Trim(); p.Code = code.Trim().ToUpperInvariant();
            p.Codes = codes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Projects.SaveAll(list); Draw("📁 Projects");
        }));
        pr.Controls.Add(Th.B("➕ Add a folder", () =>
        {
            var p = Sel(); if (p == null) { MessageBox.Show(this, "Select a project first.", "Nova Enhance"); return; }
            using var d = new FolderBrowserDialog { Description = "Another folder for " + p.Code };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            if (Projects.Private.IsMatch(d.SelectedPath)) { MessageBox.Show(this, "🔒 That folder looks private — Nova never reads it.", "Nova Enhance"); return; }
            p.Folders.Add(d.SelectedPath); Projects.SaveAll(list); Draw("📁 Projects");
        }));
        pr.Controls.Add(Th.B("➕ New project…", () =>
        {
            using var d = new FolderBrowserDialog { Description = "The project's folder" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var code = Prompt.Ask(this, "➕ New project", "Project code (as in email subjects)", ""); if (string.IsNullOrWhiteSpace(code)) return;
            list.Add(new Project { Code = code.Trim().ToUpperInvariant(), Name = Path.GetFileName(d.SelectedPath), Codes = new() { code.Trim().ToUpperInvariant() }, Folders = new() { d.SelectedPath } });
            Projects.SaveAll(list); Draw("📁 Projects");
        }));
        pr.Controls.Add(Th.B("🗑 Remove", () =>
        {
            var p = Sel(); if (p == null) return;
            if (MessageBox.Show(this, $"Remove {p.Code} from your projects? (Your files are not touched.)", "Nova Enhance", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            list.Remove(p); Projects.SaveAll(list); Draw("📁 Projects");
        }));

        page.Add(Th.Section("General reference (used with every project)"));
        var gen = Projects.General;
        Note(gen.Count == 0 ? "(none)" : string.Join("\n", gen.Select(x => "• " + x)));
        var gr = page.Add(Th.Row(false));
        gr.Controls.Add(Th.B("➕ Add", () => { using var d = new FolderBrowserDialog(); if (d.ShowDialog(this) == DialogResult.OK && !Projects.Private.IsMatch(d.SelectedPath)) { var l = Projects.General; l.Add(d.SelectedPath); Projects.General = l; Projects.Indexer.Wake(); Draw("📁 Projects"); } }));
        gr.Controls.Add(Th.B("Clear", () => { Projects.General = new(); Draw("📁 Projects"); }));

        // 📖 B · Read everything in the background (optional).
        page.Add(Th.Section("📖 Read everything in the background (optional)"));
        page.Add(Th.Check("📖 Read inside every PDF, Word, Excel and PowerPoint file of my projects (CAD / BIM and pictures skipped) — only after working hours or when the PC is idle",
            Projects.ReadAll.On, v => { Projects.ReadAll.On = v; Draw("📁 Projects"); }));
        if (Projects.ReadAll.On)
        {
            var rows = new List<(string Code, Label L, Panel Fill, Panel Back)>();
            var ra = page.Add(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.FromArgb(0x18, 0x1C, 0x24), Padding = new Padding(12, 8, 12, 8) }, true);
            foreach (var code in Projects.ReadAll.Counts.Select(x => x.Code).Append("All projects"))
            {
                var l = new Label { AutoSize = true, Font = Th.F(9.5f, code == "All projects"), ForeColor = Th.Text, Margin = new Padding(0, 4, 0, 2) };
                var (b, f) = Bar(Th.Accent);
                ra.Controls.Add(l); ra.Controls.Add(b);
                rows.Add((code, l, f, b));
            }
            var phase = new Label { AutoSize = true, Font = Th.F(9.5f), ForeColor = Th.Muted, Margin = new Padding(0, 6, 0, 0) };
            ra.Controls.Add(phase);
            ReadAllLive = () =>
            {
                if (ra.IsDisposed) return;
                var cs = Projects.ReadAll.Counts;
                var w = Math.Max(200, ra.Parent?.ClientSize.Width - 80 ?? 600);
                foreach (var (code, l, f, b) in rows)
                {
                    int t, d;
                    if (code == "All projects") { t = cs.Sum(x => x.Total); d = cs.Sum(x => x.Done); }
                    else { var c = cs.FirstOrDefault(x => x.Code == code); t = c?.Total ?? 0; d = c?.Done ?? 0; }
                    l.Text = $"{code}   📖 {d:N0} of {t:N0} readable files · {(t > 0 ? 100L * d / t : 0)}%";
                    b.Width = w; f.Width = t > 0 ? (int)(w * (double)d / t) : 0;
                }
                var left = Projects.ReadAll.Left;
                phase.Text = Projects.ReadAll.Phase + (Projects.ReadAll.Current.Length > 0 ? " · " + Projects.ReadAll.Current : "") +
                             (left is { } lf && lf > TimeSpan.Zero ? $" · about {(lf.TotalHours >= 1 ? $"{lf.TotalHours:0} h" : $"{Math.Max(1, (int)lf.TotalMinutes)} min")} of reading time left" : "");
            };
            ReadAllLive();
            var rr = page.Add(Th.Row(false));
            rr.Controls.Add(Th.B("⚡ Read now at full speed", () => { Projects.ReadAll.Now(); ReadAllLive?.Invoke(); }, true));
            rr.Controls.Add(Th.B(Projects.ReadAll.Paused ? "▶ Resume" : "⏸ Pause", () => { Projects.ReadAll.Paused = !Projects.ReadAll.Paused; Draw("📁 Projects"); }));
            rr.Controls.Add(Th.B("Choose projects…", () =>
            {
                using var f = new NovaForm { Text = "📖 Projects to read in the background", Width = 420, Height = 420, StartPosition = FormStartPosition.CenterParent };
                var cl = new CheckedListBox { Dock = DockStyle.Fill, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(10.5f), CheckOnClick = true };
                var only = Projects.ReadAll.Only;
                foreach (var p in Projects.All()) cl.Items.Add($"{p.Code} · {p.Name}", only.Count == 0 || only.Contains(p.Code, StringComparer.OrdinalIgnoreCase));
                var ok = Th.B("✓ Save", () => f.DialogResult = DialogResult.OK, true); ok.Dock = DockStyle.Bottom;
                f.Controls.Add(cl); f.Controls.Add(ok);
                if (f.ShowDialog(this) != DialogResult.OK) return;
                var all = Projects.All();
                var chosen = cl.CheckedIndices.Cast<int>().Select(i => all[i].Code).ToList();
                Projects.ReadAll.Only = chosen.Count == all.Count ? new() : chosen;
                Draw("📁 Projects");
            }));
            Note("Reading takes many hours for big projects (OCR on scanned drawings). Newest files are read first, so what you work on now is ready soonest. Nova pauses while you work and continues after working hours or when the PC is idle (Settings → Email & AI → Gentle indexing); ⚡ reads at full speed until complete.");
        }

        Note("📁 Email projects (sorting your emails per project) has its own page now: Email & AI → 📁 Email projects.", Th.Muted);

        page.Add(Th.Section("How Nova knows your files"));
        page.Add(Th.Check("Keep the list of file names up to date (new, renamed and deleted files within seconds; a full check every day)", Projects.Indexer.On, v => Projects.Indexer.On = v));
        Note("1 · Names only: Nova lists every file name, folder, date and revision of your projects — a few minutes, then instant.\n2 · Inside only when needed: when you ask something, Nova picks the likely files by their names and opens just those (OCR included for scanned drawings).\n3 · Remembered for ALL of Nova: a file read once (by Ask about this email, Ask AI, Search inside a folder, Nova Chat…) is known by every feature; if the file changes, it is read again when needed.\n4 · One Nova search: every question also looks at your emails, 🧠 Nova Memory and past answers in 🕘 Chat history, and your projects are the one project list everywhere (Ask Nova groups, Brief, prompts).\n📂 Find a project file (tray menu, or here): type a drawing number or a few words — results as you type, latest revision first, Enter opens, right-click to attach it to an email.");
        Note("📐 Drawing numbers: when an email (or the text you highlight in Outlook) mentions a drawing or document number, 💬 Talk about this email finds the file by its name in the project — the latest PDF first, then Word / Excel, CAD last (never used for the answer) — and marks older revisions as superseded.");
    }

    // ------------------------------------------------------------------ ✉ Emails per project

    void EmailProjectsSection()
    {
        page.Add(Th.Section("✉ Emails per project"));
        Note("Nova sorts every email of the index into one of your projects, on this PC. The 📁 project button in 🔍 Search for email (and the conversations) uses it. Your corrections always win.");
        page.Add(new ProgressRow(() =>
        {
            int tt = MailProjects.Total, dd = MailProjects.Done;
            var pp = tt > 0 ? (int)(100L * dd / tt) : 0;
            return ("📁 Sorting emails into projects", pp, tt == 0 ? "waiting for the email index…" : $"{dd:N0} of {tt:N0} emails checked · {MailProjects.Sorted:N0} in a project · {MailProjects.Phase}", dd < tt || MailProjects.Phase.StartsWith("✦"));
        }), true);
        var prog = page.Add(Th.L("", 10.5f, Th.Text, true));
        var (pb, pf) = Bar(Th.Accent);
        page.Add(pb);
        prog.Visible = pb.Visible = false;
        var counts = Note("", Th.Muted);
        var phase = Note("", Th.Muted);
        void Live()
        {
            if (prog.IsDisposed) return;
            int t = MailProjects.Total, d = MailProjects.Done, so = MailProjects.Sorted;
            prog.Text = t == 0 ? "Waiting for the email index…" : $"{d:N0} of {t:N0} emails checked · {(t > 0 ? 100L * d / t : 0)}%  ·  {so:N0} sorted into a project";
            var w = Math.Max(200, page.ClientSize.Width - 80);
            pb.Width = w; pf.Width = t > 0 ? (int)(w * (double)d / t) : 0;
            var cs = MailProjects.Counts;
            counts.Text = string.Join(" · ", MailProjects.Choices().Select(c => $"{c.Code} {(cs.TryGetValue(c.Code, out var n) ? n : 0):N0}")) + $" · ❔ Unsorted {(cs.TryGetValue("", out var u) ? u : 0):N0}";
            phase.Text = MailProjects.Phase;
        }
        Live();
        var lt = new System.Windows.Forms.Timer { Interval = 3000 };
        lt.Tick += (_, _) => { if (prog.IsDisposed) { lt.Stop(); lt.Dispose(); return; } Live(); };
        lt.Start();
        var br = page.Add(Th.Row(false));
        br.Controls.Add(Th.B("❔ Review unsorted", () => { Store.Set("win_ask_proj", "?"); Screens.AskFor("latest emails"); }));
        br.Controls.Add(Th.B("↻ Sort everything again", () =>
        {
            if (MessageBox.Show(this, "Sort all emails again with the current codes, names, folders and rules? (Your own corrections are kept.)", "Nova Enhance", MessageBoxButtons.YesNo) == DialogResult.Yes) MailProjects.SortAgain();
        }));

        page.Add(Th.Section("📏 Your own project rules (they come before Nova's)"));
        var ur = RuleBooks.Load("projects");
        Note(ur.Rules.Count == 0 ? "None yet — e.g. “emails from @omniumint.com about Tower C → RS5”. Write them in your words or add them one by one." : RuleBooks.Describe("projects", ur), Th.Muted);
        page.Add(Th.Row(false)).Controls.Add(Th.B("📏 Open the rules window", () => RulesForm.Open(this, "projects", () => Draw("📁 Email projects")), true));
        page.Add(Th.Section("Sorting rules · first strong match wins (▲ ▼ change the order, untick to switch a rule off)"));
        var order = MailProjects.Order;
        for (int k = 0; k < order.Count; k++)
        {
            var r = order[k]; var idx = k;
            var row = page.Add(new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.FromArgb(0x18, 0x1C, 0x24), Padding = new Padding(6, 4, 6, 4), Margin = new Padding(0, 2, 0, 2) }, true);
            var up = Th.B("▲", () => { var o = MailProjects.Order; if (idx > 0) { (o[idx - 1], o[idx]) = (o[idx], o[idx - 1]); MailProjects.Order = o; Draw("📁 Email projects"); } });
            var dn = Th.B("▼", () => { var o = MailProjects.Order; if (idx < o.Count - 1) { (o[idx + 1], o[idx]) = (o[idx], o[idx + 1]); MailProjects.Order = o; Draw("📁 Email projects"); } });
            up.Font = dn.Font = Th.F(8); up.Padding = dn.Padding = new Padding(2, 0, 2, 0);
            row.Controls.Add(up); row.Controls.Add(dn);
            var ck = Th.Check($"{k + 1} · {MailProjects.RuleNames[r]}", MailProjects.RuleOn(r), v => MailProjects.SetRule(r, v));
            ck.Font = Th.F(10, true); ck.Width = 300; ck.AutoSize = false;
            row.Controls.Add(ck);
            var help = Th.L(MailProjects.RuleHelp[r], 9, Th.Muted); help.Margin = new Padding(0, 6, 10, 0);
            row.Controls.Add(help);
            var st = Th.L(MailProjects.RuleStrength[r], 8.5f, r <= 2 || r == 4 ? Th.Good : r <= 5 ? Th.Warn : Th.Muted, true); st.Margin = new Padding(0, 6, 10, 0);
            row.Controls.Add(st);
            row.Controls.Add(Th.L($"{MailProjects.RuleCounts[r]:N0} emails", 9, Th.Muted));
        }
        Note($"✋ Your corrections always win: right-click an email → 📁 Move to project… / ✋ Always: this sender → project ({MailProjects.RuleCounts[8] + MailProjects.RuleCounts[9]:N0} emails). Two projects tie → ❔ Unsorted (no guessing). After changing rules: ↻ Sort everything again.");

        page.Add(Th.Section("Projects · what Nova looks for in emails"));
        var learned = MailProjects.Learned;
        foreach (var (code, label) in MailProjects.Choices())
        {
            var cc = code;
            var p = Projects.All().FirstOrDefault(x => x.Code == code);
            var cfg = MailProjects.CfgOf(code);
            var box = page.Add(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.FromArgb(0x18, 0x1C, 0x24), Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 3, 0, 3) }, true);
            box.Controls.Add(Th.L($"📁 {label}   ·   {MailProjects.CountOf(code):N0} emails", 10.5f, Th.Text, true));
            var codes = p == null ? new List<string>() : p.Codes.Append(p.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var lines = new[]
            {
                ("🏷 Codes in subjects / numbers", codes.Count > 0 ? string.Join(", ", codes) : "—"),
                ("📛 Names & email codes", string.Join(", ", (p != null && p.Name.Length > 3 ? new[] { p.Name } : Array.Empty<string>()).Concat(cfg.Names)) is { Length: > 0 } nm ? nm : "—"),
                ("🗂 Outlook folders", cfg.Folders.Count > 0 ? string.Join(", ", cfg.Folders) : "— (none linked)"),
                ("👤 Senders", string.Join(", ", cfg.Senders.Select(x => "✋ " + x).Concat(learned.Where(x => x.Value == code).Select(x => x.Key).Take(8))) is { Length: > 0 } sn ? sn : "(learning…)")
            };
            foreach (var (k, v) in lines) { var l = Th.L($"{k}:  {v}", 9.5f, Th.Muted); l.MaximumSize = new Size(Math.Max(400, page.ClientSize.Width - 100), 0); box.Controls.Add(l); }
            var eb = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Color.Transparent };
            if (p != null)
                eb.Controls.Add(Th.B("✎ Codes", () =>
                {
                    var v = Prompt.Ask(this, "🏷 Codes · " + cc, "Codes written in subjects and document numbers (comma separated), e.g. PRJ, PRJ-A", string.Join(", ", p.Codes));
                    if (v == null) return;
                    var all = Projects.All(); var pp = all.First(x => x.Code == cc);
                    pp.Codes = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    Projects.SaveAll(all); Draw("📁 Email projects");
                }));
            eb.Controls.Add(Th.B("✎ Names", () =>
            {
                var v = Prompt.Ask(this, "📛 Names · " + cc, "Other names people write for this project (comma separated), e.g. My Project, My-Project", string.Join(", ", cfg.Names));
                if (v == null) return;
                cfg.Names = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); MailProjects.SaveCfg(cc, cfg); Draw("📁 Email projects");
            }));
            eb.Controls.Add(Th.B("🗂 Outlook folders", () =>
            {
                var v = Prompt.Ask(this, "🗂 Outlook folders · " + cc, "Outlook folder names (comma separated) — emails in these folders and their sub-folders belong to this project, e.g. PRJ, Projects › My project", string.Join(", ", cfg.Folders));
                if (v == null) return;
                cfg.Folders = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Replace("›", "").Trim()).ToList(); MailProjects.SaveCfg(cc, cfg); Draw("📁 Email projects");
            }));
            eb.Controls.Add(Th.B("👤 Senders", () =>
            {
                var v = Prompt.Ask(this, "👤 Senders · " + cc, "Email addresses or @company.com (comma separated) whose emails always belong to this project", string.Join(", ", cfg.Senders));
                if (v == null) return;
                var list = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToList();
                foreach (var s0 in list.Except(cfg.Senders)) MailProjects.AlwaysSender(s0, cc);
                var c2 = MailProjects.CfgOf(cc); c2.Senders = list; MailProjects.SaveCfg(cc, c2); Draw("📁 Email projects");
            }));
            box.Controls.Add(eb);
        }
        page.Add(Th.Row(false)).Controls.Add(Th.B("✋ My corrections", () =>
        {
            var fx = MailProjects.Corrections();
            using var f = new NovaForm { Text = "✋ My corrections", Width = 720, Height = 460, StartPosition = FormStartPosition.CenterParent };
            var lb = new ListBox { Dock = DockStyle.Fill, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10), BorderStyle = BorderStyle.None, SelectionMode = SelectionMode.MultiExtended };
            foreach (var (_, pj, subj) in fx) lb.Items.Add($"{pj}  ·  {subj}");
            var undo = Th.B("↩ Undo selected", () => { foreach (int i in lb.SelectedIndices) MailProjects.Undo(fx[i].Entry); f.Close(); }, true);
            undo.Dock = DockStyle.Bottom;
            f.Controls.Add(lb); f.Controls.Add(undo);
            f.ShowDialog(this);
            Draw("📁 Email projects");
        }));

        page.Add(Th.Section("✦ AI for the unclear ones"));
        Opt("B · Ask the AI for emails the rules can't settle (subject + first lines only)", "Uses your Default AI, about 30 emails per request, gently in the background.", MailProjects.AiOn, () => MailProjects.AiOn = true);
        Opt("A · Only the rules on this PC", "Unclear emails stay in ❔ Unsorted until you move them.", !MailProjects.AiOn, () => MailProjects.AiOn = false);
        Note("🔒 Folders marked “never read” and emails that mention wallet / bank / password / passport / salary are never sent.", Th.Warn);
    }

    // ------------------------------------------------------------------ Folder search
    void FolderSearch()
    {
        Note("Right-click any folder in Windows Explorer → \"Search inside with Nova\" (on Windows 11: under \"Show more options\"). Everything is read on this PC.");
        page.Add(Th.Check("Show \"Search inside with Nova\" when I right-click a folder", FolderIndex.Menu, v => { FolderIndex.Menu = v; ExplorerMenu.Apply(); }));
        page.Add(Th.Row(false)).Controls.Add(Th.B("📁 Search a folder now…", () => Screens.FolderSearch(null), true));

        page.Add(Th.Section("Read inside these files"));
        foreach (var (k, label, _) in FileKinds.All)
        {
            var kk = k;
            page.Add(Th.Check($"{k} · {label}", FileKinds.On(k), v => FileKinds.Set(kk, v)));
        }
        page.Add(Th.Section("OCR · scanned PDFs and images"));
        Opt("PC only  (default)", "Windows text recognition on this PC: big drawings are read in high-resolution tiles, with a second pass turned 90° for vertical text. Nothing leaves the PC.", Ocr.Mode == "pc", () => Ocr.Mode = "pc");
        Opt("PC first, then Gemini vision for unclear pages", "Pages where the PC finds little text are offered to Gemini vision — Nova asks before sending the page images.", Ocr.Mode == "pc+gemini", () => Ocr.Mode = "pc+gemini");
        Opt("Gemini vision for every scanned page", "Best for drawings, tables, stamps, handwriting and Arabic. Nova asks before sending; slower on big folders.", Ocr.Mode == "gemini", () => Ocr.Mode = "gemini");
        var langs = Ocr.Languages;
        Note("OCR languages installed in Windows: " + langs + (Ocr.Has("ar") && Ocr.Has("fr") ? "" : " — to add " + string.Join(" and ", new[] { Ocr.Has("ar") ? null : "Arabic", Ocr.Has("fr") ? null : "French" }.Where(x => x != null)) + ": Windows Settings → Time & language → Language → Add a language (tick \"Optical character recognition\"), then restart Nova."), Ocr.Has("ar") && Ocr.Has("fr") ? Th.Good : Th.Warn);
        page.Add(Th.Row(false)).Controls.Add(Th.B("Open Windows language settings", () => Shell.Open("ms-settings:regionlanguage")));

        page.Add(Th.Section("✦ Ask AI about a folder · what the AI may see"));
        Opt("Ask me each time  (default)", "Before answering, Nova shows which extracts would be sent — Send, Show passages only, or Cancel.", FolderAsk.Sees == "ask", () => FolderAsk.Sees = "ask");
        Opt("Never send my files", "The AI only turns your question into search words; you get the best passages to read yourself.", FolderAsk.Sees == "never", () => FolderAsk.Sees = "never");
        Opt("Send the extracts without asking", "About 10–15 short passages per question, never whole files.", FolderAsk.Sees == "send", () => FolderAsk.Sees = "send");
        Note("Check your company's IT / data policy before sending project documents to an outside AI.", Th.Warn);
        Note("Old .doc, .ppt and .xls files are read through the Office installed on this PC" + (OfficeConvert.Missing("word") ? " (Word not found)" : "") + ".");
        var big = page.Add(Th.Row(false));
        big.Controls.Add(Th.L("Skip files larger than", 10, Th.Text));
        var mb = new NumericUpDown { Minimum = 5, Maximum = 2000, Value = FolderIndex.MaxMb, Width = 70, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        mb.ValueChanged += (_, _) => FolderIndex.MaxMb = (int)mb.Value;
        big.Controls.Add(mb);
        big.Controls.Add(Th.L("MB", 10, Th.Text));
        Note("A changed setting applies to files read from now on; \"Forget\" a folder to read it again completely.");

        page.Add(Th.Section("Remembered folders (instant search)"));
        var roots = FolderIndex.Roots();
        var list = page.Add(new ListBox { Height = 160, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, Font = Th.F(9.5f), IntegralHeight = false }, true);
        foreach (var (p, last, files) in roots) list.Items.Add($"{p}   ·  {files:N0} files · searched {last:dd MMM yyyy}");
        if (roots.Count == 0) list.Items.Add("(none yet)");
        Note($"Folder index: {FolderIndex.SizeMb():N0} MB · encrypted, locked to your Windows account · {FolderIndex.DirPath}");
        var fr = page.Add(Th.Row(false));
        fr.Controls.Add(Th.B("Forget selected", () =>
        {
            var i = list.SelectedIndex;
            if (i < 0 || i >= roots.Count) return;
            FolderIndex.Forget(roots[i].Path);
            Draw("Folder search");
        }));
        fr.Controls.Add(Th.B("Forget all", () =>
        {
            if (MessageBox.Show("Forget every remembered folder? (Your files are not touched.)", "Nova Enhance", MessageBoxButtons.YesNo) == DialogResult.Yes) { FolderIndex.ForgetAll(); Draw("Folder search"); }
        }));
        page.Add(Th.Check("Keep the folder index on this PC only (not in Documents / OneDrive) — takes effect for new searches", FolderIndex.Local, v => { FolderIndex.Local = v; }));
    }

    // ------------------------------------------------------------------ Google Drive
    void GoogleDrive()
    {
        Note("Two ways: (1) Google Drive for desktop — your Drive appears as G:\\ and Nova searches it like any folder; (2) the Google sign-in below — Nova reads Drive folders directly, including Google Docs, Sheets and Slides. Nova only reads your files; the one thing it writes is its own \"Nova Enhance Backups\" folder, when you tick that box in Backup & restore.");
        page.Add(Th.Section("Status"));
        Note(GDrive.SignedIn ? $"✓ Connected{(GDrive.Account.Length > 0 ? " as " + GDrive.Account : "")}{(GDrive.CanSave ? "" : " (read-only — sign in again to allow backups)")}" : GDrive.Configured ? "Not signed in yet" : "Not set up yet — follow the steps below once", GDrive.SignedIn ? Th.Good : Th.Warn);
        var row = page.Add(Th.Row(false));
        var st = Th.L("", 9.5f, Th.Muted);
        if (GDrive.SignedIn)
        {
            row.Controls.Add(Th.B("☁ Search a Drive folder…", () => { Screens.FolderSearch(null); }, true));
            row.Controls.Add(Th.B("Sign out", async () => { await GDrive.SignOut(); Draw("Google Drive"); }));
        }
        page.Add(Th.Section("Your Google app (one-time setup)"));
        var grid = page.Add(new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) }, true);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.Controls.Add(Th.L("Client ID", 9.5f, Th.Muted), 0, 0);
        var id = Th.NoSpell(Th.Box(false)); id.Text = GDrive.ClientId; id.Dock = DockStyle.Fill; id.PlaceholderText = "…apps.googleusercontent.com";
        grid.Controls.Add(id, 1, 0);
        grid.Controls.Add(Th.L("Client secret", 9.5f, Th.Muted), 0, 1);
        var sec = Th.Box(false, 1, true); sec.Text = GDrive.ClientSecret; sec.Dock = DockStyle.Fill;
        grid.Controls.Add(sec, 1, 1);
        var srow = page.Add(Th.Row(false));
        srow.Controls.Add(Th.B("Save and sign in with Google", async () =>
        {
            GDrive.ClientId = id.Text; GDrive.ClientSecret = sec.Text;
            st.Text = "Your browser opens Google's sign-in — choose your account and allow read-only access…"; st.ForeColor = Th.Muted;
            try { var who = await GDrive.SignIn(); st.Text = "✓ Connected " + who; st.ForeColor = Th.Good; Draw("Google Drive"); }
            catch (Exception e) { st.Text = "⚠ " + e.Message; st.ForeColor = Th.Bad; }
        }, true));
        srow.Controls.Add(Th.B("Open Google Cloud Console", () => Shell.Open("https://console.cloud.google.com/")));
        page.Add(st);
        page.Add(Th.Section("Steps (about 10 minutes, in Chrome)"));
        foreach (var step in new[]
                 {
                     "1 · Open console.cloud.google.com and sign in with the Google account that has your project folders.",
                     "2 · Create a project, e.g. \"Nova Enhance\".",
                     "3 · APIs & Services → Library → \"Google Drive API\" → Enable.",
                     "4 · APIs & Services → OAuth consent screen (Google Auth Platform) → External → app name \"Nova Enhance\" → add your email as a Test user.",
                     "5 · Credentials (Clients) → Create → OAuth client ID → Application type: Desktop app → Create.",
                     "6 · Copy the Client ID and Client secret into the two boxes above → Save and sign in with Google.",
                     "7 · Google shows \"Google hasn't verified this app\" (normal for your own app) → Continue → allow read-only access."
                 })
            Note(step, Th.Text);
        Note("Company Google Workspace accounts may need the admin to allow the app.");
    }

    // ------------------------------------------------------------------ Shortcuts
    void Shortcuts()
    {
        page.Add(Th.Section("Everywhere in Windows"));
        Note("Ctrl + Shift + E — enhance the selected text in any program (Word, Outlook, browser, AutoCAD/Revit text boxes…), then ⇄ Replace.", Th.Text);
        Note("Ctrl + Shift + F — Ask Nova about your email (with a button to search inside a folder).", Th.Text);
        Note("Ctrl + Shift + A — send the selected text to the AI window (Nova Chat or the AI website you used last).", Th.Text);
        Note("Ctrl + Shift + D — Nova for the PDF in front (PDF-XChange, Acrobat, Bluebeam, Foxit, Edge): ask, summary / check drawing, OCR, search, translate.", Th.Text);
        page.Add(Th.Check("Show the small ✦ button on PDF viewers (click it to open Nova for that PDF)", PdfCompanion.Button, v => PdfCompanion.Button = v));
        var hr = page.Add(Th.Row(false));
        hr.Controls.Add(Th.L("Ctrl + Alt +", 10, Th.Text));
        var hk = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        foreach (var c in "QWJKLMNOPUYZ") hk.Items.Add(c.ToString());
        hk.SelectedItem = Store.Str("win_hk_mailask", "Q");
        hk.SelectedIndexChanged += (_, _) => { Store.Set("win_hk_mailask", hk.SelectedItem?.ToString() ?? "Q"); Program.Balloon("Shortcut changed", "It works after Nova restarts (tray ✦ → Exit, then open Nova again)."); };
        hr.Controls.Add(hk);
        hr.Controls.Add(Th.L("— ✦ Ask Nova about the email open or selected in Outlook (any Outlook window; no ribbon needed). Not Ctrl+Shift+Q: that is Outlook's New meeting.", 10, Th.Text));
        Note("In the browser (with the Nova add-on): Alt + G fix grammar · Alt + T translate · right-click selected text → ✦ Nova Enhance.", Th.Text);
        Note("Right-click a folder → Search inside with Nova — find codes, references and sentences inside its files.", Th.Text);
        Note("Left-click the ✦ icon near the clock = 🔍 Search for email · right-click = menu.", Th.Text);
        page.Add(Th.Section("Start"));
        page.Add(Th.Check("Start Nova Enhance with Windows", Store.Autostart, v => { Store.Autostart = v; Startup.Apply(); }));
    }

    // ------------------------------------------------------------------ Browser add-on
    void BrowserPage()
    {
        Note("A ✦ Nova Enhance add-on for Firefox Developer Edition, Chrome and Edge: right-click selected text → fix grammar, rewrite or translate in place (Gmail, Outlook web, WhatsApp Web, forms); Alt+G and Alt+T; and a ✦ panel to summarise the page or ask about it. It uses Nova on this PC (keep Nova running), so your AI key stays here.");
        page.Add(Th.Section("Status"));
        Note(BrowserAddon.On ? $"✓ Prepared (version {Store.Str("win_browser_written", BrowserAddon.Version)}) · " + (Server.BrowserRunning ? "✓ Nova is listening for the add-on" : "⚠ not listening — restart Nova") : "Not prepared yet", BrowserAddon.On && Server.BrowserRunning ? Th.Good : Th.Warn);
        var st = Th.L("", 9.5f, Th.Good);
        var r = page.Add(Th.Row());
        r.Controls.Add(Th.B(BrowserAddon.On ? "Prepare again" : "Prepare the add-on", () =>
        {
            try { BrowserAddon.Enable(); st.Text = "✓ Ready — follow the steps below"; Shell.Open(BrowserAddon.Folder); }
            catch (Exception e) { st.Text = "⚠ " + e.Message; st.ForeColor = Th.Bad; Log.Error("Browser add-on · prepare", e); }
            Draw("Browser add-on");
        }, true));
        r.Controls.Add(Th.B("📁 Add-on folder", () => Shell.Open(BrowserAddon.Folder)));
        r.Controls.Add(Th.B("⧉ Copy Chrome/Edge folder path", () => { Clipboard.SetText(BrowserAddon.ChromeFolder); st.Text = "⧉ Copied — paste it in the Load unpacked window"; }));
        if (BrowserAddon.On) r.Controls.Add(Th.B("Turn off", () => { BrowserAddon.On = false; Draw("Browser add-on"); }));
        page.Add(st);
        page.Add(Th.Section("Chrome or Edge (once)"));
        Note("1 · Open chrome://extensions (Edge: edge://extensions) → turn on Developer mode.", Th.Text);
        Note("2 · Load unpacked → choose Documents\\Nova Enhance\\Browser Add-on\\Chrome and Edge (use the Copy button above).", Th.Text);
        Note("3 · Puzzle icon → pin ✦ Nova Enhance. Click it to open the side panel. Updates arrive by themselves with Nova.");
        page.Add(Th.Section("Firefox Developer Edition (once)"));
        Note("1 · about:config → xpinstall.signatures.required = false, and extensions.checkUpdateSecurity = false.", Th.Text);
        Note("2 · about:addons → ⚙ → Install Add-on From File → NovaEnhance-Firefox.xpi (in the add-on folder).", Th.Text);
        Note("3 · Click ✦ on the toolbar to open the Nova sidebar. Shortcuts: Alt+G, Alt+T (change them in about:addons → ⚙ → Manage Extension Shortcuts).");
    }

    // ------------------------------------------------------------------ AI window
    void AiWindowPage()
    {
        Note("💬 One window with ✦ Nova Chat (your AI key in Nova, with About me, files, folders and emails, answers with sources) and your AI websites — Gemini, ChatGPT, Claude, DeepSeek, Copilot, plus any site you add. Sign in once in each tab; you stay signed in (kept in this Windows account only).");
        var r = page.Add(Th.Row());
        r.Controls.Add(Th.B("💬 Open the AI window", () => Screens.Hub(null), true));
        r.Controls.Add(Th.B("Reset the website list", () => { Store.Set("win_hub_sites", ""); Note("✓ Default sites back — reopen the AI window.", Th.Good); }));
        page.Add(Th.Section("Tips"));
        Note("Ctrl + Shift + A in any program puts the selected text into the AI window, ready for your instruction.", Th.Text);
        Note("◫ Side by side shows two tabs at once; type in \"Same question to both\" to compare two AIs (or Nova Chat and a website).", Th.Text);
        Note("Right-click a website tab to rename, reload, open it in your browser or remove it. ＋ Add a site for any other AI.", Th.Text);
        Note("Nova Chat keeps your chats in Documents\\Nova Enhance\\Nova Chat. Your privacy choices still apply: Folder search (Never / Ask / Send) and Email & AI (first lines or full email).");
    }

    void Opt((string Value, string Label)[] items, string current, Action<string> set)
    {
        foreach (var (v, l) in items) { var vv = v; Opt(l, "", current == v, () => set(vv)); }
    }

    // ------------------------------------------------------------------ ✦ Prompts
    void PromptsPage()
    {
        Note("Write what you want in your own words under any question (✎ More instructions): Nova turns it into rules on this PC, an optional AI check of each email, grouping and an Excel / Word / CSV file. Choose here what suits you — every question can still change the level.");
        page.Add(Th.Section("Default filter level"));
        Opt(new[] { ("A", "A · Rules on this PC only — exclude, only, from, attachments, days, group, export. Fast; nothing extra is sent."),
                    ("B", "B · A + AI check of each email — for instructions a rule can't judge (\"only where the contractor asks for my approval\")") },
            Prompts.Level, v => Prompts.Level = v);
        var mx = new NumericUpDown { Minimum = 10, Maximum = 300, Value = Math.Clamp(Prompts.MaxAi, 10, 300), Width = 70, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        mx.ValueChanged += (_, _) => Prompts.MaxAi = (int)mx.Value;
        var r1 = page.Add(Th.Row(false)); r1.Controls.Add(Th.L("AI check: at most", 10, Th.Text)); r1.Controls.Add(mx); r1.Controls.Add(Th.L("emails per question", 10, Th.Text));

        page.Add(Th.Section("What the AI check may read"));
        Opt(new[] { ("lines", "Subject and first lines of each email"), ("whole", "The whole email text (no attachments) — most accurate") }, Prompts.AiSends, v => Prompts.AiSends = v);
        var ao = new NumericUpDown { Minimum = 0, Maximum = 300, Value = Math.Clamp(Prompts.AskOver, 0, 300), Width = 70, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        ao.ValueChanged += (_, _) => Prompts.AskOver = (int)ao.Value;
        var r2 = page.Add(Th.Row(false)); r2.Controls.Add(Th.L("Ask me before sending more than", 10, Th.Text)); r2.Controls.Add(ao); r2.Controls.Add(Th.L("emails (0 = always ask)", 10, Th.Text));

        page.Add(Th.Section("When my AI is busy"));
        Opt(new[] { ("other", "Use my other AI key automatically (Gemini busy → Groq / OpenAI / Claude…)"), ("tell", "Just tell me") }, Store.Str("win_ai_busy", "other"), v => Store.Set("win_ai_busy", v));
        if (Ai.WithKeys.Length < 2) Note("You have one AI key — add a second in AI & API keys (Groq is free) so a busy AI never stops you.", Th.Warn);

        page.Add(Th.Section("Output"));
        Opt(new[] { ("xlsx", "\"Export\" without a format = Excel (.xlsx)"), ("docx", "= Word (.docx)"), ("csv", "= CSV") }, Prompts.DefaultExport, v => Prompts.DefaultExport = v);
        var fr = page.Add(Th.Row(false));
        var fl = Th.L("Files go to: " + Prompts.Folder, 9.5f, Th.Muted);
        fr.Controls.Add(fl);
        fr.Controls.Add(Th.B("Change…", () => { using var d = new FolderBrowserDialog { SelectedPath = Prompts.Folder }; if (d.ShowDialog(this) == DialogResult.OK) { Prompts.Folder = d.SelectedPath; fl.Text = "Files go to: " + d.SelectedPath; } }));
        fr.Controls.Add(Th.B("📁 Open", () => Shell.Open(Prompts.Folder)));
        page.Add(Th.Check("Show ✎ More instructions open in Ask Nova", Prompts.ShowMore, v => Prompts.ShowMore = v));
        page.Add(Th.Check("Show the ✦ Nova understood chips", Prompts.ShowChips, v => Prompts.ShowChips = v));

        page.Add(Th.Section("🕘 Chat history"));
        page.Add(Th.Check("Save every Nova conversation automatically (readable page + search in 🕘 Chat history; included in your backup)", Conversations.On, v => Conversations.On = v));
        var cr = page.Add(Th.Row(false));
        var cl = Th.L("Saved in: " + Conversations.Dir, 9.5f, Th.Muted);
        cr.Controls.Add(cl);
        cr.Controls.Add(Th.B("Change…", () => { using var d = new FolderBrowserDialog { SelectedPath = Conversations.Dir }; if (d.ShowDialog(this) == DialogResult.OK) { Conversations.Dir = d.SelectedPath; cl.Text = "Saved in: " + d.SelectedPath; } }));
        cr.Controls.Add(Th.B("🕘 Open Chat history", Screens.ChatHistory, true));

        page.Add(Th.Section("⭐ Saved prompts and schedules"));
        var all = Prompts.All();
        if (all.Count == 0) Note("None yet — ask a question with instructions, then press ⭐ Save this prompt.", Th.Muted);
        foreach (var sp in all.OrderBy(x => x.Name))
        {
            var p0 = sp;
            var row = page.Add(Th.Row(false));
            var lbl = Th.L($"⭐ {sp.Name}  ·  {Prompts.Describe(sp)}" + (sp.LastRun > DateTime.MinValue ? $"  ·  last run {sp.LastRun:ddd dd MMM HH:mm}: {sp.LastNote}" : ""), 9.5f, Th.Text);
            lbl.MaximumSize = new Size(620, 0);
            row.Controls.Add(lbl);
            row.Controls.Add(Th.B("▶", async () =>
            {
                if (p0.Where == "ask") { lbl.Text = $"⭐ {p0.Name} · running…"; try { lbl.Text = $"⭐ {p0.Name} · ✓ {await Prompts.RunSaved(p0, this)}"; } catch (Exception e) { lbl.Text = "⚠ " + e.Message; } }
                else Screens.FolderSearch(p0.Where.StartsWith("folder|") ? p0.Where[7..] : null);
            }, true));
            row.Controls.Add(Th.B("✎", () => { if (Prompts.AskSave(this, p0) != null) Draw("✦ Prompts"); }));
            row.Controls.Add(Th.B("🗑", () => { if (MessageBox.Show(this, $"Delete “{p0.Name}”?", "Nova Enhance", MessageBoxButtons.YesNo) == DialogResult.Yes) { Prompts.Delete(p0.Id); Draw("✦ Prompts"); } }));
        }
    }

    // ------------------------------------------------------------------ Spelling & grammar
    void SpellPage()
    {
        Note("Red wavy line = spelling (Windows' own spell checker, on this PC: English, Arabic, French). Blue wavy line = grammar (your AI checks the sentence when you pause). Click a line for the fixes, ＋ Add to my words, Ignore or ✦ Fix all · Ctrl+Space fixes the next one.");
        if (!Speller.Available) Note("⚠ The Windows spell checker is not available on this PC (Windows Settings → Time & language → Language: add English with \"Basic typing\").", Th.Warn);
        page.Add(Th.Check("Underline spelling mistakes as I type (on this PC)", Speller.On, v => Speller.On = v));
        page.Add(Th.Check("Check grammar with my AI when I pause (questions, notes, Enhance text)", Speller.GrammarOn, v => Speller.GrammarOn = v));
        page.Add(Th.Check("Smart search: correct typos before searching (↩ search exactly as typed is shown under the box)", Speller.SmartSearch, v => Speller.SmartSearch = v));
        page.Add(Th.Section("My words (never marked)"));
        Note("Your project words, names and codes. Words in About me → terms, names from your emails, and the vocabulary Nova learned are never marked either. Codes with numbers (CSU1095, EI 60) and short capitals (PRJ, IFC) are always skipped.", Th.Muted);
        var words = page.Add(Th.NoSpell(Th.Box(true, 5)), true);
        words.Text = Store.Str("win_my_words").Replace("|", "\r\n");
        var st = Th.L("", 9.5f, Th.Good);
        var r = page.Add(Th.Row());
        r.Controls.Add(Th.B("Save my words", () =>
        {
            Store.Set("win_my_words", string.Join("|", words.Lines.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)));
            st.Text = "✓ Saved (used within a few minutes)";
        }, true));
        r.Controls.Add(st);
    }

    // ------------------------------------------------------------------ 🧠 What Nova knows
    void KnowsPage()
    {
        var m = Memory.Data;
        Note("What Nova learned from you — kept encrypted on this PC (your Windows account only) and inside your backup. Nothing here is sent anywhere unless it helps answer a question you ask.");
        Note($"🧠 {m.Cards.Count} remembered answers · {m.Vocab.Count} vocabulary words · {m.Boosts.Count} file hints · 👍 {m.Good} · 👎 {m.Bad} · writing style: {(string.IsNullOrWhiteSpace(m.Style) ? "not learned" : "learned " + m.StyleAt.ToString("dd MMM yyyy"))}", Th.AccentText);
        page.Add(Th.Check("Learn from my 👍 / 👎 and answer similar questions at once (then re-check the files)", Memory.On, v => Memory.On = v));
        page.Add(Th.Check("Add 🎓 Expert advice under answers (general knowledge, clearly kept apart from your documents)", Memory.AdviceOn, v => Memory.AdviceOn = v));

        page.Add(Th.Section("Remembered answers"));
        if (m.Cards.Count == 0) Note("None yet — press 👍 under a good answer (folder Ask AI or the PDF panel).", Th.Muted);
        foreach (var c in m.Cards.OrderByDescending(c => c.Saved).Take(40).ToList())
        {
            var row = page.Add(Th.Row(false));
            var where = c.Kind == "pdf" ? Path.GetFileName(c.Root) : GDrive.Display(c.Root);
            row.Controls.Add(Th.L($"{c.Saved:dd MMM yyyy} · {(c.Q.Length > 70 ? c.Q[..70] + "…" : c.Q)} · {where} · used {c.Uses}×", 9.5f, Th.Text));
            var card = c;
            row.Controls.Add(Th.B("View", () => MessageBox.Show(this, card.Q + "\n\n" + card.A + "\n\nFiles:\n" + string.Join("\n", card.Files), "🧠 Remembered answer")));
            row.Controls.Add(Th.B("🗑", () => { Memory.Data.Cards.Remove(card); Memory.Save(); Draw("🧠 What Nova knows"); }));
        }

        page.Add(Th.Section("Project vocabulary"));
        Note("One per line: ABBREVIATION = meaning. Nova uses these to search (PRJ finds My Project too) and never marks them as spelling mistakes. Learned from passages you liked; edit freely.", Th.Muted);
        var vocab = page.Add(Th.NoSpell(Th.Box(true, 6)), true);
        vocab.Text = string.Join("\r\n", m.Vocab.OrderBy(x => x.Key).Select(x => x.Key + " = " + x.Value));
        var vs = Th.L("", 9.5f, Th.Good);
        var vr = page.Add(Th.Row());
        vr.Controls.Add(Th.B("Save vocabulary", () =>
        {
            Memory.Data.Vocab.Clear();
            foreach (var line in vocab.Lines)
            {
                var i = line.IndexOf('=');
                if (i > 0 && line[(i + 1)..].Trim().Length > 0) Memory.Data.Vocab[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
            Memory.Save(); vs.Text = $"✓ {Memory.Data.Vocab.Count} words saved";
        }, true));
        vr.Controls.Add(vs);

        page.Add(Th.Section("Project map — current revision per document"));
        var roots = FolderIndex.Roots().Take(3).ToList();
        if (roots.Count == 0) Note("Search a project folder once and Nova maps its revisions.", Th.Muted);
        foreach (var root in roots)
        {
            var groups = Revisions.Groups(root.Path);
            Note($"📁 {GDrive.Display(root.Path)} — {groups.Count} documents with several revisions", Th.Text);
            if (groups.Count > 0)
            {
                var box = page.Add(Th.NoSpell(Th.Box(true, Math.Min(8, groups.Count + 1))), true);
                box.ReadOnly = true; box.Font = Th.F(9);
                box.Text = string.Join("\r\n", groups.Take(300).Select(g => $"{g.Name}   ← current{(g.Rev.Length > 0 ? " (Rev " + g.Rev + ")" : "")} · {g.Older} older"));
            }
        }

        page.Add(Th.Section("My writing style (for ✦ Draft reply)"));
        Note("Only when you allow it: Nova reads about 40 of your recent sent emails (your own text, not quoted replies), you see exactly what is sent, and your AI writes a short description of how you write. Only that description is kept.", Th.Muted);
        page.Add(Th.Check("Write ✦ Draft reply in my style", Memory.StyleOn, v => Memory.StyleOn = v));
        var style = page.Add(Th.Box(true, 4), true);
        style.Text = m.Style;
        style.PlaceholderText = "Not learned yet";
        var ss = Th.L("", 9.5f, Th.Good);
        var sr = page.Add(Th.Row());
        sr.Controls.Add(Th.B("✦ Learn my style from sent emails", async () => { ss.Text = "Reading your sent emails…"; ss.Text = await Memory.LearnStyle(this); style.Text = Memory.Data.Style; }, true));
        sr.Controls.Add(Th.B("Save", () => { Memory.Data.Style = style.Text.Trim(); Memory.Save(); ss.Text = "✓ Saved"; }));
        sr.Controls.Add(Th.B("Forget my style", () => { Memory.Data.Style = ""; Memory.StyleOn = false; Memory.Save(); style.Text = ""; ss.Text = "✓ Forgotten"; }));
        sr.Controls.Add(ss);

        page.Add(Th.Section("Start again"));
        page.Add(Th.B("🗑 Forget everything Nova learned", () =>
        {
            if (MessageBox.Show(this, "Delete all remembered answers, vocabulary, file hints and your writing style?", "Nova Enhance", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Memory.ForgetAll(); Draw("🧠 What Nova knows");
        }));
    }

    // ------------------------------------------------------------------ Backup
    void BackupPage()
    {
        Note("One file with all your settings, About me, terms, saved searches, history and kept originals. API keys, the Google Drive sign-in and the Windows choices inside it are locked with your password, so it also restores on a new PC. Each backup is opened again right after it is written to check it. Same format as the phone: a phone backup restores here, and this one restores on the phone.");
        page.Add(Th.Section("Backup password"));
        var pw = page.Add(Th.Row(false));
        var a = Th.Box(false, 1, true); a.Width = 220; a.PlaceholderText = "Password";
        var b = Th.Box(false, 1, true); b.Width = 220; b.PlaceholderText = "Repeat password";
        a.Text = b.Text = Store.BackupPassword;
        pw.Controls.Add(a); pw.Controls.Add(b);
        var pst = Th.L(Store.BackupPassword.Length > 0 ? "✓ Set" : "Not set — daily backup waits for it", 9.5f, Store.BackupPassword.Length > 0 ? Th.Good : Th.Warn);
        pw.Controls.Add(Th.B("Save password", () =>
        {
            if (a.Text.Length < 4) { pst.Text = "At least 4 characters"; pst.ForeColor = Th.Warn; return; }
            if (a.Text != b.Text) { pst.Text = "The two passwords are different"; pst.ForeColor = Th.Warn; return; }
            Store.BackupPassword = a.Text; pst.Text = "✓ Saved (locked to your Windows account)"; pst.ForeColor = Th.Good;
        }, true));
        pw.Controls.Add(pst);

        page.Add(Th.Section("Automatic backup"));
        var ab = page.Add(Th.Row(false));
        ab.Controls.Add(Th.Check("Every day, keep the newest", Store.AutoBackup, v => Store.AutoBackup = v));
        var keep = new NumericUpDown { Minimum = 1, Maximum = 90, Value = Store.KeepBackups, Width = 56, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        keep.ValueChanged += (_, _) => Store.KeepBackups = (int)keep.Value;
        ab.Controls.Add(keep);
        Note(AutoBackup.Status, Th.Text);

        page.Add(Th.Section("Second copy in Google Drive"));
        var gd = page.Add(Th.Row(false));
        var gst = Th.L("", 9.5f, Th.Muted);
        void GdState()
        {
            if (!Backup.DriveCopy) { gst.Text = "Off — backups stay only in Documents\\Nova Enhance\\Backups"; gst.ForeColor = Th.Muted; return; }
            if (!GDrive.SignedIn) { gst.Text = "⚠ Sign in to Google Drive first (Settings → Google Drive)"; gst.ForeColor = Th.Warn; return; }
            if (!GDrive.CanSave) { gst.Text = "⚠ Sign in to Google Drive again to allow Nova to save backups"; gst.ForeColor = Th.Warn; return; }
            var n = Store.Str("win_last_drive_note");
            gst.Text = n.Length > 0 ? n : $"✓ On — each backup is also saved in \"Nova Enhance Backups\" in {GDrive.Account}";
            gst.ForeColor = n.StartsWith("⚠") ? Th.Warn : Th.Good;
        }
        gd.Controls.Add(Th.Check("Also save each backup in my Google Drive (folder \"Nova Enhance Backups\", same number kept)", Backup.DriveCopy, v => { Backup.DriveCopy = v; GdState(); }));
        if (GDrive.Configured && !GDrive.CanSave)
            gd.Controls.Add(Th.B("Sign in to Google Drive", async () =>
            {
                try { await GDrive.SignIn(); } catch (Exception e) { gst.Text = "⚠ " + e.Message; gst.ForeColor = Th.Bad; return; }
                Store.Set("win_last_drive_note", ""); GdState();
            }));
        GdState();
        page.Add(gst);

        page.Add(Th.Section("Now"));
        var st = Th.L("", 9.5f, Th.Good);
        var row = page.Add(Th.Row());
        row.Controls.Add(Th.B("💾 Backup now", async () =>
        {
            var p = Store.BackupPassword;
            if (p.Length == 0) p = Prompt.Ask(this, "Backup", "Password for the API keys in this backup:", "", true);
            if (string.IsNullOrEmpty(p)) return;
            string f;
            try { f = Backup.WriteFile(p, false); st.Text = "✓ " + Path.GetFileName(f) + " · " + Backup.LastCheck; st.ForeColor = Th.Good; Shell.Select(f); }
            catch (Exception e) { st.Text = "⚠ " + e.Message; st.ForeColor = Th.Bad; return; }
            if (Backup.DriveCopy)
            {
                st.Text += " · ☁ copying to Google Drive…";
                var d = await Backup.CopyToDrive(f);
                st.Text = "✓ " + Path.GetFileName(f) + " · " + Backup.LastCheck + d;
                if (d.Contains("⚠")) st.ForeColor = Th.Warn;
                GdState();
            }
        }, true));
        row.Controls.Add(Th.B("↺ Restore…", () =>
        {
            using var d = new OpenFileDialog { Title = "Restore a Nova Enhance backup", InitialDirectory = Paths.Backups, Filter = "Nova Enhance backup (*.novaenhance;*.json)|*.novaenhance;*.json|All files|*.*" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var p = Prompt.Ask(this, "Restore", "Backup password (needed for the API keys):", "", true);
            if (p == null) return;
            try
            {
                var s = Backup.Restore(File.ReadAllText(d.FileName), p);
                MessageBox.Show(s, "Nova Enhance · restored");
                Draw("Backup & restore");
            }
            catch (Exception e) { MessageBox.Show("⚠ " + e.Message, "Nova Enhance"); }
        }));
        row.Controls.Add(Th.B("📁 Backups folder", () => Shell.Open(Paths.Backups)));
        page.Add(st);
        page.Add(Th.Section("Your folder"));
        Note(Paths.Root, Th.Text);
        page.Add(Th.Row(false)).Controls.Add(Th.B("📁 Open Documents\\Nova Enhance", () => Shell.Open(Paths.Root)));
    }

    // ------------------------------------------------------------------ Updates
    static string UpdateText() => $"This is {Program.Shown}. " + (Updater.Status.Length > 0 ? Updater.Status : "Checked once a day from GitHub (signature and SHA-256 verified before installing).");

    void Updates()
    {
        liveUpdate = Note(UpdateText(), Th.Text);
        var row = page.Add(Th.Row(false));
        row.Controls.Add(Th.B("⬆ Check for updates", async () =>
        {
            liveUpdate.Text = "Checking GitHub…";
            var s = await Updater.Check(t => BeginInvoke(() => liveUpdate.Text = t));
            liveUpdate.Text = s;
            Updater.Offer(true);
        }, true));
        row.Controls.Add(Th.B("Install the waiting update", () => Updater.Offer(true)));
        Note("Updates install for your Windows user only (no admin needed). Nova closes, updates and opens again by itself.");
    }

    // ------------------------------------------------------------------ Error log
    string logFilter = "today";
    bool logErrorsOnly;

    int actDays = 1;

    /// <summary>📱 Phone link (2.0): the phone gets Nova's data from this PC, encrypted with the key in the QR code.</summary>
    void PhoneLinkPage()
    {
        Note("Your phone gets Nova's data from this PC — every email with full text (all periods), attachment text, brief, email projects, project-file catalogue, ⭐ starred items, rules, prompts, profile, vocabulary and My Work — encrypted with the key in the QR code. 🔒 Private folders and files (wallet, password, bank…) never leave this PC.");
        page.Add(Th.Check("📱 Link my phone (prepare and send the data)", PhoneLink.On, v => PhoneLink.On = v));

        page.Add(Th.Section("Pair your phone"));
        var pr = page.Add(Th.Row(false));
        var qrBox = new PictureBox { Width = 260, Height = 260, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Margin = new Padding(0, 4, 16, 4) };
        void DrawQr()
        {
            try
            {
                using var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(PhoneLink.PairLink(), QRCoder.QRCodeGenerator.ECCLevel.M);
                var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
                using var ms = new MemoryStream(png);
                qrBox.Image?.Dispose();
                qrBox.Image = new Bitmap(Image.FromStream(ms));
            }
            catch (Exception e) { Log.Warn("phone link", "QR: " + e.Message); }
        }
        var qrShown = false;
        Button showQr = null;
        showQr = Th.B("🔳 Show the pairing QR code", () => { if (!qrShown) { DrawQr(); qrShown = true; showQr.Text = "🙈 Hide the QR code"; qrBox.Visible = true; } else { qrBox.Image?.Dispose(); qrBox.Image = null; qrBox.Visible = false; qrShown = false; showQr.Text = "🔳 Show the pairing QR code"; } }, true);
        qrBox.Visible = false;
        pr.Controls.Add(qrBox);
        var pc = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent };
        pc.Controls.Add(Th.L("On the phone: Nova Enhance → 🖥 My PC → Scan to pair.\nThe QR code holds the encryption key, this PC's office address and the relay key — show it only to your own phone.", 10, Th.Muted));
        pc.Controls.Add(showQr);
        pc.Controls.Add(Th.L(PhoneLink.PhoneName.Length > 0 ? $"✓ Paired: {PhoneLink.PhoneName}" + (PhoneLink.PhoneSeen > DateTime.MinValue ? $" · seen {PhoneLink.PhoneSeen:dd MMM HH:mm}" : "") : "No phone paired yet.", 10, PhoneLink.PhoneName.Length > 0 ? Th.Good : Th.Muted));
        pc.Controls.Add(Th.B("🔑 New key (un-pair the phone)", () =>
        {
            if (MessageBox.Show(this, "Make a new key? The phone stops receiving data until it scans the new QR code, and the next sync is a full copy.", "Nova Enhance", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            PhoneLink.NewKey(); if (qrShown) DrawQr();
        }));
        pr.Controls.Add(pc);

        page.Add(Th.Section("Sync status"));
        var st = Note("", Th.Muted);
        // Loaded in the background: the page opens at once (sizes and drive checks can take seconds).
        async void Line()
        {
            st.Text = "Reading the status…";
            var t = await Task.Run(() => (PhoneLink.Status.Length > 0 ? PhoneLink.Status + "\n" : "") +
                (PhoneLink.LastRun > DateTime.MinValue ? $"Last update {PhoneLink.LastRun:dd MMM HH:mm} · next about {PhoneLink.LastRun.AddMinutes(PhoneLink.EveryMin):HH:mm} · every {PhoneLink.EveryMin} min" : "Not sent yet") +
                (PhoneLink.LastError.Length > 0 ? "\n⚠ " + PhoneLink.LastError : "") + "\n" + PhoneLink.SizesText());
            if (!st.IsDisposed) { st.Text = t; page.FitAll(); }
        }
        Line();
        foreach (var (k, l) in new[] { ("email", "📥 Email — all periods, full text"), ("att", "📎 Attachment text (PDF, Word, Excel…)"), ("brief", "🌅 Brief · 📁 Email projects"), ("files", "📂 Project files catalogue"), ("star", "⭐ Starred emails and files (the real files)"), ("brain", "📏 Rules · ✦ Prompts · 👤 Profile · 📚 Vocabulary · 📌 My Work") })
        { var key = k; page.Add(Th.Check(l, PhoneLink.Send(key), v => PhoneLink.SetSend(key, v))); }
        page.Add(Th.Check($"Full text for the last {PhoneLink.RecentMonths} months only (older emails: subject, people, date, attachment names)", PhoneLink.Last12, v => PhoneLink.Last12 = v));

        page.Add(Th.Section("How the phone gets it — use any or all"));
        page.Add(Th.Check("A · ☁ Google Drive — full copy + hourly updates, from anywhere", PhoneLink.DriveOn, v => PhoneLink.DriveOn = v));
        var drNote = Note("Looking for Google Drive for desktop…", Th.Muted);
        async void DriveLine()
        {
            var dr = await Task.Run(() => PhoneLink.DriveRoot);
            if (drNote.IsDisposed) return;
            drNote.Text = dr.Length > 0 ? $"✓ Google Drive for desktop found: {dr} → Nova Enhance Sync (Drive uploads it by itself)" : "⚠ Google Drive for desktop was not found — install it (drive.google.com/download) or pick your Drive folder. Until then the files stay in Documents\\Nova Enhance\\Nova Enhance Sync.";
            drNote.ForeColor = dr.Length > 0 ? Th.Good : Th.Warn;
            page.FitAll();
        }
        DriveLine();
        // ☁ The phone reads Google Drive itself (no PC needed): your Google sign-in app + where the work folder is copied in Drive.
        var phNote = Note(GDrive.Configured
            ? "📱 The phone reads this folder from Google Drive by itself (one Google sign-in on the phone: 🖥 My PC → ⚙ → Sign in to Google Drive). The PC can be off."
            : "📱 For the phone to read Google Drive by itself, set up the Google sign-in first (Settings → Google Drive → Client ID and secret).", GDrive.Configured ? Th.Text : Th.Warn);
        var mrow = page.Add(Th.Row(false));
        mrow.Controls.Add(Th.L("📂 Work folder copied to Google Drive (FreeFileSync…) at  My Drive /", 9.5f, Th.Text));
        var mbox = Th.NoSpell(Th.Box(false)); mbox.Width = 200;
        var mset = Store.Str("win_pl_mirror", "auto");
        mbox.Text = mset == "auto" ? "" : mset == "-" ? "" : mset;
        mbox.PlaceholderText = "auto";
        mbox.Leave += (_, _) => Store.Set("win_pl_mirror", mbox.Text.Trim().Length == 0 ? "auto" : mbox.Text.Trim() == "/" ? "" : mbox.Text.Trim());
        mrow.Controls.Add(mbox);
        var mNote = Note("Checking…", Th.Muted);
        async void MirrorLine()
        {
            var m = await Task.Run(() => PhoneLink.Mirror);
            if (mNote.IsDisposed) return;
            mNote.Text = m == null ? $"⚠ {Projects.WorkFolder} was not found in Google Drive — the phone opens files through the PC (Wi-Fi / relay). Type the folder above if it is copied elsewhere (/ = My Drive itself)."
                : $"✓ {Projects.WorkFolder} = My Drive{(m.Length > 0 ? "/" + m : "")} — tapping a file on the phone opens it straight from Google Drive.";
            mNote.ForeColor = m == null ? Th.Warn : Th.Good;
            page.FitAll();
        }
        MirrorLine();
        var dpick = page.Add(Th.Row(false));
        dpick.Controls.Add(Th.B("📁 Pick the Google Drive folder…", () =>
        {
            using var d = new FolderBrowserDialog { Description = "Your Google Drive folder (My Drive)" };
            if (d.ShowDialog(this) == DialogResult.OK) { Store.Set("win_pl_drive_root", d.SelectedPath); Go("📱 Phone link"); }
        }));
        dpick.Controls.Add(Th.B("📂 Open the Sync folder", () => Shell.Open(PhoneLink.Dir)));
        page.Add(Th.Check("B · 🔁 GitHub relay — live questions from the phone (search, brief, fetch a file) + small updates when Drive is off", PhoneLink.RelayOn, v => PhoneLink.RelayOn = v));
        Note(Relay.On ? $"✓ {Relay.Repo} (Settings → 🔌 Test connector)" : "⚠ Switch on 🌐 A · GitHub relay in 🔌 Test connector first.", Relay.On ? Th.Good : Th.Warn);
        page.Add(Th.Check("C · 📶 Office Wi-Fi — direct and fastest when the phone is on the same network (used first)", PhoneLink.WifiOn, v => { PhoneLink.WifiOn = v; }));
        Note((PhoneLink.WifiStatus.Length > 0 ? PhoneLink.WifiStatus : "Starts when 📱 Link my phone is on") + " · Windows may ask once to allow Nova on the network — choose Private networks.", Th.Muted);

        page.Add(Th.Section("Timing"));
        var tr = page.Add(Th.Row(false));
        tr.Controls.Add(Th.L("Update every", 10, Th.Text));
        foreach (var (m, l) in new[] { (30, "30 min"), (60, "1 h"), (180, "3 h") })
        { var mm = m; tr.Controls.Add(Th.Radio(l, PhoneLink.EveryMin == mm, () => PhoneLink.EveryMin = mm)); }
        page.Add(Th.Check("Also right after the morning brief", PhoneLink.AfterBrief, v => PhoneLink.AfterBrief = v));
        var br = page.Add(Th.Row(false));
        br.Controls.Add(Th.B("⟳ Update the phone now", () => { st.Text = "⏳ preparing…"; _ = Task.Run(() => { PhoneLink.RunSafe("Update now"); BeginInvoke(Line); }); }, true));
        br.Controls.Add(Th.B("⟳ Make a new full copy", () => { Store.Set("win_pl_state", "{}"); st.Text = "⏳ preparing the full copy…"; _ = Task.Run(() => { PhoneLink.RunSafe("new full copy"); BeginInvoke(Line); }); }));
        br.Controls.Add(Th.B("📋 What was sent (Activity)", () => Go("🧪 Activity history")));
        Note("⚠ With ☁ A, company emails (encrypted) are stored in your Google Drive — check your company's IT policy. Untick A to keep everything inside the office network.", Th.Warn);
    }

    /// <summary>🔌 Test connector: Claude (through the Claude desktop app) tests Nova on this PC with test tools only.</summary>
    void TestConnectorPage()
    {
        Note("Lets Claude test Nova on this PC through the Claude desktop app — so problems are checked and fixed without screenshots and ZIPs. Off by default · this PC only (127.0.0.1) · its own key · web pages can't use it.");
        page.Add(Th.Section("What Claude can do"));
        Note("✓ Nova's status (index, mailboxes, archive files, projects, background jobs)\n✓ run a real email search and see every step, timing and decision\n✓ check whether an email (subject words or a document number) is in the index\n✓ read the 🧪 Activity history, the error log, crash and freeze notes\n✓ run self-checks (index, phrase search, Outlook, archives, AI keys, LM Studio)");
        Note("✗ never email bodies or the text sent to the AI · ✗ never 🔒 private emails, folders and files (wallet, password, bank…) · ✗ can't send, move or delete anything · names and addresses are hidden as Person N / personN@…", Th.Muted);
        page.Add(Th.Section("Safety"));
        var st = Note("", Th.Muted);
        void Line() => st.Text = !TestConnector.On ? "Claude desktop: off." :
            TestConnector.Running ? $"✓ On — waiting for Claude on 127.0.0.1:{TestConnector.Port}" : "⚠ Could not start: " + TestConnector.LastError;
        page.Add(Th.Check("✋ Ask me before each request (Allow · Allow for 1 hour · Deny)", TestConnector.AskEach, v => TestConnector.AskEach = v));
        Note("Three ways for Claude to reach Nova — use the one that works on this PC.", Th.Muted);

        // ---------------- 🌐 A · GitHub relay
        page.Add(Th.Section("🌐 A · GitHub relay — no app to install (recommended)"));
        Note("Claude writes a test request into YOUR PRIVATE GitHub repository; Nova checks it every 30 s, runs the test here and writes the answer back. No port is opened on this PC; works from any Claude chat that has GitHub. Answers are deleted after 7 days.");
        Note("Once: 1. On github.com create a PRIVATE repository named nova-test-relay (tick “Add a README”).  2. github.com → Settings → Developer settings → Fine-grained tokens → Generate: Repository access = Only select repositories → nova-test-relay; Permissions → Contents = Read and write; Expiration = 90 days.  3. Paste the key below, press Save and check.", Th.Muted);
        var rr = page.Add(Th.Row(false));
        rr.Controls.Add(Th.L("Repository", 10, Th.Text));
        var repoBox = new TextBox { Text = Relay.Repo, Width = 300, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, Font = Th.F(10f) };
        rr.Controls.Add(repoBox);
        rr.Controls.Add(Th.L("GitHub key", 10, Th.Text));
        var tokBox = new TextBox { Text = Relay.Token.Length > 0 ? "••••••••••••" : "", Width = 300, UseSystemPasswordChar = true, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, Font = Th.F(10f) };
        rr.Controls.Add(tokBox);
        var rst = Note(Relay.Status.Length > 0 ? Relay.Status : Relay.On ? "On" : "Off", Th.Muted);
        var rb = page.Add(Th.Row(false));
        rb.Controls.Add(Th.B("💾 Save and check", async () =>
        {
            Relay.Repo = repoBox.Text;
            if (tokBox.Text.Length > 0 && !tokBox.Text.StartsWith("••")) Relay.Token = tokBox.Text;
            rst.Text = "⏳ checking…";
            try { rst.Text = await Relay.Check(); } catch (Exception ex) { rst.Text = "⚠ " + ex.Message; }
        }, true));
        page.Add(Th.Check("🌐 Answer Claude's requests through this repository", Relay.On, async v =>
        {
            if (v) { try { await Relay.Check(); } catch (Exception ex) { rst.Text = "⚠ " + ex.Message + " — not switched on (untick and tick again after fixing)."; return; } }
            Relay.On = v; rst.Text = v ? "✓ On — Nova checks every 30 s" : "Off";
        }));

        // ---------------- 🌍 B · internet connector (claude.ai)
        page.Add(Th.Section("🌍 B · Internet connector for claude.ai"));
        Note("Nova opens a secure Cloudflare tunnel (one small program, downloaded once, no admin rights) and gets a secret internet address. Add it in claude.ai → Settings → Connectors → Add custom connector (name: Nova test). Answers are instant. The address changes each time Nova or the tunnel restarts — paste the new one in claude.ai. Company networks or antivirus may block tunnels; then use A.");
        var bst = Note("", Th.Muted);
        var urlBox = new TextBox { ReadOnly = true, Width = 900, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9.5f) };
        void BLine() { bst.Text = !Remote.On ? "Off" : Remote.Status; urlBox.Text = Remote.Url.Length > 0 ? Remote.Url : "(the address appears here when the tunnel is online — press ↻)"; }
        page.Add(Th.Check("🌍 Open the internet connector", Remote.On, v => { Remote.On = v; BLine(); }));
        page.Add(urlBox, true);
        var ub = page.Add(Th.Row(false));
        ub.Controls.Add(Th.B("⧉ Copy the address", () => { if (Remote.Url.Length > 0) { Clipboard.SetText(Remote.Url); bst.Text = "⧉ Copied — paste it in claude.ai → Settings → Connectors → Add custom connector."; } }, true));
        ub.Controls.Add(Th.B("↻ Refresh", BLine));
        ub.Controls.Add(Th.B("🔑 New secret address", () => { Remote.NewSecret(); BLine(); bst.Text = "🔑 New secret — the old address stops working; paste the new one in claude.ai."; }));
        ub.Controls.Add(Th.B("🌐 claude.ai connectors", () => Shell.Open("https://claude.ai/settings/connectors")));
        BLine();

        page.Add(Th.Section("C · Claude desktop app (if it is installed)"));
        page.Add(Th.Check("🔌 Let Claude desktop test Nova on this PC", TestConnector.On, v => { TestConnector.On = v; Line(); }));
        Line();
        Note("1. Press “Add to Claude desktop” (Nova writes its entry in Claude desktop's settings; the old file is kept as .bak).\n2. Quit Claude desktop completely (tray icon → Quit) and open it again.\n3. In Claude desktop, open the chat with Claude and choose “Link to this computer” (or start a new task on this PC). Claude now sees the tools “nova_status, nova_search, nova_find_email, nova_activity, nova_errors, nova_selfcheck”.");
        var cr = page.Add(Th.Row(false));
        var cst = Note("", Th.Muted);
        cr.Controls.Add(Th.B("➕ Add to Claude desktop", () =>
        {
            try { var f = TestConnector.AddToClaude(); cst.Text = "✓ Added in " + string.Join(" · ", f) + " — now quit and reopen Claude desktop."; }
            catch (Exception ex) { cst.Text = "⚠ " + ex.Message; }
        }, true));
        cr.Controls.Add(Th.B("⧉ Copy the setup (by hand)", () => { Clipboard.SetText(TestConnector.Snippet()); cst.Text = "⧉ Copied — paste it into claude_desktop_config.json (Claude desktop → Settings → Developer → Edit Config)."; }));
        cr.Controls.Add(Th.B("🔑 New key", () => { TestConnector.NewKey(); cst.Text = "🔑 New key made — the old one stops working at once. Restart Claude desktop so it uses the new one."; }));
        cr.Controls.Add(Th.B("📁 Claude desktop settings", () => { var f = TestConnector.ClaudeConfigs()[0]; Shell.Open(Path.GetDirectoryName(f)!); }));

        page.Add(Th.Section("Last calls from Claude"));
        string calls; lock (TestConnector.Calls) calls = TestConnector.Calls.Count == 0 ? "No call yet." : string.Join("\r\n", TestConnector.Calls);
        page.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 200, BackColor = Th.Bg, ForeColor = Th.Text, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.FixedSingle, Text = calls }, true);
        Note("Every call is also kept in 🧪 Activity history (feature “🧪 test connector”).", Th.Muted);
    }

    /// <summary>🧪 Activity history: every task with its steps, timings and thinking — exported as one ZIP for Claude.</summary>
    void ActivityPage()
    {
        Note("For development: Nova records each task (searches, conversations, AI calls, sorting) with the time of every step, what it understood and decided, and the text it sent to the AI. Kept on this PC, encrypted. 📦 Export makes one ZIP to share with Claude — names and email addresses hidden (Person 12, person12@…).");
        page.Add(Th.Check("🧪 Record the activity history", Activity.On, v => Activity.On = v));
        var kr = page.Add(Th.Row(false));
        kr.Controls.Add(Th.L("Keep for", 10, Th.Text));
        foreach (var d0 in new[] { 7, 14, 30 }) { var dd = d0; kr.Controls.Add(Th.Radio($"{dd} days", Activity.KeepDays == dd, () => Activity.KeepDays = dd)); }

        page.Add(Th.Section("Tasks"));
        var fr = page.Add(Th.Row(false));
        foreach (var (d1, l1) in new[] { (1, "Today"), (7, "7 days"), (14, "14 days"), (30, "30 days") })
        { var dd = d1; fr.Controls.Add(Th.Chip(l1, actDays == dd, () => { actDays = dd; Draw("🧪 Activity history"); })); }
        var items = Activity.Read(actDays);
        var sum = Note("", Th.Muted);
        if (items.Count > 0)
        {
            var feats = items.GroupBy(o => o["feature"]?.ToString() ?? "").OrderByDescending(g => g.Count()).Take(6)
                .Select(g => $"{g.Key}: {g.Count()} · avg {g.Average(o => o["ms"]?.GetValue<long>() ?? 0) / 1000:0.0} s");
            sum.Text = $"{items.Count:N0} tasks · " + string.Join("  ·  ", feats);
        }
        else sum.Text = "Nothing recorded yet for this period — use Nova normally, then come back.";
        var lv = page.Add(new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Height = 260, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(9.5f) }, true);
        lv.Columns.Add("Time", 140); lv.Columns.Add("Feature", 190); lv.Columns.Add("Task", 430); lv.Columns.Add("Time taken", 90); lv.Columns.Add("Result", 260);
        var list = items.OrderByDescending(o => o["start"]?.ToString()).Take(800).ToList();
        foreach (var o in list)
        {
            var ms = o["ms"]?.GetValue<long>() ?? 0;
            var it = new ListViewItem(new[] { o["start"]?.ToString()?[5..19] ?? "", o["feature"]?.ToString() ?? "", o["title"]?.ToString() ?? "", $"{ms / 1000.0:0.0} s", o["result"]?.ToString() ?? "" }) { Tag = o };
            if (o["failed"]?.GetValue<bool>() == true) it.ForeColor = Th.Warn; else if (ms > 15000) it.ForeColor = Color.FromArgb(0xFF, 0xD2, 0x7A);
            lv.Items.Add(it);
        }
        var det = page.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 260, BackColor = Th.Bg, ForeColor = Th.Text, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.FixedSingle, Text = "Select a task to see its steps, timings and thinking." }, true);
        lv.SelectedIndexChanged += (_, _) =>
        {
            if (lv.SelectedItems.Count == 0 || lv.SelectedItems[0].Tag is not System.Text.Json.Nodes.JsonObject o) return;
            var sb = new System.Text.StringBuilder($"{o["start"]}  ·  {o["feature"]}  ·  {(o["ms"]?.GetValue<long>() ?? 0) / 1000.0:0.0} s\r\n{o["title"]}\r\n\r\nSTEPS\r\n");
            foreach (var st in o["steps"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray()) sb.Append($"  {st?["ms"],7} ms   {st?["step"]}   {st?["info"]}\r\n");
            sb.Append("\r\nTHINKING\r\n");
            foreach (var (k, v) in o["thinking"]?.AsObject() ?? new System.Text.Json.Nodes.JsonObject()) sb.Append($"  {k}: {v?.ToString().Replace("\n", "\r\n      ")}\r\n");
            var ai = o["ai_calls"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray();
            if (ai.Count > 0) sb.Append($"\r\nAI CALLS ({ai.Count})\r\n");
            foreach (var a in ai) sb.Append($"  {a?["ai"]} · {a?["ms"]} ms · sent {a?["sent_chars"]} chars{(a?["error"] != null ? " · ⚠ " + a?["error"] : "")}\r\n    answer: {a?["answer"]?.ToString().Replace("\n", " ")}\r\n");
            sb.Append($"\r\nRESULT: {o["result"]}");
            det.Text = sb.ToString();
        };

        page.Add(Th.Section("📦 Export for Claude"));
        Note("One ZIP: activity.jsonl (every task with steps, timings, thinking and AI text), summary.md (averages, slowest tasks, AI calls, failures), errors and system info. Names and email addresses are hidden; project codes, subjects and file names stay.");
        var er = page.Add(Th.Row(false));
        var expStatus = Note("", Th.Muted);
        foreach (var d2 in new[] { 7, 14, 30 })
        {
            var dd = d2;
            er.Controls.Add(Th.B($"📦 Export {dd} days", async () =>
            {
                expStatus.Text = "Preparing the ZIP…";
                try
                {
                    var zip = await Task.Run(() => Activity.Export(dd, t => BeginInvoke(() => { if (!expStatus.IsDisposed) expStatus.Text = t; })));
                    expStatus.Text = "✓ " + zip;
                    Shell.Select(zip);
                }
                catch (Exception ex) { expStatus.Text = "⚠ " + ex.Message; }
            }, dd == 7));
        }
        er.Controls.Add(Th.B("📁 Activity folder", () => Shell.Open(Activity.Dir)));
        Note("Your exports are kept: Nova-Activity-User-…zip in Documents\\Nova Enhance\\Activity.", Th.Muted);

        page.Add(Th.Section("💾 Automatic save"));
        Note("Nova saves the same ZIP by itself (last " + Activity.KeepDays + " days, names hidden) as Nova-Activity-Auto.zip — each automatic save replaces the previous one. Your own exports (User) are never replaced.");
        var ar = page.Add(Th.Row(false));
        ar.Controls.Add(Th.L("Save", 10, Th.Text));
        var autoStatus = Note("", Th.Muted);
        void AutoLine() => autoStatus.Text = Activity.AutoEvery == 0 ? "Off" :
            (Activity.LastAuto == DateTime.MinValue ? "No automatic save yet — the first one comes within a few minutes." : $"Last automatic save: {Activity.LastAuto:dd MMM HH:mm} · next about {Activity.LastAuto.AddHours(Activity.AutoEvery):dd MMM HH:mm}") +
            (File.Exists(Activity.AutoFile) ? "  ·  " + Activity.AutoFile : "");
        foreach (var (h0, l0) in new[] { (0, "Off"), (1, "every 1 h"), (6, "every 6 h"), (24, "every 1 day") })
        { var hh = h0; ar.Controls.Add(Th.Radio(l0, Activity.AutoEvery == hh, () => { Activity.AutoEvery = hh; AutoLine(); })); }
        AutoLine();
        Note("The ZIP holds work text (first lines of emails sent to the AI) — share it only with Claude.", Th.Warn);
    }

    void ErrorLog()
    {
        // 🛡 Anti-crash: Guardian, safe background jobs, Outlook time limits, safe mode, crash reports.
        page.Add(Th.Section("🛡 Anti-crash"));
        if (Guard.SafeMode)
        {
            Note($"🛡 SAFE MODE — the background jobs are off because {Guard.SafeReason}. Switch them on below; if Nova closes again, the Error log shows which job it was.", Th.Warn);
            var sr = page.Add(Th.Row(false));
            sr.Controls.Add(Th.B("▶ Start all background jobs", () => { Guard.SafeMode = false; NovaContext.StartJobs(); Guard.StartOk(); Draw("Error log"); }, true));
            sr.Controls.Add(Th.B("✉ Email index only", () => { MailIndex.Start(); IndexProgress.Start(); }));
            sr.Controls.Add(Th.B("📁 Project folders only", () => Projects.Indexer.Start()));
            sr.Controls.Add(Th.B("📖 Reading files only", () => Projects.ReadAll.Start()));
            sr.Controls.Add(Th.B("📁 Email projects only", () => MailProjects.Start()));
        }
        page.Add(Th.Check("🛡 Guardian — restart Nova by itself if it closes unexpectedly or freezes (at most 3 times in 10 minutes)", Guard.On, v => { Guard.On = v; if (v) Guard.StartGuardian(); }));
        Note("Each background job (email index, project folders, reading files, email projects, meaning search) is protected: if one fails, only that job stops and restarts a minute later. Calls to Outlook have a time limit, so a busy Outlook can't freeze Nova. After two starts that don't finish, Nova opens in safe mode. Every crash or restart is written here and in 🧪 Activity history.", Th.Muted);
        if (Guard.JobsLine() is { Length: > 0 } jl) Note(jl, Th.Warn); else Note("✓ No background job has failed since Nova started.", Th.Good);
        var entries = Log.ReadAll();
        var today = entries.Where(e => e.Time.Date == DateTime.Today).ToList();
        var stats = page.Add(Th.Row(false));
        Label Stat(string big, string small, Color c)
        {
            var l = Th.L(big + "\n" + small, 9.5f, c, true);
            l.BackColor = Th.Card; l.Padding = new Padding(12, 6, 12, 6); l.Margin = new Padding(0, 0, 8, 0);
            return l;
        }
        stats.Controls.Add(Stat(today.Count(e => e.Level == "error").ToString(), "errors today", Th.Bad));
        stats.Controls.Add(Stat(entries.Count(e => e.Level == "error" && e.Time > DateTime.Now.AddDays(-7)).ToString(), "last 7 days", Th.Warn));
        var env = Log.Environment().Split('\n');
        stats.Controls.Add(Stat(Program.Shown, env.Length > 1 ? env[1].Trim() : "", Th.Text));

        page.Add(Th.Section("Recording"));
        page.Add(Th.Check("Record every error with full technical details (what failed, where, the step you were doing, the technical trace)", Log.Detailed, v => Log.Detailed = v));
        page.Add(Th.Check("Also record warnings (slow or skipped files, OCR problems, AI retries)", Log.Warnings, v => Log.Warnings = v));
        page.Add(Th.Check("Include a short text sample of the file or email that failed (first 100 characters) — off keeps your content out", Log.Samples, v => Log.Samples = v));
        var keep = page.Add(Th.Row(false));
        keep.Controls.Add(Th.L("Keep errors for", 10, Th.Text));
        var days = new NumericUpDown { Minimum = 1, Maximum = 365, Value = Log.KeepDays, Width = 60, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        days.ValueChanged += (_, _) => Log.KeepDays = (int)days.Value;
        keep.Controls.Add(days);
        keep.Controls.Add(Th.L("days", 10, Th.Text));

        page.Add(Th.Section("Errors"));
        var filters = page.Add(Th.Row(false));
        foreach (var (k, l) in new[] { ("today", "Today"), ("7", "7 days"), ("all", "All") })
        {
            var kk = k;
            filters.Controls.Add(Th.Chip(l, logFilter == k, () => { logFilter = kk; Draw("Error log"); }));
        }
        filters.Controls.Add(Th.Chip("⚠ Errors only", logErrorsOnly, () => { logErrorsOnly = !logErrorsOnly; Draw("Error log"); }));
        var search = Th.Box(false); search.Width = 200; search.PlaceholderText = "🔍 filter…"; search.Margin = new Padding(8, 0, 0, 0);
        filters.Controls.Add(search);
        IEnumerable<LogEntry> shown = logFilter switch { "today" => today, "7" => entries.Where(e => e.Time > DateTime.Now.AddDays(-7)), _ => entries };
        if (logErrorsOnly) shown = shown.Where(e => e.Level == "error");
        var list = shown.Take(500).ToList();
        var lv = page.Add(new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Height = 230, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(9.5f) }, true);
        lv.Columns.Add("Time", 120); lv.Columns.Add("Level", 60); lv.Columns.Add("Where", 170); lv.Columns.Add("What happened", 520);
        void Fill(string f)
        {
            lv.BeginUpdate(); lv.Items.Clear();
            foreach (var e in list.Where(e => f.Length == 0 || (e.Message + e.Area + e.Step).Contains(f, StringComparison.OrdinalIgnoreCase)))
                lv.Items.Add(new ListViewItem(new[] { e.Time.ToString(logFilter == "today" ? "HH:mm:ss" : "dd MMM HH:mm"), e.Level.ToUpperInvariant(), e.Area, e.Message }) { Tag = e, ForeColor = e.Level == "error" ? Th.Bad : Th.Warn });
            lv.EndUpdate();
        }
        Fill("");
        search.TextChanged += (_, _) => Fill(search.Text.Trim());
        var det = page.Add(Th.NoSpell(Th.Box(true, 8)), true);
        det.ReadOnly = true; det.Font = new Font("Consolas", 9);
        det.Text = list.Count == 0 ? "No errors — all good." : "Select an error to see its details.";
        lv.SelectedIndexChanged += (_, _) => { if (lv.SelectedItems.Count > 0) det.Text = Log.Format((LogEntry)lv.SelectedItems[0].Tag); };

        page.Add(Th.Section("Share"));
        var st = Th.L("", 9.5f, Th.Good);
        var row = page.Add(Th.Row());
        row.Controls.Add(Th.B("📦 Create error report", () =>
        {
            try { var f = Log.CreateReport(); st.Text = "✓ " + Path.GetFileName(f) + " — send it to me in the chat"; Shell.Select(f); }
            catch (Exception e) { st.Text = "⚠ " + e.Message; st.ForeColor = Th.Bad; }
        }, true));
        row.Controls.Add(Th.B("⧉ Copy last error", () =>
        {
            var e = entries.FirstOrDefault(x => x.Level == "error") ?? entries.FirstOrDefault();
            if (e != null) { Clipboard.SetText(Log.Format(e) + "\r\n\r\n" + Log.Environment()); st.Text = "⧉ Copied"; }
        }));
        row.Controls.Add(Th.B("✉ Email the report", () =>
        {
            try { var f = Log.CreateReport(); st.Text = Log.EmailReport(f) ? "✉ A new Outlook email is open with the report attached" : "Outlook could not open — the report is in the Logs folder"; if (!st.Text.StartsWith("✉")) Shell.Select(f); }
            catch (Exception e) { st.Text = "⚠ " + e.Message; }
        }));
        row.Controls.Add(Th.B("📁 Logs folder", () => Shell.Open(Paths.Logs)));
        row.Controls.Add(Th.B("Clear", () => { if (MessageBox.Show("Delete all recorded errors?", "Nova Enhance", MessageBoxButtons.YesNo) == DialogResult.Yes) { Log.Clear(); Draw("Error log"); } }));
        page.Add(st);
        Note("The report is one .zip in Documents\\Nova Enhance\\Logs: errors with details, Nova build, Windows / Office / Outlook versions and your settings — never your API keys, passwords, emails or file contents.");
    }

    // ------------------------------------------------------------------ Office tab
    void OfficeTab()
    {
        // ✦ The Nova tab in EVERY desktop Office program (like Classic Menu) — recommended.
        page.Add(Th.Section("✦ Nova tab in all Office programs (recommended)"));
        Note("A real ✦ Nova tab in Word, Excel, PowerPoint, Outlook, Visio, Project, Access and Publisher: Fix grammar · Shorten · Professional · Informal · Expand · Translate the selected text, the Nova window, Search email, Ask Nova, Morning brief, Find a file — and in Outlook “Talk about this email”. For this Windows user only: no admin rights, no certificate, 32- and 64-bit Office.");
        var rst = Th.L("", 9.5f, Th.Good);
        Note(OfficeRibbon.Installed ? "✓ Installed — the ✦ Nova tab appears when an Office program starts (close and reopen the ones already open)." :
             OfficeRibbon.FilesThere ? "Not installed yet." : "⚠ The Nova tab files are missing — reinstall Nova Enhance.", OfficeRibbon.Installed ? Th.Good : Th.Warn);
        var rr = page.Add(Th.Row());
        rr.Controls.Add(Th.B(OfficeRibbon.Installed ? "↻ Install again" : "✦ Install the Nova tab", () =>
        {
            try { OfficeRibbon.Install(); rst.Text = "✓ Installed — close and reopen Word, Excel, PowerPoint, Outlook… to see the ✦ Nova tab."; rst.ForeColor = Th.Good; }
            catch (Exception e) { rst.Text = "⚠ " + e.Message; rst.ForeColor = Th.Bad; Log.Error("Office tab · install", e); }
        }, true));
        if (OfficeRibbon.Installed) rr.Controls.Add(Th.B("Remove the Nova tab", () => { OfficeRibbon.Uninstall(); Draw("Office tab"); }));
        page.Add(rst);
        Note("If the tab doesn't show: File → Options → Add-ins → Manage: COM Add-ins → Go… → tick “✦ Nova Enhance”. A company policy may block add-ins — the side panel below still works.", Th.Muted);
        page.Add(Th.Section("Side panel (Word, Excel, PowerPoint and Outlook)"));
        Note("A ✦ Nova Enhance button inside Word, Excel, PowerPoint and Outlook: fix grammar, shorten, rewrite, translate, keep the original — and in Outlook summarise and draft replies. The panel runs from Nova on this PC, so keep Nova running (it starts with Windows).");
        page.Add(Th.Section("Status"));
        var reg = OfficeAddin.RegisteredForOffice();
        var trusted = OfficeAddin.Trusted();
        Note((reg ? "✓ Installed for Word, Excel and PowerPoint" : "Not installed for Word, Excel and PowerPoint") +
             " · " + (trusted ? "✓ panel trusted on this PC" : "panel not trusted yet") +
             " · " + (Server.Running ? "✓ panel running" : "panel not running") + (Server.LastError.Length > 0 ? " (" + Server.LastError + ")" : ""), reg && trusted && Server.Running ? Th.Good : Th.Warn);
        Note("Office: " + Log.OfficeVersion());
        var st = Th.L("", 9.5f, Th.Good);

        page.Add(Th.Section("A · Install it myself (this PC)"));
        Note("1 · Click Install for me. Windows asks once \"Do you want to install this certificate?\" → Yes (it lets Office show Nova's panel; it only works on this PC).", Th.Text);
        Note("2 · Restart Word / Excel / PowerPoint → Home tab → ✦ Nova Enhance. (If you don't see it: Insert → My Add-ins → Developer Add-ins.)", Th.Text);
        Note("3 · Outlook: Get Add-ins (Home tab) → My add-ins → Custom add-ins → + Add a custom add-in → Add from file → choose NovaEnhance-Outlook.xml.", Th.Text);
        var ra = page.Add(Th.Row());
        ra.Controls.Add(Th.B("Install for me", () =>
        {
            try { OfficeAddin.InstallForMe(); st.Text = "✓ Installed — restart Word, Excel and PowerPoint. For Outlook, follow step 3."; st.ForeColor = Th.Good; }
            catch (Exception e) { st.Text = "⚠ " + e.Message; st.ForeColor = Th.Bad; Log.Error("Office add-in · install", e); }
            Draw("Office tab");
        }, true));
        ra.Controls.Add(Th.B("📁 Add-in files", () => { OfficeAddin.WriteManifests(); Shell.Select(OfficeAddin.OutlookManifest); }));
        ra.Controls.Add(Th.B("Open Outlook add-ins page", () => Shell.Open("https://aka.ms/olksideload")));
        ra.Controls.Add(Th.B("Uninstall", () =>
        {
            if (MessageBox.Show("Remove the Nova tab from Word, Excel and PowerPoint, and stop trusting the panel? (For Outlook: Get Add-ins → My add-ins → Nova Enhance → Remove.)", "Nova Enhance", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            OfficeAddin.Uninstall(); Draw("Office tab");
        }));
        page.Add(st);

        page.Add(Th.Section("Excel · many cells at once"));
        Note("Select cells (or one empty cell for the whole sheet) and press any button in the Nova panel: each text cell is changed on its own. Empty cells, numbers, dates, formulas and codes like \"A-201 Rev C\" are skipped. ↩ Put back original restores every changed cell.");
        var rv = page.Add(Th.Row());
        bool review = Store.Bool("win_xl_review", true);
        rv.Controls.Add(Th.Radio("Show the list of changes first (Cell · Before → After), then Apply", review, () => Store.SetBool("win_xl_review", true)));
        rv.Controls.Add(Th.Radio("Apply straight away and mark changed cells in light yellow", !review, () => Store.SetBool("win_xl_review", false)));

        page.Add(Th.Section("B · Through company IT (Microsoft 365)"));
        Note("Nova saves the two add-in files and a one-page note for IT. IT uploads them once in the Microsoft 365 admin center → Integrated apps; the Nova tab then appears by itself in Outlook, Word, Excel and PowerPoint. Each PC still needs Nova Enhance installed and the panel trusted (button below).", Th.Text);
        var rb = page.Add(Th.Row());
        rb.Controls.Add(Th.B("Prepare for IT", () =>
        {
            try { var d = OfficeAddin.PrepareForIt(); Store.SetBool("win_addin_it", true); Shell.Open(d); st.Text = "✓ Files and note saved in Documents\\Nova Enhance\\Office Add-in\\For IT"; }
            catch (Exception e) { st.Text = "⚠ " + e.Message; }
        }, true));
        rb.Controls.Add(Th.B("Trust the panel on this PC", () =>
        {
            try { OfficeAddin.Trust(); Server.Start(); Store.SetBool("win_addin_it", true); st.Text = "✓ Panel trusted and running"; }
            catch (Exception e) { st.Text = "⚠ " + e.Message; }
            Draw("Office tab");
        }));
    }

    void About()
    {
        page.Add(Th.L("✦ Nova Enhance for Windows", 13, Th.Text, true));
        Note($"Version {Program.Shown}", Th.Text);
        Note("Enhance text anywhere (Ctrl+Shift+E), 🔍 Search for email (Ctrl+Shift+F), 💬 Talk about this email (Ctrl+Alt+Q), morning brief and follow-ups. Works with the Nova Enhance phone app.");
        Note("All your files: " + Paths.Root, Th.Text);
        page.Add(Th.Check("🧪 Advanced tools — show 🧪 Activity history and 🔌 Test connector in this list", Store.Bool("win_advanced", false), v =>
        {
            Store.SetBool("win_advanced", v);
            Sections = Visible();
            nav.Items.Clear(); nav.Items.AddRange(Sections);
            nav.SelectedIndex = Array.IndexOf(Sections, "About");
        }));
        page.Add(Th.L(Program.Sign, 11, Th.AccentText, true));
    }
}
