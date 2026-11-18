using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NovaEnhance;

/// <summary>🕘 Search history: every search with its date, subject, answer and sources — Open, ▶ Continue, Copy. Designed by A.HAMROUNI</summary>
class HistoryForm : NovaForm
{
    readonly WebView2 view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Th.Bg };
    bool ready;

    public HistoryForm()
    {
        Text = "Nova Enhance · 🕘 Search history";
        Width = 1000; Height = 780; MinimumSize = new Size(640, 480);
        Controls.Add(view);
        Load += async (_, _) =>
        {
            try { await view.EnsureCoreWebView2Async(await HubForm.Env()); }
            catch (Exception e) { Log.Error("Search history · WebView2", e); Close(); return; }
            var core = view.CoreWebView2;
            var env = HubForm.Current;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.AddWebResourceRequestedFilter("https://nova.chat/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var name = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
                var bytes = NovaChat.Resource(name);
                var type = name.EndsWith(".js") ? "application/javascript" : name.EndsWith(".css") ? "text/css" : "text/html";
                e.Response = bytes == null ? env.CreateWebResourceResponse(null, 404, "Not found", "")
                    : env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", $"Content-Type: {type}; charset=utf-8");
            };
            core.WebMessageReceived += (_, e) => Handle(e.WebMessageAsJson);
            core.Navigate("https://nova.chat/history.html");
        };
        SearchHistory.Changed += Changed;
        FormClosed += (_, _) => SearchHistory.Changed -= Changed;
    }

    DateTime lastPush;
    void Changed()
    {
        // A running deep search saves often: the list follows at most every 3 s.
        if ((DateTime.Now - lastPush).TotalSeconds < 3) return;
        lastPush = DateTime.Now;
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Reload)); } catch { }
    }

    void Post(JsonObject o) { try { if (ready) view.CoreWebView2?.PostWebMessageAsJson(o.ToJsonString()); } catch { } }

    public async void Reload()
    {
        if (!ready) return;
        var list = await Task.Run(SearchHistory.List);
        Post(new JsonObject { ["t"] = "list", ["items"] = list, ["days"] = SearchHistory.Days, ["sign"] = Program.Sign });
    }

    void Handle(string json)
    {
        JsonObject m;
        try { m = JsonNode.Parse(json)?.AsObject(); } catch { return; }
        if (m == null) return;
        var id = m["id"]?.ToString() ?? "";
        try
        {
            switch (m["t"]?.ToString())
            {
                case "init": ready = true; Reload(); break;
                case "open":
                    var e = SearchHistory.Get(id);
                    if (e != null) { e.Remove("state"); e["status"] = SearchHistory.Status(SearchHistory.Get(id)); Post(new JsonObject { ["t"] = "entry", ["e"] = e }); }
                    break;
                case "src": NovaChat.OpenSource(m["src"] as JsonObject); break;
                case "copy": Clipboard.SetText(m["text"]?.ToString() is { Length: > 0 } t ? t : " "); break;
                case "del": SearchHistory.Delete(id); Reload(); break;
                case "clear":
                    if (MessageBox.Show(this, "Delete every saved search?", "Nova Enhance", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) { SearchHistory.Clear(); Reload(); }
                    break;
                case "days": SearchHistory.Days = m["d"]?.GetValue<int>() ?? 60; Reload(); break;
                case "chat": Screens.HubOpenChat(m["chat"]?.ToString()); break;
                case "cont":
                {
                    var x = SearchHistory.Get(id);
                    if (x != null) Screens.HubContinueDeep(x["subject"]?.ToString() ?? "", id);
                    break;
                }
                case "folder": Screens.FolderSearch(m["path"]?.ToString()); break;
            }
        }
        catch (Exception ex) { Log.Error("Search history", ex); }
    }
}
