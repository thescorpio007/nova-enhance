using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>
/// ✦ My brief instructions (option C) on the 📏 Nova Rules engine: your words → rules you check (and can edit), applied on this PC;
/// the AI only judges the emails no rule decides. Designed by A.HAMROUNI
/// </summary>
static class BriefRules
{
    public static RuleBook Current => RuleBooks.Load("brief");
    public static string Instructions => Current.Instructions;
    public static bool Any => Current.Rules.Any(r => r.On) || Current.First.Count > 0 || Current.AiCheck.Length > 0;
    public static bool WantsCc => Current.Rules.Any(r => r.On && (r.Then == "include" || r.In == "cc"));

    /// <summary>The rules made in build 65 (kept in the settings) move into the rules file once.</summary>
    internal static RuleBook FromOldSettings()
    {
        var b = new RuleBook { Instructions = Store.Str("win_brief_instr", "") };
        try
        {
            var o = JsonNode.Parse(Store.Str("win_brief_rules", "{}"));
            b.AiCheck = o?["AiCheck"]?.ToString() ?? "";
            foreach (var x in o?["First"]?.AsArray() ?? new JsonArray()) b.First.Add(x?.ToString() ?? "");
            foreach (var r in o?["Rules"]?.AsArray() ?? new JsonArray())
            {
                if (r == null) continue;
                List<string> L(string k) => (r[k]?.AsArray() ?? new JsonArray()).Select(v => v?.ToString() ?? "").Where(v => v.Length > 0).ToList();
                b.Rules.Add(new NRule
                {
                    Project = L("Project"), From = L("From"), Subject = L("Subject"), Text = L("Text"), NoQuestion = r["NoQuestion"]?.GetValue<bool>() == true,
                    In = r["CcOnly"]?.GetValue<bool>() == true ? "cc" : "any", Auto = r["Auto"]?.GetValue<bool>() == true, Then = r["Then"]?.ToString() ?? "", Why = r["Why"]?.ToString() ?? "", Source = "words"
                });
            }
        }
        catch { }
        return b;
    }

    public static NRule Match(RuleBook b, MailHit h, bool inTo)
    {
        var p = MailProjects.Of(h.Id);
        return b.Rules.FirstOrDefault(r => r.On && RuleBooks.Matches(r, h, inTo, p));
    }

    static string Status(string then) => then switch { "urgent" => "needs", "waiting" => "week", "fyi" => "fyi", _ => "" };

    /// <summary>Applies the rules, then lets the AI judge the rest (only when your instructions ask for judgement).</summary>
    public static async Task Apply(List<MailHit> list, Func<MailHit, bool> inTo, CancellationToken ct, RuleBook b = null, bool ai = true)
    {
        b ??= Current;
        var undecided = new List<MailHit>();
        foreach (var h in list.ToList())
        {
            var r = Match(b, h, inTo(h));
            if (r == null) { undecided.Add(h); continue; }
            if (r.Then == "hide") { list.Remove(h); continue; }
            if (Status(r.Then) is { Length: > 0 } st) h.Status = st;
            h.Why = $"rule {b.Rules.IndexOf(r) + 1}: " + (r.Why.Length > 0 ? r.Why : r.Then);
        }
        if (ai && b.AiCheck.Length > 0 && undecided.Count > 0 && SmartSearch.Cloud() != null)
            try
            {
                var send = undecided.Where(h => !Projects.Private.IsMatch((h.Subject ?? "") + " " + h.FirstLines(200))).Take(40).ToList();
                var sb = new StringBuilder($"My instructions: {b.Instructions}\nWhat to judge: {b.AiCheck}\nEmails:\n");
                for (int i = 0; i < send.Count; i++) sb.Append($"[{i + 1}] {send[i].Date:dd MMM} · {send[i].FromName} · {send[i].Subject} · “{send[i].FirstLines(240)}”\n");
                var sys = "You sort emails for an architect's morning brief following HIS instructions. Reply with JSON only: {\"e\":[{\"i\":1,\"group\":\"urgent|waiting|fyi|hide\",\"why\":\"3-8 words\"}]}. " +
                          "Give a group only when the instructions say something about that email; otherwise leave it out.";
                var j = Ai.ParseJson(await SearchGate.Limit(t => Ai.Chat(sys, sb.ToString(), true, true, SmartSearch.Cloud(), t), 90, ct));
                foreach (var x in j["e"]?.AsArray() ?? new JsonArray())
                {
                    if (!int.TryParse(x?["i"]?.ToString(), out var i) || i < 1 || i > send.Count) continue;
                    var h = send[i - 1];
                    var g = x?["group"]?.ToString() ?? "";
                    if (g == "hide") { list.Remove(h); continue; }
                    if (Status(g) is { Length: > 0 } st) { h.Status = st; h.Why = "AI, from your instructions: " + (x?["why"]?.ToString() ?? g); }
                }
            }
            catch (Exception e) { Log.Warn("brief rules", "AI check: " + e.Message); }
        if (b.First.Count > 0)
        {
            int Rank(MailHit h) { var p = MailProjects.Of(h.Id); var k = b.First.FindIndex(x => x.Equals(p, StringComparison.OrdinalIgnoreCase) || (h.Subject ?? "").Contains(x, StringComparison.OrdinalIgnoreCase)); return k < 0 ? 99 : k; }
            var sorted = list.OrderBy(h => h.Status switch { "needs" => 0, "week" => 1, _ => 2 }).ThenBy(Rank).ThenByDescending(h => h.Date).ToList();
            list.Clear(); list.AddRange(sorted);
        }
    }

    // ------------------------------------------------------------------ your words → rules (the AI, once)

    /// <summary>The AI turns your words into rules for a feature (brief or projects).</summary>
    public static async Task<RuleBook> Make(string feature, string words, CancellationToken ct)
    {
        var projects = string.Join("\n", MailProjects.Choices().Select(c => $"- {c.Code}: {c.Label}"));
        var actions = feature == "brief"
            ? "\"then\": urgent | waiting | fyi | hide | include  (include = bring in emails normally left out: only in Cc, automatic senders like Asite/noreply)"
            : "\"then\": the project code the email belongs to (from the list), or unsorted";
        var what = feature == "brief" ? "his MORNING EMAIL BRIEF (received emails that may need his reply)" : "SORTING his emails into projects";
        var sys = $"You turn an architect's instructions for {what} into rules. Reply with JSON only: " +
                  "{\"rules\":[{\"from\":[],\"subject\":[],\"text\":[],\"project\":[],\"in\":\"any|to|cc\",\"no_question\":false,\"auto\":false,\"except\":[],\"then\":\"…\",\"why\":\"3-8 words\"}],\"first\":[],\"ai_check\":\"\"}. " +
                  actions + ". " +
                  "IMPORTANT: inside one list the words are alternatives (ANY of them); different fields must ALL be true. NEVER put two different kinds of words in one list " +
                  "(e.g. document types and urgency words go in two fields: subject = the document codes, text = the urgency words). " +
                  "subject also covers attachment names. in: to = I'm in To, cc = I'm only in Cc. except = words in the text that cancel the rule. " +
                  "The first rule that matches decides, so put the most specific rules first. first = project codes to show first (brief only). " +
                  "ai_check = anything that needs understanding rather than words (empty if nothing). Use these project codes:\n" + projects;
        var j = Ai.ParseJson(await SearchGate.Limit(t => Ai.Chat(sys, "Instructions:\n" + words, true, true, SmartSearch.Cloud(), t), 60, ct));
        var b = new RuleBook { AiCheck = j["ai_check"]?.ToString() ?? "", Instructions = words };
        foreach (var x in j["first"]?.AsArray() ?? new JsonArray()) if (x?.ToString() is { Length: > 0 } f) b.First.Add(f.ToUpperInvariant());
        foreach (var r in j["rules"]?.AsArray() ?? new JsonArray())
        {
            if (r == null) continue;
            List<string> L(string k) => (r[k]?.AsArray() ?? new JsonArray()).Select(v => v?.ToString() ?? "").Where(v => v.Trim().Length > 0).ToList();
            var then = r["then"]?.ToString()?.Trim() ?? "";
            if (feature == "brief") { then = then.ToLowerInvariant(); if (then is not ("urgent" or "waiting" or "fyi" or "hide" or "include")) continue; }
            else if (then.Length == 0) continue;
            var inn = (r["in"]?.ToString() ?? "any").ToLowerInvariant();
            b.Rules.Add(new NRule
            {
                From = L("from"), Subject = L("subject"), Text = L("text"), Project = L("project").Select(p => p.ToUpperInvariant()).ToList(), Except = L("except"),
                In = inn is "to" or "cc" ? inn : "any", NoQuestion = r["no_question"]?.GetValue<bool>() == true, Auto = r["auto"]?.GetValue<bool>() == true,
                Then = feature == "brief" ? then : then.ToUpperInvariant() == "UNSORTED" ? "unsorted" : then.ToUpperInvariant(), Why = r["why"]?.ToString() ?? "", Source = "words"
            });
        }
        return b;
    }

    /// <summary>✦ My instructions: your words → the rules table (📏 Rules), where you check, edit and use them.</summary>
    public static void Edit(IWin32Window owner, Action changed = null) => RulesForm.Open(owner, "brief", changed);
}
