using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using X = XstReader;

namespace NovaEnhance;

/// <summary>
/// 📂 Outlook archives (.pst) read DIRECTLY from the file — no Outlook, read-only (the file is never changed). Uses XstReader
/// (open source, Ms-PL). Each archive is known by a fingerprint of its contents, so renaming or moving it keeps everything;
/// emails already in Nova (from Outlook or another copy) are skipped. Designed by A.HAMROUNI
/// </summary>
static class Pst
{
    public class Info
    {
        public string Fp = "", Path = "", Name = "", Status = "";
        public long Size;
        public int Total, Indexed, Skipped, Seen;
        public bool Queued, Done;
        public List<string> Folders = new();   // empty = every folder
        public DateTime Added = DateTime.Now;
    }

    static readonly JsonSerializerOptions J = new() { IncludeFields = true };
    static readonly object L = new();

    public static Dictionary<string, Info> All
    {
        get { lock (L) { try { return JsonSerializer.Deserialize<Dictionary<string, Info>>(Store.Str("win_pstx", "{}"), J) ?? new(); } catch { return new(); } } }
    }

    static void Save(Info i) { lock (L) { var a = All; a[i.Fp] = i; Store.Set("win_pstx", JsonSerializer.Serialize(a, J)); } }
    public static void Forget(string fp) { lock (L) { var a = All; a.Remove(fp); Store.Set("win_pstx", JsonSerializer.Serialize(a, J)); } }

    // ------------------------------------------------------------------ opening a file

    public class Opened { public X.XstFile File; public X.Folder Root; public string Fp = "", Path = "", Relinked = ""; }

    static readonly Dictionary<string, Opened> open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens a .pst read-only and recognises it (renamed / moved archives are relinked, nothing is read again).</summary>
    public static Opened Open(string path, bool shared = true)
    {
        if (shared) lock (open) if (open.TryGetValue(path, out var o0) && File.Exists(path)) return o0;
        var x = new X.XstFile(path);
        var root = x.ReadFolderTree();
        var o = new Opened { File = x, Root = root, Path = path, Fp = Fingerprint(x, root) };
        var known = All.TryGetValue(o.Fp, out var i) ? i : null;
        if (known != null && !known.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            o.Relinked = System.IO.Path.GetFileName(known.Path);
            known.Path = path; known.Name = System.IO.Path.GetFileName(path);
            Save(known);
        }
        if (shared) lock (open) open[path] = o;
        return o;
    }

    /// <summary>The archive's fingerprint: its oldest emails (time + subject) — the same after a rename, a move or a copy.</summary>
    static string Fingerprint(X.XstFile x, X.Folder root)
    {
        var sample = new List<string>();
        foreach (var f in Walk(root).Where(f => f.ContentCount > 0).OrderByDescending(f => f.ContentCount).Take(3))
            try { sample.AddRange(x.ReadMessages(f).OrderBy(m => m.Date ?? DateTime.MaxValue).Take(25).Select(m => $"{m.Date:O}|{m.Subject}")); f.Messages.Clear(); }
            catch { }
        if (sample.Count == 0) sample.Add(root.Name + "|" + string.Join(",", Walk(root).Select(f => f.Name)));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("\n", sample))))[..16].ToLowerInvariant();
    }

    public static IEnumerable<X.Folder> Walk(X.Folder f)
    {
        yield return f;
        foreach (var c in f.Folders) foreach (var d in Walk(c)) yield return d;
    }

    public static bool Private(X.Folder f) => Projects.Private.IsMatch(f.Path ?? f.Name ?? "");

    public static string EntryOf(string fp, X.Message m) => $"pst:{fp}:{m.Nid.dwValue}";

    // ------------------------------------------------------------------ reading one email

    static string Prop(X.Message m, int tag) { try { return m.Properties.FirstOrDefault(p => (int)p.Tag == tag)?.Value?.ToString() ?? ""; } catch { return ""; } }

    public static string HtmlText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var t = Regex.Replace(html, @"<(script|style)[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<br\s*/?>|</p>|</div>|</tr>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "<[^>]+>", " ");
        t = System.Net.WebUtility.HtmlDecode(t);
        return Regex.Replace(Regex.Replace(t, @"[ \t ]+", " "), @"\n\s*\n\s*\n+", "\n\n").Trim();
    }

    public class Mail
    {
        public string Entry = "", FromName = "", FromAddr = "", To = "", Cc = "", ToA = "", CcA = "", Subject = "", Body = "", Html = "", Folder = "";
        public List<string> Files = new();
        public DateTime Date;
        public bool Sent;
    }

    /// <summary>Everything Nova needs from one email (reads its details from the file).</summary>
    public static Mail Read(Opened o, X.Message m, X.Folder f)
    {
        o.File.ReadMessageDetails(m);
        var r = new Mail { Entry = EntryOf(o.Fp, m), Subject = m.Subject ?? "", FromName = m.From ?? "", Folder = f.Path ?? f.Name ?? "", Date = m.Date ?? m.Modified ?? DateTime.MinValue };
        r.FromAddr = Prop(m, 0x5D01) is { Length: > 0 } smtp ? smtp : Prop(m, 0x0C1F);
        if (!r.FromAddr.Contains('@')) r.FromAddr = Prop(m, 0x5D01).Contains('@') ? Prop(m, 0x5D01) : "";
        var to = m.Recipients.Where(x => x.RecipientType == X.RecipientType.To).ToList();
        var cc = m.Recipients.Where(x => x.RecipientType == X.RecipientType.Cc).ToList();
        r.To = to.Count > 0 ? string.Join("; ", to.Select(x => x.DisplayName)) : m.To ?? "";
        r.Cc = cc.Count > 0 ? string.Join("; ", cc.Select(x => x.DisplayName)) : m.Cc ?? "";
        r.ToA = string.Join("; ", to.Select(x => x.EmailAddress).Where(a => a?.Contains('@') == true));
        r.CcA = string.Join("; ", cc.Select(x => x.EmailAddress).Where(a => a?.Contains('@') == true));
        try { r.Html = m.GetBodyAsHtmlString() ?? ""; } catch { r.Html = ""; }
        r.Body = !string.IsNullOrWhiteSpace(m.Body) ? m.Body : HtmlText(r.Html);
        r.Files = m.Attachments.Where(a => a.IsFile && !a.Hidden && !string.IsNullOrEmpty(a.FileName)).Select(a => a.FileName).ToList();
        r.Sent = Regex.IsMatch(r.Folder, @"\b(sent|envoy|gesendet)", RegexOptions.IgnoreCase) || Me.Addrs.Contains(r.FromAddr, StringComparer.OrdinalIgnoreCase);
        return r;
    }

    /// <summary>Finds an email of an archive by its Nova id ("pst:fingerprint:node").</summary>
    public static (Opened O, X.Message M, X.Folder F) Find(string entry)
    {
        var parts = entry.Split(':');
        if (parts.Length < 3 || !All.TryGetValue(parts[1], out var info) || !File.Exists(info.Path)) return (null, null, null);
        var o = Open(info.Path);
        var nid = uint.Parse(parts[2]);
        foreach (var f in Walk(o.Root).Where(f => f.ContentCount > 0))
        {
            var list = f.Messages.Count > 0 ? f.Messages : o.File.ReadMessages(f);
            var m = list.FirstOrDefault(x => x.Nid.dwValue == nid);
            if (m != null) return (o, m, f);
        }
        return (o, null, null);
    }

    public static string SaveAttachment(string entry, string fileName, string dir)
    {
        var (o, m, _) = Find(entry);
        if (m == null) throw new FileNotFoundException("The archive file of this email was not found (moved or deleted?) — open it in 📂 Archive reader.");
        o.File.ReadMessageDetails(m);
        var a = m.Attachments.FirstOrDefault(x => x.FileName == fileName) ?? throw new FileNotFoundException(fileName);
        var path = System.IO.Path.Combine(dir, fileName);
        o.File.SaveAttachment(path, m.Date, a);
        return path;
    }

    // ------------------------------------------------------------------ ➕ adding an archive to the Nova index (in the background)

    /// <summary>How much of an archive is already in Nova (a quick sample) — to spot a copy of an archive indexed before.</summary>
    public static int AlreadyInNova(Opened o)
    {
        int seen = 0, same = 0;
        using var c = MailIndex.Open();
        foreach (var f in Walk(o.Root).Where(f => f.ContentCount > 0).OrderByDescending(f => f.ContentCount).Take(6))
        {
            var ms = o.File.ReadMessages(f);
            foreach (var m in ms.Where((_, i) => i % Math.Max(1, ms.Count / 50) == 0).Take(50))
            {
                seen++;
                if (MailIndex.HasEntry(c, EntryOf(o.Fp, m)) || (m.Date is { } d && MailIndex.HasSame(c, d, m.Subject))) same++;
            }
            f.Messages.Clear();
        }
        return seen == 0 ? 0 : 100 * same / seen;
    }

    public static Info Add(Opened o, List<string> folders)
    {
        var i = All.TryGetValue(o.Fp, out var k) ? k : new Info { Fp = o.Fp };
        i.Path = o.Path; i.Name = System.IO.Path.GetFileName(o.Path);
        try { i.Size = new FileInfo(o.Path).Length; } catch { }
        i.Folders = folders ?? new();
        i.Total = (int)Walk(o.Root).Where(f => !Private(f) && (i.Folders.Count == 0 || i.Folders.Contains(f.Path))).Sum(f => (long)f.ContentCount);
        i.Queued = true; i.Done = false; i.Status = "waiting…";
        Save(i);
        Start();
        wake.Set();
        return i;
    }

    public static volatile string Current = "";
    public static volatile int Rate;   // emails per minute
    static Thread thread;
    static readonly AutoResetEvent wake = new(false);
    static volatile bool stopNow;

    public static void Start()
    {
        if (thread != null) return;
        thread = new Thread(() => Guard.Run("archive reader", Loop)) { IsBackground = true, Name = "Nova pst", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    public static void Pause(string fp) { if (All.TryGetValue(fp, out var i)) { i.Queued = false; i.Status = "⏸ paused"; Save(i); stopNow = true; } }

    static void Loop()
    {
        while (true)
        {
            var next = All.Values.FirstOrDefault(i => i.Queued && !i.Done && File.Exists(i.Path));
            if (next == null) { Current = ""; wake.WaitOne(TimeSpan.FromMinutes(5)); continue; }
            stopNow = false;
            try { IndexOne(next); }
            catch (Exception e) { next.Status = "⚠ " + e.Message; next.Queued = false; Save(next); Log.Error("archive reader · " + next.Name, e); }
        }
    }

    static void IndexOne(Info info)
    {
        Current = info.Fp;
        using var act = Activity.Begin("archive reader · add to index", info.Name);
        var o = Open(info.Path, false);   // its own copy: the reader window can browse the same file meanwhile
        var store = "pst:" + o.Fp;
        var name = System.IO.Path.GetFileNameWithoutExtension(info.Path);
        using var c = MailIndex.Open();
        using (var bt = c.CreateCommand()) { bt.CommandText = "PRAGMA busy_timeout=30000;"; bt.ExecuteNonQuery(); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int doneAtStart = info.Indexed + info.Skipped, batch = 0;
        SqliteTransaction tx = null;
        try
        {
            foreach (var f in Walk(o.Root).Where(f => f.ContentCount > 0 && !Private(f) && (info.Folders.Count == 0 || info.Folders.Contains(f.Path))))
            {
                var list = o.File.ReadMessages(f);
                var fid = store + ":" + f.Path;
                int seenHere = 0;
                foreach (var m in list)
                {
                    if (stopNow) { info.Status = "⏸ paused"; Save(info); return; }
                    // Gentle: during working hours, slowly (unless the PC is idle).
                    if (Gentle.On && Gentle.WorkingNow && Gentle.Idle < TimeSpan.FromMinutes(3)) Thread.Sleep(15);
                    seenHere++;
                    var entry = EntryOf(o.Fp, m);
                    if (MailIndex.HasEntry(c, entry)) continue;
                    // Already in Nova (from Outlook or another copy of this archive): skipped, not read again.
                    if (m.Date is { } d && MailIndex.HasSame(c, d, m.Subject)) { info.Skipped++; continue; }
                    Mail r;
                    try { r = Read(o, m, f); }
                    catch (Exception e) { Log.Warn("archive reader", $"{info.Name} · {m.Subject}: {e.Message}"); continue; }
                    finally { try { m.ClearContents(); } catch { } }
                    tx ??= c.BeginTransaction();
                    var body = r.Body.Length > 200000 ? r.Body[..200000] : r.Body;
                    MailIndex.InsertExternal(c, entry, store, Regex.Replace(r.Subject, @"^\s*((re|fw|fwd|aw|wg)\s*:\s*)+", "", RegexOptions.IgnoreCase).Trim().ToLowerInvariant(),
                        name + " › " + r.Folder, r.Sent, r.Date, r.FromName, r.FromAddr, r.To, r.Cc, r.ToA, r.CcA, r.Subject, body, string.Join("; ", r.Files), "", fid);
                    info.Indexed++;
                    if (++batch >= 200)
                    {
                        tx.Commit(); tx.Dispose(); tx = null; batch = 0;
                        var mins = Math.Max(0.1, sw.Elapsed.TotalMinutes);
                        Rate = (int)((info.Indexed + info.Skipped - doneAtStart) / mins);
                        info.Status = $"reading {f.Path}";
                        Save(info);
                    }
                }
                if (tx != null) { tx.Commit(); tx.Dispose(); tx = null; batch = 0; }
                MailIndex.UpsertFolder(c, fid, store, "📂 " + name, f.Path, (int)f.ContentCount, seenHere);
                f.Messages.Clear();
            }
            info.Done = true; info.Queued = false;
            info.Status = $"✓ complete · {info.Indexed:N0} added · {info.Skipped:N0} were already in Nova";
            Save(info);
            act.Result = info.Status;
        }
        finally
        {
            try { tx?.Commit(); tx?.Dispose(); } catch { }
            Current = "";
            MailProjects.Wake();
        }
    }

    public static string Line()
    {
        var a = All.Values.Where(i => i.Queued || i.Done || i.Indexed > 0).ToList();
        return string.Join(" · ", a.Select(i => $"📂 {i.Name}: {(i.Total > 0 ? Math.Min(100, 100L * (i.Indexed + i.Skipped) / i.Total) : 0)} %"));
    }
}
