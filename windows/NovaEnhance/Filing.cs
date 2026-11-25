using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>The five labels of a file (+ where each came from).</summary>
class FileLabels
{
    public string Project = "", Number = "", Source = "", Kind = "", Disc = "", Level = "", Stage = "", Rev = "", Code = "", State = "";
    public List<string> Why = new();
    public List<(string Role, string Code)> Unknown = new();
    public string WhyText => string.Join("\n", Why);
}

/// <summary>
/// 📐 Nova's filing concept — the same in every project: each file gets Source (who issued it) · Kind (what it is) · Discipline · Stage
/// (only for design and contract sets) · Revision / review code. Nova decides in this order: the document number → the title block
/// (PDFs already read) → the nearest subfolder → the higher folders → the words of the name. The codes themselves live in 🧠 Nova Brain
/// (section 📐 Filing & document codes): shared codes for every project + each project's own codes and number template, learned from
/// your files with 🧠 Learn this project (Nova asks you when a code is not clear). A submittal never gets an IFC or Contract stage.
/// Designed by A.HAMROUNI
/// </summary>
static class Filing
{
    // ------------------------------------------------------------------ the vocabulary

    public static readonly string[] Kinds =
    {
        "Design drawing", "Shop drawing", "Material submittal", "Prequalification", "Request for information (RFI)", "Inspection request",
        "Site MEP inspection clearance", "Method statement", "Non-conformance (NCR)", "Site observation report (SOR)", "Letter", "Engineer's instruction",
        "Variation", "Site instruction", "Contract document", "Specification", "Minutes", "Transmittal", "Report", "Calculation", "Schedule", "BOQ",
        "Register", "Sketch", "Memo", "Presentation", "Daily report", "Email", "Model / CAD", "Payment",
    };

    public static readonly string[] Sources = { "Consultant (MZA)", "Contractor", "Client", "Authority", "Subconsultant", "Supplier" };

    public static readonly string[] Disciplines =
    {
        "Architecture", "Interior design", "Structure", "Landscape", "MEP", "Mechanical services", "Electrical services", "Plumbing", "Fire & life safety",
        "Acoustic", "Façade", "Lighting", "Signage", "Waste management", "Civil works", "Health & safety", "Infrastructure", "ICT", "Vertical transport",
        "Project management", "General",
    };

    public static readonly string[] Stages = { "Concept", "Schematic", "Design development", "IFA", "IFT", "Tender", "Contract (executed)", "IFC", "IFC 01", "IFC 02", "IFC 03", "As-built" };

    /// <summary>Kinds that are submittals or correspondence: they never get a design stage (IFC, Contract…).</summary>
    static readonly HashSet<string> NoStage = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shop drawing", "Material submittal", "Prequalification", "Request for information (RFI)", "Inspection request", "Site MEP inspection clearance",
        "Method statement", "Non-conformance (NCR)", "Site observation report (SOR)", "Letter", "Engineer's instruction", "Variation", "Site instruction",
        "Minutes", "Daily report", "Payment", "Calculation",
    };

    // ------------------------------------------------------------------ the built-in rules (🧠 Nova Brain › 📐 Filing & document codes)

    public static JsonObject[] BuiltIn(Func<string, string, string, JsonObject> R) => new[]
    {
        R("FC-01", "The filing concept",
            "Every project file gets five labels: Source (who issued it) · Kind (what it is) · Discipline · Stage (only design drawings and contract sets: IFT, IFC 01/02, As-built) · Revision and review code (Code 1–5). " +
            "Decide in this order: the document number, then the title block, then the nearest subfolder, then the higher folders, then the words of the name. The number of the file itself wins over its folder. " +
            "A submittal (shop drawing, material, RFI, NCR, SOR, inspection, clearance, letter) never has an IFC or Contract stage, even when its name says “based on IFC”. A file in a Response / Reply folder is MZA's reply, not the submission."),
        R("FC-02", "Kinds (every project)",
            "Codes in document numbers: SDS / SHD shop drawing · MAR / MAS / SSM / MAT material submittal · PQS prequalification · RFI · WIR / MIR / IR inspection request · DSH site MEP inspection clearance · MS- / MST / MSS method statement · NCR · SOR · LTR / L letter · EI engineer's instruction · VO variation · SI site instruction · DRW / DWG design drawing · SPC specification · MOM minutes · TRN transmittal · RPT report · CAL calculation · SCH schedule · BOQ · REG register · SKT sketch · DSR daily report. " +
            "[codes.kind = SDS:Shop drawing; SHD:Shop drawing; MAR:Material submittal; MAS:Material submittal; SSM:Material submittal; MAT:Material submittal; PQS:Prequalification; RFI:Request for information (RFI); WIR:Inspection request; MIR:Inspection request; IR:Inspection request; DSH:Site MEP inspection clearance; MST:Method statement; MSS:Method statement; NCR:Non-conformance (NCR); SOR:Site observation report (SOR); LTR:Letter; LET:Letter; L:Letter; EI:Engineer's instruction; VO:Variation; VAR:Variation; SI:Site instruction; SIN:Site instruction; DRW:Design drawing; DWG:Design drawing; SPC:Specification; SPE:Specification; SPEC:Specification; MOM:Minutes; MIN:Minutes; TRN:Transmittal; TRS:Transmittal; RPT:Report; REP:Report; CAL:Calculation; CALC:Calculation; SCH:Schedule; BOQ:BOQ; REG:Register; SKT:Sketch; SK:Sketch; MEM:Memo; PRS:Presentation; DSR:Daily report; CVI:Site instruction; PAY:Payment; IPC:Payment]"),
        R("FC-03", "Disciplines (every project)",
            "AR / ARC architecture · ID / IN interior design · ST / STR / SE structure · LA / LS / LND landscape · MEP · MS / ME / MC / MV / HV / HVAC mechanical services · ES / EL / EE electrical services · PL / PLB / PH / DR plumbing · FF / FP / FA / FLS fire & life safety · AC acoustic · FC façade · LT lighting · SG signage · WM waste · CV / CI / CW civil works · HSE health & safety · IF infrastructure · ICT / IT · EV / LF vertical transport · PM project management · GN / GE / GEN general. " +
            "[codes.disc = AR:Architecture; ARC:Architecture; ID:Interior design; IN:Interior design; INT:Interior design; ST:Structure; STR:Structure; SE:Structure; LA:Landscape; LS:Landscape; LND:Landscape; MEP:MEP; MS:Mechanical services; ME:Mechanical services; MC:Mechanical services; MV:Mechanical services; HV:Mechanical services; HVAC:Mechanical services; ES:Electrical services; EL:Electrical services; EE:Electrical services; PL:Plumbing; PLB:Plumbing; PH:Plumbing; DR:Plumbing; FF:Fire & life safety; FP:Fire & life safety; FA:Fire & life safety; FLS:Fire & life safety; AC:Acoustic; FC:Façade; CW:Civil works; CV:Civil works; CI:Civil works; LT:Lighting; SG:Signage; WM:Waste management; HSE:Health & safety; IF:Infrastructure; ICT:ICT; IT:ICT; EV:Vertical transport; LF:Vertical transport; PM:Project management; GN:General; GE:General; GEN:General]"),
        R("FC-04", "Who issued it (every project)",
            "MZA = Consultant (MZA, us). [codes.source = MZA:Consultant (MZA)]"),
        R("FC-05", "Folders → kind",
            "Folder names tell the kind when the number does not: shop drawings, _SHD, material (MAR), prequalification (PQS), RFI, NCR, SOR, inspection, MEP clearance, letters, Engineer's instruction, executed contract / _Contract, specifications, minutes, transmittals, models. " +
            "[folders.kind = shop drawing:Shop drawing; _shd:Shop drawing; \\sds:Shop drawing; _mar:Material submittal; material:Material submittal; _pqs:Prequalification; prequal:Prequalification; rfi:Request for information (RFI); non conformance:Non-conformance (NCR); ncr:Non-conformance (NCR); sor:Site observation report (SOR); inspection:Inspection request; wir:Inspection request; mir:Inspection request; mep clearance:Site MEP inspection clearance; method statement:Method statement; _letter:Letter; letters:Letter; engineer- avi:Engineer's instruction; engineer's instruction:Engineer's instruction; executed contract:Contract document; _contract:Contract document; contract documents:Contract document; specification:Specification; minutes:Minutes; _mom:Minutes; transmittal:Transmittal; navix:Model / CAD; model:Model / CAD; _drawings:Design drawing; drawings:Design drawing]"),
        R("FC-06", "Folders → discipline",
            "Numbered discipline folders (01. Architecture … 13 ADCD (FLS), 01-Architectural, _AR, _ID) give the discipline of everything inside. " +
            "[folders.disc = architect:Architecture; _ar:Architecture; interior:Interior design; _id:Interior design; structur:Structure; _st:Structure; landscape:Landscape; _ls:Landscape; mep:MEP; mechanical:Mechanical services; _ms:Mechanical services; electrical:Electrical services; _es:Electrical services; plumbing:Plumbing; drainage:Plumbing; acoustic:Acoustic; facade:Façade; façade:Façade; lighting:Lighting; signage:Signage; waste:Waste management; adcd:Fire & life safety; (fls):Fire & life safety; fire:Fire & life safety; civil:Civil works; _cw:Civil works; hse:Health & safety; safety:Health & safety]"),
        R("FC-07", "Folders → stage",
            "Only design and contract sets have a stage: IFC submission → IFC (with its date and R00), IFC 01 / IFC 02, IFT / tender, executed contract → Contract (executed), as-built. " +
            "[folders.stage = ifc submission:IFC; ifc drawing:IFC; issued for construction:IFC; executed contract:Contract (executed); ift:IFT; issued for tender:IFT; tender:Tender; as-built:As-built; as built:As-built; ifa:IFA]"),
        R("FC-08", "Replies and withdrawn",
            "A file in Response / Reply / Comments is MZA's reply; Retracted / Withdrawn / Superseded is not current. [folders.reply = response; reply; repsonse; respone; comments; mza response] [folders.withdrawn = retracted; withdrawn; superseded; cancelled; _old; old]"),
    };

    public static JsonObject[] ProjectRules(Func<string, string, string, JsonObject> R) => new[]
    {
        R("PJ-04", "SLV parties and codes",
            "SLV: ZUC / ZUB = the contractor (Züblin) — every ZUC submittal is the contractor's. NY = Nine Yard, the client. MZA and YT-MZA = issued by MZA. DSH = site MEP inspection clearance. CW civil works · ES electrical services · MS mechanical services · HSE health & safety. " +
            "Numbers: SLV-<from>-<discipline>-<type>-<serial> (SLV-ZUC-ID-SDS-00411); letters MZA-ZUB-22-1497-L-<year>-<serial>; design drawings YT-MZA-<…>-DRW-<discipline>-…-<serial>-<stage>. " +
            "[codes.SLV.source = ZUC:Contractor; ZUB:Contractor; NY:Client; YT:Consultant (MZA); MZA:Consultant (MZA)] [codes.SLV.kind = DSH:Site MEP inspection clearance] [codes.SLV.template = project from discipline type serial]"),
        R("PJ-05", "RS5-C7 parties",
            "RS5-C7: ACC = the contractor. Numbers RS5-C7-ACC-<discipline>-<type>-<serial>-R<rev>. [codes.RS5-C7.source = ACC:Contractor; MZA:Consultant (MZA)]"),
    };

    // ------------------------------------------------------------------ the dictionaries, read from the brain (cached until it changes)

    class Dict
    {
        public Dictionary<string, string> Kind = new(StringComparer.OrdinalIgnoreCase), Disc = new(StringComparer.OrdinalIgnoreCase), Source = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, string>> PKind = new(StringComparer.OrdinalIgnoreCase), PDisc = new(StringComparer.OrdinalIgnoreCase), PSource = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Template = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> Ignore = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Key, string Val)> FKind = new(), FDisc = new(), FStage = new();
        public List<string> Reply = new(), Withdrawn = new();
    }

    static Dict cur;
    static readonly object L = new();
    static readonly Dictionary<string, (string Kind, string Disc, string Stage, string State, List<string> Why)> dirCache = new(StringComparer.OrdinalIgnoreCase);
    static Filing() { Brain.Changed += () => { lock (L) { cur = null; dirCache.Clear(); } }; }

    static readonly Regex SetRx = new(@"\[([a-z][\w.\-]*)\s*=\s*([^\]]+)\]", RegexOptions.IgnoreCase);

    static Dict D { get { lock (L) return cur ??= Load(); } }

    static Dict Load()
    {
        var d = new Dict();
        IEnumerable<(JsonObject, JsonObject)> rules;
        try { rules = Brain.Rules(Brain.Snapshot()).ToList(); } catch { rules = Brain.Rules(Brain.Defaults()).ToList(); }
        // built-ins first (they may be missing from an older brain), then every rule of the brain: later rules win per code
        var all = Brain.Rules(Brain.Defaults()).Concat(rules);
        foreach (var (_, r) in all)
        {
            if (r["off"]?.GetValue<bool>() == true) continue;
            foreach (Match m in SetRx.Matches(r["text"]?.ToString() ?? ""))
            {
                var name = m.Groups[1].Value.ToLowerInvariant(); var val = m.Groups[2].Value.Trim();
                if (!name.StartsWith("codes.") && !name.StartsWith("folders.")) continue;
                var parts = name.Split('.');
                if (name == "folders.reply") { d.Reply = List(val); continue; }
                if (name == "folders.withdrawn") { d.Withdrawn = List(val); continue; }
                if (name is "folders.kind" or "folders.disc" or "folders.stage")
                {
                    var target = name == "folders.kind" ? d.FKind : name == "folders.disc" ? d.FDisc : d.FStage;
                    foreach (var (k, v) in Pairs(val)) { target.RemoveAll(x => x.Key == k.ToLowerInvariant()); target.Add((k.ToLowerInvariant(), v)); }
                    continue;
                }
                if (parts.Length == 2)
                {
                    var target = parts[1] switch { "kind" => d.Kind, "disc" => d.Disc, "source" => d.Source, _ => null };
                    if (target != null) foreach (var (k, v) in Pairs(val)) target[k] = v;
                }
                else if (parts.Length >= 3)
                {
                    // codes.<PROJECT>.<what> — the project code may hold dots? no; it may hold dashes (RS5-C7)
                    var proj = string.Join(".", parts.Skip(1).Take(parts.Length - 2)).ToUpperInvariant();
                    var what = parts[^1];
                    if (what == "template") { d.Template[proj] = Regex.Split(val.ToLowerInvariant(), @"[\s,;·\-]+").Where(x => x.Length > 0).ToList(); continue; }
                    if (what == "ignore") { if (!d.Ignore.TryGetValue(proj, out var ig)) d.Ignore[proj] = ig = new(StringComparer.OrdinalIgnoreCase); foreach (var x in List(val)) ig.Add(x); continue; }
                    var map = what switch { "kind" => d.PKind, "disc" => d.PDisc, "source" => d.PSource, _ => null };
                    if (map == null) continue;
                    if (!map.TryGetValue(proj, out var pm)) map[proj] = pm = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var (k, v) in Pairs(val)) pm[k] = v;
                }
            }
        }
        return d;
    }

    static List<string> List(string v) => v.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    static IEnumerable<(string, string)> Pairs(string v) =>
        List(v).Select(p => { var i = p.IndexOf(':'); return i > 0 ? (p[..i].Trim(), p[(i + 1)..].Trim()) : ("", ""); }).Where(x => x.Item1.Length > 0 && x.Item2.Length > 0);

    static string Look(Dictionary<string, Dictionary<string, string>> proj, Dictionary<string, string> shared, string project, string code)
    {
        if (project.Length > 0 && proj.TryGetValue(project, out var pm) && pm.TryGetValue(code, out var v)) return v;
        return shared.TryGetValue(code, out var s) ? s : null;
    }

    public static string KindOf(string project, string code) => Look(D.PKind, D.Kind, project ?? "", code);
    public static string DiscOf(string project, string code) => Look(D.PDisc, D.Disc, project ?? "", code);
    public static string SourceOf(string project, string code) => Look(D.PSource, D.Source, project ?? "", code);
    public static bool Ignored(string project, string code) => D.Ignore.TryGetValue(project ?? "", out var ig) && ig.Contains(code);
    public static List<string> TemplateOf(string project) => D.Template.TryGetValue(project ?? "", out var t) ? t : null;

    // ------------------------------------------------------------------ the project of a file

    static List<(string Prefix, string Code)> roots;
    static DateTime rootsAt;

    public static string ProjectOf(string path, string number = "")
    {
        if (roots == null || DateTime.Now - rootsAt > TimeSpan.FromMinutes(2))
        {
            try
            {
                roots = Projects.All().SelectMany(p => p.Folders.Concat((p.Sources ?? new()).Select(s => s.Path)).Select(f => (f.TrimEnd('\\') + "\\", p.Code)))
                    .OrderByDescending(x => x.Item1.Length).ToList();
            }
            catch { roots = new(); }
            rootsAt = DateTime.Now;
        }
        foreach (var (pre, code) in roots) if (path.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return code.ToUpperInvariant();
        if (number.Length > 0)
            foreach (var (_, code) in roots) if (number.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase)) return code.ToUpperInvariant();
        return "";
    }

    static string RootOf(string path)
    {
        foreach (var (pre, _) in roots ?? new()) if (path.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return pre;
        return "";
    }

    // ------------------------------------------------------------------ classify one file

    static readonly Regex LevelRx = new(@"^(?:L\d{1,2}|B\d|GF|GL|RF|RL|ROF|PD|POD|MZ|MEZ|LG|UG|ZZ|XX)$", RegexOptions.IgnoreCase);
    static readonly Regex LetterNo = new(@"^([A-Z]{2,5})-([A-Z]{2,5})-[\d\-]+-L-(\d{4})-\d+", RegexOptions.IgnoreCase);
    static readonly Regex CodeRx = new(@"(?:^|[\s_\-\(])(?:code\s?|c)([1-5])(?=$|[\s_\-\.\)])", RegexOptions.IgnoreCase);
    static readonly Regex StageDate = new(@"^(\d{2})(\d{2})(\d{2})[_\s\-]");
    static readonly Regex IfcN = new(@"\bIFC[\s_\-]*0?([1-9])\b", RegexOptions.IgnoreCase);
    static readonly HashSet<string> Cad = new(StringComparer.OrdinalIgnoreCase) { ".dwg", ".dxf", ".dgn", ".rvt", ".rfa", ".ifc", ".nwd", ".nwc", ".skp", ".3dm", ".pln" };

    /// <summary>The five labels of a file (+ why). Fast: folder labels are cached per folder.</summary>
    public static FileLabels Classify(string path)
    {
        var f = new FileLabels();
        if (string.IsNullOrEmpty(path)) return f;
        var d = D;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        f.Number = Revisions.Number(name);
        f.Project = ProjectOf(path, f.Number);

        // 1 · the document number
        if (f.Number.Length > 0)
        {
            var lm = LetterNo.Match(f.Number);
            if (lm.Success)
            {
                f.Kind = "Letter"; f.Why.Add($"Kind: Letter ← “-L-” in {f.Number}");
                var src = SourceOf(f.Project, lm.Groups[1].Value);
                var to = SourceOf(f.Project, lm.Groups[2].Value);
                if (src != null) { f.Source = src; f.Why.Add($"Source: {src} ← {lm.Groups[1].Value} (from) · to {lm.Groups[2].Value}{(to != null ? " = " + to : "")}"); }
            }
            else Segments(f, d);
        }

        // 2 · the folders (nearest first), cached per folder
        var dir = Path.GetDirectoryName(path) ?? "";
        var fl = Folders(dir, f.Project);
        if (f.Kind.Length == 0 && fl.Kind.Length > 0) { f.Kind = fl.Kind; f.Why.AddRange(fl.Why.Where(w => w.StartsWith("Kind"))); }
        if (f.Disc.Length == 0 && fl.Disc.Length > 0) { f.Disc = fl.Disc; f.Why.AddRange(fl.Why.Where(w => w.StartsWith("Discipline"))); }
        else if (f.Disc.Length > 0 && fl.Disc.Length > 0 && !fl.Disc.Equals(f.Disc, StringComparison.OrdinalIgnoreCase))
            f.Why.Add($"⚠ the folder says {fl.Disc} — the file's own number wins");
        if (fl.State.Length > 0) { f.State = fl.State; f.Why.AddRange(fl.Why.Where(w => w.StartsWith("State"))); }
        if (f.State == "MZA reply" && f.Source != "Consultant (MZA)") { f.Why.Add($"Source: Consultant (MZA) ← a reply folder (the submission was from {(f.Source.Length > 0 ? f.Source : "the sender")})"); f.Source = "Consultant (MZA)"; }

        // 3 · the words of the name / the type of file
        if (f.Kind.Length == 0)
        {
            var low = name.ToLowerInvariant();
            var k = low.Contains("minutes") || Regex.IsMatch(low, @"\bmom\b") ? "Minutes" : low.Contains("transmittal") ? "Transmittal" : low.Contains("letter") ? "Letter"
                  : low.Contains("specification") ? "Specification" : low.Contains("engineer’s instruction") || low.Contains("engineer's instruction") ? "Engineer's instruction"
                  : low.Contains("contract") || low.Contains("agreement") || low.Contains("conditions of") ? "Contract document" : low.Contains("report") ? "Report"
                  : low.Contains("register") ? "Register" : Regex.IsMatch(low, @"\brfi\b") ? "Request for information (RFI)" : Regex.IsMatch(low, @"\bboq\b|bill of quantit") ? "BOQ"
                  : low.Contains("schedule") ? "Schedule" : low.Contains("shop drawing") ? "Shop drawing" : low.Contains("prequalification") ? "Prequalification"
                  : low.Contains("material submittal") ? "Material submittal" : low.Contains("method statement") ? "Method statement" : "";
            if (k.Length > 0) { f.Kind = k; f.Why.Add($"Kind: {k} ← words in the name"); }
        }
        if (ext is ".msg" or ".eml" && (f.Kind.Length == 0 || f.Kind == "Design drawing")) { f.Kind = "Email"; f.Why.Add("Kind: Email ← .msg / .eml"); }
        if (Cad.Contains(ext) && (f.Kind.Length == 0 || f.Kind == "Design drawing")) { f.Kind = "Model / CAD"; f.Why.Add("Kind: Model / CAD ← " + ext); }
        if (f.Source.Length == 0 && f.Number.Length > 0 && f.Kind.Length > 0 && SubmittalKind(f.Kind) && f.State != "MZA reply") { /* left empty: unknown sender */ }

        // 4 · stage: only design drawings, contract sets, specs, models, transmittals and emails of a set
        if (!NoStage.Contains(f.Kind))
        {
            if (fl.Stage.Length > 0) { f.Stage = fl.Stage; f.Why.AddRange(fl.Why.Where(w => w.StartsWith("Stage"))); }
            else
            {
                var tail = f.Number.Length > 0 && name.IndexOf(f.Number, StringComparison.OrdinalIgnoreCase) is var at && at >= 0 ? name[(at + f.Number.Length)..] : name;
                var sm = Regex.Match(tail, @"(?:^|[\s_\-])(IFC|IFT|IFA)(\d?)(?=$|[\s_\-\.])", RegexOptions.IgnoreCase);
                if (sm.Success && f.Kind is "Design drawing" or "Model / CAD" or "Specification" or "Contract document" or "")
                { f.Stage = sm.Groups[1].Value.ToUpperInvariant(); f.Why.Add($"Stage: {f.Stage} ← “{sm.Value.Trim(' ', '_', '-')}” after the number"); }
            }
        }
        else if (fl.Stage.Length > 0) f.Why.Add($"Stage: none — a {f.Kind.ToLowerInvariant()} never takes the folder's stage ({fl.Stage})");

        // 5 · revision and review code
        var rv = Revisions.Parse(path, 0).Rev;
        if (rv.Length > 0 && !Regex.IsMatch(rv, "^IF[A-Z]$")) { f.Rev = rv; }
        else { var r1 = Regex.Match(dir, @"\\R(\d{1,2})(?=\\|$)", RegexOptions.IgnoreCase); if (r1.Success) { f.Rev = "R" + r1.Groups[1].Value; f.Why.Add($"Revision: {f.Rev} ← folder \\R{r1.Groups[1].Value}\\"); } }
        var rest = f.Number.Length > 0 && name.IndexOf(f.Number, StringComparison.OrdinalIgnoreCase) is var a2 && a2 >= 0 ? name[(a2 + f.Number.Length)..] : name;
        var cm = CodeRx.Matches(rest).LastOrDefault();
        if (cm != null) { f.Code = "Code " + cm.Groups[1].Value; f.Why.Add($"Review: {f.Code} ← “{cm.Value.Trim(' ', '_', '-', '(')}” in the name"); }
        else { var cf = Regex.Match(dir, @"\\Code\s?([1-5])\s", RegexOptions.IgnoreCase); if (cf.Success) { f.Code = "Code " + cf.Groups[1].Value; f.Why.Add($"Review: {f.Code} ← folder “Code {cf.Groups[1].Value} …”"); } }
        if (f.State.Length == 0 && Regex.IsMatch(rest, @"(?:^|[\s_\-])open(?=$|[\s_\-\.])", RegexOptions.IgnoreCase)) { f.State = "Open"; f.Why.Add("State: Open ← “Open” in the name"); }
        return f;
    }

    public static bool SubmittalKind(string k) => NoStage.Contains(k ?? "");

    /// <summary>The parts of the number: by the project's template when it has one, else by what each code means.</summary>
    static void Segments(FileLabels f, Dict d)
    {
        var seg = f.Number.Split('-').ToList();
        var projTokens = f.Project.Split('-').ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tpl = TemplateOf(f.Project);
        if (tpl != null && seg.Count >= tpl.Count - 1)
        {
            // project codes with a dash (RS5-C7) take two parts
            int si = 0;
            foreach (var role in tpl)
            {
                if (si >= seg.Count) break;
                if (role == "project") { si += Math.Max(1, f.Project.Count(c => c == '-') + 1); continue; }
                var code = seg[si++];
                Assign(f, role, code, true);
            }
            if (f.Kind.Length > 0 || f.Disc.Length > 0) return;
        }
        // no template (or it did not fit): find each code by its meaning — the type is the LAST type-like code (it follows the discipline)
        int kindAt = -1;
        for (int i = seg.Count - 1; i >= 0; i--)
            if (!projTokens.Contains(seg[i]) && KindOf(f.Project, seg[i]) != null && !seg[i].All(char.IsDigit)) { kindAt = i; break; }
        for (int i = 0; i < seg.Count; i++)
        {
            var c = seg[i];
            if (projTokens.Contains(c) || c.All(char.IsDigit) || c.Length > 6) continue;
            if (i == kindAt) { Assign(f, "type", c, false); continue; }
            if (f.Source.Length == 0 && SourceOf(f.Project, c) != null) { Assign(f, "from", c, false); continue; }
            if (f.Disc.Length == 0 && DiscOf(f.Project, c) != null) { Assign(f, "discipline", c, false); continue; }
            if (f.Level.Length == 0 && LevelRx.IsMatch(c)) { f.Level = c.ToUpperInvariant(); continue; }
            // a short code of letters Nova does not know yet: the 🧠 Learn window asks you about it
            if (c.Length is >= 2 and <= 4 && c.All(char.IsLetter) && i > 0 && !LevelRx.IsMatch(c) && !Ignored(f.Project, c))
                f.Unknown.Add(("?", c.ToUpperInvariant()));
        }
    }

    static void Assign(FileLabels f, string role, string code, bool byTemplate)
    {
        var how = byTemplate ? "template" : "number";
        switch (role)
        {
            case "from":
            case "originator":
                var s = SourceOf(f.Project, code);
                if (s != null) { f.Source = s; f.Why.Add($"Source: {s} ← {code} in the {how}"); } else if (!Ignored(f.Project, code)) f.Unknown.Add(("from", code.ToUpperInvariant()));
                break;
            case "discipline":
                var dd = DiscOf(f.Project, code);
                if (dd != null) { f.Disc = dd; f.Why.Add($"Discipline: {dd} ← {code} in the {how}"); } else if (!Ignored(f.Project, code)) f.Unknown.Add(("discipline", code.ToUpperInvariant()));
                break;
            case "type":
                var k = KindOf(f.Project, code);
                if (k != null) { f.Kind = k; f.Why.Add($"Kind: {k} ← {code} in the {how}"); } else if (!Ignored(f.Project, code)) f.Unknown.Add(("type", code.ToUpperInvariant()));
                break;
            case "level":
                if (LevelRx.IsMatch(code)) f.Level = code.ToUpperInvariant();
                break;
        }
    }

    /// <summary>What the folders say (nearest folder first): kind, discipline, stage (+ its date and R00), reply / withdrawn.</summary>
    static (string Kind, string Disc, string Stage, string State, List<string> Why) Folders(string dir, string project)
    {
        lock (L) if (dirCache.TryGetValue(dir, out var c)) return c;
        var d = D;
        string kind = "", disc = "", stage = "", state = "";
        var why = new List<string>();
        var root = RootOf(dir + "\\");
        var rel = root.Length > 0 && (dir + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase) ? dir[Math.Min(dir.Length, root.Length)..] : dir;
        var parts = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            var raw = parts[i];
            var low = raw.ToLowerInvariant();
            var bare = Regex.Replace(low, @"^\d{1,2}\s*[\.\-_]\s*", "");    // "05. MEP" → "mep"
            var isNumber = Revisions.Number(raw).Length > 0;                 // a document folder: its words are a title
            if (state.Length == 0)
            {
                if (d.Reply.Any(r => low == r || low.StartsWith(r + " ") || low.EndsWith(" " + r) || (r.Contains(' ') && low.Contains(r)))) { state = "MZA reply"; why.Add($"State: MZA reply ← folder “{raw}”"); }
                else if (d.Withdrawn.Any(r => low == r || low.Contains(r) && r.Length > 4)) { state = "Withdrawn"; why.Add($"State: Withdrawn ← folder “{raw}”"); }
            }
            if (!isNumber)
            {
                if (kind.Length == 0) foreach (var (k, v) in d.FKind) if (Hit(low, bare, k)) { kind = v; why.Add($"Kind: {v} ← folder “{raw}”"); break; }
                if (disc.Length == 0) foreach (var (k, v) in d.FDisc) if (Hit(low, bare, k)) { disc = v; why.Add($"Discipline: {v} ← folder “{raw}”"); break; }
            }
            if (stage.Length == 0)
                foreach (var (k, v) in d.FStage)
                    if (Hit(low, bare, k))
                    {
                        var s = v;
                        var n = IfcN.Match(raw); if (s == "IFC" && n.Success) s = "IFC 0" + n.Groups[1].Value;
                        var dm = StageDate.Match(raw);
                        if (dm.Success && int.TryParse(dm.Groups[2].Value, out var mo) && mo is >= 1 and <= 12 && int.TryParse(dm.Groups[3].Value, out var dy) && dy is >= 1 and <= 31)
                            s += " · " + new DateTime(2000 + int.Parse(dm.Groups[1].Value), mo, dy).ToString("dd MMM yyyy");
                        var rr = Regex.Match(raw, @"(?:^|[\s_\-\)])(R\d{2})(?=$|[\s_\-\(])", RegexOptions.IgnoreCase);
                        if (rr.Success) s += " · " + rr.Groups[1].Value.ToUpperInvariant();
                        stage = s; why.Add($"Stage: {s} ← folder “{raw}”");
                        break;
                    }
        }
        var res = (kind, disc, stage, state, why);
        lock (L) { if (dirCache.Count > 20000) dirCache.Clear(); dirCache[dir] = res; }
        return res;
    }

    static bool Hit(string low, string bare, string key)
    {
        if (key.Length == 0) return false;
        if (key.StartsWith("\\")) return low == key[1..];                    // "\sds" = the whole folder name
        if (key.StartsWith("_")) return low == key || low.StartsWith(key + " ") || bare == key;   // "_ar" = exactly that folder
        if (key.Length <= 3) return Regex.IsMatch(low, @"(?<![a-z])" + Regex.Escape(key) + @"(?![a-z])");
        return low.Contains(key) || bare.StartsWith(key);
    }

    // ------------------------------------------------------------------ 🧠 learning: what Nova does not know yet

    public class Unknown { public string Code = "", Role = "", Project = ""; public int Count; public List<string> Examples = new(); public string Suggest = ""; }

    /// <summary>The codes of a project's numbers Nova does not know yet (most used first), with examples and a suggestion from the titles.</summary>
    public static List<Unknown> Learn(string project, IEnumerable<string> paths, CancellationToken ct = default)
    {
        var map = new Dictionary<string, Unknown>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var p in paths)
        {
            if (++n % 2000 == 0) ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(p);
            var num = Revisions.Number(name);
            if (num.Length == 0) continue;
            var f = new FileLabels { Number = num, Project = project.ToUpperInvariant() };
            var lm = LetterNo.Match(num);
            if (lm.Success)
            {
                foreach (var g in new[] { lm.Groups[1].Value, lm.Groups[2].Value })
                    if (SourceOf(f.Project, g) == null && !Ignored(f.Project, g)) f.Unknown.Add(("from", g.ToUpperInvariant()));
            }
            else Segments(f, D);
            foreach (var (role, code) in f.Unknown)
            {
                if (code.Length < 2 || code.All(char.IsDigit) || project.Split('-').Contains(code, StringComparer.OrdinalIgnoreCase)) continue;
                if (!map.TryGetValue(code, out var u)) map[code] = u = new Unknown { Code = code, Role = role, Project = project };
                u.Count++;
                if (u.Role == "?" && role != "?") u.Role = role;
                if (u.Examples.Count < 4 && !u.Examples.Any(e => e.Equals(name, StringComparison.OrdinalIgnoreCase))) u.Examples.Add(name);
            }
        }
        var res = map.Values.Where(u => u.Count >= 3).OrderByDescending(u => u.Count).Take(60).ToList();
        foreach (var u in res) u.Suggest = Suggest(u);
        return res;
    }

    static readonly (string Words, string Meaning)[] Hints =
    {
        ("shop drawing|layout|details|plan|section|elevation", "Kind: Shop drawing"), ("material|submittal for", "Kind: Material submittal"),
        ("prequalification|pre-qualification", "Kind: Prequalification"), ("inspection|wir|mir", "Kind: Inspection request"), ("request for information", "Kind: Request for information (RFI)"),
        ("non conformance|non-conformance", "Kind: Non-conformance (NCR)"), ("observation", "Kind: Site observation report (SOR)"), ("method statement", "Kind: Method statement"),
        ("clearance", "Kind: Site MEP inspection clearance"), ("letter|notice|claim", "Kind: Letter"), ("minutes|meeting", "Kind: Minutes"), ("report", "Kind: Report"),
        ("calculation", "Kind: Calculation"), ("transmittal", "Kind: Transmittal"), ("variation", "Kind: Variation"),
    };

    static string Suggest(Unknown u)
    {
        var text = string.Join(" ", u.Examples).ToLowerInvariant();
        if (u.Role is "type" or "?")
            foreach (var (w, m) in Hints) if (Regex.IsMatch(text, @"\b(" + w + @")")) return m;
        if (u.Role == "from") return "Source: Contractor";
        if (u.Role == "discipline") return "Discipline: General";
        return "";
    }

    /// <summary>Saves your answers into 🧠 Nova Brain (one rule per project) — every part of Nova uses them at once.</summary>
    public static void SaveAnswers(string project, List<(string Code, string Answer)> answers, string template = null)
    {
        if (!Brain.Unlocked) throw new InvalidOperationException("Open 🧠 Nova Brain first (its password) — the codes are saved inside it.");
        var p = project.ToUpperInvariant();
        var kinds = answers.Where(a => a.Answer.StartsWith("Kind: ")).Select(a => $"{a.Code}:{a.Answer[6..]}").ToList();
        var discs = answers.Where(a => a.Answer.StartsWith("Discipline: ")).Select(a => $"{a.Code}:{a.Answer[12..]}").ToList();
        var srcs = answers.Where(a => a.Answer.StartsWith("Source: ")).Select(a => $"{a.Code}:{a.Answer[8..]}").ToList();
        var ign = answers.Where(a => a.Answer.StartsWith("Ignore")).Select(a => a.Code).ToList();
        var words = string.Join(" · ", answers.Where(a => !a.Answer.StartsWith("Ignore")).Select(a => $"{a.Code} = {a.Answer.Split(": ").Last().ToLowerInvariant()}"));
        var text = $"{p}: {words}{(words.Length > 0 && ign.Count > 0 ? " · " : "")}{(ign.Count > 0 ? "not codes: " + string.Join(", ", ign) : "")}. " +
                   (kinds.Count > 0 ? $"[codes.{p}.kind = {string.Join("; ", kinds)}] " : "") + (discs.Count > 0 ? $"[codes.{p}.disc = {string.Join("; ", discs)}] " : "") +
                   (srcs.Count > 0 ? $"[codes.{p}.source = {string.Join("; ", srcs)}] " : "") + (ign.Count > 0 ? $"[codes.{p}.ignore = {string.Join("; ", ign)}] " : "") +
                   (!string.IsNullOrWhiteSpace(template) ? $"[codes.{p}.template = {template.Trim()}]" : "");
        var b = Brain.Snapshot();
        var secs = b["sections"] as JsonArray ?? new JsonArray();
        if (b["sections"] == null) b["sections"] = secs;
        var sec = secs.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.ToString() == "filing");
        if (sec == null) { sec = new JsonObject { ["id"] = "filing", ["icon"] = "📐", ["name"] = "Filing & document codes", ["prefix"] = "FC", ["tags"] = new JsonArray("files"), ["rules"] = new JsonArray() }; secs.Add(sec); }
        var rules = sec["rules"] as JsonArray ?? new JsonArray();
        var id = "FC-" + p + "-" + DateTime.Now.ToString("yyMMddHHmm");
        rules.Add(new JsonObject { ["id"] = id, ["title"] = $"{p} codes (learned)", ["text"] = text.Trim(), ["by"] = "You · 🧠 Learn this project", ["when"] = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
        Brain.Commit(b, "You · 🧠 Learn this project", $"{p}: {answers.Count} code(s){(template != null ? " + template" : "")}");
        lock (L) { cur = null; dirCache.Clear(); }
    }

    /// <summary>How many codes are waiting for your answer (counted in the background once a day; shown in the morning brief).</summary>
    public static System.Threading.Timer Tick;
    public static int Pending { get => Store.Int("win_filing_pending", 0); set => Store.SetInt("win_filing_pending", value); }

    public static void BackgroundCount()
    {
        if (DateTime.Now - new DateTime(Store.Long("win_filing_counted", 0)) < TimeSpan.FromHours(20)) return;
        Store.SetLong("win_filing_counted", DateTime.Now.Ticks);
        Task.Run(() =>
        {
            try
            {
                int n = 0;
                foreach (var p in Projects.All())
                {
                    var files = Projects.Catalogue(p.Folders.Concat((p.Sources ?? new()).Select(s => s.Path)), 120000).Select(x => x.Path);
                    n += Learn(p.Code, files).Count(u => !Store.Bool("win_filing_skip_" + p.Code + "_" + u.Code, false));
                }
                Pending = n;
            }
            catch (Exception e) { Log.Warn("filing", "count: " + e.Message); }
        });
    }
}
