using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 📋 Submittal log: every WIR · MIR · MAR · PQS · RFI · SDS found in Outlook — submitted through Thinkproject (SLV) or by plain email (RS5) —
/// with the MZA reply on the same thread (closed, code, date, who), revisions, tower · zone · level, overdue, and an Excel log
/// (Log · Open & overdue · Summary · Check · About). Its rules come from the 🧠 Nova Brain (section 📋 Submittal logs). Designed by A.HAMROUNI
/// </summary>
static class SubmittalLog
{
    public class Options
    {
        public List<string> Projects = new();   // SLV, RS5 … (empty = all)
        public List<string> Types = new();      // WIR, EI, MOM … (empty = every type the brain enables)
        public DateTime From = DateTime.Today.AddDays(-30), To = DateTime.Today.AddDays(1).AddSeconds(-1);
        public string Tower = "", Zone = "", Discipline = "", Status = "";
        public int LevelFrom = -99, LevelTo = 999;
        public Options Copy() { var o = (Options)MemberwiseClone(); o.Projects = new(Projects); o.Types = new(Types); return o; }
    }

    public class Item
    {
        public string Project = "", Via = "", Type = "", No = "", Rev = "", Disc = "", Title = "", Tower = "", Zone = "", Level = "", Apt = "";
        public DateTime? Submitted, Deadline, Reply;
        public string By = "", Code = "", Status = "", Why = "", Check = "", Ref = "", Series = "";
        public int RevNum, Emails, Num = -1;
        public MailHit Sub, Rep;
        public List<MailHit> Mails = new();
        public int Days => Submitted is { } s ? (int)((Reply ?? DateTime.Now) - s).TotalDays : 0;
        public int? LevelNum;
    }

    public class Result
    {
        public List<Item> Items = new(); public int Mails; public Options Opt; public double Seconds; public List<string> Gaps = new();
        /// <summary>Items per discipline before the discipline and status choices, and per status before the status choice (for the chips).</summary>
        public Dictionary<string, int> Discs = new(StringComparer.OrdinalIgnoreCase), Statuses = new();
    }

    /// <summary>A level typed in a box: “5”, “L5”, “GF”, “FF”, “UG”, “B1”, “podium”, “roof”.</summary>
    public static int? LevelOf(string s)
    {
        s = (s ?? "").Trim().ToUpperInvariant().Replace(" ", "");
        if (s.Length == 0) return null;
        if (int.TryParse(s.TrimStart('L'), out var n)) return n;
        return s switch { "GF" or "G" or "GROUND" => 0, "FF" or "FIRST" => 1, "UG" => -1, "PODIUM" => 1, "ROOF" => 100, "ATTIC" => 99, _ => s.StartsWith("B") && int.TryParse(s[1..], out var b) ? -b : s == "B" ? -1 : null };
    }

    /// <summary>
    /// One kind of document. Find: "code" = its code in the document number (…-WIR-00123), "words" = words in the subject.
    /// Close: code (MZA reply with code / approval completed / signed file) · reply (the other party replies) · anyreply (anyone replies) ·
    /// contractor (the contractor acknowledges) · mzaconfirm (MZA confirms closure after the contractor's reply) · final (MOM: final minutes issued) · received.
    /// </summary>
    public record TypeDef(string Code, string Label, string Find, string Close, int Overdue, string Rx = "", string Like = "", string Done = "", string Words = "");

    /// <summary>The types of your field (🧩 Settings → About me): construction = the built-in ones below.</summary>
    public static TypeDef[] Types
    {
        get
        {
            // Your field's types + ✎ your own types (My log settings) — read again every few seconds, not for each item.
            var now = DateTime.UtcNow;
            if (typesCache == null || now - typesAt > TimeSpan.FromSeconds(3))
            {
                var b = Tracker.Types.ToList();
                try { foreach (var t in LogPrefs.CustomTypes()) if (!b.Any(x => x.Code == t.Code)) b.Add(t); } catch { }
                typesCache = b.ToArray(); typesAt = now;
            }
            return typesCache;
        }
    }
    static TypeDef[] typesCache; static DateTime typesAt;

    public static readonly TypeDef[] Builtin =
    {
        new("WIR", "Work inspection request", "code", "code", 2), new("MIR", "Material inspection request", "code", "code", 2),
        new("MAR", "Material approval request", "code", "code", 14), new("MSS", "Method statement", "code", "code", 14),
        new("SDS", "Shop drawing", "code", "code", 14), new("SSM", "Sample", "code", "code", 14), new("TSR", "Test report", "code", "code", 14),
        new("PQS", "Prequalification", "code", "code", 14), new("RFI", "Request for information", "code", "code", 7),
        new("RPT", "Report", "code", "code", 14), new("HSE", "HSE submittal", "code", "code", 14), new("CAL", "Calculation", "code", "code", 14),
        new("RTS", "Request to start", "code", "code", 7), new("HOV", "Handover", "code", "code", 14), new("PLN", "Planning", "code", "code", 14),
        new("LTR", "Letter", "code", "reply", 14), new("PAY", "Payment application", "words", "reply", 14), new("DSR", "Daily report", "code", "received", 0),
        new("VO", "Variation", "words", "reply", 14), new("AUTH", "Authority submission", "words", "anyreply", 14),
        new("SOR", "Site observation report (MZA)", "code", "mzaconfirm", 7), new("NCR", "Non-conformance report (MZA)", "code", "mzaconfirm", 14),
        new("EI", "Engineer's instruction (MZA)", "words", "contractor", 7), new("MOM", "Minutes of meeting", "words", "final", 7)
    };
    public static TypeDef Def(string code) => Types.FirstOrDefault(t => t.Code == code);
    static readonly Dictionary<string, string> Alias = new() { ["SHD"] = "SDS", ["REP"] = "RPT", ["VOR"] = "VO", ["PAYMENT"] = "PAY", ["AUTHORITY"] = "AUTH" };

    static readonly Regex ReplyRx = new(@"^\s*((re|aw|antw)\s*:\s*|\[external\]\s*)+", RegexOptions.IgnoreCase);
    static readonly Regex FwdRx = new(@"^\s*((fw|fwd|tr)\s*:|automatic reply|auto(matic)?[- ]reply|out of office)", RegexOptions.IgnoreCase);
    static readonly Regex Strip = new(@"^\s*((re|aw|antw|fw|fwd|tr)\s*:\s*|\[external\]\s*|\[ext\]\s*)+", RegexOptions.IgnoreCase);
    static readonly Regex EiRx = new(@"Engineer\W{0,2}s?\s+Instruction|\bEI\s*No\.?\s*\d+", RegexOptions.IgnoreCase);
    static readonly Regex EiNum = new(@"(?:Instruction|\bEI)\s*(?:no\.?|number|#)?\s*0*(\d{1,4})\b", RegexOptions.IgnoreCase);
    static readonly Regex DetRx = new(@"Engineer\W{0,2}s?\s+Determination", RegexOptions.IgnoreCase);
    static readonly Regex MomRx = new(@"\bMoM\b|Minutes of|Meeting\s*No\.?\s*\d+|Kick[\s-]?Off\s+Meeting|-MOM-", RegexOptions.IgnoreCase);
    static readonly Regex PayRx = new(@"payment|\bIPC\b|\bIPA\b|statement of account|interim payment", RegexOptions.IgnoreCase);
    static readonly Regex VoRx = new(@"\bvariations?\b|\bVOR?\b|\bV\.O\.", RegexOptions.IgnoreCase);
    static readonly Regex AuthRx = new(@"\bADCD\b|civil defen[cs]e|\bADM\b|municipality|\bDMT\b|\bAldar\b|\bMiral\b|\bNOC\b|\bADDC\b|\bADQCC\b", RegexOptions.IgnoreCase);
    static readonly Regex LetterRef = new(@"\b(?:[A-Z]{2,5}-){2,4}\d{2}-\d{3,4}-L-\d{4}-\d{2,4}\b|\b[A-Z0-9]{2,6}-[A-Z]{2,5}-PM-LTR-\d{3,6}\b");

    static Regex NumRx(IEnumerable<string> codes) => new(
        @"(?<![A-Za-z0-9])([A-Z][A-Z0-9]{1,5}(?:-[A-Z0-9]{1,6}){1,4}?-(" + string.Join("|", codes.Select(Regex.Escape)) + @")-(\d{2,6}))(?:[_\- ]*(?i:Rev|R)\.?\s?(\d{1,2}))?");

    /// <summary>Your projects (📁 Projects, with their names), then any other email project Nova knows.</summary>
    public static List<string> KnownProjects() => Projects.Known().Select(x => x.Code).ToList();

    /// <summary>The project chips of the log: your order (✎ Edit), hidden ones left out.</summary>
    public static List<(string Code, string Name)> LogProjects(bool withHidden = false)
    {
        var hide = LogPrefs.HiddenProjects; var order = LogPrefs.ProjectOrder;
        return Projects.Known().Where(p => withHidden || !hide.Contains(p.Code))
            .OrderBy(p => order.FindIndex(x => x.Equals(p.Code, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : 1000).ToList();
    }

    /// <summary>Every way a project is written (code, old codes, names) → its code. For document numbers: “RS5-C7-ACC-…” → RS5-C7.</summary>
    static Dictionary<string, string> Aliases()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in NovaEnhance.Projects.All().Where(p => p.Code.Length > 0))
            foreach (var n in NovaEnhance.Projects.NamesOf(p)) d.TryAdd(n, p.Code);
        try { foreach (var k in MailProjects.Counts.Keys) if (k.Length > 0) d.TryAdd(k, k); } catch { }
        return d;
    }

    /// <summary>The types switched on: [log.all = …] in the brain (else every built-in type).</summary>
    public static List<string> Enabled(JsonObject brain = null) =>
        Brain.ListOf("log.all", Tracker.DefaultTypes, brain).Concat(LogPrefs.CustomTypes().Select(t => t.Code)).Where(x => !LogPrefs.HiddenTypes.Contains(x)).Where(x => Def(Alias.TryGetValue(x.ToUpperInvariant(), out var a0) ? a0 : x.ToUpperInvariant()) != null).Select(x => Alias.TryGetValue(x.ToUpperInvariant(), out var a) ? a : x.ToUpperInvariant()).Distinct().ToList();

    static string Norm(string subj) => Regex.Replace(Strip.Replace(subj ?? "", "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    public static Result Run(Options o, JsonObject brain = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        brain ??= Brain.Snapshot();
        var want = (o.Types.Count > 0 ? o.Types.Select(t => Alias.TryGetValue(t.ToUpperInvariant(), out var a) ? a : t.ToUpperInvariant()).ToList() : Enabled(brain)).ToHashSet();
        var mza = Brain.ListOf("mza.match", Tracker.Us, brain);
        bool builtin = Tracker.Builtin;
        var us = Tracker.UsShort;
        var refTypes = Types.Where(t => t.Find == "ref" && want.Contains(t.Code)).Select(t => (t, Rx: new Regex(t.Rx, RegexOptions.IgnoreCase), Done: t.Done.Length > 0 ? new Regex(t.Done, RegexOptions.IgnoreCase) : null)).ToList();
        var closedFiles = Brain.ListOf("closed.files", "signed, -C1, -C2, -C3, -C4, reply", brain);
        var codeTypes = Types.Where(t => t.Find == "code" && want.Contains(t.Code)).Select(t => t.Code).ToList();
        if (builtin && want.Contains("HSE") && !codeTypes.Contains("GEN")) codeTypes.Add("GEN");      // HSE comes as …-HSE-GEN-…
        if (builtin && want.Contains("PAY") && !codeTypes.Contains("LTR")) codeTypes.Add("LTR");      // payment applications come as letters
        var rx = codeTypes.Count > 0 ? NumRx(codeTypes) : null;
        var alias = Aliases();
        var known = new HashSet<string>(alias.Keys.Where(k => !k.Contains(' ')), StringComparer.OrdinalIgnoreCase);
        string ProjOf(MailHit h) { try { var p = MailProjects.Of(h.Id); return string.IsNullOrEmpty(p) ? "" : p; } catch { return ""; } }

        // Every email that can belong to a log: document codes in the subject, and the words of EI · MOM · VO · Authority · Payment.
        var subj = codeTypes.Select(c => "%-" + c + "-%").ToList();
        if (builtin && want.Contains("EI")) subj.AddRange(new[] { "%Engineer%Instruction%", "%EI No%", "%Engineer%Determination%" });
        if (want.Contains("MOM")) subj.AddRange(new[] { "%Minutes%", "%MOM%", "%Meeting No%", "%Kick%Off%" });
        if (builtin && want.Contains("VO")) subj.AddRange(new[] { "%Variation%", "% VO %", "% VO-%", "%VOR%", "%V.O.%" });
        if (builtin && want.Contains("AUTH")) subj.AddRange(new[] { "%ADCD%", "%Civil Defen%", "%ADM %", "%Municipality%", "%DMT%", "%Aldar%", "%Miral%", "%NOC%", "%ADDC%" });
        if (builtin && want.Contains("PAY")) subj.AddRange(new[] { "%Payment%", "%IPC%", "%IPA%", "%Statement of Account%" });
        foreach (var (t, _, _) in refTypes) subj.AddRange(t.Like.Split(';', StringSplitOptions.RemoveEmptyEntries));
        subj = subj.Distinct().ToList();
        var atts = want.Contains("MOM") ? new List<string> { "%-MOM-%", "%MOM%" } : new List<string>();
        var mails = MailIndex.SubmittalsLike(subj, atts, o.From.AddDays(-3), DateTime.Now.AddDays(1));
        ct.ThrowIfCancellationRequested();

        bool IsMza(MailHit h) => h.Sent || mza.Any(m => (h.FromName ?? "").Contains(m, StringComparison.OrdinalIgnoreCase) || (h.FromAddr ?? "").Contains(m, StringComparison.OrdinalIgnoreCase));

        var groups = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        var determ = new Dictionary<string, MailHit>();
        Item G(string key, Func<Item> make) { if (!groups.TryGetValue(key, out var it)) groups[key] = it = make(); return it; }

        foreach (var h in mails.OrderBy(h => h.Date))
        {
            ct.ThrowIfCancellationRequested();
            var s = h.Subject ?? "";
            var clean = Strip.Replace(s, "");
            // 1 · Engineer's Instructions (MZA letters “Engineer’s Instruction no. 083 …”); a Determination joins its EI.
            if (builtin && want.Contains("EI") && (EiRx.IsMatch(s) || DetRx.IsMatch(s)))
            {
                var n = EiNum.Match(s);
                if (n.Success)
                {
                    var p = ProjFrom(s, alias) is { Length: > 0 } pp ? pp : ProjOf(h);
                    var num = int.Parse(n.Groups[1].Value);
                    if (DetRx.IsMatch(s)) { determ.TryAdd(p + "|" + num, h); continue; }
                    var it = G($"EI|{p}|{num}", () => new Item { Type = "EI", Project = p, Num = num, No = $"EI {num:000}", Via = "Letter" });
                    it.Mails.Add(h);
                    if (it.Title.Length == 0 || !ReplyRx.IsMatch(s)) it.Title = Regex.Replace(Regex.Replace(clean, @"^.*?(?:Instruction|EI)\s*(?:no\.?|#)?\s*0*\d{1,4}\s*[-–:]*\s*", "", RegexOptions.IgnoreCase), @"\s*//.*$", "").Trim();
                    if (it.Ref.Length == 0 && LetterRef.Match(s) is { Success: true } lr) it.Ref = lr.Value;
                    continue;
                }
            }
            // 2 · Minutes of meetings (draft → final), kick-off meetings, any meeting record.
            if (want.Contains("MOM") && (MomRx.IsMatch(s) || Regex.IsMatch(h.AttNames ?? "", @"-MOM-|\bMoM\b", RegexOptions.IgnoreCase)) && !(rx?.IsMatch(s) == true && !Regex.IsMatch(s, "-MOM-")))
            {
                var p = ProjFrom(s + " " + h.AttNames, alias) is { Length: > 0 } pp ? pp : ProjOf(h);
                var (series, num, held) = MomOf(clean, h.AttNames ?? "");
                var key = num >= 0 ? $"MOM|{p}|{series}|{num}" : $"MOM|{p}|{Norm(Regex.Replace(clean, @"draft", "", RegexOptions.IgnoreCase))}";
                var it = G(key, () => new Item { Type = "MOM", Project = p, Num = num, Series = series, No = num >= 0 ? $"MOM {num:000}" : "MOM", Via = "Meeting" });
                it.Mails.Add(h);
                if (it.Title.Length == 0 || (!ReplyRx.IsMatch(s) && !Regex.IsMatch(s, "draft", RegexOptions.IgnoreCase))) it.Title = series + (held.Length > 0 ? " · held " + held : "");
                continue;
            }
            // 3 · Document numbers: SLV-ZUC-CW-WIR-15280_Rev00, RS5-C7-ACC-AR-WIR-00409-R01, SLV-ZUC-HSE-GEN-00090 …
            var m = rx?.Match(s);
            if (m is { Success: true })
            {
                var no = m.Groups[1].Value.ToUpperInvariant();
                var type = m.Groups[2].Value.ToUpperInvariant();
                if (type == "GEN") { if (!no.Contains("-HSE-GEN-")) continue; type = "HSE"; }
                if (type == "LTR" && PayRx.IsMatch(s)) type = "PAY";
                if (!want.Contains(type)) goto words;
                // A project code read wrong (“FSLV”, “LV”…) is put right from the known projects.
                var proj = no.Split('-')[0];
                if (!known.Contains(proj))
                {
                    var k = known.FirstOrDefault(x => proj.EndsWith(x, StringComparison.OrdinalIgnoreCase) && x.Length >= 2 && !x.Contains('-'));
                    if (k != null) { no = k + no[proj.Length..]; proj = k; }
                    else { var pj = ProjOf(h); if (pj.Length > 0) proj = pj; }
                }
                if (alias.TryGetValue(proj, out var canon)) proj = canon;      // RS5 → RS5-C7, SAADIYAT → Nouran
                int rev = int.TryParse(m.Groups[4].Value, out var rv) ? rv : 0;
                var parts = no.Split('-');
                var it = G($"{type}|{no}|{rev}", () => new Item { No = no, Type = type, Project = proj, RevNum = rev, Disc = parts.Length >= 4 ? parts[^3] : "" });
                it.Mails.Add(h);
                var title = TitleOf(s, m);
                if (it.Title.Length == 0 || (!ReplyRx.IsMatch(s) && !FwdRx.IsMatch(s))) it.Title = title.Length > 0 ? title : it.Title;
                continue;
            }
            words:
            // 4 · Words only: payment applications, variations, authority submissions — one row per email thread.
            foreach (var (code, wrx) in builtin ? new[] { ("PAY", PayRx), ("VO", VoRx), ("AUTH", AuthRx) } : Array.Empty<(string, Regex)>())
            {
                if (!want.Contains(code) || !wrx.IsMatch(s)) continue;
                var p = ProjFrom(s, alias) is { Length: > 0 } pp ? pp : ProjOf(h);
                var key = $"{code}|{(string.IsNullOrEmpty(h.Conv) ? Norm(s) : Norm(s))}";
                var it = G(key, () => new Item { Type = code, Project = p, No = code, Via = "Email" });
                it.Mails.Add(h);
                if (it.Title.Length == 0 || !ReplyRx.IsMatch(s)) it.Title = clean.Trim();
                break;
            }
            // 5 · 🧩 Your field's items (invoices, tickets, contracts, approvals…): one row per reference, or per thread when it has no number.
            foreach (var (t, trx, _) in refTypes)
            {
                var rm = trx.Match(s);
                if (!rm.Success) continue;
                var refv = Regex.Replace(rm.Value.Trim(), @"\s+", " ");
                bool numbered = refv.Any(char.IsDigit);
                var p = ProjOf(h);
                var key = numbered ? $"{t.Code}|{refv.ToUpperInvariant()}" : $"{t.Code}|{Norm(s)}";
                var it = G(key, () => new Item { Type = t.Code, Project = p, No = numbered ? refv : t.Code, Via = "Email" });
                it.Mails.Add(h);
                if (it.Project.Length == 0) it.Project = p;
                if (it.Title.Length == 0 || (!ReplyRx.IsMatch(s) && !FwdRx.IsMatch(s))) it.Title = clean.Trim();
                break;
            }
        }
        foreach (var (k, d) in determ)
            if (groups.TryGetValue("EI|" + k, out var ei)) ei.Why = $"Engineer’s Determination {d.Date:dd MMM yyyy}";

        // ---------------------------------------------------------------- each document: submitted · replied · closed
        var res = new Result { Mails = mails.Count, Opt = o };
        foreach (var it in groups.Values)
        {
            var def = Def(it.Type) ?? Types[0];
            var ms = it.Mails.OrderBy(x => x.Date).ToList();
            it.Emails = ms.Count;
            var thinkproject = ms.Any(x => (x.FromAddr ?? "").Contains("thinkproject", StringComparison.OrdinalIgnoreCase) || (x.Body ?? "").Contains("thinkproject", StringComparison.OrdinalIgnoreCase));
            if (thinkproject) it.Via = "Thinkproject"; else if (it.Via.Length == 0) it.Via = "Outlook";
            bool mzaIssues = def.Close is "mzaconfirm" or "contractor" || (def.Code is "LTR" or "VO" or "AUTH" or "MOM" || def.Find == "ref") && ( ms.FirstOrDefault(x => !ReplyRx.IsMatch(x.Subject ?? "") && !FwdRx.IsMatch(x.Subject ?? "")) is { } f0 && IsMza(f0));
            // The submission (or the issue): the first email that is not a reply or a forward — from the contractor, or from MZA for what MZA issues.
            it.Sub = ms.FirstOrDefault(x => !ReplyRx.IsMatch(x.Subject ?? "") && !FwdRx.IsMatch(x.Subject ?? "") && (mzaIssues || def.Close == "received" || def.Code is "AUTH" or "MOM" || def.Find == "ref" || !IsMza(x)))
                     ?? (def.Code is "EI" or "MOM" or "AUTH" or "VO" or "PAY" or "LTR" || def.Find == "ref" ? ms.FirstOrDefault(x => !FwdRx.IsMatch(x.Subject ?? "")) : null);
            it.Submitted = it.Sub?.Date;
            if (thinkproject && it.Sub != null) it.Deadline = TpDates(it.Sub.Body).Item1;
            var after = ms.Where(x => it.Sub == null || x.Date > it.Sub.Date).ToList();
            string reviewing = "";
            switch (def.Close)
            {
                case "code":
                    foreach (var x in after)
                    {
                        var s = x.Subject ?? ""; var body = x.Body ?? ""; var at = x.AttNames ?? "";
                        bool fromMza = IsMza(x);
                        bool withResponse = s.Contains("with MZA Response", StringComparison.OrdinalIgnoreCase);
                        if (!fromMza && !withResponse) continue;
                        bool done = withResponse || (thinkproject
                            ? Regex.IsMatch(body, @"Approval completed[\s\S]{0,600}?\byes\b", RegexOptions.IgnoreCase) || Regex.IsMatch(body, @"Final step", RegexOptions.IgnoreCase) || Regex.IsMatch(body + " " + at, @"-C[1-5]\.pdf", RegexOptions.IgnoreCase)
                            : closedFiles.Any(c => at.Contains(c, StringComparison.OrdinalIgnoreCase)) || Regex.IsMatch(s + " " + at, @"code\s*-?\s*[1-5]", RegexOptions.IgnoreCase) || (ReplyRx.IsMatch(s) && (x.Sent || Regex.IsMatch(body.Length > 1500 ? body[..1500] : body, @"\bcode\s*[1-5]\b|approved|no objection|rejected|revise and resubmit", RegexOptions.IgnoreCase))));
                        var code = CodeOf(body, at, s);
                        if (done) { Close(it, x, code); break; }
                        if (ReplyRx.IsMatch(s) && fromMza) reviewing = "Reviewing";
                    }
                    break;
                case "reply":
                {
                    bool issuerMza = it.Sub != null && IsMza(it.Sub);
                    var r = after.FirstOrDefault(x => IsMza(x) != issuerMza && !FwdRx.IsMatch(x.Subject ?? ""));
                    if (r != null) Close(it, r, CodeOf(r.Body, r.AttNames, r.Subject));
                    break;
                }
                case "anyreply":
                {
                    var r = after.FirstOrDefault(x => !FwdRx.IsMatch(x.Subject ?? "") && (it.Sub == null || !string.Equals(x.FromAddr, it.Sub.FromAddr, StringComparison.OrdinalIgnoreCase)));
                    if (r != null) Close(it, r, "");
                    break;
                }
                case "contractor":
                {
                    var r = after.FirstOrDefault(x => !IsMza(x) && !FwdRx.IsMatch(x.Subject ?? ""));
                    if (r != null) Close(it, r, "");
                    break;
                }
                case "mzaconfirm":
                {
                    var c = after.FirstOrDefault(x => !IsMza(x) && !FwdRx.IsMatch(x.Subject ?? ""));
                    if (c != null)
                    {
                        var conf = after.FirstOrDefault(x => x.Date > c.Date && IsMza(x) && Regex.IsMatch((x.Subject ?? "") + " " + (x.AttNames ?? "") + " " + First(x.Body, 1200), @"\bclosed?\b|closure|accepted|verified|rectified", RegexOptions.IgnoreCase));
                        if (conf != null) Close(it, conf, "");
                        else { reviewing = "Replied"; it.Why = $"contractor replied {c.Date:dd MMM HH:mm} · waiting for {us} to close it"; }
                    }
                    break;
                }
                case "done":
                {
                    // 🧩 Closed when a later email says so (paid · signed · resolved · approved …); a reply alone = Replied.
                    var drx = refTypes.FirstOrDefault(x => x.t.Code == def.Code).Done ?? (def.Done.Length > 0 ? new Regex(def.Done, RegexOptions.IgnoreCase) : null);
                    var d = drx == null ? null : after.FirstOrDefault(x => !FwdRx.IsMatch(x.Subject ?? "") && drx.IsMatch((x.Subject ?? "") + " " + MailIndex.NewPart(First(x.Body, 1500)) + " " + (x.AttNames ?? "")));
                    if (d != null) Close(it, d, "");
                    else if (after.Any(x => !FwdRx.IsMatch(x.Subject ?? "") && (it.Sub == null || !string.Equals(x.FromAddr, it.Sub.FromAddr, StringComparison.OrdinalIgnoreCase))))
                        reviewing = "Replied";
                    break;
                }
                case "final":
                {
                    var fin = ms.FirstOrDefault(x => !Regex.IsMatch((x.Subject ?? "") + " " + (x.AttNames ?? ""), @"draft", RegexOptions.IgnoreCase) && !ReplyRx.IsMatch(x.Subject ?? ""));
                    it.Sub ??= ms.FirstOrDefault();
                    it.Submitted = it.Sub?.Date;
                    if (fin != null) { it.Rep = fin; it.Reply = fin.Date; it.By = fin.Sent ? us + " (me)" : fin.FromName ?? ""; }
                    else reviewing = "Draft";
                    break;
                }
            }
            if (it.Num >= 0 && it.Type == "MOM" && it.Series.Length > 0) it.No = $"MOM {it.Num:000}";
            (it.Tower, it.Zone, it.Level, it.Apt, it.LevelNum) = Place(it.Title);
            if (def.Close == "received") it.Status = "Received";
            else if (it.Rep != null) it.Status = "Closed";
            else if (it.Sub == null) it.Status = "?";
            else
            {
                var lim = Brain.Num("overdue." + it.Type, def.Overdue, brain);
                var due = it.Deadline ?? it.Submitted!.Value.AddDays(lim);
                it.Status = lim > 0 && DateTime.Now > due ? "Overdue" : reviewing.Length > 0 ? reviewing : "Open";
                if (reviewing == "Draft" && it.Status == "Overdue") it.Status = "Draft";
                it.Deadline ??= due;
            }
            var why = new List<string>();
            if (it.Sub != null) why.Add($"{(def.Close is "mzaconfirm" or "contractor" ? "issued" : "submitted")} {it.Submitted:dd MMM HH:mm} by {it.Sub.FromName} ({it.Via})");
            if (it.Ref.Length > 0) why.Add("ref " + it.Ref);
            if (it.Rep != null) why.Add($"{(def.Close == "final" ? "final minutes" : "closed")} {it.Reply:dd MMM HH:mm} by {it.By}" + (it.Code.Length > 0 ? " · " + it.Code : ""));
            else if (it.Status == "Draft") why.Add("draft only — final minutes not issued yet");
            else if (it.Why.Length == 0 && it.Sub != null && def.Close != "received" && reviewing != "Replied") why.Add($"no {(def.Close switch { "contractor" => "contractor acknowledgement", "anyreply" => "reply", "done" => "closing reply", "reply" when !builtin => "reply", _ => us + " reply" })} yet · due {it.Deadline:dd MMM}");
            if (it.Why.Length > 0) why.Add(it.Why);
            it.Why = string.Join(" · ", why);
        }
        // Revisions: an older revision still waiting is superseded by the newer one.
        foreach (var g in groups.Values.Where(i => Def(i.Type)?.Find == "code").GroupBy(i => i.Type + "|" + i.No))
        {
            var top = g.Max(i => i.RevNum);
            foreach (var it in g)
            {
                it.Rev = (it.Via == "Thinkproject" || it.Project.Equals("SLV", StringComparison.OrdinalIgnoreCase) ? "Rev" : "R") + it.RevNum.ToString("00");
                if (it.RevNum < top && it.Status is "Open" or "Overdue" or "Reviewing") { it.Status = "Superseded"; it.Why += " · a later revision exists"; }
            }
        }
        var hiddenP = LogPrefs.HiddenProjects; var hiddenD = LogPrefs.HiddenDiscs; var discGroups = LogPrefs.Groups();
        // Registers: a missing EI or meeting number in the sequence.
        // Only what was issued inside your period (an old EI mentioned in a recent reply does not open a false gap).
        foreach (var g in groups.Values.Where(i => i.Num >= 0 && i.Type is "EI" or "MOM" && i.Sub != null && i.Submitted >= o.From && i.Submitted <= o.To
                     && (o.Projects.Count == 0 || o.Projects.Any(p => p.Equals(i.Project, StringComparison.OrdinalIgnoreCase)))).GroupBy(i => i.Type + "|" + i.Project + "|" + i.Series))
        {
            var nums = g.Select(i => i.Num).Distinct().OrderBy(x => x).ToList();
            if (nums.Count < 2) continue;
            var miss = Enumerable.Range(nums[0], nums[^1] - nums[0] + 1).Except(nums).Take(20).ToList();
            var lab = g.First().Type == "EI" ? $"{g.First().Project} EI" : $"{g.First().Project} {g.First().Series}";
            if (miss.Count > 0) res.Gaps.Add($"{lab}: no email found for no. {string.Join(", ", miss)} (issued in this period: {nums[0]} to {nums[^1]})");
        }
        foreach (var it in groups.Values)
        {
            var chk = new List<string>();
            if (it.Sub == null) chk.Add("its first email is not in the index for this period (only a reply or forward was found)");
            if (it.Type is "WIR" or "MIR" && it.Tower.Length + it.Zone.Length + it.Level.Length == 0) chk.Add("no tower / zone / level in the title");
            if (it.Project.Length == 0) chk.Add("project not recognised");
            it.Check = string.Join("; ", chk);
            // Your filters (the period is the submission date; items without submission are kept by their reply date).
            var when = it.Submitted ?? it.Reply;
            if (when == null || when < o.From || when > o.To) continue;
            if (o.Projects.Count > 0 && !o.Projects.Any(p => p.Equals(it.Project, StringComparison.OrdinalIgnoreCase))) continue;
            if (o.Projects.Count == 0 && hiddenP.Contains(it.Project)) continue;          // ✎ hidden projects are out of “All”
            if (it.Disc.Length > 0 && hiddenD.Contains(it.Disc) && !LogPrefs.DiscFits(o.Discipline, it.Disc, discGroups)) continue;
            if (o.Tower.Length > 0 && !it.Tower.Equals(o.Tower, StringComparison.OrdinalIgnoreCase) && !(o.Tower == "TH" && it.Tower.StartsWith("TH"))) continue;
            if (o.Zone.Length > 0 && !it.Zone.StartsWith(o.Zone, StringComparison.OrdinalIgnoreCase)) continue;
            if ((o.LevelFrom > -99 || o.LevelTo < 999) && (it.LevelNum == null || it.LevelNum < o.LevelFrom || it.LevelNum > o.LevelTo)) continue;
            res.Discs[it.Disc] = res.Discs.GetValueOrDefault(it.Disc) + 1;
            if (o.Discipline.Length > 0 && !LogPrefs.DiscFits(o.Discipline, it.Disc, discGroups)) continue;
            foreach (var st in new[] { it.Status is "Open" or "Reviewing" or "Replied" or "Draft" or "Overdue" ? "Open" : "", it.Status, it.Code is "Code 3" or "Code 4" ? "Code3/4" : "", it.Code })
                if (st.Length > 0) res.Statuses[st] = res.Statuses.GetValueOrDefault(st) + 1;
            if (o.Status.Length > 0 && !(o.Status == "Code3/4" ? it.Code is "Code 3" or "Code 4"
                : o.Status.StartsWith("Code ") ? it.Code.Equals(o.Status, StringComparison.OrdinalIgnoreCase)
                : o.Status == "Open" ? it.Status is "Open" or "Reviewing" or "Replied" or "Draft" or "Overdue"
                : it.Status.Equals(o.Status, StringComparison.OrdinalIgnoreCase))) continue;
            res.Items.Add(it);
        }
        res.Items = res.Items.OrderByDescending(i => i.Submitted ?? i.Reply).ToList();
        res.Seconds = sw.Elapsed.TotalSeconds;
        return res;
    }

    static void Close(Item it, MailHit x, string code)
    {
        it.Rep = x; it.Reply = x.Date; it.By = x.Sent ? Tracker.UsShort + " (me)" : (x.FromName ?? "");
        if (!string.IsNullOrEmpty(code)) it.Code = code;
    }

    static string First(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length > n ? s[..n] : s;

    /// <summary>The project named in a subject: a document number (SLV-…, RS5-…) or the project's name.</summary>
    static string ProjFrom(string s, Dictionary<string, string> alias)
    {
        s ??= "";
        foreach (Match m in Regex.Matches(s, @"(?<![A-Za-z0-9])([A-Z][A-Z0-9]{1,6})-[A-Z0-9]"))
            if (alias.TryGetValue(m.Groups[1].Value, out var c)) return c;
        // Codes, old codes and names (“Sea La Vie”, “SLV”, “RS5”, “Nouran”…) — the longest first.
        foreach (var (k, c) in alias.OrderByDescending(x => x.Key.Length))
            if ((k.Length >= 3 || k.Any(char.IsDigit)) && Regex.IsMatch(s, @"(?<![\p{L}\p{N}])" + Regex.Escape(k).Replace("\\ ", @"\s*") + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)) return c;
        return "";
    }

    /// <summary>A meeting: its series (“Sea La Vie · Progress”, “Weekly Progress”, “QA/QC”, “Kick-off”…), number and the day it was held.</summary>
    static (string Series, int Num, string Held) MomOf(string s, string atts)
    {
        string held = "";
        var hm = Regex.Match(s, @"held on\s*(\d{1,2}[-\s][A-Za-z]{3,9}[-\s]\d{4})", RegexOptions.IgnoreCase);
        if (hm.Success) held = hm.Groups[1].Value;
        else if (Regex.Match(s, @"\b(\d{2}-\d{2}-\d{4})\b") is { Success: true } dm) held = dm.Groups[1].Value;
        int num = -1; string series = "";
        var a = Regex.Match(s, @"(?:^|\|)\s*([^|]*?)\s*\|?\s*([A-Za-z/ &]*?)\s*Meeting\s*No\.?\s*0*(\d{1,4})", RegexOptions.IgnoreCase);
        var b = Regex.Match(s, @"-MOM-0*(\d{1,4})", RegexOptions.IgnoreCase);
        var c = Regex.Match(s, @"\bMoM\s*(?:no\.?|#)\s*0*(\d{1,4})", RegexOptions.IgnoreCase);
        var k = Regex.Match(s, @"(.*?)\|?\s*Kick[\s-]?Off\s+Meeting", RegexOptions.IgnoreCase);
        if (b.Success)
        {
            num = int.Parse(b.Groups[1].Value);
            var w = Regex.Match(s, @"Minutes of\s+(.+?)\s+Meeting", RegexOptions.IgnoreCase);
            series = w.Success ? w.Groups[1].Value.Trim() : "Weekly Progress";
        }
        else if (a.Success)
        {
            num = int.Parse(a.Groups[3].Value);
            var where = a.Groups[1].Value.Trim().Trim('|').Trim();
            var kind = a.Groups[2].Value.Trim();
            series = (where.Length > 0 && where.Length < 60 ? where + " · " : "") + (kind.Length > 0 ? kind : "Meeting");
        }
        else if (c.Success)
        {
            num = int.Parse(c.Groups[1].Value);
            var w = Regex.Match(s, @"\|\s*([^|]*?)\s*(?:DRAFT\s*)?MoM", RegexOptions.IgnoreCase);
            series = w.Success ? w.Groups[1].Value.Trim() : "Meeting";
        }
        else if (k.Success) series = (k.Groups[1].Value.Trim().Trim('|').Trim() is { Length: > 0 and < 70 } kw ? kw + " · " : "") + "Kick-off";
        else
        {
            var w = Regex.Split(s, @"\|").Select(x => x.Trim()).Where(x => x.Length > 0 && !Regex.IsMatch(x, @"draft|mom|minutes", RegexOptions.IgnoreCase)).FirstOrDefault();
            series = w ?? "Meeting";
        }
        series = Regex.Replace(series, @"\s{2,}", " ").Trim(' ', '-', '·');
        return (series.Length > 0 ? series : "Meeting", num, held);
    }

    static string TitleOf(string subj, Match m)
    {
        var after = subj[(m.Index + m.Length)..];
        var t = Regex.Replace(after, @"^[\s_\-–:]+", "");
        t = Regex.Replace(t, @"^\(updated\)[_\s]*", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"_with MZA Response\s*$", "", RegexOptions.IgnoreCase);
        return t.Trim(' ', '_', '-');
    }

    /// <summary>Thinkproject notification table: the Deadline and the Date of creation (dd/mm/yyyy).</summary>
    static (DateTime?, DateTime?) TpDates(string body)
    {
        body ??= "";
        var i = body.IndexOf("Deadline", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return (null, null);
        var seg = body.Substring(i, Math.Min(1500, body.Length - i));
        var ds = Regex.Matches(seg, @"\b(\d{2})/(\d{2})/(\d{4})\b").Select(x => new DateTime(int.Parse(x.Groups[3].Value), int.Parse(x.Groups[2].Value), int.Parse(x.Groups[1].Value))).ToList();
        // “-/-” in the deadline column means none: then only the creation date is there.
        var dash = Regex.IsMatch(seg, @"\t-/-\t");
        DateTime? dl = null, created = null;
        if (ds.Count >= 2) { dl = ds[0].AddHours(23).AddMinutes(59); created = ds[1]; }
        else if (ds.Count == 1) { if (dash) created = ds[0]; else dl = ds[0].AddHours(23).AddMinutes(59); }
        return (dl, created);
    }

    static string CodeOf(string body, string atts, string subj)
    {
        var m = Regex.Match(body ?? "", @"Approval result[\s\S]{0,300}?\bCode\s*([1-5])\b", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match((atts ?? "") + " " + (subj ?? ""), @"[-_ ]C([1-5])(?:[-_ .]|\.pdf|$)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match((subj ?? "") + " " + (atts ?? "") + " " + (body ?? "").Substring(0, Math.Min(800, (body ?? "").Length)), @"\bCode\s*[-:]?\s*([1-5])\b", RegexOptions.IgnoreCase);
        return m.Success ? "Code " + m.Groups[1].Value : "";
    }

    /// <summary>Tower · zone · level · apartments from a title: “T 3_L3 Apt”, “Z-1,L-4 to 6”, “TH 17,18,19 @ Z-4 & 5,L-GF”, “10th Floor”, “Zone 1 L02 podium”.</summary>
    public static (string Tower, string Zone, string Level, string Apt, int? LevelNum) Place(string title)
    {
        var s = " " + (title ?? "").Replace('_', ' ') + " ";
        string T = "", Z = "", L = "", A = ""; int? n = null;
        var x = Regex.Match(s, @"\b(?:Tower|T)\s?[-_]?\s?(\d)\b", RegexOptions.IgnoreCase);
        if (x.Success) T = "T" + x.Groups[1].Value;
        else { x = Regex.Match(s, @"\bTH\s?[-_]?\s?(\d[\d ,&\-]*\d|\d)"); if (x.Success) T = "TH " + Regex.Replace(x.Groups[1].Value, @"\s", ""); }     // TH 17 · TH-30 · TH-25,26,27
        x = Regex.Match(s, @"\b(?:Zone|Z)\s?[-_]?\s?0?(\d)(?:\s?&\s?(\d))?\b", RegexOptions.IgnoreCase);
        if (x.Success) Z = "Z" + x.Groups[1].Value + (x.Groups[2].Success ? "–" + x.Groups[2].Value : "");
        var y = Regex.Match(s, @"\b(?:Level|L)\s?[-_]?\s?(\d{1,2})\s*(?:to|-|–)\s*(\d{1,2})\b", RegexOptions.IgnoreCase);
        if (y.Success) { L = $"L{int.Parse(y.Groups[1].Value)}–{int.Parse(y.Groups[2].Value)}"; n = int.Parse(y.Groups[1].Value); }
        else
        {
            x = Regex.Match(s, @"\b(?:Level|L)\s?[-_]?\s?(\d{1,2}|GF|FF|UG|B\d)\b", RegexOptions.IgnoreCase);
            if (!x.Success) x = Regex.Match(s, @"\b(\d{1,2})(?:st|nd|rd|th)\s+Floor", RegexOptions.IgnoreCase);
            if (x.Success)
            {
                var v = x.Groups[1].Value.ToUpperInvariant();
                if (int.TryParse(v, out var k)) { L = "L" + k; n = k; }
                else { L = v; n = v == "GF" ? 0 : v == "FF" ? 1 : v == "UG" ? -1 : v.StartsWith("B") && int.TryParse(v[1..], out var bn) ? -bn : (int?)null; }
            }
            else
                foreach (var (w, v, k) in new[] { ("ground floor", "GF", 0), ("first floor", "L1", 1), ("attic", "Attic", 99), ("roof", "Roof", 100), ("podium", "Podium", 1), ("basement", "B", -1) })
                    if (s.Contains(w, StringComparison.OrdinalIgnoreCase)) { L = v; n = k; break; }
        }
        if (s.Contains("podium", StringComparison.OrdinalIgnoreCase) && L.Length > 0 && L != "Podium") L += " podium";
        x = Regex.Match(s, @"\b(?:apt|apartment)s?\.?\s*-?\s*(\d{3,4}(?:\s*(?:,|&|-|to)\s*\d{3,4})*)", RegexOptions.IgnoreCase);
        if (x.Success) A = Regex.Replace(x.Groups[1].Value, @"\s+", " ").Trim();
        return (T, Z, L, A, n);
    }

    // ================================================================ for the window, the connector and the brain test

    public static JsonObject Json(Item i, Activity.Hider hide = null) => new()
    {
        ["proj"] = i.Project, ["via"] = i.Via, ["type"] = i.Type, ["no"] = i.No, ["rev"] = i.Rev, ["disc"] = i.Disc, ["title"] = i.Title,
        ["tower"] = i.Tower, ["zone"] = i.Zone, ["level"] = i.Level, ["apt"] = i.Apt,
        ["sub"] = i.Submitted?.ToString("dd MMM yyyy HH:mm") ?? "", ["due"] = i.Deadline?.ToString("dd MMM yyyy") ?? "", ["reply"] = i.Reply?.ToString("dd MMM yyyy HH:mm") ?? "",
        ["by"] = hide != null && i.By.Length > 0 ? hide.Hide(i.By) : i.By, ["code"] = i.Code, ["days"] = i.Submitted != null ? i.Days : null, ["status"] = i.Status,
        ["why"] = hide != null ? hide.Hide(i.Why) : i.Why, ["check"] = i.Check, ["series"] = i.Series, ["ref"] = i.Ref, ["label"] = Def(i.Type)?.Label ?? i.Type,
        ["entry"] = (i.Rep ?? i.Sub)?.Entry ?? "", ["store"] = (i.Rep ?? i.Sub)?.StoreId ?? "", ["subEntry"] = i.Sub?.Entry ?? "", ["subStore"] = i.Sub?.StoreId ?? ""
    };

    public static JsonObject Kpis(Result r)
    {
        var closed = r.Items.Where(i => i.Submitted != null && i.Reply != null).ToList();
        return new JsonObject
        {
            ["found"] = r.Items.Count, ["closed"] = r.Items.Count(i => i.Status == "Closed"), ["open"] = r.Items.Count(i => i.Status is "Open" or "Reviewing" or "Replied" or "Draft"),
            ["overdue"] = r.Items.Count(i => i.Status == "Overdue"), ["check"] = r.Items.Count(i => i.Check.Length > 0),
            ["avg"] = closed.Count > 0 ? Math.Round(closed.Average(i => (i.Reply!.Value - i.Submitted!.Value).TotalDays), 1) : null,
            ["mails"] = r.Mails, ["secs"] = Math.Round(r.Seconds, 1), ["gaps"] = new JsonArray(r.Gaps.Select(g => (JsonNode)g).ToArray())
        };
    }

    /// <summary>One document explained (for the brain chat: “why was WIR-15280 marked Closed?”).</summary>
    public static string Explain(string no)
    {
        var r = Run(new Options { From = DateTime.Today.AddDays(-400) });
        var hits = r.Items.Where(i => i.No.Contains(no, StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Count == 0 ? $"{no}: not found in the indexed emails of the last 400 days."
            : string.Join("\n", hits.Select(i => $"{i.No} {i.Rev} · {i.Status}{(i.Code.Length > 0 ? " · " + i.Code : "")} · {i.Why}{(i.Check.Length > 0 ? " · check: " + i.Check : "")}"));
    }

    // ================================================================ 📗 Excel: Log · Open & overdue · Summary · Check · About

    public static string Excel(Result r, string title)
    {
        var dir = Prompts.Folder;
        Directory.CreateDirectory(dir);
        var safe = Regex.Replace(title, @"[\\/:*?""<>|]+", " ").Trim();
        var path = Path.Combine(dir, $"{safe} — {DateTime.Now:dd MMM yyyy HHmm}.xlsx");
        string[] head = { "Project", "Via", "Type", "Document no.", "Rev", "Disc.", "Title", "Tower", "Zone", "Level", "Apt / TH", "Submitted", "Deadline", Tracker.UsShort + " reply", "Replied by", "Code", "Days", "Status", "Why" };
        int[] widths = { 8, 12, 7, 26, 7, 7, 60, 9, 8, 11, 14, 17, 12, 17, 22, 9, 6, 11, 70 };
        string[] Row(Item i) => new[] { i.Project, i.Via, i.Type, i.No, i.Rev, i.Disc, i.Title, i.Tower, i.Zone, i.Level, i.Apt, i.Submitted?.ToString("dd MMM yyyy HH:mm") ?? "",
            i.Deadline?.ToString("dd MMM yyyy") ?? "", i.Reply?.ToString("dd MMM yyyy HH:mm") ?? "", i.By, i.Code, i.Submitted != null ? i.Days.ToString() : "", i.Status, i.Why };
        var o = r.Opt;
        var choices = $"Projects {(o.Projects.Count > 0 ? string.Join(" + ", o.Projects) : "all")} · {(o.Types.Count > 0 ? string.Join(", ", o.Types) : "all types")} · {o.From:dd MMM yyyy} – {o.To:dd MMM yyyy}"
                      + (o.Tower.Length > 0 ? " · " + o.Tower : "") + (o.Zone.Length > 0 ? " · " + o.Zone : "") + (o.Discipline.Length > 0 ? " · " + o.Discipline : "") + (o.Status.Length > 0 ? " · " + o.Status : "");
        var sheets = new List<XSheet>
        {
            new("Log", "📋 " + title, choices + $" · {r.Items.Count} item(s) from {r.Mails:N0} emails · Designed by A.HAMROUNI", head, widths, r.Items.Select(Row).ToList(), 17),
            new("Open & overdue", $"⏳ Still waiting · oldest first", "Overdue = past the Thinkproject Deadline, else the days set in 🧠 Nova Brain (📋 Submittal logs)", head, widths,
                r.Items.Where(i => i.Status is "Open" or "Overdue" or "Reviewing" or "Replied" or "Draft").OrderBy(i => i.Submitted).Select(Row).ToList(), 17),
        };
        // Summary
        var sum = new List<string[]>();
        void Block(string name, Func<Item, string> key, string[] cols, Func<Item, string, bool> hit)
        {
            sum.Add(new[] { name }); sum.Add(new[] { "" }.Concat(cols).Append("Total").ToArray());
            foreach (var g in r.Items.Where(i => key(i) != null).GroupBy(key).OrderBy(g => g.Key))
                sum.Add(new[] { g.Key }.Concat(cols.Select(c => { var n = g.Count(i => hit(i, c)); return n > 0 ? n.ToString() : ""; })).Append(g.Count().ToString()).ToArray());
            sum.Add(new[] { "" });
        }
        Block("By project and type", i => $"{i.Project} · {i.Type}", new[] { "Closed", "Open", "Reviewing", "Replied", "Draft", "Overdue", "Superseded", "Received" }, (i, c) => i.Status == c);
        Block("By tower (WIR / MIR)", i => i.Type is "WIR" or "MIR" ? (i.Tower.Length > 0 ? i.Tower : "— not stated") : null, new[] { "Closed", "Open", "Overdue" }, (i, c) => i.Status == c);
        Block(Tracker.UsShort + " codes", i => i.Code.Length > 0 ? $"{i.Project} · {i.Type}" : null, new[] { "Code 1", "Code 2", "Code 3", "Code 4" }, (i, c) => i.Code == c);
        sheets.Add(new("Summary", "📊 Summary", choices, null, new[] { 30, 10, 10, 10, 10, 10, 10, 10, 10, 9 }, sum, -1));
        var chk = r.Items.Where(i => i.Check.Length > 0).Select(i => Row(i).Append(i.Check).ToArray()).ToList();
        // Registers: a missing EI or meeting number.
        foreach (var g in r.Gaps) chk.Insert(0, new[] { "", "", "", "", "", "", "⚠ " + g }.Concat(Enumerable.Repeat("", head.Length - 7)).Append("missing number in the register").ToArray());
        sheets.Add(new("Check", $"🔍 Check · {chk.Count} item(s) Nova could not fully place", "Nothing is dropped silently: each line says why", head.Append("Why it is here").ToArray(), widths.Append(60).ToArray(), chk, 17));
        var about = new List<string[]>
        {
            new[] { "Made by", "Nova Enhance · 📋 " + Tracker.LogName + " · " + Program.Shown + " · Designed by A.HAMROUNI" },
            new[] { "Your choices", choices },
            new[] { "Read", $"{r.Mails:N0} emails with a document number in the subject ({r.Seconds:0.0} s)" },
            new[] { "Rules", "🧩 " + Packs.Current.Name + (Tracker.Builtin ? " · " + Tracker.CurrentRole.Name : "") + " · us = " + Tracker.UsShort + " · 🧠 Nova Brain · 📋 Submittal logs (version " + Brain.Version + ")" }
        };
        foreach (var (s, rule) in Brain.Rules(Brain.Snapshot()).Where(x => x.Section["id"]?.ToString() is "submittal" or "location"))
            about.Add(new[] { rule["id"]?.ToString() ?? "", rule["text"]?.ToString() ?? "" });
        sheets.Add(new("About", "ℹ About this log", "", null, new[] { 22, 140 }, about, -1));
        WriteBook(path, sheets);
        return path;
    }

    record XSheet(string Name, string Title, string Sub, string[] Head, int[] Widths, List<string[]> Rows, int StatusCol);

    static string X(string s) => System.Security.SecurityElement.Escape(Regex.Replace(s ?? "", @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "")) ?? "";
    static string Col(int i) { var s = ""; i++; while (i > 0) { var m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; } return s; }

    static void WriteBook(string path, List<XSheet> sheets)
    {
        using var fs = File.Create(path);
        using var z = new ZipArchive(fs, ZipArchiveMode.Create);
        void W(string name, string xml) { using var s = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false)); s.Write(xml); }
        const string H = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
        var ct = new StringBuilder(H + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (int i = 1; i <= sheets.Count; i++) ct.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        W("[Content_Types].xml", ct.Append("</Types>").ToString());
        W("_rels/.rels", H + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        var rels = new StringBuilder(H + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        var wb = new StringBuilder(H + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        var names = new StringBuilder();
        for (int i = 0; i < sheets.Count; i++)
        {
            rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            wb.Append($"<sheet name=\"{X(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            if (sheets[i].Head != null)
                names.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">'{X(sheets[i].Name)}'!$A$4:${Col(sheets[i].Head.Length - 1)}${sheets[i].Rows.Count + 4}</definedName>");
        }
        rels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        W("xl/_rels/workbook.xml.rels", rels.ToString());
        W("xl/workbook.xml", wb.Append("</sheets>").Append(names.Length > 0 ? "<definedNames>" + names + "</definedNames>" : "").Append("</workbook>").ToString());
        // Styles: 0 normal · 1 header · 2 title · 3 note · 4 Closed · 5 Open · 6 Overdue · 7 Superseded · 8 bold · 9 Reviewing
        W("xl/styles.xml", H + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
          "<fonts count=\"9\"><font><sz val=\"10\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"14\"/><color rgb=\"FF2B2F3A\"/><name val=\"Calibri\"/></font><font><i/><sz val=\"9\"/><color rgb=\"FF6B7385\"/><name val=\"Calibri\"/></font>" +
          "<font><b/><sz val=\"10\"/><color rgb=\"FF137A44\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><color rgb=\"FF8A5A00\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><color rgb=\"FFB42318\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><color rgb=\"FF666666\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><name val=\"Calibri\"/></font></fonts>" +
          "<fills count=\"8\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF2B2F3A\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE3F6EC\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFF4DD\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFDE4E4\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFEEEEEE\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE8E5FF\"/></patternFill></fill></fills>" +
          "<borders count=\"2\"><border/><border><bottom style=\"thin\"><color rgb=\"FFD5D8DE\"/></bottom></border></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
          "<cellXfs count=\"10\"><xf borderId=\"1\" applyBorder=\"1\"/><xf fontId=\"1\" fillId=\"2\" applyFont=\"1\" applyFill=\"1\"/><xf fontId=\"2\" applyFont=\"1\"/><xf fontId=\"3\" applyFont=\"1\"/>" +
          "<xf fontId=\"4\" fillId=\"3\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/><xf fontId=\"5\" fillId=\"4\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/><xf fontId=\"6\" fillId=\"5\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/><xf fontId=\"7\" fillId=\"6\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/><xf fontId=\"8\" applyFont=\"1\"/><xf fontId=\"5\" fillId=\"7\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/></cellXfs></styleSheet>");
        for (int si = 0; si < sheets.Count; si++)
        {
            var sh = sheets[si];
            var sb = new StringBuilder(H + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (sh.Head != null) sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane xSplit=\"4\" ySplit=\"4\" topLeftCell=\"E5\" activePane=\"bottomRight\" state=\"frozen\"/></sheetView></sheetViews>");
            sb.Append("<cols>");
            for (int i = 0; i < sh.Widths.Length; i++) sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{sh.Widths[i]}\" customWidth=\"1\"/>");
            sb.Append("</cols><sheetData>");
            void Row(int r, IEnumerable<string> cells, Func<int, string, int> style)
            {
                sb.Append($"<row r=\"{r}\">");
                int c = 0;
                foreach (var v in cells)
                {
                    var st = style(c, v);
                    var refc = Col(c++) + r;
                    if (st == 0 && v.Length is > 0 and < 9 && int.TryParse(v, out var n)) sb.Append($"<c r=\"{refc}\" s=\"0\"><v>{n}</v></c>");
                    else sb.Append($"<c r=\"{refc}\" t=\"inlineStr\" s=\"{st}\"><is><t xml:space=\"preserve\">{X(v)}</t></is></c>");
                }
                sb.Append("</row>");
            }
            Row(1, new[] { sh.Title }, (_, _) => 2);
            Row(2, new[] { sh.Sub }, (_, _) => 3);
            int rr = 4;
            if (sh.Head != null) Row(rr++, sh.Head, (_, _) => 1);
            foreach (var row in sh.Rows)
            {
                bool header = sh.Head == null && row.Length > 1 && row[0] == "";
                bool block = sh.Head == null && row.Length == 1 && row[0].Length > 0;
                Row(rr++, row, (c, v) => header ? 1 : block ? 8 : c == sh.StatusCol ? v switch { "Closed" => 4, "Received" => 4, "Open" => 5, "Draft" => 5, "Overdue" => 6, "Superseded" => 7, "Reviewing" => 9, "Replied" => 9, _ => 0 } : (sh.Head == null && c == 0 ? 8 : 0));
            }
            sb.Append("</sheetData>");
            if (sh.Head != null) sb.Append($"<autoFilter ref=\"A4:{Col(sh.Head.Length - 1)}{Math.Max(5, sh.Rows.Count + 4)}\"/>");
            sb.Append("</worksheet>");
            W($"xl/worksheets/sheet{si + 1}.xml", sb.ToString());
        }
    }

    // ================================================================ “WIR log Tower 4, open ones” typed in Search for email

    /// <summary>Your words for each type (“method statements”, “engineer instructions”, “minutes”…).</summary>
    public static (string Code, Regex Rx)[] TypeWords => Tracker.Builtin ? BuiltinWords
        : Types.Where(t => t.Words.Length > 0 || t.Code == "MOM").Select(t => (t.Code, new Regex(t.Code == "MOM" ? @"\bMOMs?\b|\bminutes\b|\bmeetings?\b" : t.Words, RegexOptions.IgnoreCase))).ToArray();

    static readonly (string Code, Regex Rx)[] BuiltinWords = new (string, string)[]
    {
        ("WIR", @"\bWIRs?\b|work inspections?"), ("MIR", @"\bMIRs?\b|material inspections?"), ("MAR", @"\bMARs?\b|material (approvals?|submittals?)"),
        ("MSS", @"\bMSS\b|method statements?"), ("SDS", @"\bSDS\b|\bSHD\b|shop ?drawings?"), ("SSM", @"\bSSM\b|\bsamples?\b"), ("TSR", @"\bTSR\b|test reports?"),
        ("PQS", @"\bPQS\b|pre-?qualifications?"), ("RFI", @"\bRFIs?\b|requests? for information"), ("RPT", @"\bRPT\b|\bREP\b|(weekly|monthly|progress) reports?"),
        ("HSE", @"\bHSE\b"), ("CAL", @"\bCAL\b|calculations?"), ("RTS", @"\bRTS\b|requests? to start"), ("HOV", @"\bHOV\b|hand-?over"),
        ("PLN", @"\bPLN\b|planning|programmes?"), ("LTR", @"\bLTRs?\b|\bletters?\b"), ("PAY", @"\bpayments?\b|\bIPCs?\b|\bIPAs?\b"),
        ("DSR", @"\bDSRs?\b|daily (site )?reports?"), ("VO", @"\bVOs?\b|\bvariations?\b"),
        ("AUTH", @"authorit(y|ies)|\bADCD\b|civil defen[cs]e|\bNOCs?\b|municipality|\bAldar\b|\bMiral\b"),
        ("SOR", @"\bSORs?\b|site observations?"), ("NCR", @"\bNCRs?\b|non-?conformances?"), ("EI", @"\bEIs?\b|engineer\W{0,2}s? instructions?"),
        ("MOM", @"\bMOMs?\b|\bminutes\b|\bmeetings?\b|kick-?off")
    }.Select(x => (x.Item1, new Regex(x.Item2, RegexOptions.IgnoreCase))).ToArray();

    static readonly Regex ListWords = new(@"\b(log|register|list|show|which|how many|open|closed|overdue|pending|status|still|outstanding|issued|submitted|replied|not (yet )?(closed|replied|answered)|this (week|month)|last (week|month|\d+ days)|all (the )?)\b", RegexOptions.IgnoreCase);
    static readonly Regex ExactDoc = new(@"\b[A-Z0-9]{2,6}(?:-[A-Z0-9]{1,6}){2,5}-\d{3,6}\b");

    /// <summary>“Which WIRs in tower 4 are still open?”, “EI log this month”, “minutes of the progress meetings” → the 📋 log (a question about one exact document stays a search).</summary>
    public static bool Wanted(string q)
    {
        q ??= "";
        if (ExactDoc.IsMatch(q)) return false;
        if (Regex.IsMatch(q, @"\bsubmittal log\b|\btracking log\b|\b(WIR|MIR|MAR|RFI|EI|MOM|NCR|SOR) (log|register)\b", RegexOptions.IgnoreCase)) return true;
        return TypeWords.Any(t => t.Rx.IsMatch(q)) && ListWords.IsMatch(q);
    }

    /// <summary>Options read from your words: types, project, tower, zone, level, period, status.</summary>
    public static Options FromWords(string q, string project)
    {
        var o = new Options();
        foreach (var (code, rx) in TypeWords) if (rx.IsMatch(q) && !o.Types.Contains(code)) o.Types.Add(code);
        if (o.Types.Contains("RPT") && o.Types.Contains("DSR") && !Regex.IsMatch(q, @"\b(weekly|monthly|progress)\b", RegexOptions.IgnoreCase)) o.Types.Remove("RPT");
        if (Regex.IsMatch(q, @"inspection request", RegexOptions.IgnoreCase) && o.Types.Count == 0) o.Types.AddRange(new[] { "WIR", "MIR" });
        if (!string.IsNullOrEmpty(project) && project != "?") o.Projects.Add(project);
        foreach (var p in NovaEnhance.Projects.Mentioned(q)) if (!o.Projects.Contains(p, StringComparer.OrdinalIgnoreCase)) o.Projects.Add(p);
        var (t, z, _, _, n) = Place(q);
        o.Tower = t; o.Zone = z;
        var lv = Regex.Match(q, @"\b(?:Level|L)\s?(\d{1,2})\s*(?:to|-|–)\s*(\d{1,2})\b", RegexOptions.IgnoreCase);
        if (lv.Success) { o.LevelFrom = int.Parse(lv.Groups[1].Value); o.LevelTo = int.Parse(lv.Groups[2].Value); }
        else if (n != null) { o.LevelFrom = o.LevelTo = n.Value; }
        if (Regex.IsMatch(q, @"\boverdue\b", RegexOptions.IgnoreCase)) o.Status = "Overdue";
        else if (Regex.IsMatch(q, @"\bopen\b|not (yet )?(closed|replied)|pending", RegexOptions.IgnoreCase)) o.Status = "Open";
        else if (Regex.IsMatch(q, @"\bclosed\b", RegexOptions.IgnoreCase)) o.Status = "Closed";
        // Period: “1 Oct – 19 Nov”, “this month”, “last month”, “this week”, “last 30 days”.
        var mm = Regex.Matches(q, @"\b(\d{1,2})\s*(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s*(\d{4})?", RegexOptions.IgnoreCase);
        DateTime? D(Match m) { try { var y = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : DateTime.Today.Year; return DateTime.ParseExact($"{m.Groups[1].Value} {m.Groups[2].Value[..3]} {y}", "d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture); } catch { return null; } }
        if (mm.Count >= 2 && D(mm[0]) is { } a && D(mm[1]) is { } b) { o.From = a; o.To = b.AddDays(1).AddSeconds(-1); }
        else if (mm.Count == 1 && D(mm[0]) is { } a1) { o.From = a1; }
        else if (Regex.IsMatch(q, @"last month", RegexOptions.IgnoreCase)) { var f = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1); o.From = f; o.To = f.AddMonths(1).AddSeconds(-1); }
        else if (Regex.IsMatch(q, @"this month", RegexOptions.IgnoreCase)) o.From = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        else if (Regex.IsMatch(q, @"this week", RegexOptions.IgnoreCase)) o.From = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        else if (Regex.Match(q, @"last (\d{1,3}) days", RegexOptions.IgnoreCase) is { Success: true } ld) o.From = DateTime.Today.AddDays(-int.Parse(ld.Groups[1].Value));
        return o;
    }
}
