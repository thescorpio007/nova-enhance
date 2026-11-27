using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.WinForms;

namespace NovaEnhance;

/// <summary>
/// Nova Chat: the chat page inside Nova's AI window. Uses the AI key in Nova, About me, and — when the user adds them —
/// files, a folder (through the encrypted folder index) and emails (through the encrypted mail index), with numbered sources.
/// Chats are kept in Documents\Nova Enhance\Nova Chat. Designed by A.HAMROUNI
/// </summary>
class NovaChat
{
    static string Dir { get { var p = Path.Combine(Paths.Root, "Nova Chat"); Directory.CreateDirectory(p); return p; } }
    static readonly Dictionary<long, MailHit> MailCache = new();
    static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly WebView2 view;
    readonly Form owner;
    readonly Voice voice = new();
    public string PendingText;

    public NovaChat(WebView2 view, Form owner) { this.view = view; this.owner = owner; }

    void Post(JsonObject o)
    {
        try { if (owner.InvokeRequired) owner.BeginInvoke(new Action(() => view.CoreWebView2?.PostWebMessageAsJson(o.ToJsonString()))); else view.CoreWebView2?.PostWebMessageAsJson(o.ToJsonString()); }
        catch { }
    }

    public void Insert(string text) => Post(new JsonObject { ["t"] = "insert", ["text"] = text });
    public void AskNow(string text) => Post(new JsonObject { ["t"] = "askNow", ["text"] = text });
    /// <summary>▶ Continue an interrupted deep search (🕘 Search history).</summary>
    public void ContinueDeep(string text, string id) => Post(new JsonObject { ["t"] = "continueDeep", ["text"] = text, ["resume"] = id });
    /// <summary>Opens a saved chat.</summary>
    public void OpenChat(string id) { var c = Load(id); if (c != null) Post(new JsonObject { ["t"] = "chat", ["chat"] = c }); }
    public void Attach(string path) => Post(new JsonObject { ["t"] = "files", ["paths"] = new JsonArray(path) });

    // ================================================================ chat files

    static string FileOf(string id) => Path.Combine(Dir, Regex.Replace(id ?? "", @"[^\w\-]", "") + ".json");

    static JsonObject Load(string id)
    {
        try { return File.Exists(FileOf(id)) ? JsonNode.Parse(File.ReadAllText(FileOf(id)))?.AsObject() : null; } catch { return null; }
    }

    static void Save(JsonObject chat)
    {
        chat["updated"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        File.WriteAllText(FileOf(chat["id"]!.ToString()), chat.ToJsonString(JsonOut), new UTF8Encoding(false));
    }

    static JsonArray List()
    {
        var a = new List<JsonObject>();
        foreach (var f in Directory.GetFiles(Dir, "*.json"))
        {
            try
            {
                var c = JsonNode.Parse(File.ReadAllText(f))?.AsObject();
                if (c == null) continue;
                a.Add(new JsonObject { ["id"] = c["id"]?.ToString(), ["title"] = c["title"]?.ToString() ?? "Chat", ["updated"] = c["updated"]?.GetValue<long>() ?? 0 });
            }
            catch { }
        }
        return new JsonArray(a.OrderByDescending(x => x["updated"]!.GetValue<long>()).Select(x => (JsonNode)x).ToArray());
    }

    // ================================================================ messages from the page

    public async void Handle(string json)
    {
        JsonObject m;
        try { m = JsonNode.Parse(json)?.AsObject(); } catch { return; }
        if (m == null) return;
        try
        {
            switch (m["t"]?.ToString())
            {
                case "init":
                    Post(new JsonObject { ["t"] = "init", ["ai"] = Ai.LabelOf(Ai.Current()) ?? "", ["sign"] = Program.Sign, ["chats"] = List(), ["text"] = PendingText, ["guided"] = GuidedInfo() });
                    PendingText = null;
                    break;
                case "open":
                    var c = Load(m["id"]?.ToString());
                    if (c != null) Post(new JsonObject { ["t"] = "chat", ["chat"] = c });
                    break;
                case "delete":
                    try { File.Delete(FileOf(m["id"]?.ToString())); } catch { }
                    Post(new JsonObject { ["t"] = "chats", ["chats"] = List() });
                    break;
                case "pickFolder":
                {
                    using var d = new FolderBrowserDialog { Description = "Folder for Nova Chat to read", UseDescriptionForTitle = true };
                    if (d.ShowDialog(owner) == DialogResult.OK) Post(new JsonObject { ["t"] = "folder", ["path"] = d.SelectedPath });
                    break;
                }
                case "pickFiles":
                {
                    using var d = new OpenFileDialog { Multiselect = true, Title = "Files for Nova Chat" };
                    if (d.ShowDialog(owner) == DialogResult.OK) Post(new JsonObject { ["t"] = "files", ["paths"] = new JsonArray(d.FileNames.Select(x => (JsonNode)x).ToArray()) });
                    break;
                }
                case "pasteFile":
                    // 📎 A screenshot or files pasted / dropped in the chat: saved, then attached.
                    try
                    {
                        var path = FileTalk.SaveBytes(m["name"]?.ToString() ?? "", Convert.FromBase64String(m["b64"]?.ToString() ?? ""));
                        Post(new JsonObject { ["t"] = "files", ["paths"] = new JsonArray(path) });
                    }
                    catch (Exception e) { Post(new JsonObject { ["t"] = "error", ["text"] = "Could not add the file: " + e.Message }); }
                    break;
                case "pasteClipboard":
                {
                    var got = FileTalk.FromClipboard();
                    if (got.Count > 0) Post(new JsonObject { ["t"] = "files", ["paths"] = new JsonArray(got.Select(x => (JsonNode)x).ToArray()) });
                    break;
                }
                case "exportTable":
                    try { var x = FileTalk.ExportXlsx(m["text"]?.ToString() ?? "", m["title"]?.ToString() ?? "Comparison"); Shell.Open(x); }
                    catch (Exception e) { Post(new JsonObject { ["t"] = "error", ["text"] = e.Message }); }
                    break;
                case "copyTable": Clipboard.SetText(FileTalk.Tsv(m["text"]?.ToString() ?? "") is { Length: > 0 } tsv ? tsv : " "); break;
                case "findEmail": FindEmail(m["q"]?.ToString() ?? ""); break;
                case "send":
                    sendCts?.Cancel(); sendCts = new CancellationTokenSource();
                    try { await Send(m, sendCts.Token); }
                    catch (OperationCanceledException) { Post(new JsonObject { ["t"] = "error", ["text"] = "Stopped." }); }
                    break;
                case "stop": sendCts?.Cancel(); agWait?.TrySetResult(null); break;
                case "ag.confirmed":
                {
                    List<long> Ids(string k) => (m[k] as JsonArray)?.Select(x => long.TryParse(x?.ToString(), out var v) ? v : 0).Where(v => v > 0).ToList() ?? new();
                    agWait?.TrySetResult((Ids("yes"), Ids("no"), m["note"]?.ToString() ?? ""));
                    break;
                }
                case "ag.stop": agWait?.TrySetResult(null); break;
                case "agCsv":
                {
                    var (_, a, q) = Pick(m["id"]?.ToString(), m["idx"]?.GetValue<int>() ?? -1);
                    if (a?["agResult"] is JsonObject ar)
                    {
                        var path = Agent.Csv(Agent.FromJson(ar), q ?? "Nova agent");
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
                    }
                    break;
                }
                case "agWord":
                {
                    var (_, a, q) = Pick(m["id"]?.ToString(), m["idx"]?.GetValue<int>() ?? -1);
                    if (a?["agResult"] is JsonObject ar)
                        try { Shell.Open(Export.AgentWord(Agent.FromJson(ar), q ?? "Nova agent")); }
                        catch (Exception e) { Post(new JsonObject { ["t"] = "error", ["text"] = e.Message }); }
                    break;
                }
                case "ag.toLog": Screens.SubmittalChat(m["id"]?.ToString() ?? ""); break;
                case "think.ask": Post(new JsonObject { ["t"] = "think.wait", ["text"] = Busy.Line() }); break;
                case "rowmenu": WebPage.RowMenu(m, owner); break;
                case "filemenu": WebPage.FileMenuFrom(m, owner); break;
                case "openSource": OpenSource(m["src"] as JsonObject, m["ctrl"]?.ToString() is "true" or "True"); break;
                case "copy": Clipboard.SetText(m["text"]?.ToString() ?? " "); break;
                case "saveWord": SaveWord(m["id"]?.ToString(), m["idx"]?.GetValue<int>() ?? -1); break;
                case "draftReply": DraftReply(m["id"]?.ToString(), m["idx"]?.GetValue<int>() ?? -1); break;
                case "saveTask":
                {
                    var (chat, a, q) = Pick(m["id"]?.ToString(), m["idx"]?.GetValue<int>() ?? -1);
                    if (a == null) break;
                    var links = (a["sources"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(x => x["kind"]?.ToString() == "email"
                        ? new WorkItem { Kind = "mail", Entry = x["entry"]?.ToString() ?? "", Store = x["store"]?.ToString() ?? "", Subject = x["label"]?.ToString() ?? "" }
                        : new WorkItem { Kind = "file", Path = x["path"]?.ToString() ?? "", Subject = x["label"]?.ToString() ?? "", Loc = x["loc"]?.ToString() ?? "", Goto = x["goto"]?.ToString() ?? "" }).ToList();
                    Screens.Saved(Work.FromText(q.Length > 0 ? q : chat?["title"]?.ToString() ?? "Nova Chat", q, a["text"]?.ToString() ?? "", "chat", links));
                    break;
                }
                case "mic": await Mic(m["on"]?.GetValue<bool>() == true); break;
            }
        }
        catch (Exception e)
        {
            Log.Error("Nova Chat · " + m["t"], e);
            Post(new JsonObject { ["t"] = "error", ["text"] = e.Message });
        }
    }

    async Task Mic(bool on)
    {
        if (on)
        {
            try { voice.Start(); Post(new JsonObject { ["t"] = "mic", ["on"] = true }); }
            catch (Exception e) { Post(new JsonObject { ["t"] = "mic", ["on"] = false, ["error"] = "Microphone: " + e.Message }); }
            return;
        }
        var wav = voice.Stop();
        Post(new JsonObject { ["t"] = "mic", ["on"] = false });
        if (wav.Length < 8000) return;
        try { var t = await Ai.Transcribe(wav); Post(new JsonObject { ["t"] = "mic", ["on"] = false, ["text"] = t }); }
        catch (Exception e) { Post(new JsonObject { ["t"] = "mic", ["on"] = false, ["error"] = e.Message }); }
    }

    void FindEmail(string q)
    {
        var res = new JsonArray();
        string note = null;
        if (MailIndex.Count() == 0) note = "Your email index is empty — keep Outlook open and Nova fills it by itself (Settings → Email & AI).";
        else
        {
            var words = FolderAsk.LocalTerms(q);
            var hits = MailIndex.Search(new MailQuery { Words = words, Limit = 20 });
            foreach (var h in hits)
            {
                MailCache[h.Id] = h;
                res.Add(new JsonObject
                {
                    ["id"] = h.Id, ["entry"] = h.Entry, ["store"] = h.StoreId, ["from"] = h.Sent ? "Me → " + h.To : h.FromName,
                    ["subject"] = h.Subject, ["date"] = h.Date.ToString("dd MMM yyyy"), ["sent"] = h.Sent, ["lines"] = h.FirstLines(140)
                });
            }
        }
        Post(new JsonObject { ["t"] = "emails", ["list"] = res, ["note"] = note });
    }

    // ================================================================ answering

    class Src
    {
        public string Kind, Label, Path, Loc, Goto, Remote, Entry, Store, Text;
        public JsonObject Json() => new()
        {
            ["kind"] = Kind, ["label"] = Label, ["path"] = Path, ["loc"] = Loc, ["goto"] = Goto, ["remote"] = Remote, ["entry"] = Entry, ["store"] = Store
        };
    }

    static Task RunSta(Action a)
    {
        var tcs = new TaskCompletionSource();
        var t = new Thread(() => { try { a(); tcs.SetResult(); } catch (Exception e) { tcs.SetException(e); } }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    CancellationTokenSource sendCts;
    string deepHist;

    /// <summary>🎯 Guided mode for the chat page: on, the opening menu, the time limit.</summary>
    static JsonObject GuidedInfo()
    {
        var items = new JsonArray(); foreach (var x in Guided.ChatMenu) items.Add(x);
        var wrap = new JsonArray(); foreach (var x in Guided.WrapUp) wrap.Add(x);
        return new JsonObject { ["on"] = Guided.On, ["menu"] = Guided.MenuFirst, ["min"] = Guided.Minutes, ["items"] = items, ["wrap"] = wrap, ["summary"] = Guided.SummaryAsk, ["lines"] = TypeBox.Lines, ["maxLines"] = TypeBox.MaxLines, ["font"] = TypeBox.FontSize };
    }

    string openChatId = "";

    /// <summary>📄 The other scanned pages of a PDF are read in the background (3 at a time, kept in the shared Nova index); when done,
    /// a line in the chat says so — ↻ ask again to get the answer with every page.</summary>
    void FinishInBackground(string f, string chatId, PdfFast.Quick qk)
    {
        _ = Task.Run(() =>
        {
            using var busy = Busy.Begin("📄 Reading the rest of " + Path.GetFileName(f));
            var t0 = DateTime.Now;
            try { Projects.Read(f, CancellationToken.None); }
            catch (Exception e) { Log.Warn("Nova Chat", "background PDF read: " + e.Message); return; }
            var chat = Load(chatId);
            if (chat == null) return;
            var msgs = chat["messages"] as JsonArray ?? new JsonArray();
            msgs.Add(new JsonObject
            {
                ["role"] = "assistant", ["label"] = "📄 background reading",
                ["text"] = $"✓ Nova has now read all {qk.Pages} pages of {Path.GetFileName(f)} ({qk.Scanned} scanned) in {(DateTime.Now - t0).TotalSeconds:0} s. ↻ Ask again (or press ↻ on the answer) to get the answer checked against every page."
            });
            chat["messages"] = msgs;
            Save(chat);
            if (openChatId == chatId) Post(new JsonObject { ["t"] = "answer", ["chat"] = chat.DeepClone() });
            Post(new JsonObject { ["t"] = "chats", ["chats"] = List() });
        });
    }

    async Task Send(JsonObject m, CancellationToken stop = default)
    {
        var text = m["text"]?.ToString()?.Trim() ?? "";
        if (text.Length == 0) return;
        var ctx = m["ctx"] as JsonObject ?? new JsonObject();
        bool detailed = m["detailed"]?.GetValue<bool>() == true, again = m["again"]?.GetValue<bool>() == true;
        var prov = Ai.Current() ?? throw new AiException("Add an AI key in Nova Enhance → Settings → AI & API keys");

        var chat = Load(m["id"]?.ToString());
        if (chat == null)
        {
            var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Random.Shared.Next(100, 999);
            chat = new JsonObject
            {
                ["id"] = id, ["title"] = text.Length > 60 ? text[..60] + "…" : text, ["created"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(), ["messages"] = new JsonArray()
            };
        }
        var msgs = chat["messages"] as JsonArray ?? new JsonArray();
        chat["messages"] = msgs;
        chat["ctx"] = ctx.DeepClone();
        chat["ctxUsed"] = ctx.DeepClone();
        var history = new StringBuilder();
        foreach (var old in msgs.OfType<JsonObject>().TakeLast(6))
        {
            var t = old["text"]?.ToString() ?? "";
            history.Append(old["role"]?.ToString() == "user" ? "User: " : "Nova: ").AppendLine(t.Length > 1500 ? t[..1500] + "…" : t);
        }
        if (!again) msgs.Add(new JsonObject { ["role"] = "user", ["text"] = text, ["ctxLabel"] = CtxLabel(ctx) });
        openChatId = chat["id"]?.ToString() ?? "";
        // 👁 “vision 12 14”: the AI looks at those pages of the attached PDF (you asked for it — it uses your AI quota)
        var vis = System.Text.RegularExpressions.Regex.Match(text, @"^\s*vision\s+([\d ,]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var visPdf = (ctx["files"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "").FirstOrDefault(x => x.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(x));
        if (vis.Success && visPdf != null)
        {
            var prevQ = msgs.OfType<JsonObject>().Where(x => x["role"]?.ToString() == "user").Select(x => x["text"]?.ToString() ?? "").Reverse().Skip(1).FirstOrDefault(x => !x.TrimStart().StartsWith("vision", StringComparison.OrdinalIgnoreCase)) ?? "";
            var sb = new StringBuilder();
            foreach (var pg in vis.Groups[1].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Distinct().Take(4))
            {
                Post(new JsonObject { ["t"] = "progress", ["text"] = $"👁 {prov.Label} looks at page {pg}…", ["step"] = true });
                try
                {
                    var (data, mime) = await Task.Run(() => Ocr.Picture(visPdf, pg));
                    var seen = await Ai.Vision(data, mime, stop, $"Page {pg} of {Path.GetFileName(visPdf)}. " + (prevQ.Length > 0 ? "Answer from this page: " + prevQ + " — " : "") + "quote exact values, models, finishes and references; say plainly if the page does not show it.");
                    sb.Append($"**Page {pg}** — ").AppendLine(seen.Trim()).AppendLine();
                }
                catch (Exception ex) { sb.AppendLine($"**Page {pg}** — ⚠ {ex.Message}"); }
            }
            msgs.Add(new JsonObject { ["role"] = "assistant", ["text"] = sb.ToString().Trim(), ["label"] = $"👁 {prov.Label} vision · {Path.GetFileName(visPdf)}", ["question"] = text });
            Save(chat);
            Post(new JsonObject { ["t"] = "answer", ["chat"] = chat.DeepClone() });
            Post(new JsonObject { ["t"] = "chats", ["chats"] = List() });
            return;
        }

        using var doing = Log.Doing("Nova Chat · " + (text.Length > 60 ? text[..60] : text));
        using var busyJob = Busy.Begin("💬 Nova Chat · " + (text.Length > 50 ? text[..50] + "…" : text));
        // 🧭 Who sent what to whom, who answered, submittals… → the Nova agent (log + emails + threads), in this chat.
        var chatId = chat["id"]?.ToString() ?? "";
        bool noFiles = (ctx["files"] as JsonArray ?? new JsonArray()).Count == 0 && (ctx["emails"] as JsonArray ?? new JsonArray()).Count == 0 && string.IsNullOrEmpty(ctx["folder"]?.ToString());
        if (noFiles && !detailed && (chat["agent"]?.GetValue<bool>() == true || Agent.HasSession(chatId) || Agent.Wanted(text))) { await AgentSend(chat, msgs, text, stop); return; }
        var sources = new List<Src>();
        var notes = new List<string>();
        // Smart search: the search words are taken from the corrected text (the AI still sees what you typed).
        var searchText = Speller.Correct(text).Text;
        var terms = FolderAsk.LocalTerms(searchText);

        // Files the user attached: the most relevant parts of each.
        foreach (var f in (ctx["files"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)))
        {
            if (!File.Exists(f)) { notes.Add("missing file " + Path.GetFileName(f)); continue; }
            Post(new JsonObject { ["t"] = "progress", ["text"] = "Reading " + Path.GetFileName(f) + "…", ["step"] = true });
            // 👁 Pictures, drawings, CAD, voice notes and videos: Nova looks / listens with your question in mind.
            var fx = Path.GetExtension(f).ToLowerInvariant();
            if (FileTalk.IsPicture(f) || Media.Is(f) || fx is ".dwg" or ".dxf" || (fx == ".pdf" && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"(DRW|DWG|DET|PLN|ELE|SEC|SK)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            {
                var seen = await FileTalk.Text(f, owner, CancellationToken.None, t => Post(new JsonObject { ["t"] = "progress", ["text"] = t }), text);
                sources.Add(new Src { Kind = "file", Path = f, Label = Path.GetFileName(f), Text = seen.Length > 12000 ? seen[..12000] : seen });
                continue;
            }
            List<Seg> segs = null;
            if (fx == ".pdf" && FolderIndex.Known(f) == null)
            {
                // ⚡ Not read before: the text pages now (seconds) + the scanned pages that matter first, 3 at a time; the rest in the background.
                try
                {
                    var qk = await Task.Run(() => PdfFast.Read(f, terms, 12, s => Post(new JsonObject { ["t"] = "progress", ["text"] = s })));
                    segs = qk.Segs;
                    Post(new JsonObject { ["t"] = "progress", ["text"] = "📄 " + Path.GetFileName(f) + " · " + qk.How, ["step"] = true });
                    if (qk.Left.Count > 0) { notes.Add($"{Path.GetFileName(f)}: answered from {qk.Pages - qk.Left.Count} of {qk.Pages} pages — the other {qk.Left.Count} scanned pages are being read now; ↻ ask again in a moment for every page"); FinishInBackground(f, chat["id"]?.ToString() ?? "", qk); }
                    else _ = Task.Run(() => { try { Projects.Read(f, CancellationToken.None); } catch { } });   // kept in the shared index
                }
                catch (Exception ex) { Log.Warn("Nova Chat", "fast PDF read: " + ex.Message); segs = null; }
            }
            if (segs == null)
                try { segs = (await Task.Run(() => Projects.Read(f, CancellationToken.None))).Segs; } catch { segs = null; }   // one store: read once, known by all Nova
            segs = (segs ?? new()).Where(s => (s.Text ?? "").Trim().Length > 0).ToList();
            if (segs.Sum(x => x.Text.Length) < 30 && (FileTalk.IsPicture(f) || f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
            {
                // 📎 A screenshot or a scan: read by vision (asked first unless the local AI does it).
                var v = await FileTalk.Text(f, owner, CancellationToken.None, t => Post(new JsonObject { ["t"] = "progress", ["text"] = t }));
                if (!v.StartsWith("(")) segs = new List<Seg> { new("read by vision", v) };
            }
            if (segs.Count == 0) { notes.Add("no readable text in " + Path.GetFileName(f)); continue; }
            var total = segs.Sum(s => s.Text.Length);
            // 🔎 the best parts for your words AND their related words (sliding → track, rail, roller…), not only exact words
            var pick = total <= 14000 ? segs : PdfFast.Pick(segs, terms, 10);
            var unclear = pick.Where(s => s.Unclear && s.OcrPage > 0).Select(s => s.OcrPage).Distinct().Take(4).ToList();
            if (unclear.Count > 0) notes.Add($"page(s) {string.Join(", ", unclear)} of {Path.GetFileName(f)} are unclear scans — type “vision {string.Join(" ", unclear)}” to let the AI look at them (uses your AI quota)");
            foreach (var s in pick)
            {
                var where = s.Loc.Length > 0 ? s.Loc : "";
                sources.Add(new Src { Kind = "file", Path = f, Loc = s.Loc, Goto = s.Goto, Label = Path.GetFileName(f) + (where.Length > 0 ? " · " + where : ""), Text = s.Text.Length > 3500 ? s.Text[..3500] : s.Text });
            }
        }

        // A folder: the folder index finds the best passages (it reads new or changed files first).
        var folder = ctx["folder"]?.ToString();
        if (!string.IsNullOrEmpty(folder))
        {
            if (FolderAsk.Sees == "never") notes.Add("folder not sent — Settings → Folder search is set to \"Never send my files\"");
            else
            {
                List<FolderIndex.Passage> ps;
                var expanded = await FolderAsk.Expand(searchText, null, CancellationToken.None);
                if (Projects.Covers(folder))
                {
                    // A project folder: not re-read — the file names point to the few files to open (remembered for all Nova).
                    var nsd = await Task.Run(() => NovaSearch.Run(searchText, expanded, new NovaSearch.Opts { Folders = new() { folder }, Emails = false, Mem = false, Hist = false },
                        t => Post(new JsonObject { ["t"] = "progress", ["text"] = t }), CancellationToken.None));
                    ps = nsd.Drawings.Concat(nsd.Docs).ToList();
                }
                else
                {
                    Post(new JsonObject { ["t"] = "progress", ["text"] = "Reading the folder (new or changed files only)…", ["step"] = true });
                    var pg = new FolderIndex.Progress();
                    using var tick = new System.Threading.Timer(_ => Post(new JsonObject { ["t"] = "progress", ["text"] = $"Reading the folder… {pg.Files:N0} files" }), null, 1500, 1500);
                    await RunSta(() => FolderIndex.Search(folder, null, null, _ => { }, null, pg, CancellationToken.None, null));
                    tick.Change(Timeout.Infinite, Timeout.Infinite);
                    Post(new JsonObject { ["t"] = "progress", ["text"] = "Finding the right passages…", ["step"] = true });
                    ps = await Task.Run(() => FolderIndex.Retrieve(folder, expanded, 14, 4, Memory.Boosts(expanded)));
                }
                bool send = true;
                if (ps.Count > 0 && FolderAsk.Sees == "ask")
                {
                    var nFiles = ps.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    var tcs = new TaskCompletionSource<bool>();
                    owner.BeginInvoke(new Action(() =>
                    {
                        using var dlg = new ConfirmSend($"✦ Send {ps.Count} extracts to {prov.Label}?",
                            $"To answer, Nova sends {ps.Count} short passages (about {ps.Sum(x => x.Text.Length):N0} characters) from {nFiles} file{(nFiles == 1 ? "" : "s")} of this folder. Nothing else from the folder leaves the PC.",
                            FolderAsk.Extracts(ps), "Send and answer", false);
                        dlg.ShowDialog(owner);
                        tcs.SetResult(dlg.Result == ConfirmSend.Choice.Send);
                    }));
                    send = await tcs.Task;
                }
                if (!send) notes.Add("folder passages not sent (you chose not to)");
                else if (ps.Count == 0) notes.Add("nothing relevant found in the folder");
                else foreach (var p in ps) sources.Add(new Src { Kind = "passage", Path = p.Path, Loc = p.Loc, Goto = p.Goto, Remote = p.Remote, Label = FolderAsk.Label(p), Text = p.Text });
            }
        }

        // 🔎 Deep search (the 🔎 button, or "go deep" / "approved earlier?" in the question): the parts of the system, in rounds,
        // through the index, Google Drive and the emails; the slow full read only for what nobody found.
        DeepSearch.Outcome deep = null;
        bool wantDeep = m["deep"]?.GetValue<bool>() == true || DeepSearch.Wanted(text);
        if (wantDeep && FolderAsk.Sees == "never") notes.Add("deep search not run — Settings → Folder search is set to \"Never send my files\"");
        else if (wantDeep)
        {
            var attachedText = string.Join("\n\n", sources.Where(x => x.Kind == "file").Select(x => x.Label + "\n" + x.Text));
            // 🕘 Saved after every step: if Nova closes, ▶ Continue in Search history goes on from there.
            var resumeId = m["resume"]?.ToString();
            var resumeState = string.IsNullOrEmpty(resumeId) ? null : SearchHistory.Get(resumeId)?["state"] as JsonObject;
            deepHist = string.IsNullOrEmpty(resumeId) ? SearchHistory.NewId() : resumeId;
            var hid = deepHist;
            SearchHistory.Save(hid, new JsonObject { ["kind"] = "deep", ["subject"] = text, ["status"] = "running", ["chatId"] = chat["id"]?.ToString(), ["note"] = "starting…" });
            try
            {
                deep = await DeepSearch.Run(text, attachedText, sources.Where(x => x.Kind == "file" && !string.IsNullOrEmpty(x.Path)).Select(x => x.Path).Distinct(),
                    history.ToString(), prov, t => Post(new JsonObject { ["t"] = "progress", ["text"] = t }), stop, resumeState,
                    (st, o) => SearchHistory.Save(hid, new JsonObject
                    {
                        ["status"] = "running", ["state"] = st, ["sources"] = DeepSearch.SourcesJson(o.Hits), ["note"] = $"{o.Parts.Count} part(s) · last step: {st["step"]}",
                        ["answer"] = "⏳ Not finished — " + o.Hits.Count + " find(s) so far. ▶ Continue goes on from where it stopped."
                    }));
            }
            catch (OperationCanceledException)
            {
                SearchHistory.Save(hid, new JsonObject { ["status"] = "stopped", ["answer"] = "■ Stopped — what was found so far is kept. ▶ Continue goes on from where it stopped." });
                throw;
            }
            // The attached document keeps its best pages; the rest of the room goes to what was found.
            if (deep.Hits.Count > 0 && sources.Count > 6) sources = sources.Take(6).ToList();
            bool sendDeep = true;
            if (deep.Hits.Count > 0 && FolderAsk.Sees == "ask")
            {
                var nFiles = deep.Hits.Where(h => h.Path.Length > 0).Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var tcs = new TaskCompletionSource<bool>();
                owner.BeginInvoke(new Action(() =>
                {
                    using var dlg = new ConfirmSend($"✦ Send {deep.Hits.Count} extracts to {prov.Label}?",
                        $"To answer, Nova sends {deep.Hits.Count} short passages (about {deep.Hits.Sum(x => x.Text.Length):N0} characters) from {nFiles} file(s) and {deep.Emails} email(s) found by the deep search. Nothing else leaves the PC.",
                        string.Join("\n\n", deep.Hits.Take(40).Select(h => "— " + h.Label + "\n" + (h.Text.Length > 400 ? h.Text[..400] + "…" : h.Text))), "Send and answer", false);
                    dlg.ShowDialog(owner);
                    tcs.SetResult(dlg.Result == ConfirmSend.Choice.Send);
                }));
                sendDeep = await tcs.Task;
            }
            if (!sendDeep) { notes.Add("deep search extracts not sent (you chose not to)"); deep.Hits.Clear(); }
            foreach (var h in deep.Hits.Take(46))
                sources.Add(new Src { Kind = h.Kind, Path = h.Path, Loc = h.Loc, Goto = h.Goto, Remote = h.Remote, Entry = h.Entry, Store = h.Store,
                    Label = (h.Part.Length > 0 ? "[" + h.Part + "] " : "") + h.Label, Text = h.Text });
            detailed = true;
        }

        // 🔎 One Nova search: when the question names a project or a drawing number, its documents and emails are searched too;
        // 🧠 Nova Memory and 🕘 past answers always (only close matches are used).
        if (string.IsNullOrEmpty(folder) && deep == null)
            try
            {
                var localSw = System.Diagnostics.Stopwatch.StartNew();
                var proj = Projects.ForText(searchText);
                var named = proj != null || Revisions.Numbers(searchText).Count > 0;
                var docsOk = named && FolderAsk.Sees != "never";
                var ns = await Task.Run(() => NovaSearch.Run(searchText, FolderAsk.LocalTerms(searchText), new NovaSearch.Opts { Project = proj, Docs = docsOk, Drawings = docsOk, Emails = proj != null, MaxFiles = 5, MaxDocs = 10 },
                    t => Post(new JsonObject { ["t"] = "progress", ["text"] = t }), CancellationToken.None));
                foreach (var p in ns.Drawings.Concat(ns.Docs)) sources.Add(new Src { Kind = "passage", Path = p.Path, Loc = p.Loc, Goto = p.Goto, Remote = p.Remote, Label = FolderAsk.Label(p), Text = p.Text });
                foreach (var p in ns.Emails)
                {
                    var parts = (p.Remote ?? "").Split('|');
                    sources.Add(new Src { Kind = "email", Entry = parts.Length > 1 ? parts[1] : "", Store = parts.Length > 2 ? parts[2] : "", Label = p.Loc, Text = p.Text });
                }
                foreach (var p in ns.Memory.Concat(ns.History)) sources.Add(new Src { Kind = "note", Label = p.Loc, Text = p.Text });
                // ⚡ What Nova found on this PC is shown at once — the AI writes the answer after (you can open them while it works).
                var found = sources.Where(x => x.Kind is "passage" or "email" or "file").Take(10).ToList();
                if (found.Count > 0)
                    Post(new JsonObject { ["t"] = "local", ["text"] = $"⚡ Found on this PC in {localSw.Elapsed.TotalSeconds:0.0} s (no AI) — the AI is now writing the answer from them:", ["sources"] = new JsonArray(found.Select(x => (JsonNode)x.Json()).ToArray()) });
                if (ns.Project != null) notes.Add($"📁 project {ns.Project.Code} detected");
                notes.AddRange(ns.Notes.Where(n => n.StartsWith("⚠ not reachable")));
            }
            catch (Exception ex) { Log.Warn("Nova Chat", "Nova search: " + ex.Message); }

        // Emails the user added.
        foreach (var e in (ctx["emails"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            long id = e["id"]?.GetValue<long>() ?? 0;
            MailCache.TryGetValue(id, out var h);
            string body;
            if (h != null && Store.AiSeesFull) body = h.NewPart.Length > 6000 ? h.NewPart[..6000] : h.NewPart;
            else
            {
                body = h != null ? h.FirstLines(600) : e["lines"]?.ToString() ?? "";
                if (!Store.AiSeesFull) notes.Add("only the first lines of emails are sent — Settings → Email & AI → C or D sends the full email");
            }
            sources.Add(new Src
            {
                Kind = "email", Entry = e["entry"]?.ToString(), Store = e["store"]?.ToString(),
                Label = $"Email · {e["from"]} · {e["subject"]} · {e["date"]}",
                Text = $"From: {e["from"]}\nDate: {e["date"]}\nSubject: {e["subject"]}\n\n{body}"
            });
        }

        // The question.
        bool about = ctx["about"]?.GetValue<bool>() != false;
        var sys = new StringBuilder("You are Nova, the user's assistant in Nova Enhance for Windows. Reply in the language of the question. ");
        if (sources.Count > 0)
            sys.Append("Numbered extracts from the user's own files and emails are given. Use them first, quote exact values (dimensions, ratings, codes, clauses, dates), and after each fact taken from them put its number in square brackets, e.g. [2]. " +
                       "If two extracts disagree, write a line starting with ⚠ and cite both. If the extracts do not contain the answer, say so plainly, then you may add general knowledge clearly marked as such. Never invent document content. " + NovaSearch.Authority);
        else sys.Append("Answer from general knowledge and say when something must be checked against the project documents or the local code/authority. ");
        sys.Append(detailed ? "Give a complete, well-structured answer with headings or bullets where useful. " : "Be concise: the direct answer first, then at most 6 short lines or bullets. ");
        if (sources.Any(x => x.Kind == "file"))
            sys.Append(" Here the ATTACHED FILES are the extracts from the user's files (their labels are file names); cite them by their numbers." + FileTalk.Rules);
        // 🎯 Guided mode: stay in the task of this chat, end with ▶ Options (numbered buttons).
        sys.Append(Guided.Rule($"the task of this chat (“{chat["title"]}”)" + (ctx["folder"]?.ToString() is { Length: > 0 } gf ? $", in the folder {gf}" : "") + ", inside the user's projects, files and emails"));
        if (about) sys.Append(Modes.ProfileNote());
        if (deep != null && deep.Parts.Count > 0) sys.Append(DeepSearch.Instructions(deep));

        var user = new StringBuilder();
        if (history.Length > 0) user.Append("Earlier in this chat:\n").Append(history).Append('\n');
        if (sources.Count > 0)
        {
            user.Append("Extracts:\n");
            for (int i = 0; i < sources.Count; i++) user.Append('[').Append(i + 1).Append("] ").Append(sources[i].Label).Append('\n').Append(sources[i].Text.Trim()).Append("\n\n");
        }
        user.Append("Question: ").Append(text);

        Post(new JsonObject { ["t"] = "progress", ["text"] = $"Asking {prov.Label}…", ["step"] = true });
        using var _aiFor13 = Ai.For("chat", Ai.Picked(prov, "chat"));   // ⚙ AI for each feature
        var answer = await Ai.Chat(sys.ToString(), user.ToString(), !detailed, false, prov, stop);
        if (deep != null) answer += "\n\n" + deep.Searched;
        History.Add("chat", prov.Id, Store.Model(prov.Id), detailed ? "chat-detail" : "chat", text, answer);
        // 🕘 Search history: the question, the answer and its sources.
        var srcJson = new JsonArray(sources.Select(s => (JsonNode)s.Json()).ToArray());
        if (deep != null && deepHist != null)
            SearchHistory.Save(deepHist, new JsonObject { ["status"] = "done", ["answer"] = answer, ["sources"] = srcJson, ["chatId"] = chat["id"]?.ToString(), ["note"] = $"{deep.Parts.Count} part(s) · {deep.Rounds} round(s) · {sources.Count} source(s)" });
        else SearchHistory.Add("chat", text, answer, srcJson, $"{sources.Count} source(s)", new JsonObject { ["chatId"] = chat["id"]?.ToString() });
        deepHist = null;

        var label = prov.Label + (sources.Count > 0 ? $" · {sources.Count} source{(sources.Count == 1 ? "" : "s")}" : "") + (detailed ? " · detailed" : "") +
                    (notes.Count > 0 ? " · ⚠ " + string.Join(" · ", notes.Distinct()) : "");
        msgs.Add(new JsonObject
        {
            ["role"] = "assistant", ["text"] = answer, ["label"] = label, ["detailed"] = detailed, ["question"] = text,
            ["sources"] = new JsonArray(sources.Select(s => (JsonNode)s.Json()).ToArray())
        });
        Save(chat);
        Post(new JsonObject { ["t"] = "answer", ["chat"] = chat.DeepClone() });
        Post(new JsonObject { ["t"] = "chats", ["chats"] = List() });
    }

    // ================================================================ 🧭 Nova agent in the chat

    TaskCompletionSource<(List<long>, List<long>, string)?> agWait;

    async Task AgentSend(JsonObject chat, JsonArray msgs, string text, CancellationToken stop)
    {
        var id = chat["id"]?.ToString() ?? "";
        // The user's question is already in msgs (added above): the session is rebuilt from the earlier turns only.
        var sess = Agent.SessionFor(id, new JsonArray(msgs.Take(Math.Max(0, msgs.Count - 1)).Select(x => x?.DeepClone()).ToArray()));
        var host = new Agent.Host
        {
            Progress = s => Post(new JsonObject { ["t"] = "progress", ["text"] = s, ["step"] = true }),
            Detail = s => Post(new JsonObject { ["t"] = "progress", ["text"] = s }),
            Confirm = async (q, ex) =>
            {
                agWait = new TaskCompletionSource<(List<long>, List<long>, string)?>();
                Post(new JsonObject { ["t"] = "ag.confirm", ["question"] = q, ["items"] = Agent.Examples(ex) });
                using var reg = stop.Register(() => agWait.TrySetResult(null));
                return await agWait.Task;
            }
        };
        var r = await Agent.Run(text, sess, host, "chat", stop);
        msgs.Add(AgentMessage(text, r, id));
        chat["agent"] = true;
        Save(chat);
        Post(new JsonObject { ["t"] = "answer", ["chat"] = chat.DeepClone() });
        Post(new JsonObject { ["t"] = "chats", ["chats"] = List() });
    }

    /// <summary>The agent's answer as a chat message (Markdown table + the emails to open + the result kept for follow-ups and the log).</summary>
    static JsonObject AgentMessage(string question, Agent.Result r, string chatId)
    {
        var cite = new List<MailHit>();
        var answer = Agent.Markdown(r, cite);
        var sources = cite.Select(h => new Src { Kind = "email", Entry = h.Entry, Store = h.StoreId, Label = $"{h.Date:dd MMM yyyy} · {h.Subject}" }).ToList();
        SearchHistory.Add("chat", question, answer, new JsonArray(sources.Select(x => (JsonNode)x.Json()).ToArray()), $"🧭 agent · {r.Rows.Count} row(s)", new JsonObject { ["chatId"] = chatId });
        return new JsonObject
        {
            ["role"] = "assistant", ["text"] = answer, ["label"] = "🧭 Nova agent · " + r.By + (r.Rows.Count > 0 ? $" · {r.Rows.Count} rows" : ""), ["question"] = question,
            ["sources"] = new JsonArray(sources.Select(x => (JsonNode)x.Json()).ToArray()), ["agResult"] = Agent.Json(r)
        };
    }

    /// <summary>🧭 A turn of the agent in the 📋 log, saved as a Nova Chat conversation (💬 Continue in Nova Chat opens it, with everything).</summary>
    public static string SaveAgentTurn(string chatId, string question, Agent.Result r)
    {
        try
        {
            var chat = string.IsNullOrEmpty(chatId) ? null : Load(chatId);
            if (chat == null)
            {
                chatId = string.IsNullOrEmpty(chatId) ? DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Random.Shared.Next(100, 999) : chatId;
                chat = new JsonObject { ["id"] = chatId, ["title"] = "📋 " + (question.Length > 58 ? question[..58] + "…" : question), ["created"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(), ["messages"] = new JsonArray(), ["ctx"] = new JsonObject { ["about"] = true, ["files"] = new JsonArray(), ["emails"] = new JsonArray(), ["folder"] = "" } };
            }
            var msgs = chat["messages"] as JsonArray ?? new JsonArray();
            chat["messages"] = msgs;
            msgs.Add(new JsonObject { ["role"] = "user", ["text"] = question, ["ctxLabel"] = "📋 Submittal log" });
            msgs.Add(AgentMessage(question, r, chatId));
            chat["agent"] = true;
            Save(chat);
        }
        catch (Exception e) { Log.Warn("agent · save chat", e.Message); }
        return chatId;
    }

    /// <summary>The agent turns of a saved chat (for the 📋 log): question + result.</summary>
    public static JsonArray AgentTurns(string chatId)
    {
        var a = new JsonArray();
        var msgs = Load(chatId)?["messages"] as JsonArray ?? new JsonArray();
        string q = "";
        foreach (var m in msgs.OfType<JsonObject>())
        {
            if (m["role"]?.ToString() == "user") { q = m["text"]?.ToString() ?? ""; continue; }
            a.Add(new JsonObject { ["q"] = q, ["result"] = m["agResult"]?.DeepClone() ?? new JsonObject { ["text"] = m["text"]?.ToString() ?? "" } });
        }
        return a;
    }

    static string CtxLabel(JsonObject c)
    {
        var p = new List<string>();
        if (!string.IsNullOrEmpty(c["folder"]?.ToString())) p.Add("📁 " + Path.GetFileName(c["folder"]!.ToString().TrimEnd('\\')));
        var nf = (c["files"] as JsonArray)?.Count ?? 0; if (nf > 0) p.Add($"📄 {nf} file{(nf > 1 ? "s" : "")}");
        var ne = (c["emails"] as JsonArray)?.Count ?? 0; if (ne > 0) p.Add($"✉ {ne} email{(ne > 1 ? "s" : "")}");
        if (c["about"]?.GetValue<bool>() != false) p.Add("👤 About me");
        return string.Join(" · ", p);
    }

    // ================================================================ actions on an answer

    /// <summary>An email source: Nova's view (with “Open in Outlook”); Ctrl+click = straight in Outlook.</summary>
    public static void OpenSource(JsonObject s, bool outlook = false)
    {
        if (s == null) return;
        if (s["kind"]?.ToString() == "email")
        {
            var entry = s["entry"]?.ToString();
            MailHit h = null;
            if (!outlook) try { h = MailIndex.ByEntry(entry); } catch { }
            if (h != null) Screens.MailAsk(h); else Outlook.Open(entry, s["store"]?.ToString());
            return;
        }
        var path = s["path"]?.ToString();
        if (string.IsNullOrEmpty(path)) return;
        Opener.Open(new FileHit { Path = path, Remote = s["remote"]?.ToString() }, new PieceHit { Loc = s["loc"]?.ToString() ?? "", Goto = s["goto"]?.ToString() ?? "", Text = "", Matches = new() }, "");
    }

    static (JsonObject chat, JsonObject answer, string question) Pick(string id, int idx)
    {
        var chat = Load(id);
        var msgs = chat?["messages"] as JsonArray;
        if (msgs == null || idx < 0 || idx >= msgs.Count) return (null, null, null);
        var a = msgs[idx] as JsonObject;
        return (chat, a, a?["question"]?.ToString() ?? "");
    }

    static string WithSourceList(JsonObject a)
    {
        var sb = new StringBuilder(a["text"]?.ToString() ?? "");
        var src = a["sources"] as JsonArray;
        if (src != null && src.Count > 0)
        {
            sb.Append("\n\nSources:\n");
            for (int i = 0; i < src.Count; i++) sb.Append('[').Append(i + 1).Append("] ").Append(src[i]?["label"]).Append('\n');
        }
        return sb.ToString();
    }

    void SaveWord(string id, int idx)
    {
        var (chat, a, q) = Pick(id, idx);
        if (a == null) return;
        var dir = Path.Combine(Dir, "Saved");
        Directory.CreateDirectory(dir);
        var name = Regex.Replace(q.Length > 50 ? q[..50] : q, @"[\\/:*?""<>|\r\n]", " ").Trim();
        var path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd HHmm} {name}.docx");
        Docx.Write(path, "Nova Chat", q, WithSourceList(a), Program.Sign);
        Shell.Select(path);
    }

    void DraftReply(string id, int idx)
    {
        var (chat, a, q) = Pick(id, idx);
        var mail = (chat?["ctxUsed"]?["emails"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        if (a == null || mail == null) return;
        Outlook.Reply(mail["entry"]?.ToString(), mail["store"]?.ToString(), a["text"]?.ToString() ?? "", false);
    }

    // ================================================================ the page itself (served from inside Nova)

    public static byte[] Resource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Chat." + name, StringComparison.OrdinalIgnoreCase));
        if (res == null) return null;
        using var s = asm.GetManifestResourceStream(res);
        using var ms = new MemoryStream();
        s!.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>A plain Word file (.docx) without Word: title, question, answer.</summary>
static class Docx
{
    static string X(string s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";

    static string P(string text, bool bold = false, int size = 22, string color = null) =>
        "<w:p><w:r><w:rPr>" + (bold ? "<w:b/>" : "") + (color != null ? $"<w:color w:val=\"{color}\"/>" : "") + $"<w:sz w:val=\"{size}\"/></w:rPr><w:t xml:space=\"preserve\">{X(text)}</w:t></w:r></w:p>";

    public static void Write(string path, string title, string question, string body, string sign)
    {
        var sb = new StringBuilder();
        sb.Append(P(title, true, 32, "5B4BDB"));
        sb.Append(P(DateTime.Now.ToString("dd MMM yyyy HH:mm"), false, 18, "777777"));
        if (!string.IsNullOrWhiteSpace(question)) { sb.Append(P("Question", true, 24)); sb.Append(P(question)); }
        sb.Append(P("Answer", true, 24));
        foreach (var line in (body ?? "").Replace("\r", "").Split('\n')) sb.Append(P(line.Replace("**", "")));
        sb.Append(P(sign, false, 18, "7C6CFF"));
        var doc = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + sb +
                  "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";
        if (File.Exists(path)) File.Delete(path);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content) { using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(content); }
        Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Add("word/document.xml", doc);
    }
}
