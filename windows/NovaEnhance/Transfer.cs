using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>One copy / move / mirror job of the 📦 Nova transfer engine.</summary>
class XJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Kind { get; set; } = "copy";   // copy · move · mirror
    public List<string> Sources { get; set; } = new();
    public string Dest { get; set; } = "";
    public string State { get; set; } = "queued";   // queued · scanning · running · paused · waiting · done · failed · cancelled
    public long TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public long DoneFiles { get; set; }
    public long DoneBytes { get; set; }
    public double Speed { get; set; }
    public string Current { get; set; } = "";
    public List<string> Failed { get; set; } = new();
    public string Note { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? Ended { get; set; }
    public string LogFile { get; set; } = "";
    public int Threads { get; set; }
    public long Copied { get; set; }
    public string Mode { get; set; } = "";
    public DateTime? Started { get; set; }
    public bool Verified { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public Process Proc;
    [System.Text.Json.Serialization.JsonIgnore] public bool StopAsked, PauseAsked;

    public string Title => $"{(Kind == "move" ? "✂ Move" : Kind == "mirror" ? "🔁 Mirror" : "📋 Copy")} · {(Sources.Count == 1 ? Short(Sources[0]) : Sources.Count + " items from " + Short(Path.GetDirectoryName(Sources[0]) ?? ""))} → {Short(Dest)}";
    static string Short(string p) => p.Length > 60 ? "…" + p[^58..] : p;
}

/// <summary>
/// 📦 Nova transfer engine: copy, move and mirror folders with Windows' Robocopy, many files at once (multi-threaded), with SuperCopier-style
/// choices for errors and collisions, long paths (over 260 characters), Arabic names, network drives, a pause when the 🌐 VPN drops and a
/// fast check afterwards. 🔒 Private folders are never copied. Nothing is deleted without asking (Mirror / Move). Designed by A.HAMROUNI
/// </summary>
static class Transfer
{
    // ------------------------------------------------------------------ settings (Settings ▸ 📦 Transfer engine)
    public static string FileError { get => Store.Str("win_xf_ferr", "end"); set => Store.Set("win_xf_ferr", value); }          // end · retry · skip · stop · ask
    public static string FileCollision { get => Store.Str("win_xf_fcol", "different"); set => Store.Set("win_xf_fcol", value); } // different · newer · skip · always · rename · ask
    public static string FolderError { get => Store.Str("win_xf_derr", "ask"); set => Store.Set("win_xf_derr", value); }         // ask · skip · stop
    public static string FolderCollision { get => Store.Str("win_xf_dcol", "merge"); set => Store.Set("win_xf_dcol", value); }   // merge · rename · skip · ask
    public static int Retries { get => Math.Clamp(Store.Int("win_xf_retries", 2), 0, 20); set => Store.SetInt("win_xf_retries", value); }
    public static int Wait { get => Math.Clamp(Store.Int("win_xf_wait", 5), 0, 120); set => Store.SetInt("win_xf_wait", value); }
    public static bool CheckDest { get => Store.Bool("win_xf_checkdest", true); set => Store.SetBool("win_xf_checkdest", value); }
    public static bool DeletePartial { get => Store.Bool("win_xf_delpartial", true); set => Store.SetBool("win_xf_delpartial", value); }
    public static bool RenameOriginal { get => Store.Bool("win_xf_renorig", false); set => Store.SetBool("win_xf_renorig", value); }
    public static bool AskDelete { get => Store.Bool("win_xf_askdel", true); set => Store.SetBool("win_xf_askdel", value); }
    /// <summary>Your rule: an underscore before the number — “SLV-ZUC-AR-MAR-00092_Rev01_(3).pdf” (the old default “name (2)” is moved to it).</summary>
    public static string RenameRule
    {
        get { var r = Store.Str("win_xf_rule", "{name}_({n}){ext}"); return r == "{name} ({n}){ext}" ? "{name}_({n}){ext}" : r; }
        set => Store.Set("win_xf_rule", value);
    }
    public static string Threads { get => Store.Str("win_xf_threads", "auto"); set => Store.Set("win_xf_threads", value); }          // auto · 8 · 16 · 32 · 64 · 128
    public static bool Unbuffered { get => Store.Bool("win_xf_unbuf", true); set => Store.SetBool("win_xf_unbuf", value); }
    public static string Compress { get => Store.Str("win_xf_compress", "auto"); set => Store.Set("win_xf_compress", value); }     // auto · off
    public static string SpeedLimit { get => Store.Str("win_xf_ipg", "none"); set => Store.Set("win_xf_ipg", value); }             // none · gentle · low
    public static bool PauseOnVpn { get => Store.Bool("win_xf_vpnpause", true); set => Store.SetBool("win_xf_vpnpause", value); }
    public static bool KeepAttr { get => Store.Bool("win_xf_keepattr", true); set => Store.SetBool("win_xf_keepattr", value); }
    public static bool KeepSecurity { get => Store.Bool("win_xf_keepsec", false); set => Store.SetBool("win_xf_keepsec", value); }
    public static bool Verify { get => Store.Bool("win_xf_verify", true); set => Store.SetBool("win_xf_verify", value); }
    public static bool SameDriveRename { get => Store.Bool("win_xf_samedrive", true); set => Store.SetBool("win_xf_samedrive", value); }
    public static string Exclude { get => Store.Str("win_xf_exclude", "Thumbs.db; ~$*; *.tmp; desktop.ini"); set => Store.Set("win_xf_exclude", value); }
    public static bool Menu { get => Store.Bool("win_xf_menu", true); set { Store.SetBool("win_xf_menu", value); XferMenu.Apply(); } }
    public static bool TakeOverPaste { get => Store.Bool("win_xf_ctrlv", false); set { Store.SetBool("win_xf_ctrlv", value); PasteHook.Apply(); } }

    public static void Defaults()
    {
        foreach (var k in new[] { "win_xf_ferr", "win_xf_fcol", "win_xf_derr", "win_xf_dcol", "win_xf_retries", "win_xf_wait", "win_xf_checkdest", "win_xf_delpartial", "win_xf_renorig",
                     "win_xf_askdel", "win_xf_rule", "win_xf_threads", "win_xf_unbuf", "win_xf_compress", "win_xf_ipg", "win_xf_vpnpause", "win_xf_keepattr", "win_xf_keepsec",
                     "win_xf_verify", "win_xf_samedrive", "win_xf_exclude" })
            Store.Remove(k);
    }

    // ------------------------------------------------------------------ queue

    public static readonly List<XJob> Jobs = new();
    static readonly object gate = new();
    static Thread runner;
    static readonly AutoResetEvent wake = new(false);
    public static event Action Changed;
    static void Fire() { try { Changed?.Invoke(); } catch { } }
    static string HistoryFile => Path.Combine(Paths.Root, "transfers.json");

    public static void Start()
    {
        if (runner != null) return;
        try
        {
            if (File.Exists(HistoryFile))
                foreach (var j in JsonSerializer.Deserialize<List<XJob>>(File.ReadAllText(HistoryFile)) ?? new())
                {
                    if (j.State is "running" or "scanning" or "waiting") j.State = "paused";   // Nova was closed during a transfer: resume from where it stopped
                    Jobs.Add(j);
                }
        }
        catch { }
        runner = new Thread(() => Guard.Run("transfer", Loop)) { IsBackground = true, Name = "Nova transfer" };
        runner.Start();
    }

    static void Save()
    {
        try { lock (gate) File.WriteAllText(HistoryFile, JsonSerializer.Serialize(Jobs.TakeLast(60).ToList())); } catch { }
    }

    public static XJob Add(string kind, IEnumerable<string> sources, string dest)
    {
        var j = new XJob { Kind = kind, Sources = sources.Select(LongName).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Dest = LongName(dest) };
        lock (gate) Jobs.Add(j);
        Save(); Fire(); wake.Set();
        Program.UiContext?.Post(_ => Screens.Transfers(), null);
        return j;
    }

    public static void Pause(XJob j) { j.PauseAsked = true; try { j.Proc?.Kill(true); } catch { } if (j.State is "queued" or "waiting") j.State = "paused"; Save(); Fire(); }
    public static void Resume(XJob j) { if (j.State is "paused" or "failed" or "cancelled") { j.State = "queued"; j.PauseAsked = j.StopAsked = false; j.Note = ""; Save(); Fire(); wake.Set(); } }
    public static void Cancel(XJob j)
    {
        j.StopAsked = true; try { j.Proc?.Kill(true); } catch { }
        if (j.State is "queued" or "paused" or "waiting") { j.State = "cancelled"; j.Ended = DateTime.Now; }
        Save(); Fire();
    }
    public static void Forget(XJob j) { lock (gate) Jobs.Remove(j); Save(); Fire(); }
    public static void RetryFailed(XJob j)
    {
        if (j.Failed.Count == 0) return;
        var srcs = j.Failed.Where(f => File.Exists(f) || Directory.Exists(f)).ToList();
        if (srcs.Count == 0) return;
        // each failed file goes back to where it belonged under the destination
        foreach (var g in srcs.GroupBy(f => TargetFor(j, f)))
            Add(j.Kind == "move" ? "move" : "copy", g, g.Key);
    }

    static string TargetFor(XJob j, string file)
    {
        foreach (var s in j.Sources)
        {
            if (Directory.Exists(s) && file.StartsWith(s.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(Path.Combine(j.Dest, Path.GetFileName(s.TrimEnd('\\')), file[(s.TrimEnd('\\').Length + 1)..]))!;
        }
        return j.Dest;
    }

    static void Loop()
    {
        while (true)
        {
            XJob next;
            lock (gate) next = Jobs.FirstOrDefault(x => x.State == "queued");
            if (next == null) { wake.WaitOne(TimeSpan.FromSeconds(5)); continue; }
            try { Run(next); }
            catch (Exception e) { next.State = "failed"; next.Note = e.Message; next.Ended = DateTime.Now; Log.Warn("transfer", e.Message); }
            Save(); Fire();
            if (next.State is "done" or "failed")
                Program.Balloon(next.State == "done" ? "📦 Transfer finished" : "📦 Transfer: some files failed", next.Title + (next.Note.Length > 0 ? " · " + next.Note : ""));
        }
    }

    // ------------------------------------------------------------------ one job

    record Item(string Src, string Dst, List<string> Files);   // Files == null → the whole folder

    static void Run(XJob j)
    {
        j.Failed.Clear(); j.Note = ""; j.DoneBytes = 0; j.DoneFiles = 0; j.Speed = 0; j.Verified = false; j.Copied = 0;
        j.State = "scanning"; Fire();

        // 🔒 never copied, never a source
        var priv = j.Sources.Where(s => Projects.Private.IsMatch(s)).ToList();
        if (priv.Count > 0) { j.Sources.RemoveAll(s => Projects.Private.IsMatch(s)); j.Note = $"🔒 {priv.Count} private item(s) left out. "; }
        if (j.Sources.Count == 0) { j.State = "failed"; j.Note += "Nothing to copy."; j.Ended = DateTime.Now; return; }
        foreach (var s in j.Sources)
            if (Directory.Exists(s) && (j.Dest + "\\").StartsWith(s.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            { j.State = "failed"; j.Note += "The destination is inside the folder being copied."; j.Ended = DateTime.Now; return; }

        if (!WaitForNetwork(j)) return;
        if (!Directory.Exists(j.Dest))
        {
            if (CheckDest && Ask("📦 Nova transfer", $"The destination folder does not exist:\n{j.Dest}\n\nCreate it?", "Create it", "Cancel") != 0) { j.State = "cancelled"; j.Ended = DateTime.Now; return; }
            Directory.CreateDirectory(Long(j.Dest));
        }

        // items: whole folders, and loose files grouped by their folder
        var items = new List<Item>();
        foreach (var s in j.Sources.Where(Directory.Exists)) items.Add(new Item(s.TrimEnd('\\'), Path.Combine(j.Dest, Path.GetFileName(s.TrimEnd('\\'))), null));
        foreach (var g in j.Sources.Where(File.Exists).GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase))
            items.Add(new Item(g.Key, j.Dest, g.Select(Path.GetFileName).ToList()!));

        // folder collisions
        string folderMode = FolderCollision;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Files != null || !Directory.Exists(it.Dst)) continue;
            if (RenameOriginal) { var old = it.Dst + $" (old {DateTime.Now:yyyy-MM-dd HHmm})"; Directory.Move(Long(it.Dst), Long(old)); j.Note += $"Old “{Path.GetFileName(it.Dst)}” kept as “{Path.GetFileName(old)}”. "; continue; }
            var mode = folderMode;
            if (mode == "ask")
            {
                var a = Ask("📦 Folder already there", $"“{Path.GetFileName(it.Dst)}” already exists in\n{j.Dest}\n\nWhat should Nova do?", "Merge (add / update files)", "Rename the copy", "Skip this folder", "Same answer for all: Merge");
                mode = a switch { 1 => "rename", 2 => "skip", 3 => "merge", _ => "merge" };
                if (a == 3) folderMode = "merge";
            }
            if (mode == "skip") { items.RemoveAt(i); i--; continue; }
            if (mode == "rename") items[i] = it with { Dst = Unique(it.Dst, true) };
        }
        if (items.Count == 0) { j.State = "done"; j.Note += "Nothing to do (skipped)."; j.Ended = DateTime.Now; return; }

        // ✂ same drive: moved at once by renaming (no copy)
        if (j.Kind == "move" && SameDriveRename)
        {
            foreach (var it in items.ToList())
            {
                if (!SameVolume(it.Src, j.Dest)) continue;
                try
                {
                    if (it.Files == null && !Directory.Exists(it.Dst)) { Directory.Move(Long(it.Src), Long(it.Dst)); items.Remove(it); j.DoneFiles++; }
                    else if (it.Files != null)
                    {
                        foreach (var f in it.Files.ToList())
                        {
                            var to = Path.Combine(it.Dst, f);
                            if (File.Exists(to)) continue;
                            File.Move(Long(Path.Combine(it.Src, f)), Long(to)); it.Files.Remove(f); j.DoneFiles++;
                        }
                        if (it.Files.Count == 0) items.Remove(it);
                    }
                }
                catch (Exception e) { Log.Warn("transfer", "same-drive move: " + e.Message); }
            }
            if (items.Count == 0) { j.State = "done"; j.Note += "Moved at once (same drive)."; j.Ended = DateTime.Now; return; }
        }

        // scan: how many files and bytes (for the progress), private and excluded files left out, file collisions
        var excl = Exclude.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var exRx = excl.Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase)).ToList();
        var privDirs = new List<string>(); var privFiles = new List<string>();
        var collisions = new List<(string Src, string Dst)>();
        long files = 0, bytes = 0, big = 0;
        var needCollisions = FileCollision is "rename" or "ask";
        var eo = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var it in items)
        {
            IEnumerable<(string Full, string Rel)> list;
            if (it.Files != null) list = it.Files.Select(f => (Path.Combine(it.Src, f), f));
            else list = Walk(it.Src, eo, privDirs);
            foreach (var (full, rel) in list)
            {
                if (j.StopAsked) break;
                var name = Path.GetFileName(full);
                if (Projects.Private.IsMatch(name)) { privFiles.Add(full); continue; }
                if (exRx.Any(r => r.IsMatch(name))) continue;
                long len = 0; try { len = new FileInfo(Long(full)).Length; } catch { }
                files++; bytes += len; if (len >= 100L << 20) big++;
                if (needCollisions) { var d = Path.Combine(it.Dst, rel); if (File.Exists(Long(d))) collisions.Add((full, d)); }
                if (files % 500 == 0) { j.TotalFiles = files; j.TotalBytes = bytes; Fire(); }
            }
        }
        j.TotalFiles = files; j.TotalBytes = bytes;
        if (privDirs.Count + privFiles.Count > 0) j.Note += $"🔒 {privDirs.Count + privFiles.Count} private item(s) left out. ";
        if (j.StopAsked) { Stopped(j); return; }

        // file collisions: ask once, or rename with the renaming rule
        var fileMode = FileCollision;
        if (fileMode == "ask" && collisions.Count > 0)
        {
            var a = Ask("📦 Files already there", $"{collisions.Count} file(s) already exist at the destination, e.g.\n{string.Join("\n", collisions.Take(5).Select(c => Path.GetFileName(c.Dst)))}\n\nWhat should Nova do with them?",
                "Overwrite if different", "Overwrite only if newer", "Skip them", "Rename the copies", "Overwrite all");
            fileMode = a switch { 1 => "newer", 2 => "skip", 3 => "rename", 4 => "always", _ => "different" };
        }
        if (fileMode == "ask") fileMode = "different";
        var renamedAway = new List<string>();
        if (fileMode == "rename")
        {
            foreach (var (src, dst) in collisions)
            {
                try { var to = Unique(dst, false); Directory.CreateDirectory(Long(Path.GetDirectoryName(to)!)); File.Copy(Long(src), Long(to)); renamedAway.Add(src); j.DoneFiles++; j.Copied++; try { j.DoneBytes += new FileInfo(Long(src)).Length; } catch { } }
                catch (Exception e) { j.Failed.Add(src); Log.Warn("transfer", "rename copy: " + e.Message); }
            }
            fileMode = "skip";   // the rest: copy only what is not there
        }

        // ✂ Move / 🔁 Mirror: nothing is deleted without asking
        if (j.Kind == "move" && AskDelete && Ask("📦 Move", $"Nova will copy {files:N0} file(s) to\n{j.Dest}\nand then delete the originals.\n\nGo on?", "Move", "Copy only (keep the originals)", "Cancel") is var mv && mv != 0)
        { if (mv == 1) j.Kind = "copy"; else { j.State = "cancelled"; j.Ended = DateTime.Now; return; } }
        if (j.Kind == "mirror")
        {
            var extras = new List<string>();
            foreach (var it in items.Where(i => i.Files == null))
                extras.AddRange(DryRunExtras(it, privDirs, privFiles, excl));
            if (extras.Count > 0 && AskDelete)
            {
                var a = Ask("🔁 Mirror — files would be deleted", $"{extras.Count} file(s)/folder(s) exist only at the destination and would be DELETED, e.g.\n{string.Join("\n", extras.Take(12))}{(extras.Count > 12 ? "\n…" : "")}",
                    "Delete them (mirror)", "Keep them (copy only)", "Cancel");
                if (a == 1) j.Kind = "copy"; else if (a != 0) { j.State = "cancelled"; j.Ended = DateTime.Now; return; }
            }
        }

        // threads: many small files → many threads; big files → fewer threads, unbuffered
        var avg = files > 0 ? bytes / files : 0;
        var bigMode = avg >= 50L << 20 || (big > 0 && files < 40);
        // big files or only a few: one thread with a live % (threads do not speed up a single file); many files: many threads
        // Over the VPN / a network drive: buffered and in parallel (fills the line like Explorer or better); the live % comes from the network.
        // Local: big files one at a time, unbuffered, with Robocopy's own live %.
        var overNet = IsNetwork(j.Dest) || j.Sources.Any(IsNetwork);
        int user = Threads == "auto" ? 0 : int.TryParse(Threads, out var t) ? Math.Clamp(t, 1, 128) : 16;
        if (overNet) j.Threads = files <= 1 ? 1 : user > 0 ? user : bigMode ? (int)Math.Clamp(files, 4, 8) : 32;
        else j.Threads = bigMode || files <= 3 ? 1 : user > 0 ? Math.Max(2, user) : 32;
        var unbuffered = bigMode && !overNet;
        j.Mode = $"{(overNet ? "VPN / network" : "local")} · {(unbuffered ? "unbuffered (/J)" : "buffered")} · {j.Threads} thread(s){(Compress != "off" && CanCompress() ? " · compression asked" : "")}{(SpeedLimit != "none" ? " · speed limit " + SpeedLimit : "")}";
        j.Started = DateTime.Now;

        j.State = "running"; j.LogFile = Path.Combine(Paths.Logs, $"transfer-{j.Id}.log");
        try { File.Delete(j.LogFile); } catch { }
        var sw = Stopwatch.StartNew();
        foreach (var it in items)
        {
            if (j.StopAsked || j.PauseAsked) break;
            var files2 = it.Files?.Where(f => !renamedAway.Contains(Path.Combine(it.Src, f), StringComparer.OrdinalIgnoreCase)).ToList();
            if (it.Files != null && files2!.Count == 0) continue;
            // the file list goes in chunks so a big selection never overflows Windows' command-line limit
            foreach (var chunk in Chunks(files2))
            {
                var args = Args(j, it, chunk, fileMode, unbuffered, privDirs, privFiles, renamedAway, excl);
                RunRobocopy(j, args, sw);
                if (j.StopAsked || j.PauseAsked) break;
            }
        }
        if (j.PauseAsked) { j.State = OfficeVpnDown(j) ? "waiting" : "paused"; j.PauseAsked = false; if (j.State == "waiting") { j.Note = "Waiting for the 🌐 VPN…"; j.State = "queued"; WaitForNetwork(j); } return; }
        if (j.StopAsked) { Stopped(j); return; }

        // errors: at the end (retry once) · ask · skip · stop (stopped while running)
        if (j.Failed.Count > 0)
        {
            var mode = FileError;
            if (mode == "ask")
                mode = Ask("📦 Some files failed", $"{j.Failed.Count} file(s) could not be copied, e.g.\n{string.Join("\n", j.Failed.Take(8).Select(Path.GetFileName))}", "Try them again", "Skip them") == 0 ? "end" : "skip";
            if (mode == "end") RetryAtEnd(j, sw);
            if (DeletePartial) CleanPartial(j);
        }

        // ✔ fast check: nothing left to copy
        if (Verify && j.Failed.Count == 0 && j.Kind != "move" && fileMode is "different" or "always")
        {
            j.Note += "Checking… "; Fire();
            var left = 0;
            foreach (var it in items) foreach (var chunk in Chunks(it.Files)) left += CountDifferences(it, chunk, privDirs, privFiles, excl);
            j.Verified = left == 0;
            j.Note = j.Note.Replace("Checking… ", "") + (left == 0 ? "✔ verified. " : $"⚠ {left} file(s) still differ. ");
        }
        j.State = j.Failed.Count == 0 ? "done" : "failed";
        if (j.Failed.Count > 0) j.Note += $"{j.Failed.Count} file(s) failed — see the log. ";
        else
        {
            // files already there and identical are skipped by Robocopy without a line: they count as done
            var skipped = Math.Max(0, j.TotalFiles - j.Copied);
            if (skipped > 0) j.Note += $"{skipped:N0} file(s) already up to date (skipped). ";
            j.DoneFiles = j.TotalFiles; j.DoneBytes = j.TotalBytes;
        }
        j.Speed = sw.Elapsed.TotalSeconds > 0 ? j.Copied > 0 ? j.DoneBytes / Math.Max(1, sw.Elapsed.TotalSeconds) : 0 : 0;
        j.Ended = DateTime.Now;
    }

    static void Stopped(XJob j)
    {
        j.State = "cancelled"; j.Ended = DateTime.Now; j.Note += "Cancelled. ";
        if (DeletePartial) CleanPartial(j);
    }

    static IEnumerable<(string Full, string Rel)> Walk(string root, EnumerationOptions eo, List<string> privDirs)
    {
        var stack = new Stack<string>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<string> fs = Array.Empty<string>(), ds = Array.Empty<string>();
            try { fs = Directory.EnumerateFiles(Long(d), "*", eo).ToList(); ds = Directory.EnumerateDirectories(Long(d), "*", eo).ToList(); } catch { }
            foreach (var f in fs) { var p = Unlong(f); yield return (p, p[(root.Length + 1)..]); }
            foreach (var sub in ds)
            {
                var p = Unlong(sub);
                if (Projects.Private.IsMatch(Path.GetFileName(p)) || p.Contains("TrustWalletBackup", StringComparison.OrdinalIgnoreCase)) { privDirs.Add(p); continue; }
                stack.Push(p);
            }
        }
    }

    static IEnumerable<List<string>> Chunks(List<string> files)
    {
        if (files == null) { yield return null; yield break; }
        var cur = new List<string>(); int len = 0;
        foreach (var f in files)
        {
            if (len + f.Length + 3 > 20000 && cur.Count > 0) { yield return cur; cur = new(); len = 0; }
            cur.Add(f); len += f.Length + 3;
        }
        if (cur.Count > 0) yield return cur;
    }

    // ------------------------------------------------------------------ Robocopy

    static string Q(string p) => "\"" + (p.EndsWith('\\') ? p + "." : p) + "\"";

    static bool? compressOk;
    static bool CanCompress()
    {
        if (compressOk != null) return compressOk.Value;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("robocopy.exe", "/?") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
            compressOk = o.Contains("/COMPRESS", StringComparison.OrdinalIgnoreCase);
        }
        catch { compressOk = false; }
        return compressOk.Value;
    }

    static string Args(XJob j, Item it, List<string> files, string fileMode, bool bigMode, List<string> privDirs, List<string> privFiles, List<string> renamedAway, List<string> excl, bool listOnly = false)
    {
        var a = new StringBuilder();
        a.Append(Q(it.Src)).Append(' ').Append(Q(it.Dst));
        if (files != null) foreach (var f in files) a.Append(' ').Append(Q(f));
        if (files == null) a.Append(j.Kind == "mirror" ? " /MIR" : " /E");
        if (j.Kind == "move") a.Append(files == null ? " /MOVE" : " /MOV");
        a.Append(KeepAttr ? (KeepSecurity ? " /COPY:DATS" : " /COPY:DAT") : " /COPY:D").Append(" /DCOPY:DAT");
        a.Append(fileMode switch { "newer" => " /XO", "skip" => " /XC /XN /XO", "always" => " /IS /IT", _ => "" });
        var ferr = FileError;
        a.Append(ferr is "end" or "skip" or "ask" ? " /R:0 /W:0" : $" /R:{Retries} /W:{Wait}");
        if (j.Threads > 1) a.Append($" /MT:{j.Threads}");   // one thread = Robocopy reports the live % of each file
        if (bigMode && Unbuffered) a.Append(" /J");
        if (Compress != "off" && CanCompress()) a.Append(" /COMPRESS");
        a.Append(SpeedLimit switch { "gentle" => " /IPG:5", "low" => " /IPG:25", _ => "" });
        a.Append(j.Threads > 1 || listOnly ? " /XJ /FFT /BYTES /NP /NDL /NJH /NJS" : " /XJ /FFT /BYTES /NDL /NJH /NJS");
        // 🔒 private folders and files, your exclusions, the files already handled by “rename”
        var xd = new List<string> { "*TrustWalletBackup*", "*wallet*", "*password*", "*passwd*", "*keepass*", "*bitwarden*" };
        xd.AddRange(privDirs.Where(d => d.StartsWith(it.Src, StringComparison.OrdinalIgnoreCase)));
        a.Append(" /XD"); foreach (var d in xd) a.Append(' ').Append(Q(d));
        var xf = new List<string> { "*wallet*", "*password*", "*passwd*", "*keepass*", "*bitwarden*", "*.kdbx" };
        xf.AddRange(excl);
        xf.AddRange(privFiles.Where(f => f.StartsWith(it.Src, StringComparison.OrdinalIgnoreCase)));
        xf.AddRange(renamedAway.Where(f => f.StartsWith(it.Src, StringComparison.OrdinalIgnoreCase)));
        a.Append(" /XF"); foreach (var f in xf) a.Append(' ').Append(Q(f));
        if (listOnly) a.Append(" /L /X");
        return a.ToString();
    }

    static readonly Regex FileLine = new(@"^\s*(New File|Newer|Older|Changed|Tweaked|Modified|Same|\*EXTRA File|New Dir|\*EXTRA Dir)?\s*(\d+)\s+(.+?)\s*$", RegexOptions.Compiled);
    static readonly Regex ErrLine = new(@"ERROR (\d+) \(0x[0-9A-Fa-f]+\) (.+?) (\S:\\.+|\\\\.+)$", RegexOptions.Compiled);

    /// <summary>Runs one Robocopy and follows its Unicode log: files done, bytes, speed, current file, failures.</summary>
    static readonly Regex Pct = new(@"^\s*(\d+(?:\.\d+)?)%\s*$", RegexOptions.Compiled);

    /// <summary>Bytes received and sent by this PC (the VPN adapter when it is up, otherwise every network adapter).</summary>
    static (long Rx, long Tx) NetBytes()
    {
        long rx = 0, tx = 0;
        try
        {
            var all = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback).ToList();
            var vpn = all.Where(n => n.Name.Equals(OfficeVpn.Iface, StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(n.Description, "OpenConnect|Wintun|Fortinet SSL", RegexOptions.IgnoreCase)).ToList();
            foreach (var n in vpn.Count > 0 ? vpn : all) { var st = n.GetIPStatistics(); rx += st.BytesReceived; tx += st.BytesSent; }
        }
        catch { }
        return (rx, tx);
    }

    /// <summary>Runs one Robocopy and follows its Unicode log. Big files (one thread): the live % of the file being copied. Many files (many
    /// threads): each file as it finishes, and the live speed from the network when the copy goes over the network / VPN.</summary>
    static int RunRobocopy(XJob j, string args, Stopwatch sw)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"nova-rc-{j.Id}-{Guid.NewGuid().ToString("N")[..6]}.log");
        var psi = new ProcessStartInfo("robocopy.exe", args + $" /UNILOG:{Q(tmp)}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        j.Proc = p;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        long pos = 0; var carry = "";
        var single = j.Threads <= 1;
        var overNet = IsNetwork(j.Dest) || j.Sources.Any(IsNetwork);
        long baseBytes = j.DoneBytes, curSize = -1; string openFirst = null;
        var netStart = NetBytes(); var net0 = netStart; var netAt = DateTime.Now; double netSpeed = 0;
        long confirmed = j.DoneBytes, startBytes = j.DoneBytes;
        long Moved((long Rx, long Tx) a, (long Rx, long Tx) b) => Math.Max(Math.Max(0, b.Rx - a.Rx), Math.Max(0, b.Tx - a.Tx));   // one direction counts (S: → S: would count twice)
        var stopOnError = FileError == "stop" || FolderError == "stop";

        void Commit() { if (curSize >= 0) { j.DoneFiles++; j.Copied++; baseBytes += curSize; j.DoneBytes = baseBytes; curSize = -1; } }

        void First(string l)
        {
            try { File.AppendAllText(j.LogFile, l + Environment.NewLine); } catch { }
            var e = ErrLine.Match(l);
            if (e.Success)
            {
                var path = e.Groups[3].Value.Trim();
                var folder = e.Groups[2].Value.Contains("Directory", StringComparison.OrdinalIgnoreCase);
                if (!(folder && FolderError == "skip")) j.Failed.Add(path);
                if (single && !folder) { curSize = -1; j.DoneBytes = baseBytes; }   // the file being copied failed
                if (stopOnError && (folder ? FolderError == "stop" : FileError == "stop")) { j.StopAsked = true; j.Note += "Stopped at the first error. "; try { p.Kill(true); } catch { } }
                return;
            }
            var m = FileLine.Match(l);
            if (!m.Success || m.Groups[1].Value is "Same" or "*EXTRA File" or "New Dir" or "*EXTRA Dir" || !long.TryParse(m.Groups[2].Value, out var size)) return;
            if (single) { Commit(); curSize = size; j.Current = m.Groups[3].Value; }   // starts now: the % follows
            else { j.DoneFiles++; j.Copied++; confirmed += size; j.DoneBytes = Math.Max(j.DoneBytes, confirmed); j.Current = m.Groups[3].Value; }   // finished
        }

        void Line(string line, bool complete)
        {
            var segs = line.Split('\r');
            if (!complete && segs.Length < 2) return;   // still being written
            if (openFirst == null || openFirst != segs[0]) First(segs[0]);
            openFirst = complete ? null : segs[0];
            for (int k = segs.Length - 1; k >= 1; k--)
            {
                var pm = Pct.Match(segs[k]);
                if (!pm.Success) continue;
                if (single && curSize > 0 && double.TryParse(pm.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pc))
                    j.DoneBytes = baseBytes + (long)(Math.Min(100, pc) / 100.0 * curSize);
                break;
            }
        }

        void Read()
        {
            try
            {
                if (File.Exists(tmp))
                {
                    using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length > pos)
                    {
                        fs.Position = pos;
                        var buf = new byte[fs.Length - pos]; var n = fs.Read(buf, 0, buf.Length);
                        n -= n % 2; pos += n;
                        var text = carry + Encoding.Unicode.GetString(buf, 0, n).TrimStart('﻿');
                        var lines = text.Split('\n'); carry = lines[^1];
                        foreach (var raw in lines.Take(lines.Length - 1)) Line(raw.TrimEnd('\r'), true);
                    }
                    if (carry.Length > 0) Line(carry, false);
                }
                var secs = Math.Max(0.5, sw.Elapsed.TotalSeconds);
                if (!single && overNet)
                {
                    // many threads over the network: the speed the network really carries (files only report when finished)
                    var now = DateTime.Now; var nb = NetBytes(); var dt = (now - netAt).TotalSeconds;
                    if (dt >= 1) { var v = Moved(net0, nb) / dt; netSpeed = netSpeed == 0 ? v : netSpeed * 0.6 + v * 0.4; net0 = nb; netAt = now; }
                    j.Speed = netSpeed;
                    // live %: what went over the network since this copy started (a little is protocol overhead), never past what is left
                    var est = startBytes + (long)(Moved(netStart, nb) * 0.97);
                    j.DoneBytes = Math.Max(confirmed, Math.Min(est, Math.Max(confirmed, j.TotalBytes - 1)));
                }
                else j.Speed = j.DoneBytes / secs;
                Fire();
            }
            catch { }
        }
        while (!p.WaitForExit(700))
        {
            Read();
            if (j.StopAsked || j.PauseAsked) { try { p.Kill(true); } catch { } break; }
            if (PauseOnVpn && OfficeVpnDown(j)) { j.PauseAsked = true; try { p.Kill(true); } catch { } break; }
        }
        try { p.WaitForExit(5000); } catch { }
        Read();
        if (carry.Length > 0) Line(carry, true);
        var code = p.HasExited ? p.ExitCode : 16;
        if (single && code < 8 && !j.StopAsked && !j.PauseAsked) Commit();   // the last file finished
        if (!single) j.DoneBytes = confirmed;   // exact figures once Robocopy has finished
        j.Proc = null;
        try { File.Delete(tmp); } catch { }
        return code;
    }

    static void RetryAtEnd(XJob j, Stopwatch sw)
    {
        var failed = j.Failed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        j.Failed.Clear();
        foreach (var f in failed)
        {
            if (j.StopAsked) { j.Failed.Add(f); continue; }
            try
            {
                if (!File.Exists(Long(f))) { j.Failed.Add(f); continue; }
                var to = Path.Combine(TargetFor(j, f), Path.GetFileName(f));
                Directory.CreateDirectory(Long(Path.GetDirectoryName(to)!));
                File.Copy(Long(f), Long(to), true);
                if (j.Kind == "move") File.Delete(Long(f));
                j.DoneFiles++;
            }
            catch { j.Failed.Add(f); }
        }
    }

    /// <summary>A file that failed half-way is removed at the destination (when its size differs from the original).</summary>
    static void CleanPartial(XJob j)
    {
        foreach (var f in j.Failed.Distinct(StringComparer.OrdinalIgnoreCase))
            try
            {
                if (!File.Exists(Long(f))) continue;
                var to = Path.Combine(TargetFor(j, f), Path.GetFileName(f));
                if (File.Exists(Long(to)) && new FileInfo(Long(to)).Length != new FileInfo(Long(f)).Length) File.Delete(Long(to));
            }
            catch { }
    }

    static List<string> DryRunExtras(Item it, List<string> privDirs, List<string> privFiles, List<string> excl)
    {
        var res = new List<string>();
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"nova-rc-dry-{Guid.NewGuid().ToString("N")[..6]}.log");
            var j = new XJob { Kind = "mirror", Threads = 8 };
            var args = Args(j, it, null, "different", false, privDirs, privFiles, new(), excl, listOnly: true) + $" /UNILOG:{Q(tmp)}";
            using var p = Process.Start(new ProcessStartInfo("robocopy.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            p.StandardOutput.ReadToEnd(); p.WaitForExit(600000);
            foreach (var l in File.ReadAllLines(tmp, Encoding.Unicode))
            {
                var m = FileLine.Match(l);
                if (m.Success && m.Groups[1].Value.StartsWith("*EXTRA")) res.Add(m.Groups[3].Value);
            }
            File.Delete(tmp);
        }
        catch (Exception e) { Log.Warn("transfer", "mirror check: " + e.Message); }
        return res;
    }

    /// <summary>✔ / Compare: how many files would still be copied (new, newer, older, changed).</summary>
    static int CountDifferences(Item it, List<string> files, List<string> privDirs, List<string> privFiles, List<string> excl) =>
        CompareLines(it, files, privDirs, privFiles, excl).Count(x => x.Kind is "New File" or "Newer" or "Older" or "Changed" or "Tweaked" or "Modified");

    static List<(string Kind, long Size, string Path)> CompareLines(Item it, List<string> files, List<string> privDirs, List<string> privFiles, List<string> excl)
    {
        var res = new List<(string, long, string)>();
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"nova-rc-cmp-{Guid.NewGuid().ToString("N")[..6]}.log");
            var j = new XJob { Kind = "copy", Threads = 16 };
            var args = Args(j, it, files, "different", false, privDirs, privFiles, new(), excl, listOnly: true) + $" /UNILOG:{Q(tmp)}";
            using var p = Process.Start(new ProcessStartInfo("robocopy.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            p.StandardOutput.ReadToEnd(); p.WaitForExit(600000);
            foreach (var l in File.ReadAllLines(tmp, Encoding.Unicode))
            {
                var m = FileLine.Match(l);
                if (m.Success && m.Groups[1].Value.Length > 0 && !m.Groups[1].Value.EndsWith("Dir") && long.TryParse(m.Groups[2].Value, out var sz)) res.Add((m.Groups[1].Value, sz, m.Groups[3].Value));
            }
            File.Delete(tmp);
        }
        catch (Exception e) { Log.Warn("transfer", "compare: " + e.Message); }
        return res;
    }

    /// <summary>✔ Compare with… (nothing is copied): what is new, changed, or only at the destination.</summary>
    public static string CompareReport(IEnumerable<string> sources, string dest)
    {
        var sb = new StringBuilder($"✔ Compare · {DateTime.Now:dd MMM HH:mm}\r\nDestination: {dest}\r\n\r\n");
        foreach (var s0 in sources)
        {
            var s = LongName(s0);
            var it = Directory.Exists(s) ? new Item(s.TrimEnd('\\'), Path.Combine(dest, Path.GetFileName(s.TrimEnd('\\'))), null) : new Item(Path.GetDirectoryName(s)!, dest, new List<string> { Path.GetFileName(s) });
            var lines = CompareLines(it, it.Files, new(), new(), Exclude.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
            sb.Append($"■ {s}\r\n");
            foreach (var g in lines.GroupBy(l => l.Kind).OrderBy(g => g.Key))
            {
                sb.Append($"   {Label(g.Key)}: {g.Count():N0} file(s), {Size(g.Sum(x => x.Size))}\r\n");
                foreach (var x in g.Take(30)) sb.Append("      ").Append(x.Path).Append("\r\n");
                if (g.Count() > 30) sb.Append("      …\r\n");
            }
            if (lines.Count == 0) sb.Append("   ✔ identical\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
        static string Label(string k) => k switch { "New File" => "Only in the source (new)", "Newer" => "Newer in the source", "Older" => "Older in the source", "*EXTRA File" => "Only at the destination", _ => "Changed" };
    }

    /// <summary>🔎 For Claude's read-only transfers check: the last jobs, how they ran, and the end of the newest log (🔒 private lines left out).</summary>
    public static string Report()
    {
        var sb = new StringBuilder($"📦 Nova transfer · {DateTime.Now:dd MMM HH:mm:ss}\n");
        sb.Append($"Settings: threads {Threads} · big local files {(Unbuffered ? "unbuffered" : "normal")} · compression {Compress} · speed limit {SpeedLimit} · file collision {FileCollision} · file error {FileError} · verify {(Verify ? "on" : "off")} · pause on VPN {(PauseOnVpn ? "on" : "off")} · VPN {OfficeVpn.Now}\n");
        List<XJob> jobs; try { jobs = Jobs.ToList(); } catch { jobs = new(); }
        foreach (var j in jobs.OrderByDescending(x => x.Added).Take(10))
        {
            var secs = j.Started != null ? ((j.Ended ?? DateTime.Now) - j.Started.Value).TotalSeconds : 0;
            var avg = secs > 0 && j.Copied > 0 ? Size((long)(j.DoneBytes / secs)) + "/s average" : "";
            var line = $"- {j.Added:HH:mm} {j.Kind} · {j.State} · {j.DoneFiles}/{j.TotalFiles} files · {Size(j.DoneBytes)} of {Size(j.TotalBytes)} · copied {j.Copied} · {(j.State == "running" ? Size((long)j.Speed) + "/s now · " : "")}{avg} · took {secs:0} s · {j.Mode} · failed {j.Failed.Count}" +
                       $"\n    {string.Join(" + ", j.Sources.Take(3))}{(j.Sources.Count > 3 ? $" (+{j.Sources.Count - 3})" : "")} → {j.Dest}{(j.Note.Length > 0 ? "\n    note: " + j.Note : "")}";
            if (Projects.Private.IsMatch(line)) line = $"- {j.Added:HH:mm} {j.Kind} · {j.State} · 🔒 private path not shown";
            sb.Append(line).Append('\n');
        }
        var last = jobs.Where(x => x.LogFile.Length > 0 && File.Exists(x.LogFile)).OrderByDescending(x => x.Added).FirstOrDefault();
        if (last != null)
        {
            sb.Append($"Log of the newest job ({last.Added:HH:mm}), last lines:\n");
            try { foreach (var l in File.ReadAllLines(last.LogFile).Where(x => x.Trim().Length > 0 && !Projects.Private.IsMatch(x)).TakeLast(15)) sb.Append("   ").Append(l.Trim()).Append('\n'); } catch { }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 🌐 VPN, names, long paths

    static bool IsNetwork(string p)
    {
        if (p.StartsWith(@"\\")) return true;
        try { return new DriveInfo(Path.GetPathRoot(p)!).DriveType == DriveType.Network; } catch { return false; }
    }

    static bool OfficeVpnDown(XJob j) =>
        PauseOnVpn && OfficeVpn.On && OfficeVpn.Now is OfficeVpn.State.Down or OfficeVpn.State.Connecting && (IsNetwork(j.Dest) || j.Sources.Any(IsNetwork))
        && !(Directory.Exists(j.Dest) && j.Sources.All(s => File.Exists(s) || Directory.Exists(s)));

    /// <summary>A network path that is not reachable (VPN down): the job waits and goes on by itself when it is back.</summary>
    static bool WaitForNetwork(XJob j)
    {
        var paths = j.Sources.Append(j.Dest).Where(IsNetwork).ToList();
        if (paths.Count == 0) return true;
        var waited = false;
        while (!paths.All(p => File.Exists(p) || Directory.Exists(p) || Directory.Exists(Path.GetPathRoot(p))))
        {
            if (j.StopAsked) { Stopped(j); return false; }
            if (j.PauseAsked) { j.State = "paused"; j.PauseAsked = false; return false; }
            if (!waited) { j.State = "waiting"; j.Note = "Waiting for the 🌐 VPN / network drive…"; Fire(); waited = true; }
            Thread.Sleep(5000);
        }
        if (waited) { j.Note = ""; j.State = "scanning"; Fire(); }
        return true;
    }

    static bool SameVolume(string a, string b)
    {
        try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase) && !a.StartsWith(@"\\"); }
        catch { return false; }
    }

    /// <summary>“name (2).ext” — the renaming rule, with the first free number.</summary>
    static string Unique(string path, bool folder)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = folder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = folder ? "" : Path.GetExtension(path);
        var rule = RenameRule.Contains("{n}") ? RenameRule : "{name}_({n}){ext}";
        for (int n = 2; n < 10000; n++)
        {
            var cand = Path.Combine(dir, rule.Replace("{name}", name).Replace("{n}", n.ToString()).Replace("{ext}", ext).Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd")));
            if (!File.Exists(Long(cand)) && !Directory.Exists(Long(cand))) return cand;
        }
        return path + " (copy)";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetLongPathName(string s, StringBuilder l, uint n);

    /// <summary>Explorer may hand over a very long path in its short “8.3” form (SLV-ZU~1): turned back into the real name.</summary>
    public static string LongName(string p)
    {
        p = (p ?? "").Trim().Trim('"');
        if (p.Length == 0 || !p.Contains('~')) return p;
        try
        {
            var sb = new StringBuilder(32768);
            var n = GetLongPathName(p.StartsWith(@"\\?\") ? p : (p.Length >= 248 ? (p.StartsWith(@"\\") ? @"\\?\UNC\" + p[2..] : @"\\?\" + p) : p), sb, (uint)sb.Capacity);
            if (n > 0) return Unlong(sb.ToString());
        }
        catch { }
        return p;
    }

    /// <summary>Paths over 260 characters for Nova's own file calls.</summary>
    static string Long(string p) => p.Length < 248 || p.StartsWith(@"\\?\") ? p : p.StartsWith(@"\\") ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;
    static string Unlong(string p) => p.StartsWith(@"\\?\UNC\") ? @"\\" + p[8..] : p.StartsWith(@"\\?\") ? p[4..] : p;

    public static string Size(long b) => b < 1024 ? b + " B" : b < 1 << 20 ? $"{b / 1024.0:0} KB" : b < 1L << 30 ? $"{b / 1048576.0:0.0} MB" : $"{b / 1073741824.0:0.00} GB";

    // ------------------------------------------------------------------ questions (on Nova's window thread)

    public static int Ask(string title, string text, params string[] buttons)
    {
        var res = buttons.Length - 1;
        void Show()
        {
            var lines = text.Split('\n').Sum(x => 1 + x.Length / 80);
            using var f = new NovaForm { Text = title, Width = 680, Height = Math.Min(760, 210 + lines * 21 + (buttons.Length > 3 ? 44 : 0)), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, TopMost = true };
            var s = new Stack { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18) };
            f.Controls.Add(s);
            s.Add(Th.L(title, 12, Th.Text, true));
            var l = s.Add(Th.L(text, 10, Th.Text)); l.MaximumSize = new Size(600, 0);
            var r = s.Add(Th.Row(true)); r.MaximumSize = new Size(600, 0);
            for (int i = 0; i < buttons.Length; i++) { var k = i; r.Controls.Add(Th.B(buttons[i], () => { res = k; f.Close(); }, i == 0)); }
            f.ShowDialog();
        }
        if (Program.UiContext == null) return res;
        if (SynchronizationContext.Current == Program.UiContext) Show(); else Program.UiContext.Send(_ => Show(), null);
        return res;
    }

    // ------------------------------------------------------------------ requests from Explorer's right-click menu

    static readonly List<string> clip = new();
    static bool clipCut;
    public static int ClipCount => clip.Count;
    static readonly Dictionary<string, List<string>> pending = new();
    static System.Threading.Timer flush;

    public static string RequestDir { get { var d = Path.Combine(Paths.Cache, "xfer"); Directory.CreateDirectory(d); return d; } }

    /// <summary>Explorer starts Nova once per selected item; the requests arriving together become one action.</summary>
    public static void Request(string verb, string path)
    {
        lock (pending)
        {
            if (!pending.TryGetValue(verb, out var l)) pending[verb] = l = new();
            if (path.Length > 0 && !l.Contains(path, StringComparer.OrdinalIgnoreCase)) l.Add(LongName(path));
            flush?.Dispose();
            flush = new System.Threading.Timer(_ => Flush(), null, 800, Timeout.Infinite);
        }
    }

    public static void ReadRequests()
    {
        try
        {
            foreach (var f in Directory.GetFiles(RequestDir, "*.req"))
            {
                try { var l = File.ReadAllText(f).Split('\n'); File.Delete(f); if (l.Length >= 1) Request(l[0].Trim(), l.Length > 1 ? l[1].Trim() : ""); }
                catch { }
            }
        }
        catch { }
    }

    static void Flush()
    {
        Dictionary<string, List<string>> todo;
        lock (pending) { todo = pending.ToDictionary(k => k.Key, v => v.Value.ToList()); pending.Clear(); }
        foreach (var (verb, paths) in todo)
            Program.UiContext?.Post(_ => { try { Handle(verb, paths); } catch (Exception e) { Log.Warn("transfer", verb + ": " + e.Message); } }, null);
    }

    static string Pick(string title)
    {
        using var d = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        var last = Store.Str("win_xf_lastdest", "");
        if (Directory.Exists(last)) d.InitialDirectory = last;
        if (d.ShowDialog() != DialogResult.OK) return null;
        Store.Set("win_xf_lastdest", d.SelectedPath);
        return d.SelectedPath;
    }

    static void Handle(string verb, List<string> paths)
    {
        switch (verb)
        {
            case "copy":
            case "cut":
                clip.Clear(); clip.AddRange(paths); clipCut = verb == "cut";
                Program.Balloon("📦 Nova transfer", $"{paths.Count} item(s) ready to {(clipCut ? "move" : "copy")} — right-click a folder ▸ Nova transfer ▸ Paste here with Nova.");
                break;
            case "copyto": case "moveto": case "mirrorto":
            {
                var to = Pick(verb == "moveto" ? "Move to…" : verb == "mirrorto" ? "Mirror to… (Nova asks before deleting anything)" : "Copy to…");
                if (to != null) Add(verb == "moveto" ? "move" : verb == "mirrorto" ? "mirror" : "copy", paths, to);
                break;
            }
            case "paste": case "pastemirror":
            {
                var dest = paths.FirstOrDefault() ?? "";
                if (!Directory.Exists(dest)) return;
                List<string> src; var cut = false;
                if (clip.Count > 0) { src = clip.ToList(); cut = clipCut; }
                else
                {
                    src = WindowsClipboard(out cut);
                    if (src.Count == 0) { Program.Balloon("📦 Nova transfer", "Nothing to paste — copy files first (Ctrl+C or Nova transfer ▸ Copy with Nova)."); return; }
                }
                Add(verb == "pastemirror" ? "mirror" : cut ? "move" : "copy", src, dest);
                if (cut) clip.Clear();
                break;
            }
            case "compare":
            {
                var to = Pick("Compare with… (nothing is copied)");
                if (to == null) return;
                var list = paths;
                Task.Run(() =>
                {
                    var rep = CompareReport(list, to);
                    Program.UiContext?.Post(_ => Report("✔ Compare", rep), null);
                });
                break;
            }
            case "tosource":
            {
                var all = ProjectSources.All().ToList();
                if (all.Count == 0) { Program.Balloon("📦 Nova transfer", "No project sources yet — add them in 🗂 Project sources."); return; }
                var names = all.Select(x => $"{x.S.Name} · {x.S.Kind}").ToArray();
                var i = Ask("🗂 Copy to project source", "Where should these items go?", names.Take(8).Append("Cancel").ToArray());
                if (i < Math.Min(8, all.Count)) Add("copy", paths, all[i].S.Path);
                break;
            }
            case "settings": Screens.Settings("📦 Transfer engine"); break;
            case "transfers": Screens.Transfers(); break;
        }
    }

    /// <summary>Files copied or cut with Ctrl+C / Ctrl+X in Explorer.</summary>
    public static List<string> WindowsClipboard(out bool cut)
    {
        cut = false;
        try
        {
            if (!Clipboard.ContainsFileDropList()) return new();
            var list = Clipboard.GetFileDropList().Cast<string>().ToList();
            if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms) { var b = new byte[4]; ms.Read(b, 0, 4); cut = (BitConverter.ToInt32(b, 0) & 2) == 2; }
            return list;
        }
        catch { return new(); }
    }

    public static void Report(string title, string text)
    {
        var f = new NovaForm { Text = title, Width = 900, Height = 640 };
        var t = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Th.Card, ForeColor = Th.Text, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f), Text = text };
        f.Controls.Add(t);
        f.Show();
    }

    /// <summary>🧪 Test on a sample folder: long path, Arabic name, many small files — copied, checked, then removed.</summary>
    public static string SelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nova transfer test " + DateTime.Now.ToString("HHmmss"));
        try
        {
            var src = Path.Combine(root, "مشروع SLV — source");
            var deep = src;
            while (deep.Length < 300) deep = Path.Combine(deep, "Shop Drawings Level 02 to Level 09 incl bulkheads");
            Directory.CreateDirectory(Long(deep));
            for (int i = 0; i < 200; i++) File.WriteAllText(Long(Path.Combine(i % 2 == 0 ? src : deep, $"file {i:000} — تقرير.txt")), new string('x', 1000 + i));
            var dst = Path.Combine(root, "destination");
            Directory.CreateDirectory(dst);
            var j = new XJob { Kind = "copy", Sources = new() { src }, Dest = dst, Threads = 16 };
            var sw = Stopwatch.StartNew();
            var it = new Item(src, Path.Combine(dst, Path.GetFileName(src)), null);
            j.LogFile = Path.Combine(root, "test.log");
            var code = RunRobocopy(j, Args(j, it, null, "different", false, new(), new(), new(), new()), sw);
            var left = CountDifferences(it, null, new(), new(), new());
            // a big file with one thread: the live % path
            var bigSrc = Path.Combine(root, "big"); Directory.CreateDirectory(bigSrc);
            var bigFile = Path.Combine(bigSrc, "model — نموذج.bin");
            using (var fs = File.Create(bigFile)) { fs.SetLength(48L << 20); }
            var j2 = new XJob { Kind = "copy", Sources = new() { bigFile }, Dest = dst, Threads = 1, LogFile = Path.Combine(root, "test2.log") };
            var it2 = new Item(bigSrc, dst, new List<string> { Path.GetFileName(bigFile) });
            var code2 = RunRobocopy(j2, Args(j2, it2, it2.Files, "different", true, new(), new(), new(), new()), Stopwatch.StartNew());
            var bigOk = code2 < 8 && j2.DoneFiles == 1 && j2.DoneBytes == 48L << 20 && File.Exists(Path.Combine(dst, Path.GetFileName(bigFile)));
            var ok = code < 8 && left == 0 && j.DoneFiles == 200 && bigOk;
            return $"{(ok ? "✅" : "❌")} {j.DoneFiles} of 200 files copied in {sw.Elapsed.TotalSeconds:0.0} s (Robocopy code {code}) · longest path {deep.Length + 40} characters · Arabic names · {left} difference(s) after the check" +
                   (CanCompress() ? " · compression available" : "") + $" · big file (one thread, live %): {(bigOk ? "✓" : $"✗ code {code2}, {j2.DoneFiles} file, {j2.DoneBytes:N0} bytes")}" + (ok ? "" : "\r\nLog: " + j.LogFile);
        }
        catch (Exception e) { return "❌ " + e.Message; }
        finally { try { Directory.Delete(Long(root), true); } catch { } }
    }
}

/// <summary>📦 “Nova transfer” in Explorer's right-click menu (files, folders, drives, folder background). Classic menu: in Windows 11 under
/// “Show more options” (or Shift + right-click).</summary>
static class XferMenu
{
    static readonly string[] Roots = { @"Software\Classes\*\shell\NovaTransfer", @"Software\Classes\Directory\shell\NovaTransfer", @"Software\Classes\Drive\shell\NovaTransfer", @"Software\Classes\Directory\Background\shell\NovaTransfer" };

    public static void Apply()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            foreach (var r in Roots) Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(r, false);
            if (!Transfer.Menu) return;
            var items = new (string Id, string Text, bool Folder, bool Back)[]
            {
                ("copy", "📋 Copy with Nova", false, false), ("cut", "✂ Cut (move) with Nova", false, false),
                ("paste", "📥 Paste here with Nova", true, true), ("pastemirror", "🔁 Paste here as mirror… (asks before deleting)", true, true),
                ("copyto", "📥 Copy to…", false, false), ("moveto", "📦 Move to…", false, false), ("mirrorto", "🔁 Mirror to… (asks before deleting)", false, false),
                ("tosource", "🗂 Copy to project source…", false, false), ("compare", "✔ Compare with… (no copy)", false, false),
                ("transfers", "📊 Transfers…", true, true), ("settings", "⚙ Transfer settings…", true, true),
            };
            for (int ri = 0; ri < Roots.Length; ri++)
            {
                var back = ri == 3; var folder = ri is 1 or 2;
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Roots[ri]);
                k.SetValue("MUIVerb", "🚀 Nova transfer");
                k.SetValue("Icon", "\"" + exe + "\",0");
                k.SetValue("SubCommands", "");
                k.SetValue("MultiSelectModel", "Player");
                int n = 0;
                foreach (var (id, text, forFolder, forBack) in items)
                {
                    if (back && !forBack) continue;
                    if (!back && !folder && id is "paste" or "pastemirror") continue;   // files: nothing to paste into
                    using var c = k.CreateSubKey($@"shell\{n++:00}{id}");
                    c.SetValue("MUIVerb", text);
                    c.SetValue("MultiSelectModel", "Player");
                    if (id is "copyto" or "transfers" or "compare") c.SetValue("CommandFlags", 0x20, Microsoft.Win32.RegistryValueKind.DWord);   // a line before it
                    using var cmd = c.CreateSubKey("command");
                    cmd.SetValue("", $"\"{exe}\" --xfer {id} \"{(back ? "%V" : "%1")}\"");
                }
            }
        }
        catch (Exception e) { Log.Error("transfer menu", e); }
    }
}

/// <summary>Optional: Ctrl+V in Explorer pastes with Nova (files only — text and the rename box keep Windows' own paste). If Nova cannot tell
/// which folder is open, the key goes to Explorer as usual.</summary>
static class PasteHook
{
    delegate IntPtr Proc(int code, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct KB { public uint vk, scan, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int cbSize, flags; public IntPtr active, focus, capture, menuOwner, moveSize, caret; public Rectangle rc; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public KEYBDINPUT ki; public long pad; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, Proc p, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO i);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint f);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);

    static IntPtr hook; static Proc keep;
    static readonly IntPtr Magic = new(0x4E4F5641);   // “NOVA”: our own re-sent Ctrl+V passes through

    public static void Apply()
    {
        try
        {
            if (Transfer.TakeOverPaste && hook == IntPtr.Zero) { keep = Hook; hook = SetWindowsHookEx(13, keep, GetModuleHandle(null), 0); }
            else if (!Transfer.TakeOverPaste && hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
        catch (Exception e) { Log.Warn("transfer", "Ctrl+V: " + e.Message); }
    }

    static string Class(IntPtr h) { var sb = new StringBuilder(128); GetClassName(h, sb, 128); return sb.ToString(); }

    static IntPtr Hook(int code, IntPtr w, IntPtr l)
    {
        try
        {
            if (code >= 0 && (w == (IntPtr)0x100 || w == (IntPtr)0x104))
            {
                var k = Marshal.PtrToStructure<KB>(l);
                if (k.vk == 0x56 && k.extra != Magic && (GetAsyncKeyState(0x11) & 0x8000) != 0 && (GetAsyncKeyState(0x12) & 0x8000) == 0 && (GetAsyncKeyState(0x10) & 0x8000) == 0)
                {
                    var fg = GetForegroundWindow();
                    if (Class(fg) == "CabinetWClass" && IsClipboardFormatAvailable(15))
                    {
                        var tid = GetWindowThreadProcessId(fg, out _);
                        var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                        // typing in the address bar, the search box or a rename: Windows' own paste
                        if (GetGUIThreadInfo(tid, ref gi) && !Class(gi.focus).Contains("Edit", StringComparison.OrdinalIgnoreCase))
                        {
                            Program.UiContext?.Post(_ => Paste(fg), null);
                            return (IntPtr)1;
                        }
                    }
                }
            }
        }
        catch { }
        return CallNextHookEx(hook, code, w, l);
    }

    static void Paste(IntPtr fg)
    {
        string folder = null;
        try
        {
            var title = new StringBuilder(512); GetWindowText(fg, title, 512);
            var t = Regex.Replace(title.ToString(), @" - (File Explorer|Explorateur de fichiers|مستكشف الملفات)$", "");
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            var hits = new List<string>();
            foreach (dynamic win in sh.Windows())
            {
                try
                {
                    if ((long)win.HWND != (long)fg) continue;
                    string p = win.Document.Folder.Self.Path;
                    string n = win.LocationName;
                    hits.Add(n == t ? "1" + p : "0" + p);
                }
                catch { }
            }
            // Windows 11 tabs share one window: the tab whose name is in the title
            var best = hits.Where(h => h[0] == '1').ToList();
            if (best.Count == 1) folder = best[0][1..]; else if (hits.Count == 1) folder = hits[0][1..];
        }
        catch { }
        if (folder == null || !Directory.Exists(folder)) { Resend(); return; }
        var src = Transfer.WindowsClipboard(out var cut);
        if (src.Count == 0) { Resend(); return; }
        Transfer.Add(cut ? "move" : "copy", src, folder);
    }

    static void Resend()
    {
        var i = new INPUT[4];
        i[0].type = i[1].type = i[2].type = i[3].type = 1;
        i[0].ki = new KEYBDINPUT { vk = 0x11, extra = Magic };
        i[1].ki = new KEYBDINPUT { vk = 0x56, extra = Magic };
        i[2].ki = new KEYBDINPUT { vk = 0x56, flags = 2, extra = Magic };
        i[3].ki = new KEYBDINPUT { vk = 0x11, flags = 2, extra = Magic };
        SendInput(4, i, Marshal.SizeOf<INPUT>());
    }
}
