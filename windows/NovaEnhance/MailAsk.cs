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
/// ✦ Ask Nova about this email — a continuous conversation, like a web AI chat. The email stays pinned at the top; questions and
/// answers build up in the middle; your input is at the bottom. Nova reads this email, its attachments, the earlier emails of the
/// conversation and with the same references, and your project folder, answers with sources you can open, then checks its own
/// answer against them. Every conversation is saved (🕘 Chat history). Designed by A.HAMROUNI
/// </summary>
class MailAskForm : NovaForm
{
    MailHit anchor;
    string fullBody = "";
    readonly Stack top;
    readonly Label head, meta, folderLbl, status;
    readonly FlowLayoutPanel atts;
    readonly TextBox prompt;
    readonly Button sendBtn;
    readonly Panel webHost;
    Microsoft.Web.WebView2.WinForms.WebView2 web;
    CancellationTokenSource cts;
    bool gathered, busy;
    readonly List<AskSource> thread = new();
    readonly List<(string Name, string Path, Seg Seg)> attPool = new();
    Conversation conv;
    string pendingHtml;

    public MailAskForm()
    {
        Text = "Nova Enhance · ✦ Ask Nova about this email";
        Width = Math.Max(900, Store.Int("win_mailask_w", 1150)); Height = Math.Max(640, Store.Int("win_mailask_h", 880));
        MinimumSize = new Size(760, 560);
        top = new Stack { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 10, 16, 6) };
        webHost = new Panel { Dock = DockStyle.Fill, BackColor = Th.Bg };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 116, BackColor = Color.FromArgb(0x0E, 0x11, 0x17), Padding = new Padding(16, 8, 16, 6) };
        Controls.Add(webHost); Controls.Add(bottom); Controls.Add(top);

        var hr = top.Add(Th.Row());
        hr.Controls.Add(Th.L("✦ Ask Nova about this email", 13, Th.Text, true));
        hr.Controls.Add(Th.B("🕘 Chat history", Screens.ChatHistory));
        hr.Controls.Add(Th.B("＋ New chat", NewChat));
        hr.Controls.Add(Th.B("⇩ Word", SaveWord));
        head = top.Add(Th.Wrap("", 11, Th.Text), true);
        head.Font = Th.F(11, true);
        meta = top.Add(Th.Wrap("", 9, Th.Muted), true);
        atts = top.Add(Th.Row());
        var fr = top.Add(Th.Row(false));
        folderLbl = Th.L("", 9.5f, Th.Text);
        folderLbl.Margin = new Padding(0, 7, 8, 0);
        fr.Controls.Add(folderLbl);
        fr.Controls.Add(Th.B("📁 Choose…", ChooseFolder));
        fr.Controls.Add(Th.B("No folder", () => SetFolder("")));
        fr.Controls.Add(Th.B("📂 Find a file", () => { var n = Revisions.Numbers(Outlook.SelectedText()); Screens.FindFile(n.FirstOrDefault()); }));
        status = top.Add(Th.Wrap("", 9.5f, Th.AccentText), true);

        // Your input, always at the bottom: Enter = send · Shift+Enter = new line.
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        prompt = Th.Prose(Th.Box(true, 4));
        prompt.Dock = DockStyle.Fill; prompt.Margin = new Padding(0, 0, 8, 0);
        prompt.PlaceholderText = "✎ Write anything — what to check, focus on or leave out, the format, length, tone and language. This email stays the reference.   (Enter = send · Shift+Enter = new line)";
        prompt.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Ask(); } };
        var btns = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        sendBtn = Th.B("✦ Send", Ask, true);
        sendBtn.Width = 110;
        btns.Controls.Add(sendBtn);
        var stop = Th.B("■ Stop", () => cts?.Cancel()); stop.Width = 110;
        btns.Controls.Add(stop);
        row.Controls.Add(prompt, 0, 0);
        row.Controls.Add(btns, 1, 0);
        bottom.Controls.Add(row);

        ResizeEnd += (_, _) => { if (WindowState == FormWindowState.Normal) { Store.SetInt("win_mailask_w", Width); Store.SetInt("win_mailask_h", Height); } };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; cts?.Cancel(); Hide(); } };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        Shown += async (_, _) => { await EnsureWeb(); prompt.Focus(); };
    }

    // ------------------------------------------------------------------ the email

    /// <summary>Opens for an email — a new conversation; nothing from the previous email stays.</summary>
    public void ShowMail(MailHit h, Conversation resume = null)
    {
        cts?.Cancel();
        anchor = h;
        gathered = false; thread.Clear(); attPool.Clear(); allowDocs = false;
        fullBody = h?.Body ?? "";
        conv = resume;
        status.Text = "";
        prompt.Text = "";
        if (h == null) { head.Text = "No email selected — select an email in Outlook, then press ✦ Ask Nova about this email again."; meta.Text = ""; atts.Controls.Clear(); Draw(); return; }
        head.Text = "✉ " + h.Subject;
        meta.Text = $"{(h.Sent ? "You" : h.FromName)} → {h.To}" + (h.Cc.Length > 0 ? $" · Cc: {h.Cc}" : "") + $" · {h.Date:ddd dd MMM yyyy HH:mm}";
        atts.Controls.Clear();
        foreach (var n in (h.AttNames ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => !Regex.IsMatch(n, @"^image\d*\.(png|jpe?g|gif)$", RegexOptions.IgnoreCase)))
        {
            var nn = n;
            var b = Th.B("📎 " + n, () => MailPreview.PreviewAttachment(anchor, nn, this));
            b.Font = Th.F(9);
            atts.Controls.Add(b);
        }
        // Right-click on the email header: the usual email menu.
        foreach (Control c in new Control[] { head, meta }) EmailMenu.Hook(c, h, pt => { EmailMenu.Show(h, this, pt); return true; });
        SetFolder(resume?.Folder is { Length: > 0 } rf ? rf : FolderFor(h), false);
        top.FitAll();
        Draw();
        prompt.Focus();
    }

    /// <summary>From the Outlook panel: the email selected (or open) in Outlook right now.</summary>
    public async void LoadCurrent(string subjectHint)
    {
        head.Text = "Reading the email from Outlook…";
        try
        {
            selText = await Sta(() => Outlook.SelectedText());
            var h = await Sta(() => Outlook.CurrentMail());
            if (h == null && subjectHint.Length > 0)
                h = MailIndex.Search(new MailQuery { Phrase = subjectHint, Limit = 1 }).FirstOrDefault();
            ShowMail(h);
        }
        catch (Exception e) { ShowMail(null); status.Text = "⚠ " + e.Message; Log.Error("ask about email", e); }
    }

    /// <summary>↻ Continue a saved conversation from 🕘 Chat history.</summary>
    public void Resume(string path)
    {
        var c = Conversations.Load(path);
        if (c == null) { status.Text = "⚠ Could not read that conversation."; return; }
        var h = MailIndex.ByEntry(c.Ref);
        if (h == null) h = new MailHit { Entry = c.Ref, StoreId = c.RefStore, Subject = c.Title, Date = c.Created };
        else if (string.IsNullOrEmpty(h.StoreId)) h.StoreId = c.RefStore;
        ShowMail(h, c);
        status.Text = $"↻ Continuing the conversation of {c.Created:ddd dd MMM HH:mm} — write your next question below.";
    }

    void NewChat()
    {
        cts?.Cancel();
        conv = null;
        status.Text = "＋ New conversation on the same email.";
        prompt.Text = "";
        Draw();
        prompt.Focus();
    }

    // ------------------------------------------------------------------ project folder (remembered per project)

    static Dictionary<string, string> Map()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(Store.Str("win_proj_folders", "{}")) ?? new(); } catch { return new(); }
    }

    string projectKey = "";
    string folder = "";

    Project proj;
    string selText = "";
    bool allowDocs;

    string FolderFor(MailHit h)
    {
        // 📁 Projects (Settings): found from the codes in the subject and text.
        proj = Projects.ForMail(h);
        if (proj != null && proj.Folders.Count > 0) { projectKey = proj.Code; return proj.Folders[0]; }
        projectKey = Prompts.ProjectOf(h);
        return Map().TryGetValue(projectKey, out var f) ? f : "";
    }

    /// <summary>The project's folders and the general "_" reference folders (or just the folder you chose).</summary>
    List<string> AllFolders()
    {
        var l = new List<string>();
        if (folder.Length > 0) l.Add(folder);
        if (proj != null && proj.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) l.AddRange(Projects.FoldersOf(proj));
        return l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
        var extra = proj != null && proj.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase) ? AllFolders().Count - 1 : 0;
        folderLbl.Text = folder.Length > 0
            ? $"📁 Project {(proj != null && extra >= 0 && proj.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase) ? $"{proj.Code} · {proj.Name} (found automatically)" : $"documents ({projectKey})")}: {GDrive.Display(folder)}" + (extra > 0 ? $" + {extra} more folder(s) (general reference)" : "")
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
        var dir = Path.Combine(Paths.Cache, "ask-mail", Math.Abs((anchor.Entry ?? "").GetHashCode()).ToString());
        foreach (var n in (anchor.AttNames ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ct.ThrowIfCancellationRequested();
            if (Regex.IsMatch(n, @"^image\d*\.(png|jpe?g|gif)$", RegexOptions.IgnoreCase)) continue;
            try
            {
                status.Text = "Reading " + n + "…";
                var path = await MailPreview.SaveAttachment(anchor, n, dir);
                if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    // 🗜 A zip (e.g. a shop-drawing package): unpacked on this PC and every readable file inside is read.
                    foreach (var (inner, ipath) in await Task.Run(() => Unzip(path, Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + " (unzipped)"), ct)))
                    {
                        ct.ThrowIfCancellationRequested();
                        status.Text = $"Reading {n} › {inner}…";
                        try
                        {
                            var isegs = await Sta(() => FileText.Extract(ipath));
                            foreach (var s in isegs.Where(s => s.Text.Trim().Length > 20)) attPool.Add(($"{n} › {inner}", ipath, s));
                        }
                        catch (Exception e) { Log.Warn("ask about email", $"{n} › {inner}: {e.Message}"); }
                    }
                    continue;
                }
                var segs = await Sta(() => FileText.Extract(path));
                foreach (var s in segs.Where(s => s.Text.Trim().Length > 20)) attPool.Add((n, path, s));
            }
            catch (Exception e) { Log.Warn("ask about email", n + ": " + e.Message); }
        }
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
        if (thread.Count > 0) meta.Text += $" · {thread.Count} earlier email{(thread.Count == 1 ? "" : "s")} found";
    }

    static readonly string[] Readable = { ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".xlsm", ".csv", ".pptx", ".txt", ".rtf", ".msg", ".eml", ".png", ".jpg", ".jpeg", ".tif", ".tiff" };

    /// <summary>Unpacks a zip safely (no paths outside the folder, at most 60 readable files and 800 MB).</summary>
    static List<(string Name, string Path)> Unzip(string zip, string to, CancellationToken ct)
    {
        var res = new List<(string, string)>();
        Directory.CreateDirectory(to);
        var root = Path.GetFullPath(to) + Path.DirectorySeparatorChar;
        long total = 0;
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        foreach (var e in z.Entries.OrderBy(e => e.FullName))
        {
            ct.ThrowIfCancellationRequested();
            if (e.Length == 0 || e.FullName.EndsWith("/")) continue;
            if (!Readable.Contains(Path.GetExtension(e.Name).ToLowerInvariant())) continue;
            total += e.Length;
            if (total > 800L * 1024 * 1024 || res.Count >= 60) break;
            var dest = Path.GetFullPath(Path.Combine(to, e.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (!File.Exists(dest) || new FileInfo(dest).Length != e.Length) System.IO.Compression.ZipFileExtensions.ExtractToFile(e, dest, true);
            res.Add((e.FullName.Replace('/', ' ').Trim() is var nm && nm.Length > 0 ? e.FullName.Replace('/', '›') : e.Name, dest));
        }
        return res;
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
        "Be technically precise: exact values, units, standards, clause numbers as written in the sources. This is a conversation: earlier questions and answers are given for context.";

    const string CheckRules =
        "You are a strict technical checker. Compare the ANSWER with the SOURCES, statement by statement (ignore the 🎓 ARCHITECT'S ADVICE section). Reply with JSON only: " +
        "{\"checks\":[{\"claim\":\"short statement\",\"status\":\"ok|partial|unsupported\",\"source\":\"[A1]\",\"note\":\"short reason\"}]," +
        "\"answer\":\"the same answer with every unsupported statement removed, and each partly supported one followed by ' ⚠ (partly supported: reason)'. Keep the wording, the [citations], the ⚠ CONFLICT lines and the 🎓 section otherwise unchanged.\"}";

    async void Ask()
    {
        if (busy) return;
        if (anchor == null) { status.Text = "No email — open Nova from an email."; return; }
        Speller.FixBeforeSearch(prompt, Ask);
        var p = prompt.Text.Trim();
        if (p.Length == 0) p = "Give me the key technical points of this email, what they ask from me, and a solid technical answer.";
        using var privateWork = Ai.Private();
        if (Ai.Current() == null) { status.Text = "Add an AI in Settings → AI & API keys."; return; }
        cts?.Cancel();
        var my = cts = new CancellationTokenSource();
        busy = true; sendBtn.Enabled = false;
        conv ??= new Conversation { Kind = "email", Title = anchor.Subject ?? "Email", Ref = anchor.Entry ?? "", RefStore = anchor.StoreId ?? "" };
        conv.Folder = folder;
        var history = string.Join("\n", conv.Msgs.TakeLast(6).Select(m => (m.Role == "user" ? "User: " : "Nova: ") + Cut(m.Text, 1500)));
        var userMsg = new ConvMsg { Role = "user", Text = p };
        conv.Msgs.Add(userMsg);
        prompt.Text = "";
        Draw("✦ Nova is reading the email and its sources…");
        try
        {
            using var doing = Log.Doing("Ask about email · " + anchor.Subject);
            await Gather(my.Token);

            status.Text = "✦ Understanding your prompt…";
            var lastUser = conv.Msgs.Where(m => m.Role == "user").Reverse().Skip(1).FirstOrDefault()?.Text;
            var terms = await FolderAsk.Expand(p + " " + Outstanding.NormSubject(anchor.Subject ?? ""), lastUser, my.Token);
            var words = terms.SelectMany(t => Regex.Split(t.ToLowerInvariant(), @"\W+")).Where(w => w.Length > 2).Distinct().ToList();

            var sources = new List<AskSource> { new() { Id = "E0", Mail = anchor, Title = $"This email · {(anchor.Sent ? "You" : anchor.FromName)} · {anchor.Date:dd MMM yyyy}", Text = Cut(fullBody, 9000) } };
            sources.AddRange(thread);
            int a = 1;
            var firstOf = attPool.GroupBy(x => x.Name).Select(g => g.First());
            var ranked = attPool.Select(x => (x, score: words.Count(w => x.Seg.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))).Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.x);
            foreach (var x in firstOf.Concat(ranked).Distinct().Take(14))
                sources.Add(new AskSource { Id = "A" + a++, Title = $"{x.Name} · {x.Seg.Loc}", Text = Cut(x.Seg.Text, 1400), Path = x.Path, Goto = x.Seg.Goto, Loc = x.Seg.Loc });

            string docNote = "", drawLine = "";
            int d = 1;
            var folders = AllFolders();
            if (folders.Count > 0 && FolderAsk.Sees == "never" && !ConfirmSend.LocalHandlesPrivate && !allowDocs)
            {
                var dwAsk = MessageBox.Show(this, "⚠ Settings → Folder search is set so that the AI never reads your files — so Nova can't check the project documents or the drawings in this email.\n\nAllow it for this conversation only?\n(Yes = allow now · No = answer from the emails only. To change it for good: Settings → Folder search.)",
                    "Nova Enhance · project documents", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                allowDocs = dwAsk == DialogResult.Yes;
            }
            var docsOk = folders.Count > 0 && (FolderAsk.Sees != "never" || ConfirmSend.LocalHandlesPrivate || allowDocs);
            if (folders.Count > 0 && !docsOk) docNote = "Project documents not sent (Settings → Folder search: the AI doesn't read your files).";

            // 📐 Drawing / document numbers in the email, its subject, or the text you highlighted in Outlook: their latest PDF.
            if (docsOk)
            {
                var dwSel = await Sta(() => Outlook.SelectedText());
                if (dwSel.Length > 0) selText = dwSel;
                var dwNums = Revisions.Numbers((selText.Length > 0 ? selText : "") + "\n" + p);
                if (dwNums.Count == 0) dwNums = Revisions.Numbers((anchor.Subject ?? "") + "\n" + fullBody);
                dwNums = dwNums.Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToList();
                if (dwNums.Count > 0)
                {
                    status.Text = $"📐 Looking for {dwNums.Count} drawing / document number(s) in the project folders…";
                    var dwFound = new List<string>(); var dwMissing = new List<string>();
                    foreach (var dn in dwNums)
                    {
                        my.Token.ThrowIfCancellationRequested();
                        var dwHits = await Task.Run(() => Projects.FindDoc(dn, folders));
                        var dwBest = dwHits.FirstOrDefault(x => x.Status == "latest");
                        if (dwBest == null) { dwMissing.Add(dn + (dwHits.Any(x => x.Cad) ? " (only CAD)" : "")); continue; }
                        var dwOlder = dwHits.Count(x => x.Status == "superseded");
                        dwFound.Add($"{dn} → {Path.GetFileName(dwBest.Path)}" + (dwBest.Rev.Length > 0 ? $" (rev {dwBest.Rev}, latest)" : "") + (dwOlder > 0 ? $" · {dwOlder} older superseded" : ""));
                        List<Seg> dwSegs;
                        try { status.Text = $"📐 Reading {Path.GetFileName(dwBest.Path)}…"; dwSegs = (await Task.Run(() => Projects.Read(dwBest.Path, my.Token), my.Token)).Segs; } catch (OperationCanceledException) { throw; } catch { dwSegs = new(); }
                        var dwText = string.Join("\n", dwSegs.Select(x => x.Text)).Trim();
                        sources.Add(new AskSource { Id = "D" + d++, Title = $"📐 {dn} · {Path.GetFileName(dwBest.Path)} — {Revisions.KindOf(dwBest.Path)} · latest" + (dwBest.Rev.Length > 0 ? " rev " + dwBest.Rev : ""), Text = Cut(dwText.Length > 0 ? dwText : "(drawing with no readable text — title only)", 1800), Path = dwBest.Path, Goto = dwSegs.FirstOrDefault()?.Goto ?? "", Loc = dwSegs.FirstOrDefault()?.Loc ?? "" });
                    }
                    drawLine = $"📐 **Drawings referred to: found {dwFound.Count} of {dwNums.Count}**\n" + string.Join("\n", dwFound.Select(x => "• ✓ " + x).Concat(dwMissing.Select(x => "• ✗ " + x + " — not found in " + string.Join(", ", folders.Select(Path.GetFileName))))) + "\n\n";
                }
            }

            // 🔎 One Nova search: project documents (by name, then only the likely files opened — remembered for all features),
            // related emails beyond this thread, 🧠 Nova Memory and past answers in 🕘 Chat history.
            var listed = docsOk ? folders.Where(Projects.Covers).ToList() : new List<string>();
            {
                var already = new HashSet<string>(sources.Where(s => s.Path is { Length: > 0 }).Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
                var exclude = new HashSet<string>(sources.Where(s => s.Mail != null).Select(s => s.Mail.Entry ?? ""));
                var ui = SynchronizationContext.Current;
                var nsOpts = new NovaSearch.Opts { Folders = listed, Project = proj, Docs = listed.Count > 0, Drawings = false, ExcludeConv = anchor.Conv ?? "", ExcludeEntries = exclude, MaxEmails = 4 };
                var ns = await Task.Run(() => NovaSearch.Run(p + " " + Outstanding.NormSubject(anchor.Subject ?? ""), terms, nsOpts,
                    stMsg => ui?.Post(_ => { if (!status.IsDisposed) status.Text = stMsg; }, null), my.Token), my.Token);
                foreach (var x in ns.Docs.Where(x => !already.Contains(x.Path) || (x.Loc ?? "").Length > 0))
                {
                    var tag = string.Join(" · ", new[] { Revisions.KindOf(x.Path), Revisions.Tag(x.Root ?? "", x.Path) }.Where(t => t.Length > 0));
                    sources.Add(new AskSource { Id = "D" + d++, Title = FolderAsk.Label(x) + (tag.Length > 0 ? " — " + tag : ""), Text = Cut(x.Text, 1400), Path = x.Path, Goto = x.Goto ?? "", Loc = x.Loc ?? "" });
                }
                int e = sources.Count(s => s.Id.StartsWith("E"));
                foreach (var x in ns.Emails)
                {
                    var parts = (x.Remote ?? "").Split('|');
                    var mh = parts.Length >= 2 ? MailIndex.ByEntry(parts[1]) : null;
                    sources.Add(new AskSource { Id = "E" + e++, Mail = mh, Title = x.Loc, Text = Cut(x.Text, 1500) });
                }
                int mi = 1;
                foreach (var x in ns.Memory) sources.Add(new AskSource { Id = "M" + mi++, Title = x.Loc, Text = Cut(x.Text, 1800) });
                int hi = 1;
                foreach (var x in ns.History) sources.Add(new AskSource { Id = "H" + hi++, Title = x.Loc, Text = Cut(x.Text, 1500) });
            }

            foreach (var folder in docsOk ? folders.Except(listed, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>())
            {
                if (!Directory.Exists(folder) && !GDrive.IsRoot(folder)) docNote = "The project folder is not reachable: " + folder;
                else
                {
                    status.Text = "Reading the project folder (only new or changed files)…";
                    var pg = new FolderIndex.Progress();
                    var ui = new System.Windows.Forms.Timer { Interval = 700 };
                    ui.Tick += (_, _) => status.Text = $"Reading the project folder… {pg.Done:N0} of {pg.Files:N0} files" + (pg.Current is { Length: > 0 } c ? " · " + c : "");
                    ui.Start();
                    try { await Sta(() => { FolderIndex.Search(folder, null, null, _ => { }, null, pg, my.Token); return true; }); }
                    finally { ui.Stop(); ui.Dispose(); }
                    var ps = await Task.Run(() => FolderIndex.Retrieve(folder, terms, folders.Count > 1 ? 8 : 14, 3, Memory.Boosts(terms), false));
                    foreach (var x in ps)
                    {
                        var tag = string.Join(" · ", new[] { Revisions.KindOf(x.Path), Revisions.Tag(folder, x.Path) }.Where(t => t.Length > 0));
                        sources.Add(new AskSource { Id = "D" + d++, Title = FolderAsk.Label(x) + (tag.Length > 0 ? " — " + tag : ""), Text = Cut(x.Text, 1400), Path = x.Path, Goto = x.Goto ?? "", Loc = x.Loc ?? "" });
                    }
                }
            }
            my.Token.ThrowIfCancellationRequested();

            // Local models have a small memory: the least important sources are left out to fit.
            var max = Ai.MaxChars;
            while (sources.Sum(s => s.Text.Length + s.Title.Length + 10) > max && sources.Count > 1)
            {
                var drop = sources.LastOrDefault(s => s.Id.StartsWith("D")) ?? sources.LastOrDefault(s => s.Id.StartsWith("A")) ?? sources.LastOrDefault(s => s.Id != "E0");
                if (drop == null) break;
                sources.Remove(drop);
            }
            var block = new StringBuilder();
            foreach (var s in sources) block.Append('[').Append(s.Id).Append("] ").Append(s.Title).Append('\n').Append(s.Text).Append("\n\n");
            if (!Store.AiSeesFull && !ConfirmSend.LocalHandlesPrivate)
            {
                var nE = sources.Count(s => s.Id.StartsWith("E")); var nA = sources.Count(s => s.Id.StartsWith("A")); var nD = sources.Count(s => s.Id.StartsWith("D"));
                var c = ConfirmSend.Ask(this, $"✦ Send this email and its sources to {Ai.LabelOf(Ai.Current())}?",
                    $"To answer, Nova sends: this email, {nE - 1} earlier email(s), {nA} attachment extract(s) and {nD} project-document extract(s) — about {block.Length:N0} characters. Then your AI checks its own answer against them.",
                    block.ToString(), "Send and answer", false);
                if (c != ConfirmSend.Choice.Send) { conv.Msgs.Remove(userMsg); prompt.Text = p; status.Text = "Cancelled — nothing was sent."; Draw(); return; }
            }

            var label = Ai.LabelOf(Ai.Current());
            status.Text = $"✦ {label} is writing the answer from {sources.Count} sources…";
            Draw($"✦ {label} is writing the answer from {sources.Count} sources…");
            var user = (history.Length > 0 ? "EARLIER IN THIS CONVERSATION:\n" + history + "\n\n" : "") + "SOURCES:\n" + block + "\nTHE USER'S PROMPT:\n" + p;
            var ans = (await Ai.Chat(AnswerRules + NovaSearch.SourceRule + " [M…] = 🧠 Nova Memory, [H…] = 🕘 an earlier conversation." + FolderAsk.AdviceRule + Modes.ProfileNote(), user, false, false, null, my.Token)).Trim();

            status.Text = "🔎 Technical check — your AI re-reads its answer against the sources…";
            Draw("🔎 Technical check — re-reading the answer against the sources…");
            var msg = new ConvMsg { Role = "nova", Ai = label };
            try
            {
                var j = Ai.ParseJson(await Ai.Chat(CheckRules, "SOURCES:\n" + block + "\nANSWER:\n" + ans, false, true, null, my.Token));
                foreach (var c in (j["checks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    msg.Checks.Add(new[] { c["claim"]?.ToString() ?? "", c["status"]?.ToString() ?? "", c["source"]?.ToString() ?? "", c["note"]?.ToString() ?? "" });
                var revised = j["answer"]?.ToString() ?? "";
                if (revised.Trim().Length > 40) ans = revised.Trim();
                msg.Check = $"🔎 Technical check: {msg.Checks.Count(c => c[1] == "ok")} ✓ supported · {msg.Checks.Count(c => c[1] == "partial")} ⚠ partly · {msg.Checks.Count(c => c[1] == "unsupported")} removed (not found in your sources)";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { msg.Check = "🔎 Technical check not done (" + e.Message + ") — read the answer with care."; }

            msg.Text = drawLine + ans + (docNote.Length > 0 ? "\n\n" + docNote : "");
            msg.Src = sources.Select(s => new ConvSrc { Id = s.Id, Title = s.Title, Path = s.Path, Goto = s.Goto, Loc = s.Loc, Entry = s.Mail?.Entry ?? "", Store = s.Mail?.StoreId ?? "" }).ToList();
            conv.Msgs.Add(msg);
            Conversations.Save(conv);
            History.Add("ask", Ai.Current()?.Id ?? "", "", "ask-email", anchor.Subject + " · " + p, ans);
            Draw();
            status.Text = $"✓ Answer from {sources.Count} sources · {label} · saved in 🕘 Chat history — click a source to open it";
        }
        catch (OperationCanceledException) { status.Text = "■ Stopped"; Draw(); }
        catch (Exception e)
        {
            status.Text = "⚠ " + e.Message; Log.Error("ask about email", e);
            conv.Msgs.Add(new ConvMsg { Role = "nova", Text = "⚠ " + e.Message });
            Draw();
        }
        finally { busy = false; sendBtn.Enabled = true; prompt.Focus(); }
    }

    // ------------------------------------------------------------------ the conversation view

    void Draw(string waiting = null)
    {
        var body = conv == null || conv.Msgs.Count == 0
            ? "<p class=muted>Write your prompt below and press Enter. Nova reads this email, its attachments, the earlier emails with the same subject or references, and your project folder — then checks its answer against them. Follow-ups keep this email as the reference. Every conversation is saved in 🕘 Chat history.</p>"
            : Conversations.Body(conv, true);
        if (waiting != null) body += $"<div class=a><div class=wait>{System.Net.WebUtility.HtmlEncode(waiting)}</div></div>";
        var html = "<!doctype html><html><head><meta charset=utf-8><style>" + Conversations.Css + "</style></head><body>" + body +
                   "<div id=end></div><script>window.scrollTo(0, document.body.scrollHeight);</script></body></html>";
        if (web?.CoreWebView2 != null) web.CoreWebView2.NavigateToString(html); else pendingHtml = html;
    }

    async Task EnsureWeb()
    {
        if (web != null) return;
        var w = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Th.Bg };
        webHost.Controls.Add(w);
        try { await w.EnsureCoreWebView2Async(await HubForm.Env()); }
        catch (Exception e) { status.Text = "⚠ Needs Microsoft Edge WebView2: " + e.Message; return; }
        w.CoreWebView2.Settings.IsStatusBarEnabled = false;
        w.CoreWebView2.Settings.AreDevToolsEnabled = false;
        w.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            var m = Regex.Match(e.Uri, @"nova\.src/(\d+)/([EAD]\d+)");
            if (m.Success) { OpenSource(int.Parse(m.Groups[1].Value), m.Groups[2].Value); return; }
            var act = Regex.Match(e.Uri, @"nova\.act/(\w+)/(\d+)");
            if (act.Success) { Act(act.Groups[1].Value, int.Parse(act.Groups[2].Value)); return; }
            if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Shell.Open(e.Uri);
        };
        web = w;
        if (pendingHtml != null) w.CoreWebView2.NavigateToString(pendingHtml); else Draw();
    }

    void OpenSource(int msg, string id)
    {
        if (conv == null || msg < 0 || msg >= conv.Msgs.Count) return;
        var s = conv.Msgs[msg].Src.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        try
        {
            if (s.Entry.Length > 0) { Outlook.Open(s.Entry, s.Store); return; }
            if (s.Path.Length > 0) Opener.Open(new FileHit { Path = s.Path }, new PieceHit { Loc = s.Loc, Goto = s.Goto, Text = "", Matches = new() }, "");
        }
        catch (Exception e) { status.Text = "⚠ Could not open it: " + e.Message; }
    }

    void Act(string what, int i)
    {
        if (conv == null || i < 0 || i >= conv.Msgs.Count) return;
        var m = conv.Msgs[i];
        var q = i > 0 ? conv.Msgs[i - 1].Text : "";
        switch (what)
        {
            case "copy": Clipboard.SetText(m.Text); status.Text = "⧉ Copied"; break;
            case "draft": DraftReply(m.Text); break;
            case "task":
                var t = Work.FromText($"{anchor?.Subject} — Nova answer", q, $"✎ {q}\n\n{m.Text}", "ask-email",
                    anchor != null ? new[] { Work.FromHit(anchor) } : null);
                Screens.Saved(t);
                break;
            case "good":
                Memory.Good(q, m.Text, anchor?.Entry ?? "", m.Src.Where(s => s.Path.Length > 0).Select(s => s.Path), new[] { m.Text }, "mail");
                status.Text = "👍 🧠 Nova remembers this answer";
                break;
            case "bad":
                Memory.Bad(q, anchor?.Entry ?? "", null, "mail");
                status.Text = "👎 Noted — tell Nova what was wrong in your next message and it will correct the answer.";
                prompt.Text = "That answer is not right: "; prompt.Focus(); prompt.SelectionStart = prompt.TextLength;
                break;
        }
    }

    async void DraftReply(string answer)
    {
        if (anchor == null) return;
        var extra = Prompt.AskLong(this, "✦ Draft reply from this answer", "✎ Anything to add for the reply? (optional — tone, length, who to copy, what to ask for)", "e.g. Polite, firm, ask them to resubmit the product data with the UV and fire test reports by Thursday.");
        if (extra == null) return;
        status.Text = "✦ Writing the reply…";
        try
        {
            using var privateWork = Ai.Private();
            var clean = Regex.Replace(answer, @"\s*\[(?:[EAD]\d{1,2})(?:\s*,\s*[EAD]\d{1,2})*\]", "");
            clean = Regex.Replace(clean, @"🎓[\s\S]*$", "").Trim();
            var d = await Assistant.Draft(anchor, "Base the reply on this checked technical answer (keep its facts and values; do not add new facts): \n" + clean + (extra.Trim().Length > 0 ? "\n\nThe user's instructions for the reply: " + extra : ""), CancellationToken.None);
            Outlook.Reply(anchor.Entry, anchor.StoreId, d, true);
            status.Text = "✓ The reply is open in Outlook (Reply All) above your signature — check it, then press Send.";
        }
        catch (Exception e) { status.Text = "⚠ " + e.Message; }
    }

    void SaveWord()
    {
        if (conv == null || conv.Msgs.Count == 0) { status.Text = "Nothing to save yet."; return; }
        try
        {
            var name = Regex.Replace(anchor?.Subject ?? "Conversation", @"[\\/:*?""<>|]+", " ").Trim();
            if (name.Length > 60) name = name[..60];
            var path = Path.Combine(Prompts.Folder, $"{name} — Nova conversation {DateTime.Now:dd MMM yyyy HHmm}.docx");
            var text = string.Join("\n\n", conv.Msgs.Select(m => (m.Role == "user" ? "YOU: " : "NOVA: ") + m.Text +
                (m.Src.Count > 0 ? "\nSources: " + string.Join("; ", m.Src.Select(s => $"[{s.Id}] {s.Title}")) : "") + (m.Check.Length > 0 ? "\n" + m.Check : "")));
            Docx.Write(path, anchor?.Subject ?? "", "Conversation with Nova", text, "Designed by A.HAMROUNI");
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
