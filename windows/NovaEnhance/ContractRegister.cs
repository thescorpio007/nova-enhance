using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>Your own kind of contract document (e.g. Approved Vendor List): how Nova recognizes it and what it does in a review.</summary>
class DocType
{
    public string Name = "", Code = "", Acts = "governs", Desc = "";   // Acts: governs | check | reference
    public List<string> NameWords = new(), PageWords = new();
    public JsonObject ToJson() => new()
    {
        ["name"] = Name, ["code"] = Code, ["acts"] = Acts, ["desc"] = Desc,
        ["nw"] = new JsonArray(NameWords.Select(w => (JsonNode)w).ToArray()), ["pw"] = new JsonArray(PageWords.Select(w => (JsonNode)w).ToArray()),
    };
    public static DocType From(JsonNode j) => new()
    {
        Name = j?["name"]?.ToString() ?? "", Code = j?["code"]?.ToString() ?? "", Acts = j?["acts"]?.ToString() ?? "governs", Desc = j?["desc"]?.ToString() ?? "",
        NameWords = (j?["nw"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? new(),
        PageWords = (j?["pw"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? new(),
    };
}

/// <summary>One document of the contract register.</summary>
class RegDoc
{
    public string Path = "", Name = "", Type = "", Disc = "", Rev = "", Key = "", Status = "", By = "", How = "", Acts = "";
    public DateTime Date;
    public long Size;
    public bool Sure, Custom, Draftish, SignedLook, ForcedOld, SignedOk, Tender, Executed;
    public string Guess = "", Stamps = "", EvidenceText = "";
    public int Evidence;   // 4 🔏 contract document stamped by all parties · 3 stamped by some / executed folder · 2 number + title · 1 name only
    public (int, string) Rank;
    public bool Governing => Status.StartsWith("governs");
}

/// <summary>Something Nova could not settle by itself (⚠ Needs your eye).</summary>
class EyeItem
{
    public string Kind = "", Title = "", Detail = "";
    public List<string> Paths = new();
}

class ContractReg
{
    public string Project = "";
    public DateTime Built;
    public int Files;
    public List<RegDoc> Docs = new();
    public List<EyeItem> Eyes = new();
    public string Phase = "";
}

/// <summary>
/// 📜 The contract register: you give the contract AREAS (folders); Nova labels every DOCUMENT inside by itself — from its number and name
/// (📐 filing rules), from what it says on page 1 (title, “SECTION 08 71 00”, “Bill No. 5”, “ADDENDUM No. 3”, signed / draft) and from the
/// folder path as a clue — keeps the LATEST revision as the one that governs, re-reads new or changed files, and shows you only what it could
/// not settle (⚠ Needs your eye). Your answers and your own document types (e.g. Approved Vendor List) are kept in 🧠 Nova Brain.
/// Designed by A.HAMROUNI
/// </summary>
static class ContractRegister
{
    public const string Letters = "Letters / reference";
    public const string Submittals = "Submittals (not contract)";
    public const string Stamped = "Contract document · stamped";

    /// <summary>The built-in types, in the register's order.</summary>
    public static readonly string[] BuiltIn =
    {
        "Agreement & LoA", "PTC", "Conditions · particular", "Conditions · general", "Employer's requirements", "Specifications", "Drawings · contract", "BOQ",
        "Addenda / bulletins", Stamped, "Drawings · IFC", "Changes (EI · VO · PMI · VE · RFI)", "Programme / schedule", "Authority approvals",
        "Drawings · IFT (tender)", "Draft", Letters, Submittals, "Exclude",
    };
    static readonly HashSet<string> Never = new() { "Drawings · IFT (tender)", "Draft", "Exclude" };
    static readonly HashSet<string> Refs = new() { Letters, Submittals, "Reference only" };
    static readonly string[] Docs = { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".msg", ".dwg", ".dwf", ".rtf", ".txt", ".pptx" };
    static readonly string[] Readable = { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".rtf" };
    static readonly Regex DraftPath = new(@"(^|[\\_\s\-])(draft|_?td|for comments?|superseded|old|archive|backup)([\\_\s\-]|$)", RegexOptions.IgnoreCase);
    static readonly Regex TenderPath = new(@"(^|[\\_\s\-])(ift|tender|bidding|bid)([\\_\s\-]|$)", RegexOptions.IgnoreCase);
    static readonly Regex IfcWord = new(@"\bifc\b|issued for construction", RegexOptions.IgnoreCase);
    static readonly Regex Pictures = new(@"\.(jpe?g|png|gif|bmp|tiff?|heic|mp4|mov|avi|wmv|zip|rar|7z|bak|tmp|db|ini|lnk)$", RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------ settings (🧠 Nova Brain → 📜 Contract area · cfg)

    /// <summary>The contract areas: the folders you gave. Before build 109: the folders of the folder table (except Exclude).</summary>
    public static List<string> Areas(string project)
    {
        var cfg = ContractArea.Cfg(project);
        if (cfg["areas"] is JsonArray a && a.Count > 0) return a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
        return ContractArea.Of(project).Where(e => e.Role != "Exclude").Select(e => e.Path).ToList();
    }
    public static List<string> Excluded(string project) =>
        ContractArea.Of(project).Where(e => e.Role == "Exclude").Select(e => e.Path.TrimEnd('\\'))
            .Concat((ContractArea.Cfg(project)["exclude"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "")).Where(x => x.Length > 0).ToList();
    public static List<DocType> Types(string project) => (ContractArea.Cfg(project)["types"] as JsonArray ?? new JsonArray()).Select(DocType.From).Where(t => t.Name.Length > 0).ToList();
    static Dictionary<string, string> Decisions(string project)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ContractArea.Cfg(project)["decide"] is JsonObject o) foreach (var (k, v) in o) d[k] = v?.ToString() ?? "";
        return d;
    }

    public static void SetAreas(string project, List<string> areas)
    {
        var c = ContractArea.Cfg(project);
        c["areas"] = new JsonArray(areas.Distinct(StringComparer.OrdinalIgnoreCase).Select(a => (JsonNode)a).ToArray());
        ContractArea.SaveCfg(project, c);
        Invalidate(project);
    }
    public static void SaveType(string project, DocType t, string oldName = null)
    {
        var c = ContractArea.Cfg(project);
        var list = Types(project);
        list.RemoveAll(x => x.Name.Equals(oldName ?? t.Name, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
        list.Add(t);
        c["types"] = new JsonArray(list.Select(x => (JsonNode)x.ToJson()).ToArray());
        var order = ContractArea.Order(project).ToList();
        if (oldName != null && oldName != t.Name) for (int i = 0; i < order.Count; i++) if (Bare(order[i]) == oldName) order[i] = order[i].StartsWith("=") ? "=" + t.Name : t.Name;
        ContractArea.Save(project, ContractArea.Of(project), order, ContractArea.OrderClause(project), c);
        Invalidate(project);
    }
    public static void RemoveType(string project, string name)
    {
        var c = ContractArea.Cfg(project);
        c["types"] = new JsonArray(Types(project).Where(x => x.Name != name).Select(x => (JsonNode)x.ToJson()).ToArray());
        var order = ContractArea.Order(project).Where(o => Bare(o) != name).ToList();
        ContractArea.Save(project, ContractArea.Of(project), order, ContractArea.OrderClause(project), c);
        Invalidate(project);
    }
    /// <summary>Your answer for one file (a type, “governs”, “never”…): kept, so Nova never asks it again.</summary>
    public static void Decide(string project, IEnumerable<string> paths, string answer)
    {
        var c = ContractArea.Cfg(project);
        var d = c["decide"] as JsonObject ?? new JsonObject();
        foreach (var p in paths) d[p] = answer;
        c["decide"] = d;
        ContractArea.SaveCfg(project, c);
        Invalidate(project);
    }

    // ------------------------------------------------------------------ the order of precedence, with “= same level”

    public static string Bare(string o) => o.TrimStart('=', ' ');
    /// <summary>The level of a type in the order (1 = highest; “=X” shares the level of the one above). 0 = not ranked.</summary>
    public static int Level(string project, string type)
    {
        int lvl = 0;
        foreach (var o in ContractArea.Order(project))
        {
            if (!o.StartsWith("=") || lvl == 0) lvl++;
            if (Bare(o) == type) return lvl;
        }
        return 0;
    }
    public static List<(int Level, string Type, bool Same)> Levels(string project)
    {
        var res = new List<(int, string, bool)>(); int lvl = 0;
        foreach (var o in ContractArea.Order(project)) { var same = o.StartsWith("=") && lvl > 0; if (!same) lvl++; res.Add((lvl, Bare(o), same)); }
        return res;
    }

    // ------------------------------------------------------------------ the register (built in the background, kept in memory and on disk)

    static readonly Dictionary<string, ContractReg> regs = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> building = new(StringComparer.OrdinalIgnoreCase);
    static readonly object L = new();
    public static event Action<string> Updated;

    static string FilePath(string project) => Path.Combine(Paths.Cache, "contract-register-" + Regex.Replace(project, @"[^\w\-]", "") + ".json");

    public static void Invalidate(string project) { lock (L) if (regs.TryGetValue(project, out var r)) r.Built = DateTime.MinValue; Refresh(project); }

    /// <summary>The register now (from memory or disk); a new one is built in the background when it is older than 30 minutes.</summary>
    public static ContractReg Get(string project, bool refresh = true)
    {
        ContractReg r;
        lock (L) regs.TryGetValue(project, out r);
        if (r == null) { r = LoadDisk(project); if (r != null) lock (L) regs[project] = r; }
        if (refresh && (r == null || DateTime.Now - r.Built > TimeSpan.FromMinutes(30))) Refresh(project);
        return r;
    }
    public static bool Building(string project) { lock (L) return building.Contains(project); }

    public static void Refresh(string project)
    {
        lock (L) { if (building.Contains(project)) return; building.Add(project); }
        Task.Run(() =>
        {
            try
            {
                var r = Build(project, CancellationToken.None);
                lock (L) regs[project] = r;
                SaveDisk(r);
                Log.Write($"📜 contract register {project}: {r.Docs.Count(d => d.Governing)} governing of {r.Files} files · {r.Eyes.Sum(e => e.Paths.Count)} to check");
            }
            catch (Exception e) { Log.Warn("contract register", project + ": " + e.Message); }
            finally { lock (L) building.Remove(project); try { Updated?.Invoke(project); } catch { } }
        });
    }

    static DateTime ticked = DateTime.MinValue;
    /// <summary>Every 30 minutes (from the minute check): registers of projects with contract areas are labelled again in the background
    /// — new or changed files in the areas get their type, a new revision becomes the one that governs.</summary>
    public static void Tick()
    {
        if (DateTime.Now - ticked < TimeSpan.FromMinutes(30)) return;
        ticked = DateTime.Now;
        Task.Run(() =>
        {
            try { foreach (var p in Projects.All().Select(x => x.Code)) if (Areas(p).Count > 0) Get(p); }
            catch (Exception e) { Log.Warn("contract register", e.Message); }
        });
    }

    static readonly Dictionary<string, string> Status_ = new();
    public static string Phase(string project) { lock (Status_) return Status_.GetValueOrDefault(project, ""); }
    static void Say(string project, string s) { lock (Status_) Status_[project] = s; }

    /// <summary>Labels every file of the contract areas (the 📇 names list + what Nova already read inside) — no disk walk, no AI.</summary>
    public static ContractReg Build(string project, CancellationToken ct)
    {
        var reg = new ContractReg { Project = project, Built = DateTime.Now };
        var areas = Areas(project);
        if (areas.Count == 0) return reg;
        var excl = Excluded(project);
        var types = Types(project);
        var decide = Decisions(project);
        var cfg = ContractArea.Cfg(project);
        var folderRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cfg["folders"] is JsonObject fo) foreach (var (k, v) in fo) folderRules[k] = v?.ToString() ?? "";
        Say(project, "📇 listing the documents of the contract areas…");
        List<Projects.FileRow> files;
        try { files = Projects.Catalogue(areas.Concat(folderRules.Keys), 400_000); } catch { files = new(); }
        // 📌 files you chose outside the areas (“use only this file”)
        foreach (var x in (cfg["extra"] as JsonArray ?? new JsonArray()).Select(e => e?.ToString() ?? "").Where(e => e.Length > 0))
            try { var fi = new FileInfo(x); if (fi.Exists) files.Add(new Projects.FileRow { Path = x, Name = fi.Name, Size = fi.Length, Modified = fi.LastWriteTime }); } catch { }
        files = files.Where(f => !excl.Any(x => f.Path.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase)) && !Projects.Private.IsMatch(f.Path) && !Pictures.IsMatch(f.Name))
                     .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        reg.Files = files.Count;
        // 📐 the labels come from the shared Nova index (worked out once, saved for every feature)
        var labels = NovaIndex.Labels(areas.Concat(folderRules.Keys), files, ct, s => Say(project, s));
        int n = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (++n % 500 == 0) Say(project, $"🏷 labelling {n:N0} of {files.Count:N0}…");
            reg.Docs.Add(Label(project, f, types, decide, areas, labels.GetValueOrDefault(f.Path), folderRules));
        }
        ApplyStamps(project, reg, decide, ct);
        Say(project, "⚖ finding the latest revisions…");
        Supersede(reg);
        // 📋 check lists (vendor lists…): their rows go into the index
        foreach (var d in reg.Docs.Where(d => d.Governing && d.Custom && d.Acts == "check"))
        {
            ct.ThrowIfCancellationRequested();
            Say(project, "📋 reading the rows of " + d.Name + "…");
            NovaIndex.IndexVendors(project, d);
        }
        Eyes(project, reg, decide);
        Say(project, "");
        return reg;
    }

    /// <summary>The type of one file: your answer › your types › page 1 › the 📐 filing labels › the name › the folder path.</summary>
    public static RegDoc Label(string project, Projects.FileRow f, List<DocType> types, Dictionary<string, string> decide, List<string> areas = null,
                               FileLabels lbIn = null, Dictionary<string, string> folderRules = null)
    {
        areas ??= Areas(project);
        var d = new RegDoc { Path = f.Path, Name = f.Name, Size = f.Size, Date = f.Modified };
        var ext = Path.GetExtension(f.Name).ToLowerInvariant();
        FileLabels lb = lbIn;
        if (lb == null) try { lb = Filing.Classify(f.Path); } catch { lb = new FileLabels(); }
        d.Disc = Short(lb.Disc); d.Rev = lb.Rev;
        try { var k = Revisions.Parse(f.Path, f.Modified.Ticks); d.Key = k.Key; d.Rank = k.Rank; if (d.Rev.Length == 0) d.Rev = k.Rev; } catch { d.Key = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant(); }
        if (lb.Number.Length > 0) d.Key = lb.Number.ToLowerInvariant();
        var dir = Path.GetDirectoryName(f.Path) ?? "";
        var area = areas.FirstOrDefault(a => f.Path.StartsWith(a.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) ?? "";
        var rel = area.Length > 0 && dir.Length > area.TrimEnd('\\').Length ? dir[(area.TrimEnd('\\').Length + 1)..] : "";
        decide.TryGetValue(f.Path, out var ans);
        ans ??= "";
        if (ans == "!old") d.ForcedOld = true;
        if (ans == "!signed") d.SignedOk = true;
        d.Draftish = !d.SignedOk && (DraftPath.IsMatch("\\" + rel + "\\") || DraftPath.IsMatch(Path.GetFileNameWithoutExtension(f.Name)));
        d.Executed = ExecPath.IsMatch(rel);
        d.Tender = !d.Executed && TenderPath.IsMatch("\\" + rel + "\\") && !IfcWord.IsMatch(rel);
        if (d.Executed) { d.Evidence = 3; d.EvidenceText = "folder “" + Clip(ExecPath.Match(rel).Value, 30) + "”"; }

        // 1 · your answer
        if (ans.Length > 0 && !ans.StartsWith("!"))
        {
            d.Type = ans; d.Sure = true; d.How = "✓ your answer"; d.Custom = types.Any(t => t.Name == ans);
            d.Acts = types.FirstOrDefault(t => t.Name == ans)?.Acts ?? "";
            d.Tender = false; d.Draftish = false;
            return d;
        }
        // 1b · your folder (everything inside = this type, also new files later)
        if (folderRules != null)
            foreach (var (fold, ft) in folderRules)
                if (f.Path.StartsWith(fold.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    d.Type = ft; d.Sure = true; d.How = "📁 your folder “" + Path.GetFileName(fold.TrimEnd('\\')) + "”"; d.Custom = types.Any(t => t.Name == ft);
                    d.Acts = types.FirstOrDefault(t => t.Name == ft)?.Acts ?? "";
                    d.Tender = false; d.Draftish = false;
                    return d;
                }
        var notType = ans.StartsWith("!not:") ? ans[5..] : "";
        var page = Readable.Contains(ext) ? FolderIndex.FirstText(f.Path, 1500) : "";
        if (page.Length > 0 && ContractArea.Signed.IsMatch(page)) d.SignedLook = true;
        var stem = Path.GetFileNameWithoutExtension(f.Name);
        var nStem = Norm(stem); var nPage = page.Length > 0 ? Norm(page) : ""; var nRel = Norm(rel);

        // 2 · your own types — loose: “vendor list” = “Vendors_List” = “vendors-list”; the type's name counts too; then the folder names
        foreach (var t in types.Where(t => t.Name != notType))
        {
            var words = t.NameWords.Append(t.Name).Select(Norm).Where(w => w.Length > 1).Distinct().ToList();
            var nw = words.FirstOrDefault(w => Has(nStem, w));
            var code = t.Code.Length > 0 && Regex.IsMatch(stem, @"(^|[\-_\s])" + Regex.Escape(t.Code) + @"([\-_\s]|$)", RegexOptions.IgnoreCase);
            var pw = nPage.Length > 0 ? t.PageWords.FirstOrDefault(w => Has(nPage, Norm(w))) : null;
            var fw = nw == null && pw == null && !code ? words.FirstOrDefault(w => Has(nRel, w)) : null;
            if (nw != null || code || pw != null || fw != null)
            {
                d.Type = t.Name; d.Custom = true; d.Acts = t.Acts; d.Sure = true;
                d.How = pw != null ? $"📄 p.1 “{pw}”" : code ? $"code “{t.Code}” in the name" : nw != null ? $"name “{nw}”" : $"folder “{fw}”";
                break;
            }
        }

        // 3 · page 1 (the title the document gives itself)
        if (d.Type.Length == 0 && page.Length > 0)
        {
            foreach (var (rx, role, w, tag) in ContractArea.PageRules)
            {
                var m = rx.Match(page);
                if (!m.Success || w < 4) continue;
                if (role == "Draft" && lb.Kind is "Specification" or "BOQ") continue;
                d.Type = role; d.Sure = true; d.How = $"📄 p.1 “{Clip(m.Value)}”";
                break;
            }
        }

        // 4 · the 📐 filing labels (document number, kind, stage)
        if (d.Type.Length == 0)
        {
            var kindType = lb.Kind switch
            {
                "Specification" => "Specifications",
                "BOQ" => "BOQ",
                "Engineer's instruction" or "Variation" or "Site instruction" or "Request for information (RFI)" => "Changes (EI · VO · PMI · VE · RFI)",
                "Letter" or "Transmittal" or "Minutes" or "Email" or "Memo" or "Report" or "Daily report" or "Payment" or "Register" or "Presentation" or "Calculation" => Letters,
                "Shop drawing" or "Material submittal" or "Prequalification" or "Inspection request" or "Site MEP inspection clearance" or "Method statement" or "Non-conformance (NCR)" or "Site observation report (SOR)" => Submittals,
                "Model / CAD" or "Sketch" => Letters,
                "Design drawing" => lb.Stage.StartsWith("IFC") || lb.Stage == "As-built" ? "Drawings · IFC" : lb.Stage == "Contract (executed)" ? "Drawings · contract" : lb.Stage is "IFT" or "Tender" ? "Drawings · IFT (tender)" : "",
                _ => "",
            };
            if (kindType.Length > 0)
            {
                d.Type = kindType; d.Sure = lb.Number.Length > 0 || lb.Kind != "Design drawing" || lb.Stage.Length > 0;
                d.How = (lb.Number.Length > 0 ? $"number {lb.Number}" : "name") + $" → {lb.Kind}{(lb.Stage.Length > 0 ? " · " + lb.Stage : "")}";
                if (lb.Kind == "Design drawing" && IfcWord.IsMatch(page)) { d.How += " + title block “ISSUED FOR CONSTRUCTION”"; d.Sure = true; }
            }
            else if (lb.Kind == "Design drawing")
            {
                // a drawing without a stage: the folder decides (IFC / tender / draft), else IFC but please confirm
                d.Type = IfcWord.IsMatch(rel + " " + stem + " " + page) ? "Drawings · IFC" : TenderPath.IsMatch("\\" + rel + "\\") ? "Drawings · IFT (tender)" : "Drawings · IFC";
                d.Sure = IfcWord.IsMatch(rel + " " + stem + " " + page) || TenderPath.IsMatch("\\" + rel + "\\");
                d.How = "drawing · folder “" + Clip(rel, 40) + "”";
            }
        }

        // 5 · the words of the name
        if (d.Type.Length == 0)
        {
            var best = ContractArea.NameRules.Where(r => r.Rx.IsMatch(stem)).OrderByDescending(r => r.W).FirstOrDefault();
            if (best.Rx != null)
            {
                d.Type = best.Role == "Reference only" ? Letters : best.Role;
                d.Sure = best.W >= 3; d.How = $"name “{Clip(best.Rx.Match(stem).Value)}”";
            }
        }

        // 6 · the folder path (a clue only)
        if (d.Type.Length == 0)
        {
            var best = ContractArea.NameRules.Where(r => r.Rx.IsMatch(rel)).OrderByDescending(r => r.W).FirstOrDefault();
            if (best.Rx != null) { d.Type = best.Role == "Reference only" ? Letters : best.Role; d.Sure = false; d.How = $"folder “{Clip(best.Rx.Match(rel).Value)}”"; }
        }
        if (d.Type.Length == 0) { d.Type = Docs.Contains(ext) ? "" : Letters; d.Sure = false; d.How = d.Type.Length == 0 ? "no number, no clear title" : "not a document"; }
        d.Guess = d.Type;
        if (d.Evidence < 2 && lb.Number.Length > 0 && d.Sure) { d.Evidence = 2; d.EvidenceText = "number " + lb.Number; }
        if (d.Evidence < 1) { d.Evidence = 1; d.EvidenceText = d.Sure ? "name / page 1" : "name only"; }

        // a draft folder never makes a signed, numbered IFC / contract document a draft — but a draft-looking path wins over a weak guess
        if (d.Draftish && !d.Sure && d.Type != Letters) { d.Type = "Draft"; d.How += " · in a draft folder"; }
        else if (d.Draftish && d.Sure && d.Type is not ("Draft" or "Drawings · IFT (tender)") && TenderOrDraftFolder(rel) && !IfcWord.IsMatch(stem)) { d.Type = "Draft"; d.How += " · draft folder “" + Clip(rel, 40) + "”"; }
        return d;
    }

    static readonly Regex ExecPath = new(@"executed|stamped|signed contract|contract documents? stamped", RegexOptions.IgnoreCase);

    /// <summary>Loose form for matching: lower case, _ - . and spaces as one space, plural “s” dropped (vendors list = vendor_list).</summary>
    public static string Norm(string s)
    {
        s = Regex.Replace((s ?? "").ToLowerInvariant(), @"[_\-\.\s/\\]+", " ").Trim();
        return Regex.Replace(s, @"(?<=\p{L}{3})s\b", "");
    }
    static bool Has(string hay, string w) => w.Length > 1 && (" " + hay + " ").Contains(" " + w + " ") || w.Length > 4 && hay.Contains(w);

    static bool TenderOrDraftFolder(string rel) => Regex.IsMatch(rel, @"(^|[\\_\s\-])(draft|_?td)([\\_\s\-]|$)", RegexOptions.IgnoreCase);
    static string Clip(string s, int n = 50) { s = Regex.Replace(s ?? "", @"\s+", " ").Trim(); return s.Length > n ? s[..n] + "…" : s; }
    static string Short(string disc) => disc switch
    {
        "Architecture" => "AR", "Interior design" => "ID", "Structure" => "ST", "Landscape" => "LS", "MEP" => "MEP", "Mechanical services" => "ME", "Electrical services" => "EL",
        "Plumbing" => "PL", "Fire & life safety" => "FLS", "Façade" => "FA", "Lighting" => "LT", "Signage" => "SG", "Civil works" => "CV", "" => "", _ => disc.Length > 4 ? disc[..4] : disc,
    };

    // ------------------------------------------------------------------ 🔏 stamps (the strongest evidence)

    public static readonly (string Role, string Label)[] StampRolesList =
        { ("contract", "🔏 Contract document"), ("client", "🏛 Client"), ("contractor", "🏗 Contractor"), ("consultant", "📐 Consultant"), ("other", "Other stamp"), ("ignore", "Not a stamp (logo…)") };

    /// <summary>Your named stamps: fingerprint → (role, name). Kept in 🧠 Nova Brain with the project.</summary>
    public static Dictionary<string, (string Role, string Name)> StampRoles(string project)
    {
        var d = new Dictionary<string, (string, string)>();
        if (ContractArea.Cfg(project)["stamps"] is JsonObject o)
            foreach (var (k, v) in o) d[k] = (v?["role"]?.ToString() ?? "", v?["name"]?.ToString() ?? "");
        return d;
    }
    public static void SaveStamps(string project, Dictionary<string, (string Role, string Name)> roles)
    {
        var c = ContractArea.Cfg(project);
        var o = new JsonObject();
        foreach (var (k, v) in roles) o[k] = new JsonObject { ["role"] = v.Role, ["name"] = v.Name };
        c["stamps"] = o;
        ContractArea.SaveCfg(project, c);
        Invalidate(project);
    }

    /// <summary>Reads the stamps of the contract-area PDFs (fingerprints of the pictures on page 1 and the last page, saved in the index):
    /// “Contract document” + every party → 🔏 governs, even without a number or in a draft / tender folder. Up to 3,000 new PDFs per pass.</summary>
    static void ApplyStamps(string project, ContractReg reg, Dictionary<string, string> decide, CancellationToken ct)
    {
        var roles = StampRoles(project);
        if (roles.Count == 0) return;
        var parties = roles.Values.Select(v => v.Role).Where(r => r is "client" or "contractor" or "consultant").Distinct().ToList();
        int budget = 3000, n = 0;
        foreach (var d in reg.Docs.Where(d => d.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && d.Type != Submittals))
        {
            ct.ThrowIfCancellationRequested();
            var st = NovaIndex.Stamps(d.Path, d.Size, d.Date.Ticks, false);
            if (st == null && budget > 0) { budget--; if (++n % 100 == 0) Say(project, $"🔏 reading the stamps of {n:N0} documents…"); st = NovaIndex.Stamps(d.Path, d.Size, d.Date.Ticks, true); }
            if (st == null) continue;
            var found = st.Where(roles.ContainsKey).Select(h => roles[h]).Where(r => r.Role is not ("ignore" or "other")).ToList();
            if (found.Count == 0) continue;
            var has = found.Select(f => f.Role).Distinct().ToList();
            var all = has.Contains("contract") && parties.All(has.Contains) || decide.GetValueOrDefault(d.Path) == "!valid";
            d.Stamps = string.Join(" · ", found.GroupBy(f => f.Role).Select(g => g.First().Name.Length > 0 ? g.First().Name : StampRolesList.First(x => x.Role == g.Key).Label));
            d.Evidence = all ? 4 : Math.Max(d.Evidence, 3);
            d.EvidenceText = all ? "🔏 contract document · stamped by all parties" : "🔏 stamped: " + d.Stamps;
            if (all)
            {
                d.Tender = false; d.Draftish = false; d.Sure = true;
                if (d.Type is "" or "Draft" or "Drawings · IFT (tender)" or Letters or "Exclude")
                    d.Type = d.Guess is "" or "Draft" or "Drawings · IFT (tender)" or Letters or "Exclude" ? Stamped : d.Guess;
                d.How = "🔏 stamps · " + d.How;
            }
        }
    }

    /// <summary>Same document (number) → only the latest revision governs; the others are “superseded by …”.</summary>
    static void Supersede(ContractReg reg)
    {
        foreach (var d in reg.Docs)
            d.Status = d.Type.Length == 0 ? "unclear" : Never.Contains(d.Type) || d.Tender && d.Evidence < 4 ? "never governs" : Refs.Contains(d.Type) || d.Acts == "reference" ? "reference" : d.Acts == "check" ? "governs · check list" : "governs";
        foreach (var d in reg.Docs.Where(d => d.ForcedOld && d.Governing)) { d.Status = "superseded"; d.By = "the copy you chose"; }
        foreach (var g in reg.Docs.Where(d => d.Governing && d.Key.Length > 3).GroupBy(d => d.Type + "|" + d.Key))
        {
            var list = g.OrderByDescending(d => d.Rank.Item1).ThenByDescending(d => d.Rank.Item2, StringComparer.Ordinal).ThenByDescending(d => d.Evidence).ThenByDescending(d => d.Date).ToList();
            var top = list[0];
            foreach (var d in list.Skip(1))
            {
                if (d.Rank == top.Rank && d.Rev == top.Rev) continue;   // a copy of the same revision: settled under ⚠ Needs your eye
                d.Status = "superseded"; d.By = top.Rev.Length > 0 ? top.Rev : top.Date.ToString("dd MMM yyyy");
            }
        }
    }

    static void Eyes(string project, ContractReg reg, Dictionary<string, string> decide)
    {
        bool Asked(string p) => decide.ContainsKey(p);
        // 🔍 scans Windows OCR could not read
        var scans = reg.Docs.Where(d => (d.Governing || d.Status == "unclear") && d.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !Asked(d.Path) && Ocr.IsBad(d.Path)).ToList();
        if (scans.Count > 0) reg.Eyes.Add(new EyeItem { Kind = "scan", Title = $"🔍 Scanned — could not be read ({scans.Count})", Detail = "Windows OCR gave no answer on these pages. If they are contract documents, Nova can't compare against them until they are read.", Paths = scans.Select(d => d.Path).ToList() });
        // ⚖ two “latest” copies of the same document and revision
        var dups = reg.Docs.Where(d => d.Governing && d.Key.Length > 3).GroupBy(d => d.Type + "|" + d.Key)
            .Select(g => g.Where(d => d.Status != "superseded").ToList()).Where(l => l.Count > 1 && l.Select(d => d.Size).Distinct().Count() > 1 && !l.Any(d => Asked(d.Path))).ToList();
        if (dups.Count > 0) reg.Eyes.Add(new EyeItem { Kind = "dup", Title = $"⚖ Two “latest” copies of the same document ({dups.Count})", Detail = "Same number and revision, different file dates or sizes. Choose the one that governs; the other becomes “superseded”.", Paths = dups.SelectMany(l => l.OrderByDescending(d => d.Date).Select(d => d.Path)).ToList() });
        // 🔏 stamped by some parties only (a contract document should carry every party's stamp)
        var partial = reg.Docs.Where(d => d.Governing && d.Evidence == 3 && d.Stamps.Length > 0 && !Asked(d.Path)).ToList();
        if (partial.Count > 0) reg.Eyes.Add(new EyeItem { Kind = "stamps", Title = $"🔏 Not stamped by all parties ({partial.Count})", Detail = "These carry some stamps but not “Contract Document” with every party (Client · Contractor · Consultant). Is the document valid?", Paths = partial.Select(d => d.Path).ToList() });
        // ✍ in a draft folder but looks signed
        var drafts = reg.Docs.Where(d => d.Type == "Draft" && d.SignedLook && !Asked(d.Path)).ToList();
        if (drafts.Count > 0) reg.Eyes.Add(new EyeItem { Kind = "draft", Title = $"✍ Draft or signed? ({drafts.Count})", Detail = "The folder says draft, but page 1 has a signature block or a “contract document” stamp.", Paths = drafts.Select(d => d.Path).ToList() });
        // ❔ type unclear (documents only)
        var unclear = reg.Docs.Where(d => (d.Status == "unclear" || (!d.Sure && d.Governing)) && Readable.Contains(Path.GetExtension(d.Name).ToLowerInvariant()) && !Asked(d.Path)).ToList();
        if (unclear.Count > 0) reg.Eyes.Add(new EyeItem { Kind = "unclear", Title = $"❔ Type unclear ({unclear.Count})", Detail = "No document number and no clear title on page 1 (or only the folder gave a clue). Set the type, or let the AI look at the first 1,500 characters.", Paths = unclear.Select(d => d.Path).ToList() });
    }

    // ------------------------------------------------------------------ ✦ with your consent: Gemini OCR for contract scans, the AI for unclear types

    /// <summary>Reads scanned contract PDFs with Gemini vision (page images — the user agreed in the dialog), up to 60 pages each, and keeps the text.</summary>
    public static int ReadScans(List<string> paths, Action<string> say, CancellationToken ct)
    {
        int pages = 0;
        foreach (var path in paths.Where(p => !Projects.Private.IsMatch(p)))
        {
            ct.ThrowIfCancellationRequested();
            int n = 1;
            try { using var doc = UglyToad.PdfPig.PdfDocument.Open(path); n = doc.NumberOfPages; } catch { }
            var segs = new List<Seg>();
            for (int i = 1; i <= Math.Min(60, n); i++)
            {
                ct.ThrowIfCancellationRequested();
                say?.Invoke($"✦ Gemini OCR · {Path.GetFileName(path)} · page {i} of {Math.Min(60, n)}");
                try
                {
                    var (data, mime) = Ocr.Picture(path, i);
                    var text = Ai.Vision(data, mime, ct).GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(text)) { segs.Add(new Seg { Loc = $"page {i} (Gemini vision)", Text = text, Goto = $"page:{i}" }); pages++; }
                }
                catch (Exception e) { say?.Invoke("⚠ " + e.Message); if (Ai.QuotaOut) break; }
            }
            if (segs.Count > 0) FolderIndex.Remember(path, segs);
        }
        foreach (var p in Projects.All().Select(x => x.Code)) Invalidate(p);
        return pages;
    }

    /// <summary>The type of unclear documents from their name + first 1,500 characters (never whole documents).</summary>
    public static async Task<int> AskAi(string project, List<string> paths, Action<string> say, CancellationToken ct)
    {
        var names = BuiltIn.Concat(Types(project).Select(t => t.Name)).ToList();
        var done = new Dictionary<string, string>();
        foreach (var path in paths.Where(p => !Projects.Private.IsMatch(p)))
        {
            ct.ThrowIfCancellationRequested();
            if (Ai.QuotaOut) break;
            var first = FolderIndex.FirstText(path, 1500);
            say?.Invoke("✦ Asking the AI about " + Path.GetFileName(path) + "…");
            var sys = "You label one document of a construction project's contract area. Reply JSON only: {\"type\":\"<exactly one of: " + string.Join(" | ", names) + ">\"}";
            var user = $"Project {project}. File: {Path.GetFileName(path)} · folder: {Path.GetFileName(Path.GetDirectoryName(path))}\nFirst characters:\n{first}";
            try
            {
                var j = Ai.ParseJson(await SmartSearch.CloudChat(sys, user, 12, ct));
                var t = j["type"]?.ToString() ?? "";
                if (names.Contains(t)) done[path] = t;
            }
            catch (Exception e) { say?.Invoke("⚠ AI: " + e.Message); }
        }
        if (done.Count > 0)
        {
            var c = ContractArea.Cfg(project);
            var d = c["decide"] as JsonObject ?? new JsonObject();
            foreach (var (p, t) in done) d[p] = t;
            c["decide"] = d;
            Program.UiContext.Post(_ => { try { ContractArea.SaveCfg(project, c); Invalidate(project); } catch (Exception e) { Log.Warn("contract register", e.Message); } }, null);
        }
        return done.Count;
    }

    // ------------------------------------------------------------------ disk

    static JsonObject DocJson(RegDoc d) => new()
    {
        ["t"] = d.Type, ["di"] = d.Disc, ["r"] = d.Rev, ["k"] = d.Key, ["s"] = d.Status, ["b"] = d.By, ["h"] = d.How, ["a"] = d.Acts,
        ["d"] = d.Date.Ticks, ["z"] = d.Size, ["su"] = d.Sure, ["c"] = d.Custom, ["r1"] = d.Rank.Item1, ["r2"] = d.Rank.Item2,
        ["ev"] = d.Evidence, ["et"] = d.EvidenceText, ["st"] = d.Stamps,
    };
    static RegDoc FromJson(string path, JsonNode x) => new()
    {
        Path = path, Name = Path.GetFileName(path),
        Type = x?["t"]?.ToString() ?? "", Disc = x?["di"]?.ToString() ?? "", Rev = x?["r"]?.ToString() ?? "", Key = x?["k"]?.ToString() ?? "",
        Status = x?["s"]?.ToString() ?? "", By = x?["b"]?.ToString() ?? "", How = x?["h"]?.ToString() ?? "", Acts = x?["a"]?.ToString() ?? "",
        Date = new DateTime(x?["d"]?.GetValue<long>() ?? 0), Size = x?["z"]?.GetValue<long>() ?? 0, Sure = x?["su"]?.GetValue<bool>() ?? false, Custom = x?["c"]?.GetValue<bool>() ?? false,
        Rank = (x?["r1"]?.GetValue<int>() ?? 0, x?["r2"]?.ToString() ?? ""),
        Evidence = x?["ev"]?.GetValue<int>() ?? 1, EvidenceText = x?["et"]?.ToString() ?? "", Stamps = x?["st"]?.ToString() ?? "",
    };

    /// <summary>The register goes into the shared 🗄 Nova index (every feature can read it), not a file of its own.</summary>
    static void SaveDisk(ContractReg r)
    {
        try
        {
            using var c = FolderIndex.Open();
            using var tx = FolderIndex.BeginWait(c, CancellationToken.None);
            FolderIndex.Exec(c, "DELETE FROM creg WHERE project=$j", ("$j", r.Project));
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO creg(project,path,j) VALUES($j,$p,$x)";
                cmd.Parameters.AddWithValue("$j", r.Project);
                var pp = cmd.Parameters.Add("$p", SqliteType.Text); var px = cmd.Parameters.Add("$x", SqliteType.Text);
                foreach (var d in r.Docs) { pp.Value = d.Path; px.Value = DocJson(d).ToJsonString(); cmd.ExecuteNonQuery(); }
            }
            var eyes = new JsonArray(r.Eyes.Select(e => (JsonNode)new JsonObject { ["k"] = e.Kind, ["t"] = e.Title, ["d"] = e.Detail, ["p"] = new JsonArray(e.Paths.Select(p => (JsonNode)p).ToArray()) }).ToArray());
            FolderIndex.Exec(c, "INSERT OR REPLACE INTO cregmeta(project,built,files,eyes) VALUES($j,$b,$f,$e)", ("$j", r.Project), ("$b", r.Built.Ticks), ("$f", r.Files), ("$e", eyes.ToJsonString()));
            tx.Commit();
            try { File.Delete(FilePath(r.Project)); } catch { }
        }
        catch (Exception e) { Log.Warn("contract register", "save: " + e.Message); }
    }

    static ContractReg LoadDisk(string project)
    {
        try
        {
            using var c = FolderIndex.Open();
            using var m = c.CreateCommand();
            m.CommandText = "SELECT built,files,eyes FROM cregmeta WHERE project=$j";
            m.Parameters.AddWithValue("$j", project);
            ContractReg r = null;
            using (var mr = m.ExecuteReader())
                if (mr.Read())
                {
                    r = new ContractReg { Project = project, Built = new DateTime(mr.GetInt64(0)), Files = mr.GetInt32(1) };
                    foreach (var e in JsonNode.Parse(mr.GetString(2)) as JsonArray ?? new JsonArray())
                        r.Eyes.Add(new EyeItem { Kind = e?["k"]?.ToString() ?? "", Title = e?["t"]?.ToString() ?? "", Detail = e?["d"]?.ToString() ?? "", Paths = (e?["p"] as JsonArray)?.Select(p => p?.ToString() ?? "").ToList() ?? new() });
                }
            if (r == null) return null;
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path,j FROM creg WHERE project=$j";
            cmd.Parameters.AddWithValue("$j", project);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) try { r.Docs.Add(FromJson(rd.GetString(0), JsonNode.Parse(rd.GetString(1)))); } catch { }
            return r;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ ✦ for a new type: the words Nova proposes, and what it finds now

    public static DocType Suggest(string name)
    {
        var t = new DocType { Name = name.Trim() };
        var n = name.ToLowerInvariant();
        var words = new List<string> { n };
        if (n.Contains("vendor") || n.Contains("manufactur") || n.Contains("supplier"))
        { words.AddRange(new[] { "vendors list", "vendor list", "avl", "approved manufacturers", "approved suppliers", "approved vendors" }); t.Code = "AVL"; t.Acts = "check"; t.PageWords.AddRange(new[] { "List of Manufacturers", "List of Acceptable Vendors", "Approved Vendor List", "Approved Suppliers" }); t.Desc = "Approved manufacturers per spec section. A submittal with a brand not listed needs a substitution request."; }
        else if (n.Contains("finish")) { words.AddRange(new[] { "finishes schedule", "finish schedule", "schedule of finishes" }); t.Acts = "check"; t.PageWords.Add("Schedule of Finishes"); }
        else if (n.Contains("room data")) { words.AddRange(new[] { "room data sheet", "rds" }); t.Code = "RDS"; t.PageWords.Add("Room Data Sheet"); }
        else if (n.Contains("authority")) { words.AddRange(new[] { "authority", "adcd", "municipality", "civil defence", "noc" }); t.PageWords.Add("Civil Defence"); }
        else if (n.Contains("basis of design") || n.Contains("design criteria")) { words.AddRange(new[] { "basis of design", "bod", "design criteria" }); t.PageWords.Add("Basis of Design"); }
        else t.PageWords.Add(name.Trim());
        t.NameWords = words.Distinct().ToList();
        return t;
    }

    public static List<RegDoc> Preview(string project, DocType t)
    {
        var res = new List<RegDoc>();
        List<Projects.FileRow> files;
        var areas = Areas(project);
        try { files = Projects.Catalogue(areas, 400_000); } catch { return res; }
        foreach (var f in files)
        {
            var stem = Path.GetFileNameWithoutExtension(f.Name);
            var nStem = Norm(stem); var nDir = Norm(Path.GetDirectoryName(f.Path) ?? "");
            var words = t.NameWords.Append(t.Name).Select(Norm).Where(w => w.Length > 1).Distinct().ToList();
            if (!words.Any(w => Has(nStem, w) || Has(nDir, w)) && !(t.Code.Length > 0 && Regex.IsMatch(stem, @"(^|[\-_\s])" + Regex.Escape(t.Code) + @"([\-_\s]|$)", RegexOptions.IgnoreCase))) continue;
            res.Add(Label(project, f, new List<DocType> { t }, new(), areas));
            if (res.Count > 60) break;
        }
        var reg = new ContractReg { Docs = res };
        try { ApplyStamps(project, reg, new(), CancellationToken.None); } catch { }
        Supersede(reg);
        return res;
    }

    // ------------------------------------------------------------------ 🧪 which document governs? (local, instant)

    public class Gov { public RegDoc Doc; public string Loc = "", Goto = "", Text = ""; public int Level; public double Score; }

    /// <summary>The passages about these words in the GOVERNING documents of the register, the one that governs first
    /// (a change wins on its item, then the order of precedence; superseded, drafts and letters are left out — and listed).</summary>
    public static (List<Gov> Hits, List<string> NotUsed) Governs(string project, IEnumerable<string> words, int max = 12)
    {
        var reg = Get(project, false);
        var hits = new List<Gov>(); var notUsed = new List<string>();
        if (reg == null) return (hits, notUsed);
        var byPath = reg.Docs.GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var w = words.Where(x => x.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var area in Areas(project))
        {
            List<FolderIndex.Passage> ps;
            try { ps = FolderIndex.Retrieve(area, w, 60, 3); } catch { continue; }
            foreach (var p in ps)
            {
                if (!byPath.TryGetValue(p.Path, out var d)) continue;
                if (!d.Governing)
                {
                    var why = d.Status == "superseded" ? $"superseded by {d.By}" : d.Status == "never governs" ? d.Type.ToLowerInvariant() : d.Status;
                    notUsed.Add($"{Path.GetFileName(d.Path)} ({why})");
                    continue;
                }
                var lvl = d.Type.StartsWith("Changes") ? 0 : Level(project, d.Type) is var l and > 0 ? l : 50;
                hits.Add(new Gov { Doc = d, Loc = p.Loc, Goto = p.Goto, Text = p.Text, Level = lvl, Score = p.Score });
            }
        }
        var best = hits.OrderBy(h => h.Level).ThenByDescending(h => h.Doc.Date).ThenByDescending(h => h.Score).Take(max).ToList();
        return (best, notUsed.Distinct().Take(8).ToList());
    }
}
