using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>What a list window gives the conversation: every email it shows (before Nova's filter), and the ones you picked.</summary>
class TalkContext
{
    public List<MailHit> All = new();
    public List<MailHit> Focus = new();
    public string Where = "";
}

/// <summary>Nova's filter for the list: which emails to show, in which order, with a short reason each (null Order = show all).</summary>
class NovaListFilter
{
    public List<MailHit> Order;
    public Dictionary<MailHit, string> Tags = new();
    public List<string> Chips = new();
    public string Hidden = "";
}

/// <summary>
/// 💬 Talk with Nova beside a list of emails (🔍 Search for email, ☀ Morning brief): ask about the list or the emails you picked,
/// with any files (📎 attach · Ctrl+V · drop). Nova filters, sorts and explains the list as you talk ("only SLV, hide recaps,
/// most urgent first" → chips you can remove), answers technical questions on the picked emails (their text, attachments,
/// the drawings they mention, project files, 🧠 Memory, past answers), and drafts replies. Saved in 🕘 Chat history.
/// Designed by A.HAMROUNI
/// </summary>
class TalkPanel : Panel
{
    readonly string kind;
    readonly Func<TalkContext> context;
    readonly Action<NovaListFilter> apply;
    readonly Panel webHost, bottom;
    readonly TableLayoutPanel row;
    readonly FlowLayoutPanel btns;
    readonly TextBox input;
    readonly Label status;
    readonly AttachBar attach;
    Microsoft.Web.WebView2.WinForms.WebView2 web;
    string pendingHtml;
    Conversation conv;
    CancellationTokenSource cts;
    bool busy;
    List<MailHit> lastList = new();
    NovaListFilter current;
    /// <summary>The chips row ("✦ Nova filter: … ✗ · ⭐ Keep as rule") — the window puts it above its list.</summary>
    public readonly FlowLayoutPanel ChipBar;
    /// <summary>Search for email: the conversation can run a better search ([[SEARCH {"q":…}]]) — the window searches again.</summary>
    public Func<string, Task> Research;

    public TalkPanel(string kind, Func<TalkContext> context, Action<NovaListFilter> apply)
    {
        this.kind = kind; this.context = context; this.apply = apply;
        BackColor = Th.Bg;
        webHost = new Panel { Dock = DockStyle.Fill, BackColor = Th.Bg };
        bottom = new Panel { Dock = DockStyle.Bottom, Height = 150, BackColor = Color.FromArgb(0x0E, 0x11, 0x17), Padding = new Padding(10, 6, 10, 6) };
        status = new Label { Dock = DockStyle.Bottom, Height = 30, ForeColor = Th.AccentText, Font = Th.F(10), Padding = new Padding(10, 6, 10, 4), AutoEllipsis = true, BackColor = Color.FromArgb(0x0E, 0x11, 0x17) };
        row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // Three lines to type in · Enter = send · Shift+Enter = new line.
        input = Th.Prose(Th.Box(true, 3));
        input.Dock = DockStyle.Fill; input.Margin = new Padding(0, 0, 8, 0);
        input.PlaceholderText = "Ask Nova about this list or the emails you picked — e.g. which need my answer today? only SLV, hide meeting recaps · is their proposal compliant? (Enter = send · Shift+Enter = new line · 📎 drop files)";
        input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Ask(input.Text, false); } };
        btns = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0), WrapContents = false };
        var send = Th.B("✦ Send", () => Ask(input.Text, false), true); send.Width = 96;
        var stop = Th.B("■ Stop", () => cts?.Cancel()); stop.Width = 96;
        var nw = Th.B("＋ New", NewChat); nw.Width = 96;
        btns.Controls.Add(send); btns.Controls.Add(stop); btns.Controls.Add(nw);
        row.Controls.Add(input, 0, 0); row.Controls.Add(btns, 1, 0);
        bottom.Controls.Add(row);
        attach = new AttachBar { Dock = DockStyle.Top };
        bottom.Controls.Add(attach);
        Controls.Add(webHost); Controls.Add(bottom); Controls.Add(status);
        // The typing area sizes itself: the 📎 row + a full 3-line box + the buttons, at any window size.
        attach.Changed += Fit;
        Resize += (_, _) => Fit();
        attach.SizeChanged += (_, _) => Fit();
        ChipBar = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.FromArgb(0x1B, 0x18, 0x30), Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 2, 0, 6), Visible = false };
        HandleCreated += async (_, _) => { var f = FindForm(); if (f != null) attach.Hook(input, f); await EnsureWeb(); };
    }

    public void FocusInput() => input.Focus();

    bool fitting;
    void Fit()
    {
        if (fitting || bottom.ClientSize.Width <= 0) return;
        fitting = true;
        try
        {
            var w = bottom.ClientSize.Width - bottom.Padding.Horizontal;
            attach.ShortHint = w < 760;
            var ah = attach.GetPreferredSize(new Size(w, 0)).Height + attach.Margin.Vertical;
            var lines = input.Font.Height * 3 + 14;
            var bh = btns.GetPreferredSize(Size.Empty).Height;
            bottom.Height = bottom.Padding.Vertical + ah + Math.Max(lines, bh) + 4;
        }
        finally { fitting = false; }
    }

    /// <summary>💬 Ask Nova about these emails (from the selection bar or the right-click menu).</summary>
    public void AskAbout(List<MailHit> emails)
    {
        input.Text = emails.Count == 1 ? "Tell me what this email asks from me and what I should answer." : $"Tell me what these {emails.Count} emails ask from me, the most urgent first.";
        input.Focus(); input.SelectionStart = input.TextLength;
    }

    void NewChat()
    {
        cts?.Cancel(); conv = null; attach.Clear(); status.Text = "";
        Draw();
    }

    // ------------------------------------------------------------------ the page

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
            var m = Regex.Match(e.Uri, @"nova\.src/(\d+)/([EADFMH]\d+)");
            if (m.Success) { OpenSource(int.Parse(m.Groups[1].Value), m.Groups[2].Value); return; }
            var act = Regex.Match(e.Uri, @"nova\.act/(\w+)/(\d+)");
            if (act.Success) { Act(act.Groups[1].Value, int.Parse(act.Groups[2].Value)); return; }
            if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Shell.Open(e.Uri);
        };
        // Ctrl+click / middle-click on [D2] [E0] asks for a new window: handle it here instead (no "can't reach nova.src" popup).
        w.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var m = Regex.Match(e.Uri ?? "", @"nova\.src/(\d+)/([EADFMH]\d+)");
            if (m.Success) { OpenSource(int.Parse(m.Groups[1].Value), m.Groups[2].Value); return; }
            var act = Regex.Match(e.Uri ?? "", @"nova\.act/(\w+)/(\d+)");
            if (act.Success) { Act(act.Groups[1].Value, int.Parse(act.Groups[2].Value)); return; }
            if ((e.Uri ?? "").StartsWith("http", StringComparison.OrdinalIgnoreCase)) Shell.Open(e.Uri);
        };
        web = w;
        if (pendingHtml != null) w.CoreWebView2.NavigateToString(pendingHtml); else Draw();
    }

    void Draw(string waiting = null)
    {
        Conversations.DraftAction = "✦ Draft reply";
        var body = conv == null || conv.Msgs.Count == 0
            ? "<p class=muted>💬 Talk with Nova about " + (kind == "brief" ? "your brief" : "these emails") + ". Ask to <b>filter or sort</b> the list (“only SLV and Nouran, hide meeting recaps, most urgent first”), " +
              "or <b>pick emails</b> (click · Ctrl+click · Shift+click) and ask a <b>technical question</b> — Nova reads them with their attachments, the drawings they mention, your project files, 🧠 Memory and past answers. " +
              "📎 Attach, paste (Ctrl+V) or drop any file. Every conversation is saved in 🕘 Chat history.</p>"
            : Conversations.Body(conv, true);
        if (waiting != null) body += $"<div class=a><div class=wait>{System.Net.WebUtility.HtmlEncode(waiting)}</div></div>";
        var html = "<!doctype html><html><head><meta charset=utf-8><style>" + Conversations.Css + "</style></head><body>" + body +
                   "<div id=end></div><script>window.scrollTo(0, document.body.scrollHeight);</script></body></html>";
        if (web?.CoreWebView2 != null) web.CoreWebView2.NavigateToString(html); else pendingHtml = html;
    }

    // ------------------------------------------------------------------ asking

    const string ListRule =
        " THE LIST: the emails the user sees are numbered [E1]…; the user may ask you to filter, hide, keep, sort, prioritise or group them. " +
        "ONLY when the request is about the list (which ones, only, hide, without, sort, most urgent, need my reply, group…), end your answer with ONE line exactly like " +
        "[[LIST {\"show\":[\"E3\",\"E1\"],\"tags\":{\"E3\":\"answer today\",\"E1\":\"approval: proceed\"},\"chips\":[\"SLV, Nouran\",\"Needs my action\",\"Hide: meeting recaps\"],\"hidden\":\"4 meeting recaps, 3 other projects\"}]] " +
        "— show = the ids to keep, most important first; tags = 2 to 4 words of reason for each; chips = 1 to 5 short labels of the filter; hidden = what you hid. " +
        "To show everything again: [[LIST {\"clear\":true}]]. Never put the [[LIST]] line when the question is not about the list. " +
        "In your text, number the emails in the same order as show (1., 2.…) and cite them [E3]. " +
        "An email that just says \"proceed\", \"noted\", \"approved\" or thanks is an approval or information, not a question waiting for an answer. Meeting recaps and automatic notifications are FYI unless they give the user an action.";

    const string SearchRule =
        " SEARCHING AGAIN: when the list does not hold what the user asks for (or the user asks to search for something else), end your answer with ONE line exactly like " +
        "[[SEARCH {\"q\":\"emails about fire hose cabinets (FHC) in back of house (BOH) areas, stainless steel type\"}]] — a clear search question with the important ideas and their short forms; " +
        "Nova then searches the whole mailbox again with ✦ AI search and shows the new list. Never put both [[SEARCH]] and [[LIST]].";

    /// <summary>Search for email: does this message ask Nova to search (rather than to filter or discuss the list)?</summary>
    static bool WantsSearch(string q, int listCount)
    {
        var t = (q ?? "").Trim().ToLowerInvariant();
        if (Regex.IsMatch(t, @"^(please\s+)?(only|hide|without|exclude|remove|keep|sort|group|which of (these|them)|of these|from this list|in this list)\b")) return false;
        if (listCount == 0) return true;
        return Regex.IsMatch(t, @"^(please\s+|can you\s+|nova,?\s+)?(find|search|look for|look up|get me|bring|show me (all|every|the emails?)|any emails?|all emails?|emails? (about|from|regarding|on|with))\b") ||
               Regex.IsMatch(t, @"\b(search (again|for|the mailbox|all)|find (all|every|me))\b");
    }

    async Task Again(string again, string q)
    {
        try { await Research(again); } catch (Exception e) { Log.Warn("talk", "search again: " + e.Message); return; }
        Ask("The list was searched again for: " + again + ". Answer my question from the new list: " + q, true);
    }

    async void Ask(string q, bool silent, List<string> chipsOnly = null)
    {
        q = (q ?? "").Trim();
        var files = attach.Files.ToList();
        if (q.Length == 0 && files.Count > 0) q = files.Count >= 2 ? "Compare the attached files in a table, then the key differences." : "Read the attached file and tell me what matters.";
        if (q.Length == 0 || busy) return;
        if (!Store.AiSeesFull && !ConfirmSend.LocalHandlesPrivate)
        {
            status.Text = "💬 needs Settings → Email & AI → AI sees C or D (or the local AI) — Nova must read your emails to discuss them.";
            return;
        }
        busy = true; cts?.Cancel(); var my = cts = new CancellationTokenSource();
        if (!silent) input.Text = "";
        var ctx = context?.Invoke() ?? new TalkContext();
        var shown = q + (files.Count > 0 ? "\n📎 " + string.Join(" · ", files.Select(Path.GetFileName)) : "");
        var userMsg = new ConvMsg { Role = "user", Text = silent ? "✦ " + q : shown };
        conv ??= new Conversation { Kind = kind == "brief" ? "brief" : "ask", Title = (kind == "brief" ? "☀ Brief — " : "🔍 ") + (q.Length > 70 ? q[..70] : q) };
        conv.Msgs.Add(userMsg);
        // 🔍 Option A: "find / search …" (or an empty list) → Nova searches first (✦ AI search: emails, then the list follows), then answers.
        bool searchedNow = false;
        string again = null;
        if (!silent && Research != null && WantsSearch(q, ctx.All.Count))
        {
            Draw("🔍 Nova is searching your emails, project files and drawings…");
            status.Text = "🔍 Searching…";
            try { await Research(q); searchedNow = true; } catch (Exception ex) { Log.Warn("talk", "search: " + ex.Message); }
            if (my.IsCancellationRequested) { busy = false; status.Text = "■ Stopped"; Draw(); return; }
            ctx = context?.Invoke() ?? new TalkContext();
        }
        Draw("Nova is reading " + (ctx.Focus.Count > 0 ? $"the {ctx.Focus.Count} email(s) you picked, " : "") + $"the list ({ctx.All.Count}), your files and project documents…");
        try
        {
            using var _p = Ai.Private();
            var src = new List<ConvSrc>();
            var sb = new StringBuilder();
            // The list, numbered.
            lastList = ctx.All.Take(90).ToList();
            sb.Append($"THE LIST ({ctx.Where}, {ctx.All.Count} emails{(ctx.All.Count > 90 ? ", first 90" : "")}):\n");
            for (int i = 0; i < lastList.Count; i++)
            {
                var h = lastList[i];
                var st = h.Status switch { "needs" => "waiting for my reply", "week" => "waiting for my reply", "follow" => "my email, no answer yet", "fyi" => "FYI", _ => "" };
                sb.Append($"[E{i + 1}] {(h.Sent ? "Me → " + h.To : h.FromName)} · {h.Date:ddd dd MMM HH:mm} · {h.Subject}" + (st.Length > 0 ? $" · {st}" + (h.DaysWaiting > 0 ? $" {h.DaysWaiting} d" : "") : "") +
                          (h.AttNames.Length > 0 ? " · 📎 " + h.AttNames.Split(';').Length : "") + $"\n    “{h.FirstLines(220)}”\n");
                src.Add(new ConvSrc { Id = "E" + (i + 1), Title = $"{(h.Sent ? "You" : h.FromName)} · {h.Date:dd MMM} · {h.Subject}", Entry = h.Entry ?? "", Store = h.StoreId ?? "" });
            }
            sb.Append('\n');
            // The emails you picked: in full, with their attachments.
            var focus = ctx.Focus.Take(5).ToList();
            int f = 1;
            if (!silent && focus.Count > 0)
            {
                sb.Append("THE EMAILS THE USER PICKED (in full):\n");
                foreach (var h in focus)
                {
                    var id = lastList.IndexOf(h) is var k && k >= 0 ? "E" + (k + 1) : "E" + (lastList.Count + 1 + focus.IndexOf(h));
                    var body = h.Body ?? "";
                    try { var full = await Sta(() => Outlook.FullBody(h.Entry, h.StoreId)); if (full.Length > body.Length) body = full; } catch { }
                    sb.Append($"[{id}] From {h.FromName} · {h.Date:dd MMM yyyy HH:mm} · To {h.To}" + (h.Cc.Length > 0 ? " · Cc " + h.Cc : "") + $"\nSubject: {h.Subject}\n{(body.Length > 6000 ? body[..6000] : body)}\n\n");
                    if (!src.Any(s => s.Id == id)) src.Add(new ConvSrc { Id = id, Title = $"{h.FromName} · {h.Date:dd MMM} · {h.Subject}", Entry = h.Entry ?? "", Store = h.StoreId ?? "" });
                    // Attachments (not the logos / signature pictures).
                    foreach (var name in (h.AttNames ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Where(n => !Regex.IsMatch(n, @"^image\d*\.(png|jpe?g|gif)$", RegexOptions.IgnoreCase)).Take(4))
                    {
                        my.Token.ThrowIfCancellationRequested();
                        status.Text = "📎 Reading " + name + "…";
                        try
                        {
                            var dir = Path.Combine(Paths.Cache, "talk", Math.Abs((h.Entry ?? "").GetHashCode()).ToString());
                            Directory.CreateDirectory(dir);
                            var path = await MailPreview.SaveAttachment(h, name, dir);
                            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                            var txt = await FileTalk.Text(path, FindForm(), my.Token, t => status.Text = t, q);
                            var aid = "A" + f++;
                            sb.Append($"[{aid}] attachment of [{id}]: {name}\n{(txt.Length > 5000 ? txt[..5000] : txt)}\n\n");
                            src.Add(new ConvSrc { Id = aid, Title = "📎 " + name, Path = path });
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }
                }
            }
            // 📎 Files you attached.
            int fi = 1;
            foreach (var file in files)
            {
                var txt = await FileTalk.Text(file, FindForm(), my.Token, t => status.Text = t, q);
                sb.Append($"[F{fi}] attached file {Path.GetFileName(file)}\n{(txt.Length > (files.Count > 1 ? 7000 : 12000) ? txt[..(files.Count > 1 ? 7000 : 12000)] : txt)}\n\n");
                src.Add(new ConvSrc { Id = "F" + fi++, Title = "📎 " + Path.GetFileName(file), Path = file });
            }
            // 🔎 The one Nova search: project documents and drawings, other emails, 🧠 Memory, past answers.
            if (!silent)
            {
                var focusText = string.Join("\n", focus.Select(h => h.Subject + "\n" + h.FirstLines(1500)));
                var nums = Revisions.Numbers(q + "\n" + focusText).Take(6);
                var proj = Projects.ForText(q + " " + string.Join(" ", focus.Select(h => h.Subject)), focusText);
                var docsOk = FolderAsk.Sees != "never" || ConfirmSend.LocalHandlesPrivate;
                var excl = new HashSet<string>(lastList.Select(h => h.Entry ?? ""));
                var ui = SynchronizationContext.Current;
                var ns = await Task.Run(() => NovaSearch.Run(q + " " + string.Join(" ", nums), FolderAsk.LocalTerms(q + " " + string.Join(" ", focus.Select(h => h.Subject))),
                    new NovaSearch.Opts { Project = proj, ExcludeEntries = excl, MaxEmails = 4, MaxDocs = 10, MaxFiles = 5, Docs = docsOk && (focus.Count > 0 || proj != null || nums.Any()), Drawings = docsOk },
                    t => ui?.Post(_ => { if (!status.IsDisposed) status.Text = t; }, null), my.Token), my.Token);
                int d = 1, e = lastList.Count + focus.Count + 1, mi = 1, hi = 1;
                foreach (var p in ns.Drawings.Concat(ns.Docs))
                {
                    var id = "D" + d++;
                    sb.Append($"[{id}] {FolderAsk.Label(p)}\n{(p.Text.Length > 1400 ? p.Text[..1400] : p.Text)}\n\n");
                    src.Add(new ConvSrc { Id = id, Title = FolderAsk.Label(p), Path = p.Path ?? "", Loc = p.Loc ?? "", Goto = p.Goto ?? "" });
                }
                foreach (var p in ns.Emails)
                {
                    var id = "E" + e++;
                    var parts = (p.Remote ?? "").Split('|');
                    sb.Append($"[{id}] {p.Loc}\n{p.Text}\n\n");
                    src.Add(new ConvSrc { Id = id, Title = p.Loc, Entry = parts.Length > 1 ? parts[1] : "", Store = parts.Length > 2 ? parts[2] : "" });
                }
                foreach (var p in ns.Memory) { var id = "M" + mi++; sb.Append($"[{id}] {p.Loc}\n{p.Text}\n\n"); src.Add(new ConvSrc { Id = id, Title = p.Loc }); }
                foreach (var p in ns.History) { var id = "H" + hi++; sb.Append($"[{id}] {p.Loc}\n{p.Text}\n\n"); src.Add(new ConvSrc { Id = id, Title = p.Loc }); }
            }
            // The conversation so far.
            var earlier = conv.Msgs.Take(conv.Msgs.Count - 1).TakeLast(6).ToList();
            if (earlier.Count > 0)
            {
                sb.Append("EARLIER IN THIS CONVERSATION:\n");
                foreach (var m in earlier) sb.Append(m.Role == "user" ? "Q: " : "A: ").Append(m.Text.Length > 1200 ? m.Text[..1200] : m.Text).Append('\n');
                sb.Append('\n');
            }
            sb.Append(chipsOnly != null ? "REQUEST: " + q : "QUESTION: " + q);
            var text = sb.ToString();
            var max = Ai.MaxChars;
            if (max > 0 && text.Length > max) text = text[..max];
            var sys = "You are Nova, the assistant of an architect / technical coordinator in the UAE, talking with him about his emails. Use ONLY the numbered sources for facts and cite them like [E3] [A1] [D2] [F1]; " +
                      "if something isn't in them, say so. Be clear and practical: short paragraphs or numbered points. When he asks for a reply text, put exactly the text between a line ✍ START and a line ✍ END." +
                      ListRule + (searchedNow ? " The list was just searched for this request: say briefly what it found (most relevant first, cite [E#] and [D#]) and what is missing." : Research != null && !silent ? SearchRule : "") + (files.Count > 0 ? FileTalk.Rules : "") + NovaSearch.SourceRule + FolderAsk.AdviceRule + Modes.ProfileNote();
            Draw("Nova is thinking…");
            var a = await Ai.Chat(sys, text, false);
            my.Token.ThrowIfCancellationRequested();
            // [[LIST …]] → the list follows the conversation.
            var lm = Regex.Match(a, @"\[\[LIST\s*(\{.*?\})\s*\]\]", RegexOptions.Singleline);
            if (lm.Success)
            {
                a = a.Remove(lm.Index, lm.Length).Trim();
                try { ApplyList(JsonNode.Parse(lm.Groups[1].Value)?.AsObject()); } catch (Exception ex) { Log.Warn("talk", "list: " + ex.Message); }
            }
            // [[SEARCH …]] → the window searches the mailbox again with a better question.
            var sm = Regex.Match(a, @"\[\[SEARCH\s*(\{.*?\})\s*\]\]", RegexOptions.Singleline);
            if (sm.Success)
            {
                a = a.Remove(sm.Index, sm.Length).Trim();
                try { again = JsonNode.Parse(sm.Groups[1].Value)?["q"]?.ToString(); } catch (Exception ex) { Log.Warn("talk", "search: " + ex.Message); }
                if (!string.IsNullOrWhiteSpace(again) && Research != null) a += $"\n\n🔍 Searching again: “{again}”";
            }
            var cited = new HashSet<string>(Regex.Matches(a, @"\b([EADFMH]\d{1,3})\b").Select(x => x.Groups[1].Value));
            var nova = new ConvMsg { Role = "nova", Text = a, Ai = Ai.LabelOf(Ai.Current()), Src = src.Where(s => cited.Contains(s.Id)).ToList() };
            conv.Msgs.Add(nova);
            conv.Updated = DateTime.Now;
            Conversations.Save(conv);
            if (!silent) attach.Clear();
            status.Text = "";
            Draw();
        }
        catch (OperationCanceledException) { conv.Msgs.Remove(userMsg); status.Text = "■ Stopped"; Draw(); }
        catch (Exception ex) { conv.Msgs.Add(new ConvMsg { Role = "nova", Text = "⚠ " + ex.Message }); status.Text = ""; Draw(); }
        finally { busy = false; }
        if (!string.IsNullOrWhiteSpace(again) && Research != null && !my.IsCancellationRequested) _ = Again(again, q);
    }

    static Task<T> Sta<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    void ApplyList(JsonObject o)
    {
        if (o == null) return;
        if (o["clear"]?.GetValue<bool>() == true) { current = null; apply?.Invoke(null); DrawChips(); return; }
        var f = new NovaListFilter { Order = new(), Hidden = o["hidden"]?.ToString() ?? "" };
        foreach (var n in o["show"]?.AsArray() ?? new JsonArray())
        {
            var m = Regex.Match(n?.ToString() ?? "", @"(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var i) && i >= 1 && i <= lastList.Count && !f.Order.Contains(lastList[i - 1])) f.Order.Add(lastList[i - 1]);
        }
        if (o["tags"] is JsonObject tags)
            foreach (var kv in tags)
            {
                var m = Regex.Match(kv.Key, @"(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var i) && i >= 1 && i <= lastList.Count) f.Tags[lastList[i - 1]] = kv.Value?.ToString() ?? "";
            }
        foreach (var c in o["chips"]?.AsArray() ?? new JsonArray()) if (c?.ToString() is { Length: > 0 } s) f.Chips.Add(s);
        current = f;
        apply?.Invoke(f);
        DrawChips();
    }

    // ------------------------------------------------------------------ chips · ⭐ standing rules

    List<string> Rules
    {
        get { try { return JsonSerializer.Deserialize<List<string>>(Store.Str("win_talk_rules_" + kind, "[]")) ?? new(); } catch { return new(); } }
        set => Store.Set("win_talk_rules_" + kind, JsonSerializer.Serialize(value ?? new()));
    }

    void DrawChips()
    {
        ChipBar.SuspendLayout();
        foreach (Control c in ChipBar.Controls) c.Dispose();
        ChipBar.Controls.Clear();
        var rules = Rules;
        if (current == null && rules.Count == 0) { ChipBar.Visible = false; ChipBar.ResumeLayout(); return; }
        var l = Th.L("✦ Nova filter:", 9, Th.Text, true); l.Margin = new Padding(0, 7, 6, 0);
        ChipBar.Controls.Add(l);
        Button Chip(string text, Action a) { var b = Th.B(text, a); b.Font = Th.F(9); b.BackColor = Color.FromArgb(0x2E, 0x2A, 0x55); b.Margin = new Padding(2, 3, 2, 3); ChipBar.Controls.Add(b); return b; }
        foreach (var r in rules) { var rr = r; Chip("⭐ " + r + "  ✗", () => { var x = Rules; x.Remove(rr); Rules = x; Refilter(); }); }
        if (current != null)
        {
            foreach (var c in current.Chips.Where(c => !rules.Contains(c)).ToList())
            {
                var cc = c;
                Chip(c + "  ✗", () => { current.Chips.Remove(cc); Refilter(); });
            }
            if (current.Chips.Any(c => !rules.Contains(c)))
                Chip("⭐ Keep as rule", () => { var x = Rules; foreach (var c in current.Chips) if (!x.Contains(c)) x.Add(c); Rules = x; DrawChips(); status.Text = "⭐ Kept — applied each time this list loads (✗ on a ⭐ chip removes it)."; });
            if (current.Order != null)
            {
                var hid = Th.L($"{current.Order.Count} shown" + (current.Hidden.Length > 0 ? " · hidden: " + current.Hidden : ""), 9, Th.Muted);
                hid.Margin = new Padding(6, 7, 6, 0);
                ChipBar.Controls.Add(hid);
            }
            Chip("Show all  ✗", () => { current = null; apply?.Invoke(null); DrawChips(); });
        }
        ChipBar.Visible = true;
        ChipBar.ResumeLayout();
        (ChipBar.Parent as Stack)?.FitAll();
    }

    /// <summary>A chip was removed: Nova applies the remaining filters (or shows everything).</summary>
    void Refilter()
    {
        var keep = (current?.Chips ?? new()).Concat(Rules).Distinct().ToList();
        if (keep.Count == 0) { current = null; apply?.Invoke(null); DrawChips(); return; }
        current = null; apply?.Invoke(null);
        Ask("Filter the list again with ONLY these filters (forget any other): " + string.Join("; ", keep), true, keep);
    }

    /// <summary>⭐ Standing rules: applied when the list (re)loads.</summary>
    /// <summary>Puts a text in the typing area (⭐ My prompts), ready to send.</summary>
    public void Suggest(string text) { input.Text = text; input.SelectionStart = input.TextLength; }

    public void ListLoaded()
    {
        var rules = Rules;
        if (rules.Count == 0 || busy) { DrawChips(); return; }
        current = null;
        Ask("Apply these standing filters to the list: " + string.Join("; ", rules), true, rules);
    }

    // ------------------------------------------------------------------ links under the answers

    void OpenSource(int msg, string id)
    {
        if (conv == null || msg < 0 || msg >= conv.Msgs.Count) return;
        var s = conv.Msgs[msg].Src.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        try
        {
            if (s.Entry.Length > 0) { var h = MailIndex.ByEntry(s.Entry); if (h != null) MailWindow.Open(h); else Outlook.Open(s.Entry, s.Store); return; }
            if (s.Path.Length > 0) Opener.Open(new FileHit { Path = s.Path }, new PieceHit { Loc = s.Loc, Goto = s.Goto, Text = "", Matches = new() }, "");
        }
        catch (Exception e) { status.Text = "⚠ Could not open it: " + e.Message; }
    }

    MailHit FirstEmail(ConvMsg m)
    {
        var ctx = context?.Invoke();
        if (ctx?.Focus.Count > 0) return ctx.Focus[0];
        var s = m.Src.FirstOrDefault(x => x.Entry.Length > 0);
        return s == null ? null : MailIndex.ByEntry(s.Entry);
    }

    void Act(string what, int i)
    {
        if (conv == null || i < 0 || i >= conv.Msgs.Count) return;
        var m = conv.Msgs[i];
        var q = i > 0 ? conv.Msgs[i - 1].Text : "";
        var form = FindForm();
        switch (what)
        {
            case "copy": Clipboard.SetText(Regex.Replace(m.Text, @"✍ (START|END)", "").Trim()); status.Text = "⧉ Copied"; break;
            case "xlsx":
                try { var x = FileTalk.ExportXlsx(m.Text, conv.Title); Shell.Open(x); status.Text = "📊 Saved: " + x; }
                catch (Exception ex) { status.Text = "⚠ " + ex.Message; }
                break;
            case "tsv": Clipboard.SetText(FileTalk.Tsv(m.Text)); status.Text = "⧉ Table copied"; break;
            case "draft":
            {
                var h = FirstEmail(m);
                if (h == null) { status.Text = "Pick the email to reply to (click it), then ✦ Draft reply."; break; }
                var mm = Regex.Match(m.Text, @"✍ START\s*(.*?)\s*✍ END", RegexOptions.Singleline);
                if (mm.Success)
                    try { Outlook.Reply(h.Entry, h.StoreId, Regex.Replace(mm.Groups[1].Value, @"\s*\[(?:[EADFMH]\d{1,3}(?:\s*,\s*)?)+\]", ""), false); status.Text = "✦ Reply opened in Outlook — check it and send."; }
                    catch (Exception ex) { status.Text = "⚠ " + ex.Message; }
                else EmailMenu.DraftTo(h, false, form);
                break;
            }
            case "task":
            {
                var ems = m.Src.Where(s => s.Entry.Length > 0).Select(s => MailIndex.ByEntry(s.Entry)).Where(x => x != null).Take(20).Select(Work.FromHit).ToArray();
                Screens.Saved(Work.FromText($"{conv.Title} — Nova answer", q, $"✎ {q}\n\n{m.Text}", "talk", ems.Length > 0 ? ems : null));
                break;
            }
            case "good":
                Memory.Good(q, m.Text, "", m.Src.Where(s => s.Path.Length > 0).Select(s => s.Path), new[] { m.Text }, "mail");
                status.Text = "👍 🧠 Nova remembers this answer";
                break;
            case "bad":
                Memory.Bad(q, "", null, "mail");
                status.Text = "👎 Noted — tell Nova what was wrong in your next message.";
                input.Text = "That answer is not right: "; input.Focus(); input.SelectionStart = input.TextLength;
                break;
        }
    }
}
