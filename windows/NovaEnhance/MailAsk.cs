using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>One source Nova may cite: [E0] this email, [E1…] earlier emails, [A1…] attachments, [D1…] project documents.</summary>
class AskSource
{
    public string Id = "", Title = "", Text = "";
    public MailHit Mail;                 // E
    public string Path = "", Goto = "", Loc = "";   // A / D
}

/// <summary>Your signature stays as it is: the AI's own closing is removed when Outlook already adds yours.</summary>
static class Sig
{
    static readonly Regex Closing = new(@"^\s*((best|kind|warm|warmest|with best|many)\s+regards|regards|thanks?( you)?( and regards| & regards)?|thank you( very much)?|sincerely|yours (sincerely|faithfully)|cordialement|bien à vous|مع (خالص )?التحية|وتفضلوا)\b[\s,.!]*$", RegexOptions.IgnoreCase);

    public static string StripClosing(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").TrimEnd().Split('\n').ToList();
        int nonEmpty = 0;
        for (int i = lines.Count - 1; i >= 0 && nonEmpty < 6; i--)
        {
            if (lines[i].Trim().Length == 0) continue;
            nonEmpty++;
            if (Closing.IsMatch(lines[i])) return string.Join("\n", lines.Take(i)).TrimEnd();
        }
        return string.Join("\n", lines).TrimEnd();
    }

    public static bool HasSignature(string html) =>
        Regex.IsMatch(html ?? "", "_MailAutoSig|id=\"?Signature|(best|kind|warm)\\s+regards", RegexOptions.IgnoreCase);
}

/// <summary>
/// ✦ Ask Nova about this email: the email you are reading is the reference. Write any prompt; Nova reads this email, its attachments,
/// the earlier emails of the conversation and with the same references, and your project documents, answers with sources you can open,
/// then checks its own answer against those sources (✓ supported · ⚠ partly · removed when not found). Designed by A.HAMROUNI
/// </summary>
class MailAskForm : NovaForm
{
    MailHit anchor;
    string fullBody = "";
    readonly Stack top;
    readonly Label head, meta, folderLbl, status;
    readonly FlowLayoutPanel atts;
    readonly TextBox prompt;
    readonly Button askBtn;
    readonly Panel webHost;
    readonly FlowLayoutPanel bottom;
    Microsoft.Web.WebView2.WinForms.WebView2 web;
    CancellationTokenSource cts;
    bool gathered;
    readonly List<AskSource> thread = new();
    readonly List<(string Name, string Path, Seg Seg)> attPool = new();
    List<AskSource> lastSources = new();
    string lastAnswer = "", lastPrompt = "", history = "", lastHtml = "";

    public MailAskForm()
    {
        Text = "Nova Enhance · ✦ Ask Nova about this email";
        Width = Math.Max(900, Store.Int("win_mailask_w", 1100)); Height = Math.Max(640, Store.Int("win_mailask_h", 860));
        MinimumSize = new Size(760, 560);
        top = new Stack { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 12, 16, 6) };
        webHost = new Panel { Dock = DockStyle.Fill, BackColor = Th.Bg, Padding = new Padding(16, 0, 16, 0) };
        bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Th.Bg, Padding = new Padding(14, 6, 14, 10), Visible = false };
        Controls.Add(webHost); Controls.Add(bottom); Controls.Add(top);

        top.Add(Th.L("✦ Ask Nova about this email", 14, Th.Text, true));
        head = top.Add(Th.Wrap("", 11, Th.Text), true);
        head.Font = Th.F(11, true);
        meta = top.Add(Th.Wrap("", 9, Th.Muted), true);
        atts = top.Add(Th.Row());
        var fr = top.Add(Th.Row(false));
        folderLbl = Th.L("", 9.5f, Th.Text);
        folderLbl.Margin = new Padding(0, 7, 8, 0);
        fr.Controls.Add(folderLbl);
        fr.Controls.Add(Th.B("📁 Choose…", ChooseFolder));
        fr.Controls.Add(Th.B("No folder", () => { SetFolder(""); }));

        top.Add(Th.L("✎ YOUR PROMPT — write anything: what to check, focus on, leave out, the format, length, tone and language", 8.5f, Th.Muted, true));
        prompt = top.Add(Th.Prose(Th.Box(true, 4)), true);
        prompt.PlaceholderText = "e.g. Check whether their olive-tree requirements match our landscape spec and the BOQ item; focus on UV resistance, fire rating and warranty; ignore the PS commercial part; give me a short technical reply I can send.   (Ctrl+Enter = Ask)";
        prompt.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ask(); } };
        var row = top.Add(Th.Row());
        askBtn = row.Controls.Add2(Th.B("✦ Ask", Ask, true));
        row.Controls.Add(Th.B("🧹 Clear", Clear));
        row.Controls.Add(Th.B("■ Stop", () => cts?.Cancel()));
        status = top.Add(Th.Wrap("", 9.5f, Th.AccentText), true);

        bottom.Controls.Add(Th.B("✦ Draft reply from this answer", DraftReply, true));
        bottom.Controls.Add(Th.B("⧉ Copy", () => { if (lastAnswer.Length > 0) Clipboard.SetText(lastAnswer); status.Text = "⧉ Copied"; }));
        bottom.Controls.Add(Th.B("⇩ Save as Word", SaveWord));
        bottom.Controls.Add(Th.B("👍", () => { if (lastAnswer.Length > 0) { Memory.Good(lastPrompt, lastAnswer, anchor?.Entry ?? "", lastSources.Where(s => s.Path.Length > 0).Select(s => s.Path), lastSources.Select(s => s.Text), "mail"); status.Text = "👍 🧠 Nova remembers this answer"; } }));
        bottom.Controls.Add(Th.L("   Follow-up? Write it in the prompt and press ✦ Ask — this email stays the reference.", 9, Th.Muted));

        ResizeEnd += (_, _) => { if (WindowState == FormWindowState.Normal) { Store.SetInt("win_mailask_w", Width); Store.SetInt("win_mailask_h", Height); } };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; cts?.Cancel(); Hide(); } };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        Shown += async (_, _) => await EnsureWeb();
    }

    // ------------------------------------------------------------------ the email

    /// <summary>Opens for an email (from a Nova list) — everything from the previous email is cleared.</summary>
    public void ShowMail(MailHit h)
    {
        cts?.Cancel();
        anchor = h;
        gathered = false; thread.Clear(); attPool.Clear();
        fullBody = h?.Body ?? "";
        Clear();
        if (h == null) { head.Text = "No email selected — select an email in Outlook, then press ✦ Ask Nova about this email again."; meta.Text = ""; atts.Controls.Clear(); return; }
        head.Text = h.Subject;
        meta.Text = $"{(h.Sent ? "You" : h.FromName)} → {h.To}" + (h.Cc.Length > 0 ? $" · Cc: {h.Cc}" : "") + $" · {h.Date:ddd dd MMM yyyy HH:mm}";
        atts.Controls.Clear();
        foreach (var n in (h.AttNames ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => !Regex.IsMatch(n, @"^image\d*\.(png|jpe?g|gif)$", RegexOptions.IgnoreCase)))
        {
            var nn = n;
            var b = Th.B("📎 " + n, () => MailPreview.PreviewAttachment(anchor, nn, this));
            b.Font = Th.F(9);
            atts.Controls.Add(b);
        }
        SetFolder(FolderFor(h), false);
        top.FitAll();
        prompt.Focus();
    }

    /// <summary>From the Outlook panel: the email selected (or open) in Outlook right now.</summary>
    public async void LoadCurrent(string subjectHint)
    {
        head.Text = "Reading the email from Outlook…";
        try
        {
            var h = await Sta(() => Outlook.CurrentMail());
            if (h == null && subjectHint.Length > 0)
                h = MailIndex.Search(new MailQuery { Phrase = subjectHint, Limit = 1 }).FirstOrDefault();
            ShowMail(h);
        }
        catch (Exception e) { ShowMail(null); status.Text = "⚠ " + e.Message; Log.Error("ask about email", e); }
    }

    void Clear()
    {
        cts?.Cancel();
        prompt.Text = "";
        lastAnswer = lastPrompt = history = "";
        lastSources = new();
        status.Text = "";
        bottom.Visible = false;
        Render("<p class=muted>Write your prompt above and press ✦ Ask. Nova reads this email, its attachments, the earlier emails with the same subject or references, and your project folder — then checks its answer against them.</p>");
    }

    // ------------------------------------------------------------------ project folder (remembered per project)

    static Dictionary<string, string> Map()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(Store.Str("win_proj_folders", "{}")) ?? new(); } catch { return new(); }
    }

    string projectKey = "";
    string folder = "";

    string FolderFor(MailHit h)
    {
        projectKey = Prompts.ProjectOf(h);
        return Map().TryGetValue(projectKey, out var f) ? f : "";
    }

    void SetFolder(string f, bool remember = true)
    {
        folder = f ?? "";
        if (remember && projectKey.Length > 0 && projectKey != "Other")
        {
            var m = Map();
            if (folder.Length > 0) m[projectKey] = folder; else m.Remove(projectKey);
            Store.Set("win_proj_folders", JsonSerializer.Serialize(m));
        }
        folderLbl.Text = folder.Length > 0
            ? $"📁 Project documents ({projectKey}): {GDrive.Display(folder)}"
            : $"📁 No project folder for {(projectKey.Length > 0 ? projectKey : "this email")} yet — choose one so Nova can check the spec, BOQ and drawings too (remembered)";
    }

    void ChooseFolder()
    {
        using var d = new FolderBrowserDialog { Description = $"Project folder for {projectKey} (spec, BOQ, drawings…)", SelectedPath = Directory.Exists(folder) ? folder : "" };
        if (d.ShowDialog(this) == DialogResult.OK) SetFolder(d.SelectedPath);
    }

    // ------------------------------------------------------------------ gathering

    static Task<T> Sta<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    static readonly Regex RefRx = new(@"\b[A-Z][A-Z0-9]{1,6}(?:-[A-Z0-9]{1,8}){2,9}\b");

    async Task Gather(CancellationToken ct)
    {
        if (gathered) return;
        status.Text = "Reading the email and its attachments…";
        try { var b = await Sta(() => Outlook.FullBody(anchor.Entry, anchor.StoreId)); if (b.Length > fullBody.Length) fullBody = b; } catch { }
        // Attachments: read on this PC (PDF, Word, Excel, PowerPoint, text; images with OCR).
        var dir = Path.Combine(Paths.Cache, "ask-mail", Math.Abs((anchor.Entry ?? "").GetHashCode()).ToString());
        foreach (var n in (anchor.AttNames ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ct.ThrowIfCancellationRequested();
            if (Regex.IsMatch(n, @"^image\d*\.(png|jpe?g|gif)$", RegexOptions.IgnoreCase)) continue;
            try
            {
                status.Text = "Reading " + n + "…";
                var path = await MailPreview.SaveAttachment(anchor, n, dir);
                var segs = await Sta(() => FileText.Extract(path));
                foreach (var s in segs.Where(s => s.Text.Trim().Length > 20)) attPool.Add((n, path, s));
            }
            catch (Exception e) { Log.Warn("ask about email", n + ": " + e.Message); }
        }
        // Earlier emails: the same conversation, then the same references (MZA-OMN-22-1497-L-2026-265, SLV-ZUC-…).
        status.Text = "Finding the earlier emails of this subject…";
        var found = new Dictionary<string, MailHit>();
        try { foreach (var m in MailIndex.ByConv(anchor.Conv, 12)) found.TryAdd(m.Entry, m); } catch { }
        var refs = RefRx.Matches((anchor.Subject ?? "") + " " + MailIndex.NewPart(fullBody)).Select(m => m.Value).Distinct().Take(4).ToList();
        foreach (var r in refs)
            try { foreach (var m in MailIndex.Search(new MailQuery { Phrase = r, Limit = 8 })) found.TryAdd(m.Entry, m); } catch { }
        try { foreach (var m in MailIndex.Search(new MailQuery { Phrase = Outstanding.NormSubject(anchor.Subject ?? ""), Limit = 8 })) found.TryAdd(m.Entry, m); } catch { }
        found.Remove(anchor.Entry ?? "");
        int i = 1;
        foreach (var m in found.Values.Where(m => m.Date <= anchor.Date.AddMinutes(1) || m.Sent).OrderByDescending(m => m.Date).Take(8))
            thread.Add(new AskSource { Id = "E" + i++, Mail = m, Title = $"{(m.Sent ? "You" : m.FromName)} · {m.Date:dd MMM yyyy} · {m.Subject}", Text = Cut(m.NewPart, 1500) });
        gathered = true;
    }

    static string Cut(string s, int n) { s = Regex.Replace(s ?? "", @"[ \t]+", " ").Trim(); return s.Length > n ? s[..n] + " …" : s; }

    // ------------------------------------------------------------------ asking

    const string AnswerRules =
        "You are a senior architect and technical coordinator (UAE projects) helping the user, an architect, deal with the email marked [E0]. " +
        "THE USER'S PROMPT LEADS: it decides the focus, what to check, what to leave out, the format, the length, the tone and the language. Follow it exactly. " +
        "Use ONLY the sources for facts: [E0] the email, [E1…] earlier related emails, [A1…] extracts of its attachments, [D1…] the user's project documents (labelled with type and revision). " +
        "After every fact put its source like [A2] or [D1] (several allowed). Prefer the LATEST revision; if you rely on one marked SUPERSEDED, say so with ⚠. " +
        "When sources disagree (spec vs BOQ vs drawing vs email), write a line starting with ⚠ CONFLICT: naming both values and both sources. " +
        "If something the prompt asks for is not in the sources, write 'Not found in your documents' for it — never invent values, clauses, dates or names. " +
        "Be technically precise: exact values, units, standards, clause numbers as written in the sources.";

    const string CheckRules =
        "You are a strict technical checker. Compare the ANSWER with the SOURCES, statement by statement (ignore the 🎓 ARCHITECT'S ADVICE section). Reply with JSON only: " +
        "{\"checks\":[{\"claim\":\"short statement\",\"status\":\"ok|partial|unsupported\",\"source\":\"[A1]\",\"note\":\"short reason\"}]," +
        "\"answer\":\"the same answer with every unsupported statement removed, and each partly supported one followed by ' ⚠ (partly supported: reason)'. Keep the wording, the [citations], the ⚠ CONFLICT lines and the 🎓 section otherwise unchanged.\"}";

    async void Ask()
    {
        if (anchor == null) { status.Text = "No email — open Nova from an email."; return; }
        Speller.FixBeforeSearch(prompt, Ask);
        var p = prompt.Text.Trim();
        if (p.Length == 0) p = "Give me the key technical points of this email, what they ask from me, and a solid technical answer.";
        if (Ai.Active() == null) { status.Text = "Add an AI key in Settings → AI & API keys."; return; }
        cts?.Cancel();
        var my = cts = new CancellationTokenSource();
        askBtn.Enabled = false;
        try
        {
            using var doing = Log.Doing("Ask about email · " + anchor.Subject);
            await Gather(my.Token);

            // What the prompt is about: ranks the attachment pages and the project documents.
            status.Text = "✦ Understanding your prompt…";
            var terms = await FolderAsk.Expand(p + " " + Outstanding.NormSubject(anchor.Subject ?? ""), history.Length > 0 ? lastPrompt : null, my.Token);
            var words = terms.SelectMany(t => Regex.Split(t.ToLowerInvariant(), @"\W+")).Where(w => w.Length > 2).Distinct().ToList();

            var sources = new List<AskSource> { new() { Id = "E0", Mail = anchor, Title = $"This email · {(anchor.Sent ? "You" : anchor.FromName)} · {anchor.Date:dd MMM yyyy}", Text = Cut(fullBody, 9000) } };
            sources.AddRange(thread);
            int a = 1;
            var firstOf = attPool.GroupBy(x => x.Name).Select(g => g.First());
            var ranked = attPool.Select(x => (x, score: words.Count(w => x.Seg.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))).Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.x);
            foreach (var x in firstOf.Concat(ranked).Distinct().Take(14))
                sources.Add(new AskSource { Id = "A" + a++, Title = $"{x.Name} · {x.Seg.Loc}", Text = Cut(x.Seg.Text, 1400), Path = x.Path, Goto = x.Seg.Goto, Loc = x.Seg.Loc });

            string docNote = "";
            if (folder.Length > 0)
            {
                if (FolderAsk.Sees == "never") docNote = "Project documents not sent (Settings → Folder search: the AI doesn't read your files).";
                else if (!Directory.Exists(folder) && !GDrive.IsRoot(folder)) docNote = "The project folder is not reachable: " + folder;
                else
                {
                    status.Text = "Reading the project folder (only new or changed files)…";
                    var pg = new FolderIndex.Progress();
                    var ui = new System.Windows.Forms.Timer { Interval = 700 };
                    ui.Tick += (_, _) => status.Text = $"Reading the project folder… {pg.Done:N0} of {pg.Files:N0} files" + (pg.Current is { Length: > 0 } c ? " · " + c : "");
                    ui.Start();
                    try { await Sta(() => { FolderIndex.Search(folder, null, null, _ => { }, null, pg, my.Token); return true; }); }
                    finally { ui.Stop(); ui.Dispose(); }
                    var ps = await Task.Run(() => FolderIndex.Retrieve(folder, terms, 14, 3, Memory.Boosts(terms), false));
                    int d = 1;
                    foreach (var x in ps)
                    {
                        var tag = string.Join(" · ", new[] { Revisions.KindOf(x.Path), Revisions.Tag(folder, x.Path) }.Where(t => t.Length > 0));
                        sources.Add(new AskSource { Id = "D" + d++, Title = FolderAsk.Label(x) + (tag.Length > 0 ? " — " + tag : ""), Text = Cut(x.Text, 1400), Path = x.Path, Goto = x.Goto ?? "", Loc = x.Loc ?? "" });
                    }
                }
            }
            my.Token.ThrowIfCancellationRequested();

            var block = new StringBuilder();
            foreach (var s in sources) block.Append('[').Append(s.Id).Append("] ").Append(s.Title).Append('\n').Append(s.Text).Append("\n\n");
            if (!Store.AiSeesFull)
            {
                var nE = sources.Count(s => s.Id.StartsWith("E")); var nA = sources.Count(s => s.Id.StartsWith("A")); var nD = sources.Count(s => s.Id.StartsWith("D"));
                var c = ConfirmSend.Ask(this, $"✦ Send this email and its sources to {Ai.Active().Label}?",
                    $"To answer, Nova sends: this email, {nE - 1} earlier email(s), {nA} attachment extract(s) and {nD} project-document extract(s) — about {block.Length:N0} characters. Then your AI checks its own answer against them.",
                    block.ToString(), "Send and answer", false);
                if (c != ConfirmSend.Choice.Send) { status.Text = "Cancelled — nothing was sent."; return; }
            }

            status.Text = $"✦ {Ai.Active().Label} is writing the answer from {sources.Count} sources…";
            var user = (history.Length > 0 ? "Earlier in this conversation:\n" + history + "\n" : "") + "SOURCES:\n" + block + "\nTHE USER'S PROMPT:\n" + p;
            var ans = (await Ai.Chat(AnswerRules + FolderAsk.AdviceRule + Modes.ProfileNote(), user, false, false, null, my.Token)).Trim();

            // The technical check: the answer against the sources.
            status.Text = "🔎 Technical check — your AI re-reads its answer against the sources…";
            string checkLine = ""; var checks = new List<(string Claim, string Status, string Src, string Note)>();
            try
            {
                var j = Ai.ParseJson(await Ai.Chat(CheckRules, "SOURCES:\n" + block + "\nANSWER:\n" + ans, false, true, null, my.Token));
                foreach (var c in (j["checks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    checks.Add((c["claim"]?.ToString() ?? "", c["status"]?.ToString() ?? "", c["source"]?.ToString() ?? "", c["note"]?.ToString() ?? ""));
                var revised = j["answer"]?.ToString() ?? "";
                if (revised.Trim().Length > 40) ans = revised.Trim();
                checkLine = $"🔎 Technical check: {checks.Count(c => c.Status == "ok")} ✓ supported · {checks.Count(c => c.Status == "partial")} ⚠ partly · {checks.Count(c => c.Status == "unsupported")} removed (not found in your sources)";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { checkLine = "🔎 Technical check not done (" + e.Message + ") — read the answer with care."; }

            lastAnswer = ans; lastPrompt = p; lastSources = sources;
            history += $"Prompt: {p}\nAnswer: {Cut(ans, 2500)}\n";
            History.Add("ask", Ai.Active()?.Id ?? "", "", "ask-email", anchor.Subject + " · " + p, ans);
            Render(Html(ans, checkLine, checks, sources, docNote));
            bottom.Visible = true;
            status.Text = $"✓ Answer from {sources.Count} sources · {Ai.Active().Label} — click a [source] to open it" + (docNote.Length > 0 ? " · " + docNote : "");
        }
        catch (OperationCanceledException) { status.Text = "■ Stopped"; }
        catch (Exception e) { status.Text = "⚠ " + e.Message; Log.Error("ask about email", e); }
        finally { askBtn.Enabled = true; }
    }

    // ------------------------------------------------------------------ showing the answer (sources are links)

    static string Esc(string s) => WebUtility.HtmlEncode(s ?? "");

    string Html(string ans, string checkLine, List<(string Claim, string Status, string Src, string Note)> checks, List<AskSource> sources, string docNote)
    {
        var byId = sources.ToDictionary(s => s.Id);
        string Links(string line) => Regex.Replace(Esc(line), @"\[((?:[EAD]\d{1,2})(?:\s*,\s*[EAD]\d{1,2})*)\]", m =>
            string.Join(" ", m.Groups[1].Value.Split(',').Select(x => x.Trim()).Select(id => byId.TryGetValue(id, out var s)
                ? $"<a class=src href=\"https://nova.src/{id}\" title=\"{Esc(s.Title)}\">{id}</a>" : $"[{id}]")));
        var sb = new StringBuilder();
        bool advice = false;
        foreach (var raw in ans.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Contains("🎓")) { advice = true; sb.Append("<div class=advice><b>").Append(Esc(line)).Append("</b>"); continue; }
            if (line.Trim().Length == 0) { sb.Append(advice ? "<br>" : "<p></p>"); continue; }
            var html = Regex.Replace(Links(line), @"\*\*(.+?)\*\*", "<b>$1</b>");
            html = Regex.Replace(html, @"^#{1,4}\s*(.+)$", "<b class=h>$1</b>");
            var cls = line.TrimStart().StartsWith("⚠") ? " class=warn" : line.Contains("Not found in your documents", StringComparison.OrdinalIgnoreCase) ? " class=nf" : "";
            sb.Append($"<div{cls}>").Append(html).Append("</div>");
        }
        if (advice) sb.Append("</div>");
        sb.Append($"<div class=check>{Esc(checkLine)}");
        if (checks.Count > 0)
        {
            sb.Append("<details><summary>show each statement</summary><ul>");
            foreach (var c in checks)
                sb.Append($"<li class={(c.Status == "ok" ? "ok" : c.Status == "partial" ? "part" : "no")}>{(c.Status == "ok" ? "✓" : c.Status == "partial" ? "⚠" : "✗")} {Esc(c.Claim)} {Links(c.Src)} <span class=muted>{Esc(c.Note)}</span></li>");
            sb.Append("</ul></details>");
        }
        sb.Append("</div>");
        if (docNote.Length > 0) sb.Append($"<div class=muted>{Esc(docNote)}</div>");
        sb.Append("<div class=sources><b>SOURCES</b>");
        foreach (var s in sources)
            sb.Append($"<div><a class=src href=\"https://nova.src/{s.Id}\">{s.Id}</a> {Esc(s.Title)}</div>");
        sb.Append("</div><div class=sign>Designed by A.HAMROUNI</div>");
        return sb.ToString();
    }

    void Render(string body)
    {
        lastHtml = "<!doctype html><html><head><meta charset=utf-8><style>" +
            "body{background:#12151C;color:#ECEEF3;font:14.5px 'Segoe UI',Arial,sans-serif;line-height:1.55;margin:6px 4px 30px}" +
            "div{margin:2px 0}p{margin:8px 0}.h{font-size:15.5px;color:#fff}b{color:#fff}.muted{color:#9AA3B5}" +
            "a.src{display:inline-block;background:#2E2A55;color:#C9C2FF;border-radius:4px;padding:0 6px;font-size:12px;font-weight:bold;text-decoration:none;margin:0 1px}" +
            "a.src:hover{background:#7C6CFF;color:#fff}.warn{color:#FFC27A}.nf{color:#FF9A9A}" +
            ".advice{background:#15241E;border-left:3px solid #3FAF84;color:#BFEAD6;padding:10px 14px;border-radius:6px;margin:14px 0}" +
            ".check{background:#1A1F2C;border-radius:6px;padding:10px 14px;margin:14px 0;color:#DDE2EE}details{margin-top:6px}summary{cursor:pointer;color:#B9B0FF}" +
            "li.ok{color:#BFEAD6}li.part{color:#FFC27A}li.no{color:#FF9A9A;text-decoration:line-through}" +
            ".sources{border-top:1px solid #2A3040;margin-top:16px;padding-top:10px;font-size:13px;color:#C9CED8}.sources div{margin:4px 0}" +
            ".sign{text-align:right;color:#7C6CFF;font-weight:bold;font-size:12px;margin-top:18px}</style></head><body>" + body + "</body></html>";
        if (web?.CoreWebView2 != null) web.CoreWebView2.NavigateToString(lastHtml);
    }

    async Task EnsureWeb()
    {
        if (web != null) return;
        var w = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Th.Bg };
        webHost.Controls.Add(w);
        try { await w.EnsureCoreWebView2Async(await HubForm.Env()); }
        catch (Exception e) { status.Text = "⚠ Needs Microsoft Edge WebView2: " + e.Message; return; }
        w.CoreWebView2.Settings.IsScriptEnabled = false;
        w.CoreWebView2.Settings.IsStatusBarEnabled = false;
        w.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            var m = Regex.Match(e.Uri, @"nova\.src/([EAD]\d+)");
            if (m.Success) OpenSource(m.Groups[1].Value);
            else if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Shell.Open(e.Uri);
        };
        web = w;
        if (lastHtml.Length > 0) w.CoreWebView2.NavigateToString(lastHtml);
    }

    void OpenSource(string id)
    {
        var s = lastSources.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        try
        {
            if (s.Mail != null) { Outlook.Open(s.Mail.Entry, s.Mail.StoreId); return; }
            if (s.Path.Length > 0) Opener.Open(new FileHit { Path = s.Path }, new PieceHit { Loc = s.Loc, Goto = s.Goto, Text = "", Matches = new() }, "");
        }
        catch (Exception e) { status.Text = "⚠ Could not open it: " + e.Message; }
    }

    // ------------------------------------------------------------------ actions

    async void DraftReply()
    {
        if (lastAnswer.Length == 0 || anchor == null) return;
        var extra = Prompt.AskLong(this, "✦ Draft reply from this answer", "✎ Anything to add for the reply? (optional — tone, length, who to copy, what to ask for)", "e.g. Polite, firm, ask them to resubmit the product data with the UV and fire test reports by Thursday.");
        if (extra == null) return;
        status.Text = "✦ Writing the reply…";
        try
        {
            var clean = Regex.Replace(lastAnswer, @"\s*\[(?:[EAD]\d{1,2})(?:\s*,\s*[EAD]\d{1,2})*\]", "");
            clean = Regex.Replace(clean, @"🎓[\s\S]*$", "").Trim();
            var d = await Assistant.Draft(anchor, "Base the reply on this checked technical answer (keep its facts and values; do not add new facts): \n" + clean + (extra.Trim().Length > 0 ? "\n\nThe user's instructions for the reply: " + extra : ""), CancellationToken.None);
            Outlook.Reply(anchor.Entry, anchor.StoreId, d, true);
            status.Text = "✓ The reply is open in Outlook (Reply All) above your signature — check it, then press Send.";
        }
        catch (Exception e) { status.Text = "⚠ " + e.Message; }
    }

    void SaveWord()
    {
        if (lastAnswer.Length == 0) return;
        try
        {
            var dir = Prompts.Folder;
            var name = Regex.Replace(anchor.Subject ?? "Answer", @"[\\/:*?""<>|]+", " ").Trim();
            if (name.Length > 60) name = name[..60];
            var path = Path.Combine(dir, $"{name} — Nova answer {DateTime.Now:dd MMM yyyy HHmm}.docx");
            var src = string.Join("\n", lastSources.Select(s => $"[{s.Id}] {s.Title}"));
            Docx.Write(path, anchor.Subject ?? "", lastPrompt, lastAnswer + "\n\nSOURCES\n" + src, "Designed by A.HAMROUNI");
            Shell.Open(path);
            status.Text = "⇩ Saved: " + path;
        }
        catch (Exception e) { status.Text = "⚠ " + e.Message; }
    }
}

static class ControlsExt
{
    public static T Add2<T>(this Control.ControlCollection c, T ctl) where T : Control { c.Add(ctl); return ctl; }
}
