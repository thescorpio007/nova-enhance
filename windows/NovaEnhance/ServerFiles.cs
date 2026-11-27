using System.IO.Compression;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>
/// 🏢 Server files for the phone, fetched by this PC (it is already signed in to S:, O: and N:): a file or a whole folder as one zip,
/// READ-ONLY — files are only opened for reading, nothing on the server is ever written, renamed, moved or deleted.
/// The result goes to the phone encrypted (the phone's key) through Google Drive, or straight in the answer when small.
/// Designed by A.HAMROUNI
/// </summary>
static class ServerFiles
{
    public const long MaxBytes = 150L * 1024 * 1024;   // what a phone can safely take in one piece
    static string FetchDir => Directory.CreateDirectory(Path.Combine(PhoneLink.Dir, "fetched")).FullName;

    /// <summary>The office server must answer: the phone gave the VPN back → reconnect now and wait (up to 2 min).</summary>
    public static async Task Ready(string path)
    {
        if (Projects.ReadAll.RootOnline(path)) return;
        if (OfficeVpn.HoldUntil > DateTime.Now) OfficeVpn.HoldUntil = DateTime.Now;
        if (OfficeVpn.On) OfficeVpn.Connect();
        var t0 = DateTime.Now;
        while (DateTime.Now - t0 < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(5000);
            if (Directory.Exists(Path.GetPathRoot(path) ?? path)) return;
        }
        throw new Exception("the office server is not reachable from your PC (VPN not connected?)");
    }

    static bool Hidden(string p) => Projects.Private.IsMatch(p ?? "");

    static IEnumerable<FileInfo> Files(string folder) =>
        new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
            .Where(f => !Hidden(f.FullName));

    public static JsonObject Info(string path)
    {
        if (Directory.Exists(path))
        {
            long n = 0, b = 0;
            foreach (var f in Files(path)) { n++; b += f.Length; }
            return new JsonObject { ["dir"] = true, ["files"] = n, ["bytes"] = b };
        }
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new Exception("not found on the server");
        return new JsonObject { ["dir"] = false, ["files"] = 1, ["bytes"] = fi.Length };
    }

    /// <summary>The file (read-only), or the folder packed into one zip in memory (🔒 private files left out).</summary>
    public static byte[] Bytes(string path, bool zip)
    {
        if (!zip)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new Exception("not found on the server");
            if (fi.Length > MaxBytes) throw new Exception($"file larger than {MaxBytes / 1048576} MB — open it on the PC");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream((int)fi.Length);
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        if (!Directory.Exists(path)) throw new Exception("folder not found on the server");
        var root = path.TrimEnd('\\');
        var top = Path.GetFileName(root);
        long total = 0;
        using var outMs = new MemoryStream();
        using (var z = new ZipArchive(outMs, ZipArchiveMode.Create, true))
            foreach (var f in Files(root))
            {
                total += f.Length;
                if (total > MaxBytes) throw new Exception($"folder larger than {MaxBytes / 1048576} MB — zip a smaller sub-folder");
                var e = z.CreateEntry(top + "/" + Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), CompressionLevel.Fastest);
                using var es = e.Open();
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.CopyTo(es);
            }
        return outMs.ToArray();
    }

    /// <summary>Works in the background: fetched/ID.bin (sealed for the phone) or fetched/ID.err with the reason. Google Drive uploads it.</summary>
    public static async Task ToDrive(string path, bool zip, string id)
    {
        var dir = FetchDir;
        using var act = Activity.Begin("📱 phone link", $"🏢 server {(zip ? "folder (zip)" : "file")} for the phone: {Path.GetFileName(path.TrimEnd('\\'))}");
        try
        {
            foreach (var old in Directory.GetFiles(dir)) try { if (File.GetLastWriteTime(old) < DateTime.Now.AddHours(-6)) File.Delete(old); } catch { }
            await Ready(path);
            var data = Bytes(path, zip);
            var tmp = Path.Combine(dir, id + ".part");
            await File.WriteAllBytesAsync(tmp, PhoneLink.Seal(data));
            File.Move(tmp, Path.Combine(dir, id + ".bin"), true);
        }
        catch (Exception e)
        {
            try { await File.WriteAllBytesAsync(Path.Combine(dir, id + ".err"), PhoneLink.Seal(System.Text.Encoding.UTF8.GetBytes(e.Message))); } catch { }
            Log.Warn("phone · server file", Path.GetFileName(path.TrimEnd('\\')) + ": " + e.Message);
        }
    }

    /// <summary>A live look at a server folder (read-only list, 🔒 private names left out).</summary>
    public static JsonArray List(string folder)
    {
        var d = new DirectoryInfo(folder);
        if (!d.Exists) throw new Exception("folder not found on the server");
        var arr = new JsonArray();
        foreach (var e in d.EnumerateFileSystemInfos().Where(x => !Hidden(x.Name)).OrderBy(x => x is FileInfo).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(3000))
            arr.Add(new JsonObject { ["n"] = e.Name, ["d"] = e is DirectoryInfo, ["b"] = e is FileInfo f ? f.Length : 0, ["m"] = new DateTimeOffset(e.LastWriteTime).ToUnixTimeMilliseconds() });
        return arr;
    }
}
