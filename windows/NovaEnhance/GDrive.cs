using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

class DriveFile
{
    public string Id, Name, Mime, DriveId;
    public long Size;
    public DateTimeOffset Modified;
    public bool IsFolder => Mime == "application/vnd.google-apps.folder";
    public bool IsGoogle => Mime != null && Mime.StartsWith("application/vnd.google-apps.");
}

/// <summary>
/// Google Drive, read-only, with the user's own Google sign-in (OAuth for desktop apps, PKCE, loopback on this PC).
/// The sign-in is kept locked to the Windows account. Google Docs, Sheets and Slides are exported as Word, Excel and PowerPoint for reading.
/// </summary>
static class GDrive
{
    const string Scope = "https://www.googleapis.com/auth/drive.readonly https://www.googleapis.com/auth/drive.file";
    const string Upload = "https://www.googleapis.com/upload/drive/v3";
    const string BackupFolder = "Nova Enhance Backups";
    const string Api = "https://www.googleapis.com/drive/v3";
    public const string Folder = "application/vnd.google-apps.folder";

    public static string ClientId { get => Store.Str("win_gd_client"); set => Store.Set("win_gd_client", value?.Trim()); }
    public static string ClientSecret { get => Store.Secret("win_gd_secret"); set => Store.SetSecret("win_gd_secret", value?.Trim()); }
    static string Refresh { get => Store.Secret("win_gd_refresh"); set => Store.SetSecret("win_gd_refresh", value); }
    public static string Account => Store.Str("win_gd_email");
    public static bool Configured => ClientId.Length > 0 && ClientSecret.Length > 0;
    public static bool SignedIn => Configured && Refresh.Length > 0;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    static string access;
    static DateTime accessUntil;
    static readonly SemaphoreSlim TokenGate = new(1, 1);

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Opens Google's sign-in page in the browser and waits for the answer on this PC (5 minutes).</summary>
    public static async Task<string> SignIn()
    {
        if (!Configured) throw new InvalidOperationException("Enter the Client ID and Client secret first (see the setup steps).");
        var verifier = B64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(16));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://127.0.0.1:{port}";
        var url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent" +
                  $"&client_id={Uri.EscapeDataString(ClientId)}&redirect_uri={Uri.EscapeDataString(redirect)}&scope={Uri.EscapeDataString(Scope + " email")}" +
                  $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";
        Shell.Open(url);
        string code = null, error = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            while (code == null && error == null)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                using var stream = client.GetStream();
                var buf = new byte[8192];
                int n = await stream.ReadAsync(buf, cts.Token);
                var req = Encoding.ASCII.GetString(buf, 0, n);
                var line = req.Split('\n')[0];
                var m = Regex.Match(line, @"GET (\S+) HTTP");
                var q = m.Success && m.Groups[1].Value.Contains('?') ? m.Groups[1].Value[(m.Groups[1].Value.IndexOf('?') + 1)..] : "";
                var parts = q.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                    .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
                bool ours = parts.TryGetValue("state", out var st) && st == state;
                if (ours && parts.TryGetValue("code", out var c)) code = c;
                else if (ours && parts.TryGetValue("error", out var er)) error = er;
                var ok = code != null;
                var html = $"<!doctype html><meta charset=utf-8><title>Nova Enhance</title><body style=\"font-family:Segoe UI,Arial;background:#12151C;color:#ECEEF3;text-align:center;padding-top:80px\"><h2>{(ok ? "✓ Nova Enhance is connected to Google Drive" : "Google sign-in was not completed")}</h2><p>{(ok ? "You can close this tab and go back to Nova." : WebUtility.HtmlEncode(error ?? "Waiting…"))}</p><p style=\"color:#6B7385\">{Program.Sign}</p>";
                var bytes = Encoding.UTF8.GetBytes(html);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, cts.Token);
                await stream.WriteAsync(bytes, cts.Token);
            }
        }
        catch (OperationCanceledException) { throw new TimeoutException("Google sign-in took more than 5 minutes — try again."); }
        finally { listener.Stop(); }
        if (code == null) throw new InvalidOperationException("Google sign-in was not completed: " + error);

        var tok = await PostForm("https://oauth2.googleapis.com/token", new()
        {
            ["code"] = code, ["client_id"] = ClientId, ["client_secret"] = ClientSecret, ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code", ["code_verifier"] = verifier
        });
        var refresh = tok["refresh_token"]?.ToString();
        if (string.IsNullOrEmpty(refresh)) throw new InvalidOperationException("Google did not give a lasting sign-in — remove Nova in your Google account's third-party access and sign in again.");
        Refresh = refresh;
        Store.Set("win_gd_scope", tok["scope"]?.ToString() ?? "");
        access = tok["access_token"]?.ToString();
        accessUntil = DateTime.UtcNow.AddSeconds((tok["expires_in"]?.GetValue<int>() ?? 3600) - 120);
        try
        {
            var about = await GetJson($"{Api}/about?fields=user(emailAddress,displayName)");
            Store.Set("win_gd_email", about["user"]?["emailAddress"]?.ToString() ?? "");
        }
        catch { }
        return Account;
    }

    /// <summary>True when the sign-in allows Nova to save its own files (backups) in Drive. Older sign-ins were read-only.</summary>
    public static bool CanSave => SignedIn && Store.Str("win_gd_scope").Contains("drive.file");

    static async Task<HttpResponseMessage> Send(HttpMethod method, string url, HttpContent body = null)
    {
        using var req = new HttpRequestMessage(method, url) { Content = body };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
        return await Http.SendAsync(req);
    }

    static async Task<JsonObject> SendJson(HttpMethod method, string url, HttpContent body = null)
    {
        using var res = await Send(method, url, body);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            string msg = text;
            try { msg = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? text; } catch { }
            if ((int)res.StatusCode == 403 && msg.Contains("scope", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Google sign-in doesn't allow saving backups — sign in again in Settings → Google Drive.");
            throw new InvalidOperationException($"Google Drive {(int)res.StatusCode}: {msg}");
        }
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
    }

    static async Task<string> BackupFolderId()
    {
        var cached = Store.Str("win_gd_backup_folder");
        if (cached.Length > 0)
        {
            try
            {
                var f = await SendJson(HttpMethod.Get, $"{Api}/files/{cached}?fields=id,trashed");
                if (f["trashed"]?.GetValue<bool>() != true) return cached;
            }
            catch { }
        }
        var q = Uri.EscapeDataString($"name='{BackupFolder}' and mimeType='{Folder}' and trashed=false");
        var list = await SendJson(HttpMethod.Get, $"{Api}/files?q={q}&fields=files(id)&spaces=drive");
        var id = (list["files"] as JsonArray)?.FirstOrDefault()?["id"]?.ToString();
        if (id == null)
        {
            var meta = new JsonObject { ["name"] = BackupFolder, ["mimeType"] = Folder };
            id = (await SendJson(HttpMethod.Post, $"{Api}/files?fields=id", new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json")))["id"]?.ToString();
        }
        Store.Set("win_gd_backup_folder", id ?? "");
        return id;
    }

    /// <summary>Copies a backup file into "Nova Enhance Backups" in the user's Drive; keeps the newest <paramref name="keepAuto"/> automatic copies.</summary>
    public static async Task SaveBackup(string path, int keepAuto)
    {
        if (!CanSave) throw new InvalidOperationException("Sign in to Google Drive again in Settings → Google Drive to allow saving backups.");
        var folder = await BackupFolderId();
        var meta = new JsonObject { ["name"] = Path.GetFileName(path), ["parents"] = new JsonArray(JsonValue.Create(folder)) };
        using var body = new MultipartContent("related");
        body.Add(new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json"));
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Add(file);
        var up = await SendJson(HttpMethod.Post, $"{Upload}/files?uploadType=multipart&fields=id,size", body);
        if (up["id"] == null) throw new InvalidOperationException("Google Drive did not confirm the upload.");

        var q = Uri.EscapeDataString($"'{folder}' in parents and name contains '-auto' and trashed=false");
        var list = await SendJson(HttpMethod.Get, $"{Api}/files?q={q}&fields=files(id,name)&pageSize=200&orderBy=name desc");
        foreach (var old in (list["files"] as JsonArray ?? new JsonArray()).Skip(keepAuto))
        {
            var oldId = old?["id"]?.ToString();
            if (oldId != null) try { using var _ = await Send(HttpMethod.Delete, $"{Api}/files/{oldId}"); } catch { }
        }
    }

    public static async Task SignOut()
    {
        var r = Refresh;
        Refresh = ""; access = null; Store.Set("win_gd_email", ""); Store.Set("win_gd_scope", "");
        if (r.Length > 0)
            try { await Http.PostAsync("https://oauth2.googleapis.com/revoke?token=" + Uri.EscapeDataString(r), null); } catch { }
    }

    static async Task<JsonObject> PostForm(string url, Dictionary<string, string> form)
    {
        using var res = await Http.PostAsync(url, new FormUrlEncodedContent(form));
        var text = await res.Content.ReadAsStringAsync();
        JsonObject j;
        try { j = JsonNode.Parse(text)?.AsObject() ?? new JsonObject(); } catch { j = new JsonObject(); }
        if (!res.IsSuccessStatusCode)
        {
            var err = j["error_description"]?.ToString() ?? j["error"]?.ToString() ?? text;
            if (j["error"]?.ToString() == "invalid_grant") { Refresh = ""; throw new InvalidOperationException("The Google sign-in has expired — sign in again in Settings → Google Drive."); }
            throw new InvalidOperationException("Google: " + err);
        }
        return j;
    }

    public static async Task<string> Token()
    {
        if (access != null && DateTime.UtcNow < accessUntil) return access;
        await TokenGate.WaitAsync();
        try
        {
            if (access != null && DateTime.UtcNow < accessUntil) return access;
            if (!SignedIn) throw new InvalidOperationException("Not signed in to Google Drive (Settings → Google Drive).");
            var tok = await PostForm("https://oauth2.googleapis.com/token", new()
            {
                ["client_id"] = ClientId, ["client_secret"] = ClientSecret, ["refresh_token"] = Refresh, ["grant_type"] = "refresh_token"
            });
            access = tok["access_token"]?.ToString();
            accessUntil = DateTime.UtcNow.AddSeconds((tok["expires_in"]?.GetValue<int>() ?? 3600) - 120);
            return access;
        }
        finally { TokenGate.Release(); }
    }

    static async Task<HttpResponseMessage> Get(string url, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
            var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (((int)res.StatusCode is 429 or 500 or 502 or 503) && attempt < 3) { res.Dispose(); await Task.Delay(1500 * (attempt + 1), ct); continue; }
            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { res.Dispose(); access = null; continue; }
            return res;
        }
    }

    static async Task<JsonObject> GetJson(string url, CancellationToken ct = default)
    {
        using var res = await Get(url, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string msg = text;
            try { msg = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? text; } catch { }
            throw new InvalidOperationException($"Google Drive {(int)res.StatusCode}: {msg}");
        }
        return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
    }

    static DriveFile Parse(JsonNode n)
    {
        long.TryParse(n?["size"]?.ToString(), out var size);
        DateTimeOffset.TryParse(n?["modifiedTime"]?.ToString(), out var mod);
        return new DriveFile { Id = n?["id"]?.ToString(), Name = n?["name"]?.ToString() ?? "", Mime = n?["mimeType"]?.ToString() ?? "", Size = size, Modified = mod, DriveId = n?["driveId"]?.ToString() };
    }

    const string Fields = "nextPageToken,files(id,name,mimeType,size,modifiedTime,driveId)";

    static async Task<List<DriveFile>> Query(string q, CancellationToken ct = default)
    {
        var list = new List<DriveFile>();
        string page = null;
        do
        {
            var url = $"{Api}/files?q={Uri.EscapeDataString(q)}&fields={Uri.EscapeDataString(Fields)}&pageSize=1000&supportsAllDrives=true&includeItemsFromAllDrives=true&corpora=allDrives"
                      + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            var j = await GetJson(url, ct);
            foreach (var f in j["files"] as JsonArray ?? new JsonArray()) list.Add(Parse(f));
            page = j["nextPageToken"]?.ToString();
        } while (!string.IsNullOrEmpty(page) && !ct.IsCancellationRequested);
        return list;
    }

    public static Task<List<DriveFile>> Children(string folderId, CancellationToken ct = default) =>
        Query($"'{folderId.Replace("'", "\\'")}' in parents and trashed=false", ct);

    public static async Task<List<DriveFile>> SharedDrives()
    {
        var list = new List<DriveFile>();
        try
        {
            var j = await GetJson($"{Api}/drives?pageSize=100");
            foreach (var d in j["drives"] as JsonArray ?? new JsonArray())
                list.Add(new DriveFile { Id = d?["id"]?.ToString(), Name = d?["name"]?.ToString() ?? "", Mime = Folder });
        }
        catch (Exception e) { Log.Error("shared drives", e); }
        return list;
    }

    public static Task<List<DriveFile>> SharedWithMeFolders() => Query($"sharedWithMe and mimeType='{Folder}' and trashed=false");

    /// <summary>Extension used to read a Drive file (Google Docs → .docx, Sheets → .xlsx, Slides → .pptx); "" = not readable.</summary>
    public static string ReadExt(DriveFile f) => f.Mime switch
    {
        "application/vnd.google-apps.document" => ".docx",
        "application/vnd.google-apps.spreadsheet" => ".xlsx",
        "application/vnd.google-apps.presentation" => ".pptx",
        _ when f.IsGoogle => "",
        _ => Path.GetExtension(f.Name).ToLowerInvariant()
    };

    static string ExportMime(string ext) => ext switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };

    /// <summary>Downloads (or exports) a Drive file to a local file.</summary>
    public static async Task Download(string id, string mime, string dst, CancellationToken ct = default)
    {
        var google = mime != null && mime.StartsWith("application/vnd.google-apps.");
        var ext = Path.GetExtension(dst).ToLowerInvariant();
        var url = google
            ? $"{Api}/files/{id}/export?mimeType={Uri.EscapeDataString(ExportMime(ext))}"
            : $"{Api}/files/{id}?alt=media&supportsAllDrives=true";
        using var res = await Get(url, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Google Drive {(int)res.StatusCode} for {Path.GetFileName(dst)}");
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        await using var fs = File.Create(dst);
        await s.CopyToAsync(fs, ct);
    }

    public static string WebLink(string id, string mime) => mime switch
    {
        "application/vnd.google-apps.document" => $"https://docs.google.com/document/d/{id}/edit",
        "application/vnd.google-apps.spreadsheet" => $"https://docs.google.com/spreadsheets/d/{id}/edit",
        "application/vnd.google-apps.presentation" => $"https://docs.google.com/presentation/d/{id}/edit",
        Folder => $"https://drive.google.com/drive/folders/{id}",
        _ => $"https://drive.google.com/file/d/{id}/view"
    };

    /// <summary>Google Drive for desktop puts small shortcut files (.gdoc, .gsheet, .gslides) for Google files: their Drive id.</summary>
    public static string IdFromShortcut(string text)
    {
        try
        {
            var j = JsonNode.Parse(text);
            var id = j?["doc_id"]?.ToString();
            if (!string.IsNullOrEmpty(id)) return id;
            var rid = j?["resource_id"]?.ToString();
            if (!string.IsNullOrEmpty(rid)) return rid.Contains(':') ? rid[(rid.IndexOf(':') + 1)..] : rid;
            var url = j?["url"]?.ToString() ?? "";
            var m = Regex.Match(url, @"/d/([\w-]{10,})|[?&]id=([\w-]{10,})");
            if (m.Success) return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }
        catch { }
        var m2 = Regex.Match(text ?? "", @"""doc_id""\s*:\s*""([\w-]+)""");
        return m2.Success ? m2.Groups[1].Value : null;
    }

    public static string MimeForShortcut(string ext) => ext switch
    {
        ".gdoc" => "application/vnd.google-apps.document",
        ".gsheet" => "application/vnd.google-apps.spreadsheet",
        _ => "application/vnd.google-apps.presentation"
    };

    /// <summary>Root key used in the folder index for a Drive folder.</summary>
    public static string RootKey(string id, string name) => $"gdrive:{id}:{name.Replace('\\', '-').Replace(':', '-')}";
    public static bool IsRoot(string root) => root != null && root.StartsWith("gdrive:");
    public static (string Id, string Name) ParseRoot(string root)
    {
        var rest = root[7..];
        var i = rest.IndexOf(':');
        return i < 0 ? (rest, rest) : (rest[..i], rest[(i + 1)..]);
    }
    public static string Display(string root) => IsRoot(root) ? "☁ Google Drive › " + ParseRoot(root).Name : root;
}

/// <summary>Pick a Google Drive folder: My Drive, shared drives and folders shared with you.</summary>
class DrivePicker : NovaForm
{
    readonly ListBox list;
    readonly Label path, status;
    readonly Stack<(string Id, string Name)> trail = new();
    List<DriveFile> items = new();
    public (string Id, string Name) Picked;

    public DrivePicker()
    {
        Text = "Choose a Google Drive folder";
        Width = 620; Height = 600;
        MinimumSize = new Size(480, 420);
        var stack = new Stack();
        Controls.Add(stack);
        stack.Add(Th.L("☁ Google Drive", 13, Th.Text, true));
        path = stack.Add(Th.Wrap("", 9.5f, Th.AccentText));
        list = stack.Add(new ListBox { Height = 330, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, Font = Th.F(10.5f), IntegralHeight = false }, true);
        list.DoubleClick += (_, _) => OpenSelected();
        status = stack.Add(Th.Wrap("", 9));
        var row = stack.Add(Th.Row(false));
        row.Controls.Add(Th.B("Open ▸", OpenSelected));
        row.Controls.Add(Th.B("◂ Back", Back));
        row.Controls.Add(Th.B("✓ Search this folder", Choose, true));
        row.Controls.Add(Th.B("Cancel", () => { DialogResult = DialogResult.Cancel; }));
        Shown += async (_, _) => await Home();
    }

    async Task Home()
    {
        trail.Clear();
        path.Text = "Google Drive";
        status.Text = "Loading…";
        items = new List<DriveFile> { new() { Id = "root", Name = "📁 My Drive", Mime = GDrive.Folder } };
        try
        {
            foreach (var d in await GDrive.SharedDrives()) items.Add(new DriveFile { Id = d.Id, Name = "🏢 " + d.Name, Mime = GDrive.Folder });
            foreach (var f in (await GDrive.SharedWithMeFolders()).OrderBy(f => f.Name)) items.Add(new DriveFile { Id = f.Id, Name = "👥 " + f.Name, Mime = GDrive.Folder });
            status.Text = "Double-click to open a folder, then ✓ Search this folder";
        }
        catch (Exception e) { status.Text = "⚠ " + e.Message; }
        Fill();
    }

    void Fill()
    {
        list.Items.Clear();
        foreach (var f in items) list.Items.Add(f.IsFolder ? (f.Name.StartsWith("📁") || f.Name.StartsWith("🏢") || f.Name.StartsWith("👥") ? f.Name : "📁 " + f.Name) : "     " + f.Name);
    }

    async void OpenSelected()
    {
        var i = list.SelectedIndex;
        if (i < 0 || i >= items.Count || !items[i].IsFolder) return;
        var f = items[i];
        trail.Push((f.Id, Clean(f.Name)));
        await Load(f.Id);
    }

    static string Clean(string n) => Regex.Replace(n, @"^(📁|🏢|👥)\s*", "");

    async Task Load(string id)
    {
        path.Text = "Google Drive › " + string.Join(" › ", trail.Reverse().Select(t => t.Name));
        status.Text = "Loading…";
        try
        {
            var kids = await GDrive.Children(id);
            items = kids.Where(k => k.IsFolder).OrderBy(k => k.Name).Concat(kids.Where(k => !k.IsFolder).OrderBy(k => k.Name)).ToList();
            status.Text = $"{kids.Count(k => k.IsFolder)} folders · {kids.Count(k => !k.IsFolder)} files";
        }
        catch (Exception e) { status.Text = "⚠ " + e.Message; items = new(); }
        Fill();
    }

    async void Back()
    {
        if (trail.Count <= 1) { await Home(); return; }
        trail.Pop();
        await Load(trail.Peek().Id);
    }

    void Choose()
    {
        var i = list.SelectedIndex;
        if (i >= 0 && i < items.Count && items[i].IsFolder && trail.Count == 0) Picked = (items[i].Id, Clean(items[i].Name));
        else if (trail.Count > 0) Picked = trail.Peek();
        else { status.Text = "Open or select a folder first"; return; }
        DialogResult = DialogResult.OK;
    }
}
