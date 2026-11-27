using System.Text.RegularExpressions;
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
    /// <summary>🔓 OpenConnect VPN (open source, “Fortinet SSL VPN” protocol): its program and the server name saved in it.</summary>
    public static string OcPath { get => Store.Str("win_vpn_oc", ""); set => Store.Set("win_vpn_oc", value); }
    public static string OcProfile { get => Store.Str("win_vpn_oc_profile", "AHMED-MZA"); set => Store.Set("win_vpn_oc_profile", value); }
    /// <summary>Which way first: "cli" = OpenConnect command line · "oc" = OpenConnect window · "forti" = FortiClient; then the others.</summary>
    public static string Order { get => Store.Str("win_vpn_order", "oc"); set => Store.Set("win_vpn_order", value); }
    /// <summary>Off (default): only the chosen connection type is used. On: if it fails, Nova tries the other ways too.</summary>
    public static bool Fallback { get => Store.Bool("win_vpn_fallback", false); set => Store.SetBool("win_vpn_fallback", value); }
    public static string WayName(string w) => w == "cli" ? "⌨ OpenConnect command line" : w == "forti" ? "FortiClient" : "OpenConnect window";

    /// <summary>The office firewall's certificate as OpenConnect pins it (pin-sha256:…) — only this server is accepted by the command line.</summary>
    public static string Pin { get => Store.Str("win_vpn_pin", ""); set => Store.Set("win_vpn_pin", value); }

    /// <summary>openconnect.exe — only from Program Files (only administrators can change it there; the admin helper runs nothing else).</summary>
    public static string Cli()
    {
        // 🌐 Nova's own engine first (copied into Program Files by the 🛡 helper set-up), then an OpenConnect-GUI install
        if (File.Exists(VpnHelper.EngineExe)) return VpnHelper.EngineExe;
        foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            foreach (var sub in new[] { "OpenConnect-GUI", "OpenConnect", "openconnect" })
            {
                var f = Path.Combine(d, sub, "openconnect.exe");
                if (File.Exists(f)) return f;
            }
        return null;
    }
    public static string ToolPath { get => Store.Str("win_vpn_tool", ""); set => Store.Set("win_vpn_tool", value); }
    public static string Password { get => Store.Secret("win_vpn_pw"); set => Store.SetSecret("win_vpn_pw", value ?? ""); }

    // ------------------------------------------------------------------ 🌐 Nova VPN engine options (as in OpenConnect's Edit window)
    public static readonly (string Id, string Name)[] Protocols =
    {
        ("anyconnect", "Cisco AnyConnect or OpenConnect"), ("nc", "Juniper Network Connect"), ("gp", "Palo Alto Networks GlobalProtect"),
        ("pulse", "Pulse Connect Secure"), ("f5", "F5 BIG-IP SSL VPN"), ("fortinet", "Fortinet SSL VPN"), ("array", "Array SSL VPN"),
    };
    public static string Proto { get => Store.Str("win_vpn_proto", "fortinet"); set => Store.Set("win_vpn_proto", value); }
    public static string Group { get => Store.Str("win_vpn_group", ""); set => Store.Set("win_vpn_group", value); }
    public static string CaFile { get => Store.Str("win_vpn_cafile", ""); set => Store.Set("win_vpn_cafile", value); }
    public static string OtpMode { get => Store.Str("win_vpn_otpmode", "none"); set => Store.Set("win_vpn_otpmode", value); }
    /// <summary>The OTP token secret: like the password, encrypted on this PC and never in a backup.</summary>
    public static string OtpSecret { get => Store.Secret("win_vpn_otp"); set => Store.SetSecret("win_vpn_otp", value ?? ""); }
    public static string UserCert { get => Store.Str("win_vpn_ucert", ""); set => Store.Set("win_vpn_ucert", value); }
    public static string UserKey { get => Store.Str("win_vpn_ukey", ""); set => Store.Set("win_vpn_ukey", value); }
    public static string Iface { get => Store.Str("win_vpn_iface", "Nova VPN"); set => Store.Set("win_vpn_iface", value); }
    public static string Script { get => Store.Str("win_vpn_script", ""); set => Store.Set("win_vpn_script", value); }
    public static string LogLevel { get => Store.Str("win_vpn_loglevel", "normal"); set => Store.Set("win_vpn_loglevel", value); }
    public static int ReconnectTimeout { get => Math.Clamp(Store.Int("win_vpn_rto", 300), 10, 3600); set => Store.SetInt("win_vpn_rto", value); }
    /// <summary>Try the fast UDP channel (DTLS); OpenConnect falls back to SSL by itself when the firewall does not offer it.</summary>
    public static bool Dtls { get => Store.Bool("win_vpn_dtls", true); set => Store.SetBool("win_vpn_dtls", value); }
    public static int DtlsWait { get => Math.Clamp(Store.Int("win_vpn_dtlswait", 25), 5, 120); set => Store.SetInt("win_vpn_dtlswait", value); }
    public static string Proxy { get => Store.Str("win_vpn_proxy", ""); set => Store.Set("win_vpn_proxy", value); }
    public static bool StartWithWindows { get => Store.Bool("win_vpn_boot", true); set => Store.SetBool("win_vpn_boot", value); }
    public static int IdleHours { get => Store.Int("win_vpn_idle", 0); set => Store.SetInt("win_vpn_idle", value); }
    public static string EngineLog => Path.Combine(VpnHelper.Dir, "openconnect.log");

    /// <summary>What the engine log says, in plain words: why it failed, the office IP, the fast channel, when the session ends.</summary>
    public static (string Reason, string Ip, string Route, bool? Dtls, string Ends, string Firewall) ReadEngineLog()
    {
        string reason = "", ip = "", route = "", ends = "", fw = ""; bool? dtls = null;
        try
        {
            if (!File.Exists(EngineLog)) return (reason, ip, route, dtls, ends, fw);
            string[] lines;
            using (var fs = new FileStream(EngineLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var sr = new StreamReader(fs)) lines = sr.ReadToEnd().Split('\n');
            foreach (var raw in lines)
            {
                var l = raw.Trim();
                Match m;
                if ((m = Regex.Match(l, @"Got Legacy IP address (\S+)")).Success) ip = m.Groups[1].Value;
                else if ((m = Regex.Match(l, @"Got IPv4 route (\S+)")).Success) route = m.Groups[1].Value;
                else if ((m = Regex.Match(l, @"Configured as (\S+?),")).Success && ip.Length == 0) ip = m.Groups[1].Value;
                else if ((m = Regex.Match(l, @"Session authentication will expire at (.+)$")).Success) ends = m.Groups[1].Value;
                else if ((m = Regex.Match(l, @"Reported platform is (.+)$")).Success) fw = m.Groups[1].Value;
                if (Regex.IsMatch(l, @"DTLS (connected|established|initiali[sz]ed)|Established DTLS", RegexOptions.IgnoreCase)) dtls = true;
                else if (Regex.IsMatch(l, @"DTLS disabled|DTLS.*(fail|not support)|No DTLS", RegexOptions.IgnoreCase)) dtls ??= false;
                if (Regex.IsMatch(l, @"Invalid credentials|Login failed|authentication fail|Failed to authenticate", RegexOptions.IgnoreCase)) reason = "The firewall refused the login (wrong password?)";
                else if ((m = Regex.Match(l, @"didn't match:?\s*(pin-sha256:\S+)?", RegexOptions.IgnoreCase)).Success && l.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                    reason = "⚠ The firewall's certificate is NOT the one saved in Nova (pin) — check with IT before trusting it" + (m.Groups[1].Success ? " · it now shows " + m.Groups[1].Value : "");
                else if (Regex.IsMatch(l, @"does not match hostname|signer not found|failed verification", RegexOptions.IgnoreCase) && !reason.StartsWith("⚠"))
                    reason = Pin.Length == 0
                        ? "Your firewall uses its own certificate — paste the Server certificate pin (OpenConnect ▸ Edit ▸ Server Certificate) in 🌐 Nova VPN and press Save"
                        : "";   // normal for your firewall: the saved pin is what makes it trusted
                else if (Regex.IsMatch(l, @"Failed to connect|Connection refused|timed out|getaddrinfo|resolve", RegexOptions.IgnoreCase)) reason = "Cannot reach the gateway (internet down?)";
                else if (Regex.IsMatch(l, @"Cookie was rejected|session.*expired", RegexOptions.IgnoreCase)) reason = "The session ended at the firewall — logging in again";
                else if (Regex.IsMatch(l, @"Got CONNECT response|Connected as|Configured as|SSL connected", RegexOptions.IgnoreCase)) reason = "";
            }
        }
        catch { }
        return (reason, ip, route, dtls, ends, fw);
    }

    /// <summary>📱 While the phone uses the office VPN (one session per account), the PC does not reconnect until this time.</summary>
    public static DateTime HoldUntil = DateTime.MinValue;
    public static volatile State Now = State.Off;
    public static DateTime Since = DateTime.Now;
    public static int DropsToday; static DateTime dropDay = DateTime.Today;
    /// <summary>When it came back after the last drop (seconds), for the 🌐 Nova VPN window.</summary>
    public static int LastBackSeconds = -1;
    public static volatile string Line = "🌐 Office VPN is off (Settings ▸ 🌐 Office VPN)";
    public static volatile string Way = "";
    public static event Action Changed;

    static readonly AutoResetEvent wake = new(false);
    static Thread thread;
    static DateTime lastTry = DateTime.MinValue, droppedAt = DateTime.MinValue;
    static int tries;
    static int trying;

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

    static readonly string[] OcGuesses =
    {
        @"C:\Program Files\OpenConnect-GUI\openconnect-gui.exe", @"C:\Program Files (x86)\OpenConnect-GUI\openconnect-gui.exe",
        @"C:\Program Files\OpenConnect GUI\openconnect-gui.exe", @"C:\Program Files\openconnect-gui\openconnect-gui.exe",
    };
    /// <summary>OpenConnect-GUI — set in Settings, found in the usual places, or taken from its running process.</summary>
    public static string Oc()
    {
        if (OcPath.Length > 0 && File.Exists(OcPath)) return OcPath;
        var g = OcGuesses.FirstOrDefault(File.Exists);
        if (g != null) return g;
        try { foreach (var p in Process.GetProcessesByName("openconnect-gui")) { var f = p.MainModule?.FileName; if (f != null) { OcPath = f; return f; } } } catch { }
        return null;
    }

    internal static readonly string[] FortiProcs = { "FortiClient" };
    internal static readonly string[] OcProcs = { "openconnect-gui" };

    // ------------------------------------------------------------------ windows of a program (found by the program, never by the title)

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    internal static HashSet<int> Pids(string[] names) => names.SelectMany(n => { try { return Process.GetProcessesByName(n); } catch { return Array.Empty<Process>(); } }).Select(p => p.Id).ToHashSet();

    /// <summary>A window hidden in the tray has no buttons: its main window is shown again (restored), as clicking its tray icon would.</summary>
    internal static void Reveal(string[] names)
    {
        var ids = Pids(names);
        if (ids.Count == 0) return;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!ids.Contains((int)pid) || GetWindow(h, 4) != IntPtr.Zero) return true;   // owned windows (pop-ups) are left alone
            var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
            // only the app's own window (never the hidden helper windows: “Default IME”, “MSCTFIME UI”…)
            if (!Regex.IsMatch(sb.ToString(), "Forti|OpenConnect", RegexOptions.IgnoreCase)) return true;
            ShowWindow(h, 9); SetForegroundWindow(h);   // SW_RESTORE
            return true;
        }, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    /// <summary>The program's visible windows, from Windows' own window list (UI Automation's top-level list can miss Electron / Qt windows).</summary>
    static List<IntPtr> Handles(HashSet<int> ids)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!ids.Contains((int)pid) || !IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true;
            var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
            if (Regex.IsMatch(sb.ToString(), "Forti|OpenConnect", RegexOptions.IgnoreCase)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    internal static System.Windows.Automation.AutomationElement AppWindow(string[] names, int seconds, Func<System.Windows.Automation.AutomationElement, bool> good = null)
    {
        for (int i = 0; i < Math.Max(1, seconds); i++)
        {
            var ids = Pids(names);
            System.Windows.Automation.AutomationElement first = null;
            if (ids.Count > 0)
                foreach (var h in Handles(ids))
                {
                    System.Windows.Automation.AutomationElement w;
                    try { w = System.Windows.Automation.AutomationElement.FromHandle(h); } catch { continue; }
                    if (w == null) continue;
                    first ??= w;
                    try { if (good == null || good(w)) return w; } catch { }
                }
            if (i >= Math.Min(3, seconds - 1) && first != null && (good == null || i == seconds - 1)) return first;
            if (seconds > 1) Thread.Sleep(1000);
        }
        return null;
    }

    internal static bool HasButton(System.Windows.Automation.AutomationElement w, string name) =>
        All(w, System.Windows.Automation.ControlType.Button).Any(b => NameOf(b).Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ status

    static bool VpnAdapter(NetworkInterface n) => Regex.IsMatch(n.Description + " " + n.Name, "Forti|OpenConnect|Wintun|TAP-Windows|Nova VPN|" + Regex.Escape(OcProfile) + "|" + Regex.Escape(Iface) + "|" + Regex.Escape(Gateway), RegexOptions.IgnoreCase);

    static bool AdapterUp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up &&
                VpnAdapter(n));
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
            if (droppedAt > DateTime.MinValue) LastBackSeconds = (int)(DateTime.Now - droppedAt).TotalSeconds;
            if (droppedAt > DateTime.MinValue) Program.Balloon("🟢 Office VPN", "Reconnected" + took + " — your office folders are back.");
            droppedAt = DateTime.MinValue; tries = 0;
            // the office folders are reachable again: Nova refreshes their lists and the ⚡ offline copies
            try { Projects.Indexer.Refresh(); ProjectSources.Offline.Wake(); } catch { }
        }
        if (s is State.Down && was is State.Up or State.UpNoServer && Want)
        {
            droppedAt = DateTime.Now;
            if (dropDay != DateTime.Today) { dropDay = DateTime.Today; DropsToday = 0; }
            DropsToday++;
            Program.Balloon("🔴 Office VPN dropped", Auto ? "Nova is reconnecting…" : "Click the 🌐 icon to connect again.");
        }
    }

    // ------------------------------------------------------------------ connect / disconnect

    public static void Connect()
    {
        // a press of Connect always tries now — even with “reconnect automatically” off and earlier failed tries
        Want = true;
        tries = 0;
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
                // only the chosen connection type (all of them only when “also try the other ways” is ticked)
                var all = Fallback;
                if ((all || Order == "cli") && Process.GetProcessesByName("openconnect").Any() && VpnHelper.Installed) VpnHelper.Run("cli-disconnect", "");
                if ((all || Order is not ("cli" or "forti")) && Pids(OcProcs).Count > 0) { if (VpnHelper.Installed) VpnHelper.Run("oc-disconnect", ""); else GuiButton(OcProcs, "Disconnect"); }
                if (all || Order == "forti")
                {
                    if (Tool() is { } t) Run(t, "disconnect", 20000);
                    else if (Pids(FortiProcs).Count > 0) GuiButton(FortiProcs, "Disconnect");
                }
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
        Set(State.Connecting, $"🟡 Connecting to {Host} with {WayName(Order)}…" + (tries > 1 ? $" try {tries}" : ""));
        // A · both: OpenConnect first and FortiClient if it fails (or the other way round); after a failed try the other one goes first
        var ways = new List<string>();
        if (!Fallback) ways.Add(Order is "cli" or "forti" ? Order : "oc");   // your choice only — never another program
        else
        {
            if (Cli() != null && VpnHelper.Installed && Password.Length > 0) ways.Add("cli");
            if (Oc() != null) ways.Add("oc");
            if (Tool() != null || Gui() != null) ways.Add("forti");
            if (ways.Count == 0) { Set(State.Down, "🔴 Neither OpenConnect nor FortiClient is installed on this PC"); Want = false; return; }
            if (ways.Remove(Order)) ways.Insert(0, Order);
        }
        // after a failed try the next one goes first
        if (ways.Count > 1) { var k = (tries - 1) % ways.Count; ways = ways.Skip(k).Concat(ways.Take(k)).ToList(); }
        var why = new List<string>();
        foreach (var way in ways)
        {
            if (way == "cli" && (Cli() == null || !VpnHelper.Installed || Password.Length == 0))
            { why.Add("⌨ command line not ready — " + (Cli() == null || !VpnHelper.Installed ? "press 🛡 Set up the VPN helper (it installs the engine)" : "add your password in 🌐 Nova VPN")); continue; }
            if (way == "oc" && Oc() == null) { why.Add("OpenConnect-GUI is not installed"); continue; }
            if (way == "forti" && Tool() == null && Gui() == null) { why.Add("FortiClient is not installed"); continue; }
            var r = way == "cli" ? ConnectCli() : way == "oc" ? ConnectOc() : ConnectForti();
            if (r.Length == 0) return;
            why.Add(r); Log.Warn("office vpn", r);
        }
        if (AdapterUp() && ServerOk()) return;   // it came up meanwhile (or you connected yourself)
        Set(State.Down, "🔴 " + string.Join(" · ", why));
    }

    /// <summary>⌨ OpenConnect's command line, started hidden by the 🛡 helper (it needs administrator rights): it connects with your password
    /// (passed to the helper encrypted for your Windows account), accepts only the pinned office certificate, and stays running while connected.</summary>
    static string ConnectCli()
    {
        Way = "OpenConnect command line";
        try
        {
            string Enc(string v) => v.Length == 0 ? "" : Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(v), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            // every option goes to the helper as one line; it checks each value again before use
            var o = new System.Text.Json.Nodes.JsonObject
            {
                ["proto"] = Proto, ["gw"] = Gateway, ["port"] = Port, ["user"] = User, ["pin"] = Pin, ["group"] = Group, ["cafile"] = CaFile,
                ["otp"] = OtpMode, ["otpBlob"] = OtpMode == "none" ? "" : Enc(OtpSecret), ["ucert"] = UserCert, ["ukey"] = UserKey, ["iface"] = Iface,
                ["script"] = Script, ["log"] = LogLevel, ["rto"] = ReconnectTimeout, ["dtls"] = Dtls, ["proxy"] = Proxy, ["pw"] = Enc(Password),
            };
            var r = VpnHelper.Run("cli-connect", "", 45, Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ToJsonString())));
            return r.Length == 0 ? "" : "OpenConnect command line: " + r;
        }
        catch (Exception e) { return "OpenConnect command line: " + e.Message; }
    }

    static string ConnectOc()
    {
        Way = $"OpenConnect ({OcProfile})";
        try
        {
            // OpenConnect always runs as administrator: the 🛡 VPN helper presses its buttons
            if (VpnHelper.Installed) { var r = VpnHelper.Run("oc-connect", OcProfile); return r.Length == 0 ? "" : "OpenConnect: " + r; }
            var d = OcConnect(Oc());
            return d.Length == 0 ? "" : "OpenConnect: " + d + " — set up the 🛡 VPN helper in Settings ▸ 🌐 Office VPN (OpenConnect runs as administrator)";
        }
        catch (Exception e) { return "OpenConnect: " + e.Message; }
    }

    static string ConnectForti()
    {
        try
        {
            if (Tool() is { } t)
            {
                Way = "FortiClientTools (command line)";
                var pw = Password;
                if (pw.Length == 0) return "add your VPN password in Settings ▸ 🌐 Office VPN for the FortiClientTools command line";
                // -c takes the certificate's subject[:issuer] · -i ignore the certificate warning · -m minimized · -q no message boxes
                var args = $"connect -s {Q("Nova " + Profile)} -h {Q(Host)} -u {Q(User + ":" + pw)}" + (Cert.Length > 0 && !File.Exists(Cert) ? $" -c {Q(Cert)}" : "") + " -i -m -q";
                _ = Task.Run(() => { try { Run(t, args, 90000); } catch (Exception e) { Log.Warn("office vpn", "connect: " + e.Message); } });
                return "";
            }
            if (Gui() is { } g)
            {
                Way = $"FortiClient ({Profile})";
                if (Pids(FortiProcs).Count == 0) { Process.Start(new ProcessStartInfo(g) { UseShellExecute = true }); Thread.Sleep(6000); }
                Reveal(FortiProcs);
                var r = GuiConnect();
                if (r.Length > 0 && VpnHelper.Installed) { var h = VpnHelper.Run("forti-connect", ""); r = h.Length == 0 ? "" : r + " · with the helper: " + h; }
                return r;
            }
            return "FortiClient is not installed";
        }
        catch (Exception e) { return "FortiClient: " + e.Message; }
    }

    internal static System.Windows.Automation.AutomationElement FortiWindow(int seconds) =>
        AppWindow(FortiProcs, seconds, w => HasButton(w, "Connect") || HasButton(w, "Disconnect"));

    internal static List<System.Windows.Automation.AutomationElement> All(System.Windows.Automation.AutomationElement w, System.Windows.Automation.ControlType t) =>
        w.FindAll(System.Windows.Automation.TreeScope.Descendants, new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, t))
            .Cast<System.Windows.Automation.AutomationElement>().ToList();

    internal static string NameOf(System.Windows.Automation.AutomationElement e) { try { return e.Current.Name ?? ""; } catch { return ""; } }

    /// <summary>Your free FortiClient: the window is found; the username / password are typed when it asks (“Prompt on login”); Connect is pressed.
    /// Returns "" when Connect was pressed (or it is already connected), otherwise what went wrong.</summary>
    internal static string GuiConnect()
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

    /// <summary>A button in the program's window (FortiClient or OpenConnect), pressed through Windows UI Automation — as a screen reader would.</summary>
    internal static bool GuiButton(string[] procs, string name)
    {
        try
        {
            Reveal(procs);
            var w = AppWindow(procs, 8, x => HasButton(x, name));
            var btn = w == null ? null : All(w, System.Windows.Automation.ControlType.Button).FirstOrDefault(b => NameOf(b).Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (btn != null && btn.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var pat)) { ((System.Windows.Automation.InvokePattern)pat).Invoke(); return true; }
        }
        catch (Exception e) { Log.Warn("office vpn", "window: " + e.Message); }
        return false;
    }

    /// <summary>🔓 OpenConnect: its window is shown, the server “AHMED-MZA” chosen, Connect pressed (its saved password and Batch mode do the rest).
    /// Returns "" when Connect was pressed (or it is already connected), otherwise what went wrong.</summary>
    internal static string OcConnect(string exe) => OcConnectWith(exe, OcProfile);

    internal static string OcConnectWith(string exe, string profile)
    {
        try
        {
            if (Pids(OcProcs).Count == 0) { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); Thread.Sleep(5000); }
            Reveal(OcProcs);
            var w = AppWindow(OcProcs, 12, x => HasButton(x, "Connect") || HasButton(x, "Disconnect"));
            if (w == null) return "OpenConnect's window did not open";
            if (HasButton(w, "Disconnect")) return "";
            var combo = All(w, System.Windows.Automation.ControlType.ComboBox).FirstOrDefault();
            if (combo != null)
            {
                string cur = NameOf(combo);
                try { if (combo.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var vp)) cur = ((System.Windows.Automation.ValuePattern)vp).Current.Value ?? cur; } catch { }
                if (!cur.Trim().Equals(profile, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (combo.TryGetCurrentPattern(System.Windows.Automation.ExpandCollapsePattern.Pattern, out var ec)) { ((System.Windows.Automation.ExpandCollapsePattern)ec).Expand(); Thread.Sleep(600); }
                        var item = combo.FindAll(System.Windows.Automation.TreeScope.Descendants, new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.ListItem))
                            .Cast<System.Windows.Automation.AutomationElement>().FirstOrDefault(x => NameOf(x).Trim().Equals(profile, StringComparison.OrdinalIgnoreCase));
                        if (item != null && item.TryGetCurrentPattern(System.Windows.Automation.SelectionItemPattern.Pattern, out var si)) ((System.Windows.Automation.SelectionItemPattern)si).Select();
                        else if (item == null) return $"OpenConnect has no server named “{profile}” — check the name in Settings ▸ 🌐 Office VPN";
                        if (combo.TryGetCurrentPattern(System.Windows.Automation.ExpandCollapsePattern.Pattern, out var ec2)) ((System.Windows.Automation.ExpandCollapsePattern)ec2).Collapse();
                    }
                    catch (Exception e) { Log.Warn("office vpn", "OpenConnect server list: " + e.Message); }
                }
            }
            var connect = All(w, System.Windows.Automation.ControlType.Button).FirstOrDefault(b => NameOf(b).Trim().Equals("Connect", StringComparison.OrdinalIgnoreCase));
            if (connect == null) return "OpenConnect's Connect button was not found";
            if (!connect.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var ip)) return "OpenConnect's Connect button could not be pressed";
            ((System.Windows.Automation.InvokePattern)ip).Invoke();
            return "";
        }
        catch (UnauthorizedAccessException) { return "OpenConnect runs as administrator — Windows does not let Nova press its buttons (run OpenConnect normally, or Nova as administrator)"; }
        catch (Exception e) { return "OpenConnect window: " + e.Message; }
    }

    // ------------------------------------------------------------------ 🧪 the test (Settings and Claude's nova.vpn) — read only, it never connects

    public static string Diagnose()
    {
        var sb = new StringBuilder($"🧪 Office VPN test · {DateTime.Now:dd MMM HH:mm:ss}\n");
        void L(bool? ok, string t) => sb.Append(ok == true ? "✅ " : ok == false ? "❌ " : "• ").Append(t).Append('\n');
        L(On, "Companion " + (On ? "on" : "off — tick “Use the Office VPN companion”") + $" · reconnect automatically {(Auto ? "on" : "off")} · you want it up: {(Want ? "yes" : "no — press Connect")}");
        L(null, $"Connection “{Profile}” · SSL-VPN · gateway {Gateway} · port {Port} · user {User} · {(SaveLogin ? "Save login" : "Prompt on login")} · certificate {(Cert.Length > 0 ? Path.GetFileName(Cert) : "None")}{(Sso ? " · SSO" : "")}{(DualStack ? " · dual-stack" : "")} · password in Nova: {(Password.Length > 0 ? "yes (encrypted)" : "no")}");
        var vpnUp = AdapterUp() && ServerOk();
        var useCli = Order == "cli" || Fallback; var useForti = Order == "forti" || Fallback; var useOc = Order is not ("cli" or "forti") || Fallback;
        L(null, $"Connection type tested: {WayName(Order)}" + (Fallback ? " · then the others (ticked)" : " only (the others are not used)"));
        var g = Gui(); var t = Tool();
        string Ver(string f) { try { return FileVersionInfo.GetVersionInfo(f).ProductVersion ?? ""; } catch { return ""; } }
        if (useForti)
        {
            L(g != null, g != null ? $"FortiClient {Ver(g)} installed · {(Process.GetProcessesByName("FortiClient").Any() ? "running" : "not running")}" : "FortiClient not found in Program Files");
            L(t != null ? true : null, t != null ? "FortiClientTools command line: " + t : "FortiClientTools not on this PC — Nova uses the FortiClient window");
        }
        var hs = VpnHelper.State();
        if (useCli || useOc) L(hs.StartsWith("set up (") ? true : false, "🛡 VPN admin helper: " + hs + (hs.StartsWith("set up (") ? "" : " — needed for OpenConnect (it runs as administrator): Settings ▸ 🌐 Office VPN ▸ Set up the VPN helper"));
        try
        {
            var pat = "^(" + string.Join("|", new[] { useForti ? "forti" : null, useCli || useOc ? "openconnect" : null }.Where(x => x != null)) + ")";
            foreach (var pr in Process.GetProcesses().Where(x => Regex.IsMatch(x.ProcessName, pat, RegexOptions.IgnoreCase)).OrderBy(x => x.ProcessName))
            {
                string adm; try { _ = pr.MainModule; adm = ""; } catch { adm = " · administrator"; }
                string ttl; try { ttl = pr.MainWindowTitle; } catch { ttl = ""; }
                sb.Append("   process ").Append(pr.ProcessName).Append(adm).Append(ttl.Length > 0 ? " · window “" + ttl + "”" : "").Append('\n');
            }
        }
        catch { }
        if (hs.StartsWith("set up (") && useOc)
        {
            var seen = VpnHelper.Run("look", "", 30);
            if (vpnUp) seen = Regex.Replace(seen, @"no Connect button( · buttons: )?$", "connected — OpenConnect minimises itself, no button to show", RegexOptions.Multiline);
            L(vpnUp ? true : null, "Seen by the helper: " + seen.Replace("\n", "\n   "));
        }
        var cli = Cli();
        if (useCli) L(cli != null ? true : false, cli != null ? $"OpenConnect command line: {cli} · office certificate pin {(Pin.Length > 0 ? "set" : "NOT set (paste it from OpenConnect ▸ Edit ▸ Server Certificate)")}" : "OpenConnect command line (openconnect.exe) not in Program Files\\OpenConnect-GUI — the command-line way is off");
        try
        {
            var lf = Path.Combine(VpnHelper.Dir, "openconnect.log");
            if (useCli && File.Exists(lf))
                foreach (var ln in File.ReadAllLines(lf).Where(x => x.Trim().Length > 0 && !Regex.IsMatch(x, "cookie|passw|SVPN", RegexOptions.IgnoreCase)).TakeLast(8))
                    sb.Append("   openconnect: ").Append(ln.Length > 220 ? ln[..220] : ln).Append('\n');
        }
        catch { }
        var oc = Oc();
        if (useOc) L(oc != null ? true : false, oc != null ? $"OpenConnect {Ver(oc)} installed · {(Pids(OcProcs).Count > 0 ? "running" : "not running")} · server “{OcProfile}”" : "OpenConnect-GUI not found (choose openconnect-gui.exe in Settings if it is installed elsewhere)");
        if (useOc && oc != null && Pids(OcProcs).Count > 0)
            try
            {
                var w = AppWindow(OcProcs, 1);
                if (w == null) L(null, "OpenConnect window hidden in the tray (Nova shows it when connecting)");
                else
                {
                    var combo = All(w, System.Windows.Automation.ControlType.ComboBox).FirstOrDefault();
                    string cur = combo == null ? "" : NameOf(combo);
                    try { if (combo != null && combo.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var vp)) cur = ((System.Windows.Automation.ValuePattern)vp).Current.Value ?? cur; } catch { }
                    var isUp = HasButton(w, "Disconnect") || vpnUp; var can = HasButton(w, "Connect");
                    if (vpnUp && !HasButton(w, "Disconnect")) { L(true, "OpenConnect: connected (its window is minimised — nothing to press)"); }
                    else L(isUp || can, $"OpenConnect window “{NameOf(w)}”: server shown “{cur}” · {(isUp ? "connected (Disconnect shown)" : can ? "Connect button found — Nova can press it" : "no Connect button visible")}");
                }
            }
            catch (Exception e) { L(false, "OpenConnect window: " + e.Message); }
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
            var ad = NetworkInterface.GetAllNetworkInterfaces().Where(VpnAdapter).ToList();
            ad = ad.Where(n => useForti && n.Description.Contains("Forti", StringComparison.OrdinalIgnoreCase) || (useCli || useOc) && !n.Description.Contains("Forti", StringComparison.OrdinalIgnoreCase)).ToList();
            if (ad.Count == 0) L(null, "No VPN network adapter for this connection type yet (it appears when connected)");
            foreach (var n in ad) L(n.OperationalStatus == OperationalStatus.Up ? true : null, $"Adapter “{n.Name}” ({n.Description}): {n.OperationalStatus}" + (n.OperationalStatus == OperationalStatus.Up ? " · " + string.Join(", ", n.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => u.Address)) : ""));
        }
        catch (Exception e) { L(false, "Adapters: " + e.Message); }
        var (ph, pp) = ProbeTarget();
        if (ph.Equals(Gateway, StringComparison.OrdinalIgnoreCase)) L(false, $"“Office server to check” is the gateway itself — use an office server that answers only through the VPN (e.g. 192.168.1.12, the O: N: S: server)");
        else if (ph.Length > 0) { var ok = Tcp(ph, pp, 2000); L(ok, $"Office server {ph}:{pp} " + (ok ? "answers (the VPN works)" : "does not answer (normal while the VPN is down)")); }
        if (useForti && g != null && t == null)
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
            foreach (var e in Log.ReadAll().Where(x => x.Area == "office vpn" || x.Area == "2.3 office vpn").Take(6)) sb.Append("   log ").Append(Log.Format(e).Split('\n')[0]).Append('\n');
        }
        catch { }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the watchdog

    public static void Start()
    {
        if (thread != null) return;
        // “Connect when Windows starts” (Nova starts with Windows)
        if (On && StartWithWindows) { Want = true; tries = 0; }
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

    static long idleBytes = -1; static DateTime idleSince = DateTime.Now;

    /// <summary>“Disconnect when idle”: no traffic through the VPN (under 1 MB) for the chosen hours.</summary>
    static void IdleCheck()
    {
        if (IdleHours <= 0) { idleBytes = -1; return; }
        try
        {
            var n = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.OperationalStatus == OperationalStatus.Up && VpnAdapter(x) && !x.Description.Contains("Fortinet Virtual Ethernet Adapter (NDIS", StringComparison.OrdinalIgnoreCase));
            if (n == null) return;
            var st = n.GetIPStatistics(); var b = st.BytesReceived + st.BytesSent;
            if (idleBytes < 0 || b - idleBytes > 1 << 20) { idleBytes = b; idleSince = DateTime.Now; return; }
            if (DateTime.Now - idleSince > TimeSpan.FromHours(IdleHours))
            {
                idleBytes = -1;
                Program.Balloon("🌐 Office VPN", $"Disconnected — no office traffic for {IdleHours} h.");
                Disconnect();
            }
        }
        catch { }
    }

    static void Loop()
    {
        while (true)
        {
            try
            {
                if (!On) { Set(State.Off, "🌐 Office VPN is off (Settings ▸ 🌐 Office VPN)"); wake.WaitOne(TimeSpan.FromSeconds(30)); continue; }
                var adapter = AdapterUp();
                var server = ServerOk();
                var up = adapter || server;
                if (server && !adapter) Set(State.Up, $"🟢 Office network reachable since {Since:HH:mm} (in the office, or a VPN Nova does not know)");
                else if (up && server) { Set(State.Up, $"🟢 Connected since {Since:HH:mm} · office server answers" + (Way.Length > 0 ? $" · via {Way}" : "")); IdleCheck(); }
                else if (up) Set(State.UpNoServer, $"🟡 Connected, but the office server ({Probe}) does not answer");
                else if (trying == 0 && (Now != State.Connecting || DateTime.Now - lastTry > TimeSpan.FromSeconds(45))) Set(State.Down, Want ? "🔴 Not connected — " + (Auto ? "reconnecting…" : "click to connect") : "🔴 Not connected — click the 🌐 icon to connect");
                // the connection is tried in the background: the watch goes on, so 🟢 shows as soon as the VPN is up
                if (!up && Want && DateTime.Now >= HoldUntil && (Auto || tries == 0) && DateTime.Now - lastTry > Backoff() && Interlocked.CompareExchange(ref trying, 1, 0) == 0)
                {
                    lastTry = DateTime.Now;
                    _ = Task.Run(() => { try { TryConnect(); } catch (Exception e) { Log.Warn("office vpn", e.Message); } finally { Interlocked.Exchange(ref trying, 0); lastTry = DateTime.Now; wake.Set(); } });
                }
            }
            catch (Exception e) { Log.Warn("office vpn", e.Message); }
            wake.WaitOne(TimeSpan.FromSeconds(Now is State.Connecting or State.UpNoServer || trying == 1 ? 1 : 5));
        }
    }

    // ------------------------------------------------------------------ the tray icon (🟢 🟡 🔴)

    static readonly Dictionary<State, Icon> icons = new();

    public static Icon IconFor(State s)
    {
        lock (icons)
        {
            if (icons.TryGetValue(s, out var i)) return i;
            var k = s switch { State.Up => Lights.Kind.Green, State.UpNoServer or State.Connecting => Lights.Kind.Yellow, State.Down => Lights.Kind.Red, _ => Lights.Kind.None };
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // a calm shield with a lock, and the state's raised light (🟢 🟡 🔴) on its corner — the same light as everywhere in Nova
                using var path = new GraphicsPath();
                path.AddLines(new[] { new Point(13, 1), new Point(25, 5), new Point(24, 15), new Point(13, 26), new Point(2, 15), new Point(1, 5) });
                path.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(0x5B, 0x6C, 0xFF));
                g.FillPath(fill, path);
                using var pen = new Pen(Color.FromArgb(0x14, 0x14, 0x18), 2);
                g.DrawPath(pen, path);
                using var w = new SolidBrush(Color.White);
                g.FillRectangle(w, 9, 12, 9, 7);
                using var arc = new Pen(Color.White, 2.2f);
                g.DrawArc(arc, 10, 6, 7, 9, 180, 180);
                Lights.Draw(g, new RectangleF(15, 15, 17, 17), k, false);
            }
            i = Icon.FromHandle(bmp.GetHicon());
            icons[s] = i;
            return i;
        }
    }
}
