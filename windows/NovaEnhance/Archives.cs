using System.Text.Json;

namespace NovaEnhance;

/// <summary>
/// 📂 Outlook archive files (.pst) that are not open in Outlook: Nova opens each one for indexing, then closes it again so your
/// folder list stays short (option B). The emails stay searchable in Nova; "Open in Outlook" re-opens the archive when needed.
/// Designed by A.HAMROUNI
/// </summary>
static class Archives
{
    static List<string> Load(string k) { try { return JsonSerializer.Deserialize<List<string>>(Store.Str(k, "[]")) ?? new(); } catch { return new(); } }
    static Dictionary<string, string> Map(string k) { try { return JsonSerializer.Deserialize<Dictionary<string, string>>(Store.Str(k, "{}")) ?? new(); } catch { return new(); } }

    /// <summary>The archive files you added (Settings → Email &amp; AI → Mailboxes and folders).</summary>
    public static List<string> Extra { get => Load("win_pst_extra"); set => Store.Set("win_pst_extra", JsonSerializer.Serialize(value ?? new())); }
    /// <summary>path → "size|last write" when it was completely indexed (re-indexed only if the file changes).</summary>
    static Dictionary<string, string> Done { get => Map("win_pst_done"); set => Store.Set("win_pst_done", JsonSerializer.Serialize(value)); }
    /// <summary>Outlook store id → file (to re-open it for "Open in Outlook").</summary>
    static Dictionary<string, string> Ids { get => Map("win_pst_ids"); set => Store.Set("win_pst_ids", JsonSerializer.Serialize(value)); }

    /// <summary>Which file each mailbox / archive in Outlook is (name → .pst / .ost path), remembered at every index pass.</summary>
    public static Dictionary<string, string> StoreFiles() => Map("win_store_files");

    static void RememberStores(dynamic ns)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        dynamic stores = ns.Stores;
        try
        {
            for (int i = 1; i <= (int)stores.Count; i++)
            {
                dynamic st = stores.Item(i);
                try { var p = Outlook.S(() => st.FilePath); var n = Outlook.S(() => st.DisplayName); if (p.Length > 0 && n.Length > 0) map[n] = p; } catch { }
                finally { Outlook.Release(st); }
            }
        }
        finally { Outlook.Release(stores); }
        Store.Set("win_store_files", JsonSerializer.Serialize(map));
    }

    /// <summary>Files Nova opened in Outlook itself (only those are closed again — never one you opened).</summary>
    static List<string> NovaOpened { get => Load("win_pst_nova_opened"); set => Store.Set("win_pst_nova_opened", JsonSerializer.Serialize(value ?? new())); }

    static string Stamp(string path) { try { var f = new FileInfo(path); return f.Length + "|" + f.LastWriteTimeUtc.Ticks; } catch { return ""; } }

    public static void Add(IEnumerable<string> paths)
    {
        var x = Extra;
        foreach (var p in paths) if (!x.Contains(p, StringComparer.OrdinalIgnoreCase)) x.Add(p);
        Extra = x;
        MailIndex.Now();
    }

    public static void Remove(string path) { Extra = Extra.Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList(); }

    public static bool IsDone(string path) => Done.TryGetValue(path, out var st) && st == Stamp(path);

    /// <summary>The .pst files Outlook has open now (their paths).</summary>
    public static List<string> OpenNow(dynamic ns)
    {
        var res = new List<string>();
        dynamic stores = ns.Stores;
        try
        {
            for (int i = 1; i <= (int)stores.Count; i++)
            {
                dynamic st = stores.Item(i);
                try { var p = Outlook.S(() => st.FilePath); if (p.Length > 0) res.Add(p); } catch { }
                finally { Outlook.Release(st); }
            }
        }
        finally { Outlook.Release(stores); }
        return res;
    }

    /// <summary>📂 Finds .pst archives next to the ones Outlook already uses (and in their sub-folders) that are not open in Outlook.</summary>
    public static List<(string Path, long Size, bool Added, bool Indexed)> Find()
    {
        var res = new List<(string, long, bool, bool)>();
        List<string> open;
        try
        {
            dynamic app = Outlook.Get();
            dynamic ns = app.GetNamespace("MAPI");
            open = OpenNow(ns);
            Outlook.Release(ns);
        }
        catch (Exception e) { Log.Warn("archives", e.Message); open = new(); }
        var extra = Extra;
        var dirs = open.Select(p => Path.GetDirectoryName(p) ?? "").Where(d => d.Length > 0)
            .Concat(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Outlook Files"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Outlook") })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
        {
            try
            {
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.EnumerateFiles(d, "*.pst", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }))
                {
                    if (!seen.Add(f) || open.Contains(f, StringComparer.OrdinalIgnoreCase) && !extra.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
                    if (Projects.Private.IsMatch(Path.GetFileName(f))) continue;   // 🔒
                    long size = 0; try { size = new FileInfo(f).Length; } catch { }
                    res.Add((f, size, extra.Contains(f, StringComparer.OrdinalIgnoreCase), IsDone(f)));
                }
            }
            catch { }
        }
        foreach (var x in extra.Where(x => !seen.Contains(x))) res.Add((x, File.Exists(x) ? new FileInfo(x).Length : 0, true, IsDone(x)));
        return res.OrderBy(x => x.Item1).ToList();
    }

    /// <summary>Before an index pass: opens the added archives that still need indexing. Returns the ones Nova opened.</summary>
    public static List<string> OpenForIndex(dynamic ns)
    {
        var added = new List<string>();
        try
        {
            try { RememberStores(ns); } catch { }
            var extra = Extra;
            if (extra.Count == 0) return added;
            var open = OpenNow(ns);
            var mine = NovaOpened;
            foreach (var p in extra)
            {
                if (!File.Exists(p) || IsDone(p)) continue;
                if (open.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    // Opened by Nova in a pass that stopped: close it after this one. Opened by YOU: leave it, it is indexed as a normal Outlook archive.
                    if (mine.Contains(p, StringComparer.OrdinalIgnoreCase)) added.Add(p); else Remove(p);
                    continue;
                }
                try { ns.AddStore(p); added.Add(p); mine.Add(p); NovaOpened = mine; Log.Warn("archives", "opened for indexing: " + p); }
                catch (Exception e) { Log.Warn("archives", "could not open " + p + ": " + e.Message); }
            }
        }
        catch (Exception e) { Log.Warn("archives", e.Message); }
        return added;
    }

    /// <summary>After the pass: remembers their store ids, marks them indexed when complete, and closes them in Outlook again.</summary>
    public static void CloseAfterIndex(dynamic ns, List<string> added, bool complete)
    {
        if (added == null || added.Count == 0) return;
        var ids = Ids; var done = Done;
        dynamic stores = ns.Stores;
        try
        {
            for (int i = (int)stores.Count; i >= 1; i--)
            {
                dynamic st = stores.Item(i);
                try
                {
                    var p = Outlook.S(() => st.FilePath);
                    if (!added.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                    ids[Outlook.S(() => st.StoreID)] = p;
                    if (complete) done[p] = Stamp(p);
                    dynamic root = st.GetRootFolder();
                    try { ns.RemoveStore(root); } catch (Exception e) { Log.Warn("archives", "close: " + e.Message); }
                    Outlook.Release(root);
                }
                catch { }
                finally { Outlook.Release(st); }
            }
        }
        finally { Outlook.Release(stores); }
        Ids = ids; Done = done;
        NovaOpened = NovaOpened.Where(x => !added.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>"Open in Outlook" on an email of a closed archive: opens the archive again.</summary>
    public static bool Reopen(dynamic ns, string storeId)
    {
        if (string.IsNullOrEmpty(storeId) || !Ids.TryGetValue(storeId, out var p) || !File.Exists(p)) return false;
        try { ns.AddStore(p); return true; } catch { return false; }
    }
}
