using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>What Nova understood from your instructions: rules done on this PC, one AI check, and the output.</summary>
class PromptPlan
{
    public List<string> ExStarts = new(), ExContains = new(), ExFrom = new(), OnlyFrom = new(), OnlyWords = new();
    public bool NeedAttach;
    public int MinDays;
    public string AiCheck = "", Group = "", Export = "";
    public string Note = "";

    public bool Empty => ExStarts.Count + ExContains.Count + ExFrom.Count + OnlyFrom.Count + OnlyWords.Count == 0 && !NeedAttach && MinDays == 0 && AiCheck.Length == 0 && Group.Length == 0 && Export.Length == 0;

    public PromptPlan Merge(PromptPlan o)
    {
        if (o == null) return this;
        void U(List<string> a, List<string> b) { foreach (var x in b) if (x.Trim().Length > 0 && !a.Contains(x.Trim(), StringComparer.OrdinalIgnoreCase)) a.Add(x.Trim()); }
        U(ExStarts, o.ExStarts); U(ExContains, o.ExContains); U(ExFrom, o.ExFrom); U(OnlyFrom, o.OnlyFrom); U(OnlyWords, o.OnlyWords);
        NeedAttach |= o.NeedAttach;
        MinDays = Math.Max(MinDays, o.MinDays);
        if (AiCheck.Length == 0) AiCheck = o.AiCheck;
        if (Group.Length == 0) Group = o.Group;
        if (Export.Length == 0) Export = o.Export;
        return this;
    }
}

/// <summary>A prompt you saved: question + instructions + filter level + output, on demand or on a schedule.</summary>
class SavedPrompt
{
    public string Id = Guid.NewGuid().ToString("N")[..10], Name = "", Where = "ask", Question = "", Instructions = "", Level = "B";
    public string Run = "";          // "" (only when I click) · daily · weekly
    public int Day = 1;              // weekly: 0 = Sunday … 6 = Saturday
    public string Time = "08:00";
    public bool ToWork = true, EmailMe;
    public DateTime LastRun;
    public string LastNote = "";
}

/// <summary>
/// ✦ Prompts: write what you want in your own words — Nova turns it into rules on this PC (exclude, only, from, attachments,
/// days waiting, group, export), an optional AI check of each email for what a rule can't judge, and an Excel / Word / CSV file.
/// ⭐ Saved prompts can run by themselves. Designed by A.HAMROUNI
/// </summary>
static class Prompts
{
    // ------------------------------------------------------------------ settings
    public static string Level { get => Store.Str("win_prompt_level", "B"); set => Store.Set("win_prompt_level", value); }
    public static string AiSends { get => Store.Str("win_prompt_send", "whole"); set => Store.Set("win_prompt_send", value); }
    public static int AskOver { get => Store.Int("win_prompt_askover", 30); set => Store.SetInt("win_prompt_askover", value); }
    public static int MaxAi { get => Store.Int("win_prompt_max", 60); set => Store.SetInt("win_prompt_max", value); }
    public static string DefaultExport { get => Store.Str("win_prompt_export", "xlsx"); set => Store.Set("win_prompt_export", value); }
    public static bool ShowMore { get => Store.Bool("win_prompt_more", true); set => Store.SetBool("win_prompt_more", value); }
    public static bool ShowChips { get => Store.Bool("win_prompt_chips", true); set => Store.SetBool("win_prompt_chips", value); }
    public static string Folder
    {
        get { var f = Store.Str("win_prompt_folder"); if (f.Length == 0) f = Path.Combine(Paths.Root, "Prompt results"); try { Directory.CreateDirectory(f); } catch { } return f; }
        set => Store.Set("win_prompt_folder", value);
    }

    // ------------------------------------------------------------------ understanding

    static readonly Regex Cut = new(@"\s*(?:,\s*|\band\s+|\.\s+|;\s*)?(?=\b(?:exclude|excluding|except|ignore|skip|without|but not|do not show|don't show|hide|only (?:from|the|emails|those|projects?|with)|keep only|group(?:ed)? by|sort(?:ed)? by|export|save (?:it |the list |them )?(?:as|to|in)|as an? (?:excel|word|csv))\b)", RegexOptions.IgnoreCase);

    /// <summary>"Which … awaiting my reply? and exclude … and export as Excel" → the question, and the instructions part.</summary>
    public static (string Main, string Extra) Split(string q)
    {
        q = (q ?? "").Trim();
        var m = Cut.Match(q);
        if (!m.Success || m.Index < 12) return (q, "");
        return (q[..m.Index].Trim().TrimEnd(',', ';', '.', '-').Trim(), q[m.Index..].Trim());
    }

    static List<string> Quoted(string s) =>
        Regex.Matches(s, "[\"“”'‘’«]([^\"“”'‘’«»]{1,60})[\"“”'‘’»]").Select(m => m.Groups[1].Value.Trim()).Where(x => x.Length > 0).ToList();

    static List<string> Names(string s) =>
        Regex.Split(s, @"\s*(?:,|\band\b|&|/|\bor\b)\s*", RegexOptions.IgnoreCase).Select(x => x.Trim(' ', '.', '"', '“', '”')).Where(x => x.Length > 1 && x.Length < 40).ToList();

    /// <summary>Rules understood on this PC (works when the AI is busy or off).</summary>
    public static PromptPlan Local(string text)
    {
        var p = new PromptPlan();
        if (string.IsNullOrWhiteSpace(text)) return p;
        var aiParts = new List<string>();
        foreach (var raw in Regex.Split(text, @"[\r\n;]+|(?<=[a-z\)""”])\.\s+|\s+\band\s+(?=(?:exclude|ignore|skip|only|keep|group|export|save|sort)\b)", RegexOptions.IgnoreCase))
        {
            var c = raw.Trim();
            if (c.Length < 3) continue;
            var lc = c.ToLowerInvariant();
            bool neg = Regex.IsMatch(lc, @"\b(exclude|excluding|except|ignore|skip|without|remove|hide|not|don't|do not|no)\b");
            bool only = Regex.IsMatch(lc, @"\b(only|just)\b");
            bool used = false;
            var q = Quoted(c);

            if (Regex.IsMatch(lc, @"\b(excel|xlsx|axles|spreadsheet)\b")) { p.Export = "xlsx"; used = true; }
            else if (Regex.IsMatch(lc, @"\b(word|docx)\b") && Regex.IsMatch(lc, @"\b(export|save|as|file|document)\b")) { p.Export = "docx"; used = true; }
            else if (Regex.IsMatch(lc, @"\bcsv\b")) { p.Export = "csv"; used = true; }
            else if (Regex.IsMatch(lc, @"\bexport\b")) { p.Export = DefaultExport; used = true; }

            var g = Regex.Match(lc, @"\b(?:group(?:ed)?|sort(?:ed)?|organi[sz]e(?:d)?)\s+(?:them\s+|it\s+)?by\s+(project|sender|person|people|date|day|from)");
            if (g.Success) { p.Group = g.Groups[1].Value switch { "sender" or "person" or "people" or "from" => "sender", "date" or "day" => "date", _ => "project" }; used = true; }

            if (Regex.IsMatch(lc, @"\bstart(?:s|ing|ed)?\s+(?:by|with)\b|\bbegin(?:s|ning)?\s+with\b"))
            {
                var items = q.Count > 0 ? q : Names(Regex.Replace(c, @"^.*?\b(?:by|with)\b", "", RegexOptions.IgnoreCase));
                if (neg) p.ExStarts.AddRange(items);
                else aiParts.Add(c);
                used = true;
            }
            else if (Regex.IsMatch(lc, @"\b(contain|contains|containing|mention|mentions|mentioning|with the words?|subject (?:has|with|contains))\b") && q.Count > 0)
            {
                if (neg) p.ExContains.AddRange(q); else p.OnlyWords.AddRange(q);
                used = true;
            }

            var fm = Regex.Match(c, @"\bfrom\s+((?:[A-Z][\p{L}'\-]+)(?:\s+[A-Z][\p{L}'\-]+){0,2}(?:\s*(?:,|and|&)\s*[A-Z][\p{L}'\-]+(?:\s+[A-Z][\p{L}'\-]+){0,2})*)");
            if (fm.Success)
            {
                if (neg) p.ExFrom.AddRange(Names(fm.Groups[1].Value));
                else if (only) p.OnlyFrom.AddRange(Names(fm.Groups[1].Value));
                used = true;
            }
            else if (neg && Regex.IsMatch(lc, @"\b(reminders?|automatic|notifications?)\b"))
            {
                var who = Regex.Match(c, @"\b(?:of|by)\s+((?:[A-Z][\p{L}'\-]+)(?:\s+[A-Z][\p{L}'\-]+){0,2})");
                if (who.Success) { p.ExFrom.AddRange(Names(who.Groups[1].Value)); used = true; }
            }

            var proj = Regex.Match(c, @"\bonly\s+(?:the\s+|for\s+)?([\p{L}0-9\-\s,&/]+?)\s+projects?\b", RegexOptions.IgnoreCase);
            if (!proj.Success) proj = Regex.Match(c, @"\bprojects?\s*:?\s+([A-Z][\p{L}0-9\-]+(?:\s*(?:,|and|&|/)\s*[A-Z][\p{L}0-9\-]+)*)");
            if (proj.Success && (only || !neg)) { p.OnlyWords.AddRange(Names(proj.Groups[1].Value)); used = true; }

            if (Regex.IsMatch(lc, @"\b(with|has|have|having|include|including)\s+(?:an?\s+|the\s+)?(attachments?|attached|drawings? attached|files? attached)\b|\b(drawings?|files?|documents?)\s+attached\b"))
            {
                if (!neg) p.NeedAttach = true;
                used = true;
            }
            var d = Regex.Match(lc, @"\b(?:waiting|older than|more than|over|at least)\s+(\d{1,3})\s+days?\b");
            if (d.Success) { p.MinDays = int.Parse(d.Groups[1].Value); used = true; }

            if (!used && Regex.IsMatch(lc, @"\b(keep only|only (?:emails|those|ones|items)|where|asking|asks|request|requests|approval|approve|urgent|important|resolved|decision|deadline|complain|reminder|chasing|action)\b"))
                aiParts.Add(c);
        }
        p.AiCheck = string.Join(" ", aiParts).Trim();
        return p;
    }

    const string PlanSystem =
        "The user filters a list of THEIR emails with instructions. Turn the instructions into JSON only: " +
        "{\"exclude_starts\":[\"words an email body starts with, to drop\"],\"exclude_contains\":[],\"exclude_from\":[\"sender names to drop\"],\"only_from\":[],\"only_words\":[\"project names or words to keep (subject/body)\"]," +
        "\"need_attachments\":false,\"min_days\":0,\"group\":\"project|sender|date|\",\"export\":\"xlsx|docx|csv|\",\"ai_check\":\"the part that needs reading each email to judge (e.g. 'the contractor asks for my approval'), or empty\"}. " +
        "Keep names and words exactly as written. Do not invent anything not in the instructions. \"axles\" or \"excel\" means xlsx.";

    /// <summary>Your instructions → a plan: rules on this PC first, then your AI adds what it understood (if it answers).</summary>
    public static async Task<PromptPlan> Understand(string question, string instructions, CancellationToken ct)
    {
        var p = Local(instructions);
        if (Ai.Active() == null) return p;
        try
        {
            var j = Ai.ParseJson(await Ai.Chat(PlanSystem, "Question: " + question + "\nInstructions: " + instructions, true, true, null, ct));
            List<string> L(string k) => (j[k] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? new();
            var a = new PromptPlan
            {
                ExStarts = L("exclude_starts"), ExContains = L("exclude_contains"), ExFrom = L("exclude_from"), OnlyFrom = L("only_from"), OnlyWords = L("only_words"),
                NeedAttach = j["need_attachments"]?.ToString() == "true", MinDays = int.TryParse(j["min_days"]?.ToString(), out var md) ? md : 0,
                Group = j["group"]?.ToString() ?? "", Export = j["export"]?.ToString() ?? "", AiCheck = j["ai_check"]?.ToString() ?? ""
            };
            if (a.Group is not ("project" or "sender" or "date")) a.Group = "";
            if (a.Export is not ("xlsx" or "docx" or "csv")) a.Export = "";
            // The AI's wording for the AI check is clearer; the rules are the union of both.
            if (a.AiCheck.Length > 0) p.AiCheck = a.AiCheck;
            p.Merge(a);
        }
        catch (Exception e) { p.Note = "Understood on this PC only (" + e.Message + ")"; }
        return p;
    }

    // ------------------------------------------------------------------ applying

    static string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N} ]+", " ").Trim();

    public static string ProjectOf(MailHit h, IEnumerable<string> preferred = null)
    {
        var text = (h.Subject ?? "") + " " + (h.Folder ?? "");
        foreach (var w in preferred ?? Enumerable.Empty<string>())
            if (w.Length > 1 && text.Contains(w, StringComparison.OrdinalIgnoreCase)) return w.ToUpperInvariant() == w ? w : char.ToUpperInvariant(w[0]) + w[1..];
        var subj = Outstanding.NormSubject(h.Subject ?? "").ToUpperInvariant();
        var m = Regex.Match(subj, @"^([A-Z]{2,6})\b");
        if (m.Success && m.Value is not ("RE" or "FW" or "THE" or "NEW" or "RFI" or "MAR" or "WIR" or "DRW" or "SDS")) return m.Value;
        var c = Regex.Match(h.Subject ?? "", @"\b([A-Z]{2,6})-[A-Z]{2,4}-");
        if (c.Success) return c.Groups[1].Value;
        var u = Regex.Match(h.Subject ?? "", @"\b(CSU\d{3,5}|RS\d-C\d+)\b", RegexOptions.IgnoreCase);
        if (u.Success) return u.Value.ToUpperInvariant();
        return "Other";
    }

    /// <summary>The rules, on this PC: emails kept, and how many each rule hid.</summary>
    public static (List<MailHit> Kept, List<(string Why, MailHit Hit)> Hidden) Apply(List<MailHit> hits, PromptPlan p)
    {
        var kept = new List<MailHit>();
        var hidden = new List<(string, MailHit)>();
        if (p == null) return (hits.ToList(), hidden);
        foreach (var h in hits)
        {
            var start = Norm(h.FirstLines(120));
            var subj = h.Subject ?? "";
            var body = h.FirstLines(800);
            string why = null;
            foreach (var s in p.ExStarts) if (start.StartsWith(Norm(s))) { why = $"start with “{s}”"; break; }
            if (why == null) foreach (var s in p.ExContains) if (subj.Contains(s, StringComparison.OrdinalIgnoreCase) || body.Contains(s, StringComparison.OrdinalIgnoreCase)) { why = $"mention “{s}”"; break; }
            if (why == null) foreach (var s in p.ExFrom) if (Who(h).Contains(s, StringComparison.OrdinalIgnoreCase)) { why = $"from {s}"; break; }
            if (why == null && p.OnlyFrom.Count > 0 && !p.OnlyFrom.Any(s => Who(h).Contains(s, StringComparison.OrdinalIgnoreCase))) why = "from other people";
            if (why == null && p.OnlyWords.Count > 0 && !p.OnlyWords.Any(s => subj.Contains(s, StringComparison.OrdinalIgnoreCase) || (h.Folder ?? "").Contains(s, StringComparison.OrdinalIgnoreCase) || body.Contains(s, StringComparison.OrdinalIgnoreCase))) why = "other projects";
            if (why == null && p.NeedAttach && string.IsNullOrWhiteSpace(h.AttNames)) why = "without attachments";
            if (why == null && p.MinDays > 0 && h.DaysWaiting < p.MinDays) why = $"waiting less than {p.MinDays} days";
            if (why == null) kept.Add(h); else hidden.Add((why, h));
        }
        return (kept, hidden);
    }

    static string Who(MailHit h) => (h.FromName ?? "") + " " + (h.FromAddr ?? "");

    public static string Hidden(List<(string Why, MailHit Hit)> hidden) =>
        string.Join(" · ", hidden.GroupBy(x => x.Why).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"));

    // ------------------------------------------------------------------ the AI check (only what a rule can't judge)

    static readonly Dictionary<string, (bool Keep, string Why)> aiCache = new();

    public static async Task<(List<MailHit> Kept, List<(string Why, MailHit Hit)> Hidden, int Read, string Note)> AiFilter(IWin32Window owner, List<MailHit> hits, string criterion, CancellationToken ct)
    {
        using var privateWork = Ai.Private();   // your emails / documents: the routing rule may keep them on the local AI
        var kept = new List<MailHit>(); var hidden = new List<(string, MailHit)>();
        if (string.IsNullOrWhiteSpace(criterion) || hits.Count == 0) return (hits, hidden, 0, "");
        if (Ai.Active() == null) return (hits, hidden, 0, "AI check skipped — no AI key");
        string K(MailHit h) => criterion + "|" + h.Entry;
        var todo = hits.Where(h => !aiCache.ContainsKey(K(h))).Take(MaxAi).ToList();
        if (todo.Count > 0 && owner != null && todo.Count > AskOver)
        {
            var sample = string.Join("\n", todo.Take(8).Select(h => $"• {h.FromName} — {h.Subject}"));
            var c = ConfirmSend.Ask(owner, $"✦ Send {todo.Count} emails to {Ai.LabelOf(Ai.Current())} for the AI check?",
                $"To check “{criterion}”, Nova sends {(AiSends == "whole" ? "the text" : "the subject and first lines")} of {todo.Count} emails (no attachments). Settings → ✦ Prompts changes this.",
                sample + (todo.Count > 8 ? $"\n… and {todo.Count - 8} more" : ""), "Send and check", false);
            if (c != ConfirmSend.Choice.Send) return (hits, hidden, 0, "AI check not sent — rules on this PC only");
        }
        var sys = "You check the user's emails against their criterion: \"" + criterion + "\". For each email decide if it MATCHES (keep) or not. " +
                  "Reply with JSON only: {\"results\":[{\"id\":1,\"keep\":true,\"why\":\"very short reason, max 12 words\"}]}.";
        string note = "";
        int read = 0;
        try
        {
            for (int i = 0; i < todo.Count; i += 8)
            {
                ct.ThrowIfCancellationRequested();
                var batch = todo.Skip(i).Take(8).ToList();
                var sb = new StringBuilder();
                for (int k = 0; k < batch.Count; k++)
                {
                    var h = batch[k];
                    var text = AiSends == "whole" ? h.NewPart : h.FirstLines(400);
                    if (text.Length > 2500) text = text[..2500];
                    sb.Append($"### Email {k + 1}\nFrom: {h.FromName}\nDate: {h.Date:dd MMM yyyy}\nSubject: {h.Subject}\n{text}\n\n");
                }
                var j = Ai.ParseJson(await Ai.Chat(sys, sb.ToString(), true, true, null, ct));
                foreach (var r in (j["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (int.TryParse(r["id"]?.ToString(), out var id) && id >= 1 && id <= batch.Count)
                        aiCache[K(batch[id - 1])] = (r["keep"]?.ToString() != "false", r["why"]?.ToString() ?? "");
                read += batch.Count;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { note = "AI check stopped (" + e.Message + ") — the rest kept"; }
        foreach (var h in hits)
        {
            if (aiCache.TryGetValue(K(h), out var v))
            {
                h.Why = v.Why;
                if (v.Keep) kept.Add(h); else hidden.Add(("not matching your AI check", h));
            }
            else kept.Add(h);
        }
        if (hits.Count > MaxAi && note.Length == 0) note = $"AI check limited to {MaxAi} emails (Settings → ✦ Prompts)";
        return (kept, hidden, read, note);
    }

    // ------------------------------------------------------------------ output files

    public static string GroupKey(MailHit h, PromptPlan p) => p?.Group switch
    {
        "sender" => h.Sent ? "You" : (h.FromName ?? ""),
        "date" => h.Date.ToString("ddd dd MMM yyyy"),
        "project" => ProjectOf(h, p.OnlyWords),
        _ => ""
    };

    /// <summary>Saves the list as Excel, Word or CSV in the Prompt results folder; returns the path.</summary>
    public static string Export(List<MailHit> hits, PromptPlan p, string title, string question, string instructions)
    {
        var kind = p?.Export is { Length: > 0 } e ? e : DefaultExport;
        var safe = Regex.Replace(title, @"[\\/:*?""<>|]+", " ").Trim();
        if (safe.Length > 60) safe = safe[..60].Trim();
        var path = Path.Combine(Folder, $"{safe} — {DateTime.Now:dd MMM yyyy HHmm}.{kind}");
        var rows = (p?.Group.Length > 0 ? hits.OrderBy(h => GroupKey(h, p)).ThenByDescending(h => h.Date) : hits.AsEnumerable()).ToList();
        string[] head = { "#", p?.Group switch { "sender" => "Sender", "date" => "Day", _ => "Project" }, "Subject", "From", "To", "Date", "Days waiting", "Status", "Attachments", "Nova note" };
        List<string[]> data = rows.Select((h, i) => new[]
        {
            (i + 1).ToString(), p?.Group.Length > 0 ? GroupKey(h, p) : ProjectOf(h, p?.OnlyWords), h.Subject ?? "", h.FromName ?? "", h.To ?? "",
            h.Date.ToString("yyyy-MM-dd HH:mm"), h.DaysWaiting.ToString(),
            h.Status switch { "needs" => "Overdue", "week" => "This week", "fyi" => "FYI", "follow" => "No answer to you", _ => h.MaybeAnswered.Length > 0 ? "Probably answered" : "" },
            (h.AttNames ?? "").Replace(";", ", "), h.Why.Length > 0 ? h.Why : h.Summary ?? ""
        }).ToList();
        if (kind == "csv")
        {
            var sb = new StringBuilder();
            foreach (var r in new[] { head }.Concat(data)) sb.AppendLine(string.Join(",", r.Select(c => "\"" + c.Replace("\"", "\"\"") + "\"")));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        else if (kind == "docx")
        {
            var sb = new StringBuilder();
            string last = null;
            foreach (var r in data)
            {
                if (p?.Group.Length > 0 && r[1] != last) { sb.Append("\n■ ").Append(r[1]).Append('\n'); last = r[1]; }
                sb.Append($"{r[0]}. {r[2]}\n    {r[3]} · {r[5]} · waiting {r[6]} days{(r[8].Length > 0 ? " · 📎 " + r[8] : "")}{(r[9].Length > 0 ? "\n    ✦ " + r[9] : "")}\n");
            }
            Docx.Write(path, title, question + (string.IsNullOrWhiteSpace(instructions) ? "" : "\n" + instructions), sb.ToString(), "Designed by A.HAMROUNI");
        }
        else Xlsx(path, title, head, data);
        return path;
    }

    static string X(string s) => System.Security.SecurityElement.Escape(Regex.Replace(s ?? "", @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "")) ?? "";

    static string Col(int i) { var s = ""; i++; while (i > 0) { var m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; } return s; }

    /// <summary>A real .xlsx (header row bold and frozen, filters on, column widths).</summary>
    static void Xlsx(string path, string title, string[] head, List<string[]> data)
    {
        using var fs = File.Create(path);
        using var z = new ZipArchive(fs, ZipArchiveMode.Create);
        void W(string name, string xml) { using var s = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false)); s.Write(xml); }
        W("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
        W("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        W("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        W("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Nova\" sheetId=\"1\" r:id=\"rId1\"/></sheets><definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">Nova!$A$3:$" + Col(head.Length - 1) + "$" + (data.Count + 3) + "</definedName></definedNames></workbook>");
        W("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"3\"><font><sz val=\"10\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"14\"/><color rgb=\"FF5B4BDB\"/><name val=\"Calibri\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF5B4BDB\"/></patternFill></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"4\"><xf/><xf fontId=\"1\" fillId=\"2\" applyFont=\"1\" applyFill=\"1\"/><xf fontId=\"2\" applyFont=\"1\"/><xf applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf></cellXfs></styleSheet>");
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"3\" topLeftCell=\"A4\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
        int[] widths = { 5, 14, 70, 22, 30, 17, 9, 16, 30, 50 };
        for (int i = 0; i < head.Length; i++) sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{(i < widths.Length ? widths[i] : 20)}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");
        void Row(int r, IEnumerable<string> cells, int style)
        {
            sb.Append($"<row r=\"{r}\">");
            int c = 0;
            foreach (var v in cells)
            {
                var refc = Col(c++) + r;
                if (style == 0 && c is 1 or 7 && int.TryParse(v, out var n)) sb.Append($"<c r=\"{refc}\"><v>{n}</v></c>");
                else sb.Append($"<c r=\"{refc}\" t=\"inlineStr\"{(style > 0 ? $" s=\"{style}\"" : "")}><is><t xml:space=\"preserve\">{X(v)}</t></is></c>");
            }
            sb.Append("</row>");
        }
        Row(1, new[] { title + " — " + DateTime.Now.ToString("dd MMM yyyy HH:mm") }, 2);
        Row(2, new[] { "Designed by A.HAMROUNI · Nova Enhance" }, 0);
        Row(3, head, 1);
        for (int i = 0; i < data.Count; i++) Row(i + 4, data[i], 0);
        sb.Append("</sheetData><autoFilter ref=\"A3:" + Col(head.Length - 1) + (data.Count + 3) + "\"/></worksheet>");
        W("xl/worksheets/sheet1.xml", sb.ToString());
    }

    // ------------------------------------------------------------------ ⭐ saved prompts

    static readonly JsonSerializerOptions J = new() { IncludeFields = true };

    public static List<SavedPrompt> All()
    {
        try { return JsonSerializer.Deserialize<List<SavedPrompt>>(Store.Str("win_prompts", "[]"), J) ?? new(); } catch { return new(); }
    }

    public static void SaveAll(List<SavedPrompt> list) => Store.Set("win_prompts", JsonSerializer.Serialize(list, J));

    public static void Save(SavedPrompt p)
    {
        var all = All();
        all.RemoveAll(x => x.Id == p.Id);
        all.Add(p);
        SaveAll(all);
    }

    public static void Delete(string id) { var all = All(); all.RemoveAll(x => x.Id == id); SaveAll(all); }

    public static string Describe(SavedPrompt p) =>
        (p.Where == "ask" ? "Ask Nova" : p.Where.StartsWith("folder|") ? "Folder: " + GDrive.Display(p.Where[7..]) : "PDF") + " · " +
        (p.Level == "A" ? "PC rules" : "AI check") + " · " + RunText(p);

    public static string RunText(SavedPrompt p) => p.Run switch
    {
        "daily" => $"every day {p.Time}",
        "weekly" => $"every {(DayOfWeek)p.Day} {p.Time}",
        _ => "on demand"
    };

    /// <summary>The last time this prompt should have run (for the schedule).</summary>
    static DateTime? Slot(SavedPrompt p, DateTime now)
    {
        if (p.Run is not ("daily" or "weekly")) return null;
        if (!TimeSpan.TryParse(p.Time, out var t)) t = TimeSpan.FromHours(8);
        var slot = now.Date + t;
        if (p.Run == "daily") return slot <= now ? slot : slot.AddDays(-1);
        var back = ((int)now.DayOfWeek - p.Day + 7) % 7;
        slot = now.Date.AddDays(-back) + t;
        return slot <= now ? slot : slot.AddDays(-7);
    }

    static bool running;

    /// <summary>Every minute: runs the ⭐ prompts that are due (missed ones run once when the PC is back, the same day).</summary>
    public static async void Tick()
    {
        if (running) return;
        var now = DateTime.Now;
        foreach (var p in All())
        {
            var slot = Slot(p, now);
            if (slot == null || p.LastRun >= slot || now - slot > TimeSpan.FromHours(12) || p.Where != "ask") continue;
            running = true;
            try { await RunSaved(p, null); }
            catch (Exception e) { Log.Error("prompt " + p.Name, e); }
            finally { running = false; }
            break;
        }
    }

    /// <summary>Runs an email prompt without a window: search, rules, AI check, file, My Work, email.</summary>
    public static async Task<string> RunSaved(SavedPrompt p, IWin32Window owner)
    {
        var (main, extra) = Split(p.Question);
        var instr = (p.Instructions + "\n" + extra).Trim();
        var ans = await Assistant.Ask(main, null, new List<MailHit>(), CancellationToken.None);
        var plan = instr.Length > 0 ? await Understand(main, instr, CancellationToken.None) : new PromptPlan();
        var (kept, hidden) = Apply(ans.Hits.Where(h => h.MaybeAnswered.Length == 0).ToList(), plan);
        if (p.Level != "A" && plan.AiCheck.Length > 0) { var r = await AiFilter(owner, kept, plan.AiCheck, CancellationToken.None); kept = r.Kept; }
        if (plan.Export.Length == 0) plan.Export = DefaultExport;
        var path = Export(kept, plan, p.Name, p.Question, instr);
        var note = $"{kept.Count} emails · {Path.GetFileName(path)}";
        if (p.ToWork)
        {
            var t = Work.FromMail(p.Question, ans.Filter, ans.Intent, kept);
            t.Title = $"⭐ {p.Name} — {DateTime.Now:dd MMM}";
            t.Notes = $"📎 {path}\n{(instr.Length > 0 ? "✎ " + instr : "")}".Trim();
            t.Remind = DateTime.Now.AddMinutes(1);
            Work.Save(t);
        }
        if (p.EmailMe) note += Mail(path, p.Name, kept.Count) ? " · emailed to you" : " · ⚠ not emailed (Outlook closed)";
        p.LastRun = DateTime.Now; p.LastNote = note;
        Save(p);
        try { Program.Tray?.ShowBalloonTip(8000, "⭐ " + p.Name, note, ToolTipIcon.Info); } catch { }
        return note;
    }

    static bool Mail(string path, string name, int n)
    {
        try
        {
            dynamic app = Outlook.Running();
            if (app == null || Me.Addrs.Count == 0) return false;
            dynamic m = app.CreateItem(0);
            m.To = Me.Addrs[0];
            m.Subject = $"⭐ {name} — {DateTime.Now:dd MMM yyyy} ({n} emails)";
            m.Body = $"Nova Enhance ran your saved prompt “{name}”. The file is attached.\n\nDesigned by A.HAMROUNI";
            m.Attachments.Add(path);
            m.Send();
            return true;
        }
        catch (Exception e) { Log.Warn("prompt mail", e.Message); return false; }
    }

    // ------------------------------------------------------------------ ⭐ save dialog

    public static SavedPrompt AskSave(IWin32Window owner, SavedPrompt p)
    {
        using var f = new NovaForm { Text = "⭐ Save this prompt", Width = 560, Height = 560, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent };
        var s = new Stack(); f.Controls.Add(s);
        s.Add(Th.L("NAME", 8.5f, Th.Muted, true));
        var name = s.Add(Th.Box(false), true); name.Text = p.Name;
        s.Add(Th.L("QUESTION", 8.5f, Th.Muted, true));
        var q = s.Add(Th.Box(false), true); q.Text = p.Question;
        s.Add(Th.L("✎ INSTRUCTIONS", 8.5f, Th.Muted, true));
        var ins = s.Add(Th.Box(true, 3), true); ins.Text = p.Instructions;
        s.Add(Th.L("RUN", 8.5f, Th.Muted, true));
        var run = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        run.Items.AddRange(new object[] { "Only when I click ▶", "Every day", "Every week" });
        run.SelectedIndex = p.Run == "daily" ? 1 : p.Run == "weekly" ? 2 : 0;
        var day = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        foreach (var d in Enum.GetNames(typeof(DayOfWeek))) day.Items.Add(d);
        day.SelectedIndex = Math.Clamp(p.Day, 0, 6);
        var time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 90, Font = Th.F(10), Value = DateTime.Today + (TimeSpan.TryParse(p.Time, out var tt) ? tt : TimeSpan.FromHours(8)) };
        var rr = s.Add(Th.Row(false));
        rr.Controls.Add(run); rr.Controls.Add(day); rr.Controls.Add(time);
        void Vis() { day.Visible = run.SelectedIndex == 2; time.Visible = run.SelectedIndex > 0; }
        run.SelectedIndexChanged += (_, _) => Vis(); Vis();
        if (p.Where != "ask") { run.Enabled = false; s.Add(Th.Wrap("Folder and PDF prompts run when you click ▶ (scheduled runs are for email prompts).", 9, Th.Muted)); }
        var work = s.Add(Th.Check("Add the result to 📌 My Work with a reminder", p.ToWork, null));
        var mail = s.Add(Th.Check("Email the file to me (Outlook must be open)", p.EmailMe, null));
        SavedPrompt res = null;
        var row = s.Add(Th.Row(false));
        row.Controls.Add(Th.B("⭐ Save", () =>
        {
            p.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : (q.Text.Length > 40 ? q.Text[..40] : q.Text);
            p.Question = q.Text.Trim(); p.Instructions = ins.Text.Trim();
            p.Run = run.SelectedIndex switch { 1 => "daily", 2 => "weekly", _ => "" };
            p.Day = day.SelectedIndex; p.Time = time.Value.ToString("HH:mm");
            p.ToWork = work.Checked; p.EmailMe = mail.Checked;
            Save(p); res = p; f.Close();
        }, true));
        row.Controls.Add(Th.B("Cancel", f.Close));
        f.ShowDialog(owner);
        return res;
    }

    /// <summary>The ⭐ My prompts menu for one place (Ask Nova, a folder, a PDF).</summary>
    public static void Menu(Control at, string where, Action<SavedPrompt> use)
    {
        var m = new ContextMenuStrip { Font = Th.F(10), ShowImageMargin = false };
        var list = All().Where(p => where == "*" || p.Where == where || (where.StartsWith("folder|") && p.Where.StartsWith("folder|"))).ToList();
        if (list.Count == 0) m.Items.Add(new ToolStripLabel("No saved prompts yet — ask, then ⭐ Save this prompt") { ForeColor = Color.Gray });
        foreach (var p in list.OrderBy(p => p.Name))
        {
            var pp = p;
            m.Items.Add($"⭐ {p.Name}   ·   {RunText(p)}", null, (_, _) => use(pp));
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Manage my prompts…", null, (_, _) => Screens.Settings("✦ Prompts"));
        m.Show(at, new Point(0, at.Height));
    }
}
