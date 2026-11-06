using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>
/// One backup file, the same format as the phone app (a phone backup restores here and the other way round):
/// settings, history, and the API keys plus Windows email choices locked with the password (AES-256-GCM, PBKDF2-SHA256).
/// </summary>
static class Backup
{
    const int Iter = 120_000;
    public const string Ext = ".novaenhance";

    public static string Build(string password)
    {
        var settings = new JsonObject();
        var keys = new JsonObject();
        var win = new JsonObject();
        var winSecrets = new JsonObject();
        foreach (var (k, v) in Store.Snapshot())
        {
            if (k == "win_backup_pw") continue;
            if (k.StartsWith("key_")) { var s = Store.Secret(k); if (s.Length > 0) keys[k] = s; continue; }
            if (k.StartsWith("win_") && v is JsonValue sv && sv.TryGetValue<string>(out var str) && str.StartsWith("dpapi:"))
            {
                // Locked to this Windows account: unlock and keep it under the backup password instead, so it works on another PC.
                var plain = Store.Secret(k); if (plain.Length > 0) winSecrets[k] = plain; continue;
            }
            if (k.StartsWith("win_")) { win[k] = v?.DeepClone(); continue; }
            var t = Typed(v);
            if (t != null) settings[k] = t;
        }
        var root = new JsonObject
        {
            ["app"] = "Nova Enhance", ["format"] = 1, ["created"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(), ["from"] = "windows",
            ["settings"] = settings, ["history"] = History.ExportForBackup()
        };
        if (keys.Count > 0) root["keys"] = Encrypt(keys.ToJsonString(), password);
        root["windows"] = Encrypt(win.ToJsonString(), password);
        var work = new JsonArray();
        try { foreach (var f in Directory.GetFiles(Work.Dir, "*.json")) work.Add(File.ReadAllText(f)); } catch { }
        var extra = new JsonObject { ["secrets"] = winSecrets, ["originals"] = OriginalsText(), ["work"] = work };
        root["windows_extra"] = Encrypt(extra.ToJsonString(), password);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static string OriginalsFile => Path.Combine(Paths.Originals, "originals.jsonl");

    static string OriginalsText()
    {
        try { return File.Exists(OriginalsFile) ? File.ReadAllText(OriginalsFile) : ""; } catch { return ""; }
    }

    static int RestoreOriginals(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var have = File.Exists(OriginalsFile) ? new HashSet<string>(File.ReadAllLines(OriginalsFile)) : new HashSet<string>();
        var add = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !have.Contains(l)).ToList();
        if (add.Count == 0) return 0;
        // Merge by time so the newest 20 shown in Nova stay the real newest.
        var all = have.Where(l => l.Trim().Length > 0).Concat(add)
            .OrderBy(l => { try { return JsonNode.Parse(l)?["Ts"]?.GetValue<long>() ?? 0; } catch { return 0L; } }).ToList();
        File.WriteAllLines(OriginalsFile, all.TakeLast(500));
        return add.Count;
    }

    /// <summary>Opens a backup file that was just written and unlocks every locked part, so a damaged file is found now and not on the day it's needed.</summary>
    public static string Verify(string path, string password)
    {
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(path))?.AsObject(); } catch { throw new InvalidDataException("the file can't be read back"); }
        if (root?["app"]?.ToString() != "Nova Enhance" || root["settings"] is not JsonObject) throw new InvalidDataException("the file is incomplete");
        foreach (var part in new[] { "keys", "windows", "windows_extra" })
            if (root[part] is JsonObject enc)
                try { JsonNode.Parse(Decrypt(enc, password)); } catch { throw new InvalidDataException($"the locked part \"{part}\" can't be opened with the password"); }
        return $"✓ checked ({new FileInfo(path).Length / 1024.0:0.#} KB)";
    }

    static JsonObject Typed(JsonNode v)
    {
        if (v is not JsonValue jv) return null;
        if (jv.TryGetValue<bool>(out var b)) return new JsonObject { ["t"] = "b", ["v"] = b };
        if (jv.TryGetValue<string>(out var s)) return new JsonObject { ["t"] = "s", ["v"] = s };
        if (jv.TryGetValue<long>(out var l)) return l is >= int.MinValue and <= int.MaxValue ? new JsonObject { ["t"] = "i", ["v"] = l } : new JsonObject { ["t"] = "l", ["v"] = l };
        if (jv.TryGetValue<int>(out var i)) return new JsonObject { ["t"] = "i", ["v"] = i };
        if (jv.TryGetValue<double>(out var d)) return new JsonObject { ["t"] = "l", ["v"] = (long)d };
        return null;
    }

    /// <summary>Restores a phone or Windows backup. Returns a summary line; throws with a clear message on a bad file.</summary>
    public static string Restore(string text, string password)
    {
        JsonObject root;
        try { root = JsonNode.Parse(text)?.AsObject(); } catch { throw new InvalidDataException("This isn't a Nova Enhance backup file."); }
        if (root == null || root["app"]?.ToString() != "Nova Enhance") throw new InvalidDataException("This isn't a Nova Enhance backup file.");

        int n = 0;
        if (root["settings"] is JsonObject settings)
            foreach (var (k, o) in settings)
            {
                if (o is not JsonObject t) continue;
                var v = t["v"];
                switch (t["t"]?.ToString())
                {
                    case "b": Store.SetNode(k, JsonValue.Create(v?.GetValue<bool>() ?? false)); n++; break;
                    case "i": case "l": Store.SetNode(k, JsonValue.Create(ToLong(v))); n++; break;
                    case "s": Store.SetNode(k, JsonValue.Create(v?.ToString() ?? "")); n++; break;
                }
            }

        var keysNote = "no API keys in the file";
        if (root["keys"] is JsonObject enc)
        {
            try
            {
                var keys = JsonNode.Parse(Decrypt(enc, password)).AsObject();
                foreach (var (k, v) in keys) Store.SetSecret(k, v?.ToString());
                keysNote = $"{keys.Count} API key(s) restored";
            }
            catch { keysNote = "API keys NOT restored (wrong password) — re-enter them in AI & API keys"; }
        }

        var winNote = "";
        if (root["windows"] is JsonObject wenc)
        {
            try
            {
                var win = JsonNode.Parse(Decrypt(wenc, password)).AsObject();
                foreach (var (k, v) in win) if (k != "win_backup_pw" && !k.StartsWith("win_last")) Store.SetNode(k, v?.DeepClone());
                winNote = " · Windows email settings restored";
            }
            catch { winNote = " · Windows email settings NOT restored (wrong password)"; }
        }

        var extraNote = "";
        if (root["windows_extra"] is JsonObject xenc)
        {
            try
            {
                var x = JsonNode.Parse(Decrypt(xenc, password)).AsObject();
                int ns = 0;
                if (x["secrets"] is JsonObject sec) foreach (var (k, v) in sec) { Store.SetSecret(k, v?.ToString()); ns++; }
                int no = RestoreOriginals(x["originals"]?.ToString());
                int nw = 0;
                foreach (var w in (x["work"] as JsonArray ?? new JsonArray()).Select(v => v?.ToString()).Where(v => !string.IsNullOrEmpty(v)))
                    try
                    {
                        var id = JsonNode.Parse(w)?["Id"]?.ToString();
                        if (string.IsNullOrEmpty(id)) continue;
                        var file = Path.Combine(Work.Dir, id + ".json");
                        if (!File.Exists(file)) { File.WriteAllText(file, w, new UTF8Encoding(false)); nw++; }
                    }
                    catch { }
                extraNote = $" · {ns} sign-in(s) restored (Google Drive) · {no} original(s) restored · {nw} task(s) restored (My Work)";
            }
            catch { extraNote = " · Google Drive sign-in and originals NOT restored (wrong password)"; }
        }

        int conv = 0;
        if (root["history"] is JsonArray hist && hist.Count > 0)
        {
            conv = hist.Count;
            var from = root["from"]?.ToString() == "windows" ? "windows" : "phone";
            File.WriteAllText(Path.Combine(Paths.History, $"restored-{from}-{DateTime.Now:yyyy-MM-dd-HHmm}.json"), hist.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        var applied = Reapply();
        return $"{n} setting(s) restored · {keysNote}{winNote}{extraNote} · {conv} conversation(s) saved in History{applied}";
    }

    /// <summary>Sets up again on this PC what lives outside the settings file: start with Windows, the Explorer menu, the Office add-in.</summary>
    public static Func<bool> OfficeReinstall = () => { OfficeAddin.InstallForMe(); return true; };

    static string Reapply()
    {
        var done = new List<string>();
        try { Startup.Apply(); done.Add("start with Windows"); } catch (Exception e) { Log.Error("restore startup", e); }
        try { ExplorerMenu.Apply(); done.Add("Explorer menu"); } catch (Exception e) { Log.Error("restore explorer", e); }
        if (BrowserAddon.On)
            try { BrowserAddon.Enable(); done.Add("browser add-on (files in Documents\\Nova Enhance\\Browser Add-on)"); } catch (Exception e) { Log.Error("restore browser add-on", e); }
        if (Store.Bool("win_addin", false))
            try { if (OfficeReinstall()) done.Add("Office add-in"); } catch (Exception e) { Log.Error("restore office", e); done.Add("⚠ Office add-in (open Settings → Office)"); }
        return done.Count > 0 ? "\nSet up again on this PC: " + string.Join(", ", done) : "";
    }

    static long ToLong(JsonNode v)
    {
        if (v == null) return 0;
        try { return v.GetValue<long>(); } catch { }
        try { return v.GetValue<int>(); } catch { }
        try { return (long)v.GetValue<double>(); } catch { }
        return long.TryParse(v.ToString(), out var x) ? x : 0;
    }

    static byte[] KeyFrom(string password, byte[] salt, int iter) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? ""), salt, iter, HashAlgorithmName.SHA256, 32);

    public static JsonObject Encrypt(string plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var g = new AesGcm(KeyFrom(password, salt, Iter), 16)) g.Encrypt(iv, data, cipher, tag);
        // Java's AES/GCM puts the tag after the ciphertext: same here so the phone can read it.
        var all = new byte[cipher.Length + 16];
        Buffer.BlockCopy(cipher, 0, all, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, all, cipher.Length, 16);
        return new JsonObject
        {
            ["alg"] = "AES-256-GCM/PBKDF2-SHA256", ["iter"] = Iter,
            ["salt"] = Convert.ToBase64String(salt), ["iv"] = Convert.ToBase64String(iv), ["data"] = Convert.ToBase64String(all)
        };
    }

    public static string Decrypt(JsonObject o, string password)
    {
        var salt = Convert.FromBase64String(o["salt"].ToString());
        var iv = Convert.FromBase64String(o["iv"].ToString());
        var all = Convert.FromBase64String(o["data"].ToString());
        int iter = (int)ToLong(o["iter"]); if (iter <= 0) iter = Iter;
        if (all.Length < 16) throw new CryptographicException("short");
        var cipher = all[..^16];
        var tag = all[^16..];
        var plain = new byte[cipher.Length];
        using (var g = new AesGcm(KeyFrom(password, salt, iter), 16)) g.Decrypt(iv, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Writes a backup to Documents\Nova Enhance\Backups. Returns the file path.</summary>
    public static string WriteFile(string password, bool auto)
    {
        var name = $"Nova-Enhance-Backup-{DateTime.Now:yyyy-MM-dd-HHmm}{(auto ? "-auto" : "")}{Ext}";
        var path = Path.Combine(Paths.Backups, name);
        File.WriteAllText(path, Build(password), new UTF8Encoding(false));
        try { LastCheck = Verify(path, password); }
        catch (Exception e)
        {
            LastCheck = "⚠ check failed: " + e.Message;
            Log.Error("backup check", e);
            Program.Balloon("Backup check failed", "The new backup could not be opened again (" + e.Message + "). Try Backup now in Settings.");
            throw new InvalidDataException("The backup was written but the check failed: " + e.Message);
        }
        return path;
    }

    public static string LastCheck = "";

    public static bool DriveCopy { get => Store.Bool("win_backup_gdrive", false); set => Store.SetBool("win_backup_gdrive", value); }

    /// <summary>Second copy in Google Drive ("Nova Enhance Backups"), when the box is ticked. Returns a short status.</summary>
    public static async Task<string> CopyToDrive(string path)
    {
        if (!DriveCopy) return "";
        try
        {
            await GDrive.SaveBackup(path, Store.KeepBackups);
            var note = $"☁ copied to Google Drive {DateTime.Now:dd MMM HH:mm}";
            Store.Set("win_last_drive_note", "✓ " + note);
            return " · " + note;
        }
        catch (Exception e)
        {
            Log.Error("backup drive copy", e);
            Store.Set("win_last_drive_note", $"⚠ Google Drive copy failed {DateTime.Now:dd MMM HH:mm}: {e.Message}");
            return " · ⚠ Google Drive copy failed: " + e.Message;
        }
    }
}

/// <summary>Daily automatic backup, keeping the newest 14 (only the automatic ones are removed).</summary>
static class AutoBackup
{
    public static string Status => Store.Str("win_last_backup_note", "No automatic backup yet");

    public static async void MaybeRun()
    {
        if (!Store.AutoBackup) return;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (Store.Str("win_last_backup_day") == today) return;
        var pw = Store.BackupPassword;
        if (pw.Length == 0)
        {
            if (Store.Str("win_last_pw_nag") != today)
            {
                Store.Set("win_last_pw_nag", today);
                Program.Balloon("Daily backup is waiting", "Set a backup password in Settings → Backup & restore to turn on the daily backup.");
            }
            return;
        }
        try
        {
            var f = Backup.WriteFile(pw, true);
            Store.Set("win_last_backup_day", today);
            Store.Set("win_last_backup_note", $"✓ Last automatic backup {DateTime.Now:dd MMM yyyy HH:mm} · {Path.GetFileName(f)} · {Backup.LastCheck}");
            Prune();
            var drive = await Backup.CopyToDrive(f);
            if (drive.Length > 0) Store.Set("win_last_backup_note", Store.Str("win_last_backup_note") + drive);
        }
        catch (Exception e) { Store.Set("win_last_backup_note", "⚠ Automatic backup failed: " + e.Message); Log.Error("auto backup", e); }
    }

    public static void Prune()
    {
        try
        {
            var autos = Directory.GetFiles(Paths.Backups, "Nova-Enhance-Backup-*-auto" + Backup.Ext).OrderByDescending(f => f).ToList();
            foreach (var f in autos.Skip(Store.KeepBackups)) File.Delete(f);
        }
        catch (Exception e) { Log.Error("backup prune", e); }
    }
}
