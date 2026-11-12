using System.Text;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>✦ Check rules and ▶ Preview: tried on your real emails of the last 30 days, on this PC.</summary>
static class RuleCheck
{
    /// <summary>The emails the rules are tried on.</summary>
    public static List<MailHit> Sample(string feature)
    {
        var q = feature == "brief"
            ? new MailQuery { Folder = "received", After = DateTime.Now.AddDays(-30), MeIn = "any" }
            : new MailQuery { After = DateTime.Now.AddDays(-60) };
        var ids = MailIndex.SearchIds(q, 2000).Select(x => x.Id);
        return MailIndex.ByIds(ids, 2000, 4000);
    }

    public class Report
    {
        public int[] Raw = Array.Empty<int>(), Won = Array.Empty<int>();
        public List<MailHit>[] Caught = Array.Empty<List<MailHit>>();
        public List<string> Warnings = new(), Good = new();
    }

    public static Report Run(string feature, RuleBook b, List<MailHit> sample)
    {
        int n = b.Rules.Count;
        var rep = new Report { Raw = new int[n], Won = new int[n], Caught = Enumerable.Range(0, n).Select(_ => new List<MailHit>()).ToArray() };
        var first = new Dictionary<MailHit, int>();
        var overlap = new Dictionary<(int, int), int>();
        foreach (var h in sample)
        {
            var inTo = BriefLogic.InTo(h); var p = MailProjects.Of(h.Id);
            var hits = new List<int>();
            for (int i = 0; i < n; i++) if (b.Rules[i].On && RuleBooks.Matches(b.Rules[i], h, inTo, p)) { hits.Add(i); rep.Raw[i]++; rep.Caught[i].Add(h); }
            if (hits.Count > 0) { rep.Won[hits[0]]++; first[h] = hits[0]; }
            for (int a = 0; a < hits.Count; a++) for (int c = a + 1; c < hits.Count; c++) overlap[(hits[a], hits[c])] = overlap.TryGetValue((hits[a], hits[c]), out var v) ? v + 1 : 1;
        }
        string T(int i) => RuleBooks.ThenText(feature, b.Rules[i].Then);
        for (int i = 0; i < n; i++)
        {
            if (!b.Rules[i].On) continue;
            if (rep.Raw[i] == 0) rep.Warnings.Add($"⚠ Rule {i + 1} catches nothing in the last 30 days — check its words (or move it up).");
            else if (rep.Won[i] == 0)
            {
                var by = rep.Caught[i].Select(h => first[h]).Distinct().Select(x => (x + 1).ToString());
                rep.Warnings.Add($"⚠ Rule {i + 1} never applies: rule(s) {string.Join(", ", by)} catch all its {rep.Raw[i]} emails first → move it up or narrow the earlier rule.");
            }
            if (sample.Count >= 20 && rep.Raw[i] > sample.Count * 0.4)
                rep.Warnings.Add($"⚠ Rule {i + 1} is very broad: it catches {100 * rep.Raw[i] / sample.Count} % of your emails ({rep.Raw[i]} of {sample.Count}) → add a second condition.");
        }
        foreach (var ((a, c), cnt) in overlap)
            if (cnt >= 3 && b.Rules[a].Then != b.Rules[c].Then && b.Rules[c].Then != "include")
                rep.Warnings.Add($"↔ Rules {a + 1} and {c + 1} both match {cnt} emails: rule {a + 1} wins → {T(a)} (rule {c + 1} would say {T(c)}). If that's wrong, move rule {c + 1} above rule {a + 1}.");
        if (!rep.Warnings.Any(w => w.StartsWith("↔"))) rep.Good.Add("✓ No contradiction between rules");
        if (!rep.Warnings.Any(w => w.Contains("very broad"))) rep.Good.Add("✓ No rule is too broad");
        rep.Good.Add($"Tried on {sample.Count:N0} of your emails of the last {(feature == "brief" ? 30 : 60)} days");
        return rep;
    }

    /// <summary>✦ The AI reads your words and the rules together and says what does not fit.</summary>
    public static async Task<List<string>> AiReview(string feature, RuleBook b, CancellationToken ct)
    {
        if (b.Instructions.Trim().Length == 0 || SmartSearch.Cloud() == null) return new();
        var sys = "You check rules an architect wrote (or that were made from his words) for " + (feature == "brief" ? "his morning email brief" : "sorting his emails into projects") +
                  ". Rules: all conditions of a rule must be true; each list = any of its words; the first matching rule decides. " +
                  "Reply with JSON only: {\"ok\":true|false,\"notes\":[\"short note: what does not fit his words, and the fix\"]}. Max 5 notes. If everything fits, ok=true and notes=[].";
        var j = Ai.ParseJson(await SearchGate.Limit(t => Ai.Chat(sys, $"His words:\n{b.Instructions}\n\nThe rules:\n{RuleBooks.Describe(feature, b)}", true, true, SmartSearch.Cloud(), t), 45, ct));
        var notes = (j["notes"]?.AsArray() ?? new JsonArray()).Select(x => "✦ " + x?.ToString()).Where(x => x.Length > 3).ToList();
        if (notes.Count == 0 && j["ok"]?.GetValue<bool>() == true) notes.Add("✦ Gemini: the rules match your words.");
        return notes;
    }

    /// <summary>▶ What would change (brief: the groups; projects: which project the emails go to).</summary>
    public static async Task<string> Preview(string feature, RuleBook now, RuleBook next, List<MailHit> sample, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (feature == "brief")
        {
            var (a, _) = await BriefLogic.Build(ct, now, false);
            var (b, _) = await BriefLogic.Build(ct, next, false);
            string Count(List<MailHit> l, string st) => l.Count(h => h.Status == st && h.MaybeAnswered.Length == 0).ToString();
            sb.AppendLine($"With these rules:  🔴 {Count(b, "needs")} urgent (now {Count(a, "needs")})  ·  🟠 {Count(b, "week")} waiting (now {Count(a, "week")})  ·  ⚪ {Count(b, "fyi")} FYI (now {Count(a, "fyi")})");
            sb.AppendLine("(the AI part is not included in the preview)\r\n");
            var before = a.GroupBy(h => h.Entry ?? "").ToDictionary(g => g.Key, g => g.First());
            var after = b.GroupBy(h => h.Entry ?? "").ToDictionary(g => g.Key, g => g.First());
            string S(string st) => st switch { "needs" => "urgent", "week" => "waiting", "fyi" => "FYI", _ => st };
            int shown = 0;
            foreach (var h in b) if (!before.ContainsKey(h.Entry ?? "") && shown++ < 25) sb.AppendLine($"➕ added   {h.Subject}  → {S(h.Status)}  ({h.Why})");
            foreach (var h in a) if (!after.ContainsKey(h.Entry ?? "") && shown++ < 50) sb.AppendLine($"✗ hidden  {h.Subject}");
            foreach (var h in b) if (before.TryGetValue(h.Entry ?? "", out var o) && o.Status != h.Status && shown++ < 75) sb.AppendLine($"↕ {S(o.Status)} → {S(h.Status)}   {h.Subject}  ({h.Why})");
            if (shown == 0) sb.AppendLine("Nothing would change.");
        }
        else
        {
            int moved = 0;
            var lines = new List<string>();
            foreach (var h in sample)
            {
                var r = next.Rules.FirstOrDefault(x => x.On && RuleBooks.Matches(x, h, true, ""));
                if (r == null) continue;
                var to = r.Then == "unsorted" ? "" : r.Then;
                var from = MailProjects.Of(h.Id);
                if (!to.Equals(from, StringComparison.OrdinalIgnoreCase)) { moved++; if (lines.Count < 60) lines.Add($"{(from.Length > 0 ? from : "unsorted")} → {(to.Length > 0 ? to : "unsorted")}   {h.Subject}"); }
            }
            sb.AppendLine($"{moved} of {sample.Count:N0} recent emails would change project:\r\n");
            foreach (var l in lines) sb.AppendLine(l);
        }
        return sb.ToString();
    }
}

/// <summary>📏 The rules window: one tab per feature, the rules as a table you can add to, edit, order, check and preview.</summary>
class RulesForm : NovaForm
{
    static RulesForm open;
    string feature;
    RuleBook book;           // the working copy (saved by ✓ Use)
    List<MailHit> sample = new();
    RuleCheck.Report report;
    bool checkedSinceChange;
    Action changed;

    readonly FlowLayoutPanel tabs;
    readonly Label title, extra;
    readonly TextBox words, output;
    readonly ListView lv;
    readonly Button useBtn;
    readonly WorkDots dots;
    readonly Label status;

    public static void Open(IWin32Window owner, string feature, Action changed = null)
    {
        if (open == null || open.IsDisposed) open = new RulesForm();
        open.changed = changed;
        open.ShowFeature(feature);
        Screens.Show(open);
    }

    RulesForm()
    {
        Text = "📏 Rules — Nova Enhance";
        Size = new Size(1320, 900);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Th.Bg;
        var st = new Stack();
        Controls.Add(st);
        title = st.Add(Th.L("📏 Rules", 15, Th.Text, true));
        st.Add(Th.Wrap("All conditions of a rule must be true (AND) · each box = any of its words (OR) · the first rule that matches decides · ✓ Use is enabled after ✦ Check rules.", 9.5f, Th.Muted), true);
        tabs = st.Add(new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent });
        st.Add(Th.Section("✦ Your words"));
        words = st.Add(Th.Prose(Th.Box(true, 4)), true);
        var wr = st.Add(Th.Row(false));
        wr.Controls.Add(Th.B("✦ Make the rules from my words", () => FromWords(false), true));
        wr.Controls.Add(Th.B("✦ Add from words…", () => FromWords(true)));
        st.Add(Th.Section("Rules"));
        var br = st.Add(Th.Row(false));
        br.Controls.Add(Th.B("➕ Add rule", () => EditRule(-1), true));
        br.Controls.Add(Th.B("✎ Edit", () => EditRule(Sel())));
        br.Controls.Add(Th.B("⧉ Duplicate", () => { var i = Sel(); if (i < 0) return; book.Rules.Insert(i + 1, book.Rules[i].Copy()); Changed(); Fill(i + 1); }));
        br.Controls.Add(Th.B("▲", () => Move(-1)));
        br.Controls.Add(Th.B("▼", () => Move(1)));
        br.Controls.Add(Th.B("🗑 Delete", () => { var i = Sel(); if (i < 0) return; book.Rules.RemoveAt(i); Changed(); Fill(Math.Min(i, book.Rules.Count - 1)); }));
        br.Controls.Add(Th.B("↩ Undo", () => { var u = RuleBooks.Undo(feature); if (u == null) { Say("Nothing to undo."); return; } book = u.Copy(); words.Text = book.Instructions; checkedSinceChange = false; Fill(-1); Say("↩ Back to the version before the last save."); }));
        br.Controls.Add(Th.L("      ", 9, Th.Muted));
        br.Controls.Add(Th.B("✦ Check rules", Check, true));
        br.Controls.Add(Th.B("▶ Preview", Preview));
        useBtn = Th.B("✓ Use these rules", Use, true);
        br.Controls.Add(useBtn);
        lv = st.Add(new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, MultiSelect = false, Height = 300, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = Th.F(9.5f) }, true);
        lv.Columns.Add("On · #", 60); lv.Columns.Add("When (all true)", 520); lv.Columns.Add("Except when", 140); lv.Columns.Add("Then", 120); lv.Columns.Add("Why", 220); lv.Columns.Add("Matches", 80);
        lv.DoubleClick += (_, _) => EditRule(Sel());
        lv.ItemChecked += (_, e) => { if (filling || e.Item.Index >= book.Rules.Count) return; book.Rules[e.Item.Index].On = e.Item.Checked; Changed(); };
        lv.Click += (_, _) => { var i = Sel(); if (i >= 0 && report != null && i < report.Caught.Length) ShowCaught(i, false); };
        extra = st.Add(Th.Wrap("", 9.5f, Th.Muted), true);
        var er = st.Add(Th.Row(false));
        er.Controls.Add(Th.B("✎ Projects first…", () => { var v = Prompt.Ask(this, "Projects first", "Project codes to show first (comma separated)", string.Join(", ", book.First)); if (v == null) return; book.First = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).ToList(); Changed(); Fill(Sel()); }));
        er.Controls.Add(Th.B("✎ What the AI judges…", () => { var v = Prompt.Ask(this, "What the AI judges", "For the emails no rule decides, the AI checks (empty = no AI):", book.AiCheck); if (v == null) return; book.AiCheck = v.Trim(); Changed(); Fill(Sel()); }));
        var sr = st.Add(new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent });
        dots = new WorkDots(); status = Th.L("", 10, Th.AccentText);
        sr.Controls.Add(dots); sr.Controls.Add(status);
        output = st.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 230, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10), BorderStyle = BorderStyle.None }, true);
        st.Add(Th.Signature());
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    bool filling;
    void Say(string s, bool busy = false) { status.Text = s; dots.On = busy; }
    int Sel() => lv.SelectedIndices.Count > 0 ? lv.SelectedIndices[0] : -1;
    void Changed() { checkedSinceChange = false; useBtn.Enabled = false; useBtn.Text = "✓ Use these rules (✦ Check first)"; }

    void ShowFeature(string f)
    {
        feature = f;
        book = RuleBooks.Load(f).Copy();
        title.Text = "📏 Rules · " + RuleBooks.Features.First(x => x.Id == f).Label;
        tabs.Controls.Clear();
        foreach (var (id, label, ready) in RuleBooks.Features)
        {
            var idd = id;
            var c = Th.Chip(label + (ready ? $" · {RuleBooks.Load(id).Rules.Count}" : " · soon"), id == f, () => { if (ready) ShowFeature(idd); });
            c.Enabled = ready;
            tabs.Controls.Add(c);
        }
        words.Text = book.Instructions;
        words.PlaceholderText = f == "brief"
            ? "e.g. SLV first. Contractor submittals of AR / ID / LS (SSM, SHD, SDS, MAR, RFI) = waiting, urgent when overdue. Hide MZA letters going out where I'm only in Cc."
            : "e.g. Emails from @omniumint.com about Tower C → RS5. Anything with “Saadiyat Marina” → NOU. Newsletters → unsorted.";
        report = null; checkedSinceChange = false; useBtn.Enabled = false; useBtn.Text = "✓ Use these rules (✦ Check first)";
        output.Text = "";
        Fill(-1);
        Say("Reading your recent emails to try the rules on…", true);
        Task.Run(() => RuleCheck.Sample(f)).ContinueWith(t =>
        {
            try { BeginInvoke(() => { if (feature != f) return; sample = t.Result; report = RuleCheck.Run(feature, book, sample); Fill(Sel()); Say($"Ready · tried on {sample.Count:N0} recent emails"); }); } catch { }
        });
    }

    void Fill(int select)
    {
        filling = true;
        lv.BeginUpdate();
        lv.Items.Clear();
        for (int i = 0; i < book.Rules.Count; i++)
        {
            var r = book.Rules[i];
            var m = report != null && i < report.Raw.Length ? $"{report.Raw[i]} ▸" : "";
            lv.Items.Add(new ListViewItem(new[] { $"{i + 1}", RuleBooks.When(r), string.Join(" / ", r.Except), RuleBooks.ThenText(feature, r.Then), r.Why, m }) { Checked = r.On, ForeColor = report != null && i < report.Raw.Length && report.Raw[i] == 0 ? Th.Warn : Th.Text });
        }
        lv.EndUpdate();
        filling = false;
        if (select >= 0 && select < lv.Items.Count) { lv.Items[select].Selected = true; lv.EnsureVisible(select); }
        extra.Text = (book.First.Count > 0 ? "⬆ Show first: " + string.Join(", ", book.First) + "   ·   " : "") + (book.AiCheck.Length > 0 ? "✦ The AI judges the emails no rule decides: " + book.AiCheck : "No AI step (only the rules).");
    }

    void Move(int d)
    {
        var i = Sel(); var j = i + d;
        if (i < 0 || j < 0 || j >= book.Rules.Count) return;
        (book.Rules[i], book.Rules[j]) = (book.Rules[j], book.Rules[i]);
        Changed(); Fill(j);
    }

    async void FromWords(bool add)
    {
        var text = add ? Prompt.AskLong(this, "✦ Add from words", "Describe the new rule(s) in your words — they are added to the list, the others stay:", "") : words.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) { Say("Write your words first."); return; }
        Say("✦ The AI is turning your words into rules…", true);
        try
        {
            var made = await BriefRules.Make(feature, text, CancellationToken.None);
            if (add)
            {
                book.Rules.AddRange(made.Rules);
                foreach (var p in made.First) if (!book.First.Contains(p)) book.First.Add(p);
                if (made.AiCheck.Length > 0) book.AiCheck = (book.AiCheck + " " + made.AiCheck).Trim();
                book.Instructions = (book.Instructions + "\n" + text).Trim();
                words.Text = book.Instructions;
            }
            else { made.Instructions = text; book = made; }
            Changed(); Fill(-1);
            Say($"✓ {made.Rules.Count} rule(s) {(add ? "added" : "made")} — check them, then ✦ Check rules");
        }
        catch (Exception e) { Say("⚠ " + e.Message); }
    }

    async void Check()
    {
        book.Instructions = words.Text.Trim();
        Say("✦ Checking the rules on your emails…", true);
        try
        {
            if (sample.Count == 0) sample = await Task.Run(() => RuleCheck.Sample(feature));
            report = await Task.Run(() => RuleCheck.Run(feature, book, sample));
            Fill(Sel());
            var sb = new StringBuilder($"✦ CHECK · {report.Warnings.Count} warning(s)\r\n\r\n");
            foreach (var w in report.Warnings) sb.AppendLine(w);
            if (report.Warnings.Count > 0) sb.AppendLine();
            foreach (var g in report.Good) sb.AppendLine(g);
            output.Text = sb.ToString();
            Say("✦ Asking the AI whether the rules fit your words…", true);
            try { foreach (var n in await RuleCheck.AiReview(feature, book, CancellationToken.None)) output.AppendText("\r\n" + n); } catch (Exception e) { output.AppendText("\r\n(AI review not available: " + e.Message + ")"); }
            checkedSinceChange = true; useBtn.Enabled = true; useBtn.Text = "✓ Use these rules";
            Say(report.Warnings.Count == 0 ? "✓ Checked — no warnings" : $"Checked — {report.Warnings.Count} warning(s) below (you can still use the rules)");
        }
        catch (Exception e) { Say("⚠ " + e.Message); }
    }

    async void Preview()
    {
        book.Instructions = words.Text.Trim();
        Say("▶ Building the preview…", true);
        try
        {
            if (sample.Count == 0) sample = await Task.Run(() => RuleCheck.Sample(feature));
            output.Text = "▶ PREVIEW — nothing changes until ✓ Use\r\n\r\n" + (await RuleCheck.Preview(feature, RuleBooks.Load(feature), book, sample, CancellationToken.None)).Replace("\n", "\r\n").Replace("\r\r", "\r");
            Say("▶ Preview ready");
        }
        catch (Exception e) { Say("⚠ " + e.Message); }
    }

    void Use()
    {
        if (!checkedSinceChange) { Say("✦ Check rules first."); return; }
        book.Instructions = words.Text.Trim();
        RuleBooks.Save(feature, book.Copy());
        Say(feature == "projects" ? "✓ Saved — your emails are sorted again with these rules" : "✓ Saved — the brief uses these rules from now on");
        ShowFeature(feature);
        changed?.Invoke();
    }

    void ShowCaught(int i, bool all)
    {
        var l = report.Caught[i];
        output.Text = $"Rule {i + 1} matches {l.Count} email(s) of the sample (before the rule order):\r\n\r\n" + string.Join("\r\n", l.Take(60).Select(h => $"{h.Date:dd MMM} · {h.FromName} · {h.Subject}"));
    }

    void EditRule(int i)
    {
        var r = i >= 0 && i < book.Rules.Count ? book.Rules[i].Copy() : new NRule { Source = "you", Then = feature == "brief" ? "waiting" : "" };
        using var f = new NovaForm { Text = i < 0 ? "➕ New rule" : $"✎ Rule {i + 1}", Width = 820, Height = 620, StartPosition = FormStartPosition.CenterParent };
        var s = new Stack();
        f.Controls.Add(s);
        s.Add(Th.Wrap("Fill only what you need. Each box = any of its words (comma separated). All filled boxes must be true.", 9.5f, Th.Muted), true);
        TextBox Field(string label, List<string> v)
        {
            s.Add(Th.L(label, 10, Th.Text, true));
            var b = s.Add(Th.Box(false), true); b.Text = string.Join(", ", v); return b;
        }
        var from = Field("Sender (name or address part)", r.From);
        var subj = Field("Subject or attachment name has", r.Subject);
        var text = Field("Text has", r.Text);
        var proj = Field("Project (codes)", r.Project);
        var exc = Field("Except when the text has", r.Except);
        s.Add(Th.L("I am", 10, Th.Text, true));
        var inBox = s.Add(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) });
        inBox.Items.AddRange(new object[] { "anywhere (To or Cc)", "in To", "only in Cc" });
        inBox.SelectedIndex = r.In == "to" ? 1 : r.In == "cc" ? 2 : 0;
        var noQ = s.Add(Th.Check("No question asked in the email", r.NoQuestion, _ => { }));
        var auto = s.Add(Th.Check("Automatic sender (Asite, Thinkproject, noreply…)", r.Auto, _ => { }));
        s.Add(Th.L("Then", 10, Th.Text, true));
        var then = s.Add(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) });
        var opts = feature == "brief" ? new[] { "urgent", "waiting", "fyi", "hide", "include" } : MailProjects.Choices().Select(c => c.Code).Append("unsorted").ToArray();
        foreach (var o in opts) then.Items.Add(RuleBooks.ThenText(feature, o));
        then.SelectedIndex = Math.Max(0, Array.IndexOf(opts, r.Then));
        var why = Field("Why (shown on each email)", new List<string> { r.Why });
        var row = s.Add(Th.Row(false));
        row.Controls.Add(Th.B("✓ Save rule", () =>
        {
            List<string> L(TextBox b) => b.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            r.From = L(from); r.Subject = L(subj); r.Text = L(text); r.Project = L(proj).Select(x => x.ToUpperInvariant()).ToList(); r.Except = L(exc);
            r.In = inBox.SelectedIndex == 1 ? "to" : inBox.SelectedIndex == 2 ? "cc" : "any";
            r.NoQuestion = noQ.Checked; r.Auto = auto.Checked; r.Then = opts[Math.Max(0, then.SelectedIndex)]; r.Why = why.Text.Trim(); r.Source = "you";
            if (i >= 0 && i < book.Rules.Count) book.Rules[i] = r; else book.Rules.Add(r);
            f.DialogResult = DialogResult.OK; f.Close();
        }, true));
        row.Controls.Add(Th.B("Cancel", () => f.Close()));
        if (f.ShowDialog(this) == DialogResult.OK) { Changed(); Fill(i >= 0 ? i : book.Rules.Count - 1); }
    }
}
