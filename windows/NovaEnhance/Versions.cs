using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>
/// 🕘 Versions of your drafts: before and after every Nova change in an Outlook email, the whole draft is kept with its formatting,
/// signature and the earlier emails below — one click puts any version back (Outlook's Ctrl+Z can't undo add-in changes).
/// Kept on this PC in Documents › Nova Enhance › Versions, the last 30 per draft. Designed by A.HAMROUNI
/// </summary>
static class Versions
{
    public class V { public long T; public string Label = "", Html = "", Text = ""; }
    public class Draft { public string Key = "", Subject = ""; public List<V> List = new(); }

    static readonly JsonSerializerOptions J = new() { IncludeFields = true };
    static readonly object L = new();

    public static string Dir { get { var d = Path.Combine(Paths.Root, "Versions"); Directory.CreateDirectory(d); return d; } }

    static string FileOf(string key) => Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key ?? "")))[..16] + ".json");

    static Draft Load(string key)
    {
        try { var f = FileOf(key); if (File.Exists(f)) return JsonSerializer.Deserialize<Draft>(File.ReadAllText(f), J) ?? new Draft { Key = key }; } catch { }
        return new Draft { Key = key };
    }

    /// <summary>Keeps a version (not twice the same text in a row). Returns how many versions the draft has.</summary>
    public static int Save(string key, string subject, string label, string html, string text)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(html)) return 0;
        lock (L)
        {
            var d = Load(key);
            d.Subject = subject ?? d.Subject;
            if (d.List.Count > 0 && d.List[^1].Html == html) { if (label.StartsWith("After")) d.List[^1].Label = label; }
            else d.List.Add(new V { T = DateTimeOffset.Now.ToUnixTimeMilliseconds(), Label = label, Html = html, Text = text ?? "" });
            while (d.List.Count > 30) d.List.RemoveAt(1);   // the very first (your original) always stays
            File.WriteAllText(FileOf(key), JsonSerializer.Serialize(d, J), new UTF8Encoding(false));
            return d.List.Count;
        }
    }

    public static JsonObject List(string key)
    {
        var d = Load(key);
        var a = new JsonArray();
        for (int i = d.List.Count - 1; i >= 0; i--)
        {
            var v = d.List[i];
            var prev = v.Text.Replace("\r", " ").Replace("\n", " ").Trim();
            a.Add(new JsonObject { ["i"] = i, ["when"] = DateTimeOffset.FromUnixTimeMilliseconds(v.T).LocalDateTime.ToString("ddd HH:mm"), ["label"] = v.Label, ["preview"] = prev.Length > 90 ? prev[..90] + "…" : prev });
        }
        // Other drafts too (if the subject changed since).
        var recent = new JsonArray();
        try
        {
            foreach (var f in new DirectoryInfo(Dir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTime).Take(8))
            {
                var o = JsonSerializer.Deserialize<Draft>(File.ReadAllText(f.FullName), J);
                if (o == null || o.Key == key) continue;
                recent.Add(new JsonObject { ["key"] = o.Key, ["subject"] = o.Subject, ["count"] = o.List.Count, ["when"] = f.LastWriteTime.ToString("ddd HH:mm") });
            }
        }
        catch { }
        return new JsonObject { ["versions"] = a, ["recent"] = recent };
    }

    public static string Get(string key, int i)
    {
        var d = Load(key);
        return i >= 0 && i < d.List.Count ? d.List[i].Html : "";
    }
}
