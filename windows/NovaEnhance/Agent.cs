using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🧭 Nova agent: you type freely (Nova Chat, Search for email, the Submittal log) — the AI reads your words and your 🧠 Nova Brain,
/// then uses Nova's own tools on this PC: the 📋 Submittal log, email search (From / To / Cc, Inbox, copies), the 🧵 thread check
/// (who sent what to whom, who replied), and ✉ drafts (never sent). It always shows you 3–5 example emails first, tunes the search
/// with your ✓ / ✗, then gives the table where you asked. Designed by A.HAMROUNI
/// </summary>
static class Agent
{
    public class Row
    {
        public List<string> Cells = new();
        public long Id;
        public MailHit Mail;
    }

    public class Result
    {
        public string Text = "";
        public List<string> Columns = new();
        public List<Row> Rows = new();
        public string Note = "", By = "";
        public List<string> Steps = new();
    }

    /// <summary>What the window does for the agent: show a step, and ask you to confirm example emails.</summary>
    public class Host
    {
        public Action<string> Progress = _ => { };
        /// <summary>🧪 Tests: the two runs (current brain · new rule) share the same email searches — done once.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<(string, List<MailHit>)>>> Cache;
        /// <summary>What is happening inside the step (“312 emails checked”, “thread 4 of 12”).</summary>
        public Action<string> Detail = _ => { };
        /// <summary>(question, examples) → (ids you ticked ✓, ids you marked ✗, your note); null = you stopped.</summary>
        public Func<string, List<MailHit>, Task<(List<long> Yes, List<long> No, string Note)?>> Confirm;
    }

    /// <summary>A conversation: what was said and found, kept for follow-ups (“only the older ones”, “add the MARs”).</summary>
    public class Session
    {
        public readonly List<(string Role, string Text)> Said = new();
        public readonly Dictionary<long, MailHit> Seen = new();
        public Result Last;
        public bool Confirmed;
        public string ChatId = "";
        /// <summary>📨 The last follow-up check, row by row — Nova builds the table from it when the AI's answer cannot be read.</summary>
        public List<(long Id, string[] Cells)> Follow;
        public string FollowHead = "";
    }

    // ------------------------------------------------------------------ one conversation everywhere (the log ⇄ Nova Chat)

    static readonly Dictionary<string, Session> sessions = new();

    /// <summary>The conversation of this chat (the log and Nova Chat share it). After a restart it is rebuilt from the saved chat.</summary>
    public static Session SessionFor(string chatId, JsonArray saved = null)
    {
        lock (sessions)
        {
            if (sessions.TryGetValue(chatId, out var s)) return s;
            s = new Session { ChatId = chatId };
            foreach (var m in (saved ?? new JsonArray()).OfType<JsonObject>())
            {
                var role = m["role"]?.ToString() == "user" ? "user" : "nova";
                s.Said.Add((role, m["text"]?.ToString() ?? ""));
                if (m["agResult"] is JsonObject r)
                {
                    var res = FromJson(r);
                    var ids = res.Rows.Where(x => x.Id > 0).Select(x => x.Id).ToList();
                    if (ids.Count > 0) foreach (var h in MailIndex.ByIds(ids, 200, 1200)) s.Seen[h.Id] = h;
                    foreach (var row in res.Rows) if (row.Id > 0 && s.Seen.TryGetValue(row.Id, out var mh)) row.Mail = mh;
                    s.Last = res; s.Confirmed = true;
                }
            }
            sessions[chatId] = s;
            return s;
        }
    }

    public static bool HasSession(string chatId) { lock (sessions) return sessions.ContainsKey(chatId); }

    public static Result FromJson(JsonObject j)
    {
        var r = new Result { Text = j["text"]?.ToString() ?? "", Note = j["note"]?.ToString() ?? "", By = j["by"]?.ToString() ?? "" };
        r.Steps = L(j["steps"]); r.Columns = L(j["columns"]);
        foreach (var x in (j["rows"] as JsonArray ?? new()).OfType<JsonObject>())
            r.Rows.Add(new Row { Id = long.TryParse(x["id"]?.ToString(), out var v) ? v : 0, Cells = (x["cells"] as JsonArray)?.Select(c => c?.ToString() ?? "").ToList() ?? new() });
        return r;
    }

    static readonly Regex Wants = new(
        @"\b(sent|send|forward(ed)?|replied|reply|replies|answer(ed)?|respond(ed)?|response|feedback|approv(al|e|ed)|waiting|pending|outstanding|colleague|client|consultant|contractor|charbel|who\b|which (ones|emails|submittals)|not yet|no (formal )?(reply|response)|follow[- ]?up|to\s+\w+\s+from|from\s+my)",
        RegexOptions.IgnoreCase);

    static readonly Regex ReplyAsk = new(@"\b(repl(y|ied|ies)|answer(ed)?|respon(d|ded|se)|feedback|comments?|review(ed)?|approv(al|ed))\b[\s\S]*\b(not|no|or|without|pending|waiting|still|yet|which|did)\b|\b(not|no|without|pending|waiting|still|which|did)\b[\s\S]*\b(repl(y|ied|ies)|answer(ed)?|respon(d|ded|se)|feedback)\b", RegexOptions.IgnoreCase);

    /// <summary>A free request for the agent (who sent what to whom, who answered…) rather than a plain word search.</summary>
    public static bool Wanted(string q) =>
        !string.IsNullOrWhiteSpace(q) && Regex.Matches(q, @"\S+").Count >= 5 && Wants.IsMatch(q);

    // ------------------------------------------------------------------ who is who

    static List<string> UsList() => Brain.ListOf("mza.match", Tracker.Us).Where(x => x.Length > 1).ToList();

    static bool IsUs(MailHit h) => h.Sent || UsList().Any(u => (h.FromName ?? "").Contains(u, StringComparison.OrdinalIgnoreCase) || (h.FromAddr ?? "").Contains(u, StringComparison.OrdinalIgnoreCase));

    static string Dom(string addr)
    {
        addr = addr ?? "";
        var at = addr.LastIndexOf('@');
        if (at < 0) return addr.StartsWith("/O=", StringComparison.OrdinalIgnoreCase) ? "(internal)" : "";
        return addr[(at + 1)..].Trim().TrimEnd('>', ';', ' ').ToLowerInvariant();
    }

    static string Doms(string addrs) => string.Join(", ", (addrs ?? "").Split(';', ',').Select(Dom).Where(d => d.Length > 0).Distinct().Take(6));

    static string Short(string s, int n) { s = Regex.Replace(s ?? "", @"\s+", " ").Trim(); return s.Length > n ? s[..n] + "…" : s; }

    static string Line(MailHit h)
    {
        var who = h.Sent ? $"ME ({Tracker.UsShort})" : $"{h.FromName} <{Dom(h.FromAddr)}>{(IsUs(h) ? " (" + Tracker.UsShort + ")" : "")}";
        var body = Store.AiSeesFull ? " · text: " + Short(h.FirstLines(320), 320) : "";
        return $"#{h.Id} · {h.Date:yyyy-MM-dd HH:mm} · from {who} · to: {Short(h.To, 120)} [{Doms(h.ToAddr)}] · cc: {Short(h.Cc, 100)} [{Doms(h.CcAddr)}] · subject: {Short(h.Subject, 160)}" +
               (string.IsNullOrEmpty(h.AttNames) ? "" : " · files: " + Short(h.AttNames, 140)) + body;
    }

    // ------------------------------------------------------------------ the AI's instructions

    static string System(string where)
    {
        var projects = new StringBuilder();
        foreach (var p in Projects.All().Where(p => p.Code.Length > 0).OrderBy(p => p.Order))
            projects.Append($"- {p.Code} = {p.Name}; also written {string.Join(", ", Projects.NamesOf(p).Where(n => n != p.Code && n != p.Name).Take(6))}{(p.Client.Length > 0 ? "; CLIENT: " + p.Client : "")}\n");
        return
$@"You are Nova's agent on the user's PC (Nova Enhance, designed by A.HAMROUNI). The user ({Me.Name}, {string.Join(", ", Me.Addrs.Take(2))}) works at {Tracker.UsShort}; ""my colleagues"" / ""{Tracker.UsShort}"" = senders matching: {string.Join(", ", UsList())}.
Today is {DateTime.Now:yyyy-MM-dd (dddd)}. Projects:
{projects}
You answer by using TOOLS on the user's own data, one step at a time. Reply with JSON only, one step per reply:
{{""say"":""one short line for the user about this step"",""tool"":""NAME"",""args"":{{...}}}}
TOOLS
- emails: search the email index. args: words (all must appear), any_words (at least one), from (names / addresses / domains, any), to_or_cc (any of them in To or Cc), to, cc, project (code), after, before (yyyy-mm-dd), folder (any|sent|received), limit (≤150). Use domains for people's companies (e.g. the client's domain), words for document codes and topics. The user is in copy of colleagues' emails: use folder ""any"".
- thread: every email of a conversation, oldest first, with From / To / Cc domains. args: id (an email #id) or doc (a document number such as RS5-C7-ACC-AR-SSM-00038). Use it to see who sent what to whom and whether someone replied after.
- log: the 📋 Submittal log (submissions, their {Tracker.UsShort} code / reply, status). args: types (WIR, MAR, SDS, SSM, PQS, RFI, MIR, MSS, TSR, LTR, EI, MOM…), projects (codes), from, to (yyyy-mm-dd), status (Open|Overdue|Closed|Code3/4).
- followup: THE tool for “what did we send to X, and did X reply?” (a client, consultant, contractor). It finds every email sent by the user's colleagues (or args.from) with X in To or Cc, groups them per document number / thread, and checks each thread for a later email FROM X. args: to (X's names / addresses / company domain — use the CLIENT domain from the project list), from (optional; default = the user's colleagues), project (code), words / any_words (optional, e.g. submittal type codes), after, before (yyyy-mm-dd; default: the last 3 years). Only emails with X in TO count as requests (cc: true adds the Cc ones). Daily site reports (DSR) and minutes of meetings (MOM) are left out unless the user asks for them. It reads EVERY email (no newest-N cut) and also X's addresses found from X's own emails. Returns a count line, then one line per document: first sent (date, by whom, To or Cc), how many times, and X's reply (date, #id) matched by document number in any folder, or NO REPLY, or REPLIED … then ASKED AGAIN — NO REPLY since (count that as still waiting). Give ALL its lines in the answer table, not a sample.
- confirm: show the user 3 to 5 example emails (#ids) that you think match, with a question, and get ✓ / ✗ back. args: ids, question. You MUST call confirm once before the final answer, with the best examples you found.
- draft: prepare an Outlook draft (never sent; the user checks and sends). args: to, cc, subject, body. Only when the user asked for a draft / reminder / email.
- answer: the final result. args: text (short summary: what you searched and how you decided), columns (list of column names), rows (list of {{""id"": email #id or 0, ""cells"": [...]}}). Use a table whenever there is more than one item; one row per document / thread, newest first, with a status column such as ""✅ replied 18 Nov"" / ""⏳ no reply (3 days)"".
RULES
- Follow the user's rules in the Nova Brain (given below) — they say how this user works.
- Plan: find candidates (emails / log) → check threads where it matters (sent to whom, replied by whom) → confirm examples → answer.
- Anything “sent to <client / consultant / person> … replied / answered / approved or not” → use followup FIRST (to = that company's domain), then confirm, then answer with one row per document. The log tool only knows {Tracker.UsShort}'s own replies to the contractor — never use it to decide whether the client replied.
- Ignore the filters shown in the window (period, status…): unless the user names a period, search the last 2 years.
- When the user asks for a list / table / “show me all”, the answer MUST have rows (never write “below are…” without them). Keep requests for formal feedback / approval / review / comments; drop pure information forwards, MOMs and payment letters unless asked.
- “Replied / formal response” = an email from that person or their company's domain on the same thread AFTER the request. A reply from the user's colleagues is not the client's reply.
- At most 11 steps. Never invent emails, numbers or names: only what the tools returned. If nothing matches, say so and suggest how to widen the search.
- The result is shown in: {where}.";
    }

    // ------------------------------------------------------------------ the tools

    static List<string> L(JsonNode n) => n is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Trim().Length > 0).ToList()
        : n?.ToString() is { Length: > 0 } s ? s.Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new();

    static DateTime? D(JsonNode n) => DateTime.TryParse(n?.ToString(), out var d) ? d : null;

    static string ToolEmails(JsonObject a, Session s)
    {
        var q = new MailQuery { Limit = Math.Clamp(int.TryParse(a["limit"]?.ToString(), out var lim) ? lim : 80, 1, 150), Folder = a["folder"]?.ToString() is "sent" or "received" ? a["folder"]!.ToString() : "any" };
        q.Words = L(a["words"]); q.AnyWords = L(a["any_words"]); q.From = L(a["from"]); q.ToOrCc = L(a["to_or_cc"]); q.To = L(a["to"]); q.Cc = L(a["cc"]);
        q.After = D(a["after"]); q.Before = D(a["before"]);
        var pj = a["project"]?.ToString() ?? "";
        if (pj.Length > 0) q.Project = Projects.Canon(pj) is { Length: > 0 } c ? c : pj;
        var hits = MailIndex.Search(q);
        // Words in a document number: the index splits on '-', so a code may also be searched in the subject.
        if (hits.Count == 0 && q.Words.Any(w => w.Contains('-')))
            hits = MailIndex.SubmittalsLike(q.Words.Where(w => w.Contains('-')).Select(w => "%" + w + "%"), Array.Empty<string>(), q.After ?? DateTime.Today.AddYears(-6), q.Before ?? DateTime.Now.AddDays(1), q.Limit, 1200);
        foreach (var h in hits) s.Seen[h.Id] = h;
        if (hits.Count == 0) return "0 emails.";
        return $"{hits.Count} email(s), newest first:\n" + string.Join("\n", hits.Select(Line));
    }

    static string ToolThread(JsonObject a, Session s)
    {
        var list = new List<MailHit>();
        var doc = a["doc"]?.ToString()?.Trim() ?? "";
        if (long.TryParse(a["id"]?.ToString()?.TrimStart('#'), out var id))
        {
            var h = s.Seen.TryGetValue(id, out var x) ? x : MailIndex.ByIds(new[] { id }, 1, 1200).FirstOrDefault();
            if (h != null) list.AddRange(MailIndex.ByConv(h.Conv, 60));
            if (doc.Length == 0 && h != null && Regex.Match(h.Subject ?? "", @"[A-Z0-9]{2,}(?:-[A-Z0-9]{1,8}){3,}") is { Success: true } m) doc = m.Value;
        }
        if (doc.Length > 0)
        {
            var baseNo = Regex.Replace(doc, @"[-_ ]?R(ev)?\d{1,2}$", "", RegexOptions.IgnoreCase);
            list.AddRange(MailIndex.SubmittalsLike(new[] { "%" + baseNo + "%" }, new[] { "%" + baseNo + "%" }, DateTime.Today.AddYears(-6), DateTime.Now.AddDays(1), 120, 1200));
        }
        list = list.GroupBy(h => h.Id).Select(g => g.First()).OrderBy(h => h.Date).ToList();
        foreach (var h in list) s.Seen[h.Id] = h;
        if (list.Count == 0) return "No emails for this thread.";
        return $"Thread {(doc.Length > 0 ? doc : "#" + id)} · {list.Count} email(s), oldest first:\n" + string.Join("\n", list.Take(60).Select(Line));
    }

    static readonly Regex DocRx = new(@"[A-Z0-9]{2,}(?:-[A-Z0-9]{1,8}){3,}", RegexOptions.IgnoreCase);
    static string BaseNo(string doc) => Regex.Replace(doc, @"[-_ ]?R(ev)?\d{1,2}$", "", RegexOptions.IgnoreCase).ToUpperInvariant();
    static bool Has(string a, string b) => !string.IsNullOrEmpty(a) && a.Contains(b, StringComparison.OrdinalIgnoreCase);

    static readonly Regex InfoMail = new(@"-(DSR|MOM)-|\b(DSR|MOM|minutes of (?:the )?meeting|daily site report|progress meeting|meeting postpone\w*)\b", RegexOptions.IgnoreCase);
    static readonly Regex InfoAsk = new(@"\b(DSRs?|MOMs?|minutes|daily (?:site )?reports?|meetings?)\b", RegexOptions.IgnoreCase);
    static string Strip(string subj) => Regex.Replace(subj ?? "", @"^\s*((re|fw|fwd|aw|tr|reminder)\s*[:\-]\s*)+", "", RegexOptions.IgnoreCase);

    static readonly Regex AskAgain = new(@"\b(remind(er)?|gentle reminder|kindly (?:review|advise|confirm|provide|share)|for your (?:kind )?(?:review|approval|comments?|feedback)|awaiting|still waiting|appreciate your (?:feedback|comments?|response|reply)|follow[- ]?up|your (?:response|reply|feedback))\b", RegexOptions.IgnoreCase);

    /// <summary>The person's names AND addresses: “Charbel” → also c.abirached@gefinorproperties.com (read from the emails he sent),
    /// so a contractor email that shows only his address is found too.</summary>
    static List<string> Expand(List<string> who)
    {
        var all = new List<string>(who);
        foreach (var w in who.Where(w => !w.Contains('@') && !w.Contains('.') && w.Length > 2))
            try
            {
                foreach (var h in MailIndex.Search(new MailQuery { From = new() { w }, Limit = 40, BodyChars = 1, Folder = "received" }))
                    if ((h.FromAddr ?? "").Contains('@') && !IsUs(h) && !all.Contains(h.FromAddr, StringComparer.OrdinalIgnoreCase)) all.Add(h.FromAddr);
            }
            catch { }
        return all.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
    }

    /// <summary>📨 What the colleagues (or args.from) sent to X, per document / thread, and whether X replied after.
    /// Every email is read (no “newest 500” cut): the requests (from us, X in To or Cc) and everything X sent, matched by document
    /// number in ANY folder (any revision, a new email or the same thread). Asked again after X's reply → “replied, then asked again”.</summary>
    static string ToolFollowup(JsonObject a, Session s, Action<string> detail, CancellationToken ct)
    {
        var to0 = L(a["to"]);
        if (to0.Count == 0) return "args.to is needed: the person's name, address or company domain.";
        detail($"finding {string.Join(", ", to0)}'s addresses…");
        var to = Expand(to0);
        var from = L(a["from"]);
        var q = new MailQuery { Limit = 20000, Cap = 20000, BodyChars = 700, Folder = "any", ToOrCc = to, Words = L(a["words"]), AnyWords = L(a["any_words"]), After = D(a["after"]) ?? DateTime.Today.AddYears(-3), Before = D(a["before"]) };
        if (from.Count == 0) q.FromOrSent = UsList();
        var pj = a["project"]?.ToString() ?? "";
        if (pj.Length > 0) q.Project = Projects.Canon(pj) is { Length: > 0 } c ? c : pj;
        detail($"reading every email sent to {string.Join(", ", to0)} since {q.After:dd MMM yyyy}…");
        // A request = X in To (an email only copied to X is information — MZA's reply to the contractor, a forward for record);
        // args.cc = true also counts the Cc ones.
        bool withCc = a["cc"]?.ToString() is "true" or "True" or "yes";
        var hits = MailIndex.Search(q).Where(h => from.Count == 0 ? IsUs(h) : from.Any(f => Has(h.FromName, f) || Has(h.FromAddr, f) || (h.Sent && f is "me" or "I")))
            .Where(h => withCc || to.Any(t => Has(h.To, t) || Has(h.ToAddr, t))).ToList();
        if (hits.Count == 0) return $"0 emails from {(from.Count == 0 ? Tracker.UsShort : string.Join(", ", from))} with {string.Join(", ", to)} in To/Cc since {q.After:yyyy-MM-dd}.";
        bool Doc(MailHit h, out string key) { var m = DocRx.Match(h.Subject ?? ""); key = m.Success ? BaseNo(m.Value) : "thread:" + h.Conv; return m.Success; }
        // Daily site reports and minutes of meetings are information, not review requests: left out unless asked for.
        var asked = (s.Said.LastOrDefault(x => x.Role == "user").Text ?? "") + " " + (a["include"]?.ToString() ?? "");
        bool keepInfo = InfoAsk.IsMatch(asked);
        var groups = hits.Where(h => keepInfo || !InfoMail.IsMatch(h.Subject ?? ""))
            .GroupBy(h => { Doc(h, out var k); return k; }).OrderByDescending(g => g.Max(h => h.Date)).ToList();
        if (groups.Count == 0) return $"{hits.Count} email(s) sent to {string.Join(", ", to0)}, all daily site reports / minutes of meetings (left out — ask for them to include them).";
        var rowsOut = new List<(long Id, string[] Cells)>();
        // ⚡ ONE search for everything X sent since the first request, matched below by document number or by thread.
        detail($"reading everything {string.Join(", ", to0)} sent since {hits.Min(h => h.Date):dd MMM yyyy}…");
        var fromX = MailIndex.Search(new MailQuery { From = to, After = hits.Min(h => h.Date).AddDays(-1), Limit = 20000, Cap = 20000, BodyChars = 300, Folder = "any" })
            .Where(h => !h.Sent && !IsUs(h)).GroupBy(h => (h.Subject, h.Date)).Select(g => g.First()).ToList();
        foreach (var h in fromX) s.Seen[h.Id] = h;
        var byDoc = fromX.Select(h => (h, k: DocRx.Match(h.Subject ?? "") is { Success: true } m ? BaseNo(m.Value) : "")).Where(x => x.k.Length > 0).ToLookup(x => x.k, x => x.h);
        var byConv = fromX.Where(h => !string.IsNullOrEmpty(h.Conv)).ToLookup(h => h.Conv);
        int answered = 0, open = 0, again = 0, live = 0, n = 0;
        var lines = new List<string>();
        var waiting = new List<(string Key, DateTime First, int Line, string Head)>();
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (++n % 20 == 0) detail($"document {n} of {groups.Count}");
            var sent = g.OrderBy(h => h.Date).ToList();
            var first = sent[0];
            foreach (var h in sent) s.Seen[h.Id] = h;
            var convs = sent.Select(h => h.Conv).Where(c => !string.IsNullOrEmpty(c)).ToHashSet();
            var replies = (g.Key.StartsWith("thread:") ? Enumerable.Empty<MailHit>() : byDoc[g.Key]).Concat(convs.SelectMany(c => byConv[c]))
                .Where(h => h.Date > first.Date).GroupBy(h => h.Id).Select(x => x.First()).OrderBy(h => h.Date).ToList();
            var reply = replies.FirstOrDefault();
            var lastReply = replies.LastOrDefault();
            var askedAfter = lastReply == null ? null : sent.Where(h => h.Date > lastReply.Date && (Regex.IsMatch(h.Subject ?? "", @"^\s*(fw|fwd|reminder)", RegexOptions.IgnoreCase) || AskAgain.IsMatch(Short(h.NewPart, 600)))).LastOrDefault();
            var line = to.Any(t => Has(first.ToAddr, t) || Has(first.To, t)) ? "To" : "Cc";
            var by = first.Sent ? $"me ({Tracker.UsShort})" : first.FromName;
            string status;
            if (reply == null) { open++; status = $"NO REPLY ({(DateTime.Now - sent[^1].Date).Days} days since the last)"; }
            else if (askedAfter != null) { again++; status = $"REPLIED {reply.Date:yyyy-MM-dd} by {reply.FromName} #{reply.Id}, then ASKED AGAIN {askedAfter.Date:yyyy-MM-dd} — NO REPLY since"; }
            else { answered++; status = $"REPLY from {reply.FromName} <{Dom(reply.FromAddr)}> {reply.Date:yyyy-MM-dd HH:mm} #{reply.Id}: {Short(reply.Subject, 70)}" + (replies.Count > 1 ? $" (+{replies.Count - 1} more)" : ""); }
            var headTxt = $"#{first.Id} · {(g.Key.StartsWith("thread:") ? "(no doc no.)" : g.Key)} · {Short(first.Subject, 120)} · first sent {first.Date:yyyy-MM-dd HH:mm} by {by} ({line}) · sent {sent.Count}×" +
                          (sent.Count > 1 ? $" (last {sent[^1].Date:yyyy-MM-dd})" : "");
            if (reply == null && !g.Key.StartsWith("thread:")) waiting.Add((g.Key, first.Date, lines.Count, headTxt));
            lines.Add(headTxt + " · " + status);
            var shortSt = reply == null ? $"⏳ no reply ({(DateTime.Now - sent[^1].Date).Days} days)"
                : askedAfter != null ? $"⏳ replied {reply.Date:dd MMM}, asked again {askedAfter.Date:dd MMM} — no reply since"
                : $"✅ replied {reply.Date:dd MMM yyyy}";
            rowsOut.Add((first.Id, new[] { g.Key.StartsWith("thread:") ? "—" : g.Key, Short(DocRx.Replace(Strip(first.Subject), "").Trim(' ', '-', '_', ':'), 90),
                first.Date.ToString("dd MMM yyyy"), first.Sent ? $"me ({Tracker.UsShort})" : first.FromName ?? "", sent.Count.ToString(), shortSt }));
        }
        // 🔎 Before Nova says “no reply”: Outlook is asked live for anything they sent with that document number (an email the index
        // does not have yet — just arrived, in a folder not read yet). Found → the row says so, and Sync + repair brings it in.
        if (waiting.Count > 0)
            try
            {
                detail($"re-checking {waiting.Count} “no reply” in Outlook live…");
                var liveMails = ClaudeOutlook.LiveFrom(to, waiting.Min(w => w.First), 60);
                foreach (var w in waiting)
                {
                    var hit = liveMails.Where(x => x.Date > w.First && DocRx.Match(x.Subject ?? "") is { Success: true } m && BaseNo(m.Value) == w.Key).OrderBy(x => x.Date).FirstOrDefault();
                    if (hit.Entry == null) continue;
                    lines[w.Line] = w.Head + $" · REPLY from {hit.From} {hit.Date:yyyy-MM-dd HH:mm} — found LIVE in Outlook (not in Nova's index yet; Nova is adding it): {Short(hit.Subject, 70)}";
                    var ri = rowsOut.FindIndex(x => x.Cells[0] == w.Key);
                    if (ri >= 0) rowsOut[ri].Cells[5] = $"✅ replied {hit.Date:dd MMM yyyy} (found live in Outlook)";
                    open--; answered++; live++;
                }
                if (live > 0) MailIndex.SyncNow();
            }
            catch (Exception e) { Log.Warn("follow-up live check", e.Message); }
        var head = (waiting.Count > 0 ? $"🔎 every “no reply” was re-checked in Outlook live{(live > 0 ? $" — {live} reply(ies) found there that the index did not have yet" : " — confirmed")}.\n" : "") +
                   $"{hits.Count} email(s) sent to {string.Join(", ", to0)}{(to.Count > to0.Count ? " (also as " + string.Join(", ", to.Except(to0)) + ")" : "")} since {q.After:yyyy-MM-dd} · " +
                   $"{groups.Count} document(s) / thread(s): {answered} replied · {open} NO REPLY · {again} replied then asked again · {fromX.Count} email(s) from them read. Newest first:\n";
        s.Follow = rowsOut;
        s.FollowHead = $"{groups.Count} document(s) sent to {string.Join(", ", to0)} since {q.After:dd MMM yyyy}: {answered} replied · {open} with no reply · {again} replied, then asked again with no reply since" +
                       (keepInfo ? "" : " (daily site reports and minutes of meetings left out)") + (live > 0 ? $" · {live} reply(ies) found live in Outlook" : "") + ".";
        return head + string.Join("\n", lines.Take(400)) + (lines.Count > 400 ? $"\n… {lines.Count - 400} older not shown" : "") + "\n";
    }

    static string ToolLog(JsonObject a, Session s, CancellationToken ct)
    {
        var o = new SubmittalLog.Options();
        o.Types = L(a["types"]).Select(x => x.ToUpperInvariant()).ToList();
        o.Projects = L(a["projects"]).Select(x => Projects.Canon(x) is { Length: > 0 } c ? c : x).ToList();
        if (D(a["from"]) is { } f) o.From = f; else o.From = DateTime.Today.AddYears(-2);
        if (D(a["to"]) is { } t) o.To = t.Date.AddDays(1).AddSeconds(-1);
        o.Status = a["status"]?.ToString() ?? "";
        var r = SubmittalLog.Run(o, null, ct);
        var sb = new StringBuilder($"{r.Items.Count} item(s) ({o.From:yyyy-MM-dd} → {o.To:yyyy-MM-dd}):\n");
        foreach (var i in r.Items.Take(150))
        {
            if (i.Sub != null) s.Seen[i.Sub.Id] = i.Sub;
            sb.Append($"#{i.Sub?.Id ?? 0} · {i.Project} · {i.Type} · {i.No} {i.Rev} · {Short(i.Title, 90)} · submitted {i.Submitted:yyyy-MM-dd} · status {i.Status} {i.Code} · {Tracker.UsShort} reply {(i.Reply is { } rd ? rd.ToString("yyyy-MM-dd") : "–")} by {i.By}\n");
        }
        return sb.ToString();
    }

    static string ToolDraft(JsonObject a)
    {
        string to = a["to"]?.ToString() ?? "", cc = a["cc"]?.ToString() ?? "", subj = a["subject"]?.ToString() ?? "", body = a["body"]?.ToString() ?? "";
        var ok = false; string err = "";
        void Make()
        {
            try
            {
                using var step = UiWatch.Outlook("preparing a draft for the agent");
                dynamic app = Outlook.Get();
                dynamic m = app.CreateItem(0);
                m.To = to; m.CC = cc; m.Subject = subj; m.Body = body + "\n\n";
                m.Display(false);
                ok = true;
            }
            catch (Exception e) { err = e.Message; }
        }
        if (Program.UiContext != null) Program.UiContext.Send(_ => Make(), null); else Make();
        return ok ? "Draft opened in Outlook (not sent)." : "Could not prepare the draft: " + err;
    }

    // ------------------------------------------------------------------ the loop

    /// <summary>One request (or a follow-up) of the conversation. <paramref name="where"/>: chat · email · log (its ⚙ AI for each feature applies).</summary>
    public static async Task<Result> Run(string question, Session s, Host host, string where, CancellationToken ct, int maxSteps = 12, int aiSeconds = 0)
    {
        using var aiFor = Ai.For(where == "log" ? "email" : where);
        var sys = System(where switch { "log" => "the Submittal log window (a table)", "email" => "the Search for email window (a list of emails)", _ => "Nova Chat (a table in the chat)" });
        var transcript = new StringBuilder();
        foreach (var (role, text) in s.Said.TakeLast(8)) transcript.Append(role == "user" ? "USER: " : "NOVA: ").Append(Short(text, 1500)).Append('\n');
        if (s.Last != null && s.Last.Rows.Count > 0)
            transcript.Append("PREVIOUS RESULT (for follow-ups): ").Append(string.Join(" | ", s.Last.Columns)).Append('\n')
                .Append(string.Join("\n", s.Last.Rows.Take(60).Select(r => $"#{r.Id} · " + string.Join(" | ", r.Cells)))).Append('\n');
        transcript.Append("USER: ").Append(question).Append('\n');
        s.Said.Add(("user", question));
        var res = new Result();
        bool confirmed = s.Confirmed;
        bool pushed = false, usedFollow = false, pushedFollow = false;
        for (int step = 1; step <= maxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            var user = transcript.Length > 90_000 ? transcript.ToString()[^90_000..] : transcript.ToString();
            string raw;
            if (aiSeconds > 0)
            {
                // 🧪 tests: an AI answer that hangs (Gemini sometimes takes 2 min) is asked again once instead of waiting.
                raw = null;
                for (int tryN = 0; tryN < 2 && raw == null; tryN++)
                {
                    using var one = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    one.CancelAfter(TimeSpan.FromSeconds(aiSeconds));
                    try { raw = await Ai.Chat(sys, user, false, true, null, one.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { host.Detail($"the AI took more than {aiSeconds} s — asking again"); }
                }
                raw ??= "{\"tool\":\"answer\",\"args\":{\"text\":\"The AI did not answer in time.\"}}";
            }
            else raw = await Ai.Chat(sys, user, false, true, null, ct);
            res.By = Ai.LastBy; if (Ai.LastNote.Length > 0) res.Note = Ai.LastNote;
            JsonObject j;
            try { j = Ai.ParseJson(raw) ?? new JsonObject(); } catch { j = new JsonObject(); }
            // An answer cut off in the middle (a long table): the complete rows are kept.
            if (j.Count == 0 && (raw ?? "").Contains('{')) j = Repair(raw) ?? new JsonObject { ["tool"] = "answer", ["args"] = new JsonObject { ["text"] = raw } };
            // The table sent without the {"tool":"answer","args":…} around it.
            if (j["tool"] == null && j["args"] == null && (j["rows"] != null || j["columns"] != null)) j = new JsonObject { ["tool"] = "answer", ["args"] = j.DeepClone() };
            var tool = j["tool"]?.ToString() ?? "answer";
            var args = j["args"] as JsonObject ?? new JsonObject();
            var say = j["say"]?.ToString() ?? "";
            if (say.Length > 0) { host.Progress(say); res.Steps.Add(say); }
            transcript.Append("NOVA STEP: ").Append(j.ToJsonString()).Append('\n');
            string got;
            if (tool == "followup") usedFollow = true;
            try
            {
                switch (tool)
                {
                    case "emails" or "thread" or "followup" when host.Cache != null:
                    {
                        // the same search with the same words: done once for both runs of a 🧪 test
                        var t0 = tool; var a0 = args;
                        var lz = host.Cache.GetOrAdd(tool + "|" + args.ToJsonString(), _ => new Lazy<Task<(string, List<MailHit>)>>(() => Task.Run(() =>
                        {
                            var tmp = new Session();
                            var g = t0 == "emails" ? ToolEmails(a0, tmp) : t0 == "thread" ? ToolThread(a0, tmp) : ToolFollowup(a0, tmp, host.Detail, ct);
                            return (g, tmp.Seen.Values.ToList());
                        }, ct)));
                        var (g1, seen1) = await lz.Value;
                        foreach (var h in seen1) s.Seen[h.Id] = h;
                        got = g1;
                        break;
                    }
                    case "emails": got = await Task.Run(() => ToolEmails(args, s), ct); break;
                    case "thread": got = await Task.Run(() => ToolThread(args, s), ct); break;
                    case "log": got = await Task.Run(() => ToolLog(args, s, ct), ct); break;
                    case "followup": got = await Task.Run(() => ToolFollowup(args, s, host.Detail, ct), ct); break;
                    case "draft": got = ToolDraft(args); break;
                    case "confirm":
                    {
                        var ids = L(args["ids"]).Select(x => long.TryParse(x.TrimStart('#'), out var v) ? v : 0).Where(v => v > 0).Distinct().Take(5).ToList();
                        got = await Ask(ids, args["question"]?.ToString() ?? "Are these the kind of emails you mean?", s, host);
                        if (got == null) { res.Text = "Stopped — nothing was confirmed."; return res; }
                        confirmed = s.Confirmed = true;
                        break;
                    }
                    default:
                    {
                        // ✓ Always confirm examples first (your choice B): if the AI answers before, Nova asks with the first rows.
                        var rows = (args["rows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                        var rowIds = rows.Select(r => long.TryParse(r["id"]?.ToString()?.TrimStart('#'), out var v) ? v : 0).Where(v => v > 0 && s.Seen.ContainsKey(v)).Distinct().ToList();
                        if (!confirmed && rowIds.Count > 0)
                        {
                            got = await Ask(rowIds.Take(5).ToList(), "Are these the kind of emails you mean?", s, host);
                            if (got == null) { res.Text = "Stopped — nothing was confirmed."; return res; }
                            confirmed = s.Confirmed = true;
                            transcript.Append("TOOL RESULT: ").Append(got).Append("\nNow give the final answer with this feedback (drop what the user rejected, keep what fits the ✓ examples).\n");
                            continue;
                        }
                        // 📨 “sent to X … replied or not?” answered without the follow-up tool (the log / a word search miss most of it): once, back to followup.
                        if (!usedFollow && !pushedFollow && ReplyAsk.IsMatch(question) && Regex.IsMatch(question, @"\b(sent|send|forward\w*|client|consultant|engineer|reviewer|contractor|to [A-Z]\w+)\b"))
                        {
                            pushedFollow = true;
                            host.Detail("checking every request and reply with the follow-up tool first");
                            transcript.Append("TOOL RESULT (answer): REJECTED — this asks whether someone replied to what was sent to them. Call followup first (to = that person's name AND the company domain from the project list), then answer from its lines only.\n");
                            continue;
                        }
                        // A list was asked (or promised “below”) but no rows came: Nova sends the AI back once to finish the table.
                        var ansText = args["text"]?.ToString() ?? j["say"]?.ToString() ?? raw;
                        if (!pushed && rows.Count == 0 && s.Seen.Count > 0 &&
                            (Regex.IsMatch(question, @"\b(all|list|table|show|which|every|csv|excel)\b", RegexOptions.IgnoreCase) || Regex.IsMatch(ansText, @"\b(below|following|table|listed)\b", RegexOptions.IgnoreCase)))
                        {
                            pushed = true;
                            host.Detail("the answer had no table — asking the AI to finish it");
                            transcript.Append("TOOL RESULT (answer): REJECTED — the user asked for a list and your answer has no rows. Give the full table now in args.rows (id + cells), from what the tools returned; if a tool is still needed (e.g. followup), call it first.\n");
                            continue;
                        }
                        // 🛡 Never raw JSON on screen; when the AI's table is missing or unreadable, Nova's own follow-up rows are the table.
                        bool rawJson = Regex.IsMatch(ansText ?? "", @"^\s*[\{\[]");
                        if ((rows.Count == 0 || rawJson) && s.Follow is { Count: > 0 })
                        {
                            res.Text = (rawJson || string.IsNullOrWhiteSpace(ansText) ? "" : ansText.Trim() + "\n\n") + s.FollowHead + (rawJson ? " (Nova's own result — the AI's answer could not be read.)" : "");
                            res.Columns = new() { "Document", "Subject", "First sent", "By", "Times sent", "Status" };
                            foreach (var (fid0, cells) in s.Follow)
                                res.Rows.Add(new Row { Id = fid0, Cells = cells.ToList(), Mail = s.Seen.TryGetValue(fid0, out var fm) ? fm : null });
                            s.Last = res;
                            s.Said.Add(("nova", res.Text + $" ({res.Rows.Count} rows)"));
                            return res;
                        }
                        if (rawJson) ansText = "The AI's answer could not be read — please ask again (or ask for a shorter list).";
                        res.Text = ansText;
                        res.Columns = L(args["columns"]);
                        // Daily site reports and minutes of meetings stay out of the table unless you asked for them.
                        bool infoOk = InfoAsk.IsMatch(question);
                        foreach (var r in rows)
                        {
                            if (!infoOk && InfoMail.IsMatch(string.Join(" ", L(r["cells"])) + " " + (long.TryParse(r["id"]?.ToString()?.TrimStart('#'), out var iv) && s.Seen.TryGetValue(iv, out var im) ? im.Subject : ""))) continue;
                            var row = new Row { Cells = L(r["cells"]) };
                            if (long.TryParse(r["id"]?.ToString()?.TrimStart('#'), out var v) && s.Seen.TryGetValue(v, out var mh)) { row.Id = v; row.Mail = mh; }
                            res.Rows.Add(row);
                        }
                        if (res.Columns.Count == 0 && res.Rows.Count > 0) res.Columns = Enumerable.Range(1, res.Rows.Max(x => x.Cells.Count)).Select(n => "Col " + n).ToList();
                        s.Last = res;
                        s.Said.Add(("nova", res.Text + (res.Rows.Count > 0 ? $" ({res.Rows.Count} rows)" : "")));
                        return res;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { got = "Tool error: " + e.Message; }
            host.Detail("↳ " + Short(got.Split('\n')[0], 120));
            transcript.Append("TOOL RESULT (").Append(tool).Append("): ").Append(got.Length > 40_000 ? got[..40_000] + "\n…(cut)" : got).Append('\n');
        }
        res.Text = "I could not finish in 12 steps — try a narrower request (one project, one type, a period).";
        return res;
    }

    /// <summary>A JSON answer cut off in the middle: cut back to the last complete value and close what is open.</summary>
    static JsonObject Repair(string raw)
    {
        int a = raw.IndexOf('{');
        if (a < 0) return null;
        var t = raw[a..];
        for (int end = t.Length, tries = 0; end > 1 && tries < 400; tries++)
        {
            int cut = t.LastIndexOfAny(new[] { '}', ']', '"' }, end - 1);
            if (cut < 0) break;
            var part = t[..(cut + 1)];
            var stack = new Stack<char>(); bool str = false, esc = false;
            foreach (var ch in part)
            {
                if (str) { if (esc) esc = false; else if (ch == '\\') esc = true; else if (ch == '"') str = false; continue; }
                if (ch == '"') str = true; else if (ch == '{') stack.Push('}'); else if (ch == '[') stack.Push(']'); else if ((ch == '}' || ch == ']') && stack.Count > 0) stack.Pop();
            }
            if (!str)
                try { var o = JsonNode.Parse(part.TrimEnd(',', ' ', '\n') + string.Concat(stack))?.AsObject(); if (o != null && (o["rows"] != null || o["args"] != null)) return o; } catch { }
            end = cut;
        }
        return null;
    }

    /// <summary>One of the agent's data tools, for the 🧠 Brain trainer (emails · thread · followup · log).</summary>
    public static async Task<string> Tool(string name, JsonObject args, Session s, Action<string> detail, CancellationToken ct) => name switch
    {
        "emails" => await Task.Run(() => ToolEmails(args, s), ct),
        "thread" => await Task.Run(() => ToolThread(args, s), ct),
        "followup" => await Task.Run(() => ToolFollowup(args, s, detail, ct), ct),
        "log" => await Task.Run(() => ToolLog(args, s, ct), ct),
        _ => "unknown tool " + name
    };

    public static string Clip(string s, int n) => Short(s, n);

    // ------------------------------------------------------------------ 🎓 what you rejected (✗), for the Brain trainer

    public static class Feedback
    {
        static string FilePath => Path.Combine(Paths.Root, "Rules", "agent-feedback.jsonl");
        static readonly Regex TypeRx = new(@"-(WIR|MIR|MAR|MSS|SDS|SSM|PQS|RFI|TSR|LTR|EI|MOM|RPT|HSE|NCR|SOR|VO|PAY|DSR|PLN|HOV|RTS|CAL|AUTH)-", RegexOptions.IgnoreCase);

        public static void Rejected(List<MailHit> no, string question, string note)
        {
            if (no.Count == 0 || AdminLock.InSandbox) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var lines = no.Select(h => new JsonObject
                {
                    ["at"] = DateTime.Now.ToString("s"), ["type"] = TypeRx.Match(h.Subject ?? "") is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : "",
                    ["dom"] = Dom(h.FromAddr), ["subject"] = Short(h.Subject, 120), ["q"] = Short(question, 120), ["note"] = Short(note, 160)
                }.ToJsonString());
                File.AppendAllLines(FilePath, lines);
            }
            catch (Exception e) { Log.Warn("agent feedback", e.Message); }
        }

        /// <summary>Patterns in what you rejected in the last 30 days (a type or a sender's company, 3 times or more).</summary>
        public static List<(string What, int Count, string Teach)> Patterns()
        {
            var res = new List<(string, int, string)>();
            try
            {
                if (!File.Exists(FilePath)) return res;
                var since = DateTime.Now.AddDays(-30);
                var rows = File.ReadAllLines(FilePath).Select(l => { try { return JsonNode.Parse(l)?.AsObject(); } catch { return null; } })
                    .Where(o => o != null && DateTime.TryParse(o["at"]?.ToString(), out var d) && d > since).ToList();
                // only when it happened in 2 different questions or more (one question's ✗ is about that question)
                bool Wide(IEnumerable<JsonObject> g) => g.Select(o => o["q"]?.ToString() ?? "").Distinct().Count() >= 2;
                var us = Tracker.Us.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 2).ToList();
                foreach (var g in rows.GroupBy(o => o!["type"]?.ToString() ?? "").Where(g => g.Key.Length > 0 && g.Count() >= 3 && Wide(g!)))
                    res.Add(($"{g.Key} items", g.Count(), $"In the agent's examples I rejected {g.Key} items {g.Count()} times (e.g. “{g.First()!["subject"]}”). Teach Nova to leave {g.Key} out of these searches unless I ask for them."));
                foreach (var g in rows.GroupBy(o => o!["dom"]?.ToString() ?? "").Where(g => g.Key.Length > 3 && g.Count() >= 3 && Wide(g!) && !us.Any(u => g.Key.Contains(u) || u.Contains(g.Key))))
                    res.Add(($"emails from {g.Key}", g.Count(), $"In the agent's examples I rejected emails from {g.Key} {g.Count()} times (e.g. “{g.First()!["subject"]}”). Teach Nova how to treat {g.Key} in these searches."));
            }
            catch { }
            return res;
        }
    }

    static async Task<string> Ask(List<long> ids, string question, Session s, Host host)
    {
        var ex = ids.Select(i => s.Seen.TryGetValue(i, out var h) ? h : null).Where(h => h != null).ToList();
        if (ex.Count == 0 || host.Confirm == null) return "(no examples to confirm — continue)";
        host.Progress("✓ / ✗ — please check these examples");
        var r = await host.Confirm(question, ex);
        if (r == null) return null;
        var (yes, no, note) = r.Value;
        Feedback.Rejected(no.Select(i => s.Seen.TryGetValue(i, out var h) ? h : null).Where(h => h != null).ToList(), question, note);   // 🎓 the Brain trainer learns from your ✗
        return $"The user checked the examples: ✓ match: {string.Join(", ", yes.Select(i => "#" + i))}; ✗ not what I mean: {string.Join(", ", no.Select(i => "#" + i))}" +
               (string.IsNullOrWhiteSpace(note) ? "" : "; the user says: " + note);
    }

    // ------------------------------------------------------------------ output

    public static JsonObject Json(Result r) => new()
    {
        ["text"] = r.Text, ["note"] = r.Note, ["by"] = r.By,
        ["steps"] = new JsonArray(r.Steps.Select(x => (JsonNode)x).ToArray()),
        ["columns"] = new JsonArray(r.Columns.Select(x => (JsonNode)x).ToArray()),
        ["rows"] = new JsonArray(r.Rows.Select(x => (JsonNode)new JsonObject
        {
            ["id"] = x.Id, ["entry"] = x.Mail?.Entry ?? "", ["store"] = x.Mail?.StoreId ?? "",
            ["cells"] = new JsonArray(x.Cells.Select(c => (JsonNode)c).ToArray())
        }).ToArray())
    };

    public static JsonArray Examples(List<MailHit> ex) => new(ex.Select(h => (JsonNode)new JsonObject
    {
        ["id"] = h.Id, ["date"] = h.Date.ToString("dd MMM yyyy HH:mm"), ["from"] = h.Sent ? "me" : h.FromName ?? "", ["to"] = Short(h.To, 90),
        ["cc"] = Short(h.Cc, 70), ["subject"] = h.Subject ?? "", ["lines"] = h.FirstLines(160), ["entry"] = h.Entry ?? "", ["store"] = h.StoreId ?? ""
    }).ToArray());

    /// <summary>⬇ The table as Excel (.xlsx) with ✉ Open and 📎 Email file links per row (was a CSV file).</summary>
    public static string Csv(Result r, string title) => Export.AgentExcel(r, title);

    /// <summary>The table in Markdown (Nova Chat shows it as a real table; the [n] open the emails).</summary>
    public static string Markdown(Result r, List<MailHit> cite)
    {
        var sb = new StringBuilder(r.Text.Trim()).Append("\n\n");
        if (r.Rows.Count > 0 && r.Columns.Count > 0)
        {
            sb.Append("| ").Append(string.Join(" | ", r.Columns.Append("Email"))).Append(" |\n|");
            foreach (var _ in r.Columns.Append("")) sb.Append(" --- |");
            sb.Append('\n');
            foreach (var row in r.Rows)
            {
                var link = "";
                if (row.Mail != null) { cite.Add(row.Mail); link = $"[{cite.Count}]"; }
                sb.Append("| ").Append(string.Join(" | ", row.Cells.Select(c => c.Replace("|", "/")).Concat(Enumerable.Repeat("", Math.Max(0, r.Columns.Count - row.Cells.Count))))).Append(" | ").Append(link).Append(" |\n");
            }
        }
        if (r.Note.Length > 0) sb.Append('\n').Append(r.Note);
        return sb.ToString();
    }
}
