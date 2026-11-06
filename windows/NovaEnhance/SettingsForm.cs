namespace NovaEnhance;

/// <summary>Settings, split into sections like the phone app.</summary>
class SettingsForm : NovaForm
{
    static readonly string[] Sections = { "AI & API keys", "About me", "Enhancement", "Email & AI", "Folder search", "Google Drive", "Office tab", "Browser add-on", "AI window", "Shortcuts", "Backup & restore", "Updates", "Error log", "About" };
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
        nav.Items.AddRange(Sections);
        nav.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var sel = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(sel ? Th.Card2 : nav.BackColor);
            e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, Sections[e.Index], Th.F(10.5f, sel), new Rectangle(e.Bounds.X + 16, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height),
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
        nav.SelectedIndex = Ai.WithKeys.Length == 0 ? 0 : 3;
    }

    public void Go(string section)
    {
        var i = Array.IndexOf(Sections, section);
        if (i >= 0) nav.SelectedIndex = i;
    }

    void Draw(string section)
    {
        host.SuspendLayout();
        page?.Dispose();
        page = new Stack { Padding = new Padding(24, 14, 24, 12) };
        host.Controls.Add(page);
        page.Add(Th.L(section, 15, Th.Text, true));
        liveIndex = null; liveUpdate = null;
        try
        {
            switch (section)
            {
                case "AI & API keys": AiKeys(); break;
                case "About me": AboutMe(); break;
                case "Enhancement": Enhancement(); break;
                case "Email & AI": EmailAi(); break;
                case "Folder search": FolderSearch(); break;
                case "Google Drive": GoogleDrive(); break;
                case "Shortcuts": Shortcuts(); break;
                case "Browser add-on": BrowserPage(); break;
                case "AI window": AiWindowPage(); break;
                case "Backup & restore": BackupPage(); break;
                case "Updates": Updates(); break;
                case "Error log": ErrorLog(); break;
                case "Office tab": OfficeTab(); break;
                default: About(); break;
            }
        }
        catch (Exception e) { page.Add(Th.Wrap("⚠ " + e.Message, 10, Th.Bad)); Log.Error("settings " + section, e); }
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
        foreach (var p in Ai.All)
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
            var model = Th.Box(false); model.Text = Store.Model(p.Id); model.Dock = DockStyle.Fill;
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

    // ------------------------------------------------------------------ About me
    void AboutMe()
    {
        Note("Your field and role. Every enhancement and draft uses the correct technical terms of your field (same profile as the phone).");
        page.Add(Th.Section("My field · pick one or more"));
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
        about.PlaceholderText = "e.g. Senior site architect: Design Development and coordination with structural and MEP…";
        page.Add(Th.Section("My terms · kept exactly as written"));
        var terms = page.Add(Th.Box(true, 2), true);
        terms.Text = Store.ProfileTerms;
        terms.PlaceholderText = "e.g. IFC, RFI, NY, PQP, PVRS, SLV";
        var save = page.Add(Th.Row(false));
        var st = Th.L("", 9.5f, Th.Good);
        save.Controls.Add(Th.B("Save", () =>
        {
            if (otherBox.Text.Trim().Length > 0) chosen.Add(otherBox.Text.Trim().Replace("|", " "));
            Store.ProfileFields = string.Join("|", chosen);
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
        page.Add(Th.Row(false)).Controls.Add(Th.B("☀ Open the brief now", Screens.Brief));

        page.Add(Th.Section("Email index"));
        liveIndex = Note(MailIndex.Status, Th.Text);
        page.Add(Th.Check("Index attachments (PDF, Word, text) so Ask Nova finds words inside them", Store.IndexAttachments, v => Store.IndexAttachments = v));
        page.Add(Th.Check("Keep the index on this PC only (not in Documents / OneDrive)", Store.IndexLocal, v => MailIndex.Move(v)));
        Note("The index holds copies of your email text, encrypted and locked to your Windows account.");
        var ir = page.Add(Th.Row(false));
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

        page.Add(Th.Section("Mailboxes and folders"));
        Note("Everything is indexed: your mailbox, shared mailboxes, Online Archive, Groups and .pst archives (public folders and Search Folders are skipped). Untick a folder to leave it out of the index and of the answers.");
        var folders = MailIndex.Folders();
        if (folders.Count == 0) Note("The list fills in while Nova reads Outlook — open this page again in a few minutes.", Th.Muted);
        var left = MailIndex.LeftOut();
        foreach (var g in folders.GroupBy(f => f.StoreName))
        {
            var t = Th.L(g.Key.Length > 0 ? g.Key : "Mailbox", 10, Th.AccentText, true);
            t.Margin = new Padding(0, 8, 0, 2);
            page.Add(t);
            foreach (var f in g)
            {
                var fid = f.Fid;
                var cb = Th.Check($"{f.Path}    {f.Indexed:N0} / {f.Total:N0}", !left.Contains(fid), v => { MailIndex.SetLeftOut(fid, !v); });
                cb.Margin = new Padding(16, 1, 0, 1);
                page.Add(cb);
            }
        }
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
        var id = Th.Box(false); id.Text = GDrive.ClientId; id.Dock = DockStyle.Fill; id.PlaceholderText = "…apps.googleusercontent.com";
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
        Note("In the browser (with the Nova add-on): Alt + G fix grammar · Alt + T translate · right-click selected text → ✦ Nova Enhance.", Th.Text);
        Note("Right-click a folder → Search inside with Nova — find codes, references and sentences inside its files.", Th.Text);
        Note("Left-click the ✦ icon near the clock = Ask Nova · right-click = menu.", Th.Text);
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
    static string UpdateText() => $"This is build {Program.Build}. " + (Updater.Status.Length > 0 ? Updater.Status : "Checked once a day from GitHub (signature and SHA-256 verified before installing).");

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

    void ErrorLog()
    {
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
        stats.Controls.Add(Stat("build " + Program.Build, env.Length > 1 ? env[1].Trim() : "", Th.Text));

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
        var det = page.Add(Th.Box(true, 8), true);
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
        Note($"Version 1.0 · build {Program.Build}", Th.Text);
        Note("Enhance text anywhere (Ctrl+Shift+E), Ask Nova about your email (Ctrl+Shift+F), morning brief and follow-ups. Works with the Nova Enhance phone app.");
        Note("All your files: " + Paths.Root, Th.Text);
        page.Add(Th.L(Program.Sign, 11, Th.AccentText, true));
    }
}
