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
    static readonly Agent.Session Data = new();

    static readonly Regex Vague = new(@"\b(relevant|some|usually|normally|maybe|appropriate|etc|various|certain|properly|as needed|if needed|important ones?)\b", RegexOptions.IgnoreCase);
    static readonly Regex Words = new(@"[\p{L}\p{N}][\p{L}\p{N}.@\-]{3,}");

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
TRAINER RULES
- Check before you write: if the rule names a company / person / domain / document format, look at real emails first (emails or followup) and use the exact domain and format you saw. Never guess a domain.
- Ask when unclear: scope (which project, which types), what counts (reply with comments? a code? a forward?), and exceptions. One question at a time, with options. Do not ask what the data or the brain already answers.
- Prefer changing an existing rule over adding a near-duplicate. Never propose two rules with the same meaning; if one exists, propose a change of it (its id).
- Every rule names its scope (project code, document types, company domain, section) and has no vague words (relevant, some, usually, appropriate, as needed…).
- Settings are written [name = value] (e.g. [overdue.WIR = 2], [mza.match = …], [log.types = …]) — keep that syntax exactly.
- If the instruction contradicts a rule, say which and propose replacing or changing it — never leave two rules that disagree.
- Never invent facts, emails or numbers: only what the tools returned. At most 7 steps, then propose / ask / answer.
- Write the reply in the user's language (English unless they write otherwise); short and clear.
THE BRAIN NOW:{BrainText(b)}";

    /// <summary>One message to the trainer: steps (tools), then a proposal, a question with options, or an answer.</summary>
    public static async Task<JsonObject> Ask(string text, Action<string> step, Action<string> detail, CancellationToken ct)
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
        t.Append("USER: ").Append(text).Append('\n');
        Chat.Add(new JsonObject { ["role"] = "user", ["text"] = text });
        var steps = new JsonArray();
        using var aiFor = Ai.For("brain");
        Brain.Quiet.Value = true;   // the trainer gets the whole brain itself
        try
        {
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
            ["kindOf"] = KindOf(b, changes), ["steps"] = steps, ["by"] = Ai.LastBy
        };
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
            if (Regex.IsMatch(text, @"\[[^\]=]*\]") || Regex.Matches(text, @"\[").Count != Regex.Matches(text, @"\]").Count) syntax.Add($"{id}: a [setting] must be written [name = value]");
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
        if (tags.Contains("submittal")) return "log";
        return "search";
    }

    // ------------------------------------------------------------------ 🧪 TEST

    public class TestRun
    {
        public string Id = "", Changes = "", Question = "", Kind = "";
        public int Plus, Minus, LogChanged, Examples;
        public bool Blocking;
        public string Verdict = "";   // passed · weak · blocked
        public int Yes, No, Seconds;
    }

    static readonly Dictionary<string, TestRun> tests = new();
    static readonly Dictionary<string, (string, Agent.Result)> beforeCache = new();

    static string Key(JsonArray changes) => Regex.Replace(changes?.ToJsonString() ?? "", @"\s+", "");

    /// <summary>Runs the test question with the current brain and with the proposal (nothing saved): ➕ / ➖, examples, checks.</summary>
    public static async Task<JsonObject> Test(JsonArray changes, string question, string kind, Action<string> step, Action<string> detail, CancellationToken ct)
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
        Task<JsonObject> logTask = kind == "log" ? Task.Run(() => LogDiff(now, after, 30), ct) : Task.FromResult<JsonObject>(null);

        if (kind == "writing")
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
            var hostB = new Agent.Host { Progress = x => detail("now · " + x), Detail = _ => { }, Confirm = null };
            var hostA = new Agent.Host { Progress = x => detail("new rule · " + x), Detail = _ => { }, Confirm = null };
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

    static readonly Regex TermRx = new(@"\b[\w.-]+\.(?:com|ae|net|org|co|io)\b|\b[A-Z]{2,5}-\d{2,6}\b|“([^”]{3,40})”|""([^""]{3,40})""", RegexOptions.IgnoreCase);

    public static async Task<JsonObject> Health(Action<string> step, CancellationToken ct)
    {
        var b = Brain.Snapshot();
        var list = Brain.Rules(b).ToList();
        var rows = new Dictionary<string, JsonObject>();
        JsonArray Notes(string id) => (rows[id]["notes"] as JsonArray)!;
        foreach (var (s, r) in list)
        {
            var id = r["id"]?.ToString() ?? "";
            rows[id] = new JsonObject { ["id"] = id, ["section"] = s["name"]?.ToString(), ["title"] = r["title"]?.ToString(), ["text"] = r["text"]?.ToString(), ["off"] = r["off"]?.GetValue<bool>() == true, ["tested"] = r["tested"]?.DeepClone(), ["notes"] = new JsonArray(), ["bad"] = 0 };
        }
        step("Looking for rules that say the same thing");
        for (int i = 0; i < list.Count; i++)
            for (int k = i + 1; k < list.Count; k++)
            {
                var sim = Jaccard(Bag(list[i].Rule["text"]?.ToString()), Bag(list[k].Rule["text"]?.ToString()));
                if (sim < 0.5) continue;
                var a = list[i].Rule["id"]!.ToString(); var c = list[k].Rule["id"]!.ToString();
                Notes(a).Add($"⛔ {(int)(sim * 100)}% the same as {c}"); Notes(c).Add($"⛔ {(int)(sim * 100)}% the same as {a}");
                rows[a]["bad"] = 2; rows[c]["bad"] = 2;
            }
        step("Checking wording and settings");
        foreach (var (_, r) in list)
        {
            var id = r["id"]!.ToString(); var text = r["text"]?.ToString() ?? "";
            foreach (var w in Vague.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct()) { Notes(id).Add($"⚠ “{w}” is vague"); rows[id]["bad"] = Math.Max(rows[id]["bad"]!.GetValue<int>(), 1); }
            if (Regex.IsMatch(text, @"\[[^\]=]*\]")) { Notes(id).Add("⛔ a [setting] without “= value”"); rows[id]["bad"] = 2; }
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
            var terms = TermRx.Matches(text).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Value).Distinct().Take(4).ToList();
            if (terms.Count == 0) continue;
            var n = await Task.Run(() => MailIndex.Search(new MailQuery { AnyWords = terms, After = DateTime.Today.AddDays(-30), Limit = 1, Folder = "any" }).Count, ct);
            if (n == 0) { Notes(id).Add($"💤 nothing with {string.Join(", ", terms.Select(x => "“" + x + "”"))} in the last 30 days"); rows[id]["bad"] = Math.Max(rows[id]["bad"]!.GetValue<int>(), 1); }
        }
        step("Asking the AI to read the whole brain for contradictions");
        var contra = new JsonArray();
        try
        {
            var sys = "You review the user's rule book (Nova Brain) for CONTRADICTIONS between rules (two rules that cannot both be followed) and rules too vague to apply. " +
                      "Reply with JSON only: {\"contradictions\":[{\"ids\":[\"A-01\",\"B-02\"],\"why\":\"…\",\"fix\":\"one sentence: what to change\"}],\"vague\":[{\"id\":\"…\",\"why\":\"…\"}]}. Only real problems.";
            using var aiFor = Ai.For("brain");
            Brain.Quiet.Value = true;
            JsonObject j;
            try { j = Ai.ParseJson(await Ai.Chat(sys, "THE RULES:" + BrainText(b, false), false, true, null, ct)) ?? new JsonObject(); }
            finally { Brain.Quiet.Value = false; }
            foreach (var c in (j["contradictions"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                var ids = (c["ids"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(rows.ContainsKey).ToList() ?? new();
                foreach (var id in ids) { Notes(id).Add($"⛔ contradicts {string.Join(", ", ids.Where(x => x != id))}: {c["why"]}"); rows[id]["bad"] = 2; }
                if (ids.Count > 0) contra.Add(c.DeepClone());
            }
            foreach (var v in (j["vague"] as JsonArray ?? new()).OfType<JsonObject>())
                if (v["id"]?.ToString() is { } id && rows.ContainsKey(id)) { Notes(id).Add("⚠ " + v["why"]); rows[id]["bad"] = Math.Max(rows[id]["bad"]!.GetValue<int>(), 1); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { contra.Add(new JsonObject { ["ids"] = new JsonArray(), ["why"] = "(the AI check did not answer: " + e.Message + ")", ["fix"] = "" }); }
        int strong = 0, weak = 0, untested = 0, bad = 0;
        foreach (var r in rows.Values)
        {
            var lvl = r["bad"]!.GetValue<int>();
            var badge = lvl == 2 ? "⛔ fix" : lvl == 1 ? "⚠ weak" : r["tested"] != null ? "✅ strong" : "○ untested";
            r["badge"] = badge;
            if (lvl == 2) bad++; else if (lvl == 1) weak++; else if (r["tested"] != null) strong++; else untested++;
        }
        var lessons = new JsonArray(Agent.Feedback.Patterns().Select(p => (JsonNode)new JsonObject { ["what"] = p.What, ["count"] = p.Count, ["teach"] = p.Teach }).ToArray());
        return new JsonObject
        {
            ["t"] = "health", ["rows"] = new JsonArray(rows.Values.OrderByDescending(r => r["bad"]!.GetValue<int>()).Select(r => (JsonNode)r).ToArray()),
            ["strong"] = strong, ["weak"] = weak, ["untested"] = untested, ["bad"] = bad, ["contradictions"] = contra, ["lessons"] = lessons, ["at"] = DateTime.Now.ToString("dd MMM HH:mm")
        };
    }

    // ------------------------------------------------------------------ proposals from the connector (Claude) → the same 🧪 TEST

    public static readonly List<JsonObject> FromConnector = new();

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
        return $"Sent to the 🧠 Nova Brain window on the PC ({diff.Count} change(s)) — it is applied only after the user's 🧪 TEST passes and ✓ Apply.";
    }
}
