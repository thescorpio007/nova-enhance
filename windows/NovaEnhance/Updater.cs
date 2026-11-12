using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>
/// Updates from GitHub Releases (tags win-build-N). Before anything runs:
///  1. SHA256SUMS.txt must carry a valid signature (SHA256SUMS.sig) made with Nova Enhance's own signing key (built in here),
///  2. the downloaded Setup.exe must match its SHA-256 line.
/// Checked once a day and from Settings › Updates; the user clicks once to install.
/// </summary>
static class Updater
{
    const string Repo = "thescorpio007/nova-enhance";
    const string TagPrefix = "win-build-";
    const string Want = "-Setup.exe";

    public class Ready { public int Build; public string File; public string Notes; }

    static string StateFile => Path.Combine(Paths.Cache, "update.json");
    static string Folder => Path.Combine(Paths.Cache, "update");

    static JsonObject Load() { try { return JsonNode.Parse(File.ReadAllText(StateFile))?.AsObject() ?? new JsonObject(); } catch { return new JsonObject(); } }
    static void Save(JsonObject o) { try { File.WriteAllText(StateFile, o.ToJsonString()); } catch { } }

    public static string Status => Load()["status"]?.ToString() ?? "";
    static string Done(string s) { var o = Load(); o["status"] = s; Save(o); return s; }

    public static bool Due
    {
        get
        {
            long last = 0;
            try { last = Load()["lastCheck"]?.GetValue<long>() ?? 0; } catch { }
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last > 24L * 3600 * 1000;
        }
    }

    public static Ready Waiting()
    {
        var o = Load();
        int b = 0;
        try { b = o["readyBuild"]?.GetValue<int>() ?? 0; } catch { }
        var f = o["readyFile"]?.ToString() ?? "";
        if (b <= Program.Build || !File.Exists(f)) return null;
        return new Ready { Build = b, File = f, Notes = o["readyNotes"]?.ToString() ?? "" };
    }

    static HttpClient Client()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("NovaEnhance-Windows-updater");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<string> Check(Action<string> progress = null)
    {
        if (!await Gate.WaitAsync(0)) return "Already checking…";
        try
        {
            var st = Load(); st["lastCheck"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Save(st);
            progress?.Invoke("Checking GitHub…");
            using var http = Client();
            var rel = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=50"))?.AsArray() ?? new JsonArray();
            JsonObject best = null; int bestBuild = 0;
            foreach (var n in rel)
            {
                if (n is not JsonObject r) continue;
                if (r["draft"]?.GetValue<bool>() == true || r["prerelease"]?.GetValue<bool>() == true) continue;
                var tag = r["tag_name"]?.ToString() ?? "";
                if (!tag.StartsWith(TagPrefix) || !int.TryParse(tag[TagPrefix.Length..], out var b)) continue;
                var names = (r["assets"] as JsonArray)?.Select(a => a?["name"]?.ToString() ?? "").ToList() ?? new();
                if (!names.Contains("SHA256SUMS.txt") || !names.Contains("SHA256SUMS.sig") || !names.Any(x => x.EndsWith(Want))) continue;
                if (b > bestBuild) { bestBuild = b; best = r; }
            }
            if (best == null || bestBuild <= Program.Build) return Done($"✓ Up to date (build {Program.Build})");
            var ready = Waiting();
            if (ready != null && ready.Build == bestBuild) return Done($"Build {bestBuild} is ready to install");
            return Done(await Fetch(http, best, bestBuild, progress, Folder));
        }
        catch (Exception e) { return Done("⚠ Update check failed: " + e.Message); }
        finally { Gate.Release(); }
    }

    /// <summary>Downloads and verifies one release. Returns the status line; on success the file is recorded as ready.</summary>
    static async Task<string> Fetch(HttpClient http, JsonObject release, int build, Action<string> progress, string folder, bool record = true, Func<byte[], byte[]> tamper = null)
    {
        var urls = new Dictionary<string, string>();
        foreach (var a in release["assets"] as JsonArray ?? new JsonArray())
            if (a is JsonObject o) urls[o["name"]?.ToString() ?? ""] = o["browser_download_url"]?.ToString() ?? "";
        progress?.Invoke($"Checking the signature of build {build}…");
        var sums = await http.GetByteArrayAsync(urls["SHA256SUMS.txt"]);
        var sig = await http.GetByteArrayAsync(urls["SHA256SUMS.sig"]);
        if (tamper != null) sums = tamper(sums);
        if (!SignatureOk(sums, sig)) return $"⚠ Build {build} refused: the checksum file is not signed with Nova Enhance's key";
        var file = urls.Keys.First(n => n.EndsWith(Want));
        string expected = null;
        foreach (var line in Encoding.UTF8.GetString(sums).Split('\n'))
        {
            var parts = line.Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim() == file) expected = parts[0].ToLowerInvariant();
        }
        if (expected == null) return $"⚠ Build {build} refused: the file is not in the checksum list";
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder)) try { File.Delete(old); } catch { }
        var path = Path.Combine(folder, file);
        progress?.Invoke($"Downloading build {build}…");
        await using (var s = await http.GetStreamAsync(urls[file]))
        await using (var fs = File.Create(path)) await s.CopyToAsync(fs);
        string got;
        await using (var fs = File.OpenRead(path)) got = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
        if (got != expected) { File.Delete(path); return $"⚠ Build {build} refused: the download does not match its SHA-256 (deleted)"; }
        if (record)
        {
            var o = Load(); o["readyBuild"] = build; o["readyFile"] = path; o["readyNotes"] = (release["body"]?.ToString() ?? "").Trim(); Save(o);
        }
        return $"✓ Build {build} downloaded and verified (signature ✓ · SHA-256 ✓)";
    }

    static bool SignatureOk(byte[] data, byte[] sig)
    {
        try
        {
            if (string.IsNullOrEmpty(UpdateKey.Modulus)) return false;
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(UpdateKey.Modulus), Exponent = Convert.FromBase64String(UpdateKey.Exponent) });
            return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }

    /// <summary>Runs the verified installer silently (per user, no admin) and closes Nova; the installer opens it again.</summary>
    public static string Install()
    {
        var r = Waiting();
        if (r == null) return "Nothing to install";
        try
        {
            Guard.CleanExit();   // 🛡 an update is a normal exit: the Guardian must not restart the old version
            Process.Start(new ProcessStartInfo(r.File, "/VERYSILENT /SP- /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
        }
        catch (Exception e) { return "Could not start the update: " + e.Message; }
        MailIndex.Stop();
        if (Program.Tray != null) Program.Tray.Visible = false;
        Application.Exit();
        return null;
    }

    public static void Offer(bool always)
    {
        var r = Waiting();
        if (r == null) return;
        var o = Load();
        int offered = 0;
        try { offered = o["offered"]?.GetValue<int>() ?? 0; } catch { }
        if (!always && offered == r.Build) return;
        o["offered"] = r.Build; Save(o);
        var notes = r.Notes.Length > 600 ? r.Notes[..600] : r.Notes;
        var msg = $"Nova Enhance for Windows build {Program.Build} → build {r.Build}\n\n✓ Checksum file signed with Nova Enhance's own key\n✓ SHA-256 of the download matches\n\n" +
                  (notes.Length > 0 ? notes + "\n\n" : "") + "Nova closes, updates and opens again by itself. Install now?";
        if (MessageBox.Show(msg, "⬆ Update ready", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
        {
            var err = Install();
            if (err != null) MessageBox.Show(err, "Nova Enhance");
        }
    }

    /// <summary>Build test: verifies a published release end to end, and that a changed checksum file is refused.</summary>
    public static int SelfTest(string[] args)
    {
        var tag = args.SkipWhile(a => a != "--update-selftest").Skip(1).FirstOrDefault() ?? "";
        var outFile = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "nova-update-selftest.txt");
        var sb = new StringBuilder();
        int code = 0;
        try
        {
            using var http = Client();
            var r = JsonNode.Parse(http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/tags/{tag}").Result).AsObject();
            int.TryParse(tag.Replace(TagPrefix, ""), out var build);
            var tmp = Path.Combine(Path.GetTempPath(), "nova-selftest");
            var good = Fetch(http, r, build, null, tmp, false).Result;
            sb.AppendLine("genuine: " + good);
            if (!good.StartsWith("✓")) code = 1;
            var bad = Fetch(http, r, build, null, tmp, false, b => { var c = (byte[])b.Clone(); c[0] ^= 1; return c; }).Result;
            sb.AppendLine("tampered: " + bad);
            if (!bad.Contains("refused")) code = 1;
        }
        catch (Exception e) { sb.AppendLine("error: " + e); code = 1; }
        File.WriteAllText(outFile, sb.ToString());
        return code;
    }
}
