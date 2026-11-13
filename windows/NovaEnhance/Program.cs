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
    public static int ShownBuild => Build > Base2 ? Build - Base2 : Build;
    public static string Version => Build > Base2 ? "2.0" : "1.0";
    public static string Shown => ShownOf(Build);
    public static string ShownOf(int internalBuild) => internalBuild > Base2 ? $"2.0 · build {internalBuild - Base2:00}" : $"1.0 · build {internalBuild}";
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
    public static string StartSearch;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--smoke")) return Smoke.Run(args);
        // 🔌 Test connector: Claude desktop starts "NovaEnhance.exe --mcp" (talks to the running Nova, no window).
        if (args.Contains("--mcp")) return TestConnector.Mcp();
        // 🛡 Windows OCR in quarantine: one scanned file per helper process (a crash there never closes Nova).
        if (args.Contains("--ocr")) { try { return Ocr.Helper(args); } catch { return 3; } }
        if (args.Contains("--update-selftest")) return Updater.SelfTest(args);
        // 🛡 The Guardian: a second copy that only watches Nova (restarts it after a crash or a freeze).
        if (args.Contains("--guardian")) { Application.EnableVisualStyles(); return Guard.Watch(args); }
        Restored = args.Contains("--restored");

        // Right-click a folder → "Search inside with Nova" starts NovaEnhance.exe --search "<folder>".
        var searchIdx = Array.IndexOf(args, "--search");
        var searchPath = searchIdx >= 0 && searchIdx + 1 < args.Length ? args[searchIdx + 1] : null;

        using var mutex = new Mutex(true, "NovaEnhanceWindows-single", out bool first);
        if (!first)
        {
            try
            {
                if (searchPath != null)
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

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("ui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Guard.LastWords(e.ExceptionObject); Log.Write("crash: " + e.ExceptionObject); };
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => Guard.CleanExit();
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };

        Application.Run(new NovaContext(args.Contains("--tray") || StartSearch != null));
        return 0;
    }

    public static bool Restored;

    public static void Balloon(string title, string text)
    {
        try { UiContext?.Post(_ => Tray?.ShowBalloonTip(8000, title, text, ToolTipIcon.Info), null); } catch { }
    }
}

/// <summary>Lives in the tray near the clock; owns the hotkeys and the timers.</summary>
class NovaContext : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly HotkeyWindow hot;
    readonly System.Windows.Forms.Timer timer;
    readonly EventWaitHandle showEvt, searchEvt;
    int ticks;

    public NovaContext(bool startedWithWindows)
    {
        // Nova's window thread: every window must be created here. A WinForms context is installed explicitly — before, a plain
        // context could slip in, and windows Nova opened by itself (Settings at start, a window after a restart) were created on a
        // background thread and never responded.
        var wfc = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(wfc);
        Program.UiContext = wfc;
        Program.Marshal = new Control();
        Program.Marshal.CreateControl();
        _ = Program.Marshal.Handle;
        UiWatch.Start();
        Paths.Ensure();
        Store.Load();
        // 🛡 Two starts that never finished → safe mode (background jobs off, you switch them on one by one).
        Guard.BeginStart();
        Startup.Apply();
        ExplorerMenu.Apply();

        tray = new NotifyIcon { Icon = Th.AppIcon, Text = "Nova Enhance", Visible = true };
        Program.Tray = tray;
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
                var t = IndexProgress.Tray();
                var o = Projects.Overall();
                if (o.Names > 0)
                    t += "\n📁 " + (o.Listing ? "listing…" : $"{o.Names:N0} names ✓") + $" · {o.Read:N0} read" + (Projects.ReadAll.Line() is { Length: > 0 } ra ? " · " + ra : "");
                tray.Text = t.Length > 127 ? t[..127] : t;
            }
            catch { }
        };
        trayTip.Start();
        var m = new ContextMenuStrip { Font = Th.F(10) };
        m.Items.Add("✦  Enhance selected text      Ctrl+Shift+E", null, (_, _) => Screens.Enhance());
        m.Items.Add("💬  Nova Chat & AI Hub        Ctrl+Shift+A", null, (_, _) => Screens.Hub(null));
        m.Items.Add("📌  My Work", null, (_, _) => Screens.Work(null));
        m.Items.Add("📂  Find a project file…", null, (_, _) => Screens.FindFile(null));
        m.Items.Add("📄  Nova for this PDF                Ctrl+Shift+D", null, (_, _) => PdfCompanion.Open());
        m.Items.Add("📂  Archive reader (.pst)…", null, (_, _) => Screens.PstReader(null));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("📁  Search inside a folder…", null, (_, _) => Screens.FolderSearch(null));
        m.Items.Add("🔍  Search for email                Ctrl+Shift+F", null, (_, _) => Screens.Ask());
        m.Items.Add($"💬  Talk about this email          Ctrl+Alt+{Store.Str("win_hk_mailask", "Q")}", null, (_, _) => Screens.MailAsk(null));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("☀  Morning brief", null, (_, _) => Screens.Brief());
        m.Items.Add("🕘  Chat history", null, (_, _) => Screens.ChatHistory());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("⚙  Settings", null, (_, _) => Screens.Settings());
        m.Items.Add("📁  Open the Nova Enhance folder", null, (_, _) => Shell.Open(Paths.Root));
        m.Items.Add("⬆  Check for updates", null, (_, _) => Screens.Settings("Updates"));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Exit Nova Enhance", null, (_, _) => Quit());
        tray.ContextMenuStrip = m;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Screens.Ask(); };

        hot = new HotkeyWindow();
        hot.Register(1, Keys.E, Screens.Enhance, "Ctrl+Shift+E");
        hot.Register(2, Keys.F, Screens.Ask, "Ctrl+Shift+F");
        hot.Register(3, Keys.A, Screens.HubSelection, "Ctrl+Shift+A");
        hot.Register(4, Keys.D, () => PdfCompanion.Open(), "Ctrl+Shift+D");
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
            // 🛡 Restarted by the Guardian: say so, keep the report, reopen the window you had.
            if (Program.Restored)
            {
                var what = Guard.TakeCrashReport();
                tray.ShowBalloonTip(10000, "🛡 Nova Enhance restarted", (what.Length > 0 ? what.Split('\n').Last().Trim() : "Nova closed unexpectedly") + " — the details are in Settings → Error log.", ToolTipIcon.Warning);
                switch (Guard.LastScreenName())
                {
                    case "ask": Screens.Ask(); break;
                    case "find": Screens.FindFile(null); break;
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
            UiWatch.Step("idle");
            if (ticks++ % 60 == 0 && Updater.Due) { await Updater.Check(); Updater.Offer(false); }
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
        // (2.0) The night check of moved emails was removed: it crashed Nova (build 75–76). Moved emails are found when you open them.
        Guard.Stage("phone link"); PhoneLink.Start();
        Guard.Stage("started");
    }

    void Quit()
    {
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

    public static async void Enhance()
    {
        try
        {
            var (h, text) = await Selection.Grab();
            if (enh == null || enh.IsDisposed) enh = new EnhanceForm();
            enh.Start(h, text);
        }
        catch (Exception e) { Log.Error("enhance window", e); }
    }

    static MailAskForm mailAsk;

    /// <summary>✦ Ask Nova about this email (h = an email from a Nova list; null = the email selected in Outlook).</summary>
    public static void MailAsk(MailHit h, string subjectHint = "")
    {
        if (Program.OffUi(() => MailAsk(h, subjectHint))) return;
        if (mailAsk == null || mailAsk.IsDisposed) mailAsk = new MailAskForm();
        Show(mailAsk);
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
    public static void FindFile(string q)
    {
        if (Program.OffUi(() => FindFile(q))) return;
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
    public static void ChatHistory()
    {
        if (Program.OffUi(ChatHistory)) return;
        if (history == null || history.IsDisposed) history = new ChatHistoryForm();
        else history.Reload();
        Show(history);
    }

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
