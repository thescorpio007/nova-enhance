using System.Collections.Specialized;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>What a file name says: drawing number, title, document type, discipline, level (from the codes in the name).</summary>
class FileMeta
{
    public string Number = "", Title = "", Doc = "", Disc = "", Level = "", Type = "";

    static readonly Dictionary<string, string> Docs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DRW"] = "Drawing", ["DWG"] = "Drawing", ["SKT"] = "Sketch", ["SK"] = "Sketch", ["LTR"] = "Letter", ["LET"] = "Letter", ["RFI"] = "RFI",
        ["SDS"] = "Shop drawing submittal", ["SHD"] = "Shop drawing submittal", ["SSM"] = "Material submittal", ["MAT"] = "Material submittal", ["MS"] = "Material submittal", ["MSS"] = "Material submittal",
        ["MOM"] = "Minutes", ["MIN"] = "Minutes", ["TRN"] = "Transmittal", ["TRS"] = "Transmittal", ["TR"] = "Transmittal", ["SPC"] = "Specification", ["SPE"] = "Specification", ["SPEC"] = "Specification",
        ["RPT"] = "Report", ["REP"] = "Report", ["REG"] = "Register", ["CAL"] = "Calculation", ["CALC"] = "Calculation", ["NCR"] = "NCR", ["SI"] = "Site instruction", ["SIN"] = "Site instruction",
        ["VO"] = "Variation", ["VAR"] = "Variation", ["IR"] = "Inspection request", ["WIR"] = "Inspection request", ["MIR"] = "Inspection request", ["MS-"] = "Method statement", ["MST"] = "Method statement",
        ["SCH"] = "Schedule", ["BOQ"] = "BOQ", ["MEM"] = "Memo", ["PRS"] = "Presentation", ["CVI"] = "Confirmation of verbal instruction", ["EML"] = "Email"
    };
    static readonly Dictionary<string, string> Discs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AR"] = "Architecture", ["ARC"] = "Architecture", ["ST"] = "Structure", ["SE"] = "Structure", ["STR"] = "Structure", ["ME"] = "MEP", ["MEP"] = "MEP", ["MC"] = "Mechanical", ["MV"] = "Mechanical", ["HV"] = "Mechanical",
        ["EL"] = "Electrical", ["EE"] = "Electrical", ["PL"] = "Plumbing", ["PH"] = "Plumbing", ["DR"] = "Plumbing", ["FF"] = "Fire", ["FP"] = "Fire", ["FA"] = "Fire", ["FLS"] = "Fire",
        ["FC"] = "Façade", ["FA-"] = "Façade", ["CW"] = "Façade", ["LA"] = "Landscape", ["LS"] = "Landscape", ["ID"] = "Interior", ["IN"] = "Interior", ["CV"] = "Civil", ["CI"] = "Civil", ["IF"] = "Infrastructure",
        ["PM"] = "Project management", ["GN"] = "General", ["GE"] = "General", ["ICT"] = "ICT", ["IT"] = "ICT", ["EV"] = "Vertical transport", ["LF"] = "Vertical transport", ["BMU"] = "Façade access"
    };

    static string LevelOf(string s)
    {
        s = s.ToUpperInvariant();
        var m = Regex.Match(s, @"^L(\d{1,2})$"); if (m.Success) return "Level " + int.Parse(m.Groups[1].Value).ToString("00");
        m = Regex.Match(s, @"^B(\d)$"); if (m.Success) return "Basement " + m.Groups[1].Value;
        return s switch { "PD" or "POD" or "PO" => "Podium", "RF" or "RL" or "ROF" => "Roof", "GF" or "GL" or "L00" or "00" => "Ground", "MZ" or "MEZ" => "Mezzanine", "LG" => "Lower ground", "UG" => "Upper ground", "ZZ" or "XX" => "All levels", _ => "" };
    }

    public static FileMeta Of(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path ?? "");
        var m = new FileMeta { Type = FileType.Of(path), Number = Revisions.Number(name) };
        if (m.Number.Length > 0)
        {
            foreach (var seg in m.Number.Split('-').Skip(1))
            {
                if (m.Doc.Length == 0 && Docs.TryGetValue(seg, out var d)) m.Doc = d;
                else if (m.Disc.Length == 0 && Discs.TryGetValue(seg, out var di)) m.Disc = di;
                else if (m.Level.Length == 0 && LevelOf(seg) is { Length: > 0 } lv) m.Level = lv;
            }
            var at = name.IndexOf(m.Number, StringComparison.OrdinalIgnoreCase);
            var rest = at >= 0 ? name[(at + m.Number.Length)..] : "";
            rest = Regex.Replace(rest, @"^[\s\-_]*((?:[CPTDR]\d{1,3}|IF[CTARP]|REV[\s\-_]?[A-Z0-9]+|R\d{1,3}[A-Z]?)(?=[\s\-_(]|$))?", "", RegexOptions.IgnoreCase);
            rest = Regex.Replace(rest, @"\b(PDF|CAD|DWG)\(\d+\)", "", RegexOptions.IgnoreCase);
            m.Title = Regex.Replace(rest.Replace('_', ' '), @"\s{2,}", " ").Trim(' ', '-', '.');
        }
        else m.Title = name;
        if (m.Doc.Length == 0)
        {
            var low = name.ToLowerInvariant();
            m.Doc = low.Contains("minutes") || low.Contains(" mom") ? "Minutes" : low.Contains("letter") ? "Letter" : low.Contains("transmittal") ? "Transmittal"
                  : low.Contains("specification") ? "Specification" : low.Contains("report") ? "Report" : low.Contains("register") ? "Register" : low.Contains("rfi") ? "RFI"
                  : low.Contains("boq") || low.Contains("bill of quantit") ? "BOQ" : low.Contains("schedule") ? "Schedule" : m.Type == "CAD / BIM" ? "Model / CAD" : "";
        }
        return m;
    }
}

/// <summary>The filters of the finder (from the bar or from your words).</summary>
class FinderFilter
{
    public HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase), Docs = new(StringComparer.OrdinalIgnoreCase), Discs = new(StringComparer.OrdinalIgnoreCase), Levels = new(StringComparer.OrdinalIgnoreCase);
    public string Rev = "", Status = "latest", GroupBy = "", Sort = "";
    public bool SortDesc;
    public DateTime? From, To;
    public List<string> FolderHas = new(), Exclude = new();
    public bool Backups;

    public bool Any => Types.Count + Docs.Count + Discs.Count + Levels.Count + FolderHas.Count + Exclude.Count > 0 || Rev.Length > 0 || From != null || To != null;

    static readonly (string Rx, string Val)[] TypeWords =
    {
        (@"\bpdfs?\b", "PDF"), (@"\b(word|docx?)\b", "Word"), (@"\b(excel|xlsx?|spreadsheets?|sheets?)\b", "Excel"), (@"\b(powerpoint|pptx?|presentations?)\b", "PowerPoint"),
        (@"\b(dwg|cad|autocad|revit|rvt|models?|bim)\b", "CAD / BIM"), (@"\b(images?|photos?|pictures?|jpe?g|png)\b", "Image"), (@"\b(emails?|msg)\b", "Email")
    };
    static readonly (string Rx, string Val)[] DocWords =
    {
        (@"\bshop drawings?\b", "Shop drawing submittal"), (@"\bmaterial submittals?\b", "Material submittal"), (@"\b(drawings?|plans?|elevations?|sections?|details?|layouts?)\b", "Drawing"),
        (@"\bletters?\b", "Letter"), (@"\brfis?\b", "RFI"), (@"\b(minutes|mom)\b", "Minutes"), (@"\btransmittals?\b", "Transmittal"), (@"\bspec(ification)?s?\b", "Specification"),
        (@"\breports?\b", "Report"), (@"\bregisters?\b", "Register"), (@"\bcalc(ulation)?s?\b", "Calculation"), (@"\bncrs?\b", "NCR"), (@"\bsite instructions?\b", "Site instruction"),
        (@"\b(inspection requests?|wirs?|mirs?)\b", "Inspection request"), (@"\bvariations?\b", "Variation"), (@"\bboqs?\b|bill of quantit", "BOQ"), (@"\bschedules?\b", "Schedule"), (@"\bsketch(es)?\b", "Sketch")
    };
    static readonly (string Rx, string Val)[] DiscWords =
    {
        (@"\barchitectur(e|al)\b|\barch\b", "Architecture"), (@"\bstructur(e|al)\b", "Structure"), (@"\bmep\b", "MEP"), (@"\b(mechanical|hvac)\b", "Mechanical"), (@"\belectrical\b", "Electrical"),
        (@"\b(plumbing|drainage)\b", "Plumbing"), (@"\b(fire|fls|fire fighting|fire alarm)\b", "Fire"), (@"\b(fa[cç]ade|curtain wall|cladding|glazing)\b", "Façade"), (@"\blandscap(e|ing)\b", "Landscape"),
        (@"\binterior\b", "Interior"), (@"\b(civil|infrastructure)\b", "Civil"), (@"\b(ict|telecom)\b", "ICT"), (@"\b(lifts?|elevators?|escalators?)\b", "Vertical transport"), (@"\bbmu\b|fa[cç]ade access", "Façade access")
    };

    /// <summary>Your words → filters (and the words left to search in the names). Read on this PC, nothing is sent.</summary>
    public static (FinderFilter F, string Words) Parse(string text, bool backups)
    {
        var f = new FinderFilter { Backups = backups, Status = "" };
        var t = " " + (text ?? "").ToLowerInvariant().Replace("’", "'") + " ";
        string Take(string rx, Action<Match> a)
        {
            foreach (Match m in Regex.Matches(t, rx)) a(m);
            return Regex.Replace(t, rx, " ");
        }
        // Exclusions first: "no submittals", "without CAD", "except superseded".
        t = Take(@"\b(?:no|not|without|exclude|excluding|except|but not)\s+([\p{L}\p{N}_\-]+)", m =>
        {
            var w = m.Groups[1].Value.Trim();
            var ty = TypeWords.FirstOrDefault(x => Regex.IsMatch(w, x.Rx)).Val;
            if (ty != null) { foreach (var x in TypeWords.Select(x => x.Val).Where(v => v != ty)) f.Types.Add(x); return; }
            if (Regex.IsMatch(w, @"^(superseded|old)")) { f.Status = "latest"; return; }
            f.Exclude.Add(Regex.Replace(w, @"(s|es)\b$", ""));
        });
        t = Take(@"\bsort(?:ed)?\s+by\s+(drawing number|number|date|newest|latest|oldest|size|biggest|title|name|level|discipline|type)|\b(newest|latest) first\b|\boldest first\b", m =>
        {
            var w = m.Value;
            if (w.Contains("number")) { f.Sort = "number"; f.SortDesc = false; }
            else if (w.Contains("oldest")) { f.Sort = "date"; f.SortDesc = false; }
            else if (w.Contains("date") || w.Contains("newest") || w.Contains("latest")) { f.Sort = "date"; f.SortDesc = true; }
            else if (w.Contains("size") || w.Contains("biggest")) { f.Sort = "size"; f.SortDesc = true; }
            else if (w.Contains("title") || w.Contains("name")) { f.Sort = "title"; f.SortDesc = false; }
            else if (w.Contains("level")) f.Sort = "level"; else if (w.Contains("discipline")) f.Sort = "discipline"; else if (w.Contains("type")) f.Sort = "type";
        });
        t = Take(@"\bgroup(?:ed)?\s+by\s+(level|discipline|document|type|project|folder)\b", m => f.GroupBy = m.Groups[1].Value);
        t = Take(@"\b(?:rev(?:ision)?)\s+([a-z]?\d{1,3}|if[ctarp])\b", m => f.Rev = m.Groups[1].Value.ToUpperInvariant());
        t = Take(@"\b(all revisions|every revision|old revisions|superseded( only)?|older revisions)\b", m => f.Status = m.Value.Contains("superseded") || m.Value.Contains("old") ? "superseded" : "any");
        t = Take(@"\b(latest|current|last revision|newest revision)\b", _ => f.Status = "latest");
        t = Take(@"\b(since|after|from)\s+(\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}|[a-z]+\s+\d{4}|\d{4})\b", m => f.From = Date(m.Groups[2].Value, false));
        t = Take(@"\b(before|until|till|up to)\s+(\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}|[a-z]+\s+\d{4}|\d{4})\b", m => f.To = Date(m.Groups[2].Value, true));
        t = Take(@"\b(this year)\b", _ => f.From = new DateTime(DateTime.Today.Year, 1, 1));
        t = Take(@"\b(this month)\b", _ => f.From = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
        t = Take(@"\b(ifc|ift|for construction|for tender|tender|for approval|as[\s\-]?built)\b", m => f.FolderHas.Add(m.Value.Trim() switch { "for construction" => "IFC", "for tender" or "tender" => "tender", "as built" or "as-built" or "asbuilt" => "built", var x => x.ToUpperInvariant() }));
        foreach (var (rx, v) in DocWords) t = Take(rx, _ => f.Docs.Add(v));
        foreach (var (rx, v) in DiscWords) t = Take(rx, _ => f.Discs.Add(v));
        foreach (var (rx, v) in TypeWords) t = Take(rx, _ => f.Types.Add(v));
        t = Take(@"\blevels?\s+(\d{1,2})\b|\bl(\d{2})\b", m => f.Levels.Add("Level " + int.Parse(m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value).ToString("00")));
        t = Take(@"\bpodium\b", _ => f.Levels.Add("Podium"));
        t = Take(@"\bbasement\s*(\d)\b", m => f.Levels.Add("Basement " + m.Groups[1].Value));
        t = Take(@"\broof\b", _ => f.Levels.Add("Roof"));
        t = Take(@"\bground( floor)?\b", _ => f.Levels.Add("Ground"));
        t = Take(@"\bmezzanine\b", _ => f.Levels.Add("Mezzanine"));
        var words = Regex.Split(t, @"[\s,;]+").Where(w => w.Length > 1 && !Regex.IsMatch(w, "^(and|or|the|for|of|only|all|with|in|on|at|to|a|an|me|show|find|give|list|files?|documents?|please|which|that|are|is|by|from|our|my)$"));
        if (f.Status.Length == 0) f.Status = "latest";
        return (f, string.Join(" ", words));
    }

    static DateTime? Date(string s, bool end)
    {
        s = s.Trim();
        if (Regex.IsMatch(s, @"^\d{4}$")) return end ? new DateTime(int.Parse(s), 12, 31) : new DateTime(int.Parse(s), 1, 1);
        foreach (var fmt in new[] { "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "d/M/yy", "MMMM yyyy", "MMM yyyy" })
            if (DateTime.TryParseExact(s, fmt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var d))
                return fmt.StartsWith("MMM") && end ? d.AddMonths(1).AddDays(-1) : d;
        return null;
    }
}

/// <summary>
/// 📂 Find a project file: type a drawing number or a few words — or your requirements in plain words — and every file NAME
/// of your projects is searched in a second. Columns for drawing number, title, document type, discipline, level, revision;
/// a filter bar Nova fills from your words (chips you can remove); group by; export as a drawing register; attach all to an
/// email; ask Nova about the files. Designed by A.HAMROUNI
/// </summary>
class FileFinderForm : NovaForm
{
    readonly TextBox box, req;
    readonly ComboBox proj, cType, cDoc, cDisc, cLevel, cStatus, cGroup;
    readonly TextBox tRev, tFolder, tExclude;
    readonly DateTimePicker dFrom, dTo;
    readonly CheckBox backups;
    readonly FlowLayoutPanel chips;
    readonly ListView lv;
    readonly Label info;
    readonly System.Windows.Forms.Timer debounce;
    FinderFilter flt = new();
    List<Row> rows = new();
    int run;
    bool quiet;

    class Row { public Projects.FileRow F; public FileMeta M; }

    public FileFinderForm()
    {
        Text = "Nova Enhance · 📂 Find a project file";
        Width = 1500; Height = 860;
        MinimumSize = new Size(900, 520);
        KeyPreview = true;

        ComboBox Combo(int w)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = w, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(9.5f), FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 4, 8, 2) };
            return c;
        }
        Label Lbl(string s) { var l = Th.L(s, 9, Th.Muted); l.Margin = new Padding(4, 8, 0, 0); return l; }
        TextBox Tb(int w) { var t = Th.Box(false); if (t is SpellBox sb) sb.Off = true; t.Width = w; t.Font = Th.F(9.5f); t.Margin = new Padding(2, 4, 8, 2); return t; }

        // Row 1 — search, project, latest, backups.
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 2), BackColor = Th.Bg, WrapContents = true };
        box = Th.Box(false);
        if (box is SpellBox sb0) sb0.Off = true;
        box.Width = 480; box.Font = Th.F(12);
        box.PlaceholderText = "Drawing number or words — e.g.  SLV-MZA-AR-DD-1203   ·   door schedule   ·   fire strategy";
        proj = Combo(230); proj.Font = Th.F(10);
        proj.Items.Add("All projects");
        foreach (var p in Projects.All()) proj.Items.Add($"{p.Code} · {p.Name}");
        proj.Items.Add("General reference");
        proj.SelectedIndex = 0;
        backups = new CheckBox { Text = "Show backups (.bak, ~$, temp)", AutoSize = true, ForeColor = Th.Muted, Font = Th.F(9.5f), Margin = new Padding(10, 8, 4, 0) };
        top.Controls.Add(box); top.Controls.Add(proj); top.Controls.Add(backups);

        // Row 2 — ✦ Requirements in your own words.
        var rrow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 2, 10, 2), BackColor = Th.Bg, WrapContents = false };
        rrow.Controls.Add(Lbl("✦ Requirements"));
        req = Tb(780); req.Font = Th.F(10.5f);
        req.PlaceholderText = "e.g.  latest IFC architectural plans for podium and level 02, PDF only, no submittals, sorted by drawing number";
        rrow.Controls.Add(req);
        rrow.Controls.Add(Th.B("✦ Apply", ApplyReq, true));
        rrow.Controls.Add(Th.B("⭐ Save", SaveReq));
        rrow.Controls.Add(Th.B("My filters ▾", () => SavedMenu().Show(Cursor.Position)));

        // Row 3 — what Nova understood (chips).
        chips = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 0, 10, 0), BackColor = Color.FromArgb(0x1B, 0x18, 0x30), WrapContents = true, Visible = false };

        // Row 4 — the filter bar.
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 2, 10, 4), BackColor = Th.Bg, WrapContents = true };
        cType = Combo(110); cDoc = Combo(170); cDisc = Combo(140); cLevel = Combo(120); cStatus = Combo(110); cGroup = Combo(110);
        cStatus.Items.AddRange(new object[] { "Latest", "Any revision", "Superseded" }); cStatus.SelectedIndex = 0;
        cGroup.Items.AddRange(new object[] { "No groups", "Document", "Discipline", "Level", "Type", "Project", "Folder" }); cGroup.SelectedIndex = 0;
        tRev = Tb(55); tFolder = Tb(110); tExclude = Tb(120);
        dFrom = new DateTimePicker { Width = 125, Format = DateTimePickerFormat.Custom, CustomFormat = "dd MMM yyyy", ShowCheckBox = true, Checked = false, Margin = new Padding(2, 4, 2, 2) };
        dTo = new DateTimePicker { Width = 125, Format = DateTimePickerFormat.Custom, CustomFormat = "dd MMM yyyy", ShowCheckBox = true, Checked = false, Margin = new Padding(2, 4, 8, 2) };
        foreach (var (l, c) in new (string, Control)[] { ("Type", cType), ("Document", cDoc), ("Discipline", cDisc), ("Level", cLevel), ("Rev", tRev), ("Status", cStatus), ("Modified", dFrom), ("to", dTo), ("Folder has", tFolder), ("Exclude", tExclude), ("Group by", cGroup) })
        { bar.Controls.Add(Lbl(l)); bar.Controls.Add(c); }
        bar.Controls.Add(Th.B("✗ Clear all", ClearAll));

        lv = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, BackColor = Th.Card, ForeColor = Th.Text,
            BorderStyle = BorderStyle.None, Font = Th.F(9.5f), HideSelection = false, ShowItemToolTips = true
        };
        foreach (var (h, w) in new[] { ("Drawing no. / file", 300), ("Title", 300), ("Rev", 50), ("Status", 90), ("Document", 150), ("Discipline", 110), ("Level", 90), ("Type", 70), ("Project", 70), ("Modified", 100), ("Size", 70), ("Folder", 420) })
            lv.Columns.Add(h, w);
        lv.DoubleClick += (_, _) => OpenSel();
        int sortCol = -1; bool desc = false;
        lv.ColumnClick += (_, e) =>
        {
            desc = sortCol == e.Column ? !desc : e.Column is 9 or 10; sortCol = e.Column;
            lv.ListViewItemSorter = new Cmp(e.Column, desc);
            lv.Sort();
        };
        lv.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenSel(); e.Handled = true; } if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem i in lv.Items) i.Selected = true; e.Handled = true; } };
        lv.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && Sel().Count > 0) Menu().Show(lv, e.Location); };

        // Bottom — count and actions.
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.FromArgb(0x0E, 0x11, 0x16) };
        info = new Label { Dock = DockStyle.Fill, ForeColor = Th.Muted, Font = Th.F(9.5f), Padding = new Padding(10, 12, 10, 0) };
        var acts = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(4, 6, 8, 0) };
        acts.Controls.Add(Th.B("📊 Export as drawing register", ExportRegister, true));
        acts.Controls.Add(Th.B("📎 Attach all to an email", AttachAll));
        acts.Controls.Add(Th.B("✦ Ask Nova about these files", AskNova));
        bottom.Controls.Add(info); bottom.Controls.Add(acts);

        Controls.Add(lv);
        Controls.Add(bar);
        Controls.Add(chips);
        Controls.Add(rrow);
        Controls.Add(top);
        Controls.Add(bottom);

        debounce = new System.Windows.Forms.Timer { Interval = 250 };
        debounce.Tick += (_, _) => { debounce.Stop(); Find(); };
        void Later() { if (!quiet) { debounce.Stop(); debounce.Start(); } }
        box.TextChanged += (_, _) => Later();
        proj.SelectedIndexChanged += (_, _) => Find();
        backups.CheckedChanged += (_, _) => Find();
        foreach (var c in new[] { cType, cDoc, cDisc, cLevel, cStatus, cGroup }) c.SelectedIndexChanged += (_, _) => { if (!quiet) { ReadBar(); Find(); } };
        foreach (var t in new[] { tRev, tFolder, tExclude }) t.TextChanged += (_, _) => { if (!quiet) { ReadBar(); Later(); } };
        dFrom.ValueChanged += (_, _) => { if (!quiet) { ReadBar(); Later(); } };
        dTo.ValueChanged += (_, _) => { if (!quiet) { ReadBar(); Later(); } };
        req.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyReq(); } };
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && lv.Items.Count > 0) { lv.Focus(); lv.Items[0].Selected = true; lv.Items[0].Focused = true; e.Handled = true; }
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; debounce.Stop(); Find(true); }
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Shown += (_, _) => { box.Focus(); Hint(); Facets(new()); };
    }

    void Hint()
    {
        var n = Projects.Status.Values.Sum(s => s.Files);
        info.Text = Projects.All().Count == 0
            ? "No projects yet — Settings → 📁 Projects → 🔍 Scan my work folder."
            : $"{n:N0} file names ready · type words or a number, or your requirements in ✦ · Enter opens · right-click for more · Esc closes";
    }

    /// <summary>Opens with a search already typed (e.g. a drawing number).</summary>
    public void Search(string q) { box.Text = q ?? ""; box.SelectionStart = box.Text.Length; box.Focus(); Find(true); }

    List<string> Folders()
    {
        var all = Projects.All();
        if (proj.SelectedIndex <= 0) return all.SelectMany(p => p.Folders).Concat(Projects.General).ToList();
        if (proj.SelectedIndex == proj.Items.Count - 1) return Projects.General;
        var p = all.ElementAtOrDefault(proj.SelectedIndex - 1);
        return p?.Folders ?? new();
    }

    // ------------------------------------------------------------------ requirements → filters

    void ApplyReq()
    {
        var (f, words) = FinderFilter.Parse(req.Text, backups.Checked);
        // A project named in the words ("SLV …").
        var pj = Projects.ForText(req.Text);
        quiet = true;
        if (pj != null) { var i = Projects.All().FindIndex(x => x.Code == pj.Code); if (i >= 0) proj.SelectedIndex = i + 1; words = Regex.Replace(words, @"\b" + Regex.Escape(pj.Code) + @"\b", "", RegexOptions.IgnoreCase).Trim(); }
        flt = f;
        box.Text = words;
        quiet = false;
        WriteBar();
        Find();
    }

    void ReadBar()
    {
        HashSet<string> Pick(ComboBox c, HashSet<string> cur) =>
            c.SelectedIndex <= 0 ? new(StringComparer.OrdinalIgnoreCase) : c.SelectedItem is string s && s.Contains(", ") && cur.Count > 1 ? cur : new(new[] { (string)c.SelectedItem }, StringComparer.OrdinalIgnoreCase);
        flt.Types = Pick(cType, flt.Types); flt.Docs = Pick(cDoc, flt.Docs); flt.Discs = Pick(cDisc, flt.Discs); flt.Levels = Pick(cLevel, flt.Levels);
        flt.Status = cStatus.SelectedIndex switch { 1 => "any", 2 => "superseded", _ => "latest" };
        flt.GroupBy = cGroup.SelectedIndex <= 0 ? "" : ((string)cGroup.SelectedItem).ToLowerInvariant();
        flt.Rev = tRev.Text.Trim().ToUpperInvariant();
        flt.FolderHas = Split(tFolder.Text); flt.Exclude = Split(tExclude.Text);
        flt.From = dFrom.Checked ? dFrom.Value.Date : null; flt.To = dTo.Checked ? dTo.Value.Date : null;
        Chips();
    }

    static List<string> Split(string s) => (s ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    void WriteBar()
    {
        quiet = true;
        cStatus.SelectedIndex = flt.Status switch { "any" => 1, "superseded" => 2, _ => 0 };
        cGroup.SelectedIndex = Math.Max(0, cGroup.Items.Cast<string>().ToList().FindIndex(x => x.Equals(flt.GroupBy, StringComparison.OrdinalIgnoreCase)));
        tRev.Text = flt.Rev; tFolder.Text = string.Join(", ", flt.FolderHas); tExclude.Text = string.Join(", ", flt.Exclude);
        dFrom.Checked = flt.From != null; if (flt.From is { } a) dFrom.Value = a;
        dTo.Checked = flt.To != null; if (flt.To is { } b) dTo.Value = b;
        quiet = false;
        Chips();
    }

    void Chips()
    {
        chips.SuspendLayout();
        foreach (Control c in chips.Controls) c.Dispose();
        chips.Controls.Clear();
        var l = Th.L("✦ Nova understood (click ✗ to remove):", 9, Th.Text); l.Margin = new Padding(0, 8, 6, 0);
        chips.Controls.Add(l);
        void Chip(string k, string v, Action remove)
        {
            var b = Th.B($"{k}  {v}  ✗", () => { remove(); WriteBar(); FillCombos(); Find(); });
            b.Font = Th.F(9); b.BackColor = Color.FromArgb(0x2E, 0x2A, 0x55); b.Margin = new Padding(2, 3, 2, 3);
            chips.Controls.Add(b);
        }
        foreach (var x in flt.Types.ToList()) Chip("Type", x, () => flt.Types.Remove(x));
        foreach (var x in flt.Docs.ToList()) Chip("Document", x, () => flt.Docs.Remove(x));
        foreach (var x in flt.Discs.ToList()) Chip("Discipline", x, () => flt.Discs.Remove(x));
        foreach (var x in flt.Levels.ToList()) Chip("Level", x, () => flt.Levels.Remove(x));
        if (flt.Rev.Length > 0) Chip("Rev", flt.Rev, () => flt.Rev = "");
        if (flt.Status != "latest") Chip("Status", flt.Status == "any" ? "all revisions" : "superseded", () => flt.Status = "latest");
        foreach (var x in flt.FolderHas.ToList()) Chip("Folder has", x, () => flt.FolderHas.Remove(x));
        foreach (var x in flt.Exclude.ToList()) Chip("Exclude", x, () => flt.Exclude.Remove(x));
        if (flt.From is { } a) Chip("From", a.ToString("dd MMM yyyy"), () => flt.From = null);
        if (flt.To is { } b) Chip("To", b.ToString("dd MMM yyyy"), () => flt.To = null);
        if (flt.Sort.Length > 0) Chip("Sort", flt.Sort + (flt.SortDesc ? " ↓" : " ↑"), () => flt.Sort = "");
        if (flt.GroupBy.Length > 0) Chip("Group by", flt.GroupBy, () => flt.GroupBy = "");
        chips.Visible = chips.Controls.Count > 1;
        chips.ResumeLayout();
    }

    void ClearAll()
    {
        flt = new FinderFilter { Backups = backups.Checked };
        req.Text = "";
        WriteBar(); FillCombos(); Find();
    }

    // ------------------------------------------------------------------ search

    static readonly Regex Junk = new(@"(^~\$)|\.(bak|tmp|dwl2?|sv\$|ac\$|lck|old)$|^thumbs\.db$|^desktop\.ini$|\(recovered\)", RegexOptions.IgnoreCase);

    async void Find(bool openIfOne = false)
    {
        var q = box.Text.Trim();
        var my = ++run;
        if (q.Length < 2 && !flt.Any) { lv.Items.Clear(); rows.Clear(); Hint(); Facets(new()); return; }
        var folders = Folders();
        var f = flt; var showJunk = backups.Checked;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        info.Text = "Searching…";
        var (all, shown) = await Task.Run(() =>
        {
            var found = Projects.FindFiles(q, folders, true, false, 100000, q.Length < 2);
            var list = found.Where(x => showJunk || !Junk.IsMatch(Path.GetFileName(x.Path))).Select(x => new Row { F = x, M = FileMeta.Of(x.Path) }).ToList();
            return (list, Apply(list, f));
        });
        if (my != run || IsDisposed) return;
        rows = shown;
        Facets(all);
        Draw();
        var more = shown.Count > 3000 ? " (first 3,000 shown — add a word or a filter)" : "";
        info.Text = shown.Count == 0
            ? $"Nothing matches — try fewer words or remove a filter (✗)."
            : $"{shown.Count:N0} of {all.Count:N0} files match{more} · {clock.ElapsedMilliseconds} ms · Enter opens · right-click: show in folder, copy, attach";
        if (openIfOne && shown.Count == 1) Shell.Open(shown[0].F.Path);
        else if (openIfOne && lv.Items.Count > 0) { lv.Focus(); lv.Items[0].Selected = true; lv.Items[0].Focused = true; }
    }

    static List<Row> Apply(List<Row> list, FinderFilter f)
    {
        bool In(HashSet<string> set, string v) => set.Count == 0 || set.Contains(v);
        var r = list.Where(x =>
            In(f.Types, x.M.Type) && In(f.Docs, x.M.Doc) && In(f.Discs, x.M.Disc) && In(f.Levels, x.M.Level) &&
            (f.Rev.Length == 0 || x.F.Rev.Equals(f.Rev, StringComparison.OrdinalIgnoreCase)) &&
            (f.Status == "any" || (f.Status == "superseded" ? x.F.Status == "superseded" : x.F.Status != "superseded")) &&
            (f.From == null || x.F.Modified.Date >= f.From) && (f.To == null || x.F.Modified.Date <= f.To) &&
            f.FolderHas.All(w => x.F.Path.Contains(w, StringComparison.OrdinalIgnoreCase)) &&
            !f.Exclude.Any(w => x.F.Path.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        IOrderedEnumerable<Row> o = f.Sort switch
        {
            "number" => r.OrderBy(x => x.M.Number.Length > 0 ? x.M.Number : x.F.Name, StringComparer.OrdinalIgnoreCase),
            "date" => f.SortDesc ? r.OrderByDescending(x => x.F.Modified) : r.OrderBy(x => x.F.Modified),
            "size" => r.OrderByDescending(x => x.F.Size),
            "title" => r.OrderBy(x => x.M.Title, StringComparer.OrdinalIgnoreCase),
            "level" => r.OrderBy(x => x.M.Level).ThenBy(x => x.M.Number),
            "discipline" => r.OrderBy(x => x.M.Disc).ThenBy(x => x.M.Number),
            "type" => r.OrderBy(x => x.M.Type).ThenBy(x => x.M.Number),
            _ => r.OrderByDescending(x => x.F.Score).ThenByDescending(x => x.F.Modified)
        };
        return o.ToList();
    }

    /// <summary>The drop-downs offer what is in the results (with how many).</summary>
    void Facets(List<Row> all)
    {
        lastAll = all;
        FillCombos();
    }
    List<Row> lastAll = new();

    void FillCombos()
    {
        quiet = true;
        void Fill(ComboBox c, Func<Row, string> key, HashSet<string> cur)
        {
            c.BeginUpdate();
            c.Items.Clear();
            c.Items.Add("Any");
            foreach (var g in lastAll.GroupBy(key).Where(g => g.Key.Length > 0).OrderByDescending(g => g.Count()).Take(40)) c.Items.Add(g.Key);
            foreach (var v in cur) if (!c.Items.Contains(v)) c.Items.Add(v);
            if (cur.Count > 1) c.Items.Add(string.Join(", ", cur));
            c.SelectedIndex = cur.Count == 0 ? 0 : c.Items.IndexOf(cur.Count == 1 ? cur.First() : string.Join(", ", cur));
            c.EndUpdate();
        }
        Fill(cType, r => r.M.Type, flt.Types); Fill(cDoc, r => r.M.Doc, flt.Docs); Fill(cDisc, r => r.M.Disc, flt.Discs); Fill(cLevel, r => r.M.Level, flt.Levels);
        quiet = false;
    }

    void Draw()
    {
        lv.BeginUpdate();
        lv.Items.Clear();
        lv.Groups.Clear();
        lv.ShowGroups = flt.GroupBy.Length > 0;
        var groups = new Dictionary<string, ListViewGroup>();
        foreach (var r in rows.Take(3000))
        {
            var f = r.F; var m = r.M;
            var it = new ListViewItem(new[]
            {
                m.Number.Length > 0 ? m.Number : f.Name, m.Number.Length > 0 ? m.Title : "", f.Rev,
                f.Status == "latest" ? "✓ latest" : f.Status == "superseded" ? "superseded" : f.Status,
                m.Doc, m.Disc, m.Level, m.Type, f.Project,
                f.Modified > DateTime.MinValue ? f.Modified.ToString("dd MMM yyyy") : "", Projects.Size(f.Size), Path.GetDirectoryName(f.Path) ?? ""
            }) { Tag = r, ToolTipText = f.Path };
            if (f.Status == "superseded" || f.Status.StartsWith("in an old")) it.ForeColor = Th.Muted;
            if (flt.GroupBy.Length > 0)
            {
                var k = flt.GroupBy switch { "document" => m.Doc, "discipline" => m.Disc, "level" => m.Level, "type" => m.Type, "project" => f.Project, "folder" => Path.GetFileName(Path.GetDirectoryName(f.Path) ?? ""), _ => "" };
                if (k.Length == 0) k = "(other)";
                if (!groups.TryGetValue(k, out var g)) { g = new ListViewGroup(k, k); groups[k] = g; lv.Groups.Add(g); }
                it.Group = g;
            }
            lv.Items.Add(it);
        }
        foreach (var g in groups.Values) g.Header = $"{g.Name} · {g.Items.Count}";
        lv.EndUpdate();
    }

    List<Row> Sel() => lv.SelectedItems.Cast<ListViewItem>().Select(i => (Row)i.Tag).ToList();
    List<Row> SelOrAll() { var s = Sel(); return s.Count > 1 ? s : rows; }

    void OpenSel() { foreach (var r in Sel().Take(5)) Shell.Open(r.F.Path); }

    // ------------------------------------------------------------------ actions

    void ExportRegister()
    {
        var list = SelOrAll();
        if (list.Count == 0) { info.Text = "Nothing to export."; return; }
        try
        {
            var title = $"Drawing register — {(proj.SelectedIndex > 0 ? (string)proj.SelectedItem : "all projects")}" + (req.Text.Trim().Length > 0 ? " — " + req.Text.Trim() : "");
            var head = new[] { "No.", "Drawing no. / file", "Title", "Rev", "Status", "Document", "Discipline", "Level", "Type", "Project", "Date", "Size", "Folder", "File" };
            var data = list.Select((r, i) => new[]
            {
                (i + 1).ToString(), r.M.Number.Length > 0 ? r.M.Number : r.F.Name, r.M.Title, r.F.Rev, r.F.Status, r.M.Doc, r.M.Disc, r.M.Level, r.M.Type, r.F.Project,
                r.F.Modified > DateTime.MinValue ? r.F.Modified.ToString("dd MMM yyyy") : "", Projects.Size(r.F.Size), Path.GetDirectoryName(r.F.Path) ?? "", r.F.Name
            }).ToList();
            var safe = Regex.Replace(title, @"[\\/:*?""<>|]+", " ");
            if (safe.Length > 80) safe = safe[..80].Trim();
            var path = Path.Combine(Prompts.Folder, $"{safe} {DateTime.Now:dd MMM yyyy HHmm}.xlsx");
            Prompts.Xlsx(path, title, head, data);
            Shell.Open(path);
            info.Text = $"📊 Register of {list.Count} files saved: {path}";
        }
        catch (Exception e) { info.Text = "⚠ " + e.Message; }
    }

    void AttachAll()
    {
        var list = SelOrAll();
        if (list.Count == 0) return;
        var mb = list.Sum(r => r.F.Size) / 1048576.0;
        if ((list.Count > 15 || mb > 20) && MessageBox.Show(this, $"Attach {list.Count} files ({mb:0} MB) to one email?\nLarge emails may be refused — select fewer rows (Ctrl+click) to attach only those.", "Nova Enhance", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try { Outlook.NewWithAttachments(list.Select(r => FileCheck.Resolve(r.F.Path)).ToList()); }
        catch (Exception e) { MessageBox.Show(this, "⚠ " + e.Message, "Nova Enhance"); }
    }

    void AskNova()
    {
        var list = SelOrAll().Where(r => !FileType.IsModel(r.F.Path)).Take(FileTalk.Max).ToList();
        if (list.Count == 0) { info.Text = "Select the files to ask about (CAD / BIM files can't be read)."; return; }
        if (SelOrAll().Count > FileTalk.Max) info.Text = $"Nova takes {FileTalk.Max} files per question — the first {FileTalk.Max} were sent (select rows to choose).";
        foreach (var r in list) Screens.HubAttach(FileCheck.Resolve(r.F.Path));
    }

    void SaveReq()
    {
        var text = req.Text.Trim();
        if (text.Length == 0) { info.Text = "Type your requirements first."; return; }
        var name = Prompt.Ask(this, "⭐ Save filters", "Name", text.Length > 40 ? text[..40] : text);
        if (string.IsNullOrWhiteSpace(name)) return;
        var l = Saved(); l.RemoveAll(x => x.Name == name); l.Insert(0, (name, text, proj.SelectedIndex));
        Store.Set("win_finder_saved", JsonSerializer.Serialize(l.Select(x => new[] { x.Name, x.Text, x.Proj.ToString() })));
        info.Text = "⭐ Saved: " + name;
    }

    static List<(string Name, string Text, int Proj)> Saved()
    {
        try { return (JsonSerializer.Deserialize<List<string[]>>(Store.Str("win_finder_saved", "[]")) ?? new()).Select(a => (a[0], a[1], int.TryParse(a.ElementAtOrDefault(2), out var p) ? p : 0)).ToList(); }
        catch { return new(); }
    }

    ContextMenuStrip SavedMenu()
    {
        var m = new ContextMenuStrip { Font = Th.F(10), ShowImageMargin = false };
        var l = Saved();
        if (l.Count == 0) m.Items.Add(new ToolStripLabel("No saved filters yet — type requirements and press ⭐ Save"));
        foreach (var s in l)
        {
            var ss = s;
            var it = new ToolStripMenuItem("⭐ " + s.Name);
            it.Click += (_, _) => { if (ss.Proj < proj.Items.Count) proj.SelectedIndex = ss.Proj; req.Text = ss.Text; ApplyReq(); };
            it.DropDownItems.Add("🗑 Delete", null, (_, _) =>
            {
                var all = Saved(); all.RemoveAll(x => x.Name == ss.Name);
                Store.Set("win_finder_saved", JsonSerializer.Serialize(all.Select(x => new[] { x.Name, x.Text, x.Proj.ToString() })));
            });
            m.Items.Add(it);
        }
        return m;
    }

    ContextMenuStrip Menu()
    {
        var s = Sel().Select(r => r.F).ToList();
        var m = new ContextMenuStrip { Font = Th.F(10), ShowImageMargin = false };
        m.Items.Add("Open", null, (_, _) => OpenSel());
        m.Items.Add("Show in folder", null, (_, _) => Shell.Select(s[0].Path));
        m.Items.Add("⧉ Copy the file(s) — paste into an email or a folder", null, (_, _) =>
        {
            var c = new StringCollection();
            c.AddRange(s.Select(x => FileCheck.Resolve(x.Path)).ToArray());
            Clipboard.SetFileDropList(c);
        });
        m.Items.Add("⧉ Copy the path", null, (_, _) => Clipboard.SetText(string.Join(Environment.NewLine, s.Select(x => x.Path))));
        m.Items.Add("✉ Attach to a new email", null, (_, _) =>
        {
            try { Outlook.NewWithAttachments(s.Select(x => FileCheck.Resolve(x.Path)).ToList()); }
            catch (Exception e) { MessageBox.Show(this, "⚠ " + e.Message, "Nova Enhance"); }
        });
        m.Items.Add("✦ Ask Nova about the selected files", null, (_, _) => AskNova());
        m.Items.Add("🕘 Show all revisions of this document", null, (_, _) =>
        {
            flt.Status = "any"; WriteBar();
            var n = Revisions.Number(s[0].Name);
            box.Text = n.Length > 0 ? n : Revisions.Parse(s[0].Path, 0).Key;
        });
        return m;
    }

    sealed class Cmp : System.Collections.IComparer
    {
        readonly int col; readonly bool desc;
        public Cmp(int c, bool d) { col = c; desc = d; }
        public int Compare(object a, object b)
        {
            var x = (ListViewItem)a; var y = (ListViewItem)b;
            var fx = ((Row)x.Tag).F; var fy = ((Row)y.Tag).F;
            int r = col switch
            {
                9 => fx.Modified.CompareTo(fy.Modified),
                10 => fx.Size.CompareTo(fy.Size),
                _ => string.Compare(x.SubItems[col].Text, y.SubItems[col].Text, StringComparison.OrdinalIgnoreCase)
            };
            if (r == 0) r = string.Compare(x.Text, y.Text, StringComparison.OrdinalIgnoreCase);
            return desc ? -r : r;
        }
    }
}
