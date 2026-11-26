using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 📋 Review vs contract: a submittal (MAR, SSM, SDS, method statement, RFI…) checked against the project's contract register — latest
/// revisions only, the order of precedence applied (a change wins on its item), your check lists (e.g. Approved Vendor List) checked by name.
/// Nova finds the governing passages itself (no AI); the AI only writes the item-by-item comparison when you allow it, from excerpts.
/// Designed by A.HAMROUNI
/// </summary>
class ContractReviewForm : NovaForm
{
    static readonly Regex SpecRx = new(@"\b(\d{2})\s?(\d{2})\s?(\d{2})(\.\d{2})?\b");
    static readonly Regex BrandRx = new(@"(?:manufacturer|brand|make|supplier|product(?: name)?|proposed (?:product|material)|trade name)\s*[:\-–]\s*([A-Z][\w&\.\-]+(?:\s+[A-Z][\w&\.\-]+){0,2})", RegexOptions.IgnoreCase);

    readonly string project;
    readonly FlowLayoutPanel outp;
    readonly Label status;
    readonly TextBox file;
    CancellationTokenSource cts;
    string answerText = "";

    public ContractReviewForm(string project, string path)
    {
        this.project = project;
        Text = "📋 Review vs contract · " + project;
        Width = 1240; Height = 860;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(12, 8, 12, 0), BackColor = Th.Bg };
        top.Controls.Add(Th.L("📋 Review a submittal against the " + project + " contract", 13, Th.Text));
        top.SetFlowBreak(top.Controls[0], true);
        file = Th.Box(false); file.Width = 760; file.Text = path ?? ""; file.PlaceholderText = "the submittal: a PDF, Word or .msg file (drag it here)";
        file.AllowDrop = true;
        file.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        file.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) file.Text = f[0]; };
        top.Controls.Add(file);
        top.Controls.Add(Th.B("📄 Choose…", () => { using var d = new OpenFileDialog { Filter = "Documents|*.pdf;*.docx;*.doc;*.msg;*.xlsx;*.eml|All files|*.*" }; if (d.ShowDialog(this) == DialogResult.OK) file.Text = d.FileName; }));
        top.Controls.Add(Th.B("⚖ Review", () => Run(), true));
        top.Controls.Add(Th.B("■ Stop", () => cts?.Cancel()));
        status = Th.L("", 9.5f, Th.Muted);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(10, 6, 10, 6), BackColor = Th.Bg };
        bottom.Controls.Add(Th.B("🔗 Copy the answer", () => { if (answerText.Length > 0) Clipboard.SetText(answerText); status.Text = "✓ Copied."; }));
        bottom.Controls.Add(status);
        outp = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14, 6, 14, 6), BackColor = Th.Bg };
        Controls.Add(outp); Controls.Add(bottom); Controls.Add(top);
        FormClosing += (_, _) => cts?.Cancel();
        if (!string.IsNullOrEmpty(path)) Shown += (_, _) => Run();
    }

    void Add(string text, float size = 10, Color? color = null, bool bold = false, string open = null)
    {
        var l = Th.Wrap(text, size, color ?? Th.Text);
        l.MaximumSize = new Size(Math.Max(600, outp.ClientSize.Width - 40), 0);
        if (bold) l.Font = Th.F(size, true);
        if (open != null) { l.Cursor = Cursors.Hand; l.Click += (_, _) => Shell.Open(open); FileMenu.Attach(l, _ => open); }
        outp.Controls.Add(l);
    }

    void Run()
    {
        var path = file.Text.Trim().Trim('"');
        if (!File.Exists(path)) { status.Text = "⚠ Choose the submittal file first."; return; }
        if (Projects.Private.IsMatch(path)) { status.Text = "🔒 That file looks private — Nova never reads it."; return; }
        cts?.Cancel(); var my = cts = new CancellationTokenSource();
        outp.Controls.Clear(); answerText = "";
        status.Text = "📖 Reading the submittal…";
        var reg = ContractRegister.Get(project);
        if (reg == null || reg.Docs.Count == 0) { Add("⚠ The " + project + " contract register is empty — add the contract areas in Settings → 📜 Contract area first.", 11, Th.Warn); status.Text = ""; return; }
        bool canAi = Ai.Active() != null && !Ai.QuotaOut && FolderAsk.Sees != "never";
        if (canAi && FolderAsk.Sees == "ask" &&
            MessageBox.Show(this, "Let the AI compare item by item? It receives EXCERPTS only: the submittal's first pages and the contract passages Nova found (never whole documents, 🔒 private folders never).\n\nNo = Nova shows the governing passages without the AI.", "✦ AI", MessageBoxButtons.YesNo) != DialogResult.Yes)
            canAi = false;
        Task.Run(async () =>
        {
            void Say(string s) => Program.UiContext.Post(_ => { if (!status.IsDisposed) status.Text = s; }, null);
            var segs = Projects.Read(path, my.Token).Segs;
            var text = string.Join("\n", segs.Select(s => s.Text));
            var lb = Filing.Classify(path);
            var title = Path.GetFileNameWithoutExtension(path);
            // what the submittal is about: its spec sections, drawing numbers, brands, and the words of its title
            var specs = SpecRx.Matches(text).Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value}").Where(s => !s.StartsWith("00") && !s.StartsWith("20") && !s.StartsWith("19")).GroupBy(s => s).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(4).ToList();
            var dwgs = Revisions.Numbers(text).Where(n => !n.Equals(lb.Number, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
            var brands = BrandRx.Matches(text).Select(m => m.Groups[1].Value.Trim()).Where(b => b.Length > 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
            var subject = Regex.Replace(title, @"[_\-]+", " ");
            subject = Regex.Replace(subject, @"\b(?:[A-Z]{2,5}\s){2,}\d+\b|\bRev\s?\d+\b|\bR\d{2}\b", " ", RegexOptions.IgnoreCase);
            var words = Regex.Split(subject, @"[^\p{L}\p{N}]+").Where(w => w.Length > 3 && !Regex.IsMatch(w, @"^\d+$")).Concat(specs).Concat(brands).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
            Say("⚖ Finding what governs in the contract register…");
            var (hits, notUsed) = ContractRegister.Governs(project, words, 14);
            foreach (var sp in specs)
            {
                var (h2, n2) = ContractRegister.Governs(project, new List<string> { sp }, 4);
                hits.AddRange(h2.Where(h => hits.All(x => x.Doc.Path != h.Doc.Path || x.Loc != h.Loc)));
                notUsed.AddRange(n2);
            }
            hits = hits.OrderBy(h => h.Level).ThenByDescending(h => h.Doc.Date).Take(18).ToList();
            // ✓ check lists (your types acting as a list: Approved Vendor List…)
            var checks = new List<(string Type, string Brand, bool On, string Cite, string Near)>();
            foreach (var t in ContractRegister.Types(project).Where(t => t.Acts == "check"))
            {
                var docs = reg.Docs.Where(d => d.Type == t.Name && d.Governing).ToList();
                if (docs.Count == 0 || brands.Count == 0) continue;
                // the rows of the list (📋 Vendor register in the Nova index) — the brand on the row of the product
                var vrows = NovaIndex.Vendors(project, docs.Select(d => d.Path));
                if (vrows.Count > 0)
                {
                    var subjWords = words.Where(w => w.Length > 3 && !brands.Contains(w)).Select(ContractRegister.Norm).ToList();
                    foreach (var b in brands)
                    {
                        var nb = ContractRegister.Norm(b);
                        var on = vrows.FirstOrDefault(r => ContractRegister.Norm(r.Row).Contains(nb));
                        var doc = on == null ? null : docs.First(d => d.Path.Equals(on.Path, StringComparison.OrdinalIgnoreCase));
                        if (on != null) { checks.Add((t.Name, b, true, $"{doc.Name} {doc.Rev} · p.{on.Page} · {on.Division} · {on.Product}".Trim(), on.Row)); continue; }
                        var near = vrows.FirstOrDefault(r => subjWords.Any(w => ContractRegister.Norm(r.Product + " " + r.Row).Contains(w)));
                        var nd = near == null ? null : docs.First(d => d.Path.Equals(near.Path, StringComparison.OrdinalIgnoreCase));
                        checks.Add((t.Name, b, false, near == null ? "" : $"{nd.Name} {nd.Rev} · p.{near.Page} · {near.Division} · {near.Product}".Trim(), near?.Row ?? ""));
                    }
                    continue;
                }
                foreach (var b in brands)
                {
                    (bool, string, string) found = (false, "", "");
                    foreach (var d in docs)
                    {
                        var ks = FolderIndex.Known(d.Path) ?? new List<Seg>();
                        var hit = ks.FirstOrDefault(s => (s.Text ?? "").Contains(b, StringComparison.OrdinalIgnoreCase));
                        if (hit != null) { found = (true, $"{d.Name} {d.Rev} · {hit.Loc}", Around(hit.Text, b)); break; }
                        var near = specs.Count > 0 ? ks.FirstOrDefault(s => specs.Any(sp => (s.Text ?? "").Contains(sp))) : null;
                        if (near != null && found.Item2.Length == 0) found = (false, $"{d.Name} {d.Rev} · {near.Loc}", Around(near.Text, specs.First(sp => near.Text.Contains(sp))));
                    }
                    checks.Add((t.Name, b, found.Item1, found.Item2, found.Item3));
                }
            }
            // ✦ the item-by-item comparison (AI, excerpts only)
            JsonObject ai = null;
            if (canAi && hits.Count > 0)
            {
                Say("✦ The AI compares item by item (excerpts only)…");
                var sb = new StringBuilder();
                int i = 1;
                foreach (var h in hits) sb.Append($"[{i++}] {h.Doc.Type} · level {(h.Level == 0 ? "change (wins on its item)" : h.Level.ToString())} · {h.Doc.Name} {h.Doc.Rev} · {h.Loc}\n{Clip(h.Text, 700)}\n\n");
                foreach (var c in checks) sb.Append($"[check list {c.Type}] brand “{c.Brand}”: {(c.On ? "ON the list" : "NOT found on the list")} {c.Cite} {Clip(c.Near, 300)}\n");
                var sys = "You are a senior architect reviewing a contractor's submittal against the contract. Use ONLY the contract passages given; when two disagree, the lower level number governs and a change (EI/VO/PMI/VE) wins on its own item. " +
                          "Reply JSON only: {\"verdict\":\"<one line>\",\"code\":\"A|B|C|D\",\"items\":[{\"item\":\"…\",\"result\":\"complies|does not comply|to confirm\",\"submittal\":\"what the submittal proposes\",\"contract\":\"what the contract requires (short quote)\",\"source\":\"[n]\"}],\"comments\":\"the review comments to send, numbered\"}";
                var user = $"SUBMITTAL {Path.GetFileName(path)} ({lb.Kind}):\n{Clip(text, 6000)}\n\nCONTRACT PASSAGES (latest revisions, governing):\n{sb}";
                try { ai = Ai.ParseJson(await SmartSearch.CloudChat(sys, user, 60, my.Token)); }
                catch (Exception e) { Say("⚠ AI: " + e.Message); }
            }
            foreach (var h in hits.GroupBy(h => h.Doc.Path).Take(8)) NovaIndex.Link(path, h.Key, "reviewed against", ai?["code"]?.ToString() ?? "");
            Program.UiContext.Post(_ => Present(path, lb, specs, dwgs, brands, hits, notUsed.Distinct().Take(10).ToList(), checks, ai), null);
        }, my.Token).ContinueWith(t => { if (t.IsFaulted) Program.UiContext.Post(_ => { if (!status.IsDisposed) status.Text = "⚠ " + t.Exception?.GetBaseException().Message; }, null); });
    }

    static string Clip(string s, int n) { s = Regex.Replace(s ?? "", @"\s+", " ").Trim(); return s.Length > n ? s[..n] + "…" : s; }
    static string Around(string t, string w) { t ??= ""; var i = t.IndexOf(w, StringComparison.OrdinalIgnoreCase); if (i < 0) return Clip(t, 200); var a = Math.Max(0, i - 90); return Clip(t.Substring(a, Math.Min(240, t.Length - a)), 240); }

    void Present(string path, FileLabels lb, List<string> specs, List<string> dwgs, List<string> brands, List<ContractRegister.Gov> hits, List<string> notUsed,
              List<(string Type, string Brand, bool On, string Cite, string Near)> checks, JsonObject ai)
    {
        if (IsDisposed) return;
        var sb = new StringBuilder();
        void Out(string s, float size = 10, Color? c = null, bool bold = false, string open = null) { Add(s, size, c, bold, open); sb.AppendLine(s); }
        Out($"📋 {Path.GetFileName(path)}{(lb.Kind.Length > 0 ? " · " + lb.Kind : "")}", 13, Th.Text, true);
        Out($"About: {(specs.Count > 0 ? "spec " + string.Join(", ", specs) + " · " : "")}{(brands.Count > 0 ? "brand " + string.Join(", ", brands) + " · " : "")}{(dwgs.Count > 0 ? "drawings " + string.Join(", ", dwgs.Take(4)) : "")}", 9.5f, Th.Muted);
        if (ai != null)
        {
            var code = ai["code"]?.ToString() ?? "";
            Out($"{(code is "A" ? "✓" : code is "B" ? "⚠" : "✗")} {ai["verdict"]}{(code.Length > 0 ? $"  (code {code})" : "")}", 13, code == "A" ? Th.Good : Th.Warn, true);
        }
        else if (hits.Count == 0) Out("Nothing in the contract register speaks about this submittal yet (or those documents are not read yet — 📶 Indexing progress).", 11, Th.Warn);
        else Out("⚖ What governs — Nova found the contract passages below (AI off: compare them with the submittal).", 11, Th.Text, true);
        if (hits.Count > 0)
            Out("⚖ What governs here: " + string.Join(" › ", hits.GroupBy(h => h.Doc.Name).Take(6).Select((g, i) => $"{i + 1} {g.First().Doc.Type}: {g.Key} {g.First().Doc.Rev}".Trim())), 10, Th.Text);
        foreach (var c in checks)
            Out(c.On ? $"✓ {c.Type}: “{c.Brand}” is on the list · {c.Cite}\n      “{c.Near}”" : $"✗ {c.Type}: “{c.Brand}” is NOT on the list{(c.Cite.Length > 0 ? " · nearest: " + c.Cite + "\n      “" + c.Near + "”" : "")} → a substitution request is needed", 10, c.On ? Th.Good : Th.Warn);
        if (ai?["items"] is JsonArray items)
            foreach (var it in items)
            {
                var r = it?["result"]?.ToString() ?? "";
                var src = it?["source"]?.ToString() ?? "";
                var m = Regex.Match(src, @"\d+");
                var h = m.Success && int.TryParse(m.Value, out var k) && k >= 1 && k <= hits.Count ? hits[k - 1] : null;
                Out($"{(r.StartsWith("complies") ? "✓" : r.StartsWith("does not") ? "✗" : "?")} {it?["item"]} — {r}: the submittal says {it?["submittal"]}; the contract: “{it?["contract"]}”{(h != null ? $" · {h.Doc.Name} {h.Doc.Rev} {h.Loc}" : "")}",
                    10, r.StartsWith("complies") ? Th.Good : r.StartsWith("does not") ? Color.FromArgb(0xFF, 0x7A, 0x7A) : Th.Warn, false, h?.Doc.Path);
            }
        Out("Contract passages (click to open):", 10, Th.Muted, true);
        int n = 1;
        foreach (var h in hits)
            Out($"[{n++}] {h.Doc.Type}{(h.Level is > 0 and < 50 ? " · level " + h.Level : h.Level == 0 ? " · change" : "")} · {h.Doc.Name} {h.Doc.Rev} · {h.Loc}\n      “{Clip(h.Text, 300)}”", 9.5f, Th.Text, false, h.Doc.Path);
        if (notUsed.Count > 0) Out("Not used (and why): " + string.Join(" · ", notUsed), 9.5f, Th.Muted);
        var comments = ai?["comments"]?.ToString() ?? "";
        if (comments.Length > 0)
        {
            Out("✍ Review comments (draft — check before sending):", 10.5f, Th.Text, true);
            Out(comments, 10, Th.Text);
        }
        answerText = sb.ToString();
        status.Text = "✓ Done · latest revisions only · click a line to open the source";
        Log.Write($"📋 review vs contract {project}: {Path.GetFileName(path)} · {hits.Count} passages · AI {(ai != null ? "yes" : "no")}");
    }
}
