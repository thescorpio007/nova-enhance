using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

class ConvSrc { public string Id = "", Title = "", Path = "", Goto = "", Loc = "", Entry = "", Store = ""; }

class ConvMsg
{
    public string Role = "user", Text = "", Check = "", Ai = "";
    public DateTime T = DateTime.Now;
    public List<ConvSrc> Src = new();
    public List<string[]> Checks = new();   // claim, status, source, note
}

/// <summary>One saved conversation: ✉ an email · 🔍 Ask Nova · 📁 a folder · 📄 a PDF · 💬 Nova Chat.</summary>
class Conversation
{
    public string Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")[..4];
    public string Kind = "email", Title = "", Ref = "", RefStore = "", Folder = "", File = "";
    public DateTime Created = DateTime.Now, Updated = DateTime.Now;
    public List<ConvMsg> Msgs = new();
}

/// <summary>
/// Every Nova conversation is saved after each answer — Documents › Nova Enhance › Conversations › month — as a readable page (.html)
/// and a data file (.json) for 🕘 Chat history (search) and ↻ Continue. Designed by A.HAMROUNI
/// </summary>
static class Conversations
{
    static readonly JsonSerializerOptions J = new() { IncludeFields = true, WriteIndented = false };

    public static string Dir
    {
        get
        {
            var d = Store.Str("win_conv_folder");
            if (d.Length == 0) d = Path.Combine(Paths.Root, "Conversations");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
        set => Store.Set("win_conv_folder", value);
    }

    public static bool On { get => Store.Bool("win_conv_save", true); set => Store.SetBool("win_conv_save", value); }

    static string Safe(string s) { s = Regex.Replace(s ?? "", @"[\\/:*?""<>|\r\n\t]+", " ").Trim(); return s.Length > 70 ? s[..70].Trim() : s; }

    public static void Save(Conversation c)
    {
        if (!On || c == null || c.Msgs.Count == 0) return;
        try
        {
            c.Updated = DateTime.Now;
            var month = Path.Combine(Dir, c.Created.ToString("yyyy-MM"));
            Directory.CreateDirectory(month);
            if (c.File.Length == 0) c.File = Path.Combine(month, $"{c.Created:dd MMM HHmm} – {Safe(c.Title)}");
            File.WriteAllText(c.File + ".json", JsonSerializer.Serialize(c, J), new UTF8Encoding(false));
            File.WriteAllText(c.File + ".html", Html(c), new UTF8Encoding(false));
        }
        catch (Exception e) { Log.Warn("conversation", e.Message); }
    }

    /// <summary>Quick save for one question and its answer (Ask Nova, folder Ask AI, PDF panel).</summary>
    public static void Add(ref Conversation c, string kind, string title, string reference, string user, string answer, IEnumerable<ConvSrc> src = null)
    {
        c ??= new Conversation { Kind = kind, Title = title, Ref = reference ?? "" };
        c.Msgs.Add(new ConvMsg { Role = "user", Text = user });
        c.Msgs.Add(new ConvMsg { Role = "nova", Text = answer, Ai = Ai.LabelOf(Ai.Current()), Src = src?.ToList() ?? new() });
        Save(c);
    }

    public static Conversation Load(string jsonPath)
    {
        try { return JsonSerializer.Deserialize<Conversation>(File.ReadAllText(jsonPath), J); } catch { return null; }
    }

    public static string Icon(string kind) => kind switch { "email" => "✉", "ask" => "🔍", "folder" => "📁", "pdf" => "📄", "chat" => "💬", "brief" => "☀", _ => "✦" };

    public class Item { public string Path = "", Kind = "", Title = "", Preview = "", Text = ""; public DateTime Updated; public int Count; public bool NovaChat; }

    /// <summary>All saved conversations (Nova's own and Nova Chat's), newest first.</summary>
    public static List<Item> All()
    {
        var res = new List<Item>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, "*.json", SearchOption.AllDirectories))
            {
                var c = Load(f);
                if (c == null) continue;
                var text = string.Join("\n", c.Msgs.Select(m => m.Text)) + "\n" + c.Title + "\n" + string.Join(" ", c.Msgs.SelectMany(m => m.Src).Select(s => s.Title));
                res.Add(new Item { Path = f, Kind = c.Kind, Title = c.Title, Updated = c.Updated, Count = c.Msgs.Count, Preview = c.Msgs.FirstOrDefault(m => m.Role == "user")?.Text ?? "", Text = text });
            }
        }
        catch { }
        // Nova Chat keeps its own chats: listed here too.
        try
        {
            var chatDir = Path.Combine(Paths.Root, "Nova Chat");
            if (Directory.Exists(chatDir))
                foreach (var f in Directory.GetFiles(chatDir, "*.json"))
                {
                    try
                    {
                        var j = JsonNode.Parse(File.ReadAllText(f))?.AsObject();
                        if (j == null) continue;
                        var msgs = (j["messages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                        var upd = j["updated"]?.GetValue<long>() ?? 0;
                        res.Add(new Item
                        {
                            Path = f, Kind = "chat", NovaChat = true, Title = j["title"]?.ToString() ?? "Chat", Count = msgs.Count,
                            Updated = upd > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(upd).LocalDateTime : File.GetLastWriteTime(f),
                            Preview = msgs.FirstOrDefault(m => m["role"]?.ToString() == "user")?["text"]?.ToString() ?? "",
                            Text = string.Join("\n", msgs.Select(m => m["text"]?.ToString() ?? ""))
                        });
                    }
                    catch { }
                }
        }
        catch { }
        return res.OrderByDescending(x => x.Updated).ToList();
    }

    public static Conversation FromNovaChat(string path)
    {
        var j = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
        var c = new Conversation { Kind = "chat", Title = j?["title"]?.ToString() ?? "Chat", Created = File.GetCreationTime(path), Updated = File.GetLastWriteTime(path) };
        foreach (var m in (j?["messages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            c.Msgs.Add(new ConvMsg { Role = m["role"]?.ToString() == "user" ? "user" : "nova", Text = m["text"]?.ToString() ?? "" });
        return c;
    }

    // ------------------------------------------------------------------ the readable page (and the chat view)

    static string Esc(string s) => WebUtility.HtmlEncode(s ?? "");

    public const string Css =
        "body{background:#12151C;color:#ECEEF3;font:14.5px 'Segoe UI',Arial,sans-serif;line-height:1.55;margin:0;padding:18px 56px 40px}" +
        ".u{margin:0 0 14px auto;max-width:66%;background:#2E2A55;border-radius:14px 14px 4px 14px;padding:10px 14px;white-space:pre-wrap}" +
        ".a{max-width:90%;background:#181C24;border-radius:14px 14px 14px 4px;padding:12px 16px;margin-bottom:16px}" +
        ".w{font-size:11px;color:#9AA3B5;margin-bottom:4px}.u .w{color:#B9B0FF}" +
        "a.src{display:inline-block;background:#2E2A55;color:#C9C2FF;border-radius:4px;padding:0 6px;font-size:11.5px;font-weight:bold;text-decoration:none;margin:0 1px}" +
        "a.src:hover{background:#7C6CFF;color:#fff}.warn{color:#FFC27A}.nf{color:#FF9A9A}b{color:#fff}.h{font-size:15.5px}" +
        ".advice{background:#15241E;border-left:3px solid #3FAF84;color:#BFEAD6;padding:8px 12px;border-radius:6px;margin:10px 0}" +
        ".check{background:#1A1F2C;border-radius:6px;padding:7px 11px;margin-top:10px;font-size:13px;color:#DDE2EE}" +
        "details{margin-top:4px}summary{cursor:pointer;color:#B9B0FF}li.ok{color:#BFEAD6}li.part{color:#FFC27A}li.no{color:#FF9A9A;text-decoration:line-through}" +
        ".acts{margin-top:8px}.acts a{font-size:12.5px;color:#B9B0FF;margin-right:14px;text-decoration:none}.acts a:hover{color:#fff}" +
        "mark{background:#5A4A00;color:#FFE08A;border-radius:2px}.srcs{font-size:12px;color:#9AA3B5;margin-top:8px}.muted{color:#9AA3B5}.wait{color:#B9B0FF;font-style:italic}" +
        ".head{border-bottom:1px solid #2A3040;padding-bottom:10px;margin-bottom:16px}.sign{text-align:right;color:#7C6CFF;font-weight:bold;font-size:12px;margin-top:20px}" + FileTalk.TableCss;

    /// <summary>The label of the draft link under answers ("✍ Put into my draft" while you are writing an email).</summary>
    public static string DraftAction = "✦ Draft reply";

    /// <summary>One Nova answer as HTML: [E1]/[A2]/[D3] become links, ⚠ lines orange, 🎓 advice green, the technical check underneath.</summary>
    public static string AnswerHtml(ConvMsg m, int index, bool actions)
    {
        var ids = m.Src.Select(s => s.Id).ToHashSet();
        string Links(string line) => Regex.Replace(Esc(line), @"\[((?:[EADFMH]\d{1,2})(?:\s*,\s*[EADFMH]\d{1,2})*)\]", x =>
            string.Join(" ", x.Groups[1].Value.Split(',').Select(i => i.Trim()).Select(id => ids.Contains(id)
                ? $"<a class=src href=\"https://nova.src/{index}/{id}\" title=\"{Esc(m.Src.First(s => s.Id == id).Title)}\">{id}</a>" : $"[{id}]")));
        var sb = new StringBuilder();
        bool advice = false;
        var allLines = (m.Text ?? "").Replace("\r\n", "\n").Split('\n');
        var table = new List<string>();
        void FlushTable()
        {
            if (table.Count == 0) return;
            var t = FileTalk.Tables(string.Join("\n", table));
            if (t.Count > 0) sb.Append(Links2(FileTalk.TableHtml(t[0])));
            else foreach (var l in table) sb.Append("<div>").Append(Esc(l)).Append("</div>");
            table.Clear();
        }
        // In a table cell, [F1]/[D2] become links too.
        string Links2(string html) => Regex.Replace(html, @"\[((?:[EADFMH]\d{1,2})(?:\s*,\s*[EADFMH]\d{1,2})*)\]", x =>
            string.Join(" ", x.Groups[1].Value.Split(',').Select(i => i.Trim()).Select(id => ids.Contains(id) ? $"<a class=src href=\"https://nova.src/{index}/{id}\">{id}</a>" : $"[{id}]")));
        foreach (var raw in allLines)
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("|")) { table.Add(line); continue; }
            FlushTable();
            if (line.Contains("🎓")) { if (advice) sb.Append("</div>"); advice = true; sb.Append("<div class=advice><b>").Append(Esc(line)).Append("</b>"); continue; }
            if (line.Trim().Length == 0) { sb.Append("<br>"); continue; }
            var html = Regex.Replace(Links(line), @"\*\*(.+?)\*\*", "<b>$1</b>");
            html = Regex.Replace(html, @"^#{1,4}\s*(.+)$", "<b class=h>$1</b>");
            var cls = line.TrimStart().StartsWith("⚠") ? " class=warn" : line.Contains("Not found in your", StringComparison.OrdinalIgnoreCase) ? " class=nf" : "";
            sb.Append($"<div{cls}>").Append(html).Append("</div>");
        }
        FlushTable();
        if (advice) sb.Append("</div>");
        if (m.Check.Length > 0)
        {
            sb.Append($"<div class=check>{Esc(m.Check)}");
            if (m.Checks.Count > 0)
            {
                sb.Append("<details><summary>show each statement</summary><ul>");
                foreach (var c in m.Checks)
                    sb.Append($"<li class={(c[1] == "ok" ? "ok" : c[1] == "partial" ? "part" : "no")}>{(c[1] == "ok" ? "✓" : c[1] == "partial" ? "⚠" : "✗")} {Esc(c[0])} {Links(c[2])} <span class=muted>{Esc(c[3])}</span></li>");
                sb.Append("</ul></details>");
            }
            sb.Append("</div>");
        }
        if (m.Src.Count > 0)
            sb.Append("<details class=srcs><summary>").Append(m.Src.Count).Append(" sources</summary>")
              .Append(string.Join("", m.Src.Select(s => $"<div><a class=src href=\"https://nova.src/{index}/{s.Id}\">{s.Id}</a> {Esc(s.Title)}</div>"))).Append("</details>");
        if (actions)
            sb.Append($"<div class=acts><a href=\"https://nova.act/draft/{index}\">{DraftAction}</a><a href=\"https://nova.act/copy/{index}\">⧉ Copy</a><a href=\"https://nova.act/task/{index}\">📌 Save as task</a>{(FileTalk.Tables(m.Text).Count > 0 ? $"<a href=\"https://nova.act/xlsx/{index}\">📊 Comparison sheet → Excel</a><a href=\"https://nova.act/tsv/{index}\">⧉ Copy table</a>" : "")}<a href=\"https://nova.act/good/{index}\">👍</a><a href=\"https://nova.act/bad/{index}\">👎</a></div>");
        return sb.ToString();
    }

    public static string Body(Conversation c, bool actions)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < c.Msgs.Count; i++)
        {
            var m = c.Msgs[i];
            if (m.Role == "user") sb.Append($"<div class=u><div class=w>You · {m.T:HH:mm}</div>{Esc(m.Text)}</div>");
            else sb.Append($"<div class=a><div class=w>✦ Nova{(m.Ai.Length > 0 ? " · " + Esc(m.Ai) : "")}{(m.Src.Count > 0 ? $" · {m.Src.Count} sources" : "")} · {m.T:HH:mm}</div>{AnswerHtml(m, i, actions)}</div>");
        }
        return sb.ToString();
    }

    static string Html(Conversation c) =>
        "<!doctype html><html><head><meta charset=utf-8><title>" + Esc(c.Title) + "</title><style>" + Css + "</style></head><body>" +
        $"<div class=head><b>{Icon(c.Kind)} {Esc(c.Title)}</b><div class=muted>{c.Created:dddd dd MMM yyyy HH:mm} · {c.Msgs.Count} messages · Nova Enhance</div></div>" +
        Body(c, false) + "<div class=sign>Designed by A.HAMROUNI</div></body></html>";
}

/// <summary>🕘 Chat history: every conversation, newest first, with a search bar and filters; ↻ Continue picks one up.</summary>
class ChatHistoryForm : NovaForm
{
    readonly TextBox search;
    readonly FlowLayoutPanel list;
    readonly Panel right;
    readonly Label head, meta;
    readonly FlowLayoutPanel acts;
    Microsoft.Web.WebView2.WinForms.WebView2 web;
    List<Conversations.Item> all = new();
    string kind = "";
    Conversations.Item current;
    Conversation shown;
    readonly Dictionary<string, Button> filters = new();
    string pendingHtml;
    // ☑ Several conversations picked (Ctrl+click · Shift+click · Ctrl+A).
    readonly List<Conversations.Item> picked = new();
    readonly List<Conversations.Item> shownItems = new();
    Conversations.Item pickAnchor;
    FlowLayoutPanel pickBar;

    public ChatHistoryForm()
    {
        Text = "Nova Enhance · 🕘 Chat history";
        Width = 1300; Height = 860; MinimumSize = new Size(900, 560);
        var left = new Panel { Dock = DockStyle.Left, Width = 400, BackColor = Color.FromArgb(0x0E, 0x11, 0x17), Padding = new Padding(10) };
        right = new Panel { Dock = DockStyle.Fill, BackColor = Th.Bg, Padding = new Padding(18, 12, 18, 8) };
        Controls.Add(right); Controls.Add(left);

        var top = new Stack { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Padding = new Padding(0) };
        search = top.Add(Th.NoSpell(Th.Box(false)), true);
        search.PlaceholderText = "🔍 Search every conversation (words, subject, file name)…";
        var fr = top.Add(Th.Row());
        foreach (var (k, label) in new[] { ("", "All"), ("email", "✉ Emails"), ("ask", "🔍 Ask"), ("folder", "📁 Folders"), ("pdf", "📄 PDFs"), ("chat", "💬 Chat") })
        {
            var kk = k;
            var b = Th.Chip(label, k == "", () => { kind = kk; foreach (var (fk, fb) in filters) Th.SetChip(fb, fk == kk); Fill(); });
            b.Font = Th.F(8.5f);
            filters[k] = b;
            fr.Controls.Add(b);
        }
        list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.Transparent };
        pickBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.FromArgb(0x2A, 0x25, 0x52), Padding = new Padding(4), Visible = false };
        left.Controls.Add(list);
        left.Controls.Add(pickBar);
        left.Controls.Add(top);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.A && !search.Focused) { picked.Clear(); picked.AddRange(shownItems); Fill(); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape && picked.Count > 0) { picked.Clear(); Fill(); e.Handled = true; }
        };
        var t = new System.Windows.Forms.Timer { Interval = 300 };
        t.Tick += (_, _) => { t.Stop(); Fill(); };
        search.TextChanged += (_, _) => { t.Stop(); t.Start(); };

        var rtop = new Stack { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 6) };
        head = rtop.Add(Th.Wrap("Choose a conversation on the left.", 12, Th.Text), true);
        head.Font = Th.F(12, true);
        meta = rtop.Add(Th.Wrap("", 9, Th.Muted), true);
        acts = rtop.Add(Th.Row());
        right.Controls.Add(rtop);
        Shown += async (_, _) =>
        {
            Reload();
            web = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Th.Bg };
            right.Controls.Add(web);
            web.BringToFront(); rtop.SendToBack();
            try
            {
                await web.EnsureCoreWebView2Async(await HubForm.Env());
                web.CoreWebView2.Settings.IsScriptEnabled = false;
                web.CoreWebView2.NavigationStarting += (_, e) =>
                {
                    if (e.Uri.StartsWith("about:") || e.Uri.StartsWith("data:")) return;
                    e.Cancel = true;
                    var m = Regex.Match(e.Uri, @"nova\.src/(\d+)/([EADFMH]\d+)");
                    if (m.Success && shown != null && int.Parse(m.Groups[1].Value) < shown.Msgs.Count)
                    {
                        var s = shown.Msgs[int.Parse(m.Groups[1].Value)].Src.FirstOrDefault(x => x.Id == m.Groups[2].Value);
                        try
                        {
                            if (s?.Entry.Length > 0) Outlook.Open(s.Entry, s.Store);
                            else if (s?.Path.Length > 0) Opener.Open(new FileHit { Path = s.Path }, new PieceHit { Loc = s.Loc, Goto = s.Goto, Text = "", Matches = new() }, "");
                        }
                        catch (Exception ex) { meta.Text = "⚠ " + ex.Message; }
                        return;
                    }
                    if (e.Uri.StartsWith("http") && !e.Uri.Contains("nova.")) Shell.Open(e.Uri);
                };
                if (pendingHtml != null) web.CoreWebView2.NavigateToString(pendingHtml);
            }
            catch (Exception ex) { meta.Text = "⚠ " + ex.Message; }
        };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); if (e.Control && e.KeyCode == Keys.F) { search.Focus(); e.Handled = true; } };
    }

    public void Reload() { all = Conversations.All(); Fill(); }

    void Fill()
    {
        list.SuspendLayout();
        foreach (Control c in list.Controls) c.Dispose();
        list.Controls.Clear();
        var words = Regex.Split(search.Text.Trim().ToLowerInvariant(), @"\s+").Where(w => w.Length > 0).ToList();
        var items = all.Where(i => (kind == "" || i.Kind == kind) && words.All(w => i.Text.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Title.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        string lastGroup = null;
        var w = list.ClientSize.Width - 26;
        shownItems.Clear();
        if (items.Count == 0) list.Controls.Add(Th.Wrap(all.Count == 0 ? "No conversations yet — they are saved automatically after each answer." : "Nothing found.", 9.5f, Th.Muted));
        foreach (var it in items.Take(400))
        {
            var g = Group(it.Updated);
            if (g != lastGroup) { var gl = Th.L(g, 8.5f, Th.Muted, true); gl.Margin = new Padding(4, 10, 0, 4); list.Controls.Add(gl); lastGroup = g; }
            shownItems.Add(it);
            var card = new Stack { AutoScroll = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = w, BackColor = picked.Contains(it) ? Color.FromArgb(0x33, 0x2C, 0x66) : it == current ? Th.Card2 : Th.Card, Padding = new Padding(10, 7, 10, 7), Margin = new Padding(0, 0, 0, 5), Cursor = Cursors.Hand };
            card.Add(Th.L($"{Conversations.Icon(it.Kind)} {(it.Kind == "email" ? "Email" : it.Kind == "ask" ? "Ask Nova" : it.Kind == "folder" ? "Folder" : it.Kind == "pdf" ? "PDF" : "Nova Chat")} · {it.Updated:ddd dd MMM HH:mm} · {it.Count} messages", 8.5f, Th.Muted));
            var tl = card.Add(Th.L(it.Title, 10, Th.Text, true)); tl.MaximumSize = new Size(w - 24, 40); tl.AutoEllipsis = true;
            var snip = Snippet(it, words);
            if (snip.Length > 0) { var sl = card.Add(Th.L(snip, 9, Color.FromArgb(0xC9, 0xCE, 0xD8))); sl.MaximumSize = new Size(w - 24, 38); }
            var item = it;
            void Hook(Control c) { c.Click += (_, _) => Clicked(item); foreach (Control k in c.Controls) Hook(k); }
            Hook(card);
            list.Controls.Add(card);
        }
        list.ResumeLayout();
        DrawPickBar();
    }

    void Clicked(Conversations.Item it)
    {
        var mk = Control.ModifierKeys;
        if ((mk & Keys.Control) != 0)
        {
            if (picked.Count == 0 && current != null && current != it) picked.Add(current);
            if (!picked.Remove(it)) picked.Add(it);
            pickAnchor = it;
        }
        else if ((mk & Keys.Shift) != 0)
        {
            var a = pickAnchor ?? current ?? it;
            int i = shownItems.IndexOf(a), j = shownItems.IndexOf(it);
            if (i < 0) i = j;
            picked.Clear();
            for (int k = Math.Min(i, j); k <= Math.Max(i, j); k++) picked.Add(shownItems[k]);
        }
        else { picked.Clear(); pickAnchor = it; Open(it); return; }
        Fill();
    }

    void DrawPickBar()
    {
        foreach (Control c in pickBar.Controls) c.Dispose();
        pickBar.Controls.Clear();
        pickBar.Visible = picked.Count > 1;
        if (!pickBar.Visible) return;
        var sel = picked.ToList();
        var l = Th.L($"☑ {sel.Count} selected", 9.5f, Color.White, true); l.Margin = new Padding(2, 6, 6, 0);
        pickBar.Controls.Add(l);
        void B(string text, Action a) { var b = Th.B(text, a); b.Font = Th.F(9); pickBar.Controls.Add(b); }
        B("⇩ Word (all in one)", () =>
        {
            try
            {
                var body = new StringBuilder();
                foreach (var it in sel.OrderBy(x => x.Updated))
                {
                    var c = Conversations.Load(it.Path);
                    if (c == null) continue;
                    body.Append($"\n\n■ {c.Title} — {c.Updated:dd MMM yyyy HH:mm}\n");
                    foreach (var m in c.Msgs) body.Append(m.Role == "user" ? "\nYou: " : "\nNova: ").Append(m.Text).Append('\n');
                }
                var x = System.IO.Path.Combine(Prompts.Folder, $"{sel.Count} conversations {DateTime.Now:dd MMM yyyy HHmm}.docx");
                Docx.Write(x, $"{sel.Count} conversations", "", body.ToString().Trim(), "Designed by A.HAMROUNI");
                Shell.Open(x);
            }
            catch (Exception e) { meta.Text = "⚠ " + e.Message; }
        });
        B("🗑 Delete", () =>
        {
            if (MessageBox.Show(this, $"Delete {sel.Count} conversations?", "Nova Enhance", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            foreach (var it in sel) try { File.Delete(it.Path); var h = System.IO.Path.ChangeExtension(it.Path, ".html"); if (File.Exists(h)) File.Delete(h); } catch { }
            picked.Clear(); current = null; Reload();
        });
        B("✗", () => { picked.Clear(); Fill(); });
    }

    static string Group(DateTime d)
    {
        var days = (DateTime.Today - d.Date).TotalDays;
        return days <= 0 ? "TODAY" : days <= 1 ? "YESTERDAY" : days <= 7 ? "LAST 7 DAYS" : days <= 31 ? "THIS MONTH" : d.ToString("MMMM yyyy").ToUpperInvariant();
    }

    static string Snippet(Conversations.Item it, List<string> words)
    {
        var text = it.Text.Replace("\n", " ");
        if (words.Count == 0) return it.Preview.Length > 110 ? it.Preview[..110] + "…" : it.Preview;
        var i = text.IndexOf(words[0], StringComparison.OrdinalIgnoreCase);
        if (i < 0) return it.Preview.Length > 110 ? it.Preview[..110] + "…" : it.Preview;
        var a = Math.Max(0, i - 40);
        var s = text.Substring(a, Math.Min(120, text.Length - a));
        return (a > 0 ? "…" : "") + s + "…";
    }

    void Open(Conversations.Item it)
    {
        current = it;
        var c = it.NovaChat ? Conversations.FromNovaChat(it.Path) : Conversations.Load(it.Path);
        if (c == null) return;
        shown = c;
        head.Text = $"{Conversations.Icon(c.Kind)} {c.Title}";
        meta.Text = $"{c.Created:dddd dd MMM yyyy HH:mm} → {c.Updated:HH:mm} · {c.Msgs.Count} messages · saved: {it.Path}";
        acts.Controls.Clear();
        if (c.Kind == "email" && c.Ref.Length > 0)
        {
            acts.Controls.Add(Th.B("↻ Continue this conversation", () => Screens.MailAskContinue(it.Path), true));
            acts.Controls.Add(Th.B("✉ Open the email", () => { try { Outlook.Open(c.Ref, c.RefStore); } catch (Exception e) { meta.Text = "⚠ " + e.Message; } }));
        }
        else if (c.Kind == "chat") acts.Controls.Add(Th.B("💬 Open Nova Chat", () => Screens.Hub(null), true));
        else if (c.Kind == "folder" && c.Ref.Length > 0) acts.Controls.Add(Th.B("📁 Ask in this folder again", () => Screens.FolderSearch(c.Ref), true));
        else if (c.Kind == "pdf" && File.Exists(c.Ref)) acts.Controls.Add(Th.B("📄 Open the PDF", () => Shell.Open(c.Ref), true));
        else if (c.Kind == "ask") acts.Controls.Add(Th.B("🔍 Ask again", () => Screens.AskFor(c.Msgs.FirstOrDefault(m => m.Role == "user")?.Text ?? ""), true));
        acts.Controls.Add(Th.B("⇩ Word", () =>
        {
            var p = Path.ChangeExtension(it.Path, null) + ".docx";
            Docx.Write(p, c.Title, "", string.Join("\n\n", c.Msgs.Select(m => (m.Role == "user" ? "YOU: " : "NOVA: ") + m.Text)), "Designed by A.HAMROUNI");
            Shell.Open(p);
        }));
        acts.Controls.Add(Th.B("📁 Show file", () => Shell.Select(it.Path)));
        acts.Controls.Add(Th.B("🗑", () =>
        {
            if (MessageBox.Show(this, $"Delete “{c.Title}”?", "Nova Enhance", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { File.Delete(it.Path); var h = Path.ChangeExtension(it.Path, ".html"); if (File.Exists(h)) File.Delete(h); } catch { }
            current = null; Reload();
        }));
        var html = "<!doctype html><html><head><meta charset=utf-8><style>" + Conversations.Css + "</style></head><body>" + Highlight(Conversations.Body(c, false)) + "</body></html>";
        if (web?.CoreWebView2 != null) web.CoreWebView2.NavigateToString(html); else pendingHtml = html;
        Fill();
    }

    string Highlight(string html)
    {
        foreach (var w in Regex.Split(search.Text.Trim(), @"\s+").Where(w => w.Length > 1))
            html = Regex.Replace(html, "(?<=>[^<]*)(" + Regex.Escape(WebUtility.HtmlEncode(w)) + ")", "<mark>$1</mark>", RegexOptions.IgnoreCase);
        return html;
    }
}
