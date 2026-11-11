using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// Ask Nova: the AI turns the question into a search; the search runs on this PC.
/// What else the AI may see follows Settings → Email &amp; AI (A · B · C · D).
/// </summary>
static class Assistant
{
    public class Answer
    {
        public string Intent = "search";
        public string Explain = "";
        /// <summary>✦ AI search: what was searched (ideas, passes, candidates → best).</summary>
        public string Searched = "";
        /// <summary>📐 Emails found at once by a document / drawing number in the question.</summary>
        public int ByNumber;
        public bool Slow;
        public JsonObject Filter;
        public List<MailHit> Hits = new();
        public string Message = "";
        public int Target = -1;
        public double Seconds;
    }

    const string ParseSystem = @"You turn a user's question about their own email into a JSON search filter. Reply with JSON only, no other text.
Schema:
{""intent"":""search|outstanding|followup|summarize|draft"",
 ""from"":[names or emails], ""to"":[...], ""cc"":[...],
 ""me_in"":""to|cc|any|none"",
 ""words"":[keywords], ""phrase"":""exact phrase or empty"",
 ""after"":""YYYY-MM-DD or empty"", ""before"":""YYYY-MM-DD or empty"",
 ""folder"":""received|sent|any"", ""has_attachments"":true|false|null, ""unread"":true|null,
 ""unanswered"":true|false, ""target"":number (0 = first email in the current list, -1 = none),
 ""groups"":[[""idea"",""its short form / synonyms / codes""], ...] (max 6 groups, most important idea first; e.g. ""FHC in BOH"" -> [[""FHC"",""fire hose cabinet""],[""BOH"",""back of house""]]),
 ""explain"":""very short English description of the filter, e.g. From Hamza · To Ibrahim · Cc me · all dates""}
Rules:
- ""I"", ""me"", ""my"" mean the user: use me_in (""I was in Cc"" -> me_in cc; ""sent to me"" -> me_in to). Emails the user sent -> folder sent.
- People: put each name in from/to/cc as written. If a name is in Arabic script, also add its usual Latin spelling (e.g. حمزة -> Hamza, إبراهيم -> Ibrahim).
- words: only the important subject words (project names, drawing numbers like A-201, RFI, façade, slab). Keep codes exactly. If the question is not in English, add the English words too. Never put people's names or dates in words.
- ""waiting for my reply"", ""not answered"", ""outstanding"", ""need to respond"" -> intent outstanding, unanswered true.
- ""no answer to my email"", ""follow up"", ""chase"" -> intent followup, folder sent.
- ""summarise/summarize the last one / this email / number 2"" -> intent summarize with target. ""draft/write a reply to …"" -> intent draft with target.
- Dates: relative to today. ""last month"" = the previous calendar month; ""this week"" = since Monday; leave empty when no date is mentioned.
- groups: one group per idea the emails must be about, each with the forms people really write (abbreviation, full words, singular, plural, codes, the English form). Understand construction short forms (SS stainless steel, SSM material submittal, SDS shop drawing, WIR inspection request, MS method statement). Never put names or dates in groups.
- When a previous filter is given, the new request refines it: keep everything unless the request changes it.";

    /// <summary>📐 A document / drawing number in the question: its emails at once, no AI needed (subject, text and attachment names).</summary>
    public static List<MailHit> ByNumber(string question, int max = 80)
    {
        var res = new List<MailHit>();
        foreach (var n in Revisions.Numbers(question).Take(4))
            try { foreach (var h in MailIndex.Search(new MailQuery { Phrase = n, Limit = max })) if (!res.Any(x => x.Entry == h.Entry)) res.Add(h); }
            catch (Exception e) { Log.Warn("ask", "number: " + e.Message); }
        return res.OrderByDescending(h => h.Date).ToList();
    }

    public static async Task<Answer> Ask(string question, JsonObject previous, List<MailHit> currentHits, CancellationToken ct, bool smart = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ans = new Answer();
        JsonObject f;
        if (Ai.Active() != null)
        {
            var today = DateTime.Now;
            var user = new StringBuilder();
            user.Append($"Today is {today:yyyy-MM-dd} ({today:dddd}). The user's name is {(Me.Name.Length > 0 ? Me.Name : "unknown")}.\n");
            if (previous != null) user.Append("Previous filter: ").Append(previous.ToJsonString()).Append('\n');
            if (currentHits != null && currentHits.Count > 0) user.Append($"The current list has {currentHits.Count} emails, newest first.\n");
            user.Append("Question: ").Append(question);
            // Privacy A: only the question (and the dates above) leave the PC.
            // ⏱ The AI has 20 s to understand; after that Nova plans the search itself (ideas + short forms) and carries on.
            try { f = Ai.ParseJson(await SearchGate.Limit(t => Ai.Chat(ParseSystem, user.ToString(), true, true, null, t), 20, ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Log.Warn("ask", "understand: " + e.Message); f = LocalParse(question); ans.Slow = true; }
        }
        else f = LocalParse(question);
        if (f.Count == 0) f = LocalParse(question);
        await SearchGate.Wait(ct);
        ans.Filter = f;
        ans.Intent = Str(f, "intent", "search");
        ans.Explain = Str(f, "explain", "");
        ans.Target = (int)Num(f, "target", -1);

        if (ans.Intent is "summarize" or "draft")
        {
            ans.Hits = currentHits ?? new();
            if (ans.Hits.Count == 0) ans.Message = "Ask Nova to find the emails first, then ask for a summary or a draft.";
            ans.Seconds = sw.Elapsed.TotalSeconds;
            return ans;
        }

        var q = ToQuery(f);
        if (ans.Intent == "outstanding")
        {
            q.Folder = "received"; q.Unanswered = true;
            q.After ??= DateTime.Now.AddDays(-Store.BriefDays);
            q.Limit = 200;
            if (q.MeIn == null && Store.NeedTo && !Store.NeedCcNamed) q.MeIn = "to";
            if (q.MeIn == null) q.MeIn = "any";
            ans.Hits = await Task.Run(() => MailIndex.Search(q), ct);
            var raw = ans.Hits.Where(h => !BriefLogic.IsAuto(h) && !Me.IsMe(h.FromAddr)).ToList();
            ans.Hits = BriefLogic.Classify(await Task.Run(() => Outstanding.Check(raw), ct));
            await BriefLogic.AiCheck(ans.Hits, ct);
        }
        else if (ans.Intent == "followup")
        {
            q.Folder = "sent"; q.FollowUp = true;
            q.Before ??= DateTime.Now.AddDays(-Store.FollowDays);
            q.After ??= DateTime.Now.AddDays(-45);
            ans.Hits = await Task.Run(() => MailIndex.Search(q), ct);
            foreach (var h in ans.Hits) h.Status = "follow";
        }
        else if ((SmartSearch.On || smart) && (q.Words.Count > 0 || q.Phrase.Length > 0 || f["groups"] is JsonArray { Count: > 0 }))
        {
            // ✦ AI search: ideas + short forms, several passes, scoring, meaning search, the AI keeps the best few.
            var groups = SmartSearch.FromPlan(f, question);
            if (ans.Explain.Length == 0) ans.Explain = Describe(q);
            var baseQ = q; baseQ.Words = new(); baseQ.Phrase = "";
            var sr = await SmartSearch.Mail(question, baseQ, groups, ct);
            ans.Hits = sr.Hits;
            ans.Searched = sr.Searched;
            if (Store.AiSeesLines && ans.Hits.Count > 0 && !ans.Slow)
                try { await SearchGate.Limit(async t => { await Summaries(ans.Hits.Take(15).ToList(), t); return true; }, 25, ct); } catch (TimeoutException) { }
        }
        else
        {
            ans.Hits = await Task.Run(() => MailIndex.Search(q), ct);
            if (Store.AiSeesLines && ans.Hits.Count > 0) await Summaries(ans.Hits.Take(15).ToList(), ct);
        }
        // 📐 Emails that carry the document number come first.
        if (ans.Intent is "search" or "" && Revisions.Numbers(question).Count > 0)
        {
            var byNum = await Task.Run(() => ByNumber(question), ct);
            ans.ByNumber = byNum.Count;
            ans.Hits = byNum.Concat(ans.Hits.Where(h => !byNum.Any(b => b.Entry == h.Entry))).ToList();
        }
        if (ans.Slow) ans.Explain = "⏱ the AI took more than 20 s, so Nova planned the search itself · " + ans.Explain.Replace(" (no AI key — simple search)", "");
        if (ans.Explain.Length == 0) ans.Explain = Describe(q);
        ans.Seconds = sw.Elapsed.TotalSeconds;
        return ans;
    }

    static string Str(JsonObject o, string k, string def)
    {
        var v = o[k]?.ToString();
        return string.IsNullOrWhiteSpace(v) || v == "null" ? def : v.Trim();
    }

    static double Num(JsonObject o, string k, double def) => double.TryParse(o[k]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : def;

    static List<string> Strs(JsonObject o, string k)
    {
        var res = new List<string>();
        if (o[k] is JsonArray a) foreach (var x in a) { var s = x?.ToString(); if (!string.IsNullOrWhiteSpace(s)) res.Add(s.Trim()); }
        else if (o[k] is JsonValue v && v.ToString().Trim().Length > 0) res.Add(v.ToString().Trim());
        return res;
    }

    static DateTime? Date(JsonObject o, string k) =>
        DateTime.TryParseExact(Str(o, k, ""), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    static bool? Bool3(JsonObject o, string k) => o[k]?.ToString() switch { "true" => true, "false" => false, _ => null };

    public static MailQuery ToQuery(JsonObject f)
    {
        var meIn = Str(f, "me_in", "none");
        var fo = Str(f, "folder", "any");
        var q = new MailQuery
        {
            From = Strs(f, "from"), To = Strs(f, "to"), Cc = Strs(f, "cc"), Words = Strs(f, "words"), Phrase = Str(f, "phrase", ""),
            MeIn = meIn is "to" or "cc" or "any" ? meIn : null,
            After = Date(f, "after"), Before = Date(f, "before"),
            Folder = fo is "received" or "sent" ? fo : "any",
            HasAttach = Bool3(f, "has_attachments"), Unread = Bool3(f, "unread") == true ? true : null,
            Unanswered = Bool3(f, "unanswered") == true, Limit = 50
        };
        if (q.Before is DateTime b) q.Before = b.AddDays(1); // "before" day included
        var cut = Store.CutOff;
        if (cut > DateTime.MinValue && (q.After == null || q.After < cut)) q.After = cut;
        return q;
    }

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","to","from","in","on","of","for","and","or","is","was","were","did","does","do","me","my","i","about","with","email","emails",
        "mail","mails","any","all","find","show","what","which","who","when","where","send","sent","that","this","it","please","still","not","there"
    };

    /// <summary>Without an AI key: keyword search only.</summary>
    static JsonObject LocalParse(string q)
    {
        var words = Regex.Split(q, @"[^\p{L}\p{N}\-]+").Where(w => w.Length > 2 && !Stop.Contains(w)).Take(6).ToArray();
        var arr = new JsonArray(); foreach (var w in words) arr.Add(w);
        return new JsonObject { ["intent"] = "search", ["words"] = arr, ["explain"] = "Words: " + string.Join(", ", words) + " (no AI key — simple search)" };
    }

    static string Describe(MailQuery q)
    {
        var parts = new List<string>();
        if (q.From.Count > 0) parts.Add("From " + string.Join("/", q.From));
        if (q.To.Count > 0) parts.Add("To " + string.Join("/", q.To));
        if (q.Cc.Count > 0) parts.Add("Cc " + string.Join("/", q.Cc));
        if (q.MeIn != null) parts.Add(q.MeIn == "any" ? "to or Cc me" : (q.MeIn == "to" ? "To me" : "Cc me"));
        if (q.Words.Count > 0) parts.Add("Words: " + string.Join(", ", q.Words));
        if (q.After != null) parts.Add($"since {q.After:dd MMM yyyy}");
        if (q.Before != null) parts.Add($"before {q.Before:dd MMM yyyy}");
        if (q.Unanswered) parts.Add("not answered");
        return parts.Count == 0 ? "All emails" : string.Join(" · ", parts);
    }

    /// <summary>Privacy B/D: one-line summaries from the subject and first lines (300 characters) only.</summary>
    public static async Task Summaries(List<MailHit> hits, CancellationToken ct)
    {
        try
        {
            var sb = new StringBuilder();
            for (int i = 0; i < hits.Count; i++)
                sb.Append($"{i}. From: {hits[i].FromName} | Subject: {hits[i].Subject} | Text: {hits[i].FirstLines(300)}\n");
            var r = Ai.ParseJson(await Ai.Chat(
                "For each email, write a one-line summary (max 14 words) and decide if the reader must reply or act. Reply with JSON only: {\"items\":[{\"i\":0,\"summary\":\"…\",\"needs_reply\":true}]}",
                sb.ToString(), true, true, null, ct));
            if (r["items"] is JsonArray items)
                foreach (var it in items)
                {
                    if (it is not JsonObject o) continue;
                    if (!int.TryParse(o["i"]?.ToString(), out var i) || i < 0 || i >= hits.Count) continue;
                    hits[i].Summary = o["summary"]?.ToString() ?? "";
                    if (o["needs_reply"]?.ToString() is "true" or "false") hits[i].NeedsReply = o["needs_reply"].ToString() == "true";
                }
        }
        catch (OperationCanceledException) { }   // you asked again before the summaries finished
        catch (Exception e) { Log.Error("summaries", e); }
    }

    /// <summary>Privacy C/D: the full email goes to the AI for a summary.</summary>
    public static async Task<string> Summarize(MailHit h, CancellationToken ct)
    {
        using var privateWork = Ai.Private();   // your emails / documents: the routing rule may keep them on the local AI
        if (!Store.AiSeesFull) return "A summary needs Settings → Email & AI → C or D (the full email is sent to the AI only when you ask).";
        var body = h.Body.Length > 12000 ? h.Body[..12000] : h.Body;
        return await Ai.Chat(
            "Summarise this email for a busy professional in 3–6 short bullet points: what is asked, decisions, dates and deadlines, attachments mentioned. Reply in English unless the email is in Arabic or French." + Modes.ProfileNote(),
            $"From: {h.FromName}\nTo: {h.To}\nCc: {h.Cc}\nDate: {h.Date:dd MMM yyyy HH:mm}\nSubject: {h.Subject}\n\n{body}", true, false, null, ct);
    }

    /// <summary>Privacy C/D: writes a reply draft; Outlook opens it for the user to check and send.</summary>
    public static async Task<string> Draft(MailHit h, string instruction, CancellationToken ct)
    {
        using var privateWork = Ai.Private();   // your emails / documents: the routing rule may keep them on the local AI
        if (!Store.AiSeesFull) throw new AiException("Draft reply needs Settings → Email & AI → C or D (the full email is sent to the AI only when you ask).");
        var body = h.NewPart.Length > 8000 ? h.NewPart[..8000] : h.NewPart;
        var sys = "You write email replies for the user. Write ONLY the reply body: no subject line, no notes. Use the language of the email. " +
                  "Clear, professional and concise, in the user's field terminology. Answer every question asked. If a fact is missing, put a short placeholder in [square brackets]. " +
                  $"End with a short closing and the user's first name{(Me.FirstName.Length > 0 ? " (" + Me.FirstName + ")" : "")}." + Memory.StyleNote() + Modes.ProfileNote();
        var user = $"Email received from {h.FromName} on {h.Date:dd MMM yyyy}:\nSubject: {h.Subject}\n\n{body}" +
                   (string.IsNullOrWhiteSpace(instruction) ? "" : "\n\nThe user's instruction for the reply: " + instruction);
        var draft = await Ai.Chat(sys, user, false, false, null, ct);
        try { File.WriteAllText(Path.Combine(Paths.Drafts, $"{DateTime.Now:yyyy-MM-dd-HHmm} {History.SafeName(h.Subject)}.txt"), $"Re: {h.Subject}\r\nTo: {h.FromName}\r\n\r\n{draft.Replace("\n", "\r\n")}"); } catch { }
        History.Add("ask", Ai.Active()?.Id ?? "", "", "draft", h.Subject, draft);
        return draft;
    }
}

/// <summary>Morning brief: emails waiting for the user's reply, and their own emails with no answer.</summary>
static class BriefLogic
{
    static readonly string[] Ask =
    {
        "please", "kindly", "could you", "can you", "would you", "advise", "confirm", "let me know", "your comments", "your approval", "approve",
        "review", "required", "request", "urgent", "asap", "by tomorrow", "deadline", "awaiting", "pending your", "your feedback", "action",
        "يرجى", "الرجاء", "نرجو", "برجاء", "merci de", "pouvez-vous", "veuillez", "svp", "bitte"
    };

    public static bool LooksLikeRequest(MailHit h)
    {
        var t = h.NewPart;
        if (t.Length > 3000) t = t[..3000];
        return t.Contains('?') || Ask.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)) || h.Importance == 2;
    }

    public static bool IsAuto(MailHit h)
    {
        var s = h.Subject ?? "";
        var a = (h.FromAddr ?? "").ToLowerInvariant();
        return Regex.IsMatch(s, @"^(Automatic reply|Auto:|Autoreply|Out of Office|Accepted:|Declined:|Tentative:|Canceled:|Cancelled:|Undeliverable|Delivery Status|Read:|Not read:)", RegexOptions.IgnoreCase)
               || a.Contains("noreply") || a.Contains("no-reply") || a.Contains("donotreply") || a.Contains("mailer-daemon") || a.Contains("postmaster");
    }

    /// <summary>🔴 needs a reply and older than 3 days · 🟠 needs a reply · ⚪ FYI.</summary>
    public static List<MailHit> Classify(List<MailHit> hits)
    {
        foreach (var h in hits)
        {
            bool req = h.NeedsReply ?? LooksLikeRequest(h);
            h.Status = !req ? "fyi" : (h.DaysWaiting > 3 ? "needs" : "week");
        }
        return hits.OrderBy(h => h.Status switch { "needs" => 0, "week" => 1, _ => 2 }).ThenByDescending(h => h.Date).ToList();
    }

    /// <summary>Settings "AI checks for a question / request" (only with B or D).</summary>
    public static async Task AiCheck(List<MailHit> hits, CancellationToken ct)
    {
        if (!Store.NeedAi || !Store.AiSeesLines || hits.Count == 0 || Ai.Active() == null) return;
        var top = hits.Take(25).ToList();
        await Assistant.Summaries(top, ct);
        var re = Classify(hits);
        hits.Clear(); hits.AddRange(re);
    }

    public static async Task<(List<MailHit> Needs, List<MailHit> Follow)> Build(CancellationToken ct)
    {
        var needs = new List<MailHit>();
        if (Store.NeedTo || Store.NeedCcNamed)
        {
            var q = new MailQuery { Folder = "received", Unanswered = true, After = DateTime.Now.AddDays(-Store.BriefDays), Limit = 300, MeIn = Store.NeedTo ? (Store.NeedCcNamed ? "any" : "to") : "cc" };
            var all = await Task.Run(() => MailIndex.Search(q), ct);
            var first = Me.FirstName;
            foreach (var h in all)
            {
                if (IsAuto(h) || Me.IsMe(h.FromAddr)) continue;
                bool inTo = Me.Addrs.Any(a => (h.ToAddr ?? "").Contains(a, StringComparison.OrdinalIgnoreCase)) || (Me.Name.Length > 0 && (h.To ?? "").Contains(Me.Name, StringComparison.OrdinalIgnoreCase));
                if (inTo && Store.NeedTo) { needs.Add(h); continue; }
                if (!inTo && Store.NeedCcNamed && first.Length > 1 && h.NewPart.Contains(first, StringComparison.OrdinalIgnoreCase)) needs.Add(h);
            }
            var checkedNeeds = await Task.Run(() => Outstanding.Check(needs), ct);
            needs = Classify(checkedNeeds.Where(h => h.MaybeAnswered.Length == 0).ToList());
            await AiCheck(needs, ct);
            needs.AddRange(checkedNeeds.Where(h => h.MaybeAnswered.Length > 0));
        }
        var follow = new List<MailHit>();
        if (Store.FollowUps)
        {
            var q = new MailQuery { Folder = "sent", FollowUp = true, Before = DateTime.Now.AddDays(-Store.FollowDays), After = DateTime.Now.AddDays(-45), Limit = 100 };
            follow = (await Task.Run(() => MailIndex.Search(q), ct)).Where(h => !IsAuto(h)).ToList();
            foreach (var h in follow) h.Status = "follow";
        }
        return (needs, follow);
    }

    /// <summary>Once a day, from the chosen hour, when there is something in the index.</summary>
    public static void MaybeShow()
    {
        if (!Store.BriefOn) return;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (Store.Str("win_last_brief") == today || DateTime.Now.Hour < Store.BriefHour) return;
        if (MailIndex.Count() == 0) return;
        Store.Set("win_last_brief", today);
        Screens.Brief();
    }

    /// <summary>Saves a list for Excel (CSV) and as a page to print or save as PDF (HTML). Returns the CSV path.</summary>
    public static string Export(string title, IEnumerable<MailHit> hits)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm");
        var name = History.SafeName(title, 40);
        var csv = Path.Combine(Paths.Briefs, $"{name} {stamp}.csv");
        var html = Path.ChangeExtension(csv, ".html");
        string C(string s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        var sb = new StringBuilder("Status,Date,Days waiting,From,To,Cc,Subject,Summary,Folder\r\n");
        var hb = new StringBuilder($"<!doctype html><meta charset=utf-8><title>{System.Net.WebUtility.HtmlEncode(title)}</title><style>body{{font-family:Segoe UI,Arial;margin:24px}}td,th{{border-bottom:1px solid #ddd;padding:6px 8px;text-align:left;font-size:13px;vertical-align:top}}th{{background:#f3f1ff}}</style><h2>{System.Net.WebUtility.HtmlEncode(title)}</h2><p>{DateTime.Now:dddd dd MMMM yyyy HH:mm} · {Program.Sign}</p><table><tr><th></th><th>Date</th><th>From</th><th>Subject</th><th>Summary</th></tr>");
        foreach (var h in hits)
        {
            sb.Append(string.Join(",", C(StatusText(h.Status)), C(h.Date.ToString("yyyy-MM-dd HH:mm")), h.DaysWaiting.ToString(), C(h.FromName), C(h.To), C(h.Cc), C(h.Subject), C(h.Summary), C(h.Folder))).Append("\r\n");
            hb.Append($"<tr><td>{Dot(h.Status)}</td><td>{h.Date:dd MMM HH:mm}</td><td>{System.Net.WebUtility.HtmlEncode(h.FromName)}</td><td>{System.Net.WebUtility.HtmlEncode(h.Subject)}</td><td>{System.Net.WebUtility.HtmlEncode(h.Summary)}</td></tr>");
        }
        File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));
        File.WriteAllText(html, hb.Append("</table>").ToString(), new UTF8Encoding(false));
        return csv;
    }

    public static string Dot(string status) => status switch { "needs" => "🔴", "week" => "🟠", "fyi" => "⚪", "follow" => "↩", _ => "•" };
    public static string StatusText(string status) => status switch { "needs" => "Needs reply (overdue)", "week" => "Needs reply", "fyi" => "FYI", "follow" => "No answer yet", _ => "" };
}
