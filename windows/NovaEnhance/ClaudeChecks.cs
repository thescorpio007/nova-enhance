using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🔎 What Claude may check and fix in Nova — over the relay (claude.ai) and in the Claude desktop app (🔌 connector): health, indexes, phone /
/// updates / settings, a self-test on this PC, and three safe fixes. Only while you allow Claude; every call is in Activity; 🔒 private
/// paths, passwords, keys and secrets are never shown; nothing is deleted. Designed by A.HAMROUNI
/// </summary>
static class ClaudeChecks
{
    static bool Priv(string s) => Projects.Private.IsMatch(s ?? "") || (s ?? "").Contains("TrustWalletBackup", StringComparison.OrdinalIgnoreCase);
    static string P(string path) => Priv(path) ? "🔒 (private)" : path;
    static string Size(long b) => Transfer.Size(b);

    // ------------------------------------------------------------------ 1 · health

    public static async Task<string> Errors(JsonObject a)
    {
        var sb = new StringBuilder("🩺 Nova health · " + DateTime.Now.ToString("dd MMM HH:mm") + " · " + Program.Shown + "\n");
        sb.Append($"Safe mode: {(Guard.SafeMode ? "ON — " + Guard.SafeReason : "off")} · background jobs: {Guard.JobsLine()}\n");
        if (Guard.Restarts.Count > 0) sb.Append("Jobs restarted after a failure: " + string.Join(", ", Guard.Restarts.Select(r => $"{r.Key} ×{r.Value}")) + "\n");
        // grouped: which areas fail, how often, the newest message
        var hours = a["hours"] is { } h && int.TryParse(h.ToString(), out var hh) ? Math.Clamp(hh, 1, 720) : 24;
        var list = Log.ReadAll().Where(x => x.Time > DateTime.Now.AddHours(-hours)).ToList();
        sb.Append($"\nBy area (last {hours} h, {list.Count} entries):\n");
        foreach (var g in list.GroupBy(x => x.Area).OrderByDescending(g => g.Count()).Take(15))
            sb.Append($"- {g.Key}: {g.Count()} · newest {g.First().Time:HH:mm} {Cut(g.First().Message, 160)}\n");
        sb.Append(NetStops.Report());
        sb.Append("\n").Append(await TestConnector.Tool("errors", new JsonObject { ["hours"] = hours }));
        return sb.ToString();
    }

    public static string Perf()
    {
        var sb = new StringBuilder("⚙ Nova performance · " + DateTime.Now.ToString("dd MMM HH:mm") + "\n");
        try
        {
            using var me = Process.GetCurrentProcess();
            var up = DateTime.Now - me.StartTime;
            sb.Append($"Nova: running {(int)up.TotalHours} h {up.Minutes} min · memory {Size(me.WorkingSet64)} (private {Size(me.PrivateMemorySize64)}) · CPU time {me.TotalProcessorTime.TotalMinutes:0.0} min ({100 * me.TotalProcessorTime.TotalSeconds / Math.Max(1, up.TotalSeconds) / Environment.ProcessorCount:0.0} % average) · threads {me.Threads.Count} · handles {me.HandleCount}\n");
        }
        catch (Exception e) { sb.Append("process: " + e.Message + "\n"); }
        void Dir(string name, string path)
        {
            try { if (Directory.Exists(path)) sb.Append($"{name}: {Size(new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length))}\n"); } catch { }
        }
        Dir("Email index (on this PC)", Paths.MailIndexLocal);
        Dir("Nova documents folder", Paths.Root);
        Dir("Cache", Paths.Cache);
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            sb.Append($"Disk {d.Name.TrimEnd('\\')}: {Size(d.AvailableFreeSpace)} free of {Size(d.TotalSize)}{(d.AvailableFreeSpace < 10L << 30 ? "  ⚠ low" : "")}\n");
        try
        {
            var fz = Path.Combine(Paths.Logs, "ui-freeze.txt");
            if (File.Exists(fz))
            {
                var lines = File.ReadAllLines(fz).Where(l => l.Trim().Length > 0).ToList();
                var today = lines.Count(l => l.Contains(DateTime.Now.ToString("yyyy-MM-dd")));
                sb.Append($"\nWindow freezes recorded: {lines.Count} in total, {today} today. Slowest steps (newest last):\n");
                foreach (var l in lines.TakeLast(10)) sb.Append("   ").Append(Cut(l, 220)).Append('\n');
            }
            else sb.Append("\nNo window freeze recorded.\n");
        }
        catch { }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 2 · indexes

    public static string ProjectsIndex()
    {
        var sb = new StringBuilder("📁 Project file index · " + DateTime.Now.ToString("dd MMM HH:mm") + $" · indexing {(Projects.Indexer.On ? "on" : "OFF")} · VPN {OfficeVpn.Now}\n");
        var o = Projects.Overall();
        sb.Append($"Overall: {o.Names:N0} names · {o.Read:N0} read inside{(o.Listing ? " · " + o.ListingText : "")}\n");
        foreach (var p in Projects.All())
        {
            sb.Append($"\n■ {p.Code} · {p.Name}\n");
            foreach (var f in p.Folders.Concat((p.Sources ?? new()).Select(x => x.Path)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Priv(f)) { sb.Append("   🔒 (private folder)\n"); continue; }
                var st = Projects.Status.TryGetValue(f, out var s) ? s : null;
                var net = f.StartsWith(@"\\") || ProjectSources.IsNetworkDrive(f);
                var reach = net ? (ProjectSources.Reach(f) ? "reachable" : "⚠ NOT reachable (VPN?)") : "local";
                var src = ProjectSources.Of(f) is { } x ? $" [{x.S.Name} · {x.S.Kind} · {(x.S.Depth == "text" ? "names + text" : "names only")}{(x.S.Offline ? " · offline copy" : "")}]" : "";
                sb.Append($"   {f}{src} · {reach} · " + (st == null ? "waiting" : $"{st.Phase} · {st.Files:N0} names{(st.Expected > 0 && st.Phase == "listing" ? $" of ~{st.Expected:N0}" : "")} · {st.Read:N0} read · last {(st.Last == default ? "—" : st.Last.ToString("dd MMM HH:mm"))}") + "\n");
            }
        }
        if (Projects.General.Count > 0) sb.Append("\nGeneral reference: " + string.Join(" · ", Projects.General.Select(P)) + "\n");
        sb.Append($"\n📖 Read inside (background): {(Projects.ReadAll.On ? "on" : "off")}{(Projects.ReadAll.Paused ? " · paused" : "")} · {Projects.ReadAll.Phase}" +
                  (Projects.ReadAll.Current.Length > 0 ? " · now: " + P(Projects.ReadAll.Current) : "") + (Projects.ReadAll.Left is { } l ? $" · about {(int)l.TotalMinutes} min left" : "") +
                  (Projects.ReadAll.Error.Length > 0 ? " · ⚠ " + Projects.ReadAll.Error : "") + "\n");
        foreach (var c in Projects.ReadAll.Counts) sb.Append($"   {c.Code}: {c.Done:N0} of {c.Total:N0} ({(c.Total > 0 ? 100L * c.Done / c.Total : 100)} %)\n");
        sb.Append($"⚡ Offline copies: {(ProjectSources.Offline.Line.Length > 0 ? ProjectSources.Offline.Line : "none running")}\n");
        var errs = Log.ReadAll().Where(e => e.Time > DateTime.Now.AddHours(-24) && Regex.IsMatch(e.Area, "project|read all|sources|offline", RegexOptions.IgnoreCase)).Take(10).ToList();
        if (errs.Count > 0) { sb.Append("\nLast index problems (24 h):\n"); foreach (var e in errs) sb.Append("   " + Cut(Log.Format(e).Split('\n')[0], 220) + "\n"); }
        return sb.ToString();
    }

    public static async Task<string> Jobs()
    {
        var sb = new StringBuilder("🔄 Background jobs · " + DateTime.Now.ToString("dd MMM HH:mm") + "\n");
        sb.Append($"Guardian: {Guard.JobsLine()}\n{Projects.Indexer.Line()}\n{Projects.ReadAll.Line()}\nOffline copies: {ProjectSources.Offline.Line}\n");
        sb.Append($"Phone link: {(PhoneLink.On ? "on" : "off")} · last {PhoneLink.LastRun:dd MMM HH:mm} · {PhoneLink.Status} {PhoneLink.LastError}\n");
        sb.Append($"Transfers: {Transfer.Jobs.Count(j => j.State is "running" or "queued" or "waiting" or "scanning")} active\n\n");
        sb.Append(await TestConnector.Tool("status", new JsonObject()));
        var recent = Log.ReadAll().Where(e => e.Time > DateTime.Now.AddHours(-24) && Regex.IsMatch(e.Area + " " + e.Message, "index|sync|self-check|check", RegexOptions.IgnoreCase)).Take(12).ToList();
        if (recent.Count > 0) { sb.Append("\nEmail index notes (24 h):\n"); foreach (var e in recent) sb.Append("   " + Cut(Log.Format(e).Split('\n')[0], 220) + "\n"); }
        return sb.ToString();
    }

    /// <summary>Searches like you would: the files (document number or words in the names) and the emails, nothing opened or changed.</summary>
    public static async Task<string> SearchTest(JsonObject a)
    {
        var q = a["q"]?.ToString()?.Trim() ?? a["text"]?.ToString()?.Trim() ?? "";
        if (q.Length < 2) throw new Exception("give q (a document number or words)");
        if (Priv(q)) return "🔒 private words are never searched for Claude.";
        var sb = new StringBuilder($"🔎 Search test “{q}” · {DateTime.Now:dd MMM HH:mm}\n");
        var sw = Stopwatch.StartNew();
        var folders = Projects.All().SelectMany(p => p.Folders.Concat((p.Sources ?? new()).Select(x => x.Path))).Concat(Projects.General).Where(f => !Priv(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var hits = await Task.Run(() => Projects.FindDoc(q, folders));
        sb.Append($"\nFiles ({hits.Count}, {sw.ElapsedMilliseconds} ms):\n");
        foreach (var h in hits.Where(h => !Priv(h.Path)).Take(15)) sb.Append($"   {(h.Status == "latest" ? "★" : " ")} {h.Path}{(h.Rev.Length > 0 ? " · rev " + h.Rev : "")}{(h.Cad ? " · CAD" : "")}\n");
        sb.Append("\nEmails:\n").Append(await TestConnector.Tool("find_email", new JsonObject { ["text"] = q, ["limit"] = 15 }));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 3 · phone · updates · settings

    public static string Phone() =>
        $"📱 Phone link: {(PhoneLink.On ? "on" : "off")} · last sync {PhoneLink.LastRun:dd MMM HH:mm} ({(PhoneLink.LastRun == DateTime.MinValue ? "never" : (int)(DateTime.Now - PhoneLink.LastRun).TotalMinutes + " min ago")}) · {PhoneLink.Status}" +
        (PhoneLink.LastError.Length > 0 ? " · ⚠ " + PhoneLink.LastError : "") + $"\nPC build: {Program.Shown} — the phone should have the same build number.";

    public static string Updates()
    {
        var w = Updater.Waiting();
        return $"⬆ Updates · this PC {Program.Shown}\nStatus: {Updater.Status}\nCheck due: {(Updater.Due ? "yes" : "no")}\n" +
               (w != null ? $"Waiting to install: build {w.Build} · {Path.GetFileName(w.File)}\n{Cut(w.Notes ?? "", 600)}" : "No update waiting.");
    }

    /// <summary>Your settings without anything secret: keys, passwords, PINs, OTP, tokens and encrypted values are left out.</summary>
    public static string Settings(JsonObject a)
    {
        var only = a["filter"]?.ToString() ?? "";
        var sb = new StringBuilder($"⚙ Nova settings (nothing secret) · {Program.Shown}\n");
        foreach (var (k, v) in Store.Snapshot().OrderBy(x => x.Key))
        {
            if (Regex.IsMatch(k, @"^key_|_pw$|_pw_|pass|secret|token|otp|pin|admin|cookie|_key$|oauth|refresh|session", RegexOptions.IgnoreCase)) continue;
            var s = v?.ToJsonString() ?? "";
            if (s.Contains("dpapi:")) continue;
            if (only.Length > 0 && !k.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            if (Priv(s)) s = "🔒 (private)";
            sb.Append($"{k} = {Cut(s, 160)}\n");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 4 · self-test and safe fixes

    public static async Task<string> SelfTest()
    {
        var sb = new StringBuilder($"🧪 Nova self-test on this PC · {DateTime.Now:dd MMM HH:mm} · {Program.Shown}\n\n");
        sb.Append(await TestConnector.Tool("selfcheck", new JsonObject()));
        void R(string what, Func<string> f) { try { sb.Append("✓ " + what + ": " + f() + "\n"); } catch (Exception e) { sb.Append("✗ " + what + ": " + e.Message + "\n"); } }
        R("settings file", () => { Store.Load(); return "readable"; });
        R("logs folder", () => { var f = Path.Combine(Paths.Logs, "selftest.tmp"); File.WriteAllText(f, "ok"); File.Delete(f); return "writable"; });
        R("📦 transfer engine", () => Transfer.SelfTest());
        R("🌐 Office VPN", () => $"{OfficeVpn.Now} · {OfficeVpn.Line} · helper {VpnHelper.State()} · engine {(OfficeVpn.Cli() ?? "not installed")}");
        R("📁 project index", () => { var o = Projects.Overall(); return $"{o.Names:N0} names · {o.Read:N0} read{(o.Listing ? " · listing" : "")}"; });
        R("🛡 check before sending", () => SendCheck.On ? SendCheck.Status : "off");
        R("disk", () => string.Join(" · ", DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => $"{d.Name.TrimEnd('\\')} {Size(d.AvailableFreeSpace)} free")));
        return sb.ToString();
    }

    /// <summary>🛠 Safe fix: connect · disconnect · reconnect the office VPN.</summary>
    public static string FixVpn(JsonObject a)
    {
        var act = a["action"]?.ToString() ?? "";
        switch (act)
        {
            case "connect": OfficeVpn.On = true; OfficeVpn.Connect(); return "🟡 connecting — run nova.vpn in ~20 s to see the result";
            case "disconnect": OfficeVpn.Disconnect(); return "■ disconnecting";
            case "reconnect": Task.Run(() => { OfficeVpn.Disconnect(); Thread.Sleep(4000); OfficeVpn.Connect(); }); return "↻ reconnecting — run nova.vpn in ~25 s";
            default: throw new Exception("action: connect · disconnect · reconnect");
        }
    }

    /// <summary>🛠 Safe fix: list a project folder again (or all), like “List again now”.</summary>
    public static string FixRelist(JsonObject a)
    {
        var part = a["folder"]?.ToString()?.Trim() ?? "";
        if (part.Length == 0) { Projects.Indexer.Refresh(); return "⟳ every project folder will be listed again (names only — nothing is deleted)"; }
        var n = Projects.Indexer.Relist(part);
        return n == 0 ? "no project folder contains “" + part + "”" : $"⟳ {n} folder(s) will be listed again";
    }

    /// <summary>🛠 Safe fix: retry failed files · resume · pause a transfer (never cancel or delete).</summary>
    public static string FixTransfer(JsonObject a)
    {
        var id = a["id"]?.ToString() ?? ""; var act = a["action"]?.ToString() ?? "retry";
        var j = Transfer.Jobs.FirstOrDefault(x => x.Id == id) ?? (id.Length == 0 ? Transfer.Jobs.Where(x => x.State == "failed").OrderByDescending(x => x.Added).FirstOrDefault() : null);
        if (j == null) throw new Exception("no such transfer (ids are in nova.transfers)");
        switch (act)
        {
            case "retry": Transfer.RetryFailed(j); return $"↻ {j.Failed.Count} failed file(s) of “{j.Title}” queued again";
            case "resume": Transfer.Resume(j); return "▶ resumed: " + j.Title;
            case "pause": Transfer.Pause(j); return "⏸ paused: " + j.Title;
            default: throw new Exception("action: retry · resume · pause");
        }
    }

    // ------------------------------------------------------------------ 📐 filing labels · 📁 folder tree (names only, read-only)

    static Project Proj(JsonObject a)
    {
        var code = a["project"]?.ToString() ?? "";
        return Projects.All().FirstOrDefault(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? throw new Exception("give project (one of: " + string.Join(", ", Projects.All().Select(p => p.Code)) + ")");
    }

    static List<string> Names(Project p) =>
        Projects.Catalogue(p.Folders.Concat((p.Sources ?? new()).Select(s => s.Path)).Where(f => !Priv(f)), 150000).Select(x => x.Path).Where(x => !Priv(x)).ToList();

    public static string FilingCheck(JsonObject a)
    {
        var p = Proj(a);
        var q = (a["q"]?.ToString() ?? "").Trim().ToLowerInvariant();
        var sw = Stopwatch.StartNew();
        var files = Names(p);
        var labs = files.Select(f => (f, l: Filing.Classify(f))).ToList();
        var sb = new StringBuilder($"📐 Filing labels · {p.Code} · {files.Count:N0} files · {sw.ElapsedMilliseconds} ms · {Program.Shown}\n");
        void Count(string title, Func<FileLabels, string> k)
        {
            sb.Append($"\n{title}: ");
            sb.Append(string.Join(" · ", labs.GroupBy(x => k(x.l) is { Length: > 0 } v ? v : "(none)").OrderByDescending(g => g.Count()).Take(18).Select(g => $"{g.Key} {g.Count():N0}")));
        }
        Count("Kind", l => l.Kind); Count("Source", l => l.Source); Count("Discipline", l => l.Disc); Count("Stage", l => Regex.Replace(l.Stage, " · .*", "")); Count("Review", l => l.Code); Count("State", l => l.State);
        var unk = Filing.Learn(p.Code, files);
        sb.Append($"\n\n❓ Codes not known yet ({unk.Count}): ").Append(string.Join(" · ", unk.Take(30).Select(u => $"{u.Code} ({u.Role}, {u.Count}){(u.Suggest.Length > 0 ? " → " + u.Suggest : "")}")));
        var tpl = Filing.TemplateOf(p.Code);
        sb.Append("\nTemplate: ").Append(tpl != null ? string.Join(" ", tpl) : "(none)");
        var sample = (q.Length > 0 ? labs.Where(x => x.f.ToLowerInvariant().Contains(q)) : labs.Where((_, i) => i % Math.Max(1, labs.Count / 25) == 0)).Take(25);
        sb.Append("\n\nSamples:\n");
        foreach (var (f, l) in sample)
            sb.Append($"• {Cut(f, 170)}\n   → {string.Join(" · ", new[] { l.Kind, l.Source, l.Disc, l.Stage, l.Rev, l.Code, l.State }.Where(x => x.Length > 0))}\n   why: {string.Join(" | ", l.Why)}\n");
        return sb.ToString();
    }

    public static string Tree(JsonObject a)
    {
        var p = Proj(a);
        int depth = Math.Clamp(a["depth"]?.GetValue<int>() ?? 3, 1, 6), samples = Math.Clamp(a["samples"]?.GetValue<int>() ?? 5, 0, 20);
        var files = Names(p);
        var roots = p.Folders.Concat((p.Sources ?? new()).Select(s => s.Path)).Select(f => f.TrimEnd('\\') + "\\").ToList();
        var nodes = new SortedDictionary<string, (int Count, List<string> Samples)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var root = roots.FirstOrDefault(r => f.StartsWith(r, StringComparison.OrdinalIgnoreCase)) ?? "";
            var parts = f[root.Length..].Split('\\');
            var key = root + string.Join("\\", parts.Take(Math.Min(depth, parts.Length - 1)));
            if (!nodes.TryGetValue(key, out var n)) n = (0, new List<string>());
            n.Count++;
            if (n.Samples.Count < samples) n.Samples.Add(parts[^1]);
            nodes[key] = n;
        }
        var sb = new StringBuilder($"📁 {p.Code} · {files.Count:N0} files · depth {depth}\n");
        foreach (var (k, v) in nodes.Take(900))
            sb.Append($"{k}  ({v.Count:N0})\n").Append(samples > 0 ? string.Concat(v.Samples.Select(x => "      · " + Cut(x, 120) + "\n")) : "");
        if (nodes.Count > 900) sb.Append($"… {nodes.Count - 900} more folders — use a smaller depth");
        return sb.ToString();
    }

    /// <summary>🧩 Every Office add-in registered for this user / this PC (COM + web), read-only.</summary>
    public static string Addins()
    {
        var sb = new StringBuilder($"🧩 Office add-ins · {Program.Shown}\n");
        foreach (var app in new[] { "Outlook", "Word", "Excel", "PowerPoint" })
        {
            sb.Append($"\n— {app} (COM add-ins)\n");
            foreach (var (hive, hn) in new[] { (Microsoft.Win32.Registry.CurrentUser, "this user"), (Microsoft.Win32.Registry.LocalMachine, "this PC") })
                foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
                {
                    try
                    {
                        using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive == Microsoft.Win32.Registry.CurrentUser ? Microsoft.Win32.RegistryHive.CurrentUser : Microsoft.Win32.RegistryHive.LocalMachine, view);
                        using var k = root.OpenSubKey($@"Software\Microsoft\Office\{app}\Addins");
                        if (k == null) continue;
                        foreach (var n in k.GetSubKeyNames())
                        {
                            using var a = k.OpenSubKey(n);
                            var name = a?.GetValue("FriendlyName")?.ToString() ?? n;
                            var load = a?.GetValue("LoadBehavior")?.ToString() ?? "?";
                            var desc = a?.GetValue("Description")?.ToString() ?? "";
                            var loaded = load is "3" or "9" or "16" ? "loads at start" : load is "2" or "0" ? "OFF" : "load " + load;
                            sb.Append($"   • {name}  [{n}] · {loaded} · {hn}, {(view == Microsoft.Win32.RegistryView.Registry64 ? "64-bit" : "32-bit")}{(desc.Length > 0 ? " · " + Cut(desc, 90) : "")}\n");
                        }
                    }
                    catch (Exception e) { sb.Append($"   ⚠ {hn}: {e.Message}\n"); }
                }
            // add-ins Office turned off after a crash
            try
            {
                using var dis = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Office\16.0\{app}\Resiliency\DisabledItems");
                if (dis != null && dis.GetValueNames().Length > 0) sb.Append($"   ⚠ {dis.GetValueNames().Length} item(s) disabled by Office after a crash\n");
            }
            catch { }
        }
        // web add-ins (manifests): sideloaded (Developer) + the ones Office downloaded
        sb.Append("\n— Web add-ins (manifests)\n");
        try
        {
            using var dev = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\WEF\Developer");
            if (dev != null) foreach (var v in dev.GetValueNames()) sb.Append($"   • sideloaded: {v} → {Cut(dev.GetValue(v)?.ToString() ?? "", 140)}\n");
        }
        catch { }
        var wef = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Office\16.0\Wef");
        try
        {
            if (Directory.Exists(wef))
                foreach (var f in Directory.EnumerateFiles(wef, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("manifest", StringComparison.OrdinalIgnoreCase)).Take(200))
                {
                    string txt; try { txt = File.ReadAllText(f); } catch { continue; }
                    var dn = Regex.Match(txt, @"<DisplayName\s+DefaultValue=""([^""]+)""");
                    if (!dn.Success) continue;
                    var prov = Regex.Match(txt, @"<ProviderName>([^<]+)<");
                    var tabs = Regex.Matches(txt, @"(?:Label|Tab\.Label|TabLabel)[^>]*DefaultValue=""([^""]{1,40})""").Select(m => m.Groups[1].Value).Distinct().Take(8);
                    sb.Append($"   • {dn.Groups[1].Value}{(prov.Success ? " · by " + prov.Groups[1].Value : "")} · labels: {string.Join(", ", tabs)}\n");
                }
        }
        catch (Exception e) { sb.Append("   ⚠ " + e.Message + "\n"); }
        // Nova's own tab
        try { sb.Append($"\n— Nova's own ✦ Nova tab: {(OfficeRibbon.Installed ? "installed" : "not installed")}\n"); } catch { }
        return sb.ToString();
    }

    static string Cut(string s, int n) => s == null ? "" : s.Length <= n ? s : s[..n] + "…";

    /// <summary>📋 The Submittal log run as you would: the rows found and the reason a row is missing (read only).</summary>
    public static string LogTest(JsonObject a)
    {
        var q = a["q"]?.ToString() ?? "";
        var o = new SubmittalLog.Options();
        var proj = a["project"]?.ToString() ?? "";
        if (proj.Length > 0) o.Projects.Add(proj);
        if (int.TryParse(a["days"]?.ToString(), out var days) && days > 0) o.From = DateTime.Today.AddDays(-days);
        var parsed = "";
        if (a["text"]?.ToString() is { Length: > 0 } tx)
        {
            // 🔎 the same “Filter to my text” as the log window: the words become the filters
            var first = SubmittalLog.Run(o);
            var p = SubmittalForm.ParseWith(o, first.Discs.Keys, tx, new(), new());
            o = p.O; q = tx;
            parsed = "Parsed: " + string.Join(" · ", p.Picks.OfType<JsonObject>().Where(x => x["add"] == null).Select(x => x["label"]?.ToString())) + "\nFilters: " + SubmittalForm.Describe(o) + "\n";
        }
        else if (q.Length > 0) o.Words.AddRange(q.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var r = SubmittalLog.Run(o);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📋 Submittal log test “{q}”{(proj.Length > 0 ? " · " + proj : "")} · {r.Seconds:0.0} s · {r.Mails:N0} emails read · {r.Items.Count} row(s) · period {r.Opt.From:dd MMM yyyy} → {r.Opt.To:dd MMM yyyy}");
        sb.Append(parsed);
        sb.AppendLine("Types: " + string.Join(", ", r.Opt.Types.DefaultIfEmpty("(every enabled type)")));
        foreach (var i in r.Items.Take(30))
            sb.AppendLine($"- {i.Project} · {i.Type} · {i.No} Rev{i.RevNum:00} · {i.Title} · submitted {i.Submitted:dd MMM yyyy} · reply {i.Reply:dd MMM yyyy} · {i.Code} · {i.Status} · {i.Emails} email(s){(i.Check.Length > 0 ? " · check: " + i.Check : "")}");
        if (r.Gaps.Count > 0) sb.AppendLine("Gaps: " + string.Join(" | ", r.Gaps.Take(5)));
        return sb.ToString();
    }
}
