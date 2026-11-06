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

    // ---------------------------------------------------------------- ✦ Nova ribbon tab

    static string Btn(string id, string label, string tip, string action, string ind = "                ") =>
        $"{ind}<Control xsi:type=\"Button\" id=\"{id}\">\n{ind}  <Label resid=\"{label}\"/>\n{ind}  <Supertip>\n{ind}    <Title resid=\"{label}\"/>\n{ind}    <Description resid=\"{tip}\"/>\n{ind}  </Supertip>\n{Icons(ind + "  ")}\n{ind}  {action}\n{ind}</Control>";

    static string Fn(string name) => $"<Action xsi:type=\"ExecuteFunction\"><FunctionName>{name}</FunctionName></Action>";
    static string Pane(string resid, bool taskpaneId) => $"<Action xsi:type=\"ShowTaskpane\">{(taskpaneId ? "<TaskpaneId>NovaPane</TaskpaneId>" : "")}<SourceLocation resid=\"{resid}\"/></Action>";

    static string TrMenu(string id, string ind = "                ") =>
        $"{ind}<Control xsi:type=\"Menu\" id=\"{id}\">\n{ind}  <Label resid=\"S.Tr\"/>\n{ind}  <Supertip>\n{ind}    <Title resid=\"S.Tr\"/>\n{ind}    <Description resid=\"L.Tr\"/>\n{ind}  </Supertip>\n{Icons(ind + "  ")}\n{ind}  <Items>\n" +
        string.Join("\n", new[] { ("En", "trEnglish"), ("Ar", "trArabic"), ("Fr", "trFrench"), ("De", "trGerman") }.Select(x =>
            $"{ind}    <Item id=\"{id}.{x.Item1}\">\n{ind}      <Label resid=\"S.Tr.{x.Item1}\"/>\n{ind}      <Supertip>\n{ind}        <Title resid=\"S.Tr.{x.Item1}\"/>\n{ind}        <Description resid=\"L.Tr\"/>\n{ind}      </Supertip>\n{Icons(ind + "      ")}\n{ind}      {Fn(x.Item2)}\n{ind}    </Item>")) +
        $"\n{ind}  </Items>\n{ind}</Control>";

    static string Group(string id, string label, bool icon, params string[] controls) =>
        $"              <Group id=\"{id}\">\n                <Label resid=\"{label}\"/>\n" + (icon ? Icons("                ") + "\n" : "") + string.Join("\n", controls) + "\n              </Group>";

    static string WriteControls(string p, bool taskpaneId) => string.Join("\n",
        Btn(p + ".Panel", "S.Panel", "L.Panel", Pane("Taskpane.Url", taskpaneId)),
        Btn(p + ".Grammar", "S.Grammar", "L.Grammar", Fn("fixGrammar")),
        Btn(p + ".Pro", "S.Pro", "L.Pro", Fn("professional")),
        Btn(p + ".Short", "S.Short", "L.Short", Fn("shorten")),
        TrMenu(p + ".Tr"),
        Btn(p + ".Back", "S.Back", "L.Back", Fn("putBack")));

    static string WorkControls(string p, bool mail) => string.Join("\n", (mail
        ? new[] { Btn(p + ".Ask", "S.Ask", "L.Ask", Fn("openAsk")), Btn(p + ".Brief", "S.Brief", "L.Brief", Fn("openBrief")), Btn(p + ".Meet", "S.Meet", "L.Meet", Fn("openMeetings")) }
        : Array.Empty<string>()).Concat(new[] { Btn(p + ".Hub", "S.Hub", "L.Hub", Fn("openHub")), Btn(p + ".Folder", "S.Folder", "L.Folder", Fn("openFolder")) }));

    static string NovaTab(string p, bool mail, string first) => $@"            <CustomTab id=""{p}.Tab"">
{first}
{Group(p + ".Work", "G.Work", !mail, WorkControls(p + ".W", mail))}
{Group(p + ".Nova", "G.Nova", !mail, Btn(p + ".Set", "S.Set", "L.Set", Fn("openSettings")))}
              <Label resid=""Tab.Label""/>
            </CustomTab>";

    static string TabStrings(bool mail) => $@"
        <bt:String id=""Tab.Label"" DefaultValue=""✦ Nova""/>
        <bt:String id=""G.Write"" DefaultValue=""Write""/>
        <bt:String id=""G.Mail"" DefaultValue=""This email""/>
        <bt:String id=""G.Work"" DefaultValue=""My work""/>
        <bt:String id=""G.Nova"" DefaultValue=""Nova""/>
        <bt:String id=""S.Panel"" DefaultValue=""✦ Panel""/>
        <bt:String id=""S.Grammar"" DefaultValue=""Fix grammar""/>
        <bt:String id=""S.Pro"" DefaultValue=""Professional""/>
        <bt:String id=""S.Short"" DefaultValue=""Shorten""/>
        <bt:String id=""S.Tr"" DefaultValue=""Translate""/>
        <bt:String id=""S.Tr.En"" DefaultValue=""English""/>
        <bt:String id=""S.Tr.Ar"" DefaultValue=""العربية Arabic""/>
        <bt:String id=""S.Tr.Fr"" DefaultValue=""Français""/>
        <bt:String id=""S.Tr.De"" DefaultValue=""Deutsch""/>
        <bt:String id=""S.Back"" DefaultValue=""Put back original""/>
        <bt:String id=""S.Sum"" DefaultValue=""✦ Summary""/>
        <bt:String id=""S.Draft"" DefaultValue=""✦ Draft reply""/>
        <bt:String id=""S.Ask"" DefaultValue=""Ask Nova""/>
        <bt:String id=""S.Brief"" DefaultValue=""Morning brief""/>
        <bt:String id=""S.Meet"" DefaultValue=""Today's meetings""/>
        <bt:String id=""S.Hub"" DefaultValue=""AI window""/>
        <bt:String id=""S.Folder"" DefaultValue=""Search a folder""/>
        <bt:String id=""S.Set"" DefaultValue=""Settings""/>";

    static string TabLongStrings() => @"
        <bt:String id=""L.Panel"" DefaultValue=""Open the Nova panel: all modes, translation, originals.""/>
        <bt:String id=""L.Grammar"" DefaultValue=""Fix the grammar of the selected text, in place. The original is kept.""/>
        <bt:String id=""L.Pro"" DefaultValue=""Rewrite the selected text in a professional tone, in place. The original is kept.""/>
        <bt:String id=""L.Short"" DefaultValue=""Make the selected text shorter, in place. The original is kept.""/>
        <bt:String id=""L.Tr"" DefaultValue=""Translate the selected text, in place. The original is kept.""/>
        <bt:String id=""L.Back"" DefaultValue=""Select the changed text, then put the original back.""/>
        <bt:String id=""L.Sum"" DefaultValue=""Summarise this email (follows your privacy setting in Nova).""/>
        <bt:String id=""L.Draft"" DefaultValue=""Write a reply to this email for you to check and send.""/>
        <bt:String id=""L.Ask"" DefaultValue=""Open Ask Nova on your desktop: find any email, what is waiting for your reply…""/>
        <bt:String id=""L.Brief"" DefaultValue=""Open the morning brief: waiting emails, follow-ups, meetings.""/>
        <bt:String id=""L.Meet"" DefaultValue=""Today's and tomorrow's meetings with what is still open for each.""/>
        <bt:String id=""L.Hub"" DefaultValue=""Open Nova Chat and your AI websites in one window.""/>
        <bt:String id=""L.Folder"" DefaultValue=""Search inside the files of a folder on your PC or Google Drive.""/>
        <bt:String id=""L.Set"" DefaultValue=""Open Nova Enhance settings.""/>";

    static string Resources(string tooltip) => $@"    <Resources>
      <bt:Images>
        <bt:Image id=""Icon.16"" DefaultValue=""{Origin}/icon-16.png""/>
        <bt:Image id=""Icon.32"" DefaultValue=""{Origin}/icon-32.png""/>
        <bt:Image id=""Icon.80"" DefaultValue=""{Origin}/icon-80.png""/>
      </bt:Images>
      <bt:Urls>
        <bt:Url id=""Commands.Url"" DefaultValue=""{Origin}/commands.html""/>
        <bt:Url id=""Taskpane.Url"" DefaultValue=""{Origin}/taskpane.html""/>
        <bt:Url id=""Taskpane.Sum"" DefaultValue=""{Origin}/taskpane.html?do=summary""/>
        <bt:Url id=""Taskpane.Draft"" DefaultValue=""{Origin}/taskpane.html?do=draft""/>
      </bt:Urls>
      <bt:ShortStrings>
        <bt:String id=""Group.Label"" DefaultValue=""Nova Enhance""/>
        <bt:String id=""Button.Label"" DefaultValue=""Nova Enhance""/>{TabStrings(true)}
      </bt:ShortStrings>
      <bt:LongStrings>
        <bt:String id=""Button.Tooltip"" DefaultValue=""{tooltip}""/>{TabLongStrings()}
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
{NovaTab("NovaT", false, Group("NovaT.Write", "G.Write", true, WriteControls("NovaT.X", true)))}
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
{NovaTab("NovaM" + id, true, id == "Read"
    ? Group("NovaM" + id + ".Mail", "G.Mail", false, string.Join("\n",
        Btn("NovaM" + id + ".M.Panel", "S.Panel", "L.Panel", Pane("Taskpane.Url", false)),
        Btn("NovaM" + id + ".M.Sum", "S.Sum", "L.Sum", Pane("Taskpane.Sum", false)),
        Btn("NovaM" + id + ".M.Draft", "S.Draft", "L.Draft", Pane("Taskpane.Draft", false))))
    : Group("NovaM" + id + ".Write", "G.Write", false, WriteControls("NovaM" + id + ".X", false)))}
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
    static TcpListener v4, v6, web;
    static X509Certificate2 cert;
    public static bool Running => v4 != null;
    public static bool BrowserRunning => web != null;
    public static string LastError = "";

    /// <summary>Office panel (HTTPS, port 47320) when the Office add-in is on; browser add-on (HTTP on 127.0.0.1:47321, with its own key) when that is on.</summary>
    public static void Start(int port = OfficeAddin.Port)
    {
        if (v4 == null)
            try
            {
                cert = OfficeAddin.Cert();
                v4 = new TcpListener(IPAddress.Loopback, port); v4.Start(); _ = Accept(v4, true);
                try { v6 = new TcpListener(IPAddress.IPv6Loopback, port); v6.Start(); _ = Accept(v6, true); } catch { v6 = null; }
                LastError = "";
            }
            catch (Exception e) { LastError = e.Message; v4 = null; Log.Error("Office add-in · panel server", e); }
        StartBrowser();
    }

    public static void StartBrowser()
    {
        if (web != null || !BrowserAddon.On) return;
        try { web = new TcpListener(IPAddress.Loopback, BrowserAddon.Port); web.Start(); _ = Accept(web, false); }
        catch (Exception e) { web = null; LastError = e.Message; Log.Error("Browser add-on · server", e); }
    }

    public static void Stop() { try { v4?.Stop(); v6?.Stop(); web?.Stop(); } catch { } v4 = v6 = web = null; }

    static async Task Accept(TcpListener l, bool tls)
    {
        while (true)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(); } catch { return; }
            _ = Handle(c, tls);
        }
    }

    static async Task Handle(TcpClient client, bool tls)
    {
        using var _c = client;
        try
        {
            client.ReceiveTimeout = 30000;
            Stream stream = client.GetStream();
            SslStream ssl = null;
            if (tls)
            {
                ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsServerAsync(cert, false, System.Security.Authentication.SslProtocols.None, false);
                stream = ssl;
            }
            using var _s = ssl;
            var (method, path, headers, body) = await Read(stream);
            if (method == null) return;
            var (code, type, bytes) = tls ? await Route(method, path, headers, body) : await RouteBrowser(method, path, headers, body);
            var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : "Error")}\r\n" +
                       $"Content-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
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

    /// <summary>The browser add-on: only extension pages that carry this PC's key (written into the add-on by Nova).</summary>
    static async Task<(int, string, byte[])> RouteBrowser(string method, string path, Dictionary<string, string> h, string body)
    {
        var p = path.Split('?')[0];
        if (method == "GET" && p == "/firefox/updates.json") return (200, "application/json", Encoding.UTF8.GetBytes(BrowserAddon.UpdatesJson()));
        if (method == "GET" && p == "/firefox/nova-enhance.xpi" && File.Exists(BrowserAddon.Xpi)) return (200, "application/x-xpinstall", File.ReadAllBytes(BrowserAddon.Xpi));
        if (!p.StartsWith("/api/") || !h.TryGetValue("X-Nova-Key", out var key) || key != BrowserAddon.Key) return Json(new JsonObject { ["error"] = "forbidden" }, 403);
        if (h.TryGetValue("Origin", out var origin) && origin != "null" &&
            !origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) && !origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase) &&
            !origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase))
            return Json(new JsonObject { ["error"] = "forbidden" }, 403);
        JsonObject req;
        try { req = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)?.AsObject() ?? new JsonObject(); } catch { req = new JsonObject(); }
        try
        {
            var r = await Api(p, req);
            if (p == "/api/ping") r["addon"] = BrowserAddon.Version;
            return Json(r);
        }
        catch (Exception e) { Log.Error("Browser add-on · " + p, e); return Json(new JsonObject { ["error"] = e.Message }); }
    }

    static string S(JsonObject o, string k) => o[k]?.ToString() ?? "";

    static async Task<JsonObject> Api(string p, JsonObject r)
    {
        switch (p)
        {
            case "/api/ping":
                return new JsonObject { ["ok"] = true, ["build"] = Program.Build, ["ai"] = Ai.Active()?.Label ?? "", ["lang"] = Store.TrLang, ["langLabel"] = Modes.LangLabel(), ["sign"] = Program.Sign, ["cellReview"] = Store.Bool("win_xl_review", true) };
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
            case "/api/cells":
            {
                // Excel: a batch of cells (the panel sends up to ~25 at a time and shows the progress).
                var mode = S(r, "mode"); if (mode.Length == 0) mode = "grammar";
                if (S(r, "lang").Length > 0) Store.TrLang = S(r, "lang");
                var prov = Ai.Active() ?? throw new AiException("Add an AI key in Nova Enhance → Settings → AI & API keys");
                var list = new List<(string, string)>();
                foreach (var it in r["items"] as JsonArray ?? new JsonArray())
                    if (it is JsonObject jo && S(jo, "id").Length > 0 && S(jo, "text").Trim().Length > 0) list.Add((S(jo, "id"), S(jo, "text")));
                if (list.Count == 0) return new JsonObject { ["items"] = new JsonArray() };
                using var _ = Log.Doing($"Office panel · Excel cells · {Modes.Label(mode)} ({list.Count})");
                var got = await Ai.EnhanceCells(prov, mode, list, CancellationToken.None);
                var outArr = new JsonArray();
                foreach (var (id, text) in list)
                    outArr.Add(new JsonObject { ["id"] = id, ["result"] = got.TryGetValue(id, out var t) ? t : null });
                History.Add("enhance", prov.Id, Store.Model(prov.Id), mode, $"Excel · {list.Count} cells", $"{got.Count} answered");
                return new JsonObject { ["items"] = outArr, ["label"] = Modes.Label(mode), ["ai"] = prov.Label };
            }
            case "/api/cellsoriginal":
            {
                // Keeps the cells' text before the change in Documents\Nova Enhance\Originals (the panel also keeps it for ↩ Put back original).
                var mode = S(r, "mode");
                var sb = new StringBuilder();
                var after = new StringBuilder();
                foreach (var it in r["items"] as JsonArray ?? new JsonArray())
                    if (it is JsonObject jo) { sb.Append(S(jo, "addr")).Append(": ").AppendLine(S(jo, "before")); after.Append(S(jo, "addr")).Append(": ").AppendLine(S(jo, "after")); }
                if (sb.Length > 0) History.SaveOriginal(mode, sb.ToString().TrimEnd(), after.ToString().TrimEnd());
                return new JsonObject { ["ok"] = true };
            }
            case "/api/page":
            {
                // Browser add-on: summarise the page, or answer a question from it (the page text goes to the AI only when the user clicks).
                var text = S(r, "text"); if (text.Length > 40000) text = text[..40000];
                if (text.Trim().Length < 20) return new JsonObject { ["error"] = "This page has no readable text" };
                var prov = Ai.Active() ?? throw new AiException("Add an AI key in Nova Enhance → Settings → AI & API keys");
                bool detailed = r["detailed"]?.GetValue<bool>() == true;
                using var _ = Log.Doing("Browser add-on · " + (S(r, "mode") == "ask" ? "ask about page" : "summarise page"));
                string sys, user;
                if (S(r, "mode") == "ask")
                {
                    sys = "You answer the user's question using ONLY the web page text given. Reply in the language of the question. " +
                          (detailed ? "Give a detailed answer with every relevant requirement, value and condition. " : "Answer in 2 to 5 short lines, starting with the direct answer in bold-free plain text. ") +
                          "Give exact values (dimensions, ratings, clause and table numbers). After each fact put the clause, table or heading it comes from in square brackets, e.g. [D.4.2] or [Table D.6]. " +
                          "If the page does not answer it, say so and suggest what to look for. Never invent facts." + Modes.ProfileNote();
                    user = $"Page: {S(r, "title")}\n{S(r, "url")}\n\nPage text:\n{text}\n\nQuestion: {S(r, "question")}";
                }
                else
                {
                    sys = "Summarise this web page for a busy professional. " + (detailed ? "Give a structured, detailed summary with the key requirements, numbers and conclusions. " : "Use 4 to 8 short bullet points: what it is, the key points, numbers, dates and anything to act on. ") +
                          "Reply in English unless the page is in Arabic or French. Put clause/section numbers in square brackets where useful." + Modes.ProfileNote();
                    user = $"Page: {S(r, "title")}\n{S(r, "url")}\n\n{text}";
                }
                var a = await Ai.Chat(sys, user, !detailed, false, prov, CancellationToken.None);
                History.Add("ask", prov.Id, Store.Model(prov.Id), "page-" + S(r, "mode"), S(r, "title") + " · " + S(r, "question"), a);
                return new JsonObject { ["result"] = a, ["ai"] = prov.Label };
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
                    else if (what == "brief") Screens.Brief();
                    else if (what == "hub") Screens.Hub(null);
                    else Screens.Settings();
                }, null);
                return new JsonObject { ["ok"] = true };
            }
            case "/api/notify":
                Program.Balloon(S(r, "title").Length > 0 ? S(r, "title") : "Nova Enhance", S(r, "text"));
                return new JsonObject { ["ok"] = true };
        }
        return new JsonObject { ["error"] = "unknown" };
    }
}
