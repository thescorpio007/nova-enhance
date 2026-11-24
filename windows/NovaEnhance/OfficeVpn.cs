using System.Text;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NovaEnhance;

/// <summary>
/// 🌐 Office VPN companion: a second tray icon (🟢 connected · 🟡 connecting · 🔴 not connected), one click to connect with your
/// account, and a watchdog that reconnects by itself after a drop, sleep or a Wi-Fi change. Nova drives FortiClient — with
/// Fortinet's FortiClientTools command line when it is on the PC (silent, fastest), otherwise your free FortiClient window
/// (it opens it and presses Connect, as you would). Your password stays in FortiClient; for the command line it is kept encrypted
/// on this PC only (never in a backup, never to the phone or the AI). Designed by A.HAMROUNI
/// </summary>
static class OfficeVpn
{
    public enum State { Off, Down, Connecting, Up, UpNoServer }

    public static bool On { get => Store.Bool("win_vpn_on", false); set { Store.SetBool("win_vpn_on", value); wake.Set(); } }
    public static bool Auto { get => Store.Bool("win_vpn_auto", true); set => Store.SetBool("win_vpn_auto", value); }
    /// <summary>You want it up (Connect pressed): the watchdog keeps it up until you press Disconnect.</summary>
    public static bool Want { get => Store.Bool("win_vpn_want", false); set { Store.SetBool("win_vpn_want", value); wake.Set(); } }
    public static string Profile { get => Store.Str("win_vpn_profile", "1-MZ-ahmed"); set => Store.Set("win_vpn_profile", value); }
    public static string Host { get => Store.Str("win_vpn_host", "mza.fortiddns.com:24443"); set => Store.Set("win_vpn_host", value); }
    public static string User { get => Store.Str("win_vpn_user", Environment.UserName); set => Store.Set("win_vpn_user", value); }
    /// <summary>An office server that answers only through the VPN (the projects server).</summary>
    public static string Probe { get => Store.Str("win_vpn_probe", "192.168.1.12"); set => Store.Set("win_vpn_probe", value); }
    /// <summary>The FortiClient connection, as in its “Edit VPN Connection” page.</summary>
    public static string Gateway { get => Split(Host).H; set => Host = Join(value, Port); }
    public static int Port { get => Split(Host).P; set => Host = Join(Gateway, value); }
    static (string H, int P) Split(string hp)
    {
        hp = (hp ?? "").Trim().Replace("https://", "").TrimEnd('/');
        var i = hp.LastIndexOf(':');
        return i > 0 && int.TryParse(hp[(i + 1)..], out var p) ? (hp[..i], p) : (hp, 443);
    }
    static string Join(string h, int p) => (h ?? "").Trim() + (p > 0 && p != 443 ? ":" + p : "");
    /// <summary>“Save login” (FortiClient keeps the password) or “Prompt on login” (Nova types it from its encrypted copy).</summary>
    public static bool SaveLogin { get => Store.Bool("win_vpn_savelogin", true); set => Store.SetBool("win_vpn_savelogin", value); }
    public static string Cert { get => Store.Str("win_vpn_cert", ""); set => Store.Set("win_vpn_cert", value); }
    public static bool Sso { get => Store.Bool("win_vpn_sso", false); set => Store.SetBool("win_vpn_sso", value); }
    public static bool DualStack { get => Store.Bool("win_vpn_dual", false); set => Store.SetBool("win_vpn_dual", value); }
    public static string ToolPath { get => Store.Str("win_vpn_tool", ""); set => Store.Set("win_vpn_tool", value); }
    public static string Password { get => Store.Secret("win_vpn_pw"); set => Store.SetSecret("win_vpn_pw", value ?? ""); }

    public static volatile State Now = State.Off;
    public static DateTime Since = DateTime.Now;
    public static volatile string Line = "🌐 Office VPN is off (Settings ▸ 🌐 Office VPN)";
    public static volatile string Way = "";
    public static event Action Changed;

    static readonly AutoResetEvent wake = new(false);
    static Thread thread;
    static DateTime lastTry = DateTime.MinValue, droppedAt = DateTime.MinValue;
    static int tries;

    // ------------------------------------------------------------------ the two ways to drive FortiClient

    static readonly string[] ToolGuesses =
    {
        @"C:\Program Files\Fortinet\FortiClient\FortiSSLVPNclient.exe",
        @"C:\Program Files (x86)\Fortinet\FortiClient\FortiSSLVPNclient.exe",
        @"C:\Program Files\Fortinet\FortiClientTools\SSLVPNcmdline\x64\FortiSSLVPNclient.exe",
        @"C:\FortiClientTools\SSLVPNcmdline\x64\FortiSSLVPNclient.exe",
    };

    /// <summary>Fortinet's FortiSSLVPNclient.exe (FortiClientTools) — set in Settings or found in the usual places.</summary>
    public static string Tool()
    {
        if (ToolPath.Length > 0 && File.Exists(ToolPath)) return ToolPath;
        return ToolGuesses.FirstOrDefault(File.Exists);
    }

    static readonly string[] GuiGuesses = { @"C:\Program Files\Fortinet\FortiClient\FortiClient.exe", @"C:\Program Files (x86)\Fortinet\FortiClient\FortiClient.exe" };
    public static string Gui() => GuiGuesses.FirstOrDefault(File.Exists);

    // ------------------------------------------------------------------ status

    static bool AdapterUp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up &&
                (n.Description.Contains("Fortinet", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("Fortinet", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("FortiSSL", StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

    /// <summary>The office server answers (file sharing port), within 1.5 s.</summary>
    public static bool ServerOk()
    {
        var (host, port) = ProbeTarget();
        if (host.Length == 0) return true;
        return Tcp(host, port, 1500);
    }

    /// <summary>The office server to check: “192.168.1.12” (file sharing, port 445) or “host:port”.</summary>
    public static (string Host, int Port) ProbeTarget()
    {
        var p = Probe.Trim();
        var i = p.LastIndexOf(':');
        return i > 0 && int.TryParse(p[(i + 1)..], out var n) ? (p[..i], n) : (p, 445);
    }

    static bool Tcp(string host, int port, int ms)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync(host, port);
            return t.Wait(ms) && c.Connected;
        }
        catch { return false; }
    }

    static void Set(State s, string line)
    {
        var was = Now;
        Line = line;
        if (s == was) return;
        Now = s; Since = DateTime.Now;
        try { Changed?.Invoke(); } catch { }
        if (s is State.Up && was is State.Connecting or State.Down && Want)
        {
            var took = droppedAt > DateTime.MinValue ? $" in {(int)(DateTime.Now - droppedAt).TotalSeconds} s" : "";
            if (droppedAt > DateTime.MinValue) Program.Balloon("🟢 Office VPN", "Reconnected" + took + " — your office folders are back.");
            droppedAt = DateTime.MinValue; tries = 0;
            // the office folders are reachable again: Nova refreshes their lists and the ⚡ offline copies
            try { Projects.Indexer.Refresh(); ProjectSources.Offline.Wake(); } catch { }
        }
        if (s is State.Down && was is State.Up or State.UpNoServer && Want)
        {
            droppedAt = DateTime.Now;
            Program.Balloon("🔴 Office VPN dropped", Auto ? "Nova is reconnecting…" : "Click the 🌐 icon to connect again.");
        }
    }

    // ------------------------------------------------------------------ connect / disconnect

    public static void Connect()
    {
        Want = true;
        lastTry = DateTime.MinValue;
        wake.Set();
    }

    public static void Disconnect()
    {
        Want = false;
        Task.Run(() =>
        {
            try
            {
                if (Tool() is { } t) Run(t, "disconnect", 20000);
                else GuiButton("Disconnect");
            }
            catch (Exception e) { Log.Warn("office vpn", "disconnect: " + e.Message); }
        });
    }

    static string Q(string s) => "\"" + (s ?? "").Replace("\"", "") + "\"";

    static int Run(string exe, string args, int ms)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(exe) });
        if (p == null) return -1;
        if (!p.WaitForExit(ms)) return 0;   // the client stays running while connected
        return p.ExitCode;
    }

    static void TryConnect()
    {
        lastTry = DateTime.Now; tries++;
        Set(State.Connecting, $"🟡 Connecting to {Host} ({Profile})…" + (tries > 1 ? $" try {tries}" : ""));
        try
        {
            if (Tool() is { } t)
            {
                Way = "FortiClientTools (command line)";
                var pw = Password;
                if (pw.Length == 0) { Set(State.Down, "🔴 Not connected — add your VPN password in Settings ▸ 🌐 Office VPN (kept encrypted on this PC)"); Want = false; return; }
                // -i ignore the certificate warning · -m minimized · -q no message boxes
                var args = $"connect -s {Q("Nova " + Profile)} -h {Q(Host)} -u {Q(User + ":" + pw)}" + (Cert.Length > 0 && File.Exists(Cert) ? $" -c {Q(Cert)}" : "") + " -i -m -q";
                _ = Task.Run(() => { try { Run(t, args, 90000); } catch (Exception e) { Log.Warn("office vpn", "connect: " + e.Message); } });
            }
            else if (Gui() is { } g)
            {
                Way = "your FortiClient window";
                // started again even when it runs in the tray: FortiClient then shows its window (a hidden window has no buttons)
                var running = Process.GetProcessesByName("FortiClient").Any();
                Process.Start(new ProcessStartInfo(g) { UseShellExecute = true });
                Thread.Sleep(running ? 2500 : 6000);
                var r = GuiConnect();
                if (r.Length > 0) { Log.Warn("office vpn", r); Set(State.Down, "🔴 " + r); }
            }
            else { Set(State.Down, "🔴 FortiClient is not installed on this PC"); Want = false; return; }
        }
        catch (Exception e) { Log.Warn("office vpn", "connect: " + e.Message); }
    }

    static System.Windows.Automation.AutomationElement FortiWindow(int seconds)
    {
        var root = System.Windows.Automation.AutomationElement.RootElement;
        var cond = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Window);
        for (int i = 0; i < seconds; i++)
        {
            foreach (System.Windows.Automation.AutomationElement w in root.FindAll(System.Windows.Automation.TreeScope.Children, cond))
            {
                try { if ((w.Current.Name ?? "").Contains("FortiClient", StringComparison.OrdinalIgnoreCase)) return w; } catch { }
            }
            Thread.Sleep(1000);
        }
        return null;
    }

    static List<System.Windows.Automation.AutomationElement> All(System.Windows.Automation.AutomationElement w, System.Windows.Automation.ControlType t) =>
        w.FindAll(System.Windows.Automation.TreeScope.Descendants, new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, t))
            .Cast<System.Windows.Automation.AutomationElement>().ToList();

    static string NameOf(System.Windows.Automation.AutomationElement e) { try { return e.Current.Name ?? ""; } catch { return ""; } }

    /// <summary>Your free FortiClient: the window is found; the username / password are typed when it asks (“Prompt on login”); Connect is pressed.
    /// Returns "" when Connect was pressed (or it is already connected), otherwise what went wrong.</summary>
    static string GuiConnect()
    {
        try
        {
            var w = FortiWindow(15);
            if (w == null) return "FortiClient's window did not open — open FortiClient once and leave it on the Remote Access page";
            for (int i = 0; i < 10; i++)
            {
                var buttons = All(w, System.Windows.Automation.ControlType.Button);
                if (buttons.Any(b => NameOf(b).Equals("Disconnect", StringComparison.OrdinalIgnoreCase))) return "";   // already connected
                var connect = buttons.FirstOrDefault(b => NameOf(b).Equals("Connect", StringComparison.OrdinalIgnoreCase));
                if (connect != null)
                {
                    foreach (var e in All(w, System.Windows.Automation.ControlType.Edit))
                    {
                        bool pw; try { pw = e.Current.IsPassword; } catch { continue; }
                        if (!e.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var vp)) continue;
                        var v = (System.Windows.Automation.ValuePattern)vp;
                        string cur; try { cur = v.Current.Value ?? ""; } catch { cur = ""; }
                        if (pw && cur.Length == 0) { if (Password.Length == 0) return "FortiClient asks for the password — type it in Settings ▸ 🌐 Office VPN (kept encrypted on this PC) or tick “Save login” in FortiClient"; v.SetValue(Password); }
                        else if (!pw && cur.Length == 0 && User.Length > 0 && NameOf(e).Contains("user", StringComparison.OrdinalIgnoreCase)) v.SetValue(User);
                    }
                    if (connect.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var ip)) { ((System.Windows.Automation.InvokePattern)ip).Invoke(); return ""; }
                    return "FortiClient's Connect button could not be pressed";
                }
                Thread.Sleep(1000);
            }
            return "FortiClient's Connect button was not found — open FortiClient on the Remote Access page with “" + Profile + "” selected";
        }
        catch (Exception e) { return "FortiClient window: " + e.Message; }
    }

    /// <summary>Your free FortiClient: its window is found and the button pressed (Windows UI Automation — as a screen reader would).</summary>
    static bool GuiButton(string name)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.RootElement;
            var cond = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Window);
            for (int i = 0; i < 20; i++)
            {
                foreach (System.Windows.Automation.AutomationElement w in root.FindAll(System.Windows.Automation.TreeScope.Children, cond))
                {
                    string title;
                    try { title = w.Current.Name ?? ""; } catch { continue; }
                    if (!title.Contains("FortiClient", StringComparison.OrdinalIgnoreCase)) continue;
                    var btn = w.FindFirst(System.Windows.Automation.TreeScope.Descendants, new System.Windows.Automation.AndCondition(
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Button),
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, name, System.Windows.Automation.PropertyConditionFlags.IgnoreCase)));
                    if (btn != null && btn.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var pat)) { ((System.Windows.Automation.InvokePattern)pat).Invoke(); return true; }
                }
                Thread.Sleep(1000);
            }
        }
        catch (Exception e) { Log.Warn("office vpn", "FortiClient window: " + e.Message); }
        return false;
    }

    // ------------------------------------------------------------------ 🧪 the test (Settings and Claude's nova.vpn) — read only, it never connects

    public static string Diagnose()
    {
        var sb = new StringBuilder($"🧪 Office VPN test · {DateTime.Now:dd MMM HH:mm:ss}\n");
        void L(bool? ok, string t) => sb.Append(ok == true ? "✅ " : ok == false ? "❌ " : "• ").Append(t).Append('\n');
        L(On, "Companion " + (On ? "on" : "off — tick “Use the Office VPN companion”") + $" · reconnect automatically {(Auto ? "on" : "off")} · you want it up: {(Want ? "yes" : "no — press Connect")}");
        L(null, $"Connection “{Profile}” · SSL-VPN · gateway {Gateway} · port {Port} · user {User} · {(SaveLogin ? "Save login" : "Prompt on login")} · certificate {(Cert.Length > 0 ? Path.GetFileName(Cert) : "None")}{(Sso ? " · SSO" : "")}{(DualStack ? " · dual-stack" : "")} · password in Nova: {(Password.Length > 0 ? "yes (encrypted)" : "no")}");
        var g = Gui(); var t = Tool();
        string Ver(string f) { try { return FileVersionInfo.GetVersionInfo(f).ProductVersion ?? ""; } catch { return ""; } }
        L(g != null, g != null ? $"FortiClient {Ver(g)} installed · {(Process.GetProcessesByName("FortiClient").Any() ? "running" : "not running")}" : "FortiClient not found in Program Files");
        L(t != null ? true : null, t != null ? "FortiClientTools command line: " + t : "FortiClientTools not on this PC — Nova uses the FortiClient window");
        try
        {
            var ips = System.Net.Dns.GetHostAddresses(Gateway);
            L(ips.Length > 0, $"Gateway {Gateway} → {string.Join(", ", ips.Take(3).Select(x => x.ToString()))}");
            var open = Tcp(Gateway, Port, 4000);
            L(open, open ? $"Gateway answers on port {Port} (internet path to the office firewall is fine)" : $"Gateway does NOT answer on port {Port} — internet down, wrong port, or the office firewall is off");
        }
        catch (Exception e) { L(false, $"Gateway {Gateway} cannot be found (DNS): {e.Message}"); }
        try
        {
            var ad = NetworkInterface.GetAllNetworkInterfaces().Where(n => (n.Description + n.Name).Contains("Forti", StringComparison.OrdinalIgnoreCase)).ToList();
            if (ad.Count == 0) L(false, "No Fortinet network adapter — reinstall FortiClient VPN (its virtual adapter is missing)");
            foreach (var n in ad) L(n.OperationalStatus == OperationalStatus.Up ? true : null, $"Adapter “{n.Description}”: {n.OperationalStatus}" + (n.OperationalStatus == OperationalStatus.Up ? " · " + string.Join(", ", n.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => u.Address)) : ""));
        }
        catch (Exception e) { L(false, "Adapters: " + e.Message); }
        var (ph, pp) = ProbeTarget();
        if (ph.Equals(Gateway, StringComparison.OrdinalIgnoreCase)) L(false, $"“Office server to check” is the gateway itself — use an office server that answers only through the VPN (e.g. 192.168.1.12, the O: N: S: server)");
        else if (ph.Length > 0) { var ok = Tcp(ph, pp, 2000); L(ok, $"Office server {ph}:{pp} " + (ok ? "answers (the VPN works)" : "does not answer (normal while the VPN is down)")); }
        if (g != null && t == null)
        {
            try
            {
                var w = FortiWindow(2);
                if (w == null) L(null, "FortiClient window not open (Nova opens it when connecting)");
                else
                {
                    var names = All(w, System.Windows.Automation.ControlType.Button).Select(NameOf).Where(n => n.Length > 0).Distinct().Take(12).ToList();
                    var pwBox = All(w, System.Windows.Automation.ControlType.Edit).Any(e => { try { return e.Current.IsPassword; } catch { return false; } });
                    var canConnect = names.Any(n => n.Equals("Connect", StringComparison.OrdinalIgnoreCase));
                    var isUp = names.Any(n => n.Equals("Disconnect", StringComparison.OrdinalIgnoreCase));
                    L(canConnect || isUp, $"FortiClient window “{NameOf(w)}”: {(isUp ? "connected (Disconnect shown)" : canConnect ? "Connect button found — Nova can press it" : "no Connect button visible")} · password box {(pwBox ? "shown" : "not shown")} · buttons: {string.Join(", ", names)}");
                }
            }
            catch (Exception e) { L(false, "FortiClient window: " + e.Message); }
        }
        L(Now is State.Up ? true : Now is State.Off ? null : false, "Now: " + Line + (Way.Length > 0 ? " · last way: " + Way : "") + (tries > 0 ? $" · tries {tries}" : ""));
        try
        {
            foreach (var e in Log.ReadAll().Where(x => x.Area == "office vpn").Take(6)) sb.Append("   log ").Append(Log.Format(e).Split('\n')[0]).Append('\n');
        }
        catch { }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the watchdog

    public static void Start()
    {
        if (thread != null) return;
        thread = new Thread(() => Guard.Run("office vpn", Loop)) { IsBackground = true, Name = "Nova office VPN" };
        thread.Start();
        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => wake.Set();
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Resume) { lastTry = DateTime.MinValue; wake.Set(); } };
        }
        catch { }
    }

    /// <summary>Waiting between tries after a drop: at once, then 5 s, 15 s, 30 s, then every minute.</summary>
    static TimeSpan Backoff() => tries switch { 0 => TimeSpan.Zero, 1 => TimeSpan.FromSeconds(5), 2 => TimeSpan.FromSeconds(15), 3 => TimeSpan.FromSeconds(30), _ => TimeSpan.FromSeconds(60) };

    static void Loop()
    {
        while (true)
        {
            try
            {
                if (!On) { Set(State.Off, "🌐 Office VPN is off (Settings ▸ 🌐 Office VPN)"); wake.WaitOne(TimeSpan.FromSeconds(30)); continue; }
                var up = AdapterUp();
                var server = up && ServerOk();
                if (up && server) Set(State.Up, $"🟢 Connected ({Profile}) since {Since:HH:mm} · office server answers" + (Way.Length > 0 ? $" · via {Way}" : ""));
                else if (up) Set(State.UpNoServer, $"🟡 Connected, but the office server ({Probe}) does not answer");
                else if (Now != State.Connecting || DateTime.Now - lastTry > TimeSpan.FromSeconds(45)) Set(State.Down, Want ? "🔴 Not connected — " + (Auto ? "reconnecting…" : "click to connect") : "🔴 Not connected — click the 🌐 icon to connect");
                if (!up && Want && (Auto || tries == 0) && DateTime.Now - lastTry > Backoff()) TryConnect();
            }
            catch (Exception e) { Log.Warn("office vpn", e.Message); }
            wake.WaitOne(TimeSpan.FromSeconds(Now == State.Connecting ? 2 : 5));
        }
    }

    // ------------------------------------------------------------------ the tray icon (🟢 🟡 🔴)

    static readonly Dictionary<State, Icon> icons = new();

    public static Icon IconFor(State s)
    {
        lock (icons)
        {
            if (icons.TryGetValue(s, out var i)) return i;
            var col = s switch { State.Up => Color.FromArgb(0x3D, 0xDC, 0x97), State.UpNoServer or State.Connecting => Color.FromArgb(0xFB, 0xBF, 0x24), State.Down => Color.FromArgb(0xFF, 0x6B, 0x6B), _ => Color.FromArgb(0x8A, 0x92, 0xA6) };
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // a shield with a lock-dot, in the state's colour
                using var path = new GraphicsPath();
                path.AddLines(new[] { new Point(16, 2), new Point(29, 7), new Point(28, 18), new Point(16, 30), new Point(4, 18), new Point(3, 7) });
                path.CloseFigure();
                using var fill = new SolidBrush(col);
                g.FillPath(fill, path);
                using var pen = new Pen(Color.FromArgb(0x14, 0x14, 0x18), 2);
                g.DrawPath(pen, path);
                using var w = new SolidBrush(Color.White);
                g.FillRectangle(w, 11, 15, 10, 8);
                using var arc = new Pen(Color.White, 2.4f);
                g.DrawArc(arc, 12, 9, 8, 10, 180, 180);
            }
            i = Icon.FromHandle(bmp.GetHicon());
            icons[s] = i;
            return i;
        }
    }
}
