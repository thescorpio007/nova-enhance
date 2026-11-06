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
                    Post(new JsonObject { ["t"] = "init", ["ai"] = Ai.Active()?.Label ?? "", ["sign"] = Program.Sign, ["chats"] = List(), ["text"] = PendingText });
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
                case "findEmail": FindEmail(m["q"]?.ToString() ?? ""); break;
                case "send": await Send(m); break;
                case "openSource": OpenSource(m["src"] as JsonObject); break;
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

    async Task Send(JsonObject m)
    {
        var text = m["text"]?.ToString()?.Trim() ?? "";
        if (text.Length == 0) return;
        var ctx = m["ctx"] as JsonObject ?? new JsonObject();
        bool detailed = m["detailed"]?.GetValue<bool>() == true, again = m["again"]?.GetValue<bool>() == true;
        var prov = Ai.Active() ?? throw new AiException("Add an AI key in Nova Enhance → Settings → AI & API keys");

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

        using var doing = Log.Doing("Nova Chat · " + (text.Length > 60 ? text[..60] : text));
        var sources = new List<Src>();
        var notes = new List<string>();
        // Smart search: the search words are taken from the corrected text (the AI still sees what you typed).
        var searchText = Speller.Correct(text).Text;
        var terms = FolderAsk.LocalTerms(searchText);

        // Files the user attached: the most relevant parts of each.
        foreach (var f in (ctx["files"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)))
        {
            if (!File.Exists(f)) { notes.Add("missing file " + Path.GetFileName(f)); continue; }
            Post(new JsonObject { ["t"] = "progress", ["text"] = "Reading " + Path.GetFileName(f) + "…" });
            List<Seg> segs = null;
            await RunSta(() => segs = FileText.Extract(f));
            segs = (segs ?? new()).Where(s => (s.Text ?? "").Trim().Length > 0).ToList();
            if (segs.Count == 0) { notes.Add("no readable text in " + Path.GetFileName(f)); continue; }
            var total = segs.Sum(s => s.Text.Length);
            var pick = total <= 14000 ? segs : segs.OrderByDescending(s => terms.Count(w => s.Text.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(8).OrderBy(s => segs.IndexOf(s)).ToList();
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
                Post(new JsonObject { ["t"] = "progress", ["text"] = "Reading the folder (new or changed files only)…" });
                var pg = new FolderIndex.Progress();
                using var tick = new System.Threading.Timer(_ => Post(new JsonObject { ["t"] = "progress", ["text"] = $"Reading the folder… {pg.Files:N0} files" }), null, 1500, 1500);
                await RunSta(() => FolderIndex.Search(folder, null, null, _ => { }, null, pg, CancellationToken.None, null));
                tick.Change(Timeout.Infinite, Timeout.Infinite);
                Post(new JsonObject { ["t"] = "progress", ["text"] = "Finding the right passages…" });
                var expanded = await FolderAsk.Expand(searchText, null, CancellationToken.None);
                var ps = await Task.Run(() => FolderIndex.Retrieve(folder, expanded, 14, 4, Memory.Boosts(expanded)));
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
                       "If two extracts disagree, write a line starting with ⚠ and cite both. If the extracts do not contain the answer, say so plainly, then you may add general knowledge clearly marked as such. Never invent document content. ");
        else sys.Append("Answer from general knowledge and say when something must be checked against the project documents or the local code/authority. ");
        sys.Append(detailed ? "Give a complete, well-structured answer with headings or bullets where useful. " : "Be concise: the direct answer first, then at most 6 short lines or bullets. ");
        if (about) sys.Append(Modes.ProfileNote());

        var user = new StringBuilder();
        if (history.Length > 0) user.Append("Earlier in this chat:\n").Append(history).Append('\n');
        if (sources.Count > 0)
        {
            user.Append("Extracts:\n");
            for (int i = 0; i < sources.Count; i++) user.Append('[').Append(i + 1).Append("] ").Append(sources[i].Label).Append('\n').Append(sources[i].Text.Trim()).Append("\n\n");
        }
        user.Append("Question: ").Append(text);

        Post(new JsonObject { ["t"] = "progress", ["text"] = $"Asking {prov.Label}…" });
        var answer = await Ai.Chat(sys.ToString(), user.ToString(), !detailed, false, prov, CancellationToken.None);
        History.Add("chat", prov.Id, Store.Model(prov.Id), detailed ? "chat-detail" : "chat", text, answer);

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

    static void OpenSource(JsonObject s)
    {
        if (s == null) return;
        if (s["kind"]?.ToString() == "email") { Outlook.Open(s["entry"]?.ToString(), s["store"]?.ToString()); return; }
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
