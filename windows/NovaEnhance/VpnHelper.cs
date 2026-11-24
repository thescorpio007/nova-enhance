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
    public const int Version = 4;
    static string HelperDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nova Enhance VPN Helper");
    static string HelperExe => Path.Combine(HelperDir, "NovaEnhance.exe");
    /// <summary>🌐 Nova's own VPN engine (OpenConnect), copied here by the set-up: only administrators can change it.</summary>
    public static string EngineDir => Path.Combine(HelperDir, "openconnect");
    public static string EngineExe => Path.Combine(EngineDir, "openconnect.exe");
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
            // 🌐 the VPN engine shipped with Nova (official OpenConnect release) → Program Files, beside the helper
            var eng = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "openconnect");
            if (Directory.Exists(eng))
            {
                foreach (var p in Process.GetProcessesByName("openconnect"))
                    try { if (string.Equals(p.MainModule?.FileName, EngineExe, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } } catch { }
                Directory.CreateDirectory(EngineDir);
                foreach (var f in Directory.GetFiles(EngineDir)) try { File.Delete(f); } catch { }
                foreach (var f in Directory.GetFiles(eng)) File.Copy(f, Path.Combine(EngineDir, Path.GetFileName(f)), true);
            }
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
                case "cli-connect": return Cli(At(3), dir, Answer);
                case "look": Answer("ok\n" + Look()); break;
            }
        }
        catch (Exception e) { if (id.Length > 0) Answer("helper: " + e.Message); }
        return 0;
    }

    static readonly object logGate = new();

    /// <summary>⌨ openconnect.exe (Nova's engine or OpenConnect-GUI's, only from Program Files); every option checked; it keeps running while
    /// connected and this helper waits with it.</summary>
    static int Cli(string b64, string dir, Action<string> answer)
    {
        var exe = OfficeVpn.Cli();
        if (exe == null) { answer("the VPN engine is not installed — press 🛡 Set up the VPN helper again"); return 0; }
        System.Text.Json.Nodes.JsonObject o;
        try { o = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)))!.AsObject(); }
        catch { answer("bad connection values"); return 0; }
        string S(string k) => o[k]?.ToString()?.Trim() ?? "";
        bool Ok(string v, string rx) => Regex.IsMatch(v, rx);
        string Dec(string blob)
        {
            if (blob.Length == 0) return "";
            if (!Ok(blob, @"^[A-Za-z0-9+/=]+$")) throw new Exception("bad secret");
            return Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(blob), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
        var proto = S("proto"); var gw = S("gw"); var port = S("port"); var user = S("user"); var pin = S("pin"); var group = S("group");
        var otp = S("otp"); var iface = S("iface"); var log = S("log"); var proxy = S("proxy");
        int.TryParse(S("rto"), out var rto); rto = Math.Clamp(rto == 0 ? 300 : rto, 10, 3600);
        var dtls = !string.Equals(S("dtls"), "false", StringComparison.OrdinalIgnoreCase);
        if (!OfficeVpn.Protocols.Any(p => p.Id == proto) || !Ok(gw, @"^[A-Za-z0-9.\-]{1,253}$") || !Ok(port, @"^\d{1,5}$") || !Ok(user, @"^[\w.@\\\-]{1,128}$")
            || !Ok(pin, @"^(pin-sha256:[A-Za-z0-9+/=]{20,90})?$") || !Ok(group, @"^[\w .@\-]{0,64}$") || otp is not ("none" or "hotp" or "totp" or "")
            || !Ok(iface, @"^[\w .\-]{1,40}$") || log is not ("normal" or "verbose" or "quiet" or "") || !Ok(proxy, @"^((https?|socks5?)://[\w.\-:@]+)?$"))
        { answer("bad connection values"); return 0; }
        // files the engine reads: must exist; a script it RUNS (as administrator) only from Program Files
        string FileArg(string k) { var f = S(k); if (f.Length == 0) return ""; if (f.Contains('"') || !File.Exists(f)) throw new Exception(k + " file not found: " + f); return f; }
        string ca, ucert, ukey, script;
        try { ca = FileArg("cafile"); ucert = FileArg("ucert"); ukey = FileArg("ukey"); }
        catch (Exception e) { answer(e.Message); return 0; }
        var engDir = Path.GetDirectoryName(exe)!;
        script = S("script");
        var pf = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        if (script.Length > 0 && (!File.Exists(script) || script.Contains('"') || !pf.Any(d => Path.GetFullPath(script).StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))))
        { answer("a custom vpnc script must be inside Program Files (it runs with administrator rights)"); return 0; }
        if (script.Length == 0) script = new[] { "vpnc-script-win.js", "vpnc-script.js" }.Select(n => Path.Combine(engDir, n)).FirstOrDefault(File.Exists) ?? "";
        if (Process.GetProcessesByName("openconnect").Any()) { answer(""); return 0; }
        string pw, otpSecret;
        try { pw = Dec(S("pw")); otpSecret = otp is "hotp" or "totp" ? Dec(S("otpBlob")) : ""; }
        catch { answer("the password could not be read (another Windows account?)"); return 0; }
        if (otpSecret.Contains('"') || otpSecret.Contains(' ')) { answer("bad OTP secret"); return 0; }
        var a = new StringBuilder($"--protocol={proto} --user=\"{user}\" --passwd-on-stdin --non-inter --reconnect-timeout {rto} --interface \"{iface}\"");
        if (!dtls) a.Append(" --no-dtls");
        if (pin.Length > 0) a.Append(" --servercert ").Append(pin);
        if (group.Length > 0) a.Append($" --authgroup=\"{group}\"");
        if (ca.Length > 0) a.Append($" --cafile=\"{ca}\"");
        if (ucert.Length > 0) a.Append($" --certificate=\"{ucert}\"");
        if (ukey.Length > 0) a.Append($" --sslkey=\"{ukey}\"");
        if (otpSecret.Length > 0) a.Append($" --token-mode={otp} --token-secret={otpSecret}");
        if (proxy.Length > 0) a.Append($" --proxy={proxy}");
        if (log == "verbose") a.Append(" -v"); else if (log == "quiet") a.Append(" -q");
        if (script.Length > 0) a.Append($" --script \"{script}\"");
        a.Append($" {gw}:{port}");
        var args = a.ToString();
        var logf = Path.Combine(dir, "openconnect.log");
        var shown = Regex.Replace(args, @"--token-secret=\S+", "--token-secret=••••");
        File.WriteAllText(logf, $"{DateTime.Now:HH:mm:ss} openconnect {shown}\r\n");
        var p = new Process { StartInfo = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = engDir } };
        DataReceivedEventHandler h = (_, e) =>
        {
            if (e.Data == null) return;
            // session codes and passwords never reach the log
            var line = Regex.IsMatch(e.Data, "cookie|passw|SVPN|token-secret", RegexOptions.IgnoreCase) && !e.Data.StartsWith("Password:") ? Regex.Replace(e.Data, @"(=|:)\s*\S+", "$1 ••••") : e.Data;
            lock (logGate) try { File.AppendAllText(logf, $"{DateTime.Now:HH:mm:ss} {line}\r\n"); } catch { }
        };
        p.OutputDataReceived += h; p.ErrorDataReceived += h;
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        p.StandardInput.WriteLine(pw); p.StandardInput.Close();
        pw = null; otpSecret = null;
        if (p.WaitForExit(12000))
        {
            string tail;
            lock (logGate) tail = string.Join(" / ", File.ReadAllLines(logf).Where(x => !Regex.IsMatch(x, "cookie|passw|SVPN", RegexOptions.IgnoreCase)).TakeLast(6));
            var why = OfficeVpn.ReadEngineLog().Reason;
            answer((why.Length > 0 ? why + " — " : "") + "engine stopped: " + tail);
            return 0;
        }
        answer("");
        p.WaitForExit();
        lock (logGate) try { File.AppendAllText(logf, $"{DateTime.Now:HH:mm:ss} — engine stopped (code {p.ExitCode})\r\n"); } catch { }
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
