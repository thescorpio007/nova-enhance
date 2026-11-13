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
    public static int EveryMin { get => Math.Clamp(Store.Int("win_pl_every", 60), 15, 720); set => Store.SetInt("win_pl_every", value); }
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
    static readonly object RunLock = new();
    public static void Start()
    {
        if (On && WifiOn) StartWifi();
        if (loop != null) return;
        loop = new Thread(() => Guard.Run("phone link", () =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(3));
            while (true)
            {
                if (On && DateTime.Now - S.LastRun >= TimeSpan.FromMinutes(EveryMin)) RunSafe("every " + EveryMin + " min");
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
            foreach (var f in Directory.GetFiles(Dir, "*.nes", SearchOption.AllDirectories)) if (!f.Contains(Path.DirectorySeparatorChar + "starred" + Path.DirectorySeparatorChar)) try { File.Delete(f); } catch { }
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
        foreach (var ch in MailProjects.Choices()) choices.Add(new JsonObject { ["code"] = ch.Code, ["label"] = ch.Label });
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
        ["i"] = h.Id, ["e"] = h.Entry, ["d"] = new DateTimeOffset(h.Date).ToUnixTimeMilliseconds(), ["s"] = h.Sent, ["fn"] = h.FromName, ["fa"] = h.FromAddr,
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
                        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nova-star-" + Guid.NewGuid().ToString("N") + ".msg");
                        Outlook.SaveMsg(x.Entry, x.StoreId, tmp);
                        data = File.ReadAllBytes(tmp); try { File.Delete(tmp); } catch { }
                        file = string.Join("_", (x.Title.Length > 0 ? x.Title : "email").Split(System.IO.Path.GetInvalidFileNameChars())) + ".msg";
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
        var files = new JsonArray();
        foreach (var f in Directory.GetFiles(Dir, "*.nes", SearchOption.AllDirectories).OrderBy(f => f))
        {
            var rel = Path.GetRelativePath(Dir, f).Replace('\\', '/');
            if (rel == "manifest.nes") continue;
            var fi = new FileInfo(f);
            files.Add(new JsonObject { ["n"] = rel, ["b"] = fi.Length, ["m"] = new DateTimeOffset(fi.LastWriteTime).ToUnixTimeMilliseconds() });
        }
        var o = new JsonObject
        {
            ["v"] = 2, ["pc"] = Environment.MachineName, ["build"] = Program.Shown, ["at"] = DateTime.Now.ToString("o"), ["fullAt"] = s.FullAt.ToString("o"),
            ["last12"] = Last12, ["files"] = files, ["counts"] = JsonNode.Parse(JsonSerializer.Serialize(s.Counts)), ["oldest"] = MailIndex.OldestText
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
            wifi = new TcpListener(IPAddress.Any, WifiPort); wifi.Start();
            var l = wifi; _ = Task.Run(() => Accept(l));
            WifiStatus = $"✓ Listening on {string.Join(", ", Addresses())} : {WifiPort}";
        }
        catch (Exception e) { wifi = null; WifiStatus = "⚠ " + e.Message; }
    }
    public static void StopWifi() { try { wifi?.Stop(); } catch { } wifi = null; WifiStatus = "Off"; }
    public static DateTime PhoneSeen = DateTime.MinValue;

    static async Task Accept(TcpListener l)
    {
        while (true)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(); } catch { return; }
            _ = Serve(c);
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
            var (method, path, headers, _) = await Server.Read(stream);
            if (method == null) return;
            int code = 200; byte[] body; string type = "application/octet-stream";
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
                else body = await File.ReadAllBytesAsync(full);
            }
            else if (path == "/sync-now") { _ = Task.Run(() => RunSafe("asked by the phone")); body = Encoding.UTF8.GetBytes("{\"ok\":true}"); type = "application/json"; }
            else { code = 404; body = Array.Empty<byte>(); }
            var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : code == 403 ? "Forbidden" : "Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            if (body.Length > 0) await stream.WriteAsync(body);
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
                    if (new FileInfo(p).Length > 15_000_000) throw new Exception("file larger than 15 MB — star it to get it through the sync");
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
                    if (data.Length > 15_000_000) throw new Exception("email larger than 15 MB — star it to get it through the sync");
                    name = string.Join("_", (h.Subject ?? "email").Split(System.IO.Path.GetInvalidFileNameChars())) + ".msg";
                }
                res = new JsonObject { ["name"] = name, ["data"] = Convert.ToBase64String(Gzip(data)) };
                break;
            }
            case "sync": _ = Task.Run(() => RunSafe("asked by the phone")); res = new JsonObject { ["ok"] = true }; break;
            case "hello": PhoneSeen = DateTime.Now; if (a["phone"]?.ToString() is { Length: > 0 } ph) PhoneName = ph; res = new JsonObject { ["pc"] = Environment.MachineName, ["build"] = Program.Shown }; break;
            default: throw new Exception("unknown request " + op);
        }
        act.Result = "ok";
        return Convert.ToBase64String(Seal(Gzip(Encoding.UTF8.GetBytes(res.ToJsonString()))));
    }

    // ------------------------------------------------------------------ pairing QR

    /// <summary>What the QR code holds: the key, this PC's office addresses, and the relay (repository + key) for questions away from the office.</summary>
    public static string PairPayload() => new JsonObject
    {
        ["nova"] = 2, ["pc"] = Environment.MachineName, ["key"] = Convert.ToBase64String(Key), ["ip"] = new JsonArray(Addresses().Select(a => (JsonNode)a).ToArray()),
        ["port"] = WifiPort, ["relay"] = Relay.Repo, ["rt"] = Relay.Token, ["drive"] = DriveOn ? "Nova Enhance Sync" : ""
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
