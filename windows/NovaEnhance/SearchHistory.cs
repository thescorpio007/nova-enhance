using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🕘 Search history — saved by itself: every search (🔎 deep, ✦ Nova Chat, 📂 folder, ✉ email) with its date, subject, answer and sources.
/// A deep search is saved after every step, so if Nova closes nothing is lost: it shows as "interrupted" with ▶ Continue.
/// Kept 60 days by default (90 · 180 · always). Stays on this PC (Documents\Nova Enhance\Search History). Designed by A.HAMROUNI
/// </summary>
static class SearchHistory
{
    /// <summary>This run of Nova: a search still "running" from another run was cut off.</summary>
    static readonly string Run = DateTime.Now.Ticks.ToString();
    static readonly object L = new();

    public static string Dir { get { var p = Path.Combine(Paths.Root, "Search History"); Directory.CreateDirectory(p); return p; } }
    public static int Days { get => Store.Int("win_hist_days", 60); set { Store.SetInt("win_hist_days", value); Prune(); } }

    /// <summary>Something was saved (the 🕘 button updates its count).</summary>
    public static event Action Changed;

    public static string NewId() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Random.Shared.Next(100, 999);
    static string FileOf(string id) => Path.Combine(Dir, Regex.Replace(id ?? "", @"[^\w\-]", "") + ".json");

    /// <summary>Creates or updates one search (the fields given replace the old ones).</summary>
    public static void Save(string id, JsonObject fields)
    {
        try
        {
            lock (L)
            {
                var o = Get(id) ?? new JsonObject { ["id"] = id, ["ts"] = DateTimeOffset.Now.ToUnixTimeMilliseconds() };
                foreach (var kv in fields.ToList()) o[kv.Key] = kv.Value?.DeepClone();
                o["run"] = Run;
                o["updated"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                var f = FileOf(id);
                File.WriteAllText(f + ".tmp", o.ToJsonString(), new UTF8Encoding(false));   // a cut-off save never leaves half a file
                File.Move(f + ".tmp", f, true);
            }
            Changed?.Invoke();
        }
        catch (Exception e) { Log.Warn("Search history", e.Message); }
    }

    /// <summary>A finished search in one line: kind = deep · chat · folder · email.</summary>
    public static string Add(string kind, string subject, string answer, JsonArray sources, string note = "", JsonObject extra = null)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var id = NewId();
        var o = new JsonObject { ["kind"] = kind, ["subject"] = subject.Trim(), ["status"] = "done", ["answer"] = answer ?? "", ["sources"] = sources ?? new JsonArray(), ["note"] = note ?? "" };
        if (extra != null) foreach (var kv in extra.ToList()) o[kv.Key] = kv.Value?.DeepClone();
        Save(id, o);
        return id;
    }

    public static JsonObject Get(string id)
    {
        try { var f = FileOf(id); return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f))?.AsObject() : null; } catch { return null; }
    }

    public static string Status(JsonObject o)
    {
        var s = o?["status"]?.ToString() ?? "";
        return s == "running" && o["run"]?.ToString() != Run ? "interrupted" : s;
    }

    public static void Delete(string id) { try { File.Delete(FileOf(id)); } catch { } Changed?.Invoke(); }
    public static void Clear() { try { foreach (var f in Directory.GetFiles(Dir, "*.json")) File.Delete(f); } catch { } Changed?.Invoke(); }

    /// <summary>Removes what is older than the days kept (never when "always" = 0).</summary>
    public static void Prune()
    {
        var d = Days; if (d <= 0) return;
        var cut = DateTime.Now.AddDays(-d);
        try { foreach (var f in Directory.GetFiles(Dir, "*.json")) if (File.GetLastWriteTime(f) < cut) File.Delete(f); } catch { }
    }

    public static int Count() { try { return Directory.GetFiles(Dir, "*.json").Length; } catch { return 0; } }

    /// <summary>All searches, newest first, without their answers and sources (the list).</summary>
    public static JsonArray List()
    {
        Prune();
        var all = new List<JsonObject>();
        foreach (var f in Directory.GetFiles(Dir, "*.json"))
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(f))?.AsObject();
                if (o == null) continue;
                var ans = o["answer"]?.ToString() ?? "";
                all.Add(new JsonObject
                {
                    ["id"] = o["id"]?.ToString(), ["ts"] = o["ts"]?.GetValue<long>() ?? 0, ["kind"] = o["kind"]?.ToString(), ["subject"] = o["subject"]?.ToString(),
                    ["status"] = Status(o), ["note"] = o["note"]?.ToString() ?? "", ["n"] = (o["sources"] as JsonArray)?.Count ?? 0, ["chat"] = o["chatId"]?.ToString() ?? "",
                    ["find"] = (o["subject"] + " " + (ans.Length > 4000 ? ans[..4000] : ans) + " " + o["note"]).ToLowerInvariant()
                });
            }
            catch { }
        return new JsonArray(all.OrderByDescending(x => x["ts"]!.GetValue<long>()).Select(x => (JsonNode)x).ToArray());
    }
}
