using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🛡 The VPN admin helper. OpenConnect-GUI always runs as administrator, and Windows does not let a normal program (Nova) see or press
/// the buttons of an administrator window. So a copy of Nova does only that, with administrator rights, while the Nova you use stays a
/// normal program (an administrator Nova could not reach your open Outlook).
/// Set up once (Windows asks “Yes” once — no PowerShell, no script): Nova copies itself into C:\Program Files\Nova Enhance VPN Helper
/// (only administrators can change it there) and registers a Windows scheduled task that starts that copy on demand, only for your account,
/// only while you are signed in, only you may start it. Nova asks it through a small request file; it accepts only these jobs:
/// connect / disconnect OpenConnect (window or command line) or FortiClient, and a read-only look (for the 🧪 test). Designed by A.HAMROUNI
/// </summary>
static class VpnHelper
{
    public const string TaskName = "Nova Enhance VPN helper";
    public const int Version = 3;
    static string HelperDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nova Enhance VPN Helper");
    static string HelperExe => Path.Combine(HelperDir, "NovaEnhance.exe");
    public static string Dir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova Enhance", "vpn");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    static int Sch(string args, out string output)
    {
        output = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return -1;
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch (Exception e) { output = e.Message; return -1; }
    }

    static bool TaskExists => Sch($"/query /tn \"{TaskName}\"", out _) == 0;
    static int InstalledVersion { get { try { return int.Parse(File.ReadAllText(Path.Combine(HelperDir, "version.txt")).Trim()); } catch { return 0; } } }

    public static bool Installed => TaskExists && File.Exists(HelperExe) && InstalledVersion == Version;

    public static string State() => !TaskExists ? "not set up" : Installed ? $"set up (v{Version})" : "set up, but an older version — press Set up again";

    static int Elevated(string args, int ms)
    {
        using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
        if (p == null) return -1;
        if (!p.WaitForExit(ms)) return -2;
        return p.ExitCode;
    }

    static string Arg(string s) => "\"" + (s ?? "").Replace("\"", "") + "\"";

    /// <summary>One “Yes” from Windows: Nova itself (started with administrator rights) copies itself into Program Files and registers the task.</summary>
    public static string Install()
    {
        try
        {
            var code = Elevated($"--vpn-setup {Arg(Environment.UserDomainName + "\\" + Environment.UserName)} {Arg(Dir)}", 90000);
            if (code != 0)
            {
                string why = ""; try { why = File.ReadAllText(Path.Combine(Dir, "setup.txt")).Trim(); } catch { }
                return code == -2 ? "the set-up took too long" : $"the set-up stopped (code {code}){(why.Length > 0 ? ": " + why : "")}";
            }
        }
        catch (System.ComponentModel.Win32Exception) { return "Windows' question was answered No — nothing was set up"; }
        catch (Exception e) { return e.Message; }
        return Installed ? "" : "the helper was not found after the set-up";
    }

    public static string Remove()
    {
        try { var c = Elevated("--vpn-remove", 60000); return c == 0 ? "" : $"removing stopped (code {c})"; }
        catch (System.ComponentModel.Win32Exception) { return "Windows' question was answered No"; }
        catch (Exception e) { return e.Message; }
    }

    // ------------------------------------------------------------------ run with administrator rights (NovaEnhance.exe --vpn-setup / --vpn-remove)

    public static int SetupElevated(string[] args)
    {
        var i = Array.IndexOf(args, "--vpn-setup");
        var user = i + 1 < args.Length ? args[i + 1] : "";
        var userDir = i + 2 < args.Length ? args[i + 2] : "";
        var report = Path.Combine(Directory.Exists(userDir) ? userDir : Path.GetTempPath(), "setup.txt");
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new Exception("not started as administrator");
            if (!Regex.IsMatch(user, @"^[\w .\-]+\\[\w .\-$]+$")) throw new Exception("bad account name");
            if (!Directory.Exists(userDir) || userDir.Contains('"')) throw new Exception("bad folder");
            var sid = ((SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier))).Value;
            // a running helper (the command line keeps one open) is stopped so the copy can be replaced
            foreach (var p in Process.GetProcessesByName("NovaEnhance"))
                try { if (string.Equals(p.MainModule?.FileName, HelperExe, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } } catch { }
            Directory.CreateDirectory(HelperDir);
            Directory.CreateDirectory(Path.Combine(HelperDir, "x"));
            File.Copy(Environment.ProcessPath!, HelperExe, true);
            File.WriteAllText(Path.Combine(HelperDir, "version.txt"), Version.ToString());
            // the helper unpacks its native parts into Program Files (not your Temp folder, which any program could change)
            static string X(string s) => System.Security.SecurityElement.Escape(s);
            var cmdArgs = $"--headless cmd.exe /d /c set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR={Path.Combine(HelperDir, "x")}\" && \"{HelperExe}\" --vpn-helper \"{userDir}\"";
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Nova Enhance: connects / disconnects OpenConnect and FortiClient (they run as administrator). Designed by A.HAMROUNI</Description>
    <SecurityDescriptor>D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;{sid})</SecurityDescriptor>
  </RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <UserId>{X(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Hidden>true</Hidden>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>conhost.exe</Command>
      <Arguments>{X(cmdArgs)}</Arguments>
    </Exec>
  </Actions>
</Task>";
            var xf = Path.Combine(HelperDir, "task.xml");
            File.WriteAllText(xf, xml, Encoding.Unicode);
            if (Sch($"/create /tn \"{TaskName}\" /xml \"{xf}\" /f", out var o) != 0) throw new Exception("schtasks: " + o.Trim());
            try { File.Delete(xf); } catch { }
            try { File.WriteAllText(report, $"{DateTime.Now:g} ✓ set up v{Version}"); } catch { }
            return 0;
        }
        catch (Exception e)
        {
            try { File.WriteAllText(report, $"{DateTime.Now:g} ✗ {e.Message}"); } catch { }
            return 5;
        }
    }

    public static int RemoveElevated()
    {
        Sch($"/delete /tn \"{TaskName}\" /f", out _);
        foreach (var p in Process.GetProcessesByName("NovaEnhance"))
            try { if (string.Equals(p.MainModule?.FileName, HelperExe, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { }
        try { Directory.Delete(HelperDir, true); } catch { }
        return 0;
    }

    // ------------------------------------------------------------------ asking the helper

    static readonly object gate = new();
    static readonly string[] Jobs = { "oc-connect", "oc-disconnect", "forti-connect", "forti-disconnect", "cli-connect", "cli-disconnect", "look" };

    /// <summary>Asks the helper for one job and waits for its answer: "" = done, otherwise what went wrong (or what it saw, for "look").</summary>
    public static string Run(string action, string profile, int seconds = 45, params string[] extra)
    {
        if (!Jobs.Contains(action)) return "unknown job";
        lock (gate)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            var res = Path.Combine(Dir, "res.txt");
            try { File.Delete(res); } catch { }
            File.WriteAllText(Path.Combine(Dir, "req.txt"), id + "\n" + action + "\n" + (profile ?? "") + "\n" + string.Join("\n", extra.Select(x => (x ?? "").Replace("\n", " "))) + "\n");
            if (Sch($"/run /tn \"{TaskName}\"", out var o) != 0) return "the VPN helper could not be started: " + o.Trim();
            var until = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < until)
            {
                Thread.Sleep(700);
                try
                {
                    if (!File.Exists(res)) continue;
                    var t = File.ReadAllText(res).Replace("\r", "");
                    var nl = t.IndexOf('\n');
                    if (nl < 0 || t[..nl].Trim() != id) continue;
                    var body = t[(nl + 1)..].Trim();
                    return body == "ok" ? "" : body.StartsWith("ok\n") ? body[3..] : body;
                }
                catch { }
            }
            return "the VPN helper did not answer in time";
        }
    }

    // ------------------------------------------------------------------ the helper itself (NovaEnhance.exe --vpn-helper, administrator rights)

    public static int Serve(string[] args)
    {
        var i = Array.IndexOf(args, "--vpn-helper");
        var dir = i + 1 < args.Length ? args[i + 1] : "";
        if (!Directory.Exists(dir)) return 2;
        var req = Path.Combine(dir, "req.txt");
        var res = Path.Combine(dir, "res.txt");
        string id = "";
        void Answer(string r) { try { File.WriteAllText(res, id + "\n" + (r.Length == 0 ? "ok" : r)); } catch { } }
        try
        {
            if (!File.Exists(req) || (DateTime.Now - File.GetLastWriteTime(req)).TotalSeconds > 90) return 0;
            var l = File.ReadAllText(req).Replace("\r", "").Split('\n');
            File.Delete(req);
            string At(int k) => k < l.Length ? l[k].Trim() : "";
            id = At(0);
            if (!Regex.IsMatch(id, "^[0-9a-f]{12}$")) return 0;
            var act = At(1); var prof = At(2);
            if (!Jobs.Contains(act)) { Answer("unknown job"); return 0; }
            if (!Regex.IsMatch(prof, @"^[\w .\-]{0,64}$")) { Answer("bad server name"); return 0; }
            switch (act)
            {
                case "oc-connect":
                {
                    var exe = OfficeVpn.Oc();
                    Answer(exe == null ? "OpenConnect-GUI is not installed" : OfficeVpn.OcConnectWith(exe, prof.Length > 0 ? prof : "AHMED-MZA"));
                    break;
                }
                case "oc-disconnect": OfficeVpn.GuiButton(OfficeVpn.OcProcs, "Disconnect"); Answer(""); break;
                case "forti-connect":
                {
                    if (OfficeVpn.Pids(OfficeVpn.FortiProcs).Count == 0 && OfficeVpn.Gui() is { } g) { Process.Start(new ProcessStartInfo(g) { UseShellExecute = true }); Thread.Sleep(6000); }
                    OfficeVpn.Reveal(OfficeVpn.FortiProcs);
                    Answer(OfficeVpn.GuiConnect());
                    break;
                }
                case "forti-disconnect": OfficeVpn.GuiButton(OfficeVpn.FortiProcs, "Disconnect"); Answer(""); break;
                case "cli-disconnect":
                    foreach (var p in Process.GetProcessesByName("openconnect")) try { p.Kill(); } catch { }
                    Answer("");
                    break;
                case "cli-connect": return Cli(At(3), At(4), At(5), At(6), At(7), dir, Answer);
                case "look": Answer("ok\n" + Look()); break;
            }
        }
        catch (Exception e) { if (id.Length > 0) Answer("helper: " + e.Message); }
        return 0;
    }

    static readonly object logGate = new();

    /// <summary>⌨ openconnect.exe (only from Program Files), every value checked; it keeps running while connected and this helper waits with it.</summary>
    static int Cli(string gw, string port, string user, string pin, string blob, string dir, Action<string> answer)
    {
        var exe = OfficeVpn.Cli();
        if (exe == null) { answer("openconnect.exe is not in Program Files\\OpenConnect-GUI"); return 0; }
        if (!Regex.IsMatch(gw, @"^[A-Za-z0-9.\-]{1,253}$") || !Regex.IsMatch(port, @"^\d{1,5}$") || !Regex.IsMatch(user, @"^[\w.@\-]{1,64}$")
            || !Regex.IsMatch(pin, @"^(pin-sha256:[A-Za-z0-9+/=]{20,90})?$") || !Regex.IsMatch(blob, @"^[A-Za-z0-9+/=]+$")) { answer("bad connection values"); return 0; }
        if (Process.GetProcessesByName("openconnect").Any()) { answer(""); return 0; }
        string pw;
        try { pw = Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(blob), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)); }
        catch { answer("the password could not be read (another Windows account?)"); return 0; }
        var a = $"--protocol=fortinet --user={user} --passwd-on-stdin --non-inter --no-dtls --reconnect-timeout 300 --interface \"Nova VPN\"";
        if (pin.Length > 0) a += " --servercert " + pin;
        var vs = new[] { "vpnc-script-win.js", "vpnc-script.js" }.Select(n => Path.Combine(Path.GetDirectoryName(exe)!, n)).FirstOrDefault(File.Exists);
        if (vs != null) a += $" --script \"{vs}\"";
        a += $" {gw}:{port}";
        var log = Path.Combine(dir, "openconnect.log");
        File.WriteAllText(log, $"{DateTime.Now:HH:mm:ss} openconnect {a}\r\n");
        var p = new Process { StartInfo = new ProcessStartInfo(exe, a) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe)! } };
        DataReceivedEventHandler h = (_, e) => { if (e.Data == null) return; lock (logGate) try { File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} {e.Data}\r\n"); } catch { } };
        p.OutputDataReceived += h; p.ErrorDataReceived += h;
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        p.StandardInput.WriteLine(pw); p.StandardInput.Close();
        pw = null;
        if (p.WaitForExit(12000))
        {
            string tail;
            lock (logGate) tail = string.Join(" / ", File.ReadAllLines(log).Where(x => !Regex.IsMatch(x, "cookie|passw|SVPN", RegexOptions.IgnoreCase)).TakeLast(6));
            answer("openconnect stopped: " + tail);
            return 0;
        }
        answer("");
        p.WaitForExit();
        return 0;
    }

    static string Look()
    {
        var sb = new StringBuilder();
        foreach (var (procs, title) in new[] { (OfficeVpn.OcProcs, "OpenConnect"), (OfficeVpn.FortiProcs, "FortiClient") })
        {
            if (OfficeVpn.Pids(procs).Count == 0) { sb.Append(title).Append(": not running\n"); continue; }
            var w = OfficeVpn.AppWindow(procs, 2);
            if (w == null) { sb.Append(title).Append(": running, window hidden in the tray\n"); continue; }
            var buttons = OfficeVpn.All(w, System.Windows.Automation.ControlType.Button).Select(OfficeVpn.NameOf).Where(x => x.Length > 0).Distinct().Take(12).ToList();
            var combo = OfficeVpn.All(w, System.Windows.Automation.ControlType.ComboBox).FirstOrDefault();
            var cur = combo == null ? "" : OfficeVpn.NameOf(combo);
            try { if (combo != null && combo.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var vp)) cur = ((System.Windows.Automation.ValuePattern)vp).Current.Value ?? cur; } catch { }
            var state = buttons.Any(b => b.Equals("Disconnect", StringComparison.OrdinalIgnoreCase)) ? "connected" : buttons.Any(b => b.Equals("Connect", StringComparison.OrdinalIgnoreCase)) ? "Connect button ready" : "no Connect button";
            sb.Append($"{title} window “{OfficeVpn.NameOf(w)}” (seen with administrator rights): {state}{(cur.Length > 0 ? $" · server “{cur}”" : "")} · buttons: {string.Join(", ", buttons)}\n");
        }
        return sb.ToString().Trim();
    }
}
