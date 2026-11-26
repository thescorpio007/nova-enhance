using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>One folder of a project's contract area.</summary>
class ContractEntry
{
    public string Path = "", Role = "", Desc = "", Evidence = "", Why = "";
    public int Rank;
    public bool Latest = true, Subs = true, Sure = true, Picked = true;
    public int Files;
}

/// <summary>
/// 📜 Contract area: the documents that make each project's contract — which one governs (order of precedence), which ones are drafts,
/// tender sets or changes. Kept in 🧠 Nova Brain (section 📜 Contract area), read FIRST by the background reading, and used by every
/// review (MAR, SDS, MS, RFI…) and by “Which document governs?”. Nova proposes the role of each folder from its NAME and from what its
/// documents say on their FIRST PAGES (title, signed / draft, IFC / IFT, Addendum No., Section 04 42 00…); the AI is asked only for the
/// unclear ones, when you allow it. Nothing is saved until you confirm. Designed by A.HAMROUNI
/// </summary>
static class ContractArea
{
    public static readonly string[] Roles =
    {
        "Agreement & LoA", "PTC", "Conditions · particular", "Conditions · general", "Employer's requirements", "Specifications", "Drawings · contract", "BOQ", "Addenda / bulletins",
        "Drawings · IFC", "Changes (EI · VO · PMI · VE · RFI)", "Programme / schedule", "Authority approvals",
        "Drawings · IFT (tender)", "Draft", "Reference only", "Exclude",
    };
    public static string GroupOf(string role) => Array.IndexOf(Roles, role) switch { < 9 and >= 0 => "📜 THE CONTRACT", < 13 and >= 9 => "📐 AFTER THE CONTRACT", _ => "✗ NEVER GOVERNS" };
    public static bool Governs(string role) => Array.IndexOf(Roles, role) is >= 0 and < 13;

    public static readonly string[] DefaultOrder = { "Agreement & LoA", "PTC", "Addenda / bulletins", "Conditions · particular", "Conditions · general", "Employer's requirements", "Specifications", "Drawings · contract", "BOQ" };
    public static int DefaultRank(string role, List<string> order = null) { var i = (order ?? DefaultOrder.ToList()).IndexOf(role); return i >= 0 ? i + 1 : 0; }

    // ------------------------------------------------------------------ kept in 🧠 Nova Brain

    static readonly Regex SetRx = new(@"\[contract\.([a-z0-9\-_.]+?)\.(\w+)\s*=\s*([^\]]+)\]", RegexOptions.IgnoreCase);
    static Dictionary<string, (List<ContractEntry> Entries, List<string> Order, string Clause)> cache;
    static readonly object L = new();
    static Dictionary<string, JsonObject> cfgs = new(StringComparer.OrdinalIgnoreCase);
    static ContractArea() { Brain.Changed += () => { lock (L) { cache = null; first = null; } }; }

    /// <summary>The project's settings for the register: contract areas, your own document types, your answers (Needs your eye).</summary>
    public static JsonObject Cfg(string project)
    {
        Load();
        lock (L) return cfgs.TryGetValue(project ?? "", out var j) ? (JsonObject)j.DeepClone() : new JsonObject();
    }
    public static void SaveCfg(string project, JsonObject cfg) => Save(project, Of(project), Order(project), OrderClause(project), cfg);

    static Dictionary<string, (List<ContractEntry>, List<string>, string)> Load()
    {
        lock (L)
        {
            if (cache != null) return cache;
            var res = new Dictionary<string, (List<ContractEntry>, List<string>, string)>(StringComparer.OrdinalIgnoreCase);
            var cf = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var (sec, r) in Brain.Rules(Brain.Snapshot()))
                {
                    if (sec["id"]?.ToString() != "contract" || r["off"]?.GetValue<bool>() == true) continue;
                    foreach (Match m in SetRx.Matches(r["text"]?.ToString() ?? ""))
                    {
                        var p = m.Groups[1].Value.ToUpperInvariant(); var what = m.Groups[2].Value.ToLowerInvariant(); var val = m.Groups[3].Value.Trim();
                        if (!res.TryGetValue(p, out var t)) res[p] = t = (new List<ContractEntry>(), new List<string>(), "");
                        if (what == "order") { res[p] = (t.Item1, val.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), t.Item3); continue; }
                        if (what == "clause") { res[p] = (t.Item1, t.Item2, Encoding.UTF8.GetString(Convert.FromBase64String(val))); continue; }
                        if (what == "cfg") { try { cf[p] = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(val))) as JsonObject ?? new JsonObject(); } catch { } continue; }
                        try
                        {
                            var j = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(val))) as JsonObject;
                            if (j == null) continue;
                            t.Item1.Add(new ContractEntry
                            {
                                Path = j["p"]?.ToString() ?? "", Role = j["r"]?.ToString() ?? "", Rank = j["k"]?.GetValue<int>() ?? 0, Latest = j["l"]?.GetValue<bool>() ?? true,
                                Subs = j["s"]?.GetValue<bool>() ?? true, Desc = j["d"]?.ToString() ?? "", Evidence = j["e"]?.ToString() ?? "",
                            });
                        }
                        catch { }
                    }
                }
            }
            catch (Exception e) { Log.Warn("contract area", "read: " + e.Message); }
            cfgs = cf;
            return cache = res;
        }
    }

    public static List<ContractEntry> Of(string project) => Load().TryGetValue(project ?? "", out var t) ? t.Item1.OrderBy(e => e.Rank == 0 ? 99 : e.Rank).ThenBy(e => Array.IndexOf(Roles, e.Role)).ToList() : new();
    public static List<string> Order(string project) => Load().TryGetValue(project ?? "", out var t) && t.Item2.Count > 0 ? t.Item2 : DefaultOrder.ToList();
    public static string OrderClause(string project) => Load().TryGetValue(project ?? "", out var t) ? t.Item3 : "";

    /// <summary>Saves a project's contract area into 🧠 Nova Brain (one rule per folder + the order) — a version is kept before.</summary>
    public static void Save(string project, List<ContractEntry> entries, List<string> order, string clause = null, JsonObject cfg = null)
    {
        cfg ??= Cfg(project);
        if (!Brain.Unlocked) throw new InvalidOperationException("Open 🧠 Nova Brain first (its password) — the contract area is kept inside it.");
        var p = project.ToUpperInvariant();
        var key = Regex.Replace(p.ToLowerInvariant(), @"[^a-z0-9\-_]", "");
        var b = Brain.Snapshot();
        var secs = b["sections"] as JsonArray ?? new JsonArray();
        if (b["sections"] == null) b["sections"] = secs;
        var sec = secs.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.ToString() == "contract");
        if (sec == null) { sec = new JsonObject { ["id"] = "contract", ["icon"] = "📜", ["name"] = "Contract area", ["prefix"] = "CA", ["tags"] = new JsonArray("files"), ["rules"] = new JsonArray() }; secs.Add(sec); }
        var rules = sec["rules"] as JsonArray ?? new JsonArray();
        sec["rules"] = rules;
        // this project's old rules out, the new ones in
        foreach (var old in rules.OfType<JsonObject>().Where(r => (r["id"]?.ToString() ?? "").StartsWith("CA-" + p + "-", StringComparison.OrdinalIgnoreCase)).ToList()) rules.Remove(old);
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        order ??= Order(project);
        clause ??= OrderClause(project);
        rules.Add(new JsonObject
        {
            ["id"] = $"CA-{p}-0", ["title"] = $"{p} · order of precedence", ["by"] = "You · 📜 Contract area", ["when"] = now,
            ["text"] = $"{p}: the contract documents govern in this order — " + string.Join(" › ", order.Select((o, i) => $"{i + 1} {o}")) +
                       ". Changes after signing (EI, VO, PMI, VE, RFI answers) win on their own item, the newest first. Drafts, tender (IFT) sets and reference folders never govern." +
                       (clause.Length > 0 ? " (" + clause + ")" : "") + $" [contract.{key}.order = {string.Join("; ", order)}]" +
                       (clause.Length > 0 ? $" [contract.{key}.clause = {Convert.ToBase64String(Encoding.UTF8.GetBytes(clause))}]" : "")
        });
        int n = 1;
        foreach (var e in entries)
        {
            var j = new JsonObject { ["p"] = e.Path, ["r"] = e.Role, ["k"] = e.Rank, ["l"] = e.Latest, ["s"] = e.Subs, ["d"] = e.Desc, ["e"] = e.Evidence };
            var name = System.IO.Path.GetFileName(e.Path.TrimEnd('\\'));
            rules.Add(new JsonObject
            {
                ["id"] = $"CA-{p}-{n}", ["title"] = $"{e.Role} · {name}", ["by"] = "You · 📜 Contract area", ["when"] = now,
                ["text"] = $"{p} · {e.Role}{(e.Rank > 0 ? " (rank " + e.Rank + ")" : "")} · {name}: {(e.Desc.Length > 0 ? e.Desc : "—")} [contract.{key}.f{n} = {Convert.ToBase64String(Encoding.UTF8.GetBytes(j.ToJsonString()))}]"
            });
            n++;
        }
        if (cfg.Count > 0)
            rules.Add(new JsonObject
            {
                ["id"] = $"CA-{p}-X", ["title"] = $"{p} · contract areas, your document types and answers", ["by"] = "You · 📜 Contract area", ["when"] = now,
                ["text"] = $"{p}: contract areas " + string.Join(" · ", (cfg["areas"] as JsonArray ?? new JsonArray()).Select(a => a?.ToString())) +
                           ((cfg["types"] as JsonArray)?.Count > 0 ? "; your document types: " + string.Join(", ", (cfg["types"] as JsonArray).Select(t => t?["name"]?.ToString())) : "") +
                           $". [contract.{key}.cfg = {Convert.ToBase64String(Encoding.UTF8.GetBytes(cfg.ToJsonString()))}]"
            });
        Brain.Commit(b, "You · 📜 Contract area", $"{p}: {entries.Count} folder(s) · order of precedence");
        lock (L) { cache = null; first = null; }
    }

    static List<string> first;
    static DateTime firstAt;
    /// <summary>The governing folders of every project: the background reading reads them FIRST.</summary>
    public static List<string> ReadFirst()
    {
        lock (L) if (first != null && DateTime.Now - firstAt < TimeSpan.FromMinutes(2)) return first;
        var res = new List<string>();
        try
        {
            foreach (var (proj, t) in Load())
            {
                res.AddRange(ContractRegister.Areas(proj));
                res.AddRange(t.Item1.Where(e => Governs(e.Role)).OrderBy(e => e.Rank == 0 ? 99 : e.Rank).Select(e => e.Path));
            }
            res = res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { }
        lock (L) { first = res; firstAt = DateTime.Now; }
        return res;
    }

    // ------------------------------------------------------------------ ✦ proposing a role: the name, then the first pages

    internal static readonly (Regex Rx, string Role, int W)[] NameRules =
    {
        (new(@"\bdraft\b|\b_td\b|for comments", RegexOptions.IgnoreCase), "Draft", 3),
        (new(@"render|photo|picture|_old\b|\bold\b|backup|archive|superseded|recycle", RegexOptions.IgnoreCase), "Exclude", 3),
        (new(@"form of agreement|agreement|letter of (award|acceptance)|\bloa\b|main contract|zublin contract|vol(ume)?[\s_\-]*1\b", RegexOptions.IgnoreCase), "Agreement & LoA", 2),
        (new(@"\bptc\b|post[\s\-]?tender|tender clarification", RegexOptions.IgnoreCase), "PTC", 3),
        (new(@"particular conditions", RegexOptions.IgnoreCase), "Conditions · particular", 3),
        (new(@"general conditions|\bfidic\b|conditions of contract|\bcoc\b", RegexOptions.IgnoreCase), "Conditions · general", 2),
        (new(@"employer'?s requirements", RegexOptions.IgnoreCase), "Employer's requirements", 3),
        (new(@"spec", RegexOptions.IgnoreCase), "Specifications", 2),
        (new(@"\bboq\b|bills? of quantit", RegexOptions.IgnoreCase), "BOQ", 3),
        (new(@"addend|bulletin", RegexOptions.IgnoreCase), "Addenda / bulletins", 3),
        (new(@"executed|stamped|contract drawings|vol(ume)?[\s_\-]*4\b", RegexOptions.IgnoreCase), "Drawings · contract", 2),
        (new(@"\bifc\b|issued for construction", RegexOptions.IgnoreCase), "Drawings · IFC", 2),
        (new(@"\bift\b|tender|bidding|\bbid\b", RegexOptions.IgnoreCase), "Drawings · IFT (tender)", 1),
        (new(@"\bei\b|engineer|instruction|\bvo'?s?\b|variation|\bpmi\b|\bve\d*\b|value engineering|change order|clarification|\brfi", RegexOptions.IgnoreCase), "Changes (EI · VO · PMI · VE · RFI)", 2),
        (new(@"schedule|programme|program\b|baseline", RegexOptions.IgnoreCase), "Programme / schedule", 2),
        (new(@"authority|permit|\bnoc\b|adcd|municipality|approvals?", RegexOptions.IgnoreCase), "Authority approvals", 1),
        (new(@"correspondence|letters?\b|transmittal|minutes|\bmom\b|master plan|revit|model|reference|bonds?|insurance|payment|progress", RegexOptions.IgnoreCase), "Reference only", 1),
        (new(@"\bplans?\b|drawings?", RegexOptions.IgnoreCase), "Drawings · IFC", 1),
    };

    internal static readonly (Regex Rx, string Role, int W, string Tag)[] PageRules =
    {
        (new(@"form of agreement|this agreement is made|contract agreement|letter of acceptance|letter of award", RegexOptions.IgnoreCase), "Agreement & LoA", 4, "agreement"),
        (new(@"post[\s\-]?tender clarification", RegexOptions.IgnoreCase), "PTC", 4, "post-tender clarifications"),
        (new(@"particular conditions", RegexOptions.IgnoreCase), "Conditions · particular", 4, "particular conditions"),
        (new(@"general conditions|conditions of contract for", RegexOptions.IgnoreCase), "Conditions · general", 3, "general conditions"),
        (new(@"employer'?s requirements", RegexOptions.IgnoreCase), "Employer's requirements", 4, "employer's requirements"),
        (new(@"\bsection\s+\d{2}\s?\d{2}\s?\d{2}\b|part\s*1\s*[-–]?\s*general|part\s*2\s*[-–]?\s*products", RegexOptions.IgnoreCase), "Specifications", 4, "specification section"),
        (new(@"bill of quantities|\bbill no\.?\s*\d|preliminaries|item\s+description\s+unit", RegexOptions.IgnoreCase), "BOQ", 4, "bill of quantities"),
        (new(@"addendum\s*(no\.?|#)?\s*\d|tender bulletin", RegexOptions.IgnoreCase), "Addenda / bulletins", 4, "addendum"),
        (new(@"issued for construction|\bifc\b", RegexOptions.IgnoreCase), "Drawings · IFC", 3, "issued for construction"),
        (new(@"issued for tender|\bift\b|tender drawing", RegexOptions.IgnoreCase), "Drawings · IFT (tender)", 3, "issued for tender"),
        (new(@"engineer'?s instruction|variation order|change order|project manager'?s instruction|value engineering", RegexOptions.IgnoreCase), "Changes (EI · VO · PMI · VE · RFI)", 3, "instruction / variation"),
        (new(@"baseline (programme|schedule)|gantt|critical path", RegexOptions.IgnoreCase), "Programme / schedule", 3, "programme"),
        (new(@"\bdraft\b|for comments|not for construction", RegexOptions.IgnoreCase), "Draft", 3, "draft"),
    };
    internal static readonly Regex Signed = new(@"for and on behalf|signed|signature|witness|contract document|stamp", RegexOptions.IgnoreCase);
    static readonly Regex DateRx = new(@"\b(\d{1,2})(st|nd|rd|th)?\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+(20\d\d)\b|\b(20\d\d)[\-/\.](\d{2})[\-/\.](\d{2})\b", RegexOptions.IgnoreCase);
    static readonly string[] SampleExt = { ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".msg" };

    /// <summary>The first pages of a file (what Nova already read, else page 1–2 only; OCR page 1 when there is no text).</summary>
    static string FirstPages(string path, CancellationToken ct)
    {
        try
        {
            var known = FolderIndex.Known(path);
            if (known != null && known.Count > 0) { var k = string.Join("\n", known.Take(2).Select(x => x.Text)); return k.Length > 3000 ? k[..3000] : k; }
        }
        catch { }
        ct.ThrowIfCancellationRequested();
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".pdf")
            {
                var sb = new StringBuilder();
                using (var doc = UglyToad.PdfPig.PdfDocument.Open(path))
                    for (int i = 1; i <= Math.Min(2, doc.NumberOfPages); i++)
                        try { sb.Append(string.Join(" ", doc.GetPage(i).GetWords().Select(w => w.Text))).Append('\n'); } catch { }
                var t = sb.ToString();
                if (t.Trim().Length < 40) t = string.Join("\n", Ocr.Pdf(path, new List<int> { 1 }, 1).Select(x => x.Item2));
                return t.Length > 3000 ? t[..3000] : t;
            }
            var segs = FileText.Extract(path);
            var all = string.Join("\n", segs.Take(3).Select(s => s.Text));
            return all.Length > 3000 ? all[..3000] : all;
        }
        catch { return ""; }
    }

    /// <summary>✦ A role for one folder: its name + the first pages of up to 3 of its documents (largest first).</summary>
    public static ContractEntry ProposeOne(string project, string folder, CancellationToken ct)
    {
        var e = new ContractEntry { Path = folder };
        var score = new Dictionary<string, int>();
        var why = new List<string>();
        var name = System.IO.Path.GetFileName(folder.TrimEnd('\\'));
        foreach (var (rx, role, w) in NameRules)
            if (rx.IsMatch(name)) { score[role] = score.GetValueOrDefault(role) + w; if (why.Count == 0) why.Add($"folder name “{name}”"); }
        // the documents: names from Nova's list (or the folder itself), largest readable first
        List<Projects.FileRow> files;
        try { files = Projects.Catalogue(new[] { folder }, 20000); } catch { files = new(); }
        if (files.Count == 0)
            try
            {
                files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(3000)
                    .Select(f => { var fi = new FileInfo(f); return new Projects.FileRow { Path = f, Name = fi.Name, Size = fi.Length, Modified = fi.LastWriteTime }; }).ToList();
            }
            catch { files = new(); }
        e.Files = files.Count;
        bool signed = false, draft = false; string date = "", title = "";
        foreach (var f in files.Where(f => SampleExt.Contains(System.IO.Path.GetExtension(f.Name).ToLowerInvariant())).OrderByDescending(f => f.Size).Take(3))
        {
            ct.ThrowIfCancellationRequested();
            var text = FirstPages(f.Path, ct);
            if (text.Length < 20) continue;
            foreach (var (rx, role, w, tag) in PageRules)
            {
                var m = rx.Match(text);
                if (!m.Success) continue;
                score[role] = score.GetValueOrDefault(role) + w;
                if (role == "Draft") draft = true;
                if (title.Length == 0) { title = m.Value; why.Add($"📄 {f.Name} p.1: “{m.Value.Trim()}”"); }
            }
            if (Signed.IsMatch(text)) signed = true;
            var dm = DateRx.Match(text); if (dm.Success && date.Length == 0) date = dm.Value;
        }
        var ranked = score.OrderByDescending(kv => kv.Value).ToList();
        if (draft && ranked.Count > 0 && ranked[0].Key != "Draft") ranked.Insert(0, new KeyValuePair<string, int>("Draft", ranked[0].Value + 1));
        e.Role = ranked.Count > 0 ? ranked[0].Key : files.Count == 0 ? "Exclude" : "Reference only";
        e.Sure = ranked.Count > 0 && ranked[0].Value >= 4 && (ranked.Count < 2 || ranked[0].Value - ranked[1].Value >= 2);
        e.Rank = DefaultRank(e.Role, Order(project));
        e.Latest = e.Role is not ("Changes (EI · VO · PMI · VE · RFI)" or "Addenda / bulletins" or "Agreement & LoA");
        e.Evidence = string.Join(" · ", why.Take(2)) + (signed ? " · signed / stamped" : "") + (date.Length > 0 ? " · " + date : "");
        e.Desc = $"{e.Role}{(title.Length > 0 ? " — " + title.Trim() : "")}{(date.Length > 0 ? " · " + date : "")}{(signed ? " · signed" : "")}{(draft ? " · draft: never governs" : "")}.";
        e.Why = e.Evidence.Length > 0 ? e.Evidence : "folder name";
        e.Picked = e.Role != "Exclude" || files.Count > 0;
        return e;
    }

    /// <summary>✦ A folder added by you: itself, or each subfolder (one role each).</summary>
    public static List<ContractEntry> Propose(string project, string folder, Action<string> status, CancellationToken ct)
    {
        var res = new List<ContractEntry>();
        List<string> subs;
        try { subs = Directory.GetDirectories(folder).Where(d => !Projects.Private.IsMatch(d)).OrderBy(d => d).ToList(); } catch { subs = new(); }
        if (subs.Count == 0 || subs.Count > 40) subs = new List<string> { folder };
        foreach (var s in subs)
        {
            ct.ThrowIfCancellationRequested();
            status?.Invoke($"📖 Reading the first pages in {System.IO.Path.GetFileName(s)}…");
            try { res.Add(ProposeOne(project, s, ct)); } catch (OperationCanceledException) { throw; } catch (Exception e) { Log.Warn("contract area", s + ": " + e.Message); }
        }
        return res;
    }

    /// <summary>✦ Nova proposes: folders of the project (2 levels) whose name looks like part of the contract, not added yet.</summary>
    public static List<ContractEntry> Candidates(Project p, Action<string> status, CancellationToken ct)
    {
        var have = Of(p.Code).Select(e => e.Path.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var asked = Store.Str("win_contract_no_" + p.Code, "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        foreach (var root in p.Folders)
        {
            try
            {
                foreach (var d1 in Directory.GetDirectories(root))
                {
                    dirs.Add(d1);
                    try { dirs.AddRange(Directory.GetDirectories(d1)); } catch { }
                }
            }
            catch { }
        }
        var hits = dirs.Where(d => !have.Contains(d.TrimEnd('\\')) && !asked.Contains(d) && !Projects.Private.IsMatch(d) && !have.Any(h => d.StartsWith(h + "\\", StringComparison.OrdinalIgnoreCase)))
            .Select(d => (d, s: NameRules.Where(r => r.Role is not ("Reference only" or "Exclude") && r.Rx.IsMatch(System.IO.Path.GetFileName(d))).Sum(r => r.W)))
            .Where(x => x.s >= 2).OrderByDescending(x => x.s).Take(24).Select(x => x.d).ToList();
        var res = new List<ContractEntry>();
        foreach (var d in hits)
        {
            ct.ThrowIfCancellationRequested();
            status?.Invoke($"📖 {System.IO.Path.GetFileName(d)}…");
            try { res.Add(ProposeOne(p.Code, d, ct)); } catch (OperationCanceledException) { throw; } catch { }
        }
        return res;
    }

    public static void NotAgain(string project, IEnumerable<string> folders) =>
        Store.Set("win_contract_no_" + project, string.Join("|", Store.Str("win_contract_no_" + project, "").Split('|', StringSplitOptions.RemoveEmptyEntries).Concat(folders).Distinct(StringComparer.OrdinalIgnoreCase)));

    /// <summary>✦ The AI for the unclear ones (only the first-page text of 1–2 documents and the folder name are sent).</summary>
    public static async Task AskAi(string project, List<ContractEntry> unclear, Action<string> status, CancellationToken ct)
    {
        foreach (var e in unclear)
        {
            ct.ThrowIfCancellationRequested();
            status?.Invoke("✦ Asking the AI about " + System.IO.Path.GetFileName(e.Path) + "…");
            List<Projects.FileRow> files;
            try { files = Projects.Catalogue(new[] { e.Path }, 2000); } catch { files = new(); }
            var sample = string.Join("\n---\n", files.Where(f => SampleExt.Contains(System.IO.Path.GetExtension(f.Name).ToLowerInvariant())).OrderByDescending(f => f.Size).Take(2)
                .Select(f => f.Name + ":\n" + FirstPages(f.Path, ct)).Select(t => t.Length > 1500 ? t[..1500] : t));
            var sys = "You classify a folder of a construction project's documents for its contract area. Reply with JSON only: " +
                      "{\"role\":\"<one of: " + string.Join(" | ", Roles) + ">\",\"description\":\"<one sentence for a colleague: what these documents are, their date, signed or draft>\"}";
            var user = $"Project {project}. Folder: {System.IO.Path.GetFileName(e.Path)} ({e.Files} files). First pages of its largest documents:\n{sample}";
            try
            {
                var j = Ai.ParseJson(await SmartSearch.CloudChat(sys, user, 12, ct));
                var role = j["role"]?.ToString() ?? "";
                if (Roles.Contains(role)) { e.Role = role; e.Rank = DefaultRank(role, Order(project)); e.Sure = true; e.Why = "✦ AI (first pages) · " + e.Why; }
                var d = j["description"]?.ToString() ?? "";
                if (d.Length > 0) e.Desc = d;
            }
            catch (Exception ex) { status?.Invoke("⚠ AI: " + ex.Message); if (Ai.QuotaOut) break; }
        }
    }

    // ------------------------------------------------------------------ order of precedence read from the contract itself

    static readonly Regex ClauseRx = new(@"(documents forming the contract|priority of (contract )?documents|order of precedence|documents (shall be )?(taken|deemed) as mutually explanatory|in the following order)[\s\S]{0,1800}", RegexOptions.IgnoreCase);

    /// <summary>Finds the clause that ranks the contract documents (Agreement / Particular Conditions, FIDIC 1.5) and turns it into the order.</summary>
    public static async Task<(List<string> Order, string Clause)> OrderFromContract(string project, Action<string> status, CancellationToken ct)
    {
        var docs = Of(project).Where(e => e.Role is "Agreement & LoA" or "Conditions · particular" or "Conditions · general" or "PTC").ToList();
        if (docs.Count == 0) return (null, "Add the Agreement / Conditions folders first.");
        foreach (var e in docs)
        {
            List<Projects.FileRow> files;
            try { files = Projects.Catalogue(new[] { e.Path }, 500); } catch { continue; }
            foreach (var f in files.Where(f => SampleExt.Contains(System.IO.Path.GetExtension(f.Name).ToLowerInvariant())).OrderBy(f => f.Size).Take(12))
            {
                ct.ThrowIfCancellationRequested();
                status?.Invoke("📖 Looking for the order of precedence in " + f.Name + "…");
                List<Seg> segs;
                try { segs = (await Task.Run(() => Projects.Read(f.Path, ct), ct)).Segs; } catch (OperationCanceledException) { throw; } catch { continue; }
                foreach (var s in segs)
                {
                    var m = ClauseRx.Match(s.Text ?? "");
                    if (!m.Success) continue;
                    var text = m.Value;
                    var cite = $"{f.Name} · {s.Loc}";
                    // local first: the roles in the order they appear in the clause
                    var found = new List<(int At, string Role)>();
                    foreach (var (rx, role, _) in NameRules.Where(r => DefaultOrder.Contains(r.Role)))
                    { var mm = rx.Match(text); if (mm.Success && found.All(x => x.Role != role)) found.Add((mm.Index, role)); }
                    var order = found.OrderBy(x => x.At).Select(x => x.Role).ToList();
                    if (order.Count < 4 && Ai.Active() != null && !Ai.QuotaOut)
                    {
                        try
                        {
                            var j = Ai.ParseJson(await SmartSearch.CloudChat("Turn this contract clause into the order of precedence of the contract documents. Reply JSON only: {\"order\":[\"…\"]} using only these names: " + string.Join(" | ", DefaultOrder), text.Length > 2500 ? text[..2500] : text, 12, ct));
                            var ai = (j["order"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => DefaultOrder.Contains(x)).Distinct().ToList();
                            if (ai != null && ai.Count >= order.Count) order = ai;
                        }
                        catch { }
                    }
                    foreach (var r in DefaultOrder) if (!order.Contains(r)) order.Add(r);
                    return (order, "from " + cite);
                }
            }
        }
        return (null, "The clause that ranks the documents was not found in the files Nova could read — keep the usual order or set it by hand.");
    }

    // ------------------------------------------------------------------ 🧪 which document governs?

    public class Hit { public string Path = "", Loc = "", Goto = "", Text = "", Role = ""; public int Rank; public double Score; }

    /// <summary>Passages about the words in the contract area (files already read — instant), the governing one first.</summary>
    public static List<Hit> GovernsTest(string project, string q, int max = 10)
    {
        var words = Regex.Split(q.ToLowerInvariant(), @"[^\p{L}\p{N}\.\-]+").Where(w => w.Length > 2).Distinct().Take(8).ToList();
        var res = new List<Hit>();
        if (words.Count == 0) return res;
        foreach (var e in Of(project).Where(e => Governs(e.Role)))
        {
            List<Projects.FileRow> files;
            try { files = Projects.Catalogue(new[] { e.Path }, 20000); } catch { continue; }
            foreach (var f in files)
            {
                List<Seg> segs;
                try { segs = FolderIndex.Known(f.Path); } catch { segs = null; }
                if (segs == null) continue;
                foreach (var s in segs)
                {
                    var t = s.Text ?? "";
                    var hit = words.Count(w => t.Contains(w, StringComparison.OrdinalIgnoreCase));
                    if (hit < Math.Min(2, words.Count)) continue;
                    var at = words.Select(w => t.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
                    var from = Math.Max(0, at - 80);
                    res.Add(new Hit { Path = f.Path, Loc = s.Loc, Goto = s.Goto, Role = e.Role, Rank = e.Rank, Score = hit, Text = t.Substring(from, Math.Min(260, t.Length - from)) });
                }
            }
        }
        // changes (newest) first on their item, then by rank
        return res.OrderByDescending(h => h.Score).ThenBy(h => h.Role.StartsWith("Changes") ? 0 : h.Rank == 0 ? 50 : h.Rank).Take(max)
                  .OrderBy(h => h.Role.StartsWith("Changes") ? 0 : h.Rank == 0 ? 50 : h.Rank).ThenByDescending(h => h.Score).ToList();
    }
}
