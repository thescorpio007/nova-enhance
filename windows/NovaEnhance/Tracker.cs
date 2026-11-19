using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🧩 Field packs for the 📋 log: what is tracked, who is “us”, and when an item counts as closed — for every person and every field.
/// Layers: core engine → field pack (Settings → About me: your field and, in construction, your role) → your company → 🧠 Nova Brain.
/// Your Brain always wins: a [log.all = …], [mza.match = …] or [overdue.X = …] you set there is used before anything here.
/// The construction pack is today's submittal log, unchanged. Designed by A.HAMROUNI
/// </summary>
static class Tracker
{
    /// <summary>A role inside construction: the types it follows by default (empty = every type).</summary>
    public record Role(string Id, string Icon, string Name, string Types, string Hint);

    public static readonly Role[] Roles =
    {
        new("arch", "📐", "Architect / design coordinator", "", "every submittal, letter, instruction and meeting"),
        new("pm", "🧭", "Project manager", "", "everything, with the overdue ones first"),
        new("dc", "🗂", "Document controller", "", "every transmittal and its reply"),
        new("mep", "🔌", "MEP engineer", "WIR, MIR, MAR, SDS, MSS, RFI, TSR, CAL, SSM, PQS, NCR, SOR, EI, MOM", "inspections, materials, shop drawings, RFIs, tests"),
        new("st", "🏗", "Structural / civil engineer", "WIR, MIR, MAR, SDS, MSS, RFI, TSR, CAL, NCR, SOR, EI, MOM", "inspections, method statements, calculations, tests"),
        new("qs", "💰", "QS / commercial", "PAY, VO, LTR, EI, AUTH, MOM", "payments, variations, letters, instructions"),
        new("site", "🦺", "Site engineer / inspector", "WIR, MIR, RTS, SOR, NCR, DSR, HSE, MOM", "inspections, observations, daily reports"),
        new("hse", "⛑", "HSE officer", "HSE, SOR, NCR, DSR, MOM", "HSE submittals, observations, NCRs")
    };

    public static Role CurrentRole => Roles.FirstOrDefault(r => r.Id == Store.Str("win_field_role", "arch")) ?? Roles[0];

    /// <summary>The construction pack: the built-in submittal types (WIR · MAR · EI · MOM …) — exactly as before.</summary>
    public static bool Builtin => Packs.IsConstruction;

    public static SubmittalLog.TypeDef[] Types => Builtin ? SubmittalLog.Builtin : ForPack(Packs.Current.Id);

    /// <summary>The types switched on when your Brain says nothing: your role's (construction) or every type of your field.</summary>
    public static string DefaultTypes => Builtin && CurrentRole.Types.Length > 0 ? CurrentRole.Types : string.Join(", ", Types.Select(t => t.Code));

    public static string LogName => Builtin ? "Submittal log" : "Tracking log";

    // ------------------------------------------------------------------ who is “us”

    static IEnumerable<string> MyDomains() =>
        Me.Addrs.Where(a => a.Contains('@')).Select(a => a[(a.IndexOf('@') + 1)..].ToLowerInvariant())
            .Where(d => d is not ("gmail.com" or "outlook.com" or "hotmail.com" or "yahoo.com" or "live.com" or "icloud.com")).Distinct();

    /// <summary>Names and email domains that mean “us” (Settings → About me → My company). Default: your own email domains (MZA keeps its names).</summary>
    public static string Us
    {
        get
        {
            var s = Store.Str("win_my_side", "").Trim();
            if (s.Length > 0) return s;
            var d = MyDomains().ToList();
            if (d.Any(x => x.Contains("mz-architects")) || (d.Count == 0 && Builtin)) return "MZ ARCHITECTS, mz-architects.com";
            return string.Join(", ", d);
        }
        set => Store.Set("win_my_side", value?.Trim() ?? "");
    }

    /// <summary>The short name in the log (“closed by MZA”, “MZA reply”).</summary>
    public static string UsShort
    {
        get
        {
            var s = Store.Str("win_my_short", "").Trim();
            if (s.Length > 0) return s;
            var d = MyDomains().FirstOrDefault();
            if (d == null) return Builtin ? "MZA" : "us";
            if (d.Contains("mz-architects")) return "MZA";
            var w = d.Split('.')[0];
            return w.Length <= 5 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..];
        }
        set => Store.Set("win_my_short", value?.Trim() ?? "");
    }

    // ------------------------------------------------------------------ the other fields (D)

    static SubmittalLog.TypeDef T(string code, string label, string close, int overdue, string rx, string like, string words, string done = "") =>
        new(code, label, "ref", close, overdue, rx, like, done, words);

    static readonly SubmittalLog.TypeDef Minutes = new("MOM", "Minutes of meeting", "words", "final", 7);

    const string RefNo = @"\s*(?:no\.?|number|#|:)?\s*[-/#:]?\s*";

    /// <summary>What each field tracks. Find "ref": a reference in the subject (one row per reference, or per thread when it has no number).
    /// Close: reply (the other side answers) · anyreply · done (a later email says paid / signed / resolved…) · received · final (minutes).</summary>
    public static SubmittalLog.TypeDef[] ForPack(string id) => id switch
    {
        "engineering" => new[]
        {
            T("RFI", "Request for information", "reply", 7, @"\bRFI" + RefNo + @"[A-Z0-9][\w/-]*\d", "%RFI%", @"\bRFIs?\b|requests? for information"),
            T("TQ", "Technical query", "reply", 7, @"\bTQ" + RefNo + @"[A-Z0-9][\w/-]*\d|technical query", "%TQ%;%Technical Query%", @"\bTQs?\b|technical quer(y|ies)"),
            T("NCR", "Non-conformance", "done", 14, @"\bNCR" + RefNo + @"[A-Z0-9][\w/-]*\d|non-?conformance", "%NCR%;%conformance%", @"\bNCRs?\b|non-?conformances?", @"\bclosed?\b|closure|accepted|verified"),
            T("SUB", "Submittal for review", "reply", 14, @"\bsubmittal\b|\bfor (review|approval)\b", "%submittal%;%for review%;%for approval%", @"submittals?|for (review|approval)"),
            T("REQ", "Request", "reply", 5, @"\brequest\b", "%request%", @"\brequests?\b"),
            Minutes
        },
        "finance" => new[]
        {
            T("INV", "Invoice", "done", 30, @"\b(?:INV|Invoice)" + RefNo + @"[A-Z0-9][\w/-]*\d|\binvoice\b", "%invoice%;%INV-%;%INV %;%INV#%", @"\binvoices?\b|\bINVs?\b", @"\bpaid\b|payment (?:made|released|done|processed|transferred|sent)|\bsettled\b|\bremitted\b|transfer(?:red)? (?:confirmation|done)"),
            T("PO", "Purchase order", "reply", 7, @"\b(?:PO|P\.O\.|Purchase Order)" + RefNo + @"\d[\w/-]*", "%PO %;%PO-%;%PO#%;%Purchase Order%", @"\bPOs?\b|purchase orders?"),
            T("PAY", "Payment request", "done", 14, @"payment request|request for payment|\bpayment\b", "%payment%", @"\bpayments?\b", @"\bpaid\b|payment (?:made|released|done|processed|transferred)|\bsettled\b"),
            T("APR", "Approval", "done", 5, @"\bapproval\b|\bfor approval\b|\bplease approve\b", "%approv%", @"\bapprovals?\b", @"\bapproved\b|\brejected\b"),
            Minutes
        },
        "legal" => new[]
        {
            T("CTR", "Contract / agreement", "done", 14, @"\b(?:contract|agreement|NDA|addendum|amendment|SPA|LOI)\b(?:" + RefNo + @"[A-Z0-9][\w/-]*\d)?", "%contract%;%agreement%;%NDA%;%addendum%;%amendment%", @"\bcontracts?\b|\bagreements?\b|\bNDAs?\b", @"\bsigned\b|\bexecuted\b|countersigned|fully signed"),
            T("NOT", "Notice / claim", "reply", 14, @"\bnotice\b|\bclaim\b", "%notice%;%claim%", @"\bnotices?\b|\bclaims?\b"),
            T("REV", "Review request", "reply", 5, @"\bfor (?:your )?review\b|\bplease review\b|\bcomments\b", "%review%;%comments%", @"\breviews?\b"),
            Minutes
        },
        "it" => new[]
        {
            T("TKT", "Ticket", "done", 3, @"\b(?:TICKET|INC|REQ|RITM|SR|CHG|CASE)[\s#:-]*\d{3,}\b", "%TICKET%;%INC%;%REQ%;%RITM%;%CHG%;%CASE%", @"\btickets?\b|\bincidents?\b|\bINCs?\b|\bcases?\b", @"\bresolved\b|\bclosed\b|\bfixed\b|\bcompleted\b"),
            T("CHG", "Change request", "done", 7, @"change request|\bCR" + RefNo + @"\d+", "%change request%;%CR-%;%CR %", @"change requests?|\bCRs?\b", @"\bapproved\b|\bimplemented\b|\bcompleted\b|\brejected\b"),
            T("ACC", "Access request", "done", 2, @"access request|request(?:ing)? access", "%access%", @"access requests?", @"\bgranted\b|\bdone\b|\bcompleted\b|\bprovided\b"),
            Minutes
        },
        "business" => new[]
        {
            T("QT", "Quotation / offer", "reply", 7, @"\b(?:QT|QTN|QUO|RFQ|Quotation|Quote|Offer)" + RefNo + @"[A-Z0-9][\w/-]*\d|\bquotation\b|\bRFQ\b", "%quot%;%RFQ%;%offer%;%QTN%", @"\bquot(ation|e)s?\b|\boffers?\b|\bRFQs?\b"),
            T("PO", "Purchase order", "reply", 5, @"\b(?:PO|P\.O\.|Purchase Order)" + RefNo + @"\d[\w/-]*", "%PO %;%PO-%;%PO#%;%Purchase Order%", @"\bPOs?\b|purchase orders?"),
            T("INV", "Invoice", "done", 30, @"\b(?:INV|Invoice)" + RefNo + @"[A-Z0-9][\w/-]*\d|\binvoice\b", "%invoice%;%INV-%;%INV %", @"\binvoices?\b", @"\bpaid\b|payment (?:made|released|done|received)|\bsettled\b"),
            T("ORD", "Order", "done", 7, @"\border\b" + @"(?:" + RefNo + @"\d[\w/-]*)?", "%order%", @"\borders?\b", @"\bdelivered\b|\bshipped\b|\bdispatched\b|\bcompleted\b"),
            Minutes
        },
        "government" => new[]
        {
            T("REF", "Letter with reference", "reply", 14, @"\b(?:Ref|Reference)\.?" + RefNo + @"[A-Z0-9][\w/.-]*\d", "%Ref%;%Reference%", @"\breferences?\b|\bletters?\b|\bcorrespondence\b"),
            T("APP", "Approval / permit", "done", 14, @"\b(?:approval|permit|NOC|licen[cs]e)\b", "%approval%;%permit%;%NOC%;%licen%", @"\bapprovals?\b|\bpermits?\b|\bNOCs?\b|\blicen[cs]es?\b", @"\bapproved\b|\bgranted\b|\bissued\b|\brejected\b"),
            T("CIR", "Circular", "received", 0, @"\bcircular\b|\bmemo\b", "%circular%;%memo%", @"\bcirculars?\b|\bmemos?\b"),
            Minutes
        },
        "medical" => new[]
        {
            T("RFR", "Referral", "reply", 2, @"\breferral\b|\breferred\b", "%referral%;%referred%", @"\breferrals?\b"),
            T("RES", "Results", "received", 0, @"\b(?:lab|test|scan|imaging) results?\b|\bresults? (?:for|of)\b", "%result%", @"\bresults?\b"),
            T("REQ", "Request", "reply", 3, @"\brequest\b", "%request%", @"\brequests?\b"),
            Minutes
        },
        "teaching" => new[]
        {
            T("ASG", "Assignment / submission", "done", 7, @"\b(?:assignment|homework|coursework|submission|project)\b", "%assignment%;%homework%;%coursework%;%submission%", @"\bassignments?\b|\bhomework\b|\bsubmissions?\b", @"\bgraded\b|\bmarked\b|\bfeedback\b|\bgrade\b"),
            T("PAR", "Parent message", "reply", 2, @"\bparent\b|\bguardian\b", "%parent%;%guardian%", @"\bparents?\b"),
            T("REQ", "Request", "reply", 3, @"\brequest\b", "%request%", @"\brequests?\b"),
            Minutes
        },
        "student" => new[]
        {
            T("ASG", "Assignment", "done", 14, @"\b(?:assignment|homework|coursework|submission|project|essay|lab report)\b", "%assignment%;%homework%;%coursework%;%submission%;%essay%", @"\bassignments?\b|\bhomework\b|\bessays?\b", @"\bgraded\b|\bmarked\b|\bfeedback\b|\bgrade\b"),
            T("EXM", "Exam", "received", 0, @"\bexam\b|\bquiz\b|\btest\b", "%exam%;%quiz%", @"\bexams?\b|\bquiz(zes)?\b"),
            T("Q", "Question to a teacher", "reply", 3, @"\bquestion\b", "%question%", @"\bquestions?\b"),
            Minutes
        },
        _ => new[]
        {
            T("REF", "Item with a reference", "reply", 7, @"\b[A-Z]{2,6}[-/#]\d{3,}[\w/-]*\b|\b(?:Ref|Reference)\.?" + RefNo + @"[A-Z0-9][\w/.-]*\d", "%Ref%;%-0%;%-1%;%-2%;%#%", @"\breferences?\b|\brefs?\b"),
            T("REQ", "Request", "reply", 3, @"\bplease (?:confirm|advise|approve|review|send|share|check)\b|\bkindly\b|\brequest\b", "%please%;%kindly%;%request%", @"\brequests?\b"),
            T("APR", "Approval", "done", 3, @"\bapproval\b|\bplease approve\b", "%approv%", @"\bapprovals?\b", @"\bapproved\b|\brejected\b"),
            Minutes
        }
    };

    /// <summary>For the phone: what to track, who is “us”, the defaults (the Brain's own settings come with o["settings"]).</summary>
    public static System.Text.Json.Nodes.JsonObject ForPhone() => new()
    {
        ["builtin"] = Builtin, ["us"] = Us, ["short"] = UsShort, ["name"] = LogName, ["defaults"] = DefaultTypes, ["role"] = CurrentRole.Name,
        ["types"] = new System.Text.Json.Nodes.JsonArray(Types.Select(t => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["code"] = t.Code, ["label"] = t.Label, ["find"] = t.Find, ["close"] = t.Close, ["overdue"] = t.Overdue, ["rx"] = t.Rx, ["done"] = t.Done, ["words"] = t.Words
        }).ToArray())
    };
}
