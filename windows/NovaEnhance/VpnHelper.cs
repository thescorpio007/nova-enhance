using System.Diagnostics;
using System.Text;

namespace NovaEnhance;

/// <summary>
/// 🛡 The VPN admin helper. OpenConnect-GUI always runs as administrator, and Windows does not let a normal program (Nova) see or press
/// the buttons of an administrator window. So a small helper does only that, with administrator rights, while Nova itself stays a normal
/// program (an administrator Nova could not reach your open Outlook).
/// Set up once (Windows asks “Yes” once): a script in C:\Program Files\Nova Enhance VPN Helper (only administrators can change it) and a
/// Windows scheduled task that runs it on demand, only for your account, only while you are signed in. Nova asks it through a small request
/// file; it accepts only these jobs: connect / disconnect OpenConnect or FortiClient, and a read-only look (for the 🧪 test). It never sees
/// a password (OpenConnect and FortiClient keep theirs). Designed by A.HAMROUNI
/// </summary>
static class VpnHelper
{
    public const string TaskName = "Nova Enhance VPN helper";
    public const int Version = 1;
    static string HelperDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nova Enhance VPN Helper");
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

    /// <summary>The task exists and the installed script is this version.</summary>
    public static bool Installed
    {
        get
        {
            if (Sch($"/query /tn \"{TaskName}\"", out _) != 0) return false;
            try { return File.ReadAllText(Path.Combine(HelperDir, "helper.ps1")).Contains("# nova-vpn-helper v" + Version + "\n"); } catch { return false; }
        }
    }

    public static string State() =>
        Sch($"/query /tn \"{TaskName}\"", out _) != 0 ? "not set up" : Installed ? $"set up (v{Version})" : "set up, but an older version — press Set up again";

    /// <summary>One “Yes” from Windows: copies the script into Program Files and registers the task for this account (run with highest rights,
    /// only while signed in, only you may start it).</summary>
    public static string Install()
    {
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var setup = SetupScript.Replace("__HELPER__", Script.Replace("'", "''")).Replace("__USER__", user.Replace("'", "''"))
            .Replace("__DIR__", Dir.Replace("'", "''")).Replace("__TASK__", TaskName);
        // The set-up is written to a file and the elevated PowerShell runs it only if its SHA-256 matches (read once into memory, checked, run —
        // a program changing the file in between cannot get administrator rights).
        var bytes = Encoding.UTF8.GetBytes(setup);
        var file = Path.Combine(Dir, "setup.ps1");
        File.WriteAllBytes(file, bytes);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var boot = $"$b=[IO.File]::ReadAllBytes('{file.Replace("'", "''")}'); $h=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-',''); if($h -ne '{hash}'){{exit 9}}; Invoke-Expression ([Text.Encoding]::UTF8.GetString($b))";
        var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(boot));
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + enc) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(60000);
            try { File.Delete(file); } catch { }
            if (p != null && p.ExitCode == 9) return "the set-up file was changed before it ran — nothing was set up";
            if (p != null && p.ExitCode != 0) return $"the set-up stopped (code {p.ExitCode})";
        }
        catch (System.ComponentModel.Win32Exception) { return "Windows' question was answered No — nothing was set up"; }
        catch (Exception e) { return e.Message; }
        return Installed ? "" : "the helper was not found after the set-up";
    }

    public static string Remove()
    {
        var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            $"schtasks /delete /tn '{TaskName}' /f; Remove-Item -Recurse -Force '{HelperDir.Replace("'", "''")}' -ErrorAction SilentlyContinue"));
        try { using var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -EncodedCommand " + enc) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }); p?.WaitForExit(30000); }
        catch (Exception e) { return e.Message; }
        return "";
    }

    static readonly object gate = new();

    /// <summary>Asks the helper for one job and waits for its answer: "" = done, otherwise what went wrong (or what it saw, for "look").</summary>
    public static string Run(string action, string profile, int seconds = 45)
    {
        if (action is not ("oc-connect" or "oc-disconnect" or "forti-connect" or "forti-disconnect" or "look")) return "unknown job";
        lock (gate)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            var res = Path.Combine(Dir, "res.txt");
            try { File.Delete(res); } catch { }
            File.WriteAllText(Path.Combine(Dir, "req.txt"), id + "\n" + action + "\n" + (profile ?? "") + "\n");
            if (Sch($"/run /tn \"{TaskName}\"", out var o) != 0) return "the VPN helper could not be started: " + o.Trim();
            var until = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < until)
            {
                Thread.Sleep(700);
                try
                {
                    if (!File.Exists(res)) continue;
                    var t = File.ReadAllText(res);
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

    // ------------------------------------------------------------------ the set-up (run once with “Yes”)

    const string SetupScript = @"
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:ProgramFiles 'Nova Enhance VPN Helper'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Set-Content -Path (Join-Path $dir 'helper.ps1') -Value '__HELPER__' -Encoding UTF8
$user = '__USER__'
$sid = (New-Object System.Security.Principal.NTAccount($user)).Translate([System.Security.Principal.SecurityIdentifier]).Value
$svc = New-Object -ComObject Schedule.Service
$svc.Connect()
$def = $svc.NewTask(0)
$def.RegistrationInfo.Description = 'Nova Enhance: presses Connect / Disconnect in OpenConnect and FortiClient (they run as administrator). Designed by A.HAMROUNI'
$def.Principal.UserId = $user
$def.Principal.LogonType = 3
$def.Principal.RunLevel = 1
$def.Settings.ExecutionTimeLimit = 'PT2M'
$def.Settings.MultipleInstances = 2
$def.Settings.DisallowStartIfOnBatteries = $false
$def.Settings.StopIfGoingOnBatteries = $false
$def.Settings.Hidden = $true
$a = $def.Actions.Create(0)
$a.Path = 'powershell.exe'
$a.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""' + (Join-Path $dir 'helper.ps1') + '"" -Dir ""__DIR__""'
$sddl = 'D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;' + $sid + ')'
$svc.GetFolder('\').RegisterTaskDefinition('__TASK__', $def, 6, $user, $null, 3, $sddl) | Out-Null
";

    // ------------------------------------------------------------------ the helper script (installed in Program Files)

    const string Script = @"# nova-vpn-helper v1
param([string]$Dir)
$ErrorActionPreference = 'Stop'
$res = Join-Path $Dir 'res.txt'
$id = ''
try {
  $req = Join-Path $Dir 'req.txt'
  if (-not (Test-Path $req)) { exit 0 }
  if (((Get-Date) - (Get-Item $req).LastWriteTime).TotalSeconds -gt 90) { exit 0 }
  $l = (Get-Content $req -Raw) -split ""`n""
  $id = $l[0].Trim(); $act = $l[1].Trim(); $prof = $l[2].Trim()
  if ($id -notmatch '^[0-9a-f]{12}$') { exit 0 }
  if ($act -notin @('oc-connect','oc-disconnect','forti-connect','forti-disconnect','look')) { throw 'unknown job' }
  if ($prof -notmatch '^[\w .\-]{0,64}$') { throw 'bad server name' }
  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
  Add-Type -ReferencedAssemblies UIAutomationClient, UIAutomationTypes, WindowsBase -TypeDefinition @'
using System; using System.Collections.Generic; using System.Diagnostics; using System.Linq; using System.Text; using System.Threading;
using System.Runtime.InteropServices; using System.Windows.Automation;
public static class NovaVpn {
  [DllImport(""user32.dll"")] static extern bool ShowWindow(IntPtr h, int c);
  [DllImport(""user32.dll"")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport(""user32.dll"")] static extern bool EnumWindows(EP f, IntPtr l);
  [DllImport(""user32.dll"")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport(""user32.dll"", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport(""user32.dll"")] static extern IntPtr GetWindow(IntPtr h, uint c);
  delegate bool EP(IntPtr h, IntPtr l);
  static HashSet<int> Pids(string pre) {
    var s = new HashSet<int>();
    foreach (var p in Process.GetProcesses()) { try { if (p.ProcessName.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) s.Add(p.Id); } catch { } }
    return s;
  }
  public static void Reveal(string pre, string title) {
    var ids = Pids(pre);
    EnumWindows(delegate (IntPtr h, IntPtr l) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (!ids.Contains((int)pid) || GetWindow(h, 4) != IntPtr.Zero) return true;
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
      if (sb.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) return true;
      ShowWindow(h, 9); SetForegroundWindow(h); return true;
    }, IntPtr.Zero);
  }
  static string N(AutomationElement e) { try { return (e.Current.Name ?? """").Trim(); } catch { return """"; } }
  static List<AutomationElement> All(AutomationElement w, ControlType t) {
    return w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, t)).Cast<AutomationElement>().ToList();
  }
  static AutomationElement Btn(AutomationElement w, string name) {
    return All(w, ControlType.Button).FirstOrDefault(b => N(b).Replace(""&"", """").Equals(name, StringComparison.OrdinalIgnoreCase));
  }
  public static AutomationElement Win(string pre, string title, int seconds) {
    for (int i = 0; i < seconds; i++) {
      var ids = Pids(pre); AutomationElement first = null;
      foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window))) {
        try { if (!ids.Contains(w.Current.ProcessId) || N(w).IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) continue; } catch { continue; }
        if (first == null) first = w;
        if (Btn(w, ""Connect"") != null || Btn(w, ""Disconnect"") != null) return w;
      }
      if (first != null && i >= 3) return first;
      Thread.Sleep(1000);
    }
    return null;
  }
  static string Press(AutomationElement b) {
    object p; if (b.TryGetCurrentPattern(InvokePattern.Pattern, out p)) { ((InvokePattern)p).Invoke(); return """"; }
    return ""the button could not be pressed"";
  }
  public static string Pick(AutomationElement w, string prof) {
    if (prof.Length == 0) return """";
    var combo = All(w, ControlType.ComboBox).FirstOrDefault();
    if (combo == null) return """";
    string cur = N(combo); object vp;
    try { if (combo.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) cur = ((ValuePattern)vp).Current.Value ?? cur; } catch { }
    if (cur.Trim().Equals(prof, StringComparison.OrdinalIgnoreCase)) return """";
    object ec; if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out ec)) { ((ExpandCollapsePattern)ec).Expand(); Thread.Sleep(700); }
    var item = combo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().FirstOrDefault(x => N(x).Equals(prof, StringComparison.OrdinalIgnoreCase));
    if (item == null) {
      // the list opens as its own window in some programs
      item = AutomationElement.RootElement.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().FirstOrDefault(x => N(x).Equals(prof, StringComparison.OrdinalIgnoreCase));
    }
    if (item == null) return ""no server named \u201C"" + prof + ""\u201D in the list"";
    object si; if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out si)) ((SelectionItemPattern)si).Select();
    if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out ec)) ((ExpandCollapsePattern)ec).Collapse();
    return """";
  }
  public static string Do(string pre, string title, string exe, string what, string prof) {
    if (Pids(pre).Count == 0) {
      if (what == ""Disconnect"") return """";
      if (exe != null && System.IO.File.Exists(exe)) { Process.Start(exe); Thread.Sleep(5000); }
    }
    Reveal(pre, title);
    var w = Win(pre, title, 15);
    if (w == null) return ""the "" + title + "" window did not open"";
    if (what == ""Connect"") {
      if (Btn(w, ""Disconnect"") != null) return """";
      var r = Pick(w, prof); if (r.Length > 0) return r;
    }
    var b = Btn(w, what);
    if (b == null) {
      if (what == ""Disconnect"") return """";
      return ""no \u201C"" + what + ""\u201D button in the "" + title + "" window (buttons: "" + string.Join("", "", All(w, ControlType.Button).Select(N).Where(x => x.Length > 0).Distinct().Take(12)) + "")"";
    }
    return Press(b);
  }
  public static string Look() {
    var sb = new StringBuilder();
    foreach (var t in new[] { new[] { ""openconnect"", ""OpenConnect"" }, new[] { ""Forti"", ""FortiClient"" } }) {
      var ids = Pids(t[0]);
      if (ids.Count == 0) { sb.Append(t[1] + "": not running\n""); continue; }
      var w = Win(t[0], t[1], 2);
      if (w == null) { sb.Append(t[1] + "": running, window hidden in the tray\n""); continue; }
      var combo = All(w, ControlType.ComboBox).FirstOrDefault();
      string cur = combo == null ? """" : N(combo); object vp;
      try { if (combo != null && combo.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) cur = ((ValuePattern)vp).Current.Value ?? cur; } catch { }
      sb.Append(t[1] + "" window \u201C"" + N(w) + ""\u201D (seen with administrator rights): "" + (Btn(w, ""Disconnect"") != null ? ""connected"" : Btn(w, ""Connect"") != null ? ""Connect button ready"" : ""no Connect button"")
        + (cur.Length > 0 ? "" \u00B7 server \u201C"" + cur + ""\u201D"" : """") + "" \u00B7 buttons: "" + string.Join("", "", All(w, ControlType.Button).Select(N).Where(x => x.Length > 0).Distinct().Take(12)) + ""\n"");
    }
    return sb.ToString().Trim();
  }
}
'@
  $oc = @(""$env:ProgramFiles\OpenConnect-GUI\openconnect-gui.exe"", ""${env:ProgramFiles(x86)}\OpenConnect-GUI\openconnect-gui.exe"") | Where-Object { Test-Path $_ } | Select-Object -First 1
  $fc = @(""$env:ProgramFiles\Fortinet\FortiClient\FortiClient.exe"", ""${env:ProgramFiles(x86)}\Fortinet\FortiClient\FortiClient.exe"") | Where-Object { Test-Path $_ } | Select-Object -First 1
  switch ($act) {
    'oc-connect'       { $r = [NovaVpn]::Do('openconnect', 'OpenConnect', $oc, 'Connect', $prof) }
    'oc-disconnect'    { $r = [NovaVpn]::Do('openconnect', 'OpenConnect', $oc, 'Disconnect', '') }
    'forti-connect'    { $r = [NovaVpn]::Do('Forti', 'FortiClient', $fc, 'Connect', '') }
    'forti-disconnect' { $r = [NovaVpn]::Do('Forti', 'FortiClient', $fc, 'Disconnect', '') }
    'look'             { $r = ""ok`n"" + [NovaVpn]::Look() }
  }
  if ($r -eq '') { $r = 'ok' }
  Set-Content -Path $res -Value ($id + ""`n"" + $r) -Encoding UTF8
} catch {
  if ($id -ne '') { Set-Content -Path $res -Value ($id + ""`n"" + 'helper: ' + $_.Exception.Message) -Encoding UTF8 }
}
";
}
