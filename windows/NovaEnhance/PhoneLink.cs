using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace NovaEnhance;

/// <summary>
/// 📱 Phone link (2.0): your phone gets Nova's data from this PC — every email with full text (all periods), attachment text,
/// brief, email projects, the project-file catalogue, ⭐ starred items, rules, prompts, profile, vocabulary and My Work.
/// Everything is encrypted (AES-256-GCM) with the key you pair by QR code; 🔒 private items never leave this PC.
/// Ways: A ☁ Google Drive folder (Drive for desktop uploads it) · B 🔁 GitHub relay (live questions + small updates) ·
/// C 📶 office Wi-Fi (direct). Designed by A.HAMROUNI
/// </summary>
static class PhoneLink
{
    public const int WifiPort = 47323;
    static readonly JsonSerializerOptions JO = new() { IncludeFields = true };

    // ------------------------------------------------------------------ settings

    public static bool On { get => Store.Bool("win_pl_on", false); set { Store.SetBool("win_pl_on", value); if (value) Start(); } }
    public static bool DriveOn { get => Store.Bool("win_pl_drive_on", true); set => Store.SetBool("win_pl_drive_on", value); }
    public static bool RelayOn { get => Store.Bool("win_pl_relay_on", true); set => Store.SetBool("win_pl_relay_on", value); }
    public static bool WifiOn { get => Store.Bool("win_pl_wifi_on", true); set { Store.SetBool("win_pl_wifi_on", value); if (value) StartWifi(); else StopWifi(); } }
    public static int EveryMin
    {
        get
        {
            // 15 min by default (was 60): the phone is never more than 15 min behind
            if (!Store.Bool("win_pl_every_v2", false)) { if (Store.Int("win_pl_every", 60) == 60) Store.SetInt("win_pl_every", 15); Store.SetBool("win_pl_every_v2", true); }
            return Math.Clamp(Store.Int("win_pl_every", 15), 15, 720);
        }
        set => Store.SetInt("win_pl_every", value);
    }
    public static bool AfterBrief { get => Store.Bool("win_pl_after_brief", true); set => Store.SetBool("win_pl_after_brief", value); }
    /// <summary>"Full text for the last 24 months only" (older emails: subject, people, date, attachment names).</summary>
    public static bool Last12 { get => Store.Bool("win_pl_last12", false); set => Store.SetBool("win_pl_last12", value); }
    public const int RecentMonths = 24;
    public static bool Send(string part) => Store.Bool("win_pl_send_" + part, true);
    public static void SetSend(string part, bool v) => Store.SetBool("win_pl_send_" + part, v);
    public static string PhoneName { get => Store.Str("win_pl_phone", ""); set => Store.Set("win_pl_phone", value); }

    public static byte[] Key
    {
        get
        {
            var k = Store.Secret("win_pl_key");
            if (k.Length < 40) { k = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); Store.SetSecret("win_pl_key", k); }
            return Convert.FromBase64String(k);
        }
    }
    /// <summary>🔑 New key: the paired phone stops working until it scans the new QR code.</summary>
    public static void NewKey() { Store.SetSecret("win_pl_key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))); PhoneName = ""; ResetState(); }
    public static string PairToken => Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes("nova-pair"))).ToLowerInvariant()[..32];

    // ------------------------------------------------------------------ where the files go

    /// <summary>Google Drive for desktop: the "My Drive" folder on any drive letter (or the folder you picked).</summary>
    static string driveCache; static DateTime driveAt = DateTime.MinValue;
    /// <summary>Remembered for 10 minutes: checking every drive letter can take seconds (network drives).</summary>
    public static string DriveRoot
    {
        get
        {
            if (driveCache != null && DateTime.Now - driveAt < TimeSpan.FromMinutes(10)) return driveCache;
            driveCache = FindDriveRoot(); driveAt = DateTime.Now;
            return driveCache;
        }
    }
    public static bool DriveKnown => driveCache != null && DateTime.Now - driveAt < TimeSpan.FromMinutes(10);

    static string FindDriveRoot()
    {
        {
            var picked = Store.Str("win_pl_drive_root", "");
            if (picked.Length > 0 && Directory.Exists(picked)) return picked;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    foreach (var n in new[] { "My Drive", "Mon Drive", "Mein Ablage", "Meine Ablage" })
                    {
                        var p = Path.Combine(d.RootDirectory.FullName, n);
                        if (Directory.Exists(p)) return p;
                    }
                }
                catch { }
            }
            return "";
        }
    }

    /// <summary>The sync folder: inside Google Drive when ☁ is on and Drive is found, otherwise in Documents\Nova Enhance.</summary>
    public static string Dir
    {
        get
        {
            var root = DriveOn && DriveRoot.Length > 0 ? DriveRoot : Paths.Root;
            var d = Path.Combine(root, "Nova Enhance Sync");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    // ------------------------------------------------------------------ encryption: "NES1" + nonce(12) + tag(16) + data

    public static byte[] Seal(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var g = new AesGcm(Key, 16)) g.Encrypt(nonce, plain, cipher, tag);
        var o = new byte[4 + 12 + 16 + cipher.Length];
        Encoding.ASCII.GetBytes("NES1").CopyTo(o, 0); nonce.CopyTo(o, 4); tag.CopyTo(o, 16); cipher.CopyTo(o, 32);
        return o;
    }

    public static byte[] Open(byte[] box)
    {
        if (box.Length < 32 || Encoding.ASCII.GetString(box, 0, 4) != "NES1") throw new Exception("not a Nova sync file");
        var plain = new byte[box.Length - 32];
        using (var g = new AesGcm(Key, 16)) g.Decrypt(box.AsSpan(4, 12), box.AsSpan(32), box.AsSpan(16, 16), plain);
        return plain;
    }

    static byte[] Gzip(byte[] b)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, true)) gz.Write(b);
        return ms.ToArray();
    }

    /// <summary>Writes an encrypted, compressed file (atomically) and returns its size.</summary>
    static long Write(string name, string text)
    {
        var path = Path.Combine(Dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = Seal(Gzip(Encoding.UTF8.GetBytes(text)));
        File.WriteAllBytes(path + ".part", data);
        File.Move(path + ".part", path, true);
        return data.Length;
    }

    // ------------------------------------------------------------------ state

    class State
    {
        public long LastMail, LastFile; public string ProjHash = "", BrainHash = "";
        public DateTime FullAt = DateTime.MinValue, LastRun = DateTime.MinValue;
        public List<string> Deltas = new();
        public Dictionary<string, long> Sizes = new();
        public Dictionary<string, long> Counts = new();
        public string LastWay = "", LastError = "";
    }
    static State S
    {
        get { try { return JsonSerializer.Deserialize<State>(Store.Str("win_pl_state", "{}"), JO) ?? new State(); } catch { return new State(); } }
    }
    static void Save(State s) => Store.Set("win_pl_state", JsonSerializer.Serialize(s, JO));
    static void ResetState() => Store.Set("win_pl_state", "{}");
    public static DateTime LastRun => S.LastRun;
    public static string LastError => S.LastError;
    public static Dictionary<string, long> Sizes => S.Sizes;
    public static Dictionary<string, long> Counts => S.Counts;
    public static string Status = "";

    // ------------------------------------------------------------------ the hourly job

    static Thread loop;
    static volatile bool wake;
    static readonly object RunLock = new();
    public static void Start()
    {
        if (On && WifiOn) StartWifi();
        if (loop != null) return;
        // 📤 After sleep: the phone's changes and a fresh copy at once.
        try { Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Resume) wake = true; }; } catch { }
        loop = new Thread(() => Guard.Run("phone link", () =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(60));
            var outAt = DateTime.MinValue;
            while (true)
            {
                if (On)
                {
                    WatchOutbox();
                    // 📤 the phone's changes first (at start · every 15 min · after waking up)
                    if (wake || DateTime.Now - outAt >= TimeSpan.FromMinutes(15)) { var why = wake ? "woke up" : outAt == DateTime.MinValue ? "start" : "every 15 min"; outAt = DateTime.Now; try { ReadOutbox(why).GetAwaiter().GetResult(); } catch (Exception e) { Log.Warn("phone outbox", e.Message); } }
                    if (wake || DateTime.Now - S.LastRun >= TimeSpan.FromMinutes(EveryMin)) RunSafe(wake ? "woke up" : "every " + EveryMin + " min");
                    wake = false;
                }
                Thread.Sleep(TimeSpan.FromMinutes(1));
            }
        })) { IsBackground = true, Name = "Nova phone link", Priority = ThreadPriority.BelowNormal };
        loop.Start();
    }

    public static void RunSafe(string why)
    {
        if (!Monitor.TryEnter(RunLock)) return;
        try { Run(why); }
        catch (Exception e) { var s = S; s.LastError = e.Message; s.LastRun = DateTime.Now; Save(s); Status = "⚠ " + e.Message; Log.Warn("phone link", e.Message); }
        finally { Monitor.Exit(RunLock); }
    }

    /// <summary>Full copy the first time (and after 3 days of updates), otherwise only what changed.</summary>
    static void Run(string why)
    {
        using var act = Activity.Begin("📱 phone link", why);
        var s = S;
        var full = s.FullAt == DateTime.MinValue || s.Deltas.Count > 72 || DateTime.Now - s.FullAt > TimeSpan.FromDays(3) && s.Deltas.Count > 24;
        Status = full ? "⏳ preparing the full copy for the phone…" : "⏳ preparing the update for the phone…";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (full)
        {
            foreach (var f in Directory.GetFiles(Dir, "*.nes", SearchOption.AllDirectories)) if (!f.Contains(Path.DirectorySeparatorChar + "starred" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "full" + Path.DirectorySeparatorChar)) try { File.Delete(f); } catch { }
            s.Deltas.Clear(); s.LastMail = 0; s.LastFile = 0; s.ProjHash = ""; s.BrainHash = "";
            s.Sizes.Clear();
        }
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        long added = 0;
        if (Send("email")) added += Emails(s, full, stamp);
        act.Mark("emails", $"{added:N0} rows");
        if (Send("brief")) { ProjectMap(s); Brief(s); }
        if (Send("files")) Files(s, full, stamp);
        if (Send("brain")) Brain(s);
        if (Send("star")) Starred(s);
        s.FullAt = full ? DateTime.Now : s.FullAt;
        s.LastRun = DateTime.Now; s.LastError = ""; s.LastWay = DriveOn && DriveRoot.Length > 0 ? "☁ Google Drive" : "📶 Wi-Fi / 🔁 relay";
        Manifest(s);
        Save(s);
        if (RelayOn) try { RelayDeltas(s).GetAwaiter().GetResult(); } catch (Exception e) { act.Mark("relay", e.Message); }
        Status = $"✓ {(full ? "Full copy" : "Update")} ready {DateTime.Now:HH:mm} · {sw.Elapsed.TotalSeconds:0} s";
        act.Result = Status;
    }

    static bool Private(params string[] parts) => Projects.Private.IsMatch(string.Join(" ", parts.Select(p => p ?? "")));

    /// <summary>Emails: chunks of 5,000 (full copy) or the new ones since the last run. Bodies and attachment text included
    /// (older than 24 months: left out when "last 24 months only" is on).</summary>
    static long Emails(State s, bool full, string stamp)
    {
        long n = 0, max = s.LastMail;
        var cut = Last12 ? DateTimeOffset.Now.AddMonths(-RecentMonths).ToUnixTimeMilliseconds() : 0;
        var proj = new Dictionary<long, string>();
        using var c = MailIndex.Open();
        using (var pc = c.CreateCommand())
        {
            pc.CommandText = "SELECT id, proj FROM mproj";
            try { using var r = pc.ExecuteReader(); while (r.Read()) if (!r.IsDBNull(1)) proj[r.GetInt64(0)] = r.GetString(1); } catch { }
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, entry, date, sent, folder, from_name, from_addr, to_list, cc_list, subject, body, att_names, att_text FROM mail WHERE id > $m ORDER BY id";
        cmd.Parameters.AddWithValue("$m", full ? 0 : s.LastMail);
        using var rd = cmd.ExecuteReader();
        var sb = new StringBuilder(); int inChunk = 0, chunk = 0; long bytes = 0;
        void Flush(bool last)
        {
            if (inChunk == 0) return;
            var name = full ? $"email/full-{++chunk:0000}.nes" : $"email/delta-{stamp}.nes";
            bytes += Write(name, sb.ToString());
            if (!full) s.Deltas.Add(name);
            sb.Clear(); inChunk = 0;
        }
        string G(int i) => rd.IsDBNull(i) ? "" : rd.GetString(i);
        while (rd.Read())
        {
            var id = rd.GetInt64(0); max = Math.Max(max, id);
            var subject = G(9); var folder = G(4); var att = G(11);
            if (Private(subject, folder, att)) continue;
            var date = rd.GetInt64(2);
            var old = cut > 0 && date < cut;
            var o = new JsonObject
            {
                ["i"] = id, ["e"] = G(1), ["d"] = date, ["s"] = rd.GetInt64(3) != 0, ["f"] = folder, ["fn"] = G(5), ["fa"] = G(6),
                ["to"] = G(7), ["cc"] = G(8), ["sub"] = subject, ["an"] = att, ["p"] = proj.TryGetValue(id, out var p) ? p : ""
            };
            if (!old) { o["b"] = G(10); if (Send("att")) o["at"] = G(12); }
            sb.Append(o.ToJsonString()).Append('\n');
            n++;
            if (++inChunk >= (full ? 5000 : 100000)) Flush(false);
        }
        Flush(true);
        s.LastMail = max;
        s.Sizes["email"] = full ? bytes : s.Sizes.GetValueOrDefault("email") + bytes;
        s.Counts["email"] = full ? n : s.Counts.GetValueOrDefault("email") + n;
        return n;
    }

    /// <summary>Which email belongs to which project (sent again only when it changed).</summary>
    static void ProjectMap(State s)
    {
        var sb = new StringBuilder();
        using (var c = MailIndex.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, proj FROM mproj WHERE proj<>'' ORDER BY id";
            try { using var r = cmd.ExecuteReader(); while (r.Read()) sb.Append(r.GetInt64(0)).Append('\t').Append(r.GetString(1)).Append('\n'); } catch { }
        }
        var choices = new JsonArray();
        foreach (var ch in MailProjects.Choices())
        {
            // 🏷 Names and old codes too, so “RS5”, “Saadiyat” or “Lucci Dubai” typed on the phone find the right project.
            var p = Projects.All().FirstOrDefault(x => x.Code.Equals(ch.Code, StringComparison.OrdinalIgnoreCase));
            choices.Add(new JsonObject
            {
                ["code"] = ch.Code, ["label"] = ch.Label, ["name"] = p?.Name ?? "",
                ["aliases"] = new JsonArray((p == null ? new List<string>() : Projects.NamesOf(p).Where(n => n != p.Code)).Select(n => (JsonNode)n).ToArray())
            });
        }
        var text = new JsonObject { ["projects"] = choices, ["map"] = sb.ToString() }.ToJsonString();
        var h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)));
        if (h == s.ProjHash) return;
        s.Sizes["projects"] = Write("projects.nes", text);
        s.ProjHash = h;
    }

    // ------------------------------------------------------------------ 🌅 brief

    static List<MailHit> lastNeeds, lastFollow;
    static DateTime briefAt = DateTime.MinValue;

    /// <summary>The morning brief window gives its lists here; with "also right after the brief" the phone is updated at once.</summary>
    public static void BriefMade(List<MailHit> needs, List<MailHit> follow)
    {
        lastNeeds = needs.ToList(); lastFollow = follow.ToList(); briefAt = DateTime.Now;
        if (On && AfterBrief) _ = Task.Run(() => RunSafe("after the morning brief"));
    }

    static JsonObject Hit(MailHit h) => new()
    {
        ["i"] = h.Id, ["e"] = h.Entry, ["d"] = TimeMs.Of(h.Date), ["s"] = h.Sent, ["fn"] = h.FromName, ["fa"] = h.FromAddr,
        ["to"] = h.To, ["cc"] = h.Cc, ["sub"] = h.Subject, ["an"] = h.AttNames, ["st"] = h.Status, ["why"] = h.Why, ["first"] = h.FirstLines(400),
        ["p"] = MailProjects.Of(h.Id)
    };

    static void Brief(State s)
    {
        if (lastNeeds == null || DateTime.Now - briefAt > TimeSpan.FromHours(3))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var (n, f) = BriefLogic.Build(cts.Token, null, false).GetAwaiter().GetResult();
                lastNeeds = n; lastFollow = f; briefAt = DateTime.Now;
            }
            catch (Exception e) { Log.Warn("phone link", "brief: " + e.Message); if (lastNeeds == null) return; }
        }
        var o = new JsonObject
        {
            ["at"] = briefAt.ToString("o"),
            ["needs"] = new JsonArray(lastNeeds.Where(h => !Private(h.Subject, h.Folder, h.AttNames)).Select(h => (JsonNode)Hit(h)).ToArray()),
            ["follow"] = new JsonArray(lastFollow.Where(h => !Private(h.Subject, h.Folder, h.AttNames)).Select(h => (JsonNode)Hit(h)).ToArray()),
            ["handled"] = Outstanding.HandledToday()
        };
        s.Sizes["brief"] = Write("brief.nes", o.ToJsonString());
    }

    // ------------------------------------------------------------------ 📂 project files catalogue

    static void Files(State s, bool full, string stamp)
    {
        long max = s.LastFile, n = 0, bytes = 0;
        try
        {
            using var c = FolderIndex.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT f.id, f.path, f.size, f.mtime, f.kind, (SELECT substr(group_concat(s.text, ' '), 1, 20000) FROM segs s WHERE s.file_id = f.id) FROM files f WHERE f.id > $m ORDER BY f.id";
            cmd.Parameters.AddWithValue("$m", full ? 0 : s.LastFile);
            using var r = cmd.ExecuteReader();
            var sb = new StringBuilder(); int inChunk = 0, chunk = 0;
            void Flush()
            {
                if (inChunk == 0) return;
                var name = full ? $"files/full-{++chunk:0000}.nes" : $"files/delta-{stamp}.nes";
                bytes += Write(name, sb.ToString());
                if (!full) s.Deltas.Add(name);
                sb.Clear(); inChunk = 0;
            }
            while (r.Read())
            {
                var id = r.GetInt64(0); max = Math.Max(max, id);
                var path = r.IsDBNull(1) ? "" : r.GetString(1);
                if (Private(path)) continue;
                sb.Append(new JsonObject
                {
                    ["i"] = id, ["path"] = path, ["size"] = r.IsDBNull(2) ? 0 : r.GetInt64(2), ["m"] = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                    ["k"] = r.IsDBNull(4) ? "" : r.GetString(4), ["t"] = r.IsDBNull(5) ? "" : r.GetString(5)
                }.ToJsonString()).Append('\n');
                n++;
                if (++inChunk >= (full ? 3000 : 100000)) Flush();
            }
            Flush();
        }
        catch (Exception e) { Log.Warn("phone link", "files: " + e.Message); }
        s.LastFile = max;
        s.Sizes["files"] = full ? bytes : s.Sizes.GetValueOrDefault("files") + bytes;
        s.Counts["files"] = full ? n : s.Counts.GetValueOrDefault("files") + n;
    }

    // ------------------------------------------------------------------ 🧠 the same brain on the phone

    /// <summary>Profile, your terms and words, project vocabulary, names from your emails, learned words, writing style,
    /// rules, prompts and My Work — the phone corrects and searches like the PC.</summary>
    static void Brain(State s)
    {
        var o = new JsonObject
        {
            ["fields"] = Store.ProfileFields, ["about"] = Store.ProfileAbout, ["terms"] = Store.ProfileTerms,
            ["myWords"] = Store.Str("win_my_words"), ["me"] = Me.Name,
        };
        var vocab = new JsonObject();
        try { foreach (var (a, f) in Memory.Data.Vocab) vocab[a] = f; } catch { }
        o["vocab"] = vocab;
        try { o["style"] = Memory.Data.Style; } catch { }
        try { o["names"] = new JsonArray(MailIndex.KnownNames(4000).Where(n => n.Length > 1).Take(6000).Select(n => (JsonNode)n).ToArray()); } catch { }
        try { o["learned"] = new JsonArray(Memory.VocabularyWords().Distinct(StringComparer.OrdinalIgnoreCase).Take(5000).Select(w => (JsonNode)w).ToArray()); } catch { }
        var rules = new JsonObject();
        foreach (var f in new[] { "brief", "projects" }) try { rules[f] = JsonNode.Parse(JsonSerializer.Serialize(RuleBooks.Load(f), JO)); } catch { }
        o["rules"] = rules;
        try { o["prompts"] = JsonNode.Parse(JsonSerializer.Serialize(NovaEnhance.Prompts.All(), JO)); } catch { }
        try { o["work"] = JsonNode.Parse(JsonSerializer.Serialize(Work.All(), JO)); } catch { }
        try { o["guide"] = NovaEnhance.Brain.ForPhone(); o["settings"] = NovaEnhance.Brain.AllSettings(); o["brainView"] = NovaEnhance.Brain.ViewForPhone(); } catch { }
        try { o["track"] = Tracker.ForPhone(); } catch { }
        try { o["logprefs"] = LogPrefs.Get(); } catch { }   // ✎ My log settings (hidden projects / types, your types, discipline groups, periods)   // 🧩 the field pack: what the log tracks, who is “us”   // 🧠 the same Nova Brain rules on the phone
        var text = o.ToJsonString();
        var h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)));
        if (h == s.BrainHash) return;
        s.Sizes["brain"] = Write("brain.nes", text);
        s.BrainHash = h;
    }

    // ------------------------------------------------------------------ ⭐ starred: the real files go to the phone

    public class Star { public string Kind = "mail", Entry = "", StoreId = "", Path = "", Title = ""; public DateTime At = DateTime.Now; }
    public static List<Star> Stars
    {
        get { try { return JsonSerializer.Deserialize<List<Star>>(Store.Str("win_pl_stars", "[]"), JO) ?? new(); } catch { return new(); } }
        set => Store.Set("win_pl_stars", JsonSerializer.Serialize(value, JO));
    }
    public static bool IsStarred(MailHit h) => Stars.Any(x => x.Entry == h.Entry);
    public static void ToggleStar(MailHit h)
    {
        var l = Stars;
        if (l.RemoveAll(x => x.Entry == h.Entry) == 0) l.Add(new Star { Kind = "mail", Entry = h.Entry, StoreId = h.StoreId, Title = h.Subject ?? "" });
        Stars = l;
    }
    public static void ToggleStarFile(string path)
    {
        var l = Stars;
        if (l.RemoveAll(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) == 0) l.Add(new Star { Kind = "file", Path = path, Title = System.IO.Path.GetFileName(path) });
        Stars = l;
    }

    static string StarName(Star x) => "starred/" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(x.Kind + "|" + x.Entry + "|" + x.Path)))[..20].ToLowerInvariant() + ".nes";

    static void Starred(State s)
    {
        var list = Stars; long bytes = 0;
        var keep = new HashSet<string>(list.Select(StarName));
        var dir = Path.Combine(Dir, "starred"); Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "*.nes")) if (!keep.Contains("starred/" + Path.GetFileName(f))) try { File.Delete(f); } catch { }
        var index = new JsonArray();
        foreach (var x in list)
        {
            var name = StarName(x);
            var path = Path.Combine(Dir, name);
            try
            {
                if (!File.Exists(path))
                {
                    byte[] data; string file;
                    if (x.Kind == "file") { if (!File.Exists(x.Path) || new FileInfo(x.Path).Length > 60_000_000 || Private(x.Path)) continue; data = File.ReadAllBytes(x.Path); file = System.IO.Path.GetFileName(x.Path); }
                    else
                    {
                        // ✉ The email as a formatted page (opens in Nova on the phone, offline).
                        var mh = MailIndex.ByIds(MailIndex.IdsOfEntry(x.Entry), 1, 200000).FirstOrDefault() ?? new MailHit { Entry = x.Entry, StoreId = x.StoreId, Subject = x.Title };
                        var page = MailPreview.Render(mh).GetAwaiter().GetResult();
                        data = Encoding.UTF8.GetBytes(page != null ? OnePage(page) : "<pre>" + System.Net.WebUtility.HtmlEncode(mh.Body ?? x.Title) + "</pre>");
                        file = string.Join("_", (x.Title.Length > 0 ? x.Title : "email").Split(System.IO.Path.GetInvalidFileNameChars())) + ".html";
                        if (data.Length > 60_000_000) continue;
                    }
                    var packed = Seal(Gzip(data));
                    File.WriteAllBytes(path + ".part", packed); File.Move(path + ".part", path, true);
                    index.Add(new JsonObject { ["name"] = name, ["file"] = file, ["kind"] = x.Kind, ["title"] = x.Title, ["entry"] = x.Entry, ["path"] = x.Path, ["bytes"] = data.Length });
                }
                else index.Add(new JsonObject { ["name"] = name, ["kind"] = x.Kind, ["title"] = x.Title, ["entry"] = x.Entry, ["path"] = x.Path, ["file"] = x.Kind == "file" ? System.IO.Path.GetFileName(x.Path) : x.Title + ".msg" });
                bytes += new FileInfo(path).Length;
            }
            catch (Exception e) { Log.Warn("phone link", "⭐ " + x.Title + ": " + e.Message); }
        }
        Write("starred.nes", index.ToJsonString());
        s.Sizes["star"] = bytes; s.Counts["star"] = index.Count;
    }

    // ------------------------------------------------------------------ manifest (what the phone downloads)

    static void Manifest(State s)
    {
        // 📤 the list of phone changes already applied (kept through a full copy)
        try { if (File.Exists(AppliedPath)) Write("applied.nes", AppliedList().ToJsonString()); } catch { }
        var files = new JsonArray();
        foreach (var f in Directory.GetFiles(Dir, "*.nes", SearchOption.AllDirectories).OrderBy(f => f))
        {
            var rel = Path.GetRelativePath(Dir, f).Replace('\\', '/');
            if (rel == "manifest.nes" || rel.StartsWith("full/")) continue;     // ☁ the real emails stay in Drive: the phone takes one only when you open it
            var fi = new FileInfo(f);
            files.Add(new JsonObject { ["n"] = rel, ["b"] = fi.Length, ["m"] = new DateTimeOffset(fi.LastWriteTime).ToUnixTimeMilliseconds() });
        }
        var o = new JsonObject
        {
            ["v"] = 2, ["pc"] = Environment.MachineName, ["build"] = Program.Shown, ["at"] = DateTime.Now.ToString("o"), ["fullAt"] = s.FullAt.ToString("o"),
            ["last12"] = Last12, ["files"] = files, ["counts"] = JsonNode.Parse(JsonSerializer.Serialize(s.Counts)), ["oldest"] = MailIndex.OldestText,
            ["full"] = MailFull.On ? new JsonObject { ["months"] = MailFull.Months, ["att"] = MailFull.AttMonths, ["mails"] = MailFull.Progress.Mails } : null,   // ☁ the real emails are in Drive too
            ["gd"] = PhoneDrive()     // ☁ the phone learns how to read Google Drive by itself (no new QR needed)
        };
        Write("manifest.nes", o.ToJsonString());
        File.WriteAllText(Path.Combine(Dir, "README.txt"),
            "Nova Enhance phone sync — encrypted files for your paired phone only (AES-256-GCM, key in the pairing QR code).\r\nDo not edit or share this folder. Designed by A.HAMROUNI\r\n");
    }

    // ------------------------------------------------------------------ 🔁 B · GitHub relay: the small updates, when Drive is off

    static async Task RelayDeltas(State s)
    {
        if (DriveOn && DriveRoot.Length > 0) return;          // Drive carries them
        if (!Relay.On || Relay.Token.Length < 10) return;
        foreach (var name in new[] { "manifest.nes", "brief.nes", "brain.nes", "projects.nes" }.Concat(s.Deltas.TakeLast(6)))
        {
            var p = Path.Combine(Dir, name);
            if (!File.Exists(p) || new FileInfo(p).Length > 20_000_000) continue;
            await Relay.PutFile("sync/" + name, File.ReadAllBytes(p));
        }
    }

    // ------------------------------------------------------------------ 📶 C · office Wi-Fi: the phone downloads directly

    static TcpListener wifi;
    public static string WifiStatus = "";
    public static List<string> Addresses()
    {
        var res = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)) res.Add(a.Address.ToString());
            }
        }
        catch { }
        return res.Distinct().ToList();
    }

    public static void StartWifi()
    {
        if (wifi != null || !On || !WifiOn) return;
        try
        {
            wifi = new TcpListener(IPAddress.Any, WifiPort); wifi.Start(64);
            var l = wifi;
            // Its own thread (not the shared workers): the phone gets an answer even while Nova is busy preparing the copy.
            new Thread(() => Accept(l)) { IsBackground = true, Name = "Nova phone Wi-Fi" }.Start();
            WifiStatus = $"✓ Listening on {string.Join(", ", Addresses())} : {WifiPort}";
        }
        catch (Exception e) { wifi = null; WifiStatus = "⚠ " + e.Message; }
    }
    public static void StopWifi() { try { wifi?.Stop(); } catch { } wifi = null; WifiStatus = "Off"; }
    public static DateTime PhoneSeen = DateTime.MinValue;

    static void Accept(TcpListener l)
    {
        while (true)
        {
            TcpClient c;
            try { c = l.AcceptTcpClient(); } catch { return; }
            new Thread(() => { try { Serve(c).GetAwaiter().GetResult(); } catch { } }) { IsBackground = true, Name = "Nova phone file" }.Start();
        }
    }

    /// <summary>GET /nes/&lt;file&gt; with the pairing token: the encrypted file as it is. GET /hello: is this the paired PC?</summary>
    static async Task Serve(TcpClient client)
    {
        using var _c = client;
        try
        {
            client.ReceiveTimeout = 15000;
            var stream = client.GetStream();
            var (method, path, headers, reqBody) = await Server.Read(stream);
            if (method == null) return;
            int code = 200; byte[] body; string type = "application/octet-stream"; string sendFile = null;
            if (!headers.TryGetValue("X-Nova-Pair", out var t) || t != PairToken) { code = 403; body = Encoding.UTF8.GetBytes("{\"error\":\"not paired\"}"); type = "application/json"; }
            else if (path == "/hello")
            {
                PhoneSeen = DateTime.Now;
                if (headers.TryGetValue("X-Nova-Phone", out var ph) && ph.Length > 0) PhoneName = ph;
                body = Encoding.UTF8.GetBytes(new JsonObject { ["pc"] = Environment.MachineName, ["build"] = Program.Shown, ["at"] = S.LastRun.ToString("o") }.ToJsonString()); type = "application/json";
            }
            else if (path.StartsWith("/nes/"))
            {
                PhoneSeen = DateTime.Now;
                var rel = Uri.UnescapeDataString(path[5..].Split('?')[0]).Replace('/', Path.DirectorySeparatorChar);
                var full = Path.GetFullPath(Path.Combine(Dir, rel));
                if (!full.StartsWith(Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".nes") || !File.Exists(full)) { code = 404; body = Array.Empty<byte>(); }
                else { sendFile = full; body = Array.Empty<byte>(); }
            }
            else if (path == "/ask")
            {
                // 📶 A question from the paired phone on the office Wi-Fi (same encrypted request as through the relay, answered at once).
                PhoneSeen = DateTime.Now;
                body = Encoding.UTF8.GetBytes("NES:" + await PhoneRequest(reqBody.Trim())); type = "text/plain";
            }
            else if (path == "/sync-now") { _ = Task.Run(() => RunSafe("asked by the phone")); body = Encoding.UTF8.GetBytes("{\"ok\":true}"); type = "application/json"; }
            else { code = 404; body = Array.Empty<byte>(); }
            // Files go in pieces of 1 MB (never the whole file in memory).
            using var fs = sendFile != null ? new FileStream(sendFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20) : null;
            var len = fs?.Length ?? body.Length;
            var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : code == 403 ? "Forbidden" : "Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {len}\r\nConnection: close\r\n\r\n";
            client.SendTimeout = 120000;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            if (fs != null) await fs.CopyToAsync(stream, 1 << 20);
            else if (body.Length > 0) await stream.WriteAsync(body);
            await stream.FlushAsync();
        }
        catch { }
    }

    // ------------------------------------------------------------------ 🔁 live questions from the phone (encrypted, through the relay)

    /// <summary>A request from the paired phone: {"tool":"phone","enc":"…"} — only the phone with the key can make one, so it runs
    /// without the "Ask me" window. The answer is encrypted for the phone (full names: it is your own data, on your own phone).</summary>
    public static async Task<string> PhoneRequest(string enc)
    {
        JsonObject req;
        try { req = JsonNode.Parse(Encoding.UTF8.GetString(Open(Convert.FromBase64String(enc))))!.AsObject(); }
        catch { throw new Exception("not from the paired phone (wrong key)"); }
        var op = req["op"]?.ToString() ?? "";
        var a = req["args"] as JsonObject ?? new JsonObject();
        using var act = Activity.Begin("📱 phone · " + op, a.ToJsonString());
        // 📤 A change from the phone's outbox that arrives live: done once (the same change on Google Drive is skipped later).
        var oid = a["oid"]?.ToString() ?? "";
        if (oid.Length > 0 && Applied(oid)) { act.Result = "already applied"; return Convert.ToBase64String(Seal(Gzip(Encoding.UTF8.GetBytes(new JsonObject { ["ok"] = true, ["text"] = "✓ already applied on your PC" }.ToJsonString())))); }
        var res = await Exec(op, a);
        if (oid.Length > 0) MarkApplied(oid, op, "live");
        act.Result = "ok";
        return Convert.ToBase64String(Seal(Gzip(Encoding.UTF8.GetBytes(res.ToJsonString()))));
    }

    /// <summary>📄 The phone's Word file converted by Word itself (.doc → .docx to edit it, .docx → .doc to give it back): the format
    /// stays exactly right. Word runs hidden and is closed again if Nova started it; the copies are deleted.</summary>
    [System.Runtime.InteropServices.DllImport("oleaut32.dll")] static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.IUnknown)] out object obj);
    [System.Runtime.InteropServices.DllImport("ole32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int CLSIDFromProgID(string progId, out Guid clsid);

    static JsonObject WordConvert(JsonObject a)
    {
        var name = Path.GetFileName(a["name"]?.ToString() ?? "document.doc");
        if (Private(name)) throw new Exception("🔒 private");
        var toDoc = a["action"]?.ToString() == "todoc";
        var data = Convert.FromBase64String(a["data"]?.ToString() ?? "");
        var dir = Path.Combine(Paths.Cache, "word-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + (toDoc ? ".docx" : ".doc"));
        var dst = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + (toDoc ? ".doc" : ".docx"));
        File.WriteAllBytes(src, data);
        dynamic word = null; bool mine = false; dynamic d = null;
        try
        {
            // Word already open (your documents): used, never closed. Otherwise Nova starts a hidden Word and closes it after.
            try { if (CLSIDFromProgID("Word.Application", out var clsid) == 0 && GetActiveObject(ref clsid, IntPtr.Zero, out var running) == 0) word = running; } catch { }
            if (word == null) { var t = Type.GetTypeFromProgID("Word.Application") ?? throw new Exception("Microsoft Word is not installed on this PC"); word = Activator.CreateInstance(t); mine = true; word.Visible = false; }
            word.DisplayAlerts = 0;
            d = word.Documents.Open(src, false, false, false);
            d.SaveAs2(dst, toDoc ? 0 : 16);   // 0 = Word 97–2003 .doc · 16 = .docx
            d.Close(false); d = null;
            return new JsonObject { ["data"] = Convert.ToBase64String(File.ReadAllBytes(dst)), ["name"] = Path.GetFileName(dst) };
        }
        finally
        {
            try { if (d != null) d.Close(false); } catch { }
            try { if (mine && word != null) word.Quit(false); } catch { }
            try { if (word != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(word); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static async Task<JsonNode> Exec(string op, JsonObject a)
    {
        JsonNode res;
        switch (op)
        {
            case "search":
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                var ans = await Task.Run(() => Assistant.Ask(a["q"]?.ToString() ?? "", null, null, cts.Token, true, a["project"]?.ToString() ?? ""));
                res = new JsonObject { ["explain"] = ans.Explain, ["searched"] = ans.Searched, ["hits"] = new JsonArray(ans.Hits.Where(h => !Private(h.Subject, h.Folder, h.AttNames)).Select(h => (JsonNode)Hit(h)).ToArray()) };
                break;
            }
            case "brief":
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var (n, f) = await BriefLogic.Build(cts.Token);
                lastNeeds = n; lastFollow = f; briefAt = DateTime.Now;
                res = new JsonObject { ["needs"] = new JsonArray(n.Where(h => !Private(h.Subject, h.Folder, h.AttNames)).Select(h => (JsonNode)Hit(h)).ToArray()), ["follow"] = new JsonArray(f.Select(h => (JsonNode)Hit(h)).ToArray()) };
                break;
            }
            case "mail":
            {
                var id = a["i"]?.GetValue<long>() ?? 0;
                var h = MailIndex.ByIds(new[] { id }, 1, 200000).FirstOrDefault() ?? throw new Exception("email not in the index");
                if (Private(h.Subject, h.Folder, h.AttNames)) throw new Exception("🔒 private");
                var o = Hit(h); o["b"] = h.Body;
                res = o;
                break;
            }
            case "handled":
            {
                var id = a["i"]?.GetValue<long>() ?? 0;
                var h = MailIndex.ByIds(new[] { id }, 1).FirstOrDefault() ?? throw new Exception("email not in the index");
                Outstanding.MarkHandled(h, a["on"]?.GetValue<bool>() != false);
                res = new JsonObject { ["ok"] = true };
                break;
            }
            case "fetch":
            {
                // An email (.msg with its attachments) or a project file, when it was not starred.
                byte[] data; string name;
                if ((a["path"]?.ToString() ?? "").Length > 0)
                {
                    var p = a["path"]!.ToString();
                    if (Private(p) || !File.Exists(p)) throw new Exception("file not available");
                    if (new FileInfo(p).Length > 35_000_000) throw new Exception("file larger than 35 MB — star it to get it through the sync");
                    data = await File.ReadAllBytesAsync(p); name = System.IO.Path.GetFileName(p);
                }
                else
                {
                    var id = a["i"]?.GetValue<long>() ?? 0;
                    var h = MailIndex.ByIds(new[] { id }, 1).FirstOrDefault() ?? throw new Exception("email not in the index");
                    if (Private(h.Subject, h.Folder, h.AttNames)) throw new Exception("🔒 private");
                    var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nova-fetch-" + Guid.NewGuid().ToString("N") + ".msg");
                    Outlook.SaveMsg(h.Entry, h.StoreId, tmp);
                    data = await File.ReadAllBytesAsync(tmp); try { File.Delete(tmp); } catch { }
                    if (data.Length > 35_000_000) throw new Exception("email larger than 35 MB — star it to get it through the sync");
                    name = string.Join("_", (h.Subject ?? "email").Split(System.IO.Path.GetInvalidFileNameChars())) + ".msg";
                }
                res = new JsonObject { ["name"] = name, ["data"] = Convert.ToBase64String(Gzip(data)) };
                break;
            }
            case "html":
            {
                // ✉ The email as Outlook shows it (fonts, tables, logos, pictures) in one page, and its attachments by name.
                var id = a["i"]?.GetValue<long>() ?? 0;
                var h = MailIndex.ByIds(new[] { id }, 1, 200000).FirstOrDefault() ?? throw new Exception("email not in the index");
                if (Private(h.Subject, h.Folder, h.AttNames)) throw new Exception("🔒 private");
                string html = null;
                try { var f = await MailPreview.Render(h); if (f != null) html = OnePage(f); } catch (Exception e) { Log.Warn("phone · formatted", e.Message); }
                html ??= "<html><head><meta charset='utf-8'></head><body><pre style='white-space:pre-wrap;font-family:Calibri,Segoe UI,Arial;font-size:15px'>" + System.Net.WebUtility.HtmlEncode(h.Body ?? "") + "</pre></body></html>";
                var o = Hit(h); o["html"] = html;
                o["atts"] = new JsonArray((h.AttNames ?? "").Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).Select(x => (JsonNode)x).ToArray());
                res = o;
                break;
            }
            case "att":
            {
                var id = a["i"]?.GetValue<long>() ?? 0;
                var name = a["name"]?.ToString() ?? "";
                var h = MailIndex.ByIds(new[] { id }, 1).FirstOrDefault() ?? throw new Exception("email not in the index");
                if (Private(h.Subject, h.Folder, h.AttNames, name)) throw new Exception("🔒 private");
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nova-phone-att-" + Guid.NewGuid().ToString("N"));
                var path = await MailPreview.SaveAttachment(h, name, dir);
                try
                {
                    if (new FileInfo(path).Length > 35_000_000) throw new Exception("attachment larger than 35 MB — open it on the PC");
                    res = new JsonObject { ["name"] = System.IO.Path.GetFileName(path), ["data"] = Convert.ToBase64String(Gzip(await File.ReadAllBytesAsync(path))) };
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
                break;
            }
            case "admincode":
            {
                // 🔐 Forgot the Nova PIN: the one-time code, only to the paired phone (sealed with its key).
                var c = AdminLock.PhoneCode();
                res = new JsonObject { ["code"] = c, ["until"] = c.Length > 0 ? AdminLock.CodeUntil.ToString("HH:mm") : "", ["locked"] = AdminLock.Locked, ["on"] = AdminLock.Enabled };
                break;
            }
            case "projfound":
            {
                // ＋ Projects found in your emails — the same list, ✚ add · ⇢ merge · ✗ ignore from the phone.
                var m = a.DeepClone().AsObject();
                if ((m["t"]?.ToString() ?? "").Length == 0) m["t"] = "pf.load";
                res = await Task.Run(() => ProjectFinder.Handle(m));
                break;
            }
            case "word": res = await Task.Run(() => WordConvert(a)); break;   // 📄 .doc ⇄ .docx by Word on this PC
            case "agent": res = await PhoneAgent.Handle(a); break;   // 🧭
            case "claude":
            {
                // 🔗 Allow Claude to read Outlook (from the phone): hours 0 = stop
                if (a["hours"] != null) ClaudeAccess.Allow(a["hours"]!.GetValue<double>());
                res = new JsonObject { ["on"] = ClaudeAccess.On, ["until"] = ClaudeAccess.On ? ClaudeAccess.Until.ToString("dd MMM HH:mm") : "" };
                break;
            }
            case "brainteach":
            {
                // 🎓 Teach the Brain from the phone: these emails are examples (✓ like this / ✗ not like this)
                var ids = (a["ids"] as JsonArray ?? new()).Select(x => long.TryParse(x?.ToString(), out var v) ? v : 0).Where(v => v > 0).ToList();
                var mails = MailIndex.ByIds(ids, 10, 200000).Where(h => !Private(h.Subject, h.Folder, h.AttNames)).ToList();
                if (mails.Count == 0) throw new Exception("these emails are not on the PC (or 🔒 private)");
                res = new JsonObject { ["text"] = BrainTrainer.QueueFromPhone(a["text"]?.ToString() ?? "", mails, a["good"]?.GetValue<bool>() != false) };
                break;
            }   // 🧭 the Nova agent from the phone (✓ / ✗ examples there)
            case "sync": _ = Task.Run(() => RunSafe("asked by the phone")); res = new JsonObject { ["ok"] = true }; break;
            case "hello": PhoneSeen = DateTime.Now; if (a["phone"]?.ToString() is { Length: > 0 } ph) PhoneName = ph; res = new JsonObject { ["pc"] = Environment.MachineName, ["build"] = Program.Shown }; break;
            default: throw new Exception("unknown request " + op);
        }
        return res;
    }

    // ------------------------------------------------------------------ 📤 the phone's outbox in Google Drive (changes made while the PC was off)

    static readonly object OutLock = new();
    static FileSystemWatcher outWatch;

    static string OutboxDir => DriveRoot.Length > 0 ? Path.Combine(DriveRoot, "Nova phone outbox") : "";
    static string AppliedPath => Path.Combine(Paths.Root, "Rules", "phone-applied.json");

    static JsonObject AppliedList()
    {
        try { return File.Exists(AppliedPath) ? JsonNode.Parse(File.ReadAllText(AppliedPath))!.AsObject() : new JsonObject(); } catch { return new JsonObject(); }
    }
    static bool Applied(string id) { lock (OutLock) return AppliedList().ContainsKey(id); }

    static void MarkApplied(string id, string op, string how)
    {
        lock (OutLock)
        {
            var o = AppliedList();
            o[id] = new JsonObject { ["t"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(), ["op"] = op, ["how"] = how };
            // the last 300 are enough to never apply twice
            if (o.Count > 300) foreach (var k in o.OrderBy(x => x.Value?["t"]?.GetValue<long>() ?? 0).Take(o.Count - 300).Select(x => x.Key).ToList()) o.Remove(k);
            Directory.CreateDirectory(Path.GetDirectoryName(AppliedPath)!);
            File.WriteAllText(AppliedPath, o.ToJsonString());
            // the phone sees “✓ applied on your PC 14:05” with its next update
            try { Write("applied.nes", o.ToJsonString()); } catch { }
        }
    }

    /// <summary>📤 Applies what the phone left in “Nova phone outbox” (oldest first), once each, then removes it. At start, every 15 min, on wake-up, and when a file arrives.</summary>
    public static async Task ReadOutbox(string why)
    {
        var dir = OutboxDir;
        if (dir.Length == 0 || !Directory.Exists(dir) || !On) return;
        string[] files;
        try { files = Directory.GetFiles(dir, "*.nes").OrderBy(f => Path.GetFileName(f)).ToArray(); } catch { return; }
        if (files.Length == 0) return;
        using var act = Activity.Begin("📤 phone outbox", $"{files.Length} change(s) · {why}");
        int done = 0;
        foreach (var f in files)
        {
            try
            {
                JsonObject req;
                try { req = JsonNode.Parse(Encoding.UTF8.GetString(Gunzip(Open(await File.ReadAllBytesAsync(f)))))!.AsObject(); }
                catch (IOException) { continue; }   // still being written by Google Drive: next time
                catch { Log.Warn("phone outbox", "not from the paired phone: " + Path.GetFileName(f)); try { File.Move(f, f + ".bad", true); } catch { } continue; }
                var id = req["id"]?.ToString() ?? Path.GetFileNameWithoutExtension(f);
                var op = req["op"]?.ToString() ?? "";
                if (!Applied(id))
                {
                    var a = req["args"] as JsonObject ?? new JsonObject();
                    using (Activity.Begin("📱 phone (outbox) · " + op, a.ToJsonString())) await Exec(op, a);
                    MarkApplied(id, op, "drive");
                    done++;
                }
                File.Delete(f);
            }
            catch (Exception e) { Log.Warn("phone outbox", Path.GetFileName(f) + ": " + e.Message); }
        }
        act.Result = $"{done} applied";
        if (done > 0) Status = $"📤 {done} change(s) from the phone applied";
    }

    static byte[] Gunzip(byte[] b)
    {
        using var i = new System.IO.Compression.GZipStream(new MemoryStream(b), System.IO.Compression.CompressionMode.Decompress);
        using var o = new MemoryStream(); i.CopyTo(o); return o.ToArray();
    }

    static void WatchOutbox()
    {
        try
        {
            var dir = OutboxDir;
            if (dir.Length == 0 || outWatch != null) return;
            Directory.CreateDirectory(dir);
            outWatch = new FileSystemWatcher(dir, "*.nes") { EnableRaisingEvents = true };
            DateTime last = DateTime.MinValue;
            outWatch.Created += (_, _) => { if (DateTime.Now - last < TimeSpan.FromSeconds(20)) return; last = DateTime.Now; _ = Task.Run(async () => { await Task.Delay(8000); await ReadOutbox("a change arrived"); }); };
        }
        catch (Exception e) { Log.Warn("phone outbox", "watch: " + e.Message); }
    }

    /// <summary>The rendered email page with its pictures inside it (one file the phone can show offline).</summary>
    static string OnePage(string file)
    {
        var dir = System.IO.Path.GetDirectoryName(file)!;
        var html = File.ReadAllText(file);
        return System.Text.RegularExpressions.Regex.Replace(html, @"(src\s*=\s*[""']?)(img\d+\.[A-Za-z0-9]+)", m =>
        {
            var p = System.IO.Path.Combine(dir, m.Groups[2].Value);
            if (!File.Exists(p) || new FileInfo(p).Length > 3_000_000) return m.Value;
            var ext = System.IO.Path.GetExtension(p).TrimStart('.').ToLowerInvariant();
            var mime = ext switch { "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", "bmp" => "image/bmp", _ => "image/png" };
            return m.Groups[1].Value + "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(p));
        });
    }

    // ------------------------------------------------------------------ ☁ the phone reads Google Drive itself (no PC needed)

    /// <summary>The sync folder's path inside My Drive ("Nova Enhance Sync", or "Work/Nova Enhance Sync" when a sub-folder was picked).</summary>
    static string InMyDrive(string full)
    {
        var parts = full.Replace('/', '\\').TrimEnd('\\').Split('\\');
        var i = Array.FindLastIndex(parts, p => p is "My Drive" or "Mon Drive" or "Meine Ablage" or "Mein Ablage" or "Mi unidad" or "Il mio Drive");
        return i >= 0 ? string.Join("/", parts.Skip(i + 1)) : parts[^1];
    }

    /// <summary>Where the work folder is copied in My Drive (FreeFileSync or Drive itself): "" = My Drive root, "_MZA" = a folder, null = not found.
    /// Settings → 📱 Phone link can set it; "auto" checks the project folders' names.</summary>
    public static string Mirror
    {
        get
        {
            var set = Store.Str("win_pl_mirror", "auto");
            if (set != "auto") return set == "-" ? null : set.Trim('/', '\\');
            try
            {
                var work = Projects.WorkFolder; var root = DriveRoot;
                if (work.Length == 0 || root.Length == 0 || !Directory.Exists(work)) return null;
                var names = Directory.GetDirectories(work).Select(Path.GetFileName).Where(n => !n.StartsWith('.')).Take(40).ToList();
                if (names.Count == 0) return null;
                foreach (var cand in new[] { "", Path.GetFileName(work.TrimEnd('\\')) })
                {
                    var b = cand.Length == 0 ? root : Path.Combine(root, cand);
                    if (Directory.Exists(b) && names.Count(n => Directory.Exists(Path.Combine(b, n))) * 2 >= names.Count) return cand;
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>What the phone needs to read Google Drive by itself: the Google sign-in app (yours, Settings → Google Drive), the folder, the mirror.</summary>
    static JsonObject PhoneDrive()
    {
        if (!DriveOn) return null;       // the folder and the work-folder copy always go; the sign-in app only when this PC has its own
        var root = DriveRoot;
        return new JsonObject
        {
            ["id"] = GDrive.OwnApp ? GDrive.ClientId : "", ["secret"] = GDrive.OwnApp ? GDrive.ClientSecret : "", ["folder"] = root.Length > 0 ? InMyDrive(Dir) : "Nova Enhance Sync",
            ["work"] = Projects.WorkFolder, ["mirror"] = Mirror
        };
    }

    // ------------------------------------------------------------------ pairing QR

    /// <summary>What the QR code holds: the key, this PC's office addresses, and the relay (repository + key) for questions away from the office.</summary>
    public static string PairPayload() => new JsonObject
    {
        ["nova"] = 2, ["pc"] = Environment.MachineName, ["key"] = Convert.ToBase64String(Key), ["ip"] = new JsonArray(Addresses().Select(a => (JsonNode)a).ToArray()),
        ["port"] = WifiPort, ["relay"] = Relay.Repo, ["rt"] = Relay.Token, ["drive"] = DriveOn ? "Nova Enhance Sync" : "", ["gd"] = PhoneDrive()
    }.ToJsonString();

    /// <summary>The QR code: a link the phone's camera opens in Nova Enhance (novaenhance://pair?d=…).</summary>
    public static string PairLink() => "novaenhance://pair?d=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(PairPayload())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ------------------------------------------------------------------ sizes (Settings and the relay status)

    public static string SizesText()
    {
        static string Mb(long b) => b >= 1_000_000_000 ? $"{b / 1e9:0.00} GB" : $"{b / 1e6:0.0} MB";
        var sb = new StringBuilder();
        try { sb.Append($"email index database {Mb(new FileInfo(MailIndex.DbPath).Length)}"); } catch { }
        try { var f = Path.Combine(FolderIndex.DirPath, "folders.db"); if (File.Exists(f)) sb.Append($" · folder index {Mb(new FileInfo(f).Length)}"); } catch { }
        var s = S;
        if (s.Sizes.Count > 0) sb.Append(" · phone copy: " + string.Join(", ", s.Sizes.Select(x => $"{x.Key} {Mb(x.Value)}")));
        return sb.ToString();
    }
}
