using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🎓 The 🧠 Nova Brain trainer: “Talk to the brain” works like a strong trainer. It reads the whole brain, looks at your real data
/// (emails, threads, the log, “sent to X — did X reply?”) before it writes a rule, asks one short question when something is unclear,
/// checks every proposal (duplicates, contradictions, vague words, [setting] syntax), and gives it a test question.
/// 🧪 TEST runs the question with the current brain and with the proposal (nothing saved), shows ➕ / ➖, examples to ✓ / ✗ and the checks;
/// ✓ Apply opens only after a passed test. 🩺 Health check reviews every rule. Designed by A.HAMROUNI
/// </summary>
static class BrainTrainer
{
    // ------------------------------------------------------------------ the conversation (kept while Nova runs)

    public static readonly JsonArray Chat = new();
    /// <summary>The 📎 example files of the last message (the ⚡ quick test checks the rule on them).</summary>
    public static readonly List<(string Name, bool Good, string Text)> LastExamples = new();
    static readonly Agent.Session Data = new();

    static readonly Regex Vague = new(@"\b(relevant|some|usually|normally|maybe|appropriate|etc|various|certain|properly|as needed|if needed|important ones?)\b", RegexOptions.IgnoreCase);
    static readonly Regex Words = new(@"[\p{L}\p{N}][\p{L}\p{N}.@\-]{3,}");

    /// <summary>Text for comparing quotes: lower case, plain quotes and dashes, single spaces.</summary>
    static string Fold(string t) => Regex.Replace((t ?? "").ToLowerInvariant().Replace('“', '"').Replace('”', '"').Replace('’', '\'').Replace('‘', '\'').Replace('–', '-').Replace('—', '-'), @"\s+", " ").Trim(' ', '"', '\'', '.', '…');

    static string BrainText(JsonObject b, bool withTests = true)
    {
        var sb = new StringBuilder();
        foreach (var sec in (b["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            sb.Append($"\n## {sec["icon"]} {sec["name"]} (section id: {sec["id"]}; used for: {string.Join(", ", (sec["tags"] as JsonArray)?.Select(t => t?.ToString()) ?? Array.Empty<string>())})\n");
            foreach (var r in (sec["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                sb.Append($"- {r["id"]} {(r["off"]?.GetValue<bool>() == true ? "(switched off) " : "")}{r["title"]}: {r["text"]}" +
                          (withTests && r["tested"] is JsonObject t ? $"  [🧪 {t["verdict"]} {t["when"]}]" : "") + "\n");
        }
        return sb.ToString();
    }

    static string ProjectsText()
    {
        var sb = new StringBuilder();
        foreach (var p in Projects.All().Where(p => p.Code.Length > 0).OrderBy(p => p.Order))
            sb.Append($"- {p.Code} = {p.Name}{(p.Client.Length > 0 ? "; client: " + p.Client : "")}\n");
        return sb.ToString();
    }

    static string System(JsonObject b) =>
$@"You are the Nova Brain TRAINER inside Nova Enhance (designed by A.HAMROUNI) on the user's PC. The user ({Me.Name}) is an architect at {Tracker.UsShort} ({string.Join(", ", Brain.ListOf("mza.match", Tracker.Us))}).
Nova Brain is the user's own rule book: every Nova feature (Outlook search, the 🧭 agent, the 📋 Submittal log, deep search, writing, the phone) follows it. Your job is to TRAIN it with the user like a strong, careful coach:
- Understand what the user really wants, check it against their REAL data, then write the smallest, sharpest rule — or answer their question from the rules and the data.
- Today: {DateTime.Now:yyyy-MM-dd}. Projects:
{ProjectsText()}
Reply with JSON only, ONE step per reply: {{""say"":""one short line for the user about this step"",""tool"":""NAME"",""args"":{{...}}}}
TOOLS (data — use them before writing a rule that names people, companies, document formats or folders):
- emails: args words, any_words, from, to_or_cc, to, cc, project, after, before (yyyy-mm-dd), folder (any|sent|received), limit (≤60).
- thread: args id (#id) or doc (document number). Who sent what to whom, who replied.
- followup: args to (names / addresses / domain), from (optional), project, words, after. Per document: sent by whom, did X reply.
- log: the 📋 Submittal log. args types, projects, from, to, status.
- rules: find brain rules. args words. (The whole brain is below; use it for exact ids.)
- usage: how many emails of the last N days contain any of these terms (is a rule alive?). args terms (list), days (default 30).
- ask: ONE short question to the user when the instruction is unclear or two readings change the rule. args question, options (2–4 short answers, written as “A · …”, “B · …”). This ends your turn; the user's choice comes back as the next message.
- propose: the change. args reply (what you understood + what you checked in the data, short), changes [{{""op"":""add|change|remove"",""section"":""section id or a new section name"",""id"":""rule id for change/remove, empty for add"",""title"":""2-4 words"",""text"":""the full rule""}}], conflict (which rule it contradicts and why, or """"), test_question (a realistic request the user would type that this rule should change — for writing rules: a short sample text to improve), why (one line: what goes wrong today without it).
- answer: a question answered, no change. args reply (name the rule ids and the evidence: email #ids, documents, dates).
EXAMPLE FILES: the user may give you emails, PDFs or documents as EXAMPLES (✓ “like this” / ✗ “not like this”). Then:
- Learn the PATTERN from them: who sends to whom (roles, companies, domains, To vs Cc), the document-number format, the key phrases, the attachments, what counts as an answer, and what in a ✗ example must be left out.
- Check the pattern in the real emails (emails / followup) before proposing — other emails like the ✓ example must be found too.
- In propose, add: learned (a list of short lines: “what I learned from your example”), must_find (the document numbers of the ✓ examples, so the 🧪 TEST checks they are found), must_not_find (the ✗ ones). Write the rule in general words (any project / client where it applies), with the example only as an “e.g.”.
TRAINER RULES
- Check before you write: if the rule names a company / person / domain / document format, look at real emails first (emails or followup) and use the exact domain and format you saw. Never guess a domain.
- Ask when unclear: scope (which project, which types), what counts (reply with comments? a code? a forward?), and exceptions. One question at a time, with options. Do not ask what the data or the brain already answers.
- Prefer changing an existing rule over adding a near-duplicate. Never propose two rules with the same meaning; if one exists, propose a change of it (its id).
- Every rule names its scope (project code, document types, company domain, section) and has no vague words (relevant, some, usually, appropriate, as needed…).
- Write rules by ROLE and COMPANY DOMAIN (“an MZA colleague (mz-architects.com)”, “the client (gefinorproperties.com)”, “the contractor (accsal.com)”), never by a person's name — a name only as “e.g.”, or when the user asks about that one person.
- “Did X answer / reply / approve?”: an answer is ANY later email FROM X's company domain with the same document number (same thread or a new email, any revision; with or without “Re:”). Never “only on the same thread”. X only in Cc, or replies from others, are not X's answer.
- Respect the existing rules SE-01 / SE-02 (document number = the key, with and without the revision): complete them, never contradict them.
- Settings are written [name = value] (e.g. [overdue.WIR = 2], [mza.match = …], [log.types = …]) — keep that syntax exactly.
- If the instruction contradicts a rule, say which and propose replacing or changing it — never leave two rules that disagree.
- Never invent facts, emails or numbers: only what the tools returned. At most 7 steps, then propose / ask / answer.
- Write the reply in the user's language (English unless they write otherwise); short and clear.
THE BRAIN NOW:{BrainText(b)}";

    /// <summary>One message to the trainer: steps (tools), then a proposal, a question with options, or an answer.</summary>
    public static async Task<JsonObject> Ask(string text, Action<string> step, Action<string> detail, CancellationToken ct, List<(string Name, bool Good, string Text)> examples = null)
    {
        var b = Brain.Snapshot();
        var sys = System(b);
        var t = new StringBuilder();
        foreach (var h in Chat.OfType<JsonObject>().TakeLast(30))
            t.Append(h["role"]?.ToString() == "user" ? "USER: " : "TRAINER: ").Append(Agent.Clip(h["text"]?.ToString() ?? "", 1500)).Append('\n');
        // “Why was WIR-15280 closed?” → what the Submittal log sees for that document.
        foreach (Match d in Regex.Matches(text, @"\b(?:[A-Z0-9]{2,6}-){0,5}(WIR|MIR|MAR|PQS|RFI|SDS)-\d{2,6}\b", RegexOptions.IgnoreCase))
            try { t.Append("FACT FROM THE SUBMITTAL LOG: ").Append(SubmittalLog.Explain(d.Value)).Append('\n'); } catch { }
        // 🎓 What you rejected in the agent's examples (✗), for lessons.
        foreach (var p in Agent.Feedback.Patterns().Take(3)) t.Append("NOTE (agent feedback): ").Append(p.Teach).Append('\n');
        // 📎 Example files: the pattern to learn (✓ like this / ✗ not like this).
        if (examples is { Count: > 0 }) lock (LastExamples) { LastExamples.Clear(); LastExamples.AddRange(examples); }
        if (examples is { Count: > 0 })
        {
            t.Append("EXAMPLE FILES FROM THE USER (learn the pattern):\n");
            int n = 0;
            foreach (var (name, good, body) in examples)
                t.Append($"=== Example {++n} · {(good ? "✓ LIKE THIS" : "✗ NOT LIKE THIS")} · {name} ===\n").Append(body.Length > 9000 ? body[..9000] + "\n…(cut)" : body).Append('\n');
        }
        t.Append("USER: ").Append(text.Length > 0 ? text : "Learn from these examples and propose the rule.").Append('\n');
        if (examples == null || examples.Count == 0) lock (LastExamples) if (Chat.Count == 0) LastExamples.Clear();
        Chat.Add(new JsonObject { ["role"] = "user", ["text"] = (examples is { Count: > 0 } ? "📎 " + string.Join(", ", examples.Select(e => (e.Good ? "✓ " : "✗ ") + e.Name)) + "\n" : "") + text });
        var steps = new JsonArray();
        using var aiFor = Ai.For("brain");
        Brain.Quiet.Value = true;   // the trainer gets the whole brain itself
        try
        {
            bool pushedBack = false;
            for (int i = 1; i <= 8; i++)
            {
                ct.ThrowIfCancellationRequested();
                string raw = null;
                for (int tryN = 0; tryN < 2 && raw == null; tryN++)
                {
                    // an AI answer that hangs (Gemini sometimes takes 2 min) is asked again once instead of waiting
                    using var one = CancellationTokenSource.CreateLinkedTokenSource(ct); one.CancelAfter(TimeSpan.FromSeconds(75));
                    try { raw = await Ai.Chat(sys, t.Length > 80_000 ? t.ToString()[^80_000..] : t.ToString(), false, true, null, one.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { detail("the AI took more than 75 s — asking again"); }
                }
                if (raw == null) throw new AiException("the AI did not answer twice in a row — try again in a minute");
                JsonObject j;
                try { j = Ai.ParseJson(raw) ?? new JsonObject(); } catch { j = new JsonObject { ["tool"] = "answer", ["args"] = new JsonObject { ["reply"] = raw } }; }
                var tool = j["tool"]?.ToString() ?? "answer";
                var a = j["args"] as JsonObject ?? new JsonObject();
                var say = j["say"]?.ToString() ?? "";
                if (say.Length > 0) { step(say); steps.Add(say); }
                t.Append("TRAINER STEP: ").Append(j.ToJsonString()).Append('\n');
                string got;
                switch (tool)
                {
                    case "emails" or "thread" or "followup" or "log":
                        if (tool == "emails" && !(a["limit"] is JsonNode)) a["limit"] = 40;
                        try { got = await Agent.Tool(tool, a, Data, detail, ct); } catch (OperationCanceledException) { throw; } catch (Exception e) { got = "Tool error: " + e.Message; }
                        break;
                    case "rules": got = FindRules(b, a); break;
                    case "usage": got = await Task.Run(() => Usage(a), ct); break;
                    case "ask":
                    {
                        var q = a["question"]?.ToString() ?? j["say"]?.ToString() ?? "";
                        var opts = a["options"] as JsonArray ?? new JsonArray();
                        Chat.Add(new JsonObject { ["role"] = "trainer", ["text"] = q + (opts.Count > 0 ? "\n" + string.Join("\n", opts.Select(o => o?.ToString())) : "") });
                        return new JsonObject { ["t"] = "answer", ["kind"] = "ask", ["reply"] = q, ["options"] = opts.DeepClone(), ["steps"] = steps, ["by"] = Ai.LastBy };
                    }
                    case "propose": return Proposal(b, a, steps);
                    default:
                    {
                        var reply = a["reply"]?.ToString() ?? a["text"]?.ToString() ?? say;
                        // “Updating PR-04…” with no change: the trainer must propose it (once)
                        if (!pushedBack && a["changes"] is not JsonArray { Count: > 0 } && Regex.IsMatch(reply + " " + say, @"\b(updat|chang|add(ed|ing)?|merg|fix|correct|improv|rewrit|propos)\w*\b.{0,40}\b([A-Z]{2}-\d{2}|rule)", RegexOptions.IgnoreCase))
                        {
                            pushedBack = true;
                            t.Append("TOOL RESULT (answer): REJECTED — you describe a change but gave none. Use the tool propose with the exact changes (op, section, id, title, text).\n");
                            continue;
                        }
                        // An “answer” that still carries changes is a proposal.
                        if (a["changes"] is JsonArray { Count: > 0 }) return Proposal(b, a, steps);
                        Chat.Add(new JsonObject { ["role"] = "trainer", ["text"] = reply });
                        return new JsonObject { ["t"] = "answer", ["kind"] = "answer", ["reply"] = reply, ["steps"] = steps, ["by"] = Ai.LastBy };
                    }
                }
                detail("↳ " + Agent.Clip(got.Split('\n')[0], 120));
                t.Append("TOOL RESULT (").Append(tool).Append("): ").Append(got.Length > 30_000 ? got[..30_000] + "\n…(cut)" : got).Append('\n');
            }
        }
        finally { Brain.Quiet.Value = false; }
        return new JsonObject { ["t"] = "answer", ["kind"] = "answer", ["reply"] = "I could not finish in 9 steps — tell me a bit more precisely what to teach (one project, one type).", ["steps"] = steps };
    }

    static string FindRules(JsonObject b, JsonObject a)
    {
        var ws = (a["words"]?.ToString() ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToList();
        var hits = Brain.Rules(b).Where(x => ws.Count == 0 || ws.Any(w => ((x.Rule["text"]?.ToString() ?? "") + " " + x.Rule["title"]).Contains(w, StringComparison.OrdinalIgnoreCase))).Take(30)
            .Select(x => $"{x.Rule["id"]} [{x.Section["name"]}] {x.Rule["title"]}: {x.Rule["text"]}").ToList();
        return hits.Count == 0 ? "No rule matches." : string.Join("\n", hits);
    }

    static string Usage(JsonObject a)
    {
        var terms = (a["terms"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 1).ToList() ?? new();
        int days = int.TryParse(a["days"]?.ToString(), out var d) ? Math.Clamp(d, 1, 365) : 30;
        if (terms.Count == 0) return "args.terms is needed.";
        var sb = new StringBuilder();
        foreach (var term in terms.Take(8))
        {
            var n = MailIndex.Search(new MailQuery { AnyWords = new() { term }, After = DateTime.Today.AddDays(-days), Limit = 200, Folder = "any" }).Count;
            sb.Append($"“{term}”: {(n >= 200 ? "200+" : n.ToString())} email(s) in the last {days} days\n");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ ✓ the proposal and its checks

    static JsonObject Proposal(JsonObject b, JsonObject a, JsonArray steps)
    {
        var changes = a["changes"] as JsonArray ?? new JsonArray();
        var (after, _) = Brain.Apply(b, changes, "you · brain trainer");
        var diff = Brain.Diff(b, after);
        var checks = Checks(b, diff);
        var reply = a["reply"]?.ToString() ?? "";
        Chat.Add(new JsonObject { ["role"] = "trainer", ["text"] = reply + "\nPROPOSED: " + string.Join(" | ", diff.OfType<JsonObject>().Select(x => $"{x["op"]} {x["id"]}: {x["text"]}")) });
        return new JsonObject
        {
            ["t"] = "answer", ["kind"] = "propose", ["reply"] = reply, ["why"] = a["why"]?.ToString() ?? "", ["conflict"] = a["conflict"]?.ToString() ?? "",
            ["changes"] = changes.DeepClone(), ["diff"] = diff, ["checks"] = checks, ["testQuestion"] = a["test_question"]?.ToString() ?? "",
            ["learned"] = (a["learned"] as JsonArray)?.DeepClone() ?? new JsonArray(), ["mustFind"] = (a["must_find"] as JsonArray)?.DeepClone() ?? new JsonArray(), ["mustNotFind"] = (a["must_not_find"] as JsonArray)?.DeepClone() ?? new JsonArray(),
            ["kindOf"] = KindOf(b, changes), ["steps"] = steps, ["by"] = Ai.LastBy
        };
    }

    static List<string> names; static DateTime namesAt;
    /// <summary>The people who wrote to you in the last 120 days (full names of 2+ words) — to spot rules written for one person.</summary>
    static List<string> Names()
    {
        if (names != null && DateTime.Now - namesAt < TimeSpan.FromHours(6)) return names;
        try
        {
            names = MailIndex.Search(new MailQuery { After = DateTime.Today.AddDays(-120), Limit = 3000, Folder = "any" })
                .SelectMany(h => new[] { h.FromName ?? "" }.Concat((h.To ?? "").Split(';')))
                .Select(n => Regex.Replace(n.Trim(), @"\s+", " ")).Where(n => n.Length >= 7 && n.Length < 40 && n.Contains(' ') && !n.Contains('@') && Regex.IsMatch(n, @"^[\p{L}][\p{L} .'-]+$"))
                .Where(n => n != n.ToUpperInvariant() && !Projects.All().Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { names = new(); }
        namesAt = DateTime.Now;
        return names;
    }

    static HashSet<string> Bag(string s) => Words.Matches((s ?? "").ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 3).ToHashSet();

    static double Jaccard(HashSet<string> a, HashSet<string> b) => a.Count == 0 || b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / a.Union(b).Count();

    /// <summary>Quick checks on a proposal (no AI): same meaning as another rule, vague words, broken [setting] syntax.</summary>
    public static JsonObject Checks(JsonObject before, JsonArray diff)
    {
        var dups = new JsonArray(); var vague = new JsonArray(); var syntax = new JsonArray();
        foreach (var d in diff.OfType<JsonObject>().Where(x => x["op"]?.ToString() != "remove"))
        {
            var id = d["id"]?.ToString() ?? ""; var text = d["text"]?.ToString() ?? "";
            var bag = Bag(text);
            foreach (var (s, r) in Brain.Rules(before))
            {
                if (r["id"]?.ToString() == id) continue;
                var sim = Jaccard(bag, Bag(r["text"]?.ToString()));
                if (sim >= 0.5) dups.Add(new JsonObject { ["id"] = id, ["with"] = r["id"]?.ToString(), ["why"] = $"{(int)(sim * 100)}% the same words as {r["id"]} “{Agent.Clip(r["title"]?.ToString(), 40)}” ({s["name"]})" });
            }
            foreach (var w in Vague.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct()) vague.Add($"{id}: “{w}” is vague — say exactly which / when");
            if (BadSetting(text)) syntax.Add($"{id}: a [setting] must be written [name = value]");
            // 👤 people's names: the rule would miss the same email from another colleague
            var people = Names().Where(n => text.Contains(n, StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(text, @"e\.g\.[^.;]{0,80}" + Regex.Escape(n), RegexOptions.IgnoreCase)).Take(4).ToList();
            if (people.Count > 0) vague.Add($"{id}: names people ({string.Join(", ", people)}) — say the role and company instead (e.g. “an MZA colleague (mz-architects.com)”, “the client (domain)”), names only as “e.g.”");
        }
        return new JsonObject { ["duplicates"] = dups, ["vague"] = vague, ["syntax"] = syntax };
    }

    /// <summary>What a test runs: “writing” (a sample text improved), “log” (the 📋 log, item by item, plus a search) or “search” (the 🧭 agent).</summary>
    static string KindOf(JsonObject b, JsonArray changes)
    {
        var tags = new HashSet<string>();
        foreach (var c in changes.OfType<JsonObject>())
        {
            var sn = c["section"]?.ToString() ?? "";
            var sec = (b["sections"] as JsonArray ?? new()).OfType<JsonObject>().FirstOrDefault(s => s["id"]?.ToString() == sn || string.Equals(s["name"]?.ToString(), sn, StringComparison.OrdinalIgnoreCase));
            if (sec == null && c["id"]?.ToString() is { Length: > 0 } id) sec = Brain.Rules(b).FirstOrDefault(x => x.Rule["id"]?.ToString() == id).Section;
            foreach (var tg in (sec?["tags"] as JsonArray ?? new()).Select(x => x?.ToString() ?? "")) tags.Add(tg);
            if (Regex.IsMatch(sn, "submittal|tower|zone|level", RegexOptions.IgnoreCase)) tags.Add("submittal");
        }
        if (tags.Contains("writing") && !tags.Contains("search") && !tags.Contains("submittal")) return "writing";
        // ⚡ the 📋 log is re-read only when the rule changes how the log works (a [setting] like overdue / closed / log / mza / tower…)
        var txt = string.Join(" ", changes.OfType<JsonObject>().Select(c => c["text"]?.ToString() ?? ""));
        if (tags.Contains("submittal") && Regex.IsMatch(txt, @"\[\s*(overdue|closed|log|mza|tower|zone|level|type|code|places)", RegexOptions.IgnoreCase)) return "log";
        return "search";
    }

    // ------------------------------------------------------------------ 🧪 TEST

    public class TestRun
    {
        public string Id = "", Changes = "", Question = "", Kind = "";
        public int Plus, Minus, LogChanged, Examples;
        public bool MissedExample;
        public bool Blocking;
        public string Verdict = "";   // passed · weak · blocked
        public int Yes, No, Seconds;
    }

    static readonly Dictionary<string, TestRun> tests = new();
    static readonly HashSet<string> QuickTypes = new(StringComparer.OrdinalIgnoreCase) { "WIR", "MIR", "MAR", "MSS", "SDS", "SSM", "PQS", "RFI", "TSR", "LTR", "MOM", "RPT", "HSE", "NCR", "SOR", "EI" };
    static readonly Dictionary<string, (string, Agent.Result)> beforeCache = new();

    static string Key(JsonArray changes) => Regex.Replace(changes?.ToJsonString() ?? "", @"\s+", "");

    /// <summary>Runs the test question with the current brain and with the proposal (nothing saved): ➕ / ➖, examples, checks.</summary>
    public static async Task<JsonObject> Test(JsonArray changes, string question, string kind, Action<string> step, Action<string> detail, CancellationToken ct, JsonArray mustFind = null, JsonArray mustNotFind = null, bool quickMode = false)
    {
        var now = Brain.Snapshot();
        var (after, _) = Brain.Apply(now, changes, "test");
        var diff = Brain.Diff(now, after);
        if (kind.Length == 0) kind = KindOf(now, changes);
        // 🧪 Testing a rule as it is (no change): compare the brain WITHOUT it to the brain with it.
        if (diff.Count == 0)
        {
            var ids = changes.OfType<JsonObject>().Select(c => c["id"]?.ToString() ?? "").Where(x => x.Length > 0).ToList();
            if (ids.Count > 0)
            {
                (now, _) = Brain.Apply(after, new JsonArray(ids.Select(i => (JsonNode)new JsonObject { ["op"] = "remove", ["id"] = i }).ToArray()), "test");
                diff = new JsonArray(Brain.Rules(after).Where(x => ids.Contains(x.Rule["id"]?.ToString() ?? "")).Select(x => (JsonNode)new JsonObject { ["op"] = "add", ["id"] = x.Rule["id"]?.ToString(), ["section"] = x.Section["name"]?.ToString(), ["text"] = x.Rule["text"]?.ToString() }).ToArray());
            }
        }
        if (question.Trim().Length == 0) question = kind == "writing" ? "Dear Sir, please find attached the revised shop drawing for you review and approval, kindly revert at the earliest." : "Show me the recent submittals and whether they were answered";
        var run = new TestRun { Id = DateTime.Now.ToString("HHmmssfff"), Changes = Key(changes), Question = question, Kind = kind };
        var res = new JsonObject { ["t"] = "test2", ["id"] = run.Id, ["question"] = question, ["kind"] = kind };
        var examples = new JsonArray();

        // ⚡ Everything at once (it used to run one after the other): the current brain, the new rule, the 📋 log, the AI check.
        // The “current brain” answer is kept for “🧪 Test again” with the same question.
        step("Running the test question twice at once (your brain now · with the new rule)");
        var aiCheck = Task.Run(async () =>
        {
            try { return await AiChecks(now, diff, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { return new JsonObject { ["contradictions"] = new JsonArray(), ["duplicates"] = new JsonArray(), ["weak"] = new JsonArray($"(the AI check did not answer: {e.Message})"), ["better"] = "" }; }
        }, ct);
        Task<JsonObject> logTask = kind == "log" && !quickMode ? Task.Run(() => LogDiff(now, after, 30), ct) : Task.FromResult<JsonObject>(null);
        res["mode"] = quickMode && kind != "writing" ? "quick" : "full";

        if (quickMode && kind != "writing")
        {
            // ⚡ QUICK TEST: the rule applied to your 📎 examples and to the 20 most similar real emails, in one AI reading.
            step("Picking your examples and the 20 most similar emails");
            var ruleText = string.Join(" ", diff.OfType<JsonObject>().Select(d => d["text"]?.ToString() ?? ""));
            var doms = Regex.Matches(ruleText, @"\b[\w-]+(?:\.[\w-]+)*\.(?:com|ae|net|org|co|io)\b", RegexOptions.IgnoreCase).Select(m => m.Value.ToLowerInvariant()).Distinct().Where(d => !Tracker.Us.Contains(d, StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
            var pj = Projects.All().Select(p => p.Code).Where(c => c.Length > 1 && Regex.IsMatch(ruleText, @"\b" + Regex.Escape(c) + @"\b", RegexOptions.IgnoreCase)).ToList();
            var sample = await Task.Run(() =>
            {
                var l = new List<MailHit>();
                if (doms.Count > 0)
                {
                    l.AddRange(MailIndex.Search(new MailQuery { ToOrCc = doms, After = DateTime.Today.AddDays(-150), Limit = 12, Folder = "any", Project = pj.Count == 1 ? pj[0] : "" }));
                    l.AddRange(MailIndex.Search(new MailQuery { From = doms, After = DateTime.Today.AddDays(-150), Limit = 8, Folder = "any", Project = pj.Count == 1 ? pj[0] : "" }));
                }
                if (l.Count < 10)
                {
                    var words = Regex.Matches(ruleText, @"\b[A-Z]{3,4}\b").Select(m => m.Value).Where(w => QuickTypes.Contains(w)).Distinct().Take(5).ToList();
                    l.AddRange(MailIndex.Search(new MailQuery { AnyWords = words.Concat(pj).Distinct().ToList(), After = DateTime.Today.AddDays(-90), Limit = 20 - l.Count, Folder = "any" }));
                }
                return l.GroupBy(h => h.Id).Select(g => g.First()).Where(h => !Projects.Private.IsMatch($"{h.Subject} {h.Folder} {h.AttNames}")).OrderByDescending(h => h.Date).Take(20).ToList();
            }, ct);
            List<(string Name, bool Good, string Text)> files; lock (LastExamples) files = LastExamples.ToList();
            step($"Reading {files.Count + sample.Count} email(s) with the new rule (one AI call)");
            var sb = new StringBuilder();
            int k = 0;
            foreach (var f in files) sb.Append($"\n[{++k}] 📎 EXAMPLE FILE “{f.Name}”:\n{Agent.Clip(f.Text, 2500)}\n");
            foreach (var h in sample) sb.Append($"\n[{++k}] From: {h.FromName} <{h.FromAddr}> · To: {Agent.Clip(h.To + " [" + h.ToAddr + "]", 200)} · Cc: {Agent.Clip(h.Cc + " [" + h.CcAddr + "]", 160)} · {h.Date:yyyy-MM-dd HH:mm}\nSubject: {h.Subject}\n{Agent.Clip(h.Body, 600)}\n");
            var sys = "You apply the user's NEW RULE (with the rest of their rule book) to each numbered email. Reply with JSON only: " +
                      "{\"items\":[{\"n\":1,\"fits\":true,\"label\":\"what Nova says under the new rule, 2-6 words (e.g. Request to the client · Answered by the client · not concerned)\",\"why\":\"one short reason\",\"changed\":true}]} — " +
                      "fits = the email is one the new rule is about; changed = Nova would treat it differently WITHOUT the new rule. Every email gets one item.";
            var user = "THE NEW RULE:\n" + string.Join("\n", diff.OfType<JsonObject>().Select(d => $"{d["op"]} {d["id"]}: {d["text"]}")) + "\n\nTHE OTHER RULES (short):" + Agent.Clip(BrainText(now, false), 20000) + "\n\nTHE EMAILS:" + sb;
            JsonObject j;
            using (Ai.For("brain"))
            {
                using var one = CancellationTokenSource.CreateLinkedTokenSource(ct); one.CancelAfter(TimeSpan.FromSeconds(90));
                Brain.Quiet.Value = true;
                try { j = Ai.ParseJson(await Ai.Chat(sys, user, false, true, null, one.Token)) ?? new JsonObject(); }
                finally { Brain.Quiet.Value = false; }
            }
            var items = (j["items"] as JsonArray ?? new()).OfType<JsonObject>().ToDictionary(x => x["n"]?.GetValue<int>() ?? 0, x => x);
            var rows = new JsonArray(); k = 0; int changedN = 0;
            var missed0 = new List<string>();
            foreach (var f in files)
            {
                items.TryGetValue(++k, out var it);
                var fits = it?["fits"]?.GetValue<bool>() == true;
                if (fits != f.Good) missed0.Add($"{(f.Good ? "✓" : "✗")} {f.Name} → Nova says “{it?["label"]}”");
                rows.Add(new JsonObject { ["key"] = f.Name, ["cells"] = new JsonArray("📎 " + f.Name, $"{(fits ? "✓ fits" : "— not concerned")} · {it?["label"]}", it?["why"]?.ToString() ?? ""), ["mark"] = fits == f.Good ? "plus" : "minus" });
                examples.Add(new JsonObject { ["id"] = -k, ["subject"] = "📎 " + f.Name + (f.Good ? " (your ✓ example)" : " (your ✗ example)"), ["lines"] = $"Nova says: {it?["label"]} — {it?["why"]}", ["date"] = "", ["from"] = "", ["to"] = "" });
            }
            var exMails = new List<MailHit>();
            foreach (var h in sample)
            {
                items.TryGetValue(++k, out var it);
                var ch = it?["changed"]?.GetValue<bool>() == true; if (ch) changedN++;
                rows.Add(new JsonObject { ["key"] = h.Subject, ["cells"] = new JsonArray(Agent.Clip(h.Subject, 90), $"{(it?["fits"]?.GetValue<bool>() == true ? "✓ fits" : "— not concerned")} · {it?["label"]}", (ch ? "⟳ changes · " : "") + (it?["why"]?.ToString() ?? "")), ["mark"] = ch ? "plus" : "" });
                if (it?["fits"]?.GetValue<bool>() == true && exMails.Count < 5) exMails.Add(h);
            }
            foreach (var o in Agent.Examples(exMails).OfType<JsonObject>())
            {
                var idx = sample.FindIndex(h => h.Id == (o["id"]?.GetValue<long>() ?? 0));
                if (idx >= 0 && items.TryGetValue(files.Count + idx + 1, out var it)) o["lines"] = $"Nova says: {it["label"]} — {it["why"]}";
                examples.Add(o.DeepClone());
            }
            res["after"] = new JsonObject { ["text"] = $"{files.Count} example file(s) · {sample.Count} similar email(s)", ["columns"] = new JsonArray("Email", "With the new rule", "Why"), ["rows"] = rows };
            res["before"] = new JsonObject { ["text"] = "", ["rows"] = new JsonArray() };
            run.Plus = changedN;
            if (missed0.Count > 0) { run.MissedExample = true; res["quickMissed"] = new JsonArray(missed0.Select(x => (JsonNode)x).ToArray()); }
            if (examples.Count == 0) examples.Add(new JsonObject { ["id"] = 1, ["subject"] = "The results with the new rule", ["lines"] = "no similar email found", ["date"] = "", ["from"] = "", ["to"] = "" });
        }
        else if (kind == "writing")
        {
            const string sys = "You improve the text: fix grammar, spelling and wording, keep the meaning, the language and the format. Reply with the improved text only.";
            async Task<string> Write(JsonObject brain)
            {
                using var f = Ai.For("brain");
                Brain.Override.Value = brain;
                using var one = CancellationTokenSource.CreateLinkedTokenSource(ct); one.CancelAfter(TimeSpan.FromSeconds(90));
                return await Ai.Chat(sys, question, true, false, null, one.Token);
            }
            var bKey = "w|" + Brain.Version + "|" + question;
            Task<string> tb;
            lock (beforeCache) tb = beforeCache.TryGetValue(bKey, out var cached) && cached.Item1.Length > 0 ? Task.FromResult(cached.Item1) : Task.Run(() => Write(now), ct);
            var ta = Task.Run(() => Write(after), ct);
            var before = await tb; var with = await ta;
            lock (beforeCache) beforeCache[bKey] = (before, null);
            res["before"] = new JsonObject { ["text"] = before.Trim(), ["rows"] = new JsonArray() };
            res["after"] = new JsonObject { ["text"] = with.Trim(), ["rows"] = new JsonArray() };
            run.Plus = before.Trim() == with.Trim() ? 0 : 1;
            examples.Add(new JsonObject { ["id"] = 1, ["subject"] = "The text with the new rule", ["lines"] = Agent.Clip(with, 400), ["date"] = "", ["from"] = "", ["to"] = "" });
        }
        else
        {
            // ⚡ the same email searches are done once for both runs (the data is the same; only the rule differs)
            var shared = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<(string, List<MailHit>)>>>();
            var hostB = new Agent.Host { Progress = x => detail("now · " + x), Detail = _ => { }, Confirm = null, Cache = shared };
            var hostA = new Agent.Host { Progress = x => detail("new rule · " + x), Detail = _ => { }, Confirm = null, Cache = shared };
            async Task<Agent.Result> Search(JsonObject brain, Agent.Host h)
            {
                Brain.Override.Value = brain;   // this task only (AsyncLocal)
                return await Agent.Run(question, new Agent.Session(), h, "brain", ct, 7, 60);
            }
            var bKey = "s|" + Brain.Version + "|" + question;
            Agent.Result cachedB = null;
            lock (beforeCache) if (beforeCache.TryGetValue(bKey, out var cb)) cachedB = cb.Item2;
            if (cachedB != null) detail("your brain now: the same question was run a moment ago — reused");
            var tB = cachedB != null ? Task.FromResult(cachedB) : Task.Run(() => Search(now, hostB), ct);
            var tA = Task.Run(() => Search(after, hostA), ct);
            var rB = await tB; var rA = await tA;
            lock (beforeCache) { if (beforeCache.Count > 20) beforeCache.Clear(); beforeCache[bKey] = ("", rB); }
            step("Comparing the two results");
            string K(Agent.Row r) => (Regex.Match(string.Join(" ", r.Cells), @"[A-Z0-9]{2,}(?:-[A-Z0-9]{1,8}){3,}", RegexOptions.IgnoreCase) is { Success: true } m ? m.Value : r.Cells.FirstOrDefault() ?? "").ToUpperInvariant();
            var kb = rB.Rows.Select(K).Where(x => x.Length > 0).ToHashSet(); var ka = rA.Rows.Select(K).Where(x => x.Length > 0).ToHashSet();
            JsonObject Side(Agent.Result r, HashSet<string> other, string mark) => new()
            {
                ["text"] = r.Text, ["columns"] = new JsonArray(r.Columns.Select(c => (JsonNode)c).ToArray()),
                ["rows"] = new JsonArray(r.Rows.Take(60).Select(x => (JsonNode)new JsonObject { ["key"] = K(x), ["cells"] = new JsonArray(x.Cells.Select(c => (JsonNode)c).ToArray()), ["mark"] = other.Contains(K(x)) ? "" : mark }).ToArray())
            };
            res["before"] = Side(rB, ka, "minus"); res["after"] = Side(rA, kb, "plus");
            run.Plus = ka.Count(k => !kb.Contains(k)); run.Minus = kb.Count(k => !ka.Contains(k));
            // ✓ / ✗ examples: what the new rule adds first, then what it keeps.
            var ex = rA.Rows.Where(r => r.Mail != null).OrderBy(r => kb.Contains(K(r)) ? 1 : 0).Take(5).Select(r => r.Mail).ToList();
            foreach (var o in Agent.Examples(ex).OfType<JsonObject>()) examples.Add(o.DeepClone());
            if (examples.Count == 0) examples.Add(new JsonObject { ["id"] = 1, ["subject"] = "The results with the new rule (right column)", ["lines"] = Agent.Clip(rA.Text, 300), ["date"] = "", ["from"] = "", ["to"] = "" });
        }
        if (await logTask is JsonObject lg) { res["log"] = lg; run.LogChanged = lg["changed"]?.GetValue<int>() ?? 0; }
        run.Examples = examples.Count;
        res["examples"] = examples;

        // Checks: the quick ones + the AI reading the whole brain.
        step("Checking against your other rules (contradictions, duplicates, weak spots)");
        var quick = Checks(now, diff);
        var ai = await aiCheck;
        var contra = (ai["contradictions"] as JsonArray)?.DeepClone().AsArray() ?? new JsonArray();
        var dups = new JsonArray((quick["duplicates"] as JsonArray ?? new()).Select(x => x?.DeepClone()).Concat((ai["duplicates"] as JsonArray ?? new()).Select(x => x?.DeepClone())).ToArray());
        var weak = new JsonArray((ai["weak"] as JsonArray ?? new()).Select(x => x?.DeepClone()).Concat((quick["vague"] as JsonArray ?? new()).Select(x => x?.DeepClone())).Concat((quick["syntax"] as JsonArray ?? new()).Select(x => x?.DeepClone())).ToArray());
        // 📎 Your ✓ examples must come out, your ✗ ones must not.
        if (kind != "writing")
        {
            var all = ((res["after"]?["text"]?.ToString() ?? "") + " " + string.Join(" ", (res["after"]?["rows"] as JsonArray ?? new()).OfType<JsonObject>().Select(r => string.Join(" ", (r["cells"] as JsonArray ?? new()).Select(x => x?.ToString())))) + " " + (res["log"]?.ToJsonString() ?? "")).ToUpperInvariant();
            string Base(string d) => Regex.Replace((d ?? "").Trim().ToUpperInvariant(), @"[-_ ]?R(EV)?\d{1,2}$", "");
            var found = new JsonArray(); var missed = new JsonArray();
            foreach (var d in (mustFind ?? new()).Select(x => x?.ToString() ?? "").Where(x => x.Length > 3)) (all.Contains(Base(d)) ? found : missed).Add(d);
            foreach (var d in (mustNotFind ?? new()).Select(x => x?.ToString() ?? "").Where(x => x.Length > 3)) if (all.Contains(Base(d))) missed.Add("✗ " + d + " (should NOT be found)");
            if (res["quickMissed"] is JsonArray qm) foreach (var x in qm) missed.Add(x?.DeepClone());
            if (res["mode"]?.ToString() == "quick") { found.Clear(); foreach (var r in (res["after"]?["rows"] as JsonArray ?? new()).OfType<JsonObject>().Where(r => (r["key"]?.ToString() ?? "").Length > 0 && r["mark"]?.ToString() == "plus" && (r["cells"]?[0]?.ToString() ?? "").StartsWith("📎"))) found.Add(r["key"]!.ToString()); }
            res["examplesFound"] = found; res["examplesMissed"] = missed;
            if (missed.Count > 0) { run.MissedExample = true; weak.Add("Your example is not handled right: " + string.Join(", ", missed.Select(x => x?.ToString())) + " — sharpen the rule (✎ / suggested wording) and test again."); }
        }
        if (kind != "writing" && run.Plus == 0 && run.Minus == 0 && run.LogChanged == 0) weak.Add("The new rule changes nothing in this test — try another test question, or the rule is not needed.");
        run.Blocking = contra.Count > 0 || dups.Count > 0 || (quick["syntax"] as JsonArray)?.Count > 0;
        res["checks"] = new JsonObject { ["contradictions"] = contra, ["duplicates"] = dups, ["weak"] = weak, ["better"] = ai["better"]?.ToString() ?? "" };
        res["plus"] = run.Plus; res["minus"] = run.Minus; res["blocking"] = run.Blocking;
        lock (tests) tests[run.Id] = run;
        return res;
    }

    static async Task<JsonObject> AiChecks(JsonObject now, JsonArray diff, CancellationToken ct)
    {
        var sys = "You check a proposed change to the user's rule book (Nova Brain) against all the other rules. Be strict and concrete. " +
                  "Reply with JSON only: {\"contradictions\":[\"RULE-ID: why it disagrees\"],\"duplicates\":[\"RULE-ID: why it says the same\"],\"weak\":[\"what is vague, too wide, too narrow or untestable\"],\"better\":\"a better full wording of the new rule, or empty when it is good\"}. " +
                  "Only real problems: empty lists when there are none.";
        var user = "THE RULES NOW:" + BrainText(now, false) + "\n\nTHE PROPOSED CHANGE:\n" + string.Join("\n", diff.OfType<JsonObject>().Select(d => $"{d["op"]} {d["id"]} ({d["section"]}): {d["text"]}{(d["old"] != null ? "   (was: " + d["old"] + ")" : "")}"));
        using var aiFor = Ai.For("brain");
        Brain.Quiet.Value = true;
        try { return Ai.ParseJson(await Ai.Chat(sys, user, false, true, null, ct)) ?? new JsonObject(); }
        finally { Brain.Quiet.Value = false; }
    }

    /// <summary>The 📋 log with the current brain and with the proposal, item by item (what changes status).</summary>
    public static JsonObject LogDiff(JsonObject now, JsonObject after, int days)
    {
        var o = new SubmittalLog.Options { From = DateTime.Today.AddDays(-days) };
        var a = SubmittalLog.Run(o, now).Items.GroupBy(i => i.No + "|" + i.Rev).ToDictionary(g => g.Key, g => g.First());
        var b = SubmittalLog.Run(o, after).Items;
        var rows = new JsonArray(); int changed = 0;
        foreach (var i in b)
        {
            a.TryGetValue(i.No + "|" + i.Rev, out var old);
            var s0 = old == null ? "—" : old.Status + (old.Code.Length > 0 ? " · " + old.Code : "");
            var s1 = i.Status + (i.Code.Length > 0 ? " · " + i.Code : "");
            if (s0 == s1) continue;
            changed++;
            if (rows.Count < 30) rows.Add(new JsonObject { ["no"] = i.No + " " + i.Rev, ["now"] = s0, ["then"] = s1, ["why"] = i.Why });
        }
        return new JsonObject { ["days"] = days, ["changed"] = changed, ["total"] = b.Count, ["rows"] = rows };
    }

    /// <summary>Your ✓ / ✗ on the examples → passed · weak · blocked. ✓ Apply opens only for “passed”.</summary>
    public static JsonObject Verdict(string id, int yes, int no, int secs = 0)
    {
        TestRun r;
        lock (tests) if (!tests.TryGetValue(id, out r)) return new JsonObject { ["t"] = "verdict", ["id"] = id, ["verdict"] = "blocked", ["why"] = "This test is no longer known — test again." };
        r.Yes = yes; r.No = no; if (secs > 0) r.Seconds = secs;
        string why;
        if (r.Blocking) { r.Verdict = "blocked"; why = "Fix the contradiction / duplicate first (✎ Edit, or ⇢ merge), then test again."; }
        else if (yes == 0) { r.Verdict = "weak"; why = "No example was marked ✓ — the rule does not find what you mean yet."; }
        else if (no > yes) { r.Verdict = "weak"; why = $"Most examples are ✗ ({no} of {yes + no}) — sharpen the rule (use the suggested wording) and test again."; }
        else if (r.MissedExample) { r.Verdict = "weak"; why = "Your 📎 example is not found the right way by the rule — sharpen it and test again."; }
        else if (r.Kind != "writing" && r.Plus == 0 && r.Minus == 0 && r.LogChanged == 0) { r.Verdict = "weak"; why = "The rule changes nothing in this test — try a test question it should change."; }
        else { r.Verdict = "passed"; why = $"{yes} ✓ · {r.Plus} more found · {r.Minus} dropped" + (r.LogChanged > 0 ? $" · {r.LogChanged} log item(s) change" : ""); }
        return new JsonObject { ["t"] = "verdict", ["id"] = id, ["verdict"] = r.Verdict, ["why"] = why };
    }

    /// <summary>The passed test of exactly these changes (else null: ✓ Apply stays locked).</summary>
    public static TestRun Passed(string id, JsonArray changes)
    {
        lock (tests) return tests.TryGetValue(id ?? "", out var r) && r.Verdict == "passed" && r.Changes == Key(changes) ? r : null;
    }

    /// <summary>After ✓ Apply: each changed rule keeps its test (shown with 🧪 in the brain, on the phone and in Versions).</summary>
    public static void MarkTested(JsonObject brain, JsonArray diff, JsonArray changes, TestRun r)
    {
        var ids = diff.OfType<JsonObject>().Where(x => x["op"]?.ToString() != "remove").Select(x => x["id"]?.ToString() ?? "")
            .Concat(changes.OfType<JsonObject>().Where(c => c["op"]?.ToString() == "change").Select(c => c["id"]?.ToString() ?? "")).Where(x => x.Length > 0).Distinct();
        foreach (var id in ids)
        {
            var (_, rule) = Brain.Rules(brain).FirstOrDefault(x => x.Rule["id"]?.ToString() == id);
            if (rule != null) rule["tested"] = new JsonObject { ["verdict"] = "passed", ["when"] = DateTime.Now.ToString("dd MMM yyyy HH:mm"), ["q"] = Agent.Clip(r.Question, 160), ["yes"] = r.Yes, ["no"] = r.No, ["plus"] = r.Plus, ["minus"] = r.Minus, ["secs"] = r.Seconds };
        }
    }

    // ------------------------------------------------------------------ 🩺 health check

    // “Is the rule alive?” looks only for company domains — quoted examples, templates (<number>_RevNN) and typo examples (“FSLV”) are not real words to find
    static readonly Regex TermRx = new(@"\b[\w-]+(?:\.[\w-]+)*\.(?:com|ae|net|org|co|io)\b", RegexOptions.IgnoreCase);

    /// <summary>A [setting] is [name.part = value]; “[n]”, “[1]” or “[see …]” are not settings.</summary>
    static bool BadSetting(string text) =>
        Regex.IsMatch(text, @"\[\s*[a-z][a-z0-9_]*\.[a-z0-9_.]+\s*(?:[^=\]]*)\]", RegexOptions.IgnoreCase) && Regex.Matches(text, @"\[\s*[a-z][a-z0-9_]*\.[a-z0-9_.]+\s*=", RegexOptions.IgnoreCase).Count < Regex.Matches(text, @"\[\s*[a-z][a-z0-9_]*\.[a-z0-9_.]+", RegexOptions.IgnoreCase).Count;

    public static async Task<JsonObject> Health(Action<string> step, CancellationToken ct)
    {
        var b = Brain.Snapshot();
        Brain.FixIds(b);   // the check never stops on two rules with one id (the real brain is repaired when it opens)
        var list = Brain.Rules(b).ToList();
        var rows = new Dictionary<string, JsonObject>();
        JsonArray Notes(string id) => (rows[id]["notes"] as JsonArray)!;
        foreach (var (s, r) in list)
        {
            var id = r["id"]?.ToString() ?? "";
            rows[id] = new JsonObject { ["id"] = id, ["section"] = s["name"]?.ToString(), ["title"] = r["title"]?.ToString(), ["text"] = r["text"]?.ToString(), ["off"] = r["off"]?.GetValue<bool>() == true, ["tested"] = r["tested"]?.DeepClone(), ["autotest"] = r["autotest"]?.DeepClone(), ["notes"] = new JsonArray(), ["bad"] = 0 };
        }
        step("Looking for rules that say the same thing");
        for (int i = 0; i < list.Count; i++)
            for (int k = i + 1; k < list.Count; k++)
            {
                var sim = Jaccard(Bag(list[i].Rule["text"]?.ToString()), Bag(list[k].Rule["text"]?.ToString()));
                if (sim < 0.5) continue;
                var a = list[i].Rule["id"]!.ToString(); var c = list[k].Rule["id"]!.ToString();
                if (a.Equals(c, StringComparison.OrdinalIgnoreCase)) continue;
                // 📜 The Contract area writes one rule per document type with the same wording — alike by design, not a duplicate.
                var contract = list[i].Section == list[k].Section && Regex.IsMatch(list[i].Section["name"]?.ToString() ?? "", "contract", RegexOptions.IgnoreCase);
                if (contract && sim < 0.85) continue;
                if (sim < 0.7)
                {
                    Notes(a).Add($"⚠ {(int)(sim * 100)}% alike {c} — check they don't repeat"); Notes(c).Add($"⚠ {(int)(sim * 100)}% alike {a} — check they don't repeat");
                    foreach (var x in new[] { a, c }) rows[x]["bad"] = Math.Max(rows[x]["bad"]!.GetValue<int>(), 1);
                    continue;
                }
                Notes(a).Add($"⛔ {(int)(sim * 100)}% the same as {c}"); Notes(c).Add($"⛔ {(int)(sim * 100)}% the same as {a}");
                rows[a]["bad"] = 2; rows[c]["bad"] = 2;
            }
        step("Checking wording and settings");
        foreach (var (_, r) in list)
        {
            var id = r["id"]!.ToString(); var text = r["text"]?.ToString() ?? "";
            foreach (var w in Vague.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct()) { Notes(id).Add($"⚠ “{w}” is vague"); rows[id]["bad"] = Math.Max(rows[id]["bad"]!.GetValue<int>(), 1); }
            if (BadSetting(text)) { Notes(id).Add("⛔ a [setting] without “= value”"); rows[id]["bad"] = 2; }
        }
        step("Is each rule still alive? (its names and codes in the last 30 days of email)");
        var hidden = LogPrefs.HiddenProjects;
        var codes = Projects.All().Select(p => p.Code).Where(c => c.Length > 1).ToList();
        foreach (var (_, r) in list)
        {
            ct.ThrowIfCancellationRequested();
            var id = r["id"]!.ToString(); var text = r["text"]?.ToString() ?? "";
            var pj = codes.Where(c => Regex.IsMatch(text, @"\b" + Regex.Escape(c) + @"\b", RegexOptions.IgnoreCase)).ToList();
            if (pj.Count > 0 && pj.All(hidden.Contains)) { Notes(id).Add($"⚠ only about {string.Join(", ", pj)}, hidden in your log (finished?)"); rows[id]["bad"] = Math.Max(rows[id]["bad"]!.GetValue<int>(), 1); }
            var terms = TermRx.Matches(text).Select(m => m.Value).Where(d => !Tracker.Us.Contains(d, StringComparison.OrdinalIgnoreCase)).Distinct().Take(4).ToList();
            if (terms.Count == 0) continue;
            var n = await Task.Run(() => MailIndex.Search(new MailQuery { AnyWords = terms, After = DateTime.Today.AddDays(-30), Limit = 1, Folder = "any" }).Count, ct);
            if (n == 0) { Notes(id).Add($"💤 nothing with {string.Join(", ", terms.Select(x => "“" + x + "”"))} in the last 30 days"); rows[id]["bad"] = Math.Max(rows[id]["bad"]!.GetValue<int>(), 1); }
        }
        step("Asking the AI to read the whole brain for contradictions");
        var contra = new JsonArray();
        try
        {
            // 🩺 EVERY contradiction in one check (was: the AI named one pair, the next showed only after it was fixed): the AI reads
            // the whole brain section by section, then is asked up to twice more “any others?” with what it already found.
            var sys = "You review the user's rule book (Nova Brain) for CONTRADICTIONS between rules (two rules that cannot both be followed, " +
                      "or that give the same list, setting, code or number differently) and rules too vague to apply. Check EVERY pair of rules, " +
                      "section by section, and list ALL contradictions you find — never stop at the first one. " +
                      "A contradiction is ONLY when following one rule makes it impossible to follow the other. Two rules that say the same thing, " +
                      "one rule that refers to another, or a rule that is more detailed than another are NOT contradictions. " +
                      "For each one, quote the exact conflicting words from BOTH rules (copied word for word from the rule text). " +
                      "Reply with JSON only: {\"contradictions\":[{\"ids\":[\"A-01\",\"B-02\"],\"quote_a\":\"exact words from A-01\",\"quote_b\":\"exact words from B-02\",\"why\":\"…\",\"fix\":\"one sentence: what to change\"}],\"vague\":[{\"id\":\"…\",\"why\":\"…\"}]}. Only real problems; an empty list is fine.";
            using var aiFor = Ai.For("brain");
            Brain.Quiet.Value = true;
            var j = new JsonObject { ["contradictions"] = new JsonArray(), ["vague"] = new JsonArray() };
            var seen = new HashSet<string>();
            try
            {
                for (int round = 0; round < 3; round++)
                {
                    var found = string.Join("\n", (j["contradictions"] as JsonArray)!.OfType<JsonObject>().Select(c => string.Join(" ↔ ", (c["ids"] as JsonArray)?.Select(x => x?.ToString()) ?? Array.Empty<string>()) + ": " + c["why"]));
                    var ask = "THE RULES:" + BrainText(b, false) + (round == 0 ? "" : "\n\nALREADY FOUND (do not repeat them):\n" + found + "\n\nAre there any OTHER contradictions? Give only new ones (an empty list if none).");
                    if (round > 0) step($"Looking for more contradictions (round {round + 1})");
                    var r = Ai.ParseJson(await Ai.Chat(sys, ask, false, true, null, ct)) ?? new JsonObject();
                    int added = 0;
                    foreach (var c in (r["contradictions"] as JsonArray ?? new()).OfType<JsonObject>())
                    {
                        var key = string.Join("|", ((c["ids"] as JsonArray)?.Select(x => x?.ToString() ?? "") ?? Array.Empty<string>()).OrderBy(x => x));
                        if (key.Length == 0 || !seen.Add(key)) continue;
                        (j["contradictions"] as JsonArray)!.Add(c.DeepClone()); added++;
                    }
                    if (round == 0) foreach (var v in (r["vague"] as JsonArray ?? new()).OfType<JsonObject>()) (j["vague"] as JsonArray)!.Add(v.DeepClone());
                    if (added == 0) break;
                }
            }
            finally { Brain.Quiet.Value = false; }
            // 🛡 Strict: a finding counts only when both quotes are really in the two rules and they are not the same statement;
            // then the AI judges once more, finding by finding, “can both be followed?”. The rest is dropped (said in the summary).
            var texts = Brain.Rules(b).GroupBy(x => x.Rule["id"]?.ToString() ?? "").ToDictionary(g => g.Key, g => Fold(g.First().Rule["text"]?.ToString()));
            var kept = new List<JsonObject>(); int dropped = 0;
            foreach (var c in (j["contradictions"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                var ids = (c["ids"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(rows.ContainsKey).ToList() ?? new();
                string qa = Fold(c["quote_a"]?.ToString()), qb = Fold(c["quote_b"]?.ToString());
                bool InRule(string id, string q) => q.Length >= 8 && texts.TryGetValue(id, out var t) && t.Contains(q);
                bool quoted = ids.Count == 2 && ((InRule(ids[0], qa) && InRule(ids[1], qb)) || (InRule(ids[0], qb) && InRule(ids[1], qa)));
                bool same = qa.Length > 0 && (qa == qb || Jaccard(Bag(qa), Bag(qb)) >= 0.8);
                if (!quoted || same) { dropped++; continue; }
                kept.Add(c);
            }
            if (kept.Count > 0)
                try
                {
                    step("Double-checking each contradiction (can both rules be followed?)");
                    Brain.Quiet.Value = true;
                    var possible = string.Join("\n", kept.Select((c, i) => $"{i}. {string.Join(" ↔ ", (c["ids"] as JsonArray)!.Select(x => x?.ToString()))}: “{c["quote_a"]}” vs “{c["quote_b"]}” — {c["why"]}"));
                    var v = Ai.ParseJson(await Ai.Chat("You judge possible contradictions in a rule book. For each numbered item, answer whether it is a REAL contradiction: following one rule makes it impossible to follow the other. " +
                        "Same meaning, one rule referring to the other, or one being more detailed is NOT real. Reply with JSON only: {\"real\":[0,2]} — the numbers of the real ones.",
                        "THE RULES:" + BrainText(b, false) + "\n\nPOSSIBLE CONTRADICTIONS:\n" + possible, false, true, null, ct)) ?? new JsonObject();
                    if (v["real"] is JsonArray real)
                    {
                        var keep = real.Select(x => int.TryParse(x?.ToString(), out var k) ? k : -1).ToHashSet();
                        dropped += kept.Count(c => !keep.Contains(kept.IndexOf(c)));
                        kept = kept.Where((c, i) => keep.Contains(i)).ToList();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                finally { Brain.Quiet.Value = false; }
            foreach (var c in kept)
            {
                var ids = (c["ids"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(rows.ContainsKey).ToList() ?? new();
                foreach (var id in ids) { Notes(id).Add($"⛔ contradicts {string.Join(", ", ids.Where(x => x != id))}: {c["why"]}"); rows[id]["bad"] = 2; }
                if (ids.Count > 0) contra.Add(c.DeepClone());
            }
            if (dropped > 0) step($"{dropped} possible contradiction(s) dropped — not real after checking the exact words");
            foreach (var v in (j["vague"] as JsonArray ?? new()).OfType<JsonObject>())
                if (v["id"]?.ToString() is { } id && rows.ContainsKey(id)) Notes(id).Add("💡 " + v["why"]);   // a suggestion, not a fault
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { contra.Add(new JsonObject { ["ids"] = new JsonArray(), ["why"] = "(the AI check did not answer: " + e.Message + ")", ["fix"] = "" }); }
        int strong = 0, weak = 0, untested = 0, bad = 0, auto = 0;
        foreach (var r in rows.Values)
        {
            // 🧪 the automatic test (never “strong”: ✅ strong still needs your ✓ on the examples)
            if (r["tested"] == null && r["autotest"] is JsonObject at)
            {
                var v = at["verdict"]?.ToString();
                if (v == "no effect") { Notes(r["id"]!.ToString()).Add($"💤 changed nothing in its auto test (“{at["q"]}”)"); r["bad"] = Math.Max(r["bad"]!.GetValue<int>(), 1); }
                else if (v == "blocked") { Notes(r["id"]!.ToString()).Add("⛔ auto test: blocked — a duplicate or a contradiction"); r["bad"] = 2; }
            }
            var lvl = r["bad"]!.GetValue<int>();
            var isAuto = r["tested"] == null && r["autotest"]?["verdict"]?.ToString() == "works";
            var badge = lvl == 2 ? "⛔ fix" : lvl == 1 ? "⚠ weak" : r["tested"] != null ? "✅ strong" : isAuto ? "🔎 auto ✓" : "○ untested";
            r["badge"] = badge;
            if (lvl == 2) bad++; else if (lvl == 1) weak++; else if (r["tested"] != null) strong++; else if (isAuto) auto++; else untested++;
        }
        var lessons = new JsonArray(Agent.Feedback.Patterns().Select(p => (JsonNode)new JsonObject { ["what"] = p.What, ["count"] = p.Count, ["teach"] = p.Teach }).ToArray());
        return new JsonObject
        {
            ["t"] = "health", ["rows"] = new JsonArray(rows.Values.OrderByDescending(r => r["bad"]!.GetValue<int>()).Select(r => (JsonNode)r).ToArray()),
            ["strong"] = strong, ["weak"] = weak, ["untested"] = untested, ["bad"] = bad, ["auto"] = auto, ["contradictions"] = contra, ["lessons"] = lessons, ["at"] = DateTime.Now.ToString("dd MMM HH:mm")
        };
    }

    /// <summary>🧪 Tests untested rules on their own (up to max per run): the AI writes one question per rule, Nova answers it with and
    /// without the rule. The result is kept on the rule as “🔎 auto ✓ / 💤 no effect / ⛔ blocked” — never “✅ strong”, which still needs your ✓.</summary>
    public static async Task<JsonObject> AutoTest(int max, Action<string> step, CancellationToken ct)
    {
        if (!Brain.Unlocked) throw new Exception("the 🧠 Nova Brain is locked — open it first");
        var b = Brain.Snapshot();
        Brain.FixIds(b);
        var todo = Brain.Rules(b).Where(x => x.Rule["tested"] == null && x.Rule["autotest"] == null && x.Rule["off"]?.GetValue<bool>() != true).Take(Math.Clamp(max, 1, 30)).ToList();
        var results = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var lines = new JsonArray();
        int i = 0;
        foreach (var (s, r) in todo)
        {
            ct.ThrowIfCancellationRequested();
            var id = r["id"]!.ToString();
            step($"🧪 {++i} of {todo.Count} · {id} {r["title"]}");
            JsonObject res;
            try
            {
                string q;
                using (Ai.For("brain"))
                {
                    Brain.Quiet.Value = true;
                    try { q = (await Ai.Chat("Write ONE short question a user would ask Nova (an assistant for emails, documents, submittal logs and writing) whose answer THIS rule should change. Reply with the question only, no quotes.", $"Section: {s["name"]}\nRule {id} — {r["title"]}: {r["text"]}", false, true, null, ct)).Trim().Trim('"', '“', '”'); }
                    finally { Brain.Quiet.Value = false; }
                }
                if (q.Length > 300) q = q[..300];
                var change = new JsonArray(new JsonObject { ["op"] = "change", ["id"] = id, ["text"] = r["text"]?.ToString() });
                var t = await Test(change, q, "", _ => { }, _ => { }, ct, quickMode: true);
                TestRun run; lock (tests) tests.TryGetValue(t["id"]?.ToString() ?? "", out run);
                var effect = run != null && (run.Kind == "writing" || run.Plus + run.Minus + run.LogChanged > 0);
                var verdict = run == null ? "error" : run.Blocking ? "blocked" : effect ? "works" : "no effect";
                res = new JsonObject { ["verdict"] = verdict, ["q"] = Agent.Clip(q, 160), ["plus"] = run?.Plus ?? 0, ["minus"] = run?.Minus ?? 0, ["when"] = DateTime.Now.ToString("dd MMM yyyy HH:mm") };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { res = new JsonObject { ["verdict"] = "error", ["why"] = Agent.Clip(e.Message, 140) }; }
            results[id] = res;
            lines.Add($"{id} · {res["verdict"]} · {(res["q"] ?? res["why"])}" + (res["plus"] is { } pl ? $" · +{pl} −{res["minus"]}" : ""));
        }
        var kept = results.Where(x => x.Value["verdict"]?.ToString() != "error").ToList();
        if (kept.Count > 0)
        {
            var nb = Brain.Snapshot();
            Brain.FixIds(nb);
            foreach (var (_, r) in Brain.Rules(nb))
                if (results.TryGetValue(r["id"]?.ToString() ?? "", out var x) && x["verdict"]?.ToString() != "error") r["autotest"] = x.DeepClone();
            Brain.Commit(nb, "🧪 auto test", $"{kept.Count} rule(s) tested automatically");
        }
        var left = Brain.Rules(Brain.Snapshot()).Count(x => x.Rule["tested"] == null && x.Rule["autotest"] == null && x.Rule["off"]?.GetValue<bool>() != true);
        return new JsonObject { ["t"] = "autotest", ["done"] = results.Count, ["lines"] = lines, ["left"] = left };
    }

    // ------------------------------------------------------------------ proposals from the connector (Claude) → the same 🧪 TEST

    public static readonly List<JsonObject> FromConnector = new();
    public static readonly List<JsonObject> FromPhone = new();

    /// <summary>An email as a text file (From · To · Cc · Date · Subject · files · text) — an example for the trainer.</summary>
    public static string MailFile(MailHit h)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Nova brain examples"); Directory.CreateDirectory(dir);
        var name = Regex.Replace(h.Subject ?? "email", @"[^\p{L}\p{N} _.-]", "_"); if (name.Length > 80) name = name[..80];
        var path = Path.Combine(dir, $"{name} ({h.Date:yyyy-MM-dd HHmm}).txt");
        File.WriteAllText(path, $"EMAIL\nFrom: {h.FromName} <{h.FromAddr}>{(h.Sent ? " (me)" : "")}\nTo: {h.To} [{h.ToAddr}]\nCc: {h.Cc} [{h.CcAddr}]\nDate: {h.Date:yyyy-MM-dd HH:mm}\nSubject: {h.Subject}\nAttachments: {h.AttNames}\nFolder: {h.Folder}\n\n{h.Body}", new UTF8Encoding(false));
        return path;
    }

    /// <summary>📱 “🎓 Teach the Brain” on the phone: the emails become examples in the Brain window on the PC.</summary>
    public static string QueueFromPhone(string text, List<MailHit> mails, bool good)
    {
        var files = new JsonArray(mails.Select(h => (JsonNode)new JsonObject { ["path"] = MailFile(h), ["name"] = (h.Subject ?? "email") + ".txt", ["good"] = good }).ToArray());
        lock (FromPhone) FromPhone.Add(new JsonObject { ["t"] = "teach", ["text"] = text, ["files"] = files });
        Screens.Brain();
        Screens.BrainNudge();
        return $"Sent to the 🧠 Brain trainer on your PC ({mails.Count} email(s)) — open the Brain there: it proposes the rule, then 🧪 TEST and ✓ Apply.";
    }

    public static string QueueFromConnector(JsonArray changes, string summary)
    {
        var b = Brain.Snapshot();
        var (after, _) = Brain.Apply(b, changes, "Claude · connector");
        var diff = Brain.Diff(b, after);
        if (diff.Count == 0) return "No rule would change.";
        var p = new JsonObject
        {
            ["t"] = "answer", ["kind"] = "propose", ["reply"] = "🔗 Proposed by Claude (connector): " + summary, ["why"] = "", ["conflict"] = "",
            ["changes"] = changes.DeepClone(), ["diff"] = diff, ["checks"] = Checks(b, diff), ["testQuestion"] = "", ["kindOf"] = KindOf(b, changes), ["steps"] = new JsonArray(), ["by"] = "Claude · connector"
        };
        lock (FromConnector) FromConnector.Add(p);
        Screens.Brain();
        Screens.BrainNudge();
        return $"Sent to the 🧠 Nova Brain window on the PC ({diff.Count} change(s)) — it is applied only after the user's 🧪 TEST passes and ✓ Apply.";
    }
}
