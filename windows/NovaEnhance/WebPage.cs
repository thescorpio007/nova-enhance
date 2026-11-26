using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NovaEnhance;

/// <summary>A Nova window drawn by one of the built-in pages (Chat folder: brain.html, submittal.html…), talking to C# with messages. Designed by A.HAMROUNI</summary>
abstract class WebPage : NovaForm
{
    protected readonly WebView2 View = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Th.Bg };
    protected bool Ready;
    readonly string name;

    protected WebPage(string title, string page, int w, int h)
    {
        Text = "Nova Enhance · " + title;
        name = title;
        Width = w; Height = h; MinimumSize = new Size(760, 520);
        Controls.Add(View);
        Load += async (_, _) =>
        {
            try { await View.EnsureCoreWebView2Async(await HubForm.Env()); }
            catch (Exception e) { Log.Error(title + " · WebView2", e); Close(); return; }
            var core = View.CoreWebView2;
            var env = HubForm.Current;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.AddWebResourceRequestedFilter("https://nova.chat/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var name = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
                var bytes = NovaChat.Resource(name);
                var type = name.EndsWith(".js") ? "application/javascript" : name.EndsWith(".css") ? "text/css" : "text/html";
                e.Response = bytes == null ? env.CreateWebResourceResponse(null, 404, "Not found", "")
                    : env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", $"Content-Type: {type}; charset=utf-8");
            };
            core.WebMessageReceived += (_, e) =>
            {
                JsonObject m;
                try { m = JsonNode.Parse(e.WebMessageAsJson)?.AsObject(); } catch { return; }
                if (m == null) return;
                if (m["t"]?.ToString() == "init") Ready = true;
                // ● ● ● a “thinking” card asks what Nova is waiting for (after 12 quiet seconds)
                if (m["t"]?.ToString() == "think.ask") { Post(new JsonObject { ["t"] = "think.wait", ["text"] = Busy.Line() }); return; }
                if (m["t"]?.ToString() == "rowmenu") { RowMenu(m, this); return; }
                if (m["t"]?.ToString() == "filemenu") { FileMenuFrom(m, this); return; }
                try { OnMessage(m); } catch (Exception ex) { Log.Error(title, ex); Post(new JsonObject { ["t"] = "error", ["text"] = ex.Message }); }
            };
            core.Navigate("https://nova.chat/" + page);
        };
    }

    protected abstract void OnMessage(JsonObject m);

    /// <summary>🖱 Right-click on an email row of a Nova page: Nova's full email menu at the mouse.</summary>
    public static void RowMenu(JsonObject m, Control owner)
    {
        try
        {
            MailHit h = null;
            if (long.TryParse(m["id"]?.ToString(), out var id) && id > 0) h = MailIndex.ByIds(new[] { id }, 1, 0).FirstOrDefault();
            var entry = m["entry"]?.ToString() ?? "";
            if (h == null && entry.Length > 0) h = MailIndex.ByEntry(entry) ?? new MailHit { Entry = entry, StoreId = m["store"]?.ToString() ?? "", Subject = "" };
            if (h == null) return;
            EmailMenu.Show(h, owner, Cursor.Position, rowText: m["row"]?.ToString());
        }
        catch (Exception e) { Log.Warn("row menu", e.Message); }
    }

    /// <summary>📄 Right-click or ⋯ on a file row of a Nova page: Nova's file menu at the mouse.</summary>
    public static void FileMenuFrom(JsonObject m, Control owner)
    {
        var p = m["path"]?.ToString() ?? "";
        if (p.Length == 0 || p.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        FileMenu.Show(p, owner, Cursor.Position, new FileMenu.At { Loc = m["loc"]?.ToString() ?? "", Goto = m["go"]?.ToString() ?? "" });
    }

    public void Post(JsonObject o)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(() => Post(o))); } catch { } return; }
        try { if (Ready) View.CoreWebView2?.PostWebMessageAsJson(o.ToJsonString()); } catch { }
    }

    /// <summary>Runs slow work off the window, then posts what it returns (or the error).</summary>
    protected void Work(string doing, Func<JsonObject> f)
    {
        Post(new JsonObject { ["t"] = "busy", ["text"] = doing });
        _ = Task.Run(() =>
        {
            using var busy = Busy.Begin(doing.TrimEnd('…', '.'));
            try { var r = f(); if (r != null) Post(r); }
            catch (Exception e) { Log.Error(name, e); Post(new JsonObject { ["t"] = "error", ["text"] = e.Message }); }
        });
    }
}
