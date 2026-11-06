using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NovaEnhance;

/// <summary>Talks to the classic Outlook already installed and signed in on this PC.</summary>
/// <summary>Stops an index pass at once when Outlook goes away or is busy; the next pass carries on where this one stopped.</summary>
class OutlookPause : Exception { public readonly bool Closed; public OutlookPause(bool closed, Exception inner) : base(closed ? "Outlook was closed" : "Outlook is busy", inner) { Closed = closed; } }

static class Outlook
{
    [DllImport("ole32.dll")] static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);
    [DllImport("oleaut32.dll")] static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    public static bool Installed => Type.GetTypeFromProgID("Outlook.Application") != null;

    /// <summary>The running Outlook, or null (the index never starts Outlook by itself).</summary>
    public static dynamic Running()
    {
        try
        {
            if (CLSIDFromProgID("Outlook.Application", out var clsid) != 0) return null;
            return GetActiveObject(ref clsid, IntPtr.Zero, out var o) == 0 ? o : null;
        }
        catch { return null; }
    }

    /// <summary>The running Outlook, or starts it (used when the user opens an email or a draft).</summary>
    public static dynamic Get()
    {
        var o = Running();
        if (o != null) return o;
        var t = Type.GetTypeFromProgID("Outlook.Application") ?? throw new InvalidOperationException("Outlook isn't installed on this PC");
        return Activator.CreateInstance(t);
    }

    /// <summary>Outlook was closed, restarted or crashed while Nova was reading it (the connection is gone).</summary>
    public static bool Gone(Exception e)
    {
        for (var x = e; x != null; x = x.InnerException)
            if ((uint)x.HResult is 0x800706BA or 0x800706BE or 0x80010108 or 0x80010012) return true;
        return false;
    }

    /// <summary>Outlook is busy (a dialog is open, or it is syncing) and refused the call for now.</summary>
    public static bool Busy(Exception e)
    {
        for (var x = e; x != null; x = x.InnerException)
            if ((uint)x.HResult is 0x80010001 or 0x8001010A) return true;
        return false;
    }

    public static void Release(object o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
    }

    public static string S(Func<object> f)
    {
        try { return Convert.ToString(f()) ?? ""; } catch { return ""; }
    }

    /// <summary>Outlook's own mark on a received email: 102 replied · 103 replied to all · 104 forwarded, with when. (0, null) when unknown.</summary>
    public static (int Verb, DateTime? When) LastVerb(dynamic ns, string entry, string store)
    {
        dynamic item = null;
        try
        {
            item = ns.GetItemFromID(entry, store);
            dynamic pa = item.PropertyAccessor;
            int verb = 0; DateTime? when = null;
            try { verb = Convert.ToInt32(pa.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x10810003")); } catch { }
            try { when = ((DateTime)pa.UTCToLocalTime(pa.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x10820040"))); } catch { }
            Release(pa);
            return (verb, when);
        }
        catch { return (0, null); }
        finally { Release(item); }
    }

    /// <summary>Saves the email as an Outlook .msg file (with its attachments).</summary>
    public static void SaveMsg(string entry, string store, string path)
    {
        dynamic app = Get();
        dynamic ns = app.GetNamespace("MAPI");
        dynamic item = ns.GetItemFromID(entry, store);
        try { item.SaveAs(path, 9); }   // olMSGUnicode
        finally { Release(item); Release(ns); }
    }

    public static void Open(string entry, string store)
    {
        dynamic app = Get();
        dynamic ns = app.GetNamespace("MAPI");
        dynamic item = ns.GetItemFromID(entry, store);
        item.Display(false);
    }

    /// <summary>Opens a reply in Outlook with the draft on top. Nothing is sent: the user checks and sends.</summary>
    public static void Reply(string entry, string store, string draft, bool all)
    {
        dynamic app = Get();
        dynamic ns = app.GetNamespace("MAPI");
        dynamic item = ns.GetItemFromID(entry, store);
        dynamic reply = all ? item.ReplyAll() : item.Reply();
        var html = System.Net.WebUtility.HtmlEncode(draft ?? "").Replace("\r\n", "\n").Replace("\n", "<br>");
        reply.Display(false);
        string existing = Convert.ToString(reply.HTMLBody) ?? "";
        var div = "<div style=\"font-family:Calibri,Arial,sans-serif;font-size:11pt\">" + html + "</div>";
        var m = Regex.Match(existing, "<body[^>]*>", RegexOptions.IgnoreCase);
        reply.HTMLBody = m.Success ? existing.Insert(m.Index + m.Length, div) : div + existing;
    }
}

/// <summary>The user's own name and addresses, so Nova knows when "I" was in To or Cc.</summary>
static class Me
{
    public static string Name = "";
    public static List<string> Addrs = new();

    public static string FirstName
    {
        get
        {
            var n = Name.Trim();
            if (n.Contains(',')) n = n[(n.IndexOf(',') + 1)..].Trim();   // "Hamrouni, Ahmed"
            var parts = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : "";
        }
    }

    public static bool IsMe(string addr) => !string.IsNullOrEmpty(addr) && Addrs.Any(a => a.Equals(addr, StringComparison.OrdinalIgnoreCase));
}

class MailHit
{
    public long Id;
    public string Entry, StoreId, Conv, Folder, FromName, FromAddr, To, Cc, ToAddr, CcAddr, Subject, Body, AttNames;
    public bool Sent, Unread;
    public int Importance;
    public DateTime Date;
    /// <summary>needs · week · fyi · follow · "" (plain search)</summary>
    public string Status = "";
    public string Summary = "";
    public bool? NeedsReply;
    /// <summary>Outstanding lists: why Nova thinks it was probably answered already (shown in the grey section), or "".</summary>
    public string MaybeAnswered = "";
    /// <summary>Outstanding lists: how many earlier emails of the same conversation/topic are folded into this card.</summary>
    public int Earlier;

    public string NewPart => MailIndex.NewPart(Body ?? "");
    public string FirstLines(int n = 300) { var t = Regex.Replace(NewPart, @"\s+", " ").Trim(); return t.Length > n ? t[..n] + "…" : t; }
    public int DaysWaiting => Math.Max(0, (int)(DateTime.Now - Date).TotalDays);
}

class MailQuery
{
    public List<string> From = new(), To = new(), Cc = new(), Words = new();
    public string Phrase = "";
    public string MeIn;            // to · cc · any · null
    public DateTime? After, Before;
    public string Folder = "any";  // received · sent · any
    public bool? HasAttach, Unread;
    public bool Unanswered, FollowUp;
    public int Limit = 50;
}

/// <summary>
/// A private search index of the Outlook mail on this PC (and attachment text), so Ask Nova answers in about a second.
/// The database is encrypted (SQLCipher) with a key locked to this Windows account.
/// </summary>
static class MailIndex
{
    static Thread th;
    static readonly AutoResetEvent wake = new(false);
    static volatile bool stop;
    static readonly object InitLock = new();
    static bool inited, batteries;
    static readonly Dictionary<string, string> exCache = new(StringComparer.OrdinalIgnoreCase);

    public static volatile string Status = "Index not started yet";
    public static bool Fts { get; private set; } = true;
    public static bool Running => th != null;

    static string Dir => Store.IndexLocal ? Paths.MailIndexLocal : Paths.MailIndexDocs;
    static string DbPath => Path.Combine(Dir, "mail.db");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nova Enhance mail index");

    static string KeyHex()
    {
        var f = Path.Combine(Dir, "key.bin");
        byte[] key;
        if (File.Exists(f)) key = ProtectedData.Unprotect(File.ReadAllBytes(f), Entropy, DataProtectionScope.CurrentUser);
        else
        {
            key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(f, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        }
        return Convert.ToHexString(key);
    }

    public static SqliteConnection Open()
    {
        lock (InitLock)
        {
            if (!batteries) { SQLitePCL.Batteries_V2.Init(); batteries = true; }
        }
        var cs = new SqliteConnectionStringBuilder { DataSource = DbPath, Password = KeyHex(), Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=8000;");
        lock (InitLock)
        {
            if (!inited) { Init(c); inited = true; }
        }
        return c;
    }

    static void Exec(SqliteConnection c, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static object Scalar(SqliteConnection c, string sql, params (string, object)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    static void Init(SqliteConnection c)
    {
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, @"CREATE TABLE IF NOT EXISTS mail(id INTEGER PRIMARY KEY, entry TEXT UNIQUE, store TEXT, conv TEXT, folder TEXT, sent INTEGER,
            date INTEGER, from_name TEXT, from_addr TEXT, to_list TEXT, cc_list TEXT, to_addr TEXT, cc_addr TEXT, subject TEXT, body TEXT,
            att_names TEXT, att_text TEXT, unread INTEGER, importance INTEGER, flagged INTEGER);");
        Exec(c, "CREATE INDEX IF NOT EXISTS mail_conv ON mail(conv, date);");
        Exec(c, "CREATE INDEX IF NOT EXISTS mail_date ON mail(date);");
        Exec(c, "CREATE TABLE IF NOT EXISTS state(k TEXT PRIMARY KEY, v TEXT);");
        // Which Outlook folder each email came from (for the folder list in Settings and leaving folders out).
        try { Exec(c, "ALTER TABLE mail ADD COLUMN fid TEXT;"); } catch { }
        Exec(c, "CREATE INDEX IF NOT EXISTS mail_fid ON mail(fid);");
        Exec(c, "CREATE TABLE IF NOT EXISTS folders(fid TEXT PRIMARY KEY, store TEXT, store_name TEXT, path TEXT, total INTEGER, seen INTEGER);");
        try
        {
            Exec(c, "CREATE VIRTUAL TABLE IF NOT EXISTS mail_fts USING fts5(subject, body, from_name, to_list, cc_list, att_names, att_text, content='mail', content_rowid='id', tokenize='unicode61 remove_diacritics 2');");
            Fts = true;
        }
        catch (Exception e) { Fts = false; Log.Write("Full-text search not available, using simple search: " + e.Message); }
        var n = Get(c, "me_name"); if (n != null) Me.Name = n;
        var a = Get(c, "me_addrs"); if (a != null) Me.Addrs = a.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    static string Get(SqliteConnection c, string k) => Scalar(c, "SELECT v FROM state WHERE k=$k", ("$k", k)) as string;
    static void Put(SqliteConnection c, string k, string v) => Exec(c, "INSERT INTO state(k,v) VALUES($k,$v) ON CONFLICT(k) DO UPDATE SET v=$v", ("$k", k), ("$v", v));
    static long GetL(SqliteConnection c, string k, long def) => long.TryParse(Get(c, k), out var x) ? x : def;

    public static long Count()
    {
        try { using var c = Open(); return Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM mail")); } catch { return 0; }
    }

    // ================================================================== background indexer

    public static void Start()
    {
        if (th != null) return;
        stop = false;
        th = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Nova mail index" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }

    public static void Stop()
    {
        stop = true;
        wake.Set();
        try { th?.Join(5000); } catch { }
        th = null;
    }

    public static void Now() => wake.Set();

    static void Loop()
    {
        if (!stop) wake.WaitOne(TimeSpan.FromSeconds(10));
        while (!stop)
        {
            try
            {
                if (Store.EmailSource == "m365") Status = "Microsoft 365 connection comes in build 2 (needs IT approval) — choose \"Outlook on this PC\" or \"Both\"";
                else Pass();
            }
            catch (Exception e) when (e is OutlookPause || Outlook.Gone(e) || Outlook.Busy(e))
            {
                bool closed = e is OutlookPause op ? op.Closed : Outlook.Gone(e);
                Status = closed ? $"Outlook was closed at {DateTime.Now:HH:mm} — the index carries on by itself when Outlook is open"
                                : $"Outlook was busy at {DateTime.Now:HH:mm} — trying again in 2 minutes";
                Log.Warn("index", closed ? "Outlook was closed or restarted while the mail index was reading it — will continue later (nothing lost)" : "Outlook was busy — will try again");
                wake.WaitOne(TimeSpan.FromMinutes(2));
                continue;
            }
            catch (Exception e) { Status = "⚠ " + e.Message; Log.Error("index", e); }
            wake.WaitOne(TimeSpan.FromMinutes(10));
        }
    }

    /// <summary>Deletes the index and builds it again (e.g. after changing the search range).</summary>
    public static void Rebuild()
    {
        Stop();
        Store.SetBool("win_mail_complete", false);
        try
        {
            lock (InitLock) inited = false;
            foreach (var f in new[] { "mail.db", "mail.db-wal", "mail.db-shm" }) { var p = Path.Combine(Dir, f); if (File.Exists(p)) File.Delete(p); }
            Status = "Index deleted — building again";
        }
        catch (Exception e) { Status = "⚠ Could not delete the index: " + e.Message; }
        Start();
        Now();
    }

    /// <summary>Moves the index between Documents\Nova Enhance and this PC only.</summary>
    public static void Move(bool local)
    {
        Stop();
        var from = Dir;
        Store.IndexLocal = local;
        var to = Dir;
        try
        {
            if (!from.Equals(to, StringComparison.OrdinalIgnoreCase))
                foreach (var f in new[] { "mail.db", "mail.db-wal", "mail.db-shm", "key.bin" })
                {
                    var a = Path.Combine(from, f);
                    if (File.Exists(a)) File.Move(a, Path.Combine(to, f), true);
                }
            lock (InitLock) inited = false;
        }
        catch (Exception e) { Log.Error("index move", e); }
        Start();
    }

    static void Pass()
    {
        dynamic app = Outlook.Running();
        if (app == null)
        {
            Status = Outlook.Installed ? "Waiting for Outlook — open Outlook and the index updates by itself" : "Outlook isn't installed on this PC";
            return;
        }
        dynamic ns = null;
        try
        {
            ns = app.GetNamespace("MAPI");
            using var c = Open();
            LoadMe((object)ns, c);
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in new[] { 3, 4, 16, 23 }) // deleted, outbox, drafts, junk
                try { skip.Add(Outlook.S(() => ns.GetDefaultFolder(id).EntryID)); } catch { }
            string sentId = "";
            try { sentId = Outlook.S(() => ns.GetDefaultFolder(5).EntryID); } catch { }
            var cutoff = Store.CutOff;
            Status = "Preparing the index — checking your mailboxes and archives…";
            // Emails from me filed in other folders count as sent (fixes rows indexed before build 19).
            foreach (var a in Me.Addrs) Exec(c, "UPDATE mail SET sent=1 WHERE sent=0 AND from_addr=$a COLLATE NOCASE", ("$a", a));
            if (Me.Name.Length > 0) Exec(c, "UPDATE mail SET sent=1 WHERE sent=0 AND from_name=$n COLLATE NOCASE", ("$n", Me.Name));
            doneThisPass = new(StringComparer.OrdinalIgnoreCase);
            Deferred = false;
            folderOrder = 0;
            // Every store except public folders: your mailbox first, then shared mailboxes / Online Archive / Groups, then .pst archives.
            // ExchangeStoreType: 0 primary · 1 delegate/shared · 2 public folders · 3 not Exchange (.pst) · 4 additional mailbox
            dynamic stores = ns.Stores;
            int n = (int)stores.Count;
            var order = new List<(int Index, int Rank)>();
            for (int i = 1; i <= n; i++)
            {
                int type = 0;
                try { dynamic st0 = stores.Item(i); try { type = (int)st0.ExchangeStoreType; } catch { } Outlook.Release(st0); } catch { }
                if (type == 2) continue;
                order.Add((i, type == 0 ? 0 : type == 3 ? 2 : 1));
            }
            foreach (var (i, _) in order.OrderBy(x => x.Rank).ThenBy(x => x.Index))
            {
                if (stop) break;
                dynamic st = stores.Item(i);
                try
                {
                    string storeId = Outlook.S(() => st.StoreID);
                    storeName = Outlook.S(() => st.DisplayName);
                    // Inbox and Sent Items first, so new mail and your replies are known early.
                    foreach (var which in new[] { 6, 5 })
                    {
                        if (stop) break;
                        dynamic df = null;
                        try { df = st.GetDefaultFolder(which); } catch { }
                        if (df == null) continue;
                        try { IndexOne(c, (object)df, Outlook.S(() => df.EntryID), Outlook.S(() => df.Name), Outlook.S(() => df.Name), storeId, which == 5 ? Outlook.S(() => df.EntryID) : sentId, cutoff); }
                        finally { Outlook.Release(df); }
                    }
                    dynamic root = st.GetRootFolder();
                    Walk(c, (object)root, storeId, sentId, skip, cutoff, 0);
                    Outlook.Release(root);
                }
                catch (OutlookPause) { throw; }
                catch (Exception e) when (Outlook.Gone(e) || Outlook.Busy(e)) { throw new OutlookPause(Outlook.Gone(e), e); }
                catch (Exception e) { Log.Error("index store", e); }
                finally { Outlook.Release(st); }
            }
            Outlook.Release(stores);
            if (!stop)
            {
                if (!Deferred) Store.SetBool("win_mail_complete", true);
                var count = Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM mail"));
                Status = Deferred
                    ? $"✓ {count:N0} emails indexed · new mail is up to date · older mail continues after {Gentle.EndHour}:00 (gentle indexing)"
                    : $"✓ {count:N0} emails indexed · updated {DateTime.Now:HH:mm}";
            }
        }
        finally { Outlook.Release(ns); Outlook.Release(app); }
    }

    static void LoadMe(dynamic ns, SqliteConnection c)
    {
        try
        {
            var addrs = new List<string>();
            dynamic cu = ns.CurrentUser;
            var name = Outlook.S(() => cu.Name);
            try
            {
                dynamic ae = cu.AddressEntry;
                string a = Outlook.S(() => ae.Address);
                try { dynamic u = ae.GetExchangeUser(); if (u != null) { a = Outlook.S(() => u.PrimarySmtpAddress); Outlook.Release(u); } } catch { }
                if (a.Length > 0) addrs.Add(a);
                Outlook.Release(ae);
            }
            catch { }
            try
            {
                dynamic accs = ns.Accounts;
                for (int i = 1; i <= (int)accs.Count; i++)
                {
                    dynamic acc = accs.Item(i);
                    var s = Outlook.S(() => acc.SmtpAddress);
                    if (s.Length > 0 && !addrs.Contains(s, StringComparer.OrdinalIgnoreCase)) addrs.Add(s);
                    Outlook.Release(acc);
                }
            }
            catch { }
            if (name.Length > 0) { Me.Name = name; Put(c, "me_name", name); }
            if (addrs.Count > 0) { Me.Addrs = addrs; Put(c, "me_addrs", string.Join("|", addrs)); }
        }
        catch (Exception e) { Log.Error("me", e); }
    }

    static readonly string[] SkipNames = { "Sync Issues", "Conversation History", "RSS Feeds", "RSS Subscriptions", "Calendar", "Contacts", "Tasks", "Notes", "Journal", "Search Folders", "Finder", "Search Root" };

    /// <summary>Folders the user left out (Settings → Email & AI → Mailboxes and folders).</summary>
    public static HashSet<string> LeftOut()
    {
        try { return new HashSet<string>((JsonNode.Parse(Store.Str("win_mail_skip", "[]"))?.AsArray() ?? new JsonArray()).Select(x => x?.ToString() ?? ""), StringComparer.OrdinalIgnoreCase); }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    public static void SetLeftOut(string fid, bool leftOut)
    {
        var s = LeftOut();
        if (leftOut) s.Add(fid); else s.Remove(fid);
        Store.Set("win_mail_skip", new JsonArray(s.Select(x => (JsonNode)x).ToArray()).ToJsonString());
    }

    /// <summary>Every mail folder Nova has seen, with Outlook's count and how many are in the index.</summary>
    public static List<(string Fid, string StoreName, string Path, int Total, int Indexed)> Folders()
    {
        var res = new List<(string, string, string, int, int)>();
        try
        {
            using var c = Open();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT fid, COUNT(*) FROM mail WHERE fid IS NOT NULL GROUP BY fid";
                using var r = cmd.ExecuteReader();
                while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT fid, store_name, path, total FROM folders ORDER BY seen, store_name, path";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var fid = r.GetString(0);
                    res.Add((fid, r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? 0 : r.GetInt32(3), counts.TryGetValue(fid, out var n) ? n : 0));
                }
            }
        }
        catch (Exception e) { Log.Error("folder list", e); }
        return res;
    }

    public static bool Deferred;
    public static bool Complete => Store.Bool("win_mail_complete", false);
    static HashSet<string> doneThisPass = new(StringComparer.OrdinalIgnoreCase);
    static string storeName = "";
    static int folderOrder;

    static void IndexOne(SqliteConnection c, dynamic folder, string id, string name, string path, string storeId, string sentId, DateTime cutoff)
    {
        if (!doneThisPass.Add(id)) return;
        Status = $"Checking {storeName} › {path}…";
        int total = 0;
        try { total = (int)folder.Items.Count; } catch { }
        // Older rows (before build 19) get their folder id now; no rebuild needed.
        Exec(c, "UPDATE mail SET fid=$fid WHERE fid IS NULL AND store=$s AND folder=$f", ("$fid", id), ("$s", storeId), ("$f", name));
        Exec(c, "INSERT INTO folders(fid,store,store_name,path,total,seen) VALUES($fid,$s,$sn,$p,$t,$o) ON CONFLICT(fid) DO UPDATE SET store_name=$sn, path=$p, total=$t, seen=$o",
            ("$fid", id), ("$s", storeId), ("$sn", storeName), ("$p", path), ("$t", total), ("$o", folderOrder++));
        if (LeftOut().Contains(id)) return;
        bool sent = id.Equals(sentId, StringComparison.OrdinalIgnoreCase) || name.Contains("Sent", StringComparison.OrdinalIgnoreCase);
        IndexFolder(c, (object)folder, id, name, storeId, sent, cutoff);
    }

    static void Walk(SqliteConnection c, dynamic folder, string storeId, string sentId, HashSet<string> skip, DateTime cutoff, int depth, string path = "")
    {
        if (stop || depth > 8) return;
        string id = Outlook.S(() => folder.EntryID);
        string name = Outlook.S(() => folder.Name);
        if (skip.Contains(id) || SkipNames.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase))) return;
        var here = depth == 0 ? "" : (path.Length > 0 ? path + " › " : "") + name;
        int type = -1;
        try { type = (int)folder.DefaultItemType; } catch { }
        if (type == 0 && depth > 0)
        {
            try { IndexOne(c, (object)folder, id, name, here, storeId, sentId, cutoff); }
            catch (OutlookPause) { throw; }
            catch (Exception e) when (Outlook.Gone(e) || Outlook.Busy(e)) { throw new OutlookPause(Outlook.Gone(e), e); }
            catch (Exception e) { Log.Error("index folder " + name, e); }
        }
        dynamic subs = null;
        try
        {
            subs = folder.Folders;
            int n = (int)subs.Count;
            for (int i = 1; i <= n && !stop; i++)
            {
                dynamic f = subs.Item(i);
                try { Walk(c, (object)f, storeId, sentId, skip, cutoff, depth + 1, here); } finally { Outlook.Release(f); }
            }
        }
        catch (OutlookPause) { throw; }
        catch (Exception e) when (Outlook.Gone(e)) { throw new OutlookPause(true, e); }
        catch { }
        finally { Outlook.Release(subs); }
    }

    static long Ms(DateTime d) => new DateTimeOffset(d).ToUnixTimeMilliseconds();

    /// <summary>
    /// Newest first. "hi/lo" remember which date span of this folder is already complete, so later passes only add new mail
    /// and continue further back after an interruption.
    /// </summary>
    static void IndexFolder(SqliteConnection c, dynamic folder, string fid, string fname, string storeId, bool sent, DateTime cutoff)
    {
        dynamic items = folder.Items;
        try
        {
            if ((int)items.Count == 0) return;
            items.Sort(sent ? "[SentOn]" : "[ReceivedTime]", true);
            long hi = GetL(c, "hi:" + fid, 0), lo = GetL(c, "lo:" + fid, long.MaxValue);
            bool fresh = hi == 0;
            long oldHi = hi, newHi = hi;
            bool segDone = fresh;
            int batch = 0, errors = 0, seen = 0;
            var tx = c.BeginTransaction();
            dynamic it = items.GetFirst();
            while (it != null && !stop)
            {
                try
                {
                    int cls = 0;
                    try { cls = (int)it.Class; } catch { }
                    if (cls == 43)
                    {
                        DateTime d = sent ? (DateTime)it.SentOn : (DateTime)it.ReceivedTime;
                        if (d < cutoff) { Outlook.Release(it); it = null; break; }
                        long ms = Ms(d);
                        // Gentle indexing: in working hours only new mail (and the last 14 days); older mail waits for the evening.
                        if (ms <= oldHi && Gentle.HoldBacklog(d)) { Deferred = true; Outlook.Release(it); it = null; break; }
                        bool doIt;
                        if (fresh) { doIt = true; if (hi == 0) hi = ms; lo = ms; }
                        else if (ms > oldHi) { doIt = true; newHi = Math.Max(newHi, ms); }
                        else if (ms >= lo) { doIt = false; if (!segDone) { hi = newHi; segDone = true; } }
                        else { doIt = true; if (!segDone) { hi = newHi; segDone = true; } lo = ms; }
                        if (doIt)
                        {
                            string entry = Outlook.S(() => it.EntryID);
                            if (entry.Length > 0 && Scalar(c, "SELECT 1 FROM mail WHERE entry=$e", ("$e", entry)) == null)
                            {
                                Insert(c, (object)it, entry, storeId, fname, sent, ms, fid);
                                batch++;
                            }
                        }
                    }
                }
                catch (Exception e) when (Outlook.Gone(e) || Outlook.Busy(e)) { Outlook.Release(it); throw new OutlookPause(Outlook.Gone(e), e); }
                catch (Exception e) { if (errors++ < 3) Log.Error("index item", e); }
                Outlook.Release(it);
                if (batch >= 50)
                {
                    Put(c, "hi:" + fid, (segDone ? hi : oldHi).ToString());
                    if (lo != long.MaxValue) Put(c, "lo:" + fid, lo.ToString());
                    tx.Commit(); tx.Dispose(); tx = c.BeginTransaction(); batch = 0;
                    Status = $"Indexing {fname}… {Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM mail")):N0} emails so far";
                }
                if (++seen % 25 == 0) Gentle.Pause();
                it = stop ? null : items.GetNext();
            }
            if (!stop && !segDone) hi = newHi;
            Put(c, "hi:" + fid, hi.ToString());
            if (lo != long.MaxValue) Put(c, "lo:" + fid, lo.ToString());
            tx.Commit(); tx.Dispose();
        }
        finally { Outlook.Release(items); }
    }

    static string ExAddr(string raw, Func<object> addressEntry)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith("/o=", StringComparison.OrdinalIgnoreCase)) return raw ?? "";
        lock (exCache) if (exCache.TryGetValue(raw, out var hit)) return hit;
        string smtp = raw;
        try
        {
            dynamic ae = addressEntry();
            dynamic u = ae?.GetExchangeUser();
            if (u != null) { smtp = Outlook.S(() => u.PrimarySmtpAddress); Outlook.Release(u); }
            Outlook.Release(ae);
        }
        catch { }
        if (string.IsNullOrEmpty(smtp)) smtp = raw;
        lock (exCache) exCache[raw] = smtp;
        return smtp;
    }

    static void Insert(SqliteConnection c, dynamic it, string entry, string storeId, string folder, bool sent, long ms, string fid)
    {
        string fromName = Outlook.S(() => it.SenderName);
        string fromAddr = ExAddr(Outlook.S(() => it.SenderEmailAddress), () => (object)it.Sender);
        // Sent by me = my address (or name) as the sender, in ANY folder: Sent Items › To Do, project folders, archives…
        if (!sent && (Me.IsMe(fromAddr) || (Me.Name.Length > 0 && fromName.Equals(Me.Name, StringComparison.OrdinalIgnoreCase)))) sent = true;
        var to = new List<string>(); var cc = new List<string>(); var toA = new List<string>(); var ccA = new List<string>();
        try
        {
            dynamic rs = it.Recipients;
            int n = (int)rs.Count;
            for (int i = 1; i <= n && i <= 60; i++)
            {
                dynamic r = rs.Item(i);
                try
                {
                    int type = (int)r.Type;
                    string name = Outlook.S(() => r.Name);
                    string addr = ExAddr(Outlook.S(() => r.Address), () => (object)r.AddressEntry);
                    if (type == 1) { to.Add(name); toA.Add(addr); }
                    else if (type == 2) { cc.Add(name); ccA.Add(addr); }
                }
                finally { Outlook.Release(r); }
            }
            Outlook.Release(rs);
        }
        catch { }
        string subject = Outlook.S(() => it.Subject);
        string body = Outlook.S(() => it.Body);
        if (body.Length > 40000) body = body[..40000];
        string conv = Outlook.S(() => it.ConversationID);
        bool unread = false; int imp = 1, flag = 0;
        try { unread = (bool)it.UnRead; } catch { }
        try { imp = (int)it.Importance; } catch { }
        try { flag = (int)it.FlagStatus; } catch { }
        var attNames = new List<string>();
        var attText = new StringBuilder();
        try
        {
            dynamic atts = it.Attachments;
            int n = (int)atts.Count;
            for (int i = 1; i <= n && i <= 20; i++)
            {
                dynamic a = atts.Item(i);
                try
                {
                    string name = Outlook.S(() => a.FileName);
                    if (name.Length == 0 || Regex.IsMatch(name, @"^image\d+\.(png|jpe?g|gif)$", RegexOptions.IgnoreCase)) continue;
                    attNames.Add(name);
                    if (!Store.IndexAttachments || attText.Length > 60000) continue;
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    if (ext is not (".pdf" or ".docx" or ".txt" or ".csv")) continue;
                    int size = 0, atype = 1;
                    try { size = (int)a.Size; } catch { }
                    try { atype = (int)a.Type; } catch { }
                    if (atype != 1 || size > 8_000_000) continue;
                    var tmp = Path.Combine(Paths.Cache, Guid.NewGuid().ToString("N") + ext);
                    try
                    {
                        a.SaveAsFile(tmp);
                        var t = DocText.Extract(tmp);
                        if (t.Length > 0) attText.Append(name).Append(": ").Append(t).Append('\n');
                    }
                    catch { }
                    finally { try { File.Delete(tmp); } catch { } }
                }
                finally { Outlook.Release(a); }
            }
            Outlook.Release(atts);
        }
        catch { }
        var at = attText.ToString();
        if (at.Length > 60000) at = at[..60000];

        Exec(c, @"INSERT OR IGNORE INTO mail(entry,store,conv,folder,sent,date,from_name,from_addr,to_list,cc_list,to_addr,cc_addr,subject,body,att_names,att_text,unread,importance,flagged,fid)
                  VALUES($e,$s,$cv,$f,$sent,$d,$fn,$fa,$to,$cc,$toa,$cca,$sub,$body,$an,$at,$u,$imp,$fl,$fid)",
            ("$e", entry), ("$s", storeId), ("$cv", conv), ("$f", folder), ("$sent", sent ? 1 : 0), ("$d", ms), ("$fid", fid),
            ("$fn", fromName), ("$fa", fromAddr), ("$to", string.Join("; ", to)), ("$cc", string.Join("; ", cc)),
            ("$toa", string.Join("; ", toA)), ("$cca", string.Join("; ", ccA)), ("$sub", subject), ("$body", body),
            ("$an", string.Join("; ", attNames)), ("$at", at), ("$u", unread ? 1 : 0), ("$imp", imp), ("$fl", flag));
        if (Fts)
        {
            var id = Scalar(c, "SELECT id FROM mail WHERE entry=$e", ("$e", entry));
            if (id != null)
                Exec(c, "INSERT INTO mail_fts(rowid,subject,body,from_name,to_list,cc_list,att_names,att_text) VALUES($id,$sub,$body,$fn,$to,$cc,$an,$at)",
                    ("$id", id), ("$sub", subject), ("$body", body), ("$fn", fromName), ("$to", string.Join("; ", to)), ("$cc", string.Join("; ", cc)),
                    ("$an", string.Join("; ", attNames)), ("$at", at));
        }
    }

    /// <summary>For the start-up test: adds one email without Outlook.</summary>
    public static void InsertTest(string entry, string from, string to, string cc, string subject, string body, DateTime date, bool sent, string conv)
    {
        using var c = Open();
        Exec(c, @"INSERT OR IGNORE INTO mail(entry,store,conv,folder,sent,date,from_name,from_addr,to_list,cc_list,to_addr,cc_addr,subject,body,att_names,att_text,unread,importance,flagged)
                  VALUES($e,'test',$cv,'Inbox',$sent,$d,$fn,$fa,$to,$cc,$to,$cc,$sub,$body,'','',0,1,0)",
            ("$e", entry), ("$cv", conv), ("$sent", sent ? 1 : 0), ("$d", Ms(date)), ("$fn", from), ("$fa", from.ToLowerInvariant() + "@test.local"),
            ("$to", to), ("$cc", cc), ("$sub", subject), ("$body", body));
        if (Fts)
        {
            var id = Scalar(c, "SELECT id FROM mail WHERE entry=$e", ("$e", entry));
            Exec(c, "INSERT INTO mail_fts(rowid,subject,body,from_name,to_list,cc_list,att_names,att_text) VALUES($id,$sub,$body,$fn,$to,$cc,'','')",
                ("$id", id), ("$sub", subject), ("$body", body), ("$fn", from), ("$to", to), ("$cc", cc));
        }
    }

    // ================================================================== search

    public static string NewPart(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var m = Regex.Match(body, @"(^|\n)\s*(From:|De :|De:|من:|-----Original Message|_{10,}|On .{5,120} wrote:)", RegexOptions.IgnoreCase);
        return (m.Success && m.Index > 0 ? body[..m.Index] : body).Trim();
    }

    static string Fold(string s) => s.Replace("\"", "").Trim();

    public static List<MailHit> Search(MailQuery q)
    {
        var where = new List<string>();
        var ps = new List<(string, object)>();
        int pi = 0;
        string P(object v) { var n = "$p" + pi++; ps.Add((n, v)); return n; }

        string NameClause(List<string> names, string nameCol, string addrCol)
        {
            var ors = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n =>
            {
                var like = "%" + Fold(n) + "%";
                return $"({nameCol} LIKE {P(like)} OR {addrCol} LIKE {P(like)})";
            }).ToList();
            return ors.Count == 0 ? null : "(" + string.Join(" OR ", ors) + ")";
        }

        string MeClause(string listCol, string addrCol)
        {
            var ors = new List<string>();
            foreach (var a in Me.Addrs) ors.Add($"{addrCol} LIKE {P("%" + a + "%")}");
            if (Me.Name.Length > 0) ors.Add($"{listCol} LIKE {P("%" + Me.Name + "%")}");
            return ors.Count == 0 ? null : "(" + string.Join(" OR ", ors) + ")";
        }

        if (NameClause(q.From, "m.from_name", "m.from_addr") is { } f) where.Add(f);
        if (NameClause(q.To, "m.to_list", "m.to_addr") is { } t) where.Add(t);
        if (NameClause(q.Cc, "m.cc_list", "m.cc_addr") is { } cc) where.Add(cc);
        switch (q.MeIn)
        {
            case "to": if (MeClause("m.to_list", "m.to_addr") is { } a) where.Add(a); break;
            case "cc": if (MeClause("m.cc_list", "m.cc_addr") is { } b) where.Add(b); break;
            case "any":
                var x = MeClause("m.to_list", "m.to_addr"); var y = MeClause("m.cc_list", "m.cc_addr");
                if (x != null && y != null) where.Add($"({x} OR {y})");
                break;
        }
        if (q.Folder == "received") where.Add("m.sent=0");
        if (q.Folder == "sent") where.Add("m.sent=1");
        if (q.After is DateTime af) where.Add($"m.date >= {P(Ms(af))}");
        if (q.Before is DateTime bf) where.Add($"m.date < {P(Ms(bf))}");
        if (q.HasAttach == true) where.Add("m.att_names <> ''");
        if (q.HasAttach == false) where.Add("m.att_names = ''");
        if (q.Unread == true) where.Add("m.unread=1");

        var words = q.Words.Select(Fold).Where(w => w.Length > 0).ToList();
        var phrase = Fold(q.Phrase ?? "");
        if (words.Count > 0 || phrase.Length > 0)
        {
            if (Fts)
            {
                var parts = words.Select(w => "\"" + w + "\"*").ToList();
                if (phrase.Length > 0) parts.Add("\"" + phrase + "\"");
                where.Add($"m.id IN (SELECT rowid FROM mail_fts WHERE mail_fts MATCH {P(string.Join(" AND ", parts))})");
            }
            else
            {
                foreach (var w in words.Append(phrase).Where(w => w.Length > 0))
                {
                    var like = P("%" + w + "%");
                    where.Add($"(m.subject LIKE {like} OR m.body LIKE {like} OR m.att_names LIKE {like} OR m.att_text LIKE {like} OR m.from_name LIKE {like})");
                }
            }
        }
        var leftOut = LeftOut();
        if (leftOut.Count > 0)
        {
            var ids = leftOut.Select(P).ToList();
            where.Add($"(m.fid IS NULL OR m.fid NOT IN ({string.Join(",", ids)}))");
        }
        if (q.Unanswered)
            where.Add("m.sent=0 AND NOT EXISTS (SELECT 1 FROM mail s WHERE s.sent=1 AND s.conv=m.conv AND m.conv<>'' AND s.date>m.date)");
        if (q.FollowUp)
            where.Add("m.sent=1 AND NOT EXISTS (SELECT 1 FROM mail r WHERE r.sent=0 AND r.conv=m.conv AND m.conv<>'' AND r.date>m.date)");

        var sql = "SELECT m.id,m.entry,m.store,m.conv,m.folder,m.sent,m.date,m.from_name,m.from_addr,m.to_list,m.cc_list,m.to_addr,m.cc_addr,m.subject,m.body,m.att_names,m.unread,m.importance FROM mail m"
                  + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                  + $" ORDER BY m.date DESC LIMIT {Math.Clamp(q.Limit, 1, 500)}";
        var list = new List<MailHit>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string Str(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            list.Add(new MailHit
            {
                Id = r.GetInt64(0), Entry = Str(1), StoreId = Str(2), Conv = Str(3), Folder = Str(4), Sent = r.GetInt64(5) == 1,
                Date = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)).LocalDateTime,
                FromName = Str(7), FromAddr = Str(8), To = Str(9), Cc = Str(10), ToAddr = Str(11), CcAddr = Str(12),
                Subject = Str(13), Body = Str(14), AttNames = Str(15), Unread = r.GetInt64(16) == 1, Importance = (int)r.GetInt64(17)
            });
        }
        // The same email can sit in two folders (e.g. moved copies): keep one per subject+date+sender.
        return list.GroupBy(h => (h.Subject, h.Date, h.FromAddr)).Select(g => g.First()).ToList();
    }

    /// <summary>Emails I sent after a date (subject, people, start of the text) — to find answers given outside the conversation.</summary>
    /// <summary>One email from the index by its Outlook id (null when it is not indexed).</summary>
    public static MailHit ByEntry(string entry)
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id,entry,store,conv,folder,sent,date,from_name,from_addr,to_list,cc_list,to_addr,cc_addr,subject,body,att_names,unread,importance FROM mail WHERE entry=$e LIMIT 1";
            cmd.Parameters.AddWithValue("$e", entry ?? "");
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            string Str(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            return new MailHit
            {
                Id = r.GetInt64(0), Entry = Str(1), StoreId = Str(2), Conv = Str(3), Folder = Str(4), Sent = r.GetInt64(5) == 1,
                Date = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)).LocalDateTime,
                FromName = Str(7), FromAddr = Str(8), To = Str(9), Cc = Str(10), ToAddr = Str(11), CcAddr = Str(12),
                Subject = Str(13), Body = Str(14), AttNames = Str(15), Unread = r.GetInt64(16) == 1, Importance = (int)r.GetInt64(17)
            };
        }
        catch { return null; }
    }

    public static List<MailHit> SentAfter(DateTime after, int limit = 5000)
    {
        var list = new List<MailHit>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT conv,date,subject,to_addr,cc_addr,to_list,cc_list,substr(body,1,4000) FROM mail WHERE sent=1 AND date >= $d ORDER BY date DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$d", Ms(after));
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string Str(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            list.Add(new MailHit
            {
                Sent = true, Conv = Str(0), Date = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).LocalDateTime, Subject = Str(2),
                ToAddr = Str(3), CcAddr = Str(4), To = Str(5), Cc = Str(6), Body = Str(7)
            });
        }
        return list;
    }
}

/// <summary>Plain text from PDF, Word (.docx) and text attachments, for the search index only.</summary>
static class DocText
{
    public static string Extract(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            string t = ext switch
            {
                ".pdf" => Pdf(path),
                ".docx" => Docx(path),
                ".txt" or ".csv" => File.ReadAllText(path),
                _ => ""
            };
            t = Regex.Replace(t, @"[ \t]+", " ");
            return t.Length > 40000 ? t[..40000] : t;
        }
        catch { return ""; }
    }

    static string Pdf(string path)
    {
        var sb = new StringBuilder();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        int n = 0;
        foreach (var page in doc.GetPages())
        {
            sb.Append(page.Text).Append('\n');
            if (++n >= 60 || sb.Length > 40000) break;
        }
        return sb.ToString();
    }

    static string Docx(string path)
    {
        using var z = ZipFile.OpenRead(path);
        var e = z.GetEntry("word/document.xml");
        if (e == null) return "";
        using var s = new StreamReader(e.Open());
        var xml = s.ReadToEnd();
        xml = Regex.Replace(xml, "</w:p>", "\n");
        xml = Regex.Replace(xml, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(xml);
    }
}
