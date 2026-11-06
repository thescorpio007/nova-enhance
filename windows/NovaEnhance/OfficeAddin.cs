using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>
/// The ✦ Nova tab inside Word, Excel, PowerPoint and Outlook. The panel is served by Nova itself on this PC
/// (https://localhost:47320, with a certificate made for this PC), so the AI keys never leave Nova.
/// Install: (A) for me — Word/Excel/PowerPoint registered for this Windows user, Outlook "Add from file";
/// (B) company IT — the same add-in files plus a note for the Microsoft 365 admin center.
/// </summary>
static class OfficeAddin
{
    public const int Port = 47320;
    public static string Origin => $"https://localhost:{Port}";
    const string OfficeId = "6f1d2a7c-4b8e-4f5a-9c31-7a2e5d0b9e41";
    const string OutlookId = "8a3c9e12-5d4f-4b6a-a1e7-2f9c0d4b7e63";
    const string CertName = "Nova Enhance localhost";
    public static string Folder { get { var p = Path.Combine(Paths.Root, "Office Add-in"); Directory.CreateDirectory(p); return p; } }
    public static string OfficeManifest => Path.Combine(Folder, "NovaEnhance-Office.xml");
    public static string OutlookManifest => Path.Combine(Folder, "NovaEnhance-Outlook.xml");

    // ================================================================ certificate (made once for this PC)

    static X509Certificate2 FindCert()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindBySubjectName, CertName, false).OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30)).OrderByDescending(c => c.NotAfter).FirstOrDefault();
    }

    public static X509Certificate2 Cert()
    {
        var c = FindCert();
        if (c != null) return c;
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + CertName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); san.AddIpAddress(IPAddress.IPv6Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var tmp = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        var pfx = tmp.Export(X509ContentType.Pfx);
        var cert = new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);
        return cert;
    }

    public static bool Trusted()
    {
        try
        {
            var c = FindCert();
            if (c == null) return false;
            using var root = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            root.Open(OpenFlags.ReadOnly);
            return root.Certificates.Find(X509FindType.FindByThumbprint, c.Thumbprint, false).Count > 0;
        }
        catch { return false; }
    }

    /// <summary>Windows asks once: "Do you want to install this certificate?" → Yes. Only this PC trusts it; it only works for localhost.</summary>
    public static void Trust()
    {
        var c = Cert();
        using var root = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        root.Open(OpenFlags.ReadWrite);
        root.Add(new X509Certificate2(c.Export(X509ContentType.Cert)));
    }

    public static void Untrust()
    {
        var c = FindCert();
        if (c == null) return;
        using var root = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        root.Open(OpenFlags.ReadWrite);
        foreach (var x in root.Certificates.Find(X509FindType.FindByThumbprint, c.Thumbprint, false)) root.Remove(x);
    }

    // ================================================================ install

    const string DevKey = @"Software\Microsoft\Office\16.0\WEF\Developer";

    public static bool RegisteredForOffice()
    {
        try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DevKey); return k?.GetValue(OfficeId) != null; } catch { return false; }
    }

    public static string StatusShort() => (RegisteredForOffice() ? "installed for Word/Excel/PowerPoint" : "not installed") + (Server.Running ? " · panel running" : "");

    public static void WriteManifests()
    {
        File.WriteAllText(OfficeManifest, OfficeXml(), new UTF8Encoding(false));
        File.WriteAllText(OutlookManifest, OutlookXml(), new UTF8Encoding(false));
    }

    /// <summary>A · for me: trusted certificate, panel running, Word/Excel/PowerPoint registered for this Windows user (no admin).</summary>
    public static void InstallForMe()
    {
        Cert();
        if (!Trusted()) Trust();
        Server.Start();
        WriteManifests();
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(DevKey);
        k.SetValue(OfficeId, OfficeManifest);
        Store.SetBool("win_addin", true);
    }

    public static void Uninstall()
    {
        try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DevKey, true); k?.DeleteValue(OfficeId, false); } catch { }
        Store.SetBool("win_addin", false);
        try { Untrust(); } catch { }
    }

    /// <summary>B · company IT: the add-in files and a note for the Microsoft 365 admin center.</summary>
    public static string PrepareForIt()
    {
        WriteManifests();
        var dir = Path.Combine(Folder, "For IT");
        Directory.CreateDirectory(dir);
        File.Copy(OfficeManifest, Path.Combine(dir, Path.GetFileName(OfficeManifest)), true);
        File.Copy(OutlookManifest, Path.Combine(dir, Path.GetFileName(OutlookManifest)), true);
        File.WriteAllText(Path.Combine(dir, "Note for IT.txt"),
$@"Nova Enhance — Office add-in (Word, Excel, PowerPoint, Outlook)
{Program.Sign}

What it is
  A task-pane add-in that enhances, translates and summarises text. The panel is served by the Nova Enhance
  desktop app on each user's own PC at {Origin} (no external web server; AI keys stay in the desktop app).
  Permissions: Word/Excel/PowerPoint ReadWriteDocument · Outlook ReadWriteItem.

How to deploy (Microsoft 365 admin center)
  1. Settings → Integrated apps → Upload custom apps → ""Office Add-in"" → Upload manifest file.
  2. Upload NovaEnhance-Office.xml (Word, Excel, PowerPoint) and NovaEnhance-Outlook.xml (Outlook).
  3. Assign to the user(s) who have Nova Enhance installed, then Deploy.
  4. Each user opens Nova Enhance once → Settings → Office tab → ""Trust the panel on this PC"".

Uninstall: remove the two apps in Integrated apps.
");
        return dir;
    }

    static string Icons(string indent) =>
        $"{indent}<Icon>\n{indent}  <bt:Image size=\"16\" resid=\"Icon.16\"/>\n{indent}  <bt:Image size=\"32\" resid=\"Icon.32\"/>\n{indent}  <bt:Image size=\"80\" resid=\"Icon.80\"/>\n{indent}</Icon>";

    static string Resources(string tooltip) => $@"    <Resources>
      <bt:Images>
        <bt:Image id=""Icon.16"" DefaultValue=""{Origin}/icon-16.png""/>
        <bt:Image id=""Icon.32"" DefaultValue=""{Origin}/icon-32.png""/>
        <bt:Image id=""Icon.80"" DefaultValue=""{Origin}/icon-80.png""/>
      </bt:Images>
      <bt:Urls>
        <bt:Url id=""Commands.Url"" DefaultValue=""{Origin}/commands.html""/>
        <bt:Url id=""Taskpane.Url"" DefaultValue=""{Origin}/taskpane.html""/>
      </bt:Urls>
      <bt:ShortStrings>
        <bt:String id=""Group.Label"" DefaultValue=""Nova Enhance""/>
        <bt:String id=""Button.Label"" DefaultValue=""Nova Enhance""/>
      </bt:ShortStrings>
      <bt:LongStrings>
        <bt:String id=""Button.Tooltip"" DefaultValue=""{tooltip}""/>
      </bt:LongStrings>
    </Resources>";

    static string HostBlock(string type) => $@"      <Host xsi:type=""{type}"">
        <DesktopFormFactor>
          <FunctionFile resid=""Commands.Url""/>
          <ExtensionPoint xsi:type=""PrimaryCommandSurface"">
            <OfficeTab id=""TabHome"">
              <Group id=""Nova.Group"">
                <Label resid=""Group.Label""/>
{Icons("                ")}
                <Control xsi:type=""Button"" id=""Nova.Open"">
                  <Label resid=""Button.Label""/>
                  <Supertip>
                    <Title resid=""Button.Label""/>
                    <Description resid=""Button.Tooltip""/>
                  </Supertip>
{Icons("                  ")}
                  <Action xsi:type=""ShowTaskpane"">
                    <TaskpaneId>NovaPane</TaskpaneId>
                    <SourceLocation resid=""Taskpane.Url""/>
                  </Action>
                </Control>
              </Group>
            </OfficeTab>
          </ExtensionPoint>
        </DesktopFormFactor>
      </Host>";

    public static string OfficeXml() => $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<OfficeApp xmlns=""http://schemas.microsoft.com/office/appforoffice/1.1"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:bt=""http://schemas.microsoft.com/office/officeappbasictypes/1.0"" xmlns:ov=""http://schemas.microsoft.com/office/taskpaneappversionoverrides"" xsi:type=""TaskPaneApp"">
  <Id>{OfficeId}</Id>
  <Version>1.0.{Math.Max(0, Program.Build)}.0</Version>
  <ProviderName>A.HAMROUNI</ProviderName>
  <DefaultLocale>en-US</DefaultLocale>
  <DisplayName DefaultValue=""Nova Enhance""/>
  <Description DefaultValue=""Fix grammar, shorten, rewrite and translate the selected text with Nova Enhance.""/>
  <IconUrl DefaultValue=""{Origin}/icon-32.png""/>
  <HighResolutionIconUrl DefaultValue=""{Origin}/icon-80.png""/>
  <SupportUrl DefaultValue=""https://github.com/thescorpio007/nova-enhance""/>
  <AppDomains>
    <AppDomain>{Origin}</AppDomain>
  </AppDomains>
  <Hosts>
    <Host Name=""Document""/>
    <Host Name=""Workbook""/>
    <Host Name=""Presentation""/>
  </Hosts>
  <DefaultSettings>
    <SourceLocation DefaultValue=""{Origin}/taskpane.html""/>
  </DefaultSettings>
  <Permissions>ReadWriteDocument</Permissions>
  <VersionOverrides xmlns=""http://schemas.microsoft.com/office/taskpaneappversionoverrides"" xsi:type=""VersionOverridesV1_0"">
    <Hosts>
{HostBlock("Document")}
{HostBlock("Workbook")}
{HostBlock("Presentation")}
    </Hosts>
{Resources("Open the Nova Enhance panel: enhance, translate and keep the original of the selected text.")}
  </VersionOverrides>
</OfficeApp>
";

    static string MailSurface(string surface, string id) => $@"          <ExtensionPoint xsi:type=""{surface}"">
            <OfficeTab id=""TabDefault"">
              <Group id=""Nova.{id}.Group"">
                <Label resid=""Group.Label""/>
                <Control xsi:type=""Button"" id=""Nova.{id}.Open"">
                  <Label resid=""Button.Label""/>
                  <Supertip>
                    <Title resid=""Button.Label""/>
                    <Description resid=""Button.Tooltip""/>
                  </Supertip>
{Icons("                  ")}
                  <Action xsi:type=""ShowTaskpane"">
                    <SourceLocation resid=""Taskpane.Url""/>
                  </Action>
                </Control>
              </Group>
            </OfficeTab>
          </ExtensionPoint>";

    public static string OutlookXml() => $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<OfficeApp xmlns=""http://schemas.microsoft.com/office/appforoffice/1.1"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:bt=""http://schemas.microsoft.com/office/officeappbasictypes/1.0"" xmlns:mailappor=""http://schemas.microsoft.com/office/mailappversionoverrides/1.0"" xsi:type=""MailApp"">
  <Id>{OutlookId}</Id>
  <Version>1.0.{Math.Max(0, Program.Build)}.0</Version>
  <ProviderName>A.HAMROUNI</ProviderName>
  <DefaultLocale>en-US</DefaultLocale>
  <DisplayName DefaultValue=""Nova Enhance""/>
  <Description DefaultValue=""Summarise, draft replies, translate and enhance emails with Nova Enhance.""/>
  <IconUrl DefaultValue=""{Origin}/icon-64.png""/>
  <HighResolutionIconUrl DefaultValue=""{Origin}/icon-128.png""/>
  <SupportUrl DefaultValue=""https://github.com/thescorpio007/nova-enhance""/>
  <AppDomains>
    <AppDomain>{Origin}</AppDomain>
  </AppDomains>
  <Hosts>
    <Host Name=""Mailbox""/>
  </Hosts>
  <Requirements>
    <Sets>
      <Set Name=""Mailbox"" MinVersion=""1.1""/>
    </Sets>
  </Requirements>
  <FormSettings>
    <Form xsi:type=""ItemRead"">
      <DesktopSettings>
        <SourceLocation DefaultValue=""{Origin}/taskpane.html""/>
        <RequestedHeight>450</RequestedHeight>
      </DesktopSettings>
    </Form>
    <Form xsi:type=""ItemEdit"">
      <DesktopSettings>
        <SourceLocation DefaultValue=""{Origin}/taskpane.html""/>
      </DesktopSettings>
    </Form>
  </FormSettings>
  <Permissions>ReadWriteItem</Permissions>
  <Rule xsi:type=""RuleCollection"" Mode=""Or"">
    <Rule xsi:type=""ItemIs"" ItemType=""Message"" FormType=""Read""/>
    <Rule xsi:type=""ItemIs"" ItemType=""Message"" FormType=""Edit""/>
  </Rule>
  <DisableEntityHighlighting>false</DisableEntityHighlighting>
  <VersionOverrides xmlns=""http://schemas.microsoft.com/office/mailappversionoverrides"" xsi:type=""VersionOverridesV1_0"">
    <Requirements>
      <bt:Sets DefaultMinVersion=""1.3"">
        <bt:Set Name=""Mailbox""/>
      </bt:Sets>
    </Requirements>
    <Hosts>
      <Host xsi:type=""MailHost"">
        <DesktopFormFactor>
          <FunctionFile resid=""Commands.Url""/>
{MailSurface("MessageReadCommandSurface", "Read")}
{MailSurface("MessageComposeCommandSurface", "Compose")}
        </DesktopFormFactor>
      </Host>
    </Hosts>
{Resources("Open Nova Enhance: summarise, draft a reply, translate or enhance this email.")}
  </VersionOverrides>
</OfficeApp>
";
}

/// <summary>The little web server for the Office panel: https://localhost:47320, this PC only.</summary>
static class Server
{
    static TcpListener v4, v6;
    static X509Certificate2 cert;
    public static bool Running => v4 != null;
    public static string LastError = "";

    public static void Start(int port = OfficeAddin.Port)
    {
        if (v4 != null) return;
        try
        {
            cert = OfficeAddin.Cert();
            v4 = new TcpListener(IPAddress.Loopback, port); v4.Start(); _ = Accept(v4);
            try { v6 = new TcpListener(IPAddress.IPv6Loopback, port); v6.Start(); _ = Accept(v6); } catch { v6 = null; }
            LastError = "";
        }
        catch (Exception e) { LastError = e.Message; v4 = null; Log.Error("Office add-in · panel server", e); }
    }

    public static void Stop() { try { v4?.Stop(); v6?.Stop(); } catch { } v4 = v6 = null; }

    static async Task Accept(TcpListener l)
    {
        while (true)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(); } catch { return; }
            _ = Handle(c);
        }
    }

    static async Task Handle(TcpClient client)
    {
        using var _c = client;
        try
        {
            client.ReceiveTimeout = 30000;
            using var ssl = new SslStream(client.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(cert, false, System.Security.Authentication.SslProtocols.None, false);
            var (method, path, headers, body) = await Read(ssl);
            if (method == null) return;
            var (code, type, bytes) = await Route(method, path, headers, body);
            var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : "Error")}\r\n" +
                       $"Content-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(head));
            await ssl.WriteAsync(bytes);
            await ssl.FlushAsync();
        }
        catch { }
    }

    static async Task<(string, string, Dictionary<string, string>, string)> Read(Stream s)
    {
        var buf = new byte[65536];
        var ms = new MemoryStream();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await s.ReadAsync(buf);
            if (n <= 0) return (null, null, null, null);
            ms.Write(buf, 0, n);
            var all = ms.ToArray();
            for (int i = 3; i < all.Length; i++)
                if (all[i - 3] == 13 && all[i - 2] == 10 && all[i - 1] == 13 && all[i] == 10) { headerEnd = i + 1; break; }
            if (ms.Length > 1_000_000) return (null, null, null, null);
        }
        var data = ms.ToArray();
        var headText = Encoding.ASCII.GetString(data, 0, headerEnd);
        var lines = headText.Split("\r\n");
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1)) { var i = l.IndexOf(':'); if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim(); }
        int len = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var x) ? Math.Min(x, 20_000_000) : 0;
        var bodyBytes = new MemoryStream();
        bodyBytes.Write(data, headerEnd, data.Length - headerEnd);
        while (bodyBytes.Length < len)
        {
            int n = await s.ReadAsync(buf);
            if (n <= 0) break;
            bodyBytes.Write(buf, 0, n);
        }
        return (first[0], first.Length > 1 ? first[1] : "/", headers, Encoding.UTF8.GetString(bodyBytes.ToArray()));
    }

    static (int, string, byte[]) Json(JsonObject o, int code = 200) => (code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(o.ToJsonString()));

    static readonly Dictionary<string, string> Types = new()
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "application/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8", [".png"] = "image/png"
    };

    static byte[] Resource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Addin." + name, StringComparison.OrdinalIgnoreCase));
        if (res == null) return null;
        using var s = asm.GetManifestResourceStream(res);
        using var ms = new MemoryStream();
        s!.CopyTo(ms);
        return ms.ToArray();
    }

    static async Task<(int, string, byte[])> Route(string method, string path, Dictionary<string, string> h, string body)
    {
        var p = path.Split('?')[0];
        if (method == "GET" && !p.StartsWith("/api/"))
        {
            var name = p == "/" ? "taskpane.html" : p.TrimStart('/');
            if (name.Contains("..") || name.Contains('/')) return (404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
            var bytes = Resource(name);
            if (bytes == null) return (404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
            return (200, Types.TryGetValue(Path.GetExtension(name), out var t) ? t : "application/octet-stream", bytes);
        }
        // Only the Nova panel may call the API: a custom header (blocks other web pages) and our own origin.
        if (!h.TryGetValue("X-Nova", out var xn) || xn != "1") return Json(new JsonObject { ["error"] = "forbidden" }, 403);
        if (h.TryGetValue("Origin", out var origin) && !origin.Equals(OfficeAddin.Origin, StringComparison.OrdinalIgnoreCase) && origin != "null")
            return Json(new JsonObject { ["error"] = "forbidden" }, 403);
        JsonObject req;
        try { req = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)?.AsObject() ?? new JsonObject(); } catch { req = new JsonObject(); }
        try { return Json(await Api(p, req)); }
        catch (Exception e) { Log.Error("Office add-in · " + p, e); return Json(new JsonObject { ["error"] = e.Message }); }
    }

    static string S(JsonObject o, string k) => o[k]?.ToString() ?? "";

    static async Task<JsonObject> Api(string p, JsonObject r)
    {
        switch (p)
        {
            case "/api/ping":
                return new JsonObject { ["ok"] = true, ["build"] = Program.Build, ["ai"] = Ai.Active()?.Label ?? "", ["lang"] = Store.TrLang, ["langLabel"] = Modes.LangLabel(), ["sign"] = Program.Sign };
            case "/api/langs":
            {
                var a = new JsonArray();
                foreach (var (v, l) in Modes.MainLangs.Concat(Modes.OtherLangs)) a.Add(new JsonObject { ["v"] = v, ["l"] = l });
                return new JsonObject { ["langs"] = a, ["current"] = Store.TrLang };
            }
            case "/api/enhance":
            {
                var text = S(r, "text");
                if (text.Trim().Length == 0) return new JsonObject { ["error"] = "Select some text first" };
                var mode = S(r, "mode"); if (mode.Length == 0) mode = "grammar";
                if (S(r, "lang").Length > 0) Store.TrLang = S(r, "lang");
                var prov = Ai.Active() ?? throw new AiException("Add an AI key in Nova Enhance → Settings → AI & API keys");
                using var _ = Log.Doing($"Office panel · {Modes.Label(mode)} ({S(r, "host")})");
                var outText = await Ai.Enhance(prov, mode, text, CancellationToken.None);
                History.SaveOriginal(mode, text, outText);
                History.Add("enhance", prov.Id, Store.Model(prov.Id), mode, text, outText);
                return new JsonObject { ["result"] = outText, ["label"] = Modes.Label(mode), ["ai"] = prov.Label };
            }
            case "/api/originals":
            {
                var a = new JsonArray();
                foreach (var o in History.LastOriginals(10))
                    a.Add(new JsonObject { ["text"] = o.Text, ["mode"] = Modes.Label(o.Mode), ["when"] = DateTimeOffset.FromUnixTimeMilliseconds(o.Ts).LocalDateTime.ToString("dd MMM HH:mm") });
                return new JsonObject { ["originals"] = a };
            }
            case "/api/summarize":
            {
                using var _ = Log.Doing("Office panel · summarise email");
                var body = S(r, "text"); if (body.Length > 12000) body = body[..12000];
                var a = await Ai.Chat("Summarise this email for a busy professional in 3–6 short bullet points: what is asked, decisions, dates and deadlines, attachments mentioned. Reply in English unless the email is in Arabic or French." + Modes.ProfileNote(),
                    $"From: {S(r, "from")}\nSubject: {S(r, "subject")}\n\n{body}", true);
                History.Add("ask", Ai.Active()?.Id ?? "", "", "summary", S(r, "subject"), a);
                return new JsonObject { ["result"] = a };
            }
            case "/api/draft":
            {
                using var _ = Log.Doing("Office panel · draft reply");
                var body = S(r, "text"); if (body.Length > 8000) body = body[..8000];
                var sys = "You write email replies for the user. Write ONLY the reply body: no subject line, no notes. Use the language of the email. " +
                          "Clear, professional and concise, in the user's field terminology. Answer every question asked. If a fact is missing, put a short placeholder in [square brackets]. " +
                          $"End with a short closing{(Me.FirstName.Length > 0 ? " and the name " + Me.FirstName : "")}." + Modes.ProfileNote();
                var instr = S(r, "instruction");
                var a = await Ai.Chat(sys, $"Email from {S(r, "from")}:\nSubject: {S(r, "subject")}\n\n{body}" + (instr.Length > 0 ? "\n\nThe user's instruction for the reply: " + instr : ""), false);
                History.Add("ask", Ai.Active()?.Id ?? "", "", "draft", S(r, "subject"), a);
                return new JsonObject { ["result"] = a };
            }
            case "/api/open":
            {
                var what = S(r, "what");
                Program.UiContext?.Post(_ =>
                {
                    if (what == "ask") Screens.Ask();
                    else if (what == "folder") Screens.FolderSearch(null);
                    else Screens.Settings();
                }, null);
                return new JsonObject { ["ok"] = true };
            }
        }
        return new JsonObject { ["error"] = "unknown" };
    }
}
