using Microsoft.Win32;

namespace NovaEnhance;

/// <summary>
/// ✦ The Nova tab in every desktop Office program (like Classic Menu): a small native add-in (NovaRibbon64.dll / NovaRibbon32.dll,
/// installed next to NovaEnhance.exe) registered for this Windows user only — no admin rights, no certificate.
/// Both the 64-bit and the 32-bit registry views are written, so it works whatever Office you have. Designed by A.HAMROUNI
/// </summary>
static class OfficeRibbon
{
    const string Clsid = "{6E7A2D41-3C9B-4F1E-8A52-1D9C7B3E5F02}";
    const string ProgId = "NovaEnhance.Ribbon";
    /// <summary>Office program → its key under Software\Microsoft\Office.</summary>
    public static readonly (string Key, string Name)[] Apps =
    {
        ("Word", "Word"), ("Excel", "Excel"), ("PowerPoint", "PowerPoint"), ("Outlook", "Outlook"),
        ("Visio", "Visio"), ("MS Project", "Project"), ("Access", "Access"), ("Publisher", "Publisher")
    };

    /// <summary>The ✦ Nova tab's buttons (ids in NovaRibbon.cpp); each one is a named event Nova listens to.</summary>
    public static readonly string[] Buttons =
        { "mode_grammar", "mode_shorten", "mode_professional", "mode_informal", "mode_expand", "mode_translate", "enhance", "mailask", "search", "hub", "brief", "files", "settings" };

    /// <summary>The files that come with Nova (next to NovaEnhance.exe).</summary>
    static string Src => AppContext.BaseDirectory;
    public static bool FilesThere => File.Exists(Path.Combine(Src, "NovaRibbon64.dll")) && File.Exists(Path.Combine(Src, "NovaRibbon32.dll"));
    /// <summary>Office loads a copy kept per build (…\Nova Enhance\ribbon\ + the build number), so an update never has to close Word or Outlook.</summary>
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova Enhance", "ribbon", Program.Build.ToString());
    static string Dll64 => Path.Combine(Dir, "NovaRibbon64.dll");
    static string Dll32 => Path.Combine(Dir, "NovaRibbon32.dll");

    public static bool Installed
    {
        get
        {
            try
            {
                using var k = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64).OpenSubKey($@"Software\Microsoft\Office\Word\Addins\{ProgId}");
                return k != null && (k.GetValue("LoadBehavior") as int? ?? 0) == 3;
            }
            catch { return false; }
        }
    }

    /// <summary>Registers the add-in for this user in every Office program (64- and 32-bit Office).</summary>
    public static void Install()
    {
        if (!FilesThere) throw new FileNotFoundException("The Nova tab files are missing next to NovaEnhance.exe — your antivirus may have held them during Setup. Allow the Nova Enhance folder in the antivirus, then run Setup again.");
        Directory.CreateDirectory(Dir);
        foreach (var n in new[] { "NovaRibbon64.dll", "NovaRibbon32.dll" })
            if (!File.Exists(Path.Combine(Dir, n)) || new FileInfo(Path.Combine(Dir, n)).Length != new FileInfo(Path.Combine(Src, n)).Length) try { File.Copy(Path.Combine(Src, n), Path.Combine(Dir, n), true); }
                catch (Exception e) { throw new IOException($"Windows or the antivirus refused to copy {n} ({e.Message}). Allow the Nova Enhance folders in the antivirus, then try again."); }
        foreach (var (view, dll) in new[] { (RegistryView.Registry64, Dll64), (RegistryView.Registry32, Dll32) })
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using (var c = hk.CreateSubKey($@"Software\Classes\CLSID\{Clsid}"))
            {
                c.SetValue("", "Nova Enhance ribbon");
                using (var s = c.CreateSubKey("InprocServer32")) { s.SetValue("", dll); s.SetValue("ThreadingModel", "Apartment"); }
                using (var p = c.CreateSubKey("ProgID")) p.SetValue("", ProgId);
            }
            using (var p = hk.CreateSubKey($@"Software\Classes\{ProgId}")) { p.SetValue("", "Nova Enhance ribbon"); using var c = p.CreateSubKey("CLSID"); c.SetValue("", Clsid); }
            foreach (var (key, _) in Apps)
                using (var a = hk.CreateSubKey($@"Software\Microsoft\Office\{key}\Addins\{ProgId}"))
                {
                    a.SetValue("FriendlyName", "✦ Nova Enhance");
                    a.SetValue("Description", "The ✦ Nova tab: fix, shorten, rewrite and translate the selection, search email, ask Nova. Designed by A.HAMROUNI");
                    a.SetValue("LoadBehavior", 3, RegistryValueKind.DWord);
                }
            // Office may have switched it off after a slow start or an error: clear that, so it loads again.
            foreach (var (key, _) in Apps)
                try
                {
                    foreach (var ver in new[] { "16.0", "15.0" })
                    {
                        using var dis = hk.OpenSubKey($@"Software\Microsoft\Office\{ver}\{key}\Resiliency\DisabledItems", true);
                        if (dis == null) continue;
                        foreach (var n in dis.GetValueNames())
                            if (dis.GetValue(n) is byte[] b && System.Text.Encoding.Unicode.GetString(b).Contains("NovaRibbon", StringComparison.OrdinalIgnoreCase)) dis.DeleteValue(n, false);
                    }
                    using var dnd = hk.CreateSubKey($@"Software\Microsoft\Office\16.0\{key}\Resiliency\DoNotDisableAddinList");
                    dnd.SetValue(ProgId, 1, RegistryValueKind.DWord);
                }
                catch { }
        }
        Store.SetBool("win_ribbon", true);
        Store.Set("win_ribbon_dir", Dir);
        // Older builds' copies: removed when no Office program uses them any more.
        try
        {
            foreach (var old in Directory.GetDirectories(Path.GetDirectoryName(Dir)!).Where(d => !d.Equals(Dir, StringComparison.OrdinalIgnoreCase)))
                try { Directory.Delete(old, true); } catch { }
        }
        catch { }
    }

    public static void Uninstall()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            foreach (var (key, _) in Apps)
            {
                try { hk.DeleteSubKeyTree($@"Software\Microsoft\Office\{key}\Addins\{ProgId}", false); } catch { }
                try { using var dnd = hk.OpenSubKey($@"Software\Microsoft\Office\16.0\{key}\Resiliency\DoNotDisableAddinList", true); dnd?.DeleteValue(ProgId, false); } catch { }
            }
            try { hk.DeleteSubKeyTree($@"Software\Classes\CLSID\{Clsid}", false); } catch { }
            try { hk.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false); } catch { }
        }
        Store.SetBool("win_ribbon", false);
    }

    /// <summary>After an update or a move: the registration follows the files (only when it was installed).</summary>
    public static void Refresh()
    {
        try { if (Store.Bool("win_ribbon", false) && FilesThere && (!Installed || Store.Str("win_ribbon_dir") != Dir)) Install(); }
        catch (Exception e) { Log.Warn("Office tab", e.Message); }
    }
}
