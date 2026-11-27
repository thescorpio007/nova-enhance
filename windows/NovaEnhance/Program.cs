using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NovaEnhance;

static class Program
{
    public const string Sign = "Designed by A.HAMROUNI";
    public static int Build => Assembly.GetExecutingAssembly().GetName().Version?.Build ?? 0;
    /// <summary>2.0: the build count starts again at 01 (internally the number keeps going up, so updates keep working).</summary>
    public const int Base2 = 77;
    /// <summary>From internal 101 the number is shared with the Android app (one build number for both): shown = internal − 95.</summary>
    public const int BaseShared = 95;
    public static int ShownBuild => Build > 100 ? Build - BaseShared : Build > Base2 ? Build - Base2 : Build;
    public static string Version => Build > Base2 ? "2.0" : "1.0";
    public static string Shown => ShownOf(Build);
    public static string ShownOf(int internalBuild) => internalBuild > 100 ? $"2.0 · build {internalBuild - BaseShared:00}"
        : internalBuild > Base2 ? $"2.0 · build {internalBuild - Base2:00}" : $"1.0 · build {internalBuild}";
    public static SynchronizationContext UiContext;
    /// <summary>A hidden control on Nova's window thread (to bring work back to it).</summary>
    public static Control Marshal;

    /// <summary>Called from another thread: runs <paramref name="a"/> on Nova's window thread instead and returns true.</summary>
    public static bool OffUi(Action a)
    {
        var m = Marshal;
        if (m == null || !m.IsHandleCreated || !m.InvokeRequired) return false;
        try { m.BeginInvoke(a); } catch { }
        return true;
    }
    public static NotifyIcon Tray;
    public static string StartSearch, StartOffice, StartOpen;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--smoke")) return Smoke.Run(args);
        // 🔌 Test connector: Claude desktop starts "NovaEnhance.exe --mcp" (talks to the running Nova, no window).
        if (args.Contains("--mcp")) return TestConnector.Mcp();
        // 🛡 Windows OCR in quarantine: one scanned file per helper process (a crash there never closes Nova).
        if (args.Contains("--ocr")) { try { return Ocr.Helper(args); } catch { return 3; } }
        if (args.Contains("--update-selftest")) return Updater.SelfTest(args);
        // 🛡 Office VPN admin helper: the one-time set-up / removal (started with Windows' “Yes”) and the helper copy in Program Files
        if (args.Contains("--vpn-setup")) return VpnHelper.SetupElevated(args);
        if (args.Contains("--vpn-remove")) return VpnHelper.RemoveElevated();
        if (args.Contains("--vpn-helper")) { try { return VpnHelper.Serve(args); } catch { return 3; } }
        // 🛡 The Guardian: a second copy that only watches Nova (restarts it after a crash or a freeze).
        if (args.Contains("--guardian")) { Application.EnableVisualStyles(); return Guard.Watch(args); }
        // ⏻ The installer (or anyone) asks the running Nova to close: it closes within 5 s, whatever is still running.
        if (args.Contains("--quit"))
        {
            try { EventWaitHandle.OpenExisting("NovaEnhanceWindows-quit").Set(); } catch { }
            return 0;
        }
        Restored = args.Contains("--restored");

        // Right-click a folder → "Search inside with Nova" starts NovaEnhance.exe --search "<folder>".
        var searchIdx = Array.IndexOf(args, "--search");
        var searchPath = searchIdx >= 0 && searchIdx + 1 < args.Length ? args[searchIdx + 1] : null;
        // ✦ Nova tab in Office: NovaEnhance.exe --office <action> (the add-in starts Nova with it when Nova was closed).
        var officeIdx = Array.IndexOf(args, "--office");
        var officeAct = officeIdx >= 0 && officeIdx + 1 < args.Length ? args[officeIdx + 1] : null;
        // 🔗 nova:mail/123 — a ✉ Open link clicked in an Excel / Word export.
        var openLink = args.FirstOrDefault(x => x.StartsWith("nova:", StringComparison.OrdinalIgnoreCase));
        // 📦 Explorer ▸ Nova transfer: NovaEnhance.exe --xfer <copy|cut|paste|…> "<path>" (Explorer starts one per selected item)
        var xferIdx = Array.IndexOf(args, "--xfer");
        var xferVerb = xferIdx >= 0 && xferIdx + 1 < args.Length ? args[xferIdx + 1] : null;
        var xferPath = xferIdx >= 0 && xferIdx + 2 < args.Length ? args[xferIdx + 2] : "";
        if (xferVerb != null)
        {
            try { File.WriteAllText(Path.Combine(Transfer.RequestDir, Guid.NewGuid().ToString("N") + ".req"), xferVerb + "\n" + xferPath); } catch { }
        }

        using var mutex = new Mutex(true, "NovaEnhanceWindows-single", out bool first);
        if (!first)
        {
            try
            {
                if (xferVerb != null) EventWaitHandle.OpenExisting("NovaEnhanceWindows-xfer").Set();
                else if (openLink != null)
                {
                    File.WriteAllText(Path.Combine(Paths.Cache, "open-request.txt"), openLink);
                    EventWaitHandle.OpenExisting("NovaEnhanceWindows-open").Set();
                }
                else if (officeAct != null)
                    EventWaitHandle.OpenExisting("NovaEnhanceWindows-office-" + officeAct.Replace("mode:", "mode_")).Set();
                else if (searchPath != null)
                {
                    File.WriteAllText(Path.Combine(Paths.Cache, "search-request.txt"), searchPath);
                    EventWaitHandle.OpenExisting("NovaEnhanceWindows-search").Set();
                }
                else EventWaitHandle.OpenExisting("NovaEnhanceWindows-show").Set();   // already running: open Settings
            }
            catch { }
            return 0;
        }
        StartSearch = searchPath;
        StartOffice = officeAct;
        StartOpen = openLink;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("ui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Guard.LastWords(e.ExceptionObject); Log.Write("crash: " + e.ExceptionObject); };
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => Guard.CleanExit();
        // Windows sign-out / shutdown / an installer's “close the applications”: do not hold them up
        Microsoft.Win32.SystemEvents.SessionEnded += (_, _) => { try { Guard.CleanExit(); } catch { } new Thread(() => { Thread.Sleep(3000); Environment.Exit(0); }) { IsBackground = true }.Start(); };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            // a network call stopped on purpose (closing, cancelling, the PC sleeping) is not an error
            var b = e.Exception?.GetBaseException();
            if (b is OperationCanceledException || b is System.Net.Sockets.SocketException { ErrorCode: 995 } || b is IOException && (b.Message.Contains("aborted") || b.Message.Contains("995")))
            {
                // 🔎 which feature it came from (counted by source, for 🩺 nova.errors)
                var src = NetStops.Source(e.Exception);
                NetStops.Add(src, Busy.Now());
                Log.Write($"· background network call stopped ({src}): " + b.Message);
                return;
            }
            Log.Error("task", e.Exception);
        };

        Application.Run(new NovaContext(args.Contains("--tray") || xferVerb != null || StartSearch != null || StartOffice != null || StartOpen != null));
        return 0;
    }

    public static bool Restored;

    public static void Balloon(string title, string text)
    {
        if (AdminLock.Locked) return;   // 🔐 nothing of yours pops up while Nova is locked
        try { UiContext?.Post(_ => Tray?.ShowBalloonTip(8000, title, text, ToolTipIcon.Info), null); } catch { }
    }
}

/// <summary>Lives in the tray near the clock; owns the hotkeys and the timers.</summary>
class NovaContext : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly HotkeyWindow hot;
    readonly System.Windows.Forms.Timer timer;
    readonly EventWaitHandle showEvt, searchEvt, openEvt, xferEvt, quitEvt;
    readonly List<EventWaitHandle> officeEvts = new();
    int ticks;

    public NovaContext(bool startedWithWindows)
    {
        // Nova's window thread: every window must be created here. A WinForms context is installed explicitly — before, a plain
        // context could slip in, and windows Nova opened by itself (Settings at start, a window after a restart) were created on a
        // background thread and never responded.
        var wfc = new UiWatch.Ctx(new WindowsFormsSynchronizationContext());
        SynchronizationContext.SetSynchronizationContext(wfc);
        Program.UiContext = wfc;
        Program.Marshal = new Control();
        Program.Marshal.CreateControl();
        _ = Program.Marshal.Handle;
        UiWatch.Start();
        Paths.Ensure();
        Store.Load();
        try { Persona.Migrate(); } catch { }   // 🌐 universal Nova: an old install keeps its field (construction for an architecture profile)
        Task.Run(() => { try { Projects.MigrateNames(); Projects.MigrateClients(); } catch { } });   // 🏷 build 37: your project codes and names
        // 🛡 Two starts that never finished → safe mode (background jobs off, you switch them on one by one).
        Guard.BeginStart();
        Startup.Apply();
        ExplorerMenu.Apply();

        tray = new NotifyIcon { Icon = Th.AppIcon, Text = "Nova Enhance", Visible = true };
        Program.Tray = tray;
        // 🌐 Office VPN: its own icon beside the clock (🟢 connected · 🟡 connecting · 🔴 not connected); click = connect.
        var vpnTray = new NotifyIcon { Icon = OfficeVpn.IconFor(OfficeVpn.State.Off), Text = "Office VPN", Visible = OfficeVpn.On };
        var vm = new ContextMenuStrip { Font = Th.F(10) };
        var vStatus = new ToolStripMenuItem("…") { Enabled = false };
        vm.Items.Add(vStatus);
        vm.Items.Add(new ToolStripSeparator());
        vm.Items.Add("🟢  Connect", null, (_, _) => OfficeVpn.Connect());
        vm.Items.Add("■  Disconnect", null, (_, _) => OfficeVpn.Disconnect());
        vm.Items.Add("🔁  Reconnect automatically", null, (s2, _) => { OfficeVpn.Auto = !OfficeVpn.Auto; });
        vm.Items.Add(new ToolStripSeparator());
        vm.Items.Add("🌐  Nova VPN window…", null, (_, _) => Screens.Vpn());
        vm.Items.Add("📦  Transfers…", null, (_, _) => Screens.Transfers());
        vm.Items.Add("🗂  Project sources (office folders)…", null, (_, _) => Screens.Sources());
        vm.Items.Add("🛡  Open FortiClient", null, (_, _) => { if (OfficeVpn.Gui() is { } g) Shell.Open(g); });
        vm.Items.Add("⚙  VPN settings", null, (_, _) => Screens.Settings("🌐 Office VPN"));
        vm.Opening += (_, _) => { vStatus.Text = OfficeVpn.Line; ((ToolStripMenuItem)vm.Items[4]).Checked = OfficeVpn.Auto; };
        vpnTray.ContextMenuStrip = vm;
        vpnTray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { if (OfficeVpn.Now is OfficeVpn.State.Up or OfficeVpn.State.UpNoServer) vpnTray.ShowBalloonTip(4000, "Office VPN", OfficeVpn.Line, ToolTipIcon.Info); else OfficeVpn.Connect(); } };
        var vpnTick = new System.Windows.Forms.Timer { Interval = 2000 };
        vpnTick.Tick += (_, _) =>
        {
            try
            {
                vpnTray.Visible = OfficeVpn.On;
                if (!OfficeVpn.On) return;
                vpnTray.Icon = OfficeVpn.IconFor(OfficeVpn.Now);
                var t = OfficeVpn.Line;
                vpnTray.Text = t.Length > 127 ? t[..127] : t;
            }
            catch { }
        };
        vpnTick.Start();
        OfficeVpn.Start();
        Task.Run(ProjectSources.Migrate);
        ProjectSources.Offline.Start();
        // 🌐 Universal Nova: old installs keep their field (construction for an architecture profile); a first start shows the welcome.
        try { if (Persona.Fresh) new System.Windows.Forms.Timer { Interval = 1500, Enabled = true }.Tick += (s, _) => { ((System.Windows.Forms.Timer)s!).Stop(); new WelcomeForm().Show(); }; } catch { }
        Dots.Start();   // ● ○ ○ Nova is thinking — every "…" status in every window, and the tray icon
        // ⏳ Pointing at the tray icon shows the email index progress.
        if (!Guard.SafeMode) StartJobs();
        Guard.StartGuardian();
        // 💓 The Guardian sees Nova is alive (and not frozen).
        var beat = new System.Windows.Forms.Timer { Interval = 10000 };
        beat.Tick += (_, _) => Guard.Heartbeat();
        beat.Start();
        var okTimer = new System.Windows.Forms.Timer { Interval = 60000 };
        okTimer.Tick += (_, _) => { okTimer.Stop(); if (!Guard.SafeMode) Guard.StartOk(); };
        okTimer.Start();
        var trayTip = new System.Windows.Forms.Timer { Interval = 5000 };
        trayTip.Tick += (_, _) =>
        {
            try
            {
                // ⏳ Email index, then 📁 your project files (names, read inside, 📖 background reading).
                if (Busy.Any) return;   // the ● working line is shown instead
                var t = IndexProgress.Tray();
                var o = Projects.Overall();
                if (o.Names > 0)
                    t += "\n📁 " + (o.Listing ? "listing…" : $"{o.Names:N0} names ✓") + $" · {o.Read:N0} read" + (Projects.ReadAll.Line() is { Length: > 0 } ra ? " · " + ra : "");
                tray.Text = t.Length > 127 ? t[..127] : t;
            }
            catch { }
        };
        trayTip.Start();
        // ● ● ● Something running (AI, the log, a search…): amber dot on the tray icon, and the hover text says what.
        bool dotOn = false;
        var busyTip = new System.Windows.Forms.Timer { Interval = 1000 };
        busyTip.Tick += (_, _) =>
        {
            try
            {
                var any = Busy.Any;
                // ⏱ the running time on the tray icon itself (amber after 30 s)
                if (any) { tray.Icon = Busy.TimeIcon(Th.AppIcon, Busy.Seconds); var t = Busy.Tray(); tray.Text = t.Length > 127 ? t[..127] : t; }
                else if (dotOn) tray.Icon = Th.AppIcon;
                dotOn = any;
            }
            catch { }
        };
        busyTip.Start();
        var m = new ContextMenuStrip { Font = Th.F(10) };
        m.Items.Add("5.1  ✦  Enhance selected text      Ctrl+Shift+E", null, (_, _) => Screens.Enhance());
        m.Items.Add("1.0  💬  Nova Chat & AI Hub        Ctrl+Shift+A", null, (_, _) => Screens.Hub(null));
        m.Items.Add("0.1  📌  My Work", null, (_, _) => Screens.Work(null));
        m.Items.Add("4.3  🎯  Find a file…", null, (_, _) => Screens.Guided());
        m.Items.Add("4.5  📄  Nova for this PDF                Ctrl+Shift+D", null, (_, _) => PdfCompanion.Open());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("4.6  📁  Search inside a folder…", null, (_, _) => Screens.FolderSearch(null));
        m.Items.Add("3.6  🔍  Search for email                Ctrl+Shift+F", null, (_, _) => Screens.Ask());
        m.Items.Add($"3.7  💬  Talk about this email          Ctrl+Alt+{Store.Str("win_hk_mailask", "Q")}", null, (_, _) => Screens.MailAsk(null));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("3.8  ☀  Morning brief", null, (_, _) => Screens.Brief());
        m.Items.Add("0.2  🕘  History…                          Ctrl+Shift+H", null, (_, _) => Screens.History(false));
        m.Items.Add("4.4  📋  Submittal log (WIR · MIR · MAR · PQS · RFI · SDS)", null, (_, _) => Screens.Submittal());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("8     ⚙  Settings", null, (_, _) => Screens.Settings());
        m.Items.Add("📁  Open the Nova Enhance folder", null, (_, _) => Shell.Open(Paths.Root));
        m.Items.Add("⬆  Check for updates", null, (_, _) => Screens.Settings("Updates"));
        m.Items.Add(new ToolStripSeparator());
        // 🔗 Claude ↔ Outlook: you decide when, it switches itself off
        var claude = new ToolStripMenuItem("🔗  Allow Claude to read Outlook");
        claude.DropDownItems.Add("for 2 hours", null, (_, _) => ClaudeAccess.Allow(2));
        claude.DropDownItems.Add("for 8 hours", null, (_, _) => ClaudeAccess.Allow(8));
        claude.DropDownItems.Add("until tonight", null, (_, _) => ClaudeAccess.Allow(24));
        var claudeStop = new ToolStripMenuItem("■ Stop now", null, (_, _) => ClaudeAccess.Allow(0));
        claude.DropDownItems.Add(claudeStop);
        m.Items.Add(claude);
        m.Opening += (_, _) => { claude.Text = ClaudeAccess.On ? $"🔗  Claude can read Outlook until {ClaudeAccess.Until:HH:mm}" : "🔗  Allow Claude to read Outlook"; claudeStop.Enabled = ClaudeAccess.On; claude.Visible = Relay.On; };
        var lockItem = new ToolStripMenuItem("🔐  Lock Nova now", null, (_, _) => AdminLock.Lock("🔐 Lock now"));
        m.Items.Add(lockItem);
        m.Opening += (_, _) => { lockItem.Visible = AdminLock.Enabled; lockItem.Text = AdminLock.Locked ? "🔓  Unlock Nova…" : "🔐  Lock Nova now"; };
        lockItem.Click += (_, _) => { if (AdminLock.Locked && lockItem.Text.StartsWith("🔓")) AdminLock.Pass("Unlock"); };
        m.Items.Add("Exit Nova Enhance", null, (_, _) => { if (AdminLock.Pass("Exit Nova Enhance")) Quit(); });
        tray.ContextMenuStrip = m;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Screens.Ask(); };

        AdminLock.Start();   // 🔐 locked at start (when a PIN is set), then on idle, Windows lock and sleep
        hot = new HotkeyWindow();
        hot.Register(1, Keys.E, Screens.Enhance, "Ctrl+Shift+E");
        hot.Register(2, Keys.F, Screens.Ask, "Ctrl+Shift+F");
        hot.Register(3, Keys.A, Screens.HubSelection, "Ctrl+Shift+A");
        hot.Register(4, Keys.D, () => PdfCompanion.Open(), "Ctrl+Shift+D");
        hot.Register(6, Keys.H, () => Screens.History(false), "Ctrl+Shift+H");
        // ✦ Ask Nova about the email open or selected in Outlook — any Outlook window, no ribbon needed (Ctrl+Alt+Q by default).
        var mk = Store.Str("win_hk_mailask", "Q");
        if (Enum.TryParse<Keys>(mk, out var mkey)) hot.Register(5, mkey, () => Screens.MailAsk(null), "Ctrl+Alt+" + mk, 0x1 | 0x2);
        PdfCompanion.Watch();

        showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "NovaEnhanceWindows-show");
        ThreadPool.RegisterWaitForSingleObject(showEvt, (_, _) => Program.UiContext.Post(_ => Screens.Settings(), null), null, -1, false);
        searchEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "NovaEnhanceWindows-search");
        ThreadPool.RegisterWaitForSingleObject(searchEvt, (_, _) =>
        {
            string p = null;
            try { var f = Path.Combine(Paths.Cache, "search-request.txt"); p = File.ReadAllText(f); File.Delete(f); } catch { }
            Program.UiContext.Post(_ => Screens.FolderSearch(p), null);
        }, null, -1, false);
        // 📦 Nova transfer: requests from Explorer's right-click menu
        Transfer.Start();
        // 📐 once a day: how many document codes Nova does not know yet (names only, on this PC) — shown in the morning brief
        Filing.Tick = new System.Threading.Timer(_ => { try { Filing.BackgroundCount(); } catch { } }, null, TimeSpan.FromMinutes(12), TimeSpan.FromHours(6));
        XferMenu.Apply();
        PasteHook.Apply();
        quitEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "NovaEnhanceWindows-quit");
        ThreadPool.RegisterWaitForSingleObject(quitEvt, (_, _) => { try { Program.UiContext.Post(_ => Quit(), null); } catch { Environment.Exit(0); } }, null, -1, true);
        xferEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "NovaEnhanceWindows-xfer");
        ThreadPool.RegisterWaitForSingleObject(xferEvt, (_, _) => Transfer.ReadRequests(), null, -1, false);
        Transfer.ReadRequests();   // Nova was started by the menu itself
        openEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "NovaEnhanceWindows-open");
        ThreadPool.RegisterWaitForSingleObject(openEvt, (_, _) =>
        {
            string p = null;
            try { var f = Path.Combine(Paths.Cache, "open-request.txt"); p = File.ReadAllText(f); File.Delete(f); } catch { }
            if (p != null) Program.UiContext.Post(_ => Export.OpenLink(p), null);
        }, null, -1, false);
        Export.RegisterLinks();
        // ✦ A button of the Nova tab in Word, Excel, PowerPoint, Outlook, Visio, Project, Access or Publisher.
        // 🛡 Check before sending: listens to Outlook's Send (again every minute — Outlook may open later or restart).
        try { SendCheck.Ensure(); } catch (Exception e) { Log.Warn("check before sending", e.Message); }
        // One event per button: the add-in only signals it (no files, no program started from Office).
        OfficeRibbon.Refresh();
        foreach (var id in OfficeRibbon.Buttons)
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, "NovaEnhanceWindows-office-" + id);
            officeEvts.Add(ev);
            var act = id.StartsWith("mode_") ? "mode:" + id[5..] : id;
            ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => Program.UiContext.Post(_ => Screens.Office(act), null), null, -1, false);
        }

        if (Store.Bool("win_addin", false) || Store.Bool("win_addin_it", false))
        {
            Server.Start();
            // After an update: the add-in files carry the new version (Word/Excel/PowerPoint pick it up at their next start).
            try { if (Store.Str("win_manifest_build") != Program.Build.ToString()) { OfficeAddin.WriteManifests(); Store.Set("win_manifest_build", Program.Build.ToString()); } } catch (Exception e) { Log.Error("Office add-in · manifests", e); }
        }
        if (BrowserAddon.On) { BrowserAddon.Refresh(); Server.StartBrowser(); }

        timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Program.UiContext.Post(_ =>
        {
            if (Program.StartSearch != null) Screens.FolderSearch(Program.StartSearch);
            if (Program.StartOffice != null) Screens.Office(Program.StartOffice);
            if (Program.StartOpen != null) Export.OpenLink(Program.StartOpen);
            // 🛡 Restarted by the Guardian: say so, keep the report, reopen the window you had.
            if (Program.Restored)
            {
                var what = Guard.TakeCrashReport();
                tray.ShowBalloonTip(10000, "🛡 Nova Enhance restarted", (what.Length > 0 ? what.Split('\n').Last().Trim() : "Nova closed unexpectedly") + " — the details are in Settings → Error log.", ToolTipIcon.Warning);
                switch (Guard.LastScreenName())
                {
                    case "ask": Screens.Ask(); break;
                    case "find": Screens.FindFile(null); break;
                    case "guided": Screens.Guided(); break;
                    case "folder": Screens.FolderSearch(null); break;
                    case "brief": Screens.Brief(); break;
                    case "hub": Screens.Hub(null); break;
                    case "work": Screens.Work(null); break;
                    case "pst": Screens.PstReader(null); break;
                }
            }
            if (Guard.SafeMode)
            {
                tray.ShowBalloonTip(15000, "🛡 Nova Enhance — safe mode", $"Background jobs are off because {Guard.SafeReason}. Settings → Error log → 🛡 Anti-crash to switch them back on.", ToolTipIcon.Warning);
                Screens.Settings("Error log");
            }
            else if (!Program.Restored) FirstRun(startedWithWindows);
            Tick();
        }, null);
    }

    void FirstRun(bool startedWithWindows)
    {
        if (Store.Bool("win_welcomed", false))
        {
            if (!startedWithWindows) Screens.Settings();
            return;
        }
        Store.SetBool("win_welcomed", true);
        tray.ShowBalloonTip(10000, "Nova Enhance is running",
            "Select text anywhere and press Ctrl+Shift+E. Press Ctrl+Shift+F to ask about your email. Right-click the ✦ icon near the clock for Settings.", ToolTipIcon.Info);
        Screens.Settings();
    }

    async void Tick()
    {
        try
        {
            UiWatch.Step("minute check: backup"); AutoBackup.MaybeRun();
            UiWatch.Step("minute check: morning brief"); BriefLogic.MaybeShow();
            UiWatch.Step("minute check: calendar"); Calendar.Tick();
            UiWatch.Step("minute check: my work"); Work.Tick();
            UiWatch.Step("minute check: prompts"); Prompts.Tick();
            UiWatch.Step("minute check: send check"); SendCheck.Ensure();
            UiWatch.Step("minute check: contract register"); ContractRegister.Tick();
            UiWatch.Step("idle");
            if (ticks++ % 60 == 0 && Updater.Due) { UiWatch.Step("hourly: update check"); await Updater.Check(); Updater.Offer(false); UiWatch.Step("idle"); }
        }
        catch (Exception e) { Log.Error("tick", e); }
    }

    /// <summary>The background jobs (each protected: a failure stops only that job, which restarts a minute later).</summary>
    public static void StartJobs()
    {
        Guard.Stage("reading Outlook"); MailIndex.Start();
        Guard.Stage("email index"); IndexProgress.Start();
        ReadGuard.AtStart();   // 🛡 a file Nova was reading when it closed is skipped from now on
        Guard.Stage("project folders"); Projects.Indexer.Start();
        Guard.Stage("reading project files"); Projects.ReadAll.Start();
        Guard.Stage("meaning search"); Semantic.Start();
        Guard.Stage("email projects"); MailProjects.Start();
        Guard.Stage("archive reader"); Pst.Start();
        Guard.Stage("activity auto-save"); Activity.StartAuto();
        Guard.Stage("test connector"); TestConnector.Start();
        Guard.Stage("brain"); Brain.Start();
        // (2.0) The night check of moved emails was removed: it crashed Nova (build 75–76). Moved emails are found when you open them.
        Guard.Stage("phone link"); PhoneLink.Start(); MailFull.Start();
        Guard.Stage("started");
    }

    void Quit()
    {
        // ⏻ Exit always ends within 5 s: background work (listing, reading, OCR, index) gets 5 s to stop, then Nova closes anyway
        //    (the index is written in small transactions: nothing is lost, the file is simply read again later).
        new Thread(() => { Thread.Sleep(5000); try { Log.Write("⏻ closed after 5 s (background work still running)"); } catch { } Environment.Exit(0); }) { IsBackground = true, Name = "Nova exit watchdog" }.Start();
        Guard.CleanExit();
        tray.Visible = false;
        MailIndex.Stop();
        Server.Stop();
        TestConnector.Stop();
        hot.DestroyHandle();
        ExitThread();
    }
}

/// <summary>Ctrl+Shift+E and Ctrl+Shift+F, everywhere in Windows.</summary>
class HotkeyWindow : NativeWindow
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    readonly Dictionary<int, Action> map = new();

    public HotkeyWindow() { CreateHandle(new CreateParams()); }

    public void Register(int id, Keys k, Action a, string name, uint mods = 0x2 | 0x4)
    {
        map[id] = a;
        if (!RegisterHotKey(Handle, id, mods | 0x4000, (uint)k))
        {
            Log.Write($"Hotkey {name} is already used by another program");
            Program.Balloon("Shortcut busy", $"{name} is used by another program — use the ✦ icon near the clock instead.");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && map.TryGetValue(m.WParam.ToInt32(), out var a))
        {
            try { a(); } catch (Exception e) { Log.Error("hotkey", e); }
        }
        base.WndProc(ref m);
    }
}

/// <summary>One copy of each window.</summary>
static class Screens
{
    static EnhanceForm enh;
    static AskForm ask;
    static SettingsForm set;
    static BriefForm brief;
    static FolderSearchForm folder;
    static HubForm hub;
    static WorkForm work;

    public static void Work(string id)
    {
        if (Program.OffUi(() => Work(id))) return;
        Guard.Screen("work");
        try
        {
            if (work == null || work.IsDisposed) work = new WorkForm();
            Show(work);
            work.Reload(id);
        }
        catch (Exception e) { Log.Error("My Work", e); }
    }

    /// <summary>📌 Save as task, then show it in My Work.</summary>
    public static void Saved(WorkTask t)
    {
        Program.Balloon("📌 Saved in My Work", t.Title);
        Work(t.Id);
    }

    static HistoryForm hist;
    static BrainForm brainForm;

    public static void ShowForm(Form f) => Show(f);

    /// <summary>🧠 Nova Brain window.</summary>
    /// <summary>The Brain window, if open, shows what arrived from the phone / the connector.</summary>
    public static void BrainNudge()
    {
        if (Program.OffUi(BrainNudge)) return;
        try { if (brainForm != null && !brainForm.IsDisposed) brainForm.Flush(); } catch { }
    }

    static FilingLearnForm learnWin;
    /// <summary>🧠 Learn this project: the codes of its document numbers, asked once and kept in Nova Brain.</summary>
    public static void FilingLearn(string project = null)
    {
        if (Program.OffUi(() => FilingLearn(project))) return;
        try { if (learnWin == null || learnWin.IsDisposed) learnWin = new FilingLearnForm(project); Show(learnWin); }
        catch (Exception e) { Log.Error("learn this project", e); }
    }

    public static void Brain()
    {
        if (Program.OffUi(Brain)) return;
        try { if (brainForm == null || brainForm.IsDisposed) brainForm = new BrainForm(); Show(brainForm); }
        catch (Exception e) { Log.Error("Nova Brain", e); }
    }

    /// <summary>📋 Submittal log window (a new one each time, with its own choices).</summary>
    /// <summary>📋 The log with a Nova agent conversation from Nova Chat (its table, follow-ups here).</summary>
    public static void SubmittalChat(string chatId)
    {
        if (Program.OffUi(() => SubmittalChat(chatId))) return;
        try { Show(new SubmittalForm(null, false, chatId)); }
        catch (Exception e) { Log.Error("Submittal log", e); }
    }

    public static void Submittal(SubmittalLog.Options o = null)
    {
        if (Program.OffUi(() => Submittal(o))) return;
        try { Show(new SubmittalForm(o)); }
        catch (Exception e) { Log.Error("Submittal log", e); }
    }

    static ProjectsForm projForm;

    /// <summary>📁 Your projects + ＋ Projects found in your emails.</summary>
    public static void ProjectsFound()
    {
        if (Program.OffUi(ProjectsFound)) return;
        try { if (projForm == null || projForm.IsDisposed) projForm = new ProjectsForm(); Show(projForm); }
        catch (Exception e) { Log.Error("Projects", e); }
    }

    public static void SearchHistory() => History(true);

    static HistoryHub hub2;
    /// <summary>0.2 🕘 History: 💬 Chats | 🔎 Searches in one window.</summary>
    public static void History(bool searches)
    {
        if (Program.OffUi(() => History(searches))) return;
        try
        {
            if (hub2 == null || hub2.IsDisposed) hub2 = new HistoryHub(searches); else hub2.Show(searches);
            Show(hub2);
        }
        catch (Exception e) { Log.Error("History", e); }
    }

    public static void HubContinueDeep(string text, string id)
    {
        if (Program.OffUi(() => HubContinueDeep(text, id))) return;
        try { if (hub == null || hub.IsDisposed) hub = new HubForm(); Show(hub); hub.ContinueDeep(text, id); }
        catch (Exception e) { Log.Error("AI window · continue", e); }
    }

    public static void HubOpenChat(string id)
    {
        if (Program.OffUi(() => HubOpenChat(id))) return;
        try { if (hub == null || hub.IsDisposed) hub = new HubForm(); Show(hub); hub.OpenChat(id); }
        catch (Exception e) { Log.Error("AI window · open chat", e); }
    }

    public static void HubAttach(string path)
    {
        if (Program.OffUi(() => HubAttach(path))) return;
        try
        {
            if (hub == null || hub.IsDisposed) hub = new HubForm();
            Show(hub);
            hub.AttachFile(path);
        }
        catch (Exception e) { Log.Error("AI window · attach", e); }
    }

    public static void Hub(string text)
    {
        if (Program.OffUi(() => Hub(text))) return;
        Guard.Screen("hub");
        try
        {
            if (hub == null || hub.IsDisposed) hub = new HubForm();
            Show(hub);
            if (text != null) hub.SendText(text);
        }
        catch (Exception e) { Log.Error("AI window", e); }
    }

    /// <summary>Ctrl+Shift+A: copies the selected text from the program in front and puts it into the AI window.</summary>
    public static async void HubSelection()
    {
        try
        {
            var (_, text) = await Selection.Grab();
            Hub(string.IsNullOrWhiteSpace(text) ? null : text);
        }
        catch (Exception e) { Log.Error("AI window · selection", e); }
    }

    public static void FolderSearch(string path)
    {
        if (Program.OffUi(() => FolderSearch(path))) return;
        Guard.Screen("folder");
        if (folder == null || folder.IsDisposed) folder = new FolderSearchForm();
        Show(folder);
        folder.Begin(path);
    }

    public static async void Enhance() => await EnhanceWith(null);

    /// <summary>The Enhance window with the selected text, started in one mode (✦ Nova tab buttons: Fix grammar, Shorten…).</summary>
    public static async Task EnhanceWith(string mode)
    {
        try
        {
            var (h, text) = await Selection.Grab();
            // ✉ Outlook: nothing selected (or the whole email selected) = your text only — closing, signature and earlier emails stay untouched.
            bool body = false, all = false;
            if (h != IntPtr.Zero && OutlookBody.IsOutlook(h))
            {
                using var step = UiWatch.Outlook("reading your email for ✦ Enhance");
                var own = OutlookBody.Read();
                if (own.Length > 0 && (string.IsNullOrWhiteSpace(text) || Norm(text).StartsWith(Norm(own)) || Norm(text).Length > Norm(own).Length + 20 && Norm(text).Contains(Norm(own))))
                { text = own; body = true; }
            }
            // Nothing selected anywhere else: all the text of the box / document in front.
            // (Writing programs only — in a web page, Ctrl+A would take the whole page.)
            if (!body && string.IsNullOrWhiteSpace(text) && h != IntPtr.Zero && Regex.IsMatch(Selection.AppName(h), @"Word|PowerPoint|OneNote|Notepad|WordPad|Publisher|Visio|Project|Access|Teams", RegexOptions.IgnoreCase))
            { text = await Selection.GrabAll(h); all = text.Trim().Length > 0; }
            if (enh == null || enh.IsDisposed) enh = new EnhanceForm();
            enh.Start(h, text, mode, body, all);
        }
        catch (Exception e) { Log.Error("enhance window", e); }
    }

    static string Norm(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    /// <summary>✦ A button of the Nova tab in an Office program.</summary>
    public static void Office(string action)
    {
        Log.Write("Office tab: " + action);
        switch (action)
        {
            case var m when m.StartsWith("mode:"): _ = EnhanceWith(m[5..]); break;
            case "enhance": Enhance(); break;
            case "mailask": MailAsk(null); break;
            case "related" or "related_c": global::NovaEnhance.Related.FromOutlook(false); break;
            case "relatedol" or "relatedol_c": global::NovaEnhance.Related.FromOutlook(true); break;
            case "search": Ask(); break;
            case "hub": HubSelection(); break;
            case "brief": Brief(); break;
            case "files": FindFile(""); break;
            default: Settings(); break;
        }
    }

    static SourcesForm sources;

    /// <summary>🗂 The project sources explorer (office and local folders per project).</summary>
    static VpnForm vpnWin;
    /// <summary>🌐 Nova VPN window (Nova's own engine).</summary>
    public static void Vpn()
    {
        if (Program.OffUi(Vpn)) return;
        if (vpnWin == null || vpnWin.IsDisposed) vpnWin = new VpnForm();
        Show(vpnWin);
    }

    static TransferForm xferWin;
    /// <summary>📦 Nova transfer window (queue, progress, history).</summary>
    public static void Transfers()
    {
        if (Program.OffUi(Transfers)) return;
        if (xferWin == null || xferWin.IsDisposed) xferWin = new TransferForm();
        Show(xferWin);
    }

    public static void Sources()
    {
        if (Program.OffUi(Sources)) return;
        if (sources == null || sources.IsDisposed) sources = new SourcesForm();
        Show(sources);
    }

    static RelatedForm related;

    /// <summary>🔗 Find related → In Nova: the timeline of one document (all revisions).</summary>
    public static void Timeline(string baseNo, string subjectHint = "")
    {
        if (Program.OffUi(() => Timeline(baseNo, subjectHint))) return;
        if (related == null || related.IsDisposed) related = new RelatedForm(baseNo, subjectHint);
        else related.Load2(baseNo, subjectHint);
        Show(related);
    }

    static MailAskForm mailAsk;

    /// <summary>✦ Ask Nova about this email (h = an email from a Nova list; null = the email selected in Outlook).</summary>
    public static void MailAsk(MailHit h, string subjectHint = "")
    {
        if (Program.OffUi(() => MailAsk(h, subjectHint))) return;
        if (mailAsk == null || mailAsk.IsDisposed) mailAsk = new MailAskForm();
        Show(mailAsk);
        using var step = UiWatch.Outlook("reading the email for 💬 Talk about this email");
        if (h != null) mailAsk.ShowMail(h); else mailAsk.LoadCurrent(subjectHint ?? "");
    }

    /// <summary>💬 The full conversation about the email you are writing (continues what was asked in the Outlook pane).</summary>
    public static void MailAskDraft(Conversation c, string subjectHint)
    {
        if (mailAsk == null || mailAsk.IsDisposed) mailAsk = new MailAskForm();
        Show(mailAsk);
        mailAsk.LoadCurrent(subjectHint ?? "", c);
    }

    /// <summary>↻ Continue a saved email conversation from 🕘 Chat history.</summary>
    public static void MailAskContinue(string path)
    {
        if (mailAsk == null || mailAsk.IsDisposed) mailAsk = new MailAskForm();
        Show(mailAsk);
        mailAsk.Resume(path);
    }

    static ChatHistoryForm history;
    static FileFinderForm finder;

    /// <summary>📂 Find a project file by name (optionally with a search already typed).</summary>
    static GuidedSearchForm guidedWin;
    /// <summary>🎯 Guided search: Where › What › Narrow › Find, always by numbered options.</summary>
    public static void Guided(Project start = null)
    {
        if (Program.OffUi(() => Guided(start))) return;
        Guard.Screen("guided");
        try
        {
            if (guidedWin == null || guidedWin.IsDisposed) guidedWin = new GuidedSearchForm();
            Show(guidedWin);
            if (start != null) guidedWin.StartAt(start);
        }
        catch (Exception e) { Log.Error("guided search", e); }
    }

    public static void FindFile(string q)
    {
        if (Program.OffUi(() => FindFile(q))) return;
        // 🎯 One “Find a file”: with nothing to find yet, the ⚡ quick find + guided steps window.
        if (string.IsNullOrEmpty(q)) { Guided(); return; }
        FindFileList(q);
    }

    /// <summary>▦ The full file list (columns, filters, group by, drawing register, attach all) — from ⚡ Quick find, Nova Chat, the phone…</summary>
    public static void FindFileList(string q)
    {
        if (Program.OffUi(() => FindFileList(q))) return;
        Guard.Screen("find");
        if (finder == null || finder.IsDisposed) finder = new FileFinderForm();
        Show(finder);
        if (!string.IsNullOrEmpty(q)) finder.Search(q);
    }

    static PstReaderForm pstReader;

    /// <summary>📂 Archive reader: a .pst read directly (a path), or an email of one ("pst:…").</summary>
    public static void PstReader(string what)
    {
        if (Program.OffUi(() => PstReader(what))) return;
        Guard.Screen("pst");
        if (pstReader == null || pstReader.IsDisposed) pstReader = new PstReaderForm();
        Show(pstReader);
        if (string.IsNullOrEmpty(what)) return;
        if (what.StartsWith("pst:")) pstReader.ShowEntry(what); else pstReader.OpenFile(what);
    }

    /// <summary>🕘 Chat history: every conversation, newest first, with search.</summary>
    public static void ChatHistory() => History(false);

    public static void AskFor(string q)
    {
        if (Program.OffUi(() => AskFor(q))) return;
        if (ask == null || ask.IsDisposed) ask = new AskForm();
        Show(ask);
        ask.AskNow(q);
    }

    public static void Ask()
    {
        if (Program.OffUi(Ask)) return;
        Guard.Screen("ask");
        if (ask == null || ask.IsDisposed) ask = new AskForm();
        Show(ask);
        ask.FocusQuestion();
    }

    public static void Settings(string section = null)
    {
        if (Program.OffUi(() => Settings(section))) return;
        if (set == null || set.IsDisposed) set = new SettingsForm();
        Show(set);
        if (section != null) set.Go(section);
    }

    public static void Brief()
    {
        if (Program.OffUi(Brief)) return;
        Guard.Screen("brief");
        if (brief == null || brief.IsDisposed) brief = new BriefForm();
        Show(brief);
        brief.Reload();
    }

    public static void Show(Form f)
    {
        if (!f.Visible) f.Show();
        if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
        f.Activate();
        f.BringToFront();
    }
}

/// <summary>Copies the selected text from any program, and pastes the result back into it.</summary>
static class Selection
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    const uint KeyUp = 2;
    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static void CtrlPlus(byte vk)
    {
        keybd_event(0x10, 0, KeyUp, UIntPtr.Zero); // make sure Shift is up
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KeyUp, UIntPtr.Zero);
        keybd_event(0x11, 0, KeyUp, UIntPtr.Zero);
    }

    public static string AppName(IntPtr h)
    {
        try
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return "Nova";
            var p = Process.GetProcessById((int)pid);
            try
            {
                var d = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) return d.Replace("Microsoft ", "").Replace("Microsoft® ", "");
            }
            catch { }
            return p.ProcessName;
        }
        catch { return "the app"; }
    }

    public static bool IsOwn(IntPtr h)
    {
        try { GetWindowThreadProcessId(h, out var pid); return pid == Environment.ProcessId; } catch { return false; }
    }

    public static async Task<(IntPtr, string)> Grab()
    {
        var h = GetForegroundWindow();
        if (IsOwn(h)) return (IntPtr.Zero, "");
        // Wait until Ctrl, Shift and E are released, or the copy would become Ctrl+Shift+C.
        for (int i = 0; i < 40 && (Down(0x10) || Down(0x11) || Down(0x45) || Down(0x41) || Down(0x44)); i++) await Task.Delay(15);
        string old = null;
        try { if (Clipboard.ContainsText()) old = Clipboard.GetText(); } catch { }
        try { Clipboard.Clear(); } catch { }
        CtrlPlus(0x43);
        string got = "";
        for (int i = 0; i < 25; i++)
        {
            await Task.Delay(30);
            try { if (Clipboard.ContainsText()) { got = Clipboard.GetText(); break; } } catch { }
        }
        try { if (old != null) Clipboard.SetText(old); } catch { }
        return (h, got);
    }

    /// <summary>Nothing selected: selects all the text of the box / document in front (Ctrl+A) and copies it.</summary>
    public static async Task<string> GrabAll(IntPtr h)
    {
        if (h == IntPtr.Zero || IsOwn(h)) return "";
        string old = null;
        try { if (Clipboard.ContainsText()) old = Clipboard.GetText(); } catch { }
        try { Clipboard.Clear(); } catch { }
        SetForegroundWindow(h);
        await Task.Delay(80);
        CtrlPlus(0x41);
        await Task.Delay(120);
        CtrlPlus(0x43);
        string got = "";
        for (int i = 0; i < 25; i++)
        {
            await Task.Delay(30);
            try { if (Clipboard.ContainsText()) { got = Clipboard.GetText(); break; } } catch { }
        }
        try { if (old != null) Clipboard.SetText(old); } catch { }
        return got;
    }

    public static async Task<bool> Paste(IntPtr h, string text)
    {
        for (int i = 0; i < 3; i++)
        {
            try { Clipboard.SetText(text); break; } catch { await Task.Delay(100); }
        }
        if (h == IntPtr.Zero) return false;
        SetForegroundWindow(h);
        await Task.Delay(180);
        CtrlPlus(0x56);
        await Task.Delay(120);
        return true;
    }
}


/// <summary>🔎 Background network calls that were stopped (cancelled, closed, the PC sleeping): counted by the Nova feature they came
/// from, with what Nova was busy with at that moment — so a retry loop can be found. Designed by A.HAMROUNI</summary>
static class NetStops
{
    static readonly Dictionary<string, (int N, DateTime First, DateTime Last, string While)> by = new();

    public static string Source(Exception ex)
    {
        var s = ex?.ToString() ?? "";
        // the innermost Nova frame: “at NovaEnhance.OfficeAddin.Handle(” or “at NovaEnhance.Projects.<ListFolder>d__12.MoveNext(”
        var m = System.Text.RegularExpressions.Regex.Match(s, @"at NovaEnhance\.([\w\.]+?)\.(?:<(\w+)>[\w_]*\.MoveNext|(\w+))\(");
        if (m.Success) return m.Groups[1].Value.Replace("NovaEnhance.", "") + "." + (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        var f = System.Text.RegularExpressions.Regex.Match(s, @"at ([\w\.]+)\.(\w+)\(");
        return f.Success ? "no Nova frame · " + f.Groups[1].Value.Split('.').Last() + "." + f.Groups[2].Value : "unknown";
    }

    public static void Add(string src, string busy)
    {
        lock (by)
        {
            var x = by.TryGetValue(src, out var v) ? v : (0, DateTime.Now, DateTime.Now, "");
            by[src] = (x.Item1 + 1, x.Item2, DateTime.Now, busy.Length > 0 ? busy : x.Item4);
        }
    }

    public static string Report()
    {
        lock (by)
        {
            if (by.Count == 0) return "";
            var sb = new System.Text.StringBuilder("\nBackground network calls stopped, by source (since Nova started):\n");
            foreach (var (k, v) in by.OrderByDescending(x => x.Value.N).Take(12))
                sb.Append($"- {k}: ×{v.N} · {v.First:dd MMM HH:mm} → {v.Last:HH:mm}{(v.While.Length > 0 ? " · Nova was busy with: " + v.While : "")}\n");
            return sb.ToString();
        }
    }
}
