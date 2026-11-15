using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// ☁ The real emails for the phone, in Google Drive — so the phone shows the FORMATTED email and opens its attachments with the PC off.
/// · the formatted page (fonts, tables, pictures) of every email of the last 24 months;
/// · the attachments of the last 3 months (each up to 35 MB);
/// · the real Outlook email (.msg) of the last 3 months (up to 35 MB) — for ↗ Share on the phone.
/// Encrypted with the pairing key like the rest of the copy (Google only stores locked files); 🔒 private emails and files never go.
/// It works in the background, newest first, a little at a time, and stops at the size limit you set. Designed by A.HAMROUNI
/// </summary>
static class MailFull
{
    public static bool On { get => Store.Bool("win_pl_full", false); set { Store.SetBool("win_pl_full", value); if (value) Start(); } }
    /// <summary>The copy stops growing at this size (GB).</summary>
    public static int LimitGb { get => Math.Clamp(Store.Int("win_pl_full_gb", 10), 1, 200); set => Store.SetInt("win_pl_full_gb", value); }
    public const int Months = 24, AttMonths = 3, Bucket = 200, AttMax = 35_000_000;
    public static string Status = "";
    static Thread loop;

    static string Root => Path.Combine(PhoneLink.Dir, "full");
    static string StateFile => Path.Combine(Paths.Root, "phone-full.json");

    class State
    {
        public Dictionary<string, long> Done = new();   // bucket → highest id already looked at
        public long Mails, Atts, Bytes;
        public bool Finished;
    }

    static State Load()
    {
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(StateFile))!.AsObject();
            var s = new State { Mails = o["mails"]?.GetValue<long>() ?? 0, Atts = o["atts"]?.GetValue<long>() ?? 0, Bytes = o["bytes"]?.GetValue<long>() ?? 0, Finished = o["finished"]?.GetValue<bool>() == true };
            foreach (var (k, v) in o["done"] as JsonObject ?? new JsonObject()) s.Done[k] = v!.GetValue<long>();
            return s;
        }
        catch { return new State(); }
    }

    static void Save(State s)
    {
        var d = new JsonObject();
        foreach (var (k, v) in s.Done) d[k] = v;
        try { File.WriteAllText(StateFile, new JsonObject { ["done"] = d, ["mails"] = s.Mails, ["atts"] = s.Atts, ["bytes"] = s.Bytes, ["finished"] = s.Finished }.ToJsonString()); } catch { }
    }

    /// <summary>What is in Drive now (for Settings and the phone's manifest).</summary>
    public static (long Mails, long Atts, long Bytes, bool Finished) Progress { get { var s = Load(); return (s.Mails, s.Atts, s.Bytes, s.Finished); } }

    /// <summary>Before you switch it on: how many emails, and a rough size of the formatted pages (attachments are counted as they are copied).</summary>
    public static (long Mails, long WithAtts, long RoughMb) Estimate()
    {
        using var c = MailIndex.Open();
        long Q(string sql, long cut)
        {
            using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("$d", cut);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        var m = Q("SELECT COUNT(*) FROM mail WHERE date >= $d", Ms(DateTime.Now.AddMonths(-Months)));
        var a = Q("SELECT COUNT(*) FROM mail WHERE date >= $d AND IFNULL(att_names,'') <> ''", Ms(DateTime.Now.AddMonths(-AttMonths)));
        return (m, a, m * 9 / 1024);     // about 9 KB a page once packed
    }

    static long Ms(DateTime d) => new DateTimeOffset(d).ToUnixTimeMilliseconds();

    public static void Forget()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        try { File.Delete(StateFile); } catch { }
        try { File.Delete(MsgSkipFile); } catch { }
        msgDone = null;
        Status = "";
    }

    public static void Start()
    {
        if (loop != null || !On) return;
        loop = new Thread(() => Guard.Run("emails for the phone (Drive)", () =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(2));          // let Nova and Outlook settle first
            while (true)
            {
                var wait = TimeSpan.FromMinutes(10);
                if (On)
                    try { if (Pass() || PassMsg()) wait = TimeSpan.FromSeconds(5); }
                    catch (Exception e) { Status = "⚠ " + e.Message; Log.Warn("emails for the phone", e.Message); }
                Thread.Sleep(wait);
            }
        })) { IsBackground = true, Name = "Nova emails for the phone", Priority = ThreadPriority.BelowNormal };
        loop.Start();
    }

    static string Sha(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    /// <summary>The attachment's file in Drive — the phone works out the same name.</summary>
    static string AttFile(long id, string name) => Path.Combine(Root, "a", (id / 1000).ToString(), $"{id}-{Sha(name.ToLowerInvariant())[..16]}.nes");

    static byte[] Gzip(byte[] b)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(b);
        return ms.ToArray();
    }

    static void WriteSealed(string file, byte[] plain, State s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        long before = File.Exists(file) ? new FileInfo(file).Length : 0;
        var box = PhoneLink.Seal(Gzip(plain));
        var tmp = file + ".part";
        File.WriteAllBytes(tmp, box);
        File.Move(tmp, file, true);
        s.Bytes += box.Length - before;
    }

    /// <summary>One bucket of emails (newest ids first). True = there is more to do right away.</summary>
    static bool Pass()
    {
        if (!PhoneLink.On || !PhoneLink.DriveOn) { Status = "Waiting — switch on 📱 Link my phone and A · Google Drive"; return false; }
        if (Outlook.Running() == null) { Status = "Waiting for Outlook to open…"; return false; }
        var s = Load();
        if (s.Bytes > LimitGb * 1_000_000_000L) { Status = $"⏸ Paused at your limit of {LimitGb} GB — raise the limit to continue"; return false; }
        long max;
        var cut = Ms(DateTime.Now.AddMonths(-Months));
        var attCut = DateTime.Now.AddMonths(-AttMonths);
        var ids = new List<long>();
        long top = -1;
        using (var c = MailIndex.Open())
        {
            using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT IFNULL(MAX(id),0) FROM mail"; max = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L); }
            // The newest bucket that still has ids not looked at.
            for (long b = max / Bucket; b >= 0; b--)
            {
                long hi = Math.Min(max, b * Bucket + Bucket - 1);
                if (s.Done.TryGetValue(b.ToString(), out var seen) && seen >= hi) continue;
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT id FROM mail WHERE id > $a AND id <= $b AND id >= $lo AND date >= $d ORDER BY id";
                cmd.Parameters.AddWithValue("$a", s.Done.TryGetValue(b.ToString(), out var sn) ? sn : -1);
                cmd.Parameters.AddWithValue("$b", hi); cmd.Parameters.AddWithValue("$lo", b * Bucket); cmd.Parameters.AddWithValue("$d", cut);
                using var r = cmd.ExecuteReader();
                while (r.Read()) ids.Add(r.GetInt64(0));
                top = b;
                if (ids.Count > 0) break;
                s.Done[b.ToString()] = hi;          // nothing in the last 24 months here
                top = -1;
            }
        }
        if (top < 0) { s.Finished = true; Save(s); Status = $"✓ Up to date · {s.Mails:N0} emails · {s.Atts:N0} attachments · {s.Bytes / 1e9:0.00} GB in Google Drive"; return false; }
        s.Finished = false;

        // The bucket's file as it is (new emails are added to it).
        var file = Path.Combine(Root, "m", top + ".nes");
        JsonObject mails = new(), imgs = new();
        if (File.Exists(file))
            try
            {
                using var gz = new GZipStream(new MemoryStream(PhoneLink.Open(File.ReadAllBytes(file))), CompressionMode.Decompress);
                using var sr = new StreamReader(gz, Encoding.UTF8);
                var o = JsonNode.Parse(sr.ReadToEnd())!.AsObject();
                mails = o["m"] as JsonObject ?? new(); imgs = o["img"] as JsonObject ?? new();
            }
            catch { mails = new(); imgs = new(); }

        long last = -1; bool broke = false;
        foreach (var h in MailIndex.ByIds(ids, ids.Count + 1, 200000).OrderBy(x => x.Id))
        {
            if (!On) { broke = true; break; }
            last = h.Id;
            if (Projects.Private.IsMatch($"{h.Subject} {h.Folder} {h.AttNames}")) continue;        // 🔒 never
            Status = $"⏳ Copying emails for the phone · {s.Mails:N0} done · {s.Bytes / 1e9:0.00} GB · now {h.Date:dd MMM yyyy}";
            string html = null, dir = null;
            try
            {
                var f = MailPreview.Render(h).GetAwaiter().GetResult();
                if (f != null) { dir = Path.GetDirectoryName(f); html = Inline(f, imgs); }
            }
            catch { }
            html ??= "<html><head><meta charset='utf-8'></head><body><pre style='white-space:pre-wrap;font-family:Calibri,Segoe UI,Arial;font-size:15px'>" + System.Net.WebUtility.HtmlEncode(h.Body ?? "") + "</pre></body></html>";
            var names = (h.AttNames ?? "").Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var here = new JsonArray();
            if (h.Date >= attCut)
                foreach (var name in names)
                {
                    if (Projects.Private.IsMatch(name)) continue;
                    var af = AttFile(h.Id, name);
                    if (File.Exists(af)) { here.Add(name); continue; }
                    var tmp = Path.Combine(Path.GetTempPath(), "nova-full-att-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        var p = MailPreview.SaveAttachment(h, name, tmp).GetAwaiter().GetResult();
                        if (p != null && File.Exists(p) && new FileInfo(p).Length <= AttMax) { WriteSealed(af, File.ReadAllBytes(p), s); s.Atts++; here.Add(name); }
                    }
                    catch { }
                    finally { try { Directory.Delete(tmp, true); } catch { } }
                }
            mails[h.Id.ToString()] = new JsonObject { ["h"] = html, ["a"] = new JsonArray(names.Select(x => (JsonNode)x).ToArray()), ["f"] = here };
            s.Mails++;
            try { if (dir != null) Directory.Delete(dir, true); } catch { }
            Thread.Sleep(120);                  // gentle on Outlook
        }
        WriteSealed(file, Encoding.UTF8.GetBytes(new JsonObject { ["v"] = 1, ["m"] = mails, ["img"] = imgs }.ToJsonString()), s);
        // Stopped half-way (switched off): the rest of this bucket is done next time.
        if (!broke) s.Done[top.ToString()] = Math.Min(max, top * Bucket + Bucket - 1);
        else if (last >= 0) s.Done[top.ToString()] = last;
        Save(s);
        return On;
    }

    // ------------------------------------------------------------------ ✉ the real .msg of the last 3 months (↗ Share on the phone)

    static string MsgFile(long id) => Path.Combine(Root, "e", (id / 1000).ToString(), id + ".nes");
    static HashSet<long> msgDone;
    static string MsgSkipFile => Path.Combine(Paths.Root, "phone-full-msg-skip.txt");

    /// <summary>A few emails at a time, newest first. True = there is more to do right away.</summary>
    static bool PassMsg()
    {
        if (!PhoneLink.On || !PhoneLink.DriveOn || Outlook.Running() == null) return false;
        var s = Load();
        if (s.Bytes > LimitGb * 1_000_000_000L) return false;
        if (msgDone == null)
        {
            msgDone = new HashSet<long>();
            try
            {
                var e = Path.Combine(Root, "e");
                if (Directory.Exists(e)) foreach (var f in Directory.EnumerateFiles(e, "*.nes", SearchOption.AllDirectories)) if (long.TryParse(Path.GetFileNameWithoutExtension(f), out var id)) msgDone.Add(id);
                if (File.Exists(MsgSkipFile)) foreach (var l in File.ReadAllLines(MsgSkipFile)) if (long.TryParse(l, out var id)) msgDone.Add(id);   // too large, private or unreadable
            }
            catch { }
        }
        var todo = new List<long>();
        using (var c = MailIndex.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM mail WHERE date >= $d ORDER BY id DESC";
            cmd.Parameters.AddWithValue("$d", Ms(DateTime.Now.AddMonths(-AttMonths)));
            using var r = cmd.ExecuteReader();
            while (r.Read() && todo.Count < 25) { var id = r.GetInt64(0); if (!msgDone.Contains(id)) todo.Add(id); }
        }
        if (todo.Count == 0) return false;
        foreach (var h in MailIndex.ByIds(todo, todo.Count + 1).OrderByDescending(x => x.Id))
        {
            if (!On) break;
            bool kept = false;
            if (!Projects.Private.IsMatch($"{h.Subject} {h.Folder} {h.AttNames}"))
            {
                var tmp = Path.Combine(Path.GetTempPath(), "nova-full-msg-" + Guid.NewGuid().ToString("N") + ".msg");
                try
                {
                    Status = $"⏳ Copying Outlook emails (.msg) for ↗ Share · {s.Bytes / 1e9:0.00} GB · now {h.Date:dd MMM yyyy}";
                    Outlook.SaveMsg(h.Entry, h.StoreId, tmp);
                    if (File.Exists(tmp) && new FileInfo(tmp).Length <= AttMax) { WriteSealed(MsgFile(h.Id), File.ReadAllBytes(tmp), s); kept = true; }
                }
                catch { }
                finally { try { File.Delete(tmp); } catch { } }
            }
            msgDone.Add(h.Id);
            if (!kept) try { File.AppendAllText(MsgSkipFile, h.Id + "\n"); } catch { }
            Thread.Sleep(150);
        }
        foreach (var id in todo) msgDone.Add(id);        // not in the index any more: not asked again this run
        Save(s);
        return On;
    }

    /// <summary>The rendered page with its pictures taken out into a shared list (a logo repeated in 200 emails is stored once).</summary>
    static string Inline(string file, JsonObject imgs)
    {
        var dir = Path.GetDirectoryName(file)!;
        var html = File.ReadAllText(file);
        return Regex.Replace(html, @"(src\s*=\s*[""']?)(img\d+\.[A-Za-z0-9]+)", m =>
        {
            var p = Path.Combine(dir, m.Groups[2].Value);
            if (!File.Exists(p) || new FileInfo(p).Length > 1_500_000) return m.Value;
            var bytes = File.ReadAllBytes(p);
            var key = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant()[..20];
            if (!imgs.ContainsKey(key))
            {
                var ext = Path.GetExtension(p).TrimStart('.').ToLowerInvariant();
                var mime = ext switch { "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", "bmp" => "image/bmp", _ => "image/png" };
                imgs[key] = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            }
            return m.Groups[1].Value + "nimg:" + key;
        });
    }
}
