using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NovaEnhance;

/// <summary>
/// ✦ Nova Chat & AI Hub: one window with Nova Chat (Nova's own AI with your files and emails) and your AI websites
/// (Gemini, ChatGPT, Claude, DeepSeek, Copilot, + your own), each signed in once and kept signed in.
/// Side by side shows two tabs at once; "Ask both" types the same question in both. Designed by A.HAMROUNI
/// </summary>
class HubForm : NovaForm
{
    class Tab { public string Name, Url; public WebView2 View; public Button Btn; public NovaChat Chat; public TaskCompletionSource Loaded = new(); public bool IsChat => Url == null; }

    static CoreWebView2Environment env;
    readonly List<Tab> tabs = new();
    readonly FlowLayoutPanel strip;
    readonly SplitContainer split;
    readonly Button sideBtn, sendSel;
    readonly TextBox both;
    readonly Button bothBtn;
    readonly Label hint;
    Tab left, right;

    static readonly (string Name, string Url)[] Defaults =
    {
        ("Gemini", "https://gemini.google.com/app"), ("ChatGPT", "https://chatgpt.com/"), ("Claude", "https://claude.ai/"),
        ("DeepSeek", "https://chat.deepseek.com/"), ("Copilot", "https://copilot.microsoft.com/")
    };

    static List<(string Name, string Url)> Sites()
    {
        try
        {
            var a = JsonNode.Parse(Store.Str("win_hub_sites", ""))?.AsArray();
            if (a != null) return a.OfType<JsonObject>().Select(o => (o["name"]?.ToString() ?? "Site", o["url"]?.ToString() ?? "")).Where(x => x.Item2.StartsWith("http")).ToList();
        }
        catch { }
        return Defaults.ToList();
    }

    static void SaveSites(IEnumerable<(string Name, string Url)> s) =>
        Store.Set("win_hub_sites", new JsonArray(s.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["url"] = x.Url }).ToArray()).ToJsonString());

    public HubForm()
    {
        Text = "Nova Enhance · Nova Chat & AI Hub";
        Width = 1400; Height = 900; MinimumSize = new Size(820, 560);
        KeyPreview = true;

        var top = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(0x0E, 0x11, 0x17), Padding = new Padding(8, 7, 8, 0) };
        strip = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, BackColor = top.BackColor };
        top.Controls.Add(strip);

        var tools = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Th.Card2, Padding = new Padding(8, 6, 8, 4) };
        var tl = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Th.Card2 };
        var tr = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Th.Card2, FlowDirection = FlowDirection.LeftToRight };
        sendSel = Th.B("⇢ Send selection", PasteClipboard, true);
        hint = Th.L("Ctrl+Shift+A in any program sends the selected text here", 9, Th.Muted);
        hint.Margin = new Padding(6, 8, 6, 0);
        tl.Controls.Add(sendSel); tl.Controls.Add(hint);
        both = new TextBox { Width = 320, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, Font = Th.F(10), PlaceholderText = "Same question to both…", Visible = false, Margin = new Padding(3, 4, 3, 3) };
        both.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AskBoth(); } };
        bothBtn = Th.B("⇄ Ask both", AskBoth); bothBtn.Visible = false;
        sideBtn = Th.B("◫ Side by side", SideBySide);
        tr.Controls.Add(both); tr.Controls.Add(bothBtn); tr.Controls.Add(sideBtn);
        tr.Controls.Add(Th.B("↻", () => left?.View?.CoreWebView2?.Reload()));
        tools.Controls.Add(tr); tools.Controls.Add(tl);

        split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Th.Bg, Panel2Collapsed = true, SplitterWidth = 4 };
        split.Panel1.BackColor = split.Panel2.BackColor = Th.Bg;

        Controls.Add(split);
        Controls.Add(tools);
        Controls.Add(top);
        Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 18, Text = Program.Sign + "  ", TextAlign = ContentAlignment.MiddleRight, ForeColor = Th.Muted, Font = Th.F(8), BackColor = Th.Bg });

        BuildTabs();
        Shown += async (_, _) =>
        {
            if (left != null) return;   // already opened by Ctrl+Shift+A
            var last = Store.Str("win_hub_last", "Nova Chat");
            await Select(tabs.FirstOrDefault(t => t.Name == last) ?? tabs[0]);
        };
    }

    void BuildTabs()
    {
        strip.Controls.Clear();
        var keep = tabs.ToDictionary(t => t.Name + "|" + t.Url, t => t);
        tabs.Clear();
        tabs.Add(Reuse(keep, "Nova Chat", null));
        foreach (var (n, u) in Sites()) tabs.Add(Reuse(keep, n, u));
        foreach (var t in tabs)
        {
            var b = new Button
            {
                Text = (t.IsChat ? "✦ " : "") + t.Name, AutoSize = true, FlatStyle = FlatStyle.Flat, Font = Th.F(10, t.IsChat), ForeColor = Th.Text,
                BackColor = Color.FromArgb(0x1A, 0x1F, 0x2A), Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 4, 0), Height = 34, UseMnemonic = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += async (_, _) => await Select(t);
            if (!t.IsChat)
            {
                var m = new ContextMenuStrip { Font = Th.F(10) };
                m.Items.Add("↻ Reload", null, (_, _) => t.View?.CoreWebView2?.Reload());
                m.Items.Add("⌂ Back to start page", null, (_, _) => t.View?.CoreWebView2?.Navigate(t.Url));
                m.Items.Add("Open in my browser", null, (_, _) => Shell.Open(t.View?.CoreWebView2?.Source ?? t.Url));
                m.Items.Add("Rename…", null, (_, _) => { var n = Prompt.Ask(this, "Rename", "Name of this tab:", t.Name); if (!string.IsNullOrWhiteSpace(n)) { SaveSites(Sites().Select(x => x.Url == t.Url ? (n.Trim(), x.Url) : x)); t.Name = n.Trim(); BuildTabs(); } });
                m.Items.Add("Remove this site", null, (_, _) => { SaveSites(Sites().Where(x => x.Url != t.Url)); t.View?.Dispose(); t.View = null; if (left == t) left = null; if (right == t) right = null; BuildTabs(); _ = Select(tabs[0]); });
                b.ContextMenuStrip = m;
            }
            t.Btn = b;
            strip.Controls.Add(b);
        }
        var add = new Button { Text = "＋ Add a site", AutoSize = true, FlatStyle = FlatStyle.Flat, Font = Th.F(10), ForeColor = Th.Muted, BackColor = Color.FromArgb(0x1A, 0x1F, 0x2A), Height = 34, Padding = new Padding(8, 2, 8, 2) };
        add.FlatAppearance.BorderSize = 0;
        add.Click += (_, _) => AddSite();
        strip.Controls.Add(add);
        Highlight();
    }

    static Tab Reuse(Dictionary<string, Tab> keep, string name, string url) => keep.TryGetValue(name + "|" + url, out var t) ? t : new Tab { Name = name, Url = url };

    void AddSite()
    {
        var url = Prompt.Ask(this, "Add a site", "Address of the AI website (e.g. https://www.perplexity.ai):", "https://");
        if (string.IsNullOrWhiteSpace(url) || url.Trim() == "https://") return;
        url = url.Trim();
        if (!url.StartsWith("http")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) { MessageBox.Show(this, "That address doesn't look right.", "Nova Enhance"); return; }
        var name = Prompt.Ask(this, "Add a site", "Name for the tab:", u.Host.Replace("www.", "").Split('.')[0] is var h && h.Length > 0 ? char.ToUpper(h[0]) + h[1..] : u.Host);
        if (string.IsNullOrWhiteSpace(name)) return;
        var s = Sites(); s.Add((name.Trim(), url)); SaveSites(s);
        BuildTabs();
        _ = Select(tabs.Last());
    }

    void Highlight()
    {
        foreach (var t in tabs)
        {
            if (t.Btn == null) continue;
            bool on = t == left || t == right;
            t.Btn.BackColor = t == left ? Th.Card2 : on ? Color.FromArgb(0x2A, 0x26, 0x4A) : Color.FromArgb(0x1A, 0x1F, 0x2A);
            t.Btn.ForeColor = on ? Color.White : Th.Text;
        }
    }

    // ================================================================ web views

    static async Task<CoreWebView2Environment> Env()
    {
        if (env != null) return env;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova Enhance", "WebView");
        Directory.CreateDirectory(data);
        env = await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions());
        return env;
    }

    async Task<WebView2> Ensure(Tab t)
    {
        if (t.View != null) return t.View;
        var v = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = t.IsChat ? Th.Bg : Color.White };
        t.View = v;
        try
        {
            await v.EnsureCoreWebView2Async(await Env());
        }
        catch (Exception e)
        {
            Log.Error("AI window · WebView2", e);
            t.View = null; v.Dispose();
            MessageBox.Show(this, "The Microsoft Edge WebView2 component is missing or blocked on this PC, so the AI window can't open.\n\n" + e.Message +
                "\n\nInstall \"Microsoft Edge WebView2 Runtime\" from Microsoft, or ask IT.", "Nova Enhance");
            return null;
        }
        var core = v.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += (s, e) => OnNewWindow(e);
        core.NavigationCompleted += (_, _) => t.Loaded.TrySetResult();
        if (t.IsChat)
        {
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsWebMessageEnabled = true;
            core.AddWebResourceRequestedFilter("https://nova.chat/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var name = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
                if (name.Length == 0) name = "chat.html";
                var bytes = NovaChat.Resource(name);
                var type = name.EndsWith(".js") ? "application/javascript" : name.EndsWith(".css") ? "text/css" : "text/html";
                e.Response = bytes == null
                    ? env.CreateWebResourceResponse(null, 404, "Not found", "")
                    : env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", $"Content-Type: {type}; charset=utf-8");
            };
            t.Chat = new NovaChat(v, this) { PendingText = pendingChat };
            pendingChat = null;
            core.WebMessageReceived += (_, e) => t.Chat.Handle(e.WebMessageAsJson);
            core.Navigate("https://nova.chat/chat.html");
        }
        else core.Navigate(t.Url);
        return v;
    }

    static readonly string[] AuthHosts = { "accounts.google.com", "login.microsoftonline.com", "login.live.com", "appleid.apple.com", "auth.openai.com", "auth0.openai.com", "login.microsoft.com", "github.com" };

    /// <summary>Sign-in pop-ups open in a small Nova window (so the sign-in completes); other links open in your normal browser.</summary>
    async void OnNewWindow(CoreWebView2NewWindowRequestedEventArgs e)
    {
        var uri = e.Uri ?? "";
        Uri.TryCreate(uri, UriKind.Absolute, out var u);
        bool auth = uri.Length == 0 || uri == "about:blank" || (u != null && AuthHosts.Any(h => u.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase))) || e.IsUserInitiated == false;
        if (!auth) { e.Handled = true; Shell.Open(uri); return; }
        var deferral = e.GetDeferral();
        try
        {
            var f = new NovaForm { Text = "Sign in", Width = 520, Height = 700, StartPosition = FormStartPosition.CenterParent };
            var pv = new WebView2 { Dock = DockStyle.Fill };
            f.Controls.Add(pv);
            f.Show(this);
            await pv.EnsureCoreWebView2Async(await Env());
            pv.CoreWebView2.WindowCloseRequested += (_, _) => f.Close();
            e.NewWindow = pv.CoreWebView2;
            e.Handled = true;
        }
        catch (Exception ex) { Log.Error("AI window · pop-up", ex); }
        finally { deferral.Complete(); }
    }

    async Task Select(Tab t)
    {
        if (t == null) return;
        if (t == right) { right = left; left = t; }
        else left = t;
        Store.Set("win_hub_last", t.Name);
        await Place();
    }

    async Task Place()
    {
        var lv = await Ensure(left);
        split.Panel1.Controls.Clear();
        if (lv != null) split.Panel1.Controls.Add(lv);
        bool sbs = right != null && right != left;
        split.Panel2Collapsed = !sbs;
        split.Panel2.Controls.Clear();
        if (sbs)
        {
            var rv = await Ensure(right);
            if (rv != null) split.Panel2.Controls.Add(rv);
            split.SplitterDistance = Math.Max(200, split.Width / 2);
        }
        both.Visible = bothBtn.Visible = sbs;
        sideBtn.Text = sbs ? $"◫ {left.Name} | {right.Name}  ✕" : "◫ Side by side";
        Highlight();
        lv?.Focus();
    }

    void SideBySide()
    {
        if (right != null) { right = null; _ = Place(); return; }
        var m = new ContextMenuStrip { Font = Th.F(10) };
        m.Items.Add(new ToolStripLabel("Show next to " + left?.Name + ":") { ForeColor = Color.Gray });
        foreach (var t in tabs.Where(x => x != left))
        {
            var tt = t;
            m.Items.Add((tt.IsChat ? "✦ " : "") + tt.Name, null, async (_, _) => { right = tt; await Place(); });
        }
        m.Show(sideBtn, new Point(0, sideBtn.Height));
    }

    // ================================================================ typing into the AI

    /// <summary>Types the text into the page's message box; with send, also presses the page's Send button.</summary>
    static string InjectJs(string text, bool send) =>
        "(function(t,send){" +
        "function vis(e){var r=e.getBoundingClientRect();return r.width>40&&r.height>10&&getComputedStyle(e).visibility!=='hidden';}" +
        "var c=[].slice.call(document.querySelectorAll('textarea,[contenteditable=\"true\"],div[role=\"textbox\"]')).filter(vis);" +
        "var el=document.activeElement;if(!el||!(el.isContentEditable||el.tagName==='TEXTAREA'))el=c[c.length-1];" +
        "if(!el)return 'noinput';el.focus();" +
        "if(el.tagName==='TEXTAREA'){var set=Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set;set.call(el,(el.value?el.value+'\\n':'')+t);el.dispatchEvent(new Event('input',{bubbles:true}));}" +
        "else{document.execCommand('insertText',false,t);}" +
        "if(send){setTimeout(function(){var b=document.querySelector('button[data-testid=\"send-button\"],button[aria-label*=\"Send\" i],button[aria-label*=\"Submit\" i],button[type=\"submit\"]');" +
        "if(b&&!b.disabled)b.click();else el.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true}));},450);}" +
        "return 'ok';})(" + JsonSerializer.Serialize(text) + "," + (send ? "true" : "false") + ")";

    async Task Type(Tab t, string text, bool send)
    {
        if (t == null || string.IsNullOrWhiteSpace(text)) return;
        var v = await Ensure(t);
        if (v == null) return;
        // A page that was just opened needs to finish loading (and draw its message box) first.
        if (!t.Loaded.Task.IsCompleted) { await Task.WhenAny(t.Loaded.Task, Task.Delay(20000)); await Task.Delay(t.IsChat ? 300 : 1800); }
        if (t.IsChat) { if (send) t.Chat?.AskNow(text); else t.Chat?.Insert(text); return; }
        try
        {
            var r = await v.CoreWebView2.ExecuteScriptAsync(InjectJs(text, send));
            if (r.Contains("noinput")) { Clipboard.SetText(text); Program.Balloon("Nova Enhance", $"{t.Name}: click in the message box and press Ctrl+V — the text is on the clipboard."); }
        }
        catch (Exception e) { Log.Error("AI window · type", e); }
    }

    static string pendingChat;

    /// <summary>Ctrl+Shift+A: the text selected in another program goes into the active tab (not sent, so you can add your instruction).</summary>
    public async void SendText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { Program.Balloon("Nova Enhance", "Select some text first, then press Ctrl+Shift+A."); return; }
        if (left == null) { var last = Store.Str("win_hub_last", "Nova Chat"); left = tabs.FirstOrDefault(t => t.Name == last) ?? tabs[0]; }
        bool fresh = left.View == null;
        if (left.IsChat && fresh) pendingChat = text;   // the chat page picks it up when it opens
        await Place();
        if (!(left.IsChat && fresh)) await Type(left, text, false);
    }

    void PasteClipboard()
    {
        string t = "";
        try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { }
        if (t.Length == 0) { MessageBox.Show(this, "Select text in any program and press Ctrl+Shift+A — or copy it first (Ctrl+C) and click this button.", "Nova Enhance"); return; }
        _ = Type(left, t, false);
    }

    async void AskBoth()
    {
        var q = both.Text.Trim();
        if (q.Length == 0 || right == null) return;
        both.Text = "";
        await Type(left, q, true);
        await Type(right, q, true);
    }
}
