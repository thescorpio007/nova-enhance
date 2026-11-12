using System.Diagnostics;

namespace NovaEnhance;

/// <summary>
/// 🛡 Anti-crash. (1) The Guardian: a tiny second copy of Nova that watches the first one — if Nova closes unexpectedly or freezes,
/// it restarts it (at most 3 times in 10 minutes) and reopens the window you had. (2) Safe background jobs: a failing job stops
/// alone and restarts a minute later. (5) Safe mode after two failed starts. (8) A crash report in the Error log and Activity history.
/// Designed by A.HAMROUNI
/// </summary>
static class Guard
{
    static string F(string n) => Path.Combine(Paths.Cache, n);
    static string Beat => F("heartbeat.txt");
    static string CleanFile => F("clean-exit.txt");
    static string BootFile => F("startup.txt");
    static string CrashFile => F("crash.txt");
    static string LastScreen => F("last-screen.txt");

    public static bool On { get => Store.Bool("win_guard_on", true); set => Store.SetBool("win_guard_on", value); }
    public static bool SafeMode;
    public static string SafeReason = "";
    public static readonly Dictionary<string, int> Restarts = new();

    // ------------------------------------------------------------------ (2) safe background jobs

    /// <summary>Runs a background job's loop; if it fails, only this job stops, is logged, and starts again after a minute.</summary>
    public static void Run(string job, Action loop)
    {
        while (true)
        {
            try { loop(); return; }
            catch (ThreadInterruptedException) { return; }
            catch (Exception e)
            {
                lock (Restarts) Restarts[job] = Restarts.TryGetValue(job, out var n) ? n + 1 : 1;
                Log.Error("🛡 " + job, e);
                Thread.Sleep(TimeSpan.FromMinutes(1));
            }
        }
    }

    /// <summary>A one-shot background work: an error is logged instead of closing Nova.</summary>
    public static ThreadStart Safe(string job, Action work) => () => { try { work(); } catch (Exception e) { Log.Error("🛡 " + job, e); } };

    /// <summary>(3) A time limit for a call to Outlook / Office: if it does not answer, Nova carries on instead of waiting forever.</summary>
    public static async Task<T> Limit<T>(Task<T> work, int seconds, string who)
    {
        try { return await work.WaitAsync(TimeSpan.FromSeconds(seconds)); }
        catch (TimeoutException)
        {
            Log.Warn("🛡 " + who, $"no answer within {seconds} s");
            throw new InvalidOperationException($"{who} did not answer within {seconds} s — it may be busy (a big archive, a password or security prompt). Try again in a moment.");
        }
    }

    public static string JobsLine()
    {
        lock (Restarts) return Restarts.Count == 0 ? "" : string.Join(" · ", Restarts.Select(x => $"⚠ {x.Key} restarted {x.Value}×"));
    }

    // ------------------------------------------------------------------ (5) safe mode: two starts that never finished

    /// <summary>Called first at start: counts starts that did not get through. Returns true → start in safe mode.</summary>
    public static bool BeginStart()
    {
        try
        {
            var parts = File.Exists(BootFile) ? File.ReadAllText(BootFile).Split('|') : Array.Empty<string>();
            var n = parts.Length > 0 && int.TryParse(parts[0], out var k) ? k : 0;
            var stage = parts.Length > 1 ? parts[1] : "";
            File.WriteAllText(BootFile, $"{n + 1}|starting");
            if (n >= 2) { SafeMode = true; SafeReason = stage.Length > 0 ? $"the last two starts stopped during “{stage}”" : "the last two starts did not finish"; }
        }
        catch { }
        return SafeMode;
    }

    /// <summary>What Nova is starting now (shown in safe mode if it crashed there).</summary>
    public static void Stage(string stage)
    {
        try { var n = File.Exists(BootFile) ? File.ReadAllText(BootFile).Split('|')[0] : "1"; File.WriteAllText(BootFile, $"{n}|{stage}"); } catch { }
    }

    /// <summary>Nova ran a minute without a problem: the start is good.</summary>
    public static void StartOk() { try { File.WriteAllText(BootFile, "0|ok"); } catch { } }

    // ------------------------------------------------------------------ (1) heartbeat · clean exit · last window

    public static void Heartbeat() { try { File.WriteAllText(Beat, DateTime.UtcNow.Ticks.ToString()); } catch { } }

    /// <summary>A normal exit (Exit, update, Windows shutting down): the Guardian must not restart Nova.</summary>
    public static void CleanExit() { try { File.WriteAllText(CleanFile, DateTime.UtcNow.Ticks.ToString()); } catch { } }

    public static void Screen(string name) { UiWatch.Step("opening " + name); try { File.WriteAllText(LastScreen, name + "|" + DateTime.UtcNow.Ticks); } catch { } }

    /// <summary>The window to reopen after a restart — not one opened just before the crash (it may be what crashed).</summary>
    public static string LastScreenName()
    {
        try
        {
            if (!File.Exists(LastScreen)) return "";
            var p = File.ReadAllText(LastScreen).Trim().Split('|');
            File.Delete(LastScreen);
            if (p.Length > 1 && long.TryParse(p[1], out var t) && DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc) < TimeSpan.FromSeconds(120)) return "";
            return p[0];
        }
        catch { return ""; }
    }

    /// <summary>Starts the Guardian (once) for this Nova.</summary>
    public static void StartGuardian()
    {
        if (!On) return;
        try
        {
            if (Mutex.TryOpenExisting("NovaEnhanceWindows-guardian", out var m)) { m.Dispose(); return; }
            try { File.Delete(CleanFile); } catch { }
            Heartbeat();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--guardian {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e) { Log.Warn("guardian", "could not start: " + e.Message); }
    }

    /// <summary>The Guardian process: watches Nova, restarts it after a crash or a freeze.</summary>
    public static int Watch(string[] args)
    {
        using var me = new Mutex(true, "NovaEnhanceWindows-guardian", out bool first);
        if (!first) return 0;
        var i = Array.IndexOf(args, "--guardian");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return 0;
        Process nova;
        try { nova = Process.GetProcessById(pid); } catch { return 0; }
        var restarts = new List<DateTime>();
        int frozenChecks = 0;
        while (true)
        {
            Thread.Sleep(5000);
            try
            {
                nova.Refresh();
                if (nova.HasExited)
                {
                    // A normal exit leaves the clean-exit mark: the Guardian stops too.
                    if (File.Exists(CleanFile)) return 0;
                    Crash("Nova closed unexpectedly" + (TryExit(nova) is { } code ? $" (exit code {code})" : ""));
                    restarts.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(10));
                    if (restarts.Count >= 3)
                    {
                        File.AppendAllText(CrashFile, "\r\nThe Guardian stopped restarting Nova: 3 restarts in 10 minutes.\r\n");
                        MessageBox.Show("Nova Enhance closed 3 times in 10 minutes, so it was not restarted again.\n\nStart it from the Start menu — it will open in 🛡 safe mode if needed. Settings → Error log has the details.", "Nova Enhance · Guardian", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return 0;
                    }
                    restarts.Add(DateTime.Now);
                    try { File.Delete(CleanFile); } catch { }
                    Heartbeat();
                    nova = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restored") { UseShellExecute = false })!;
                    frozenChecks = 0;
                    Thread.Sleep(30000);   // give it time to start
                    continue;
                }
                // Frozen: no heartbeat for 90 s and the window does not respond.
                long.TryParse(File.Exists(Beat) ? File.ReadAllText(Beat) : "0", out var ticks);
                var silent = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
                if (silent > TimeSpan.FromSeconds(90) && !nova.Responding) frozenChecks++; else frozenChecks = 0;
                if (frozenChecks >= 3)
                {
                    Crash($"Nova was frozen for {(int)silent.TotalSeconds} s — restarted by the Guardian");
                    try { nova.Kill(true); } catch { try { nova.Kill(); } catch (Exception e) { Crash("could not close the frozen Nova: " + e.Message); } }
                    try { nova.WaitForExit(10000); } catch { }
                    frozenChecks = 0;
                }
            }
            catch { }
        }
    }

    static int? TryExit(Process p) { try { return p.ExitCode; } catch { return null; } }

    static void Crash(string what)
    {
        try { File.AppendAllText(CrashFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what}\r\n"); } catch { }
    }

    /// <summary>(8) After a restart: the crash goes to the Error log and the Activity history (with the last tasks before it).</summary>
    public static string TakeCrashReport()
    {
        try
        {
            if (!File.Exists(CrashFile)) return "";
            var text = File.ReadAllText(CrashFile).Trim();
            File.Delete(CrashFile);
            if (text.Length == 0) return "";
            var last = Activity.Read(1).TakeLast(15).Select(o => $"{o["start"]} · {o["feature"]} · {o["title"]} · {o["ms"]} ms · {o["result"]}");
            var lastCrash = Path.Combine(Paths.Logs, "last-crash.txt");
            var trace = File.Exists(lastCrash) && DateTime.Now - File.GetLastWriteTime(lastCrash) < TimeSpan.FromMinutes(15) ? File.ReadAllText(lastCrash) : "";
            Log.Error("🛡 guardian", new Exception(text + "\r\n\r\nLast tasks before it:\r\n" + string.Join("\r\n", last) + (trace.Length > 0 ? "\r\n\r\nTechnical trace:\r\n" + trace : "")));
            using (var t = Activity.Begin("🛡 crash / restart", text)) { t.Note("last tasks", string.Join("\n", last)); if (trace.Length > 0) t.Note("trace", trace); t.Fail(text); }
            return text;
        }
        catch { return ""; }
    }

    /// <summary>The last words of a crashing Nova (an error no one caught): kept for the report after the restart.</summary>
    public static void LastWords(object ex)
    {
        try { File.WriteAllText(Path.Combine(Paths.Logs, "last-crash.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{ex}"); } catch { }
        try { File.AppendAllText(CrashFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  error: {(ex as Exception)?.Message ?? ex?.ToString()}\r\n"); } catch { }
    }
}

/// <summary>
/// 🧊 Freeze recorder: notes what Nova's window thread was doing whenever it stops responding for more than 5 seconds
/// (Documents\Nova Enhance\Logs\ui-freeze.txt — plain text, to send to Claude even when Settings can't open).
/// </summary>
static class UiWatch
{
    public static volatile string Doing = "";
    static long beat = DateTime.UtcNow.Ticks;
    static string FilePath => Path.Combine(Paths.Logs, "ui-freeze.txt");

    public static void Step(string what) => Doing = what;

    public static void Start()
    {
        var t = new System.Windows.Forms.Timer { Interval = 500 };
        t.Tick += (_, _) => Interlocked.Exchange(ref beat, DateTime.UtcNow.Ticks);
        t.Start();
        new Thread(() =>
        {
            DateTime? since = null; string what = ""; bool wrote30 = false;
            while (true)
            {
                Thread.Sleep(1000);
                var silent = DateTime.UtcNow - new DateTime(Interlocked.Read(ref beat), DateTimeKind.Utc);
                if (silent > TimeSpan.FromSeconds(5))
                {
                    if (since == null) { since = DateTime.Now - silent; what = Doing; wrote30 = false; }
                    if (!wrote30 && silent > TimeSpan.FromSeconds(30)) { wrote30 = true; Write($"{since:yyyy-MM-dd HH:mm:ss}  STILL FROZEN after {(int)silent.TotalSeconds} s · while: {what}"); }
                }
                else if (since != null)
                {
                    var s = (int)(DateTime.Now - since.Value).TotalSeconds;
                    Write($"{since:yyyy-MM-dd HH:mm:ss}  frozen {s} s · while: {what}");
                    Log.Warn("🧊 window froze", $"{s} s while: {what}");
                    since = null;
                }
            }
        }) { IsBackground = true, Name = "Nova freeze recorder" }.Start();
    }

    static void Write(string line) { try { File.AppendAllText(FilePath, line + " · build " + Program.Build + "\r\n"); } catch { } }
}
