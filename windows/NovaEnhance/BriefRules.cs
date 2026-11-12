using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// ✦ My brief instructions (option C): you write in your own words what the morning brief must do; the AI turns it ONCE into
/// clear rules that you confirm; Nova applies them on this PC every time; the AI only judges the emails no rule decides
/// (subject + first lines). Every email says why it is where it is. Designed by A.HAMROUNI
/// </summary>
static class BriefRules
{
    public class Rule
    {
        public List<string> Project = new(), From = new(), Subject = new(), Text = new();
        public bool NoQuestion, CcOnly, Auto;
        public string Then = "";   // urgent · waiting · fyi · hide · include
        public string Why = "";
    }

    public class Set
    {
        public List<Rule> Rules = new();
        public List<string> First = new();   // projects first
        public string AiCheck = "";          // what the AI judges for the emails no rule decides
    }

    static readonly JsonSerializerOptions J = new() { IncludeFields = true };

    public static string Instructions { get => Store.Str("win_brief_instr", ""); set => Store.Set("win_brief_instr", value ?? ""); }
    public static Set Current
    {
        get { try { return JsonSerializer.Deserialize<Set>(Store.Str("win_brief_rules", "{}"), J) ?? new(); } catch { return new(); } }
        set => Store.Set("win_brief_rules", JsonSerializer.Serialize(value ?? new Set(), J));
    }
    public static bool Any => Current.Rules.Count > 0 || Current.First.Count > 0 || Current.AiCheck.Length > 0;
    /// <summary>Emails in Cc / automatic senders are read too when a rule may bring them in.</summary>
    public static bool WantsCc => Current.Rules.Any(r => r.Then == "include" || r.CcOnly);
    public static bool WantsAuto => Current.Rules.Any(r => r.Auto && r.Then != "hide");

    // ------------------------------------------------------------------ the AI turns your words into rules (once)

    public static async Task<Set> Make(string instructions, CancellationToken ct)
    {
        var projects = string.Join("\n", MailProjects.Choices().Select(c => $"- {c.Code}: {c.Label}"));
        var sys = "You turn an architect's instructions for his MORNING EMAIL BRIEF into rules. The brief lists received emails from the last days that may need his reply, in groups: " +
                  "urgent (red), waiting (needs a reply), fyi (no reply needed); 'hide' removes an email; 'include' brings in emails normally left out (where he is only in Cc, or automatic senders like Asite / noreply). " +
                  "Reply with JSON only: {\"rules\":[{\"project\":[\"SLV\"],\"from\":[\"name or address part\"],\"subject\":[\"words or codes\"],\"text\":[\"words in the email\"],\"no_question\":false,\"cc_only\":false,\"auto\":false,\"then\":\"urgent|waiting|fyi|hide|include\",\"why\":\"3-8 words\"}], " +
                  "\"first\":[\"project codes to show first\"], \"ai_check\":\"what still needs judgement email by email (empty if nothing)\"}. " +
                  "Every condition inside one rule must ALL be true; leave a condition empty when not needed. Use the project codes below. Keep the rules short and literal; put anything that needs understanding (not words) into ai_check.\nProjects:\n" + projects;
        var j = Ai.ParseJson(await SearchGate.Limit(t => Ai.Chat(sys, "Instructions:\n" + instructions, true, true, SmartSearch.Cloud(), t), 60, ct));
        var s = new Set { AiCheck = j["ai_check"]?.ToString() ?? "" };
        foreach (var x in j["first"]?.AsArray() ?? new JsonArray()) if (x?.ToString() is { Length: > 0 } f) s.First.Add(f.ToUpperInvariant());
        foreach (var r in j["rules"]?.AsArray() ?? new JsonArray())
        {
            if (r == null) continue;
            List<string> L(string k) => (r[k]?.AsArray() ?? new JsonArray()).Select(v => v?.ToString() ?? "").Where(v => v.Trim().Length > 0).ToList();
            var then = (r["then"]?.ToString() ?? "").ToLowerInvariant();
            if (then is not ("urgent" or "waiting" or "fyi" or "hide" or "include")) continue;
            s.Rules.Add(new Rule
            {
                Project = L("project").Select(p => p.ToUpperInvariant()).ToList(), From = L("from"), Subject = L("subject"), Text = L("text"),
                NoQuestion = r["no_question"]?.GetValue<bool>() == true, CcOnly = r["cc_only"]?.GetValue<bool>() == true, Auto = r["auto"]?.GetValue<bool>() == true,
                Then = then, Why = r["why"]?.ToString() ?? ""
            });
        }
        return s;
    }

    /// <summary>The rules in plain words (to check them before using them).</summary>
    public static string Describe(Set s)
    {
        var sb = new StringBuilder();
        if (s.First.Count > 0) sb.AppendLine("⬆ Show first: " + string.Join(", ", s.First));
        int i = 1;
        foreach (var r in s.Rules)
        {
            var c = new List<string>();
            if (r.Project.Count > 0) c.Add("project " + string.Join(" or ", r.Project));
            if (r.From.Count > 0) c.Add("from " + string.Join(" or ", r.From));
            if (r.Subject.Count > 0) c.Add("subject has " + string.Join(" or ", r.Subject.Select(x => $"“{x}”")));
            if (r.Text.Count > 0) c.Add("text has " + string.Join(" or ", r.Text.Select(x => $"“{x}”")));
            if (r.NoQuestion) c.Add("no question asked");
            if (r.CcOnly) c.Add("I'm only in Cc");
            if (r.Auto) c.Add("automatic sender (Asite, noreply…)");
            var then = r.Then switch { "urgent" => "🔴 urgent", "waiting" => "🟠 waiting for my reply", "fyi" => "⚪ FYI", "hide" => "✗ hide", _ => "➕ include in the brief" };
            sb.AppendLine($"{i++}. If {(c.Count > 0 ? string.Join(" and ", c) : "any email")} → {then}{(r.Why.Length > 0 ? "   (" + r.Why + ")" : "")}");
        }
        if (s.AiCheck.Length > 0) sb.AppendLine("✦ The AI judges the emails no rule decides: " + s.AiCheck);
        return sb.Length == 0 ? "(no rules)" : sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ applying them (on this PC)

    static bool Has(string text, List<string> words) => words.Count == 0 || words.Any(w => (text ?? "").Contains(w, StringComparison.OrdinalIgnoreCase));

    public static Rule Match(Set s, MailHit h, bool inTo)
    {
        foreach (var r in s.Rules)
        {
            if (r.Project.Count > 0 && !r.Project.Contains(MailProjects.Of(h.Id), StringComparer.OrdinalIgnoreCase) && !r.Project.Any(p => (h.Subject ?? "").Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (!Has((h.FromName ?? "") + " " + (h.FromAddr ?? ""), r.From)) continue;
            if (!Has(h.Subject, r.Subject)) continue;
            if (!Has(h.NewPart, r.Text)) continue;
            if (r.NoQuestion && h.NewPart.Contains('?')) continue;
            if (r.CcOnly && inTo) continue;
            if (r.Auto && !BriefLogic.IsAuto(h)) continue;
            return r;
        }
        return null;
    }

    static string Status(string then) => then switch { "urgent" => "needs", "waiting" => "week", "fyi" => "fyi", _ => "" };

    /// <summary>Applies your rules, then lets the AI judge the rest (only when your instructions ask for judgement).</summary>
    public static async Task Apply(List<MailHit> list, Func<MailHit, bool> inTo, CancellationToken ct)
    {
        var s = Current;
        if (!Any) return;
        var undecided = new List<MailHit>();
        foreach (var h in list.ToList())
        {
            var r = Match(s, h, inTo(h));
            if (r == null) { undecided.Add(h); continue; }
            if (r.Then == "hide") { list.Remove(h); continue; }
            if (Status(r.Then) is { Length: > 0 } st) h.Status = st;
            h.Why = "your rule: " + (r.Why.Length > 0 ? r.Why : r.Then);
        }
        // ✦ The AI only for what no rule decided (subject + first lines; 🔒 private ones never sent).
        if (s.AiCheck.Length > 0 && undecided.Count > 0 && SmartSearch.Cloud() != null)
            try
            {
                var send = undecided.Where(h => !Projects.Private.IsMatch((h.Subject ?? "") + " " + h.FirstLines(200))).Take(60).ToList();
                var sb = new StringBuilder($"My instructions: {Instructions}\nWhat to judge: {s.AiCheck}\nEmails:\n");
                for (int i = 0; i < send.Count; i++) sb.Append($"[{i + 1}] {send[i].Date:dd MMM} · {send[i].FromName} · {send[i].Subject} · “{send[i].FirstLines(240)}”\n");
                var sys = "You sort emails for an architect's morning brief following HIS instructions. Reply with JSON only: {\"e\":[{\"i\":1,\"group\":\"urgent|waiting|fyi|hide\",\"why\":\"3-8 words\"}]}. " +
                          "Give a group only when the instructions say something about that email; otherwise leave it out.";
                var j = Ai.ParseJson(await SearchGate.Limit(t => Ai.Chat(sys, sb.ToString(), true, true, SmartSearch.Cloud(), t), 45, ct));
                foreach (var x in j["e"]?.AsArray() ?? new JsonArray())
                {
                    if (!int.TryParse(x?["i"]?.ToString(), out var i) || i < 1 || i > send.Count) continue;
                    var h = send[i - 1];
                    var g = x?["group"]?.ToString() ?? "";
                    if (g == "hide") { list.Remove(h); continue; }
                    if (Status(g) is { Length: > 0 } st) { h.Status = st; h.Why = "AI, from your instructions: " + (x?["why"]?.ToString() ?? g); }
                }
            }
            catch (Exception e) { Log.Warn("brief instructions", "AI check: " + e.Message); }
        // ⬆ Your projects first (inside each group).
        if (s.First.Count > 0)
        {
            int Rank(MailHit h) { var p = MailProjects.Of(h.Id); var k = s.First.FindIndex(x => x.Equals(p, StringComparison.OrdinalIgnoreCase) || (h.Subject ?? "").Contains(x, StringComparison.OrdinalIgnoreCase)); return k < 0 ? 99 : k; }
            var sorted = list.OrderBy(h => h.Status switch { "needs" => 0, "week" => 1, _ => 2 }).ThenBy(Rank).ThenByDescending(h => h.Date).ToList();
            list.Clear(); list.AddRange(sorted);
        }
    }

    // ------------------------------------------------------------------ the window to write them

    public static void Edit(IWin32Window owner, Action changed = null)
    {
        using var f = new NovaForm { Text = "✦ My brief instructions", Width = 900, Height = 720, StartPosition = FormStartPosition.CenterParent, MinimumSize = new Size(640, 520) };
        var st = new Stack();
        f.Controls.Add(st);
        st.Add(Th.Wrap("Write in your own words what the morning brief must do. Nova's AI turns it into clear rules (you check them), Nova applies them on this PC every time, and the AI only judges the emails no rule decides.", 10, Th.Text), true);
        var box = st.Add(Th.Prose(Th.Box(true, 7)), true);
        box.Text = Instructions;
        box.PlaceholderText = "e.g. Put SLV and Nouran first. Asite notifications only when a task is assigned to me. “Kindly find attached” without a question = FYI. Any WIR or MIR waiting for my approval = urgent. Ignore newsletters and HR. Show emails from Mark Ltaif even when I'm only in Cc.";
        var row = st.Add(Th.Row(false));
        var status = st.Add(Th.Wrap("", 9.5f, Th.Muted), true);
        var rulesBox = st.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 240, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10), BorderStyle = BorderStyle.None }, true);
        var made = Current;
        rulesBox.Text = Any ? "Rules in use now:\r\n" + Describe(made).Replace("\n", "\r\n") : "No rules yet.";
        var use = Th.B("✓ Use these rules", () => { Instructions = box.Text.Trim(); Current = made; changed?.Invoke(); f.Close(); }, true);
        use.Enabled = false;
        row.Controls.Add(Th.B("✦ Make the rules", async () =>
        {
            if (box.Text.Trim().Length < 5) { status.Text = "Write your instructions first."; return; }
            status.Text = "✦ The AI is turning your instructions into rules…";
            try
            {
                made = await Make(box.Text.Trim(), CancellationToken.None);
                rulesBox.Text = "Check these rules, then press ✓ Use these rules:\r\n\r\n" + Describe(made).Replace("\n", "\r\n");
                status.Text = $"✓ {made.Rules.Count} rule(s)" + (made.First.Count > 0 ? " · projects first" : "") + (made.AiCheck.Length > 0 ? " · AI for the rest" : "");
                use.Enabled = true;
            }
            catch (Exception e) { status.Text = "⚠ " + e.Message; }
        }, true));
        row.Controls.Add(use);
        row.Controls.Add(Th.B("✗ Remove all rules", () => { Instructions = ""; Current = new Set(); changed?.Invoke(); f.Close(); }));
        f.ShowDialog(owner);
    }
}
