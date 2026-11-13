using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>One recorded error or warning.</summary>
class LogEntry
{
    public long Ts { get; set; }
    public string Level { get; set; } = "error";   // error · warn
    public string Area { get; set; } = "";
    public string Message { get; set; } = "";
    public string Step { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Sample { get; set; } = "";
    public DateTime Time => DateTimeOffset.FromUnixTimeMilliseconds(Ts).LocalDateTime;
}

/// <summary>
/// Error log (Settings → Error log): every error with where it happened, the step the user was doing and the technical trace.
/// Kept in Documents\Nova Enhance\Logs\errors.jsonl for 30 days (choice in Settings). Never stores keys, passwords or content
/// (a 100-character sample only when the user turns it on).
/// </summary>
static class Log
{
    static readonly object L = new();
    static readonly AsyncLocal<string> step = new();
    static string FilePath => Path.Combine(Paths.Logs, "errors.jsonl");

    public static bool Detailed { get => Store.Bool("win_log_detail", true); set => Store.SetBool("win_log_detail", value); }
    public static bool Warnings { get => Store.Bool("win_log_warn", true); set => Store.SetBool("win_log_warn", value); }
    public static bool Samples { get => Store.Bool("win_log_sample", false); set => Store.SetBool("win_log_sample", value); }
    public static int KeepDays { get => Math.Max(1, Store.Int("win_log_days", 30)); set => Store.SetInt("win_log_days", value); }

    sealed class StepScope : IDisposable
    {
        readonly string prev;
        public StepScope(string s) { prev = step.Value; step.Value = s; }
        public void Dispose() => step.Value = prev;
    }

    /// <summary>What the user is doing now (recorded with any error): using (Log.Doing("Search …")) { … }</summary>
    public static IDisposable Doing(string what) => new StepScope(what);
    public static string CurrentStep => step.Value ?? "";

    public static void Error(string area, Exception e, string sample = null) =>
        Add(new LogEntry { Level = "error", Area = area, Message = $"{e.GetType().Name}: {e.Message}", Detail = Detailed ? e.ToString() : "", Sample = sample });

    public static void Warn(string area, string message, string sample = null)
    {
        if (!Warnings) return;
        Add(new LogEntry { Level = "warn", Area = area, Message = message, Sample = sample });
    }

    /// <summary>Older calls: a line of text is a warning.</summary>
    public static void Write(string s) => Warn("Nova", s);

    // The same warning again and again (e.g. one bad file at every pass) is written once an hour, with how many times it happened.
    static readonly Dictionary<string, (DateTime At, int Skipped)> recent = new();

    static void Add(LogEntry e)
    {
        try
        {
            var key = e.Level + "|" + e.Area + "|" + (e.Message.Length > 160 ? e.Message[..160] : e.Message);
            lock (recent)
            {
                var window = e.Level == "warn" ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(10);
                if (recent.TryGetValue(key, out var r) && DateTime.Now - r.At < window) { recent[key] = (r.At, r.Skipped + 1); return; }
                if (recent.TryGetValue(key, out var r2) && r2.Skipped > 0) e.Message += $"   (×{r2.Skipped + 1} since {r2.At:HH:mm})";
                recent[key] = (DateTime.Now, 0);
                if (recent.Count > 2000) recent.Clear();
            }
            e.Ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            e.Step = CurrentStep;
            if (!Samples || string.IsNullOrEmpty(e.Sample)) e.Sample = "";
            else if (e.Sample.Length > 100) e.Sample = e.Sample[..100];
            var line = JsonSerializer.Serialize(e);
            lock (L)
            {
                File.AppendAllText(FilePath, line + "\n");
                var fi = new FileInfo(FilePath);
                if (fi.Length > 4_000_000) Trim();
            }
        }
        catch { }
    }

    static void Trim()
    {
        var cut = DateTimeOffset.Now.AddDays(-KeepDays).ToUnixTimeMilliseconds();
        var keep = ReadAll().Where(x => x.Ts >= cut).TakeLast(5000).Select(x => JsonSerializer.Serialize(x));
        File.WriteAllText(FilePath, string.Join("\n", keep) + "\n");
    }

    public static List<LogEntry> ReadAll()
    {
        var list = new List<LogEntry>();
        try
        {
            string[] lines;
            lock (L) lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath) : Array.Empty<string>();
            var cut = DateTimeOffset.Now.AddDays(-KeepDays).ToUnixTimeMilliseconds();
            foreach (var l in lines)
            {
                if (l.Trim().Length == 0) continue;
                try { var e = JsonSerializer.Deserialize<LogEntry>(l); if (e != null && e.Ts >= cut) list.Add(e); } catch { }
            }
            // Old text log from earlier builds
            var old = Path.Combine(Paths.Logs, "errors.log");
            if (File.Exists(old))
                foreach (var l in File.ReadAllLines(old).TakeLast(300))
                    if (l.Length > 21 && DateTime.TryParse(l[..19], out var t) && t > DateTime.Now.AddDays(-KeepDays))
                        list.Add(new LogEntry { Ts = new DateTimeOffset(t).ToUnixTimeMilliseconds(), Level = l.Contains("Exception") || l.Contains("Error") ? "error" : "warn", Area = "earlier build", Message = l[19..].Trim() });
        }
        catch { }
        return list.OrderByDescending(x => x.Ts).ToList();
    }

    public static string Read() => string.Join("\r\n", ReadAll().Select(Format));

    public static string Format(LogEntry e) =>
        $"{e.Time:yyyy-MM-dd HH:mm:ss}  {e.Level.ToUpperInvariant()}  {e.Area}: {e.Message}" +
        (e.Step.Length > 0 ? $"\r\n    Step: {e.Step}" : "") +
        (e.Sample.Length > 0 ? $"\r\n    Sample: {e.Sample}" : "") +
        (e.Detail.Length > 0 ? "\r\n    " + e.Detail.Replace("\n", "\n    ") : "");

    public static void Clear()
    {
        try
        {
            lock (L)
            {
                File.WriteAllText(FilePath, "");
                var old = Path.Combine(Paths.Logs, "errors.log");
                if (File.Exists(old)) File.Delete(old);
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ environment and report

    public static string Environment()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Nova Enhance for Windows {Program.Shown} (internal {Program.Build})");
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName")?.ToString() ?? "Windows";
            var build = k?.GetValue("CurrentBuild")?.ToString() ?? "";
            if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            sb.AppendLine($"{name} {k?.GetValue("DisplayVersion")} (build {build}) · {(System.Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
        }
        catch { sb.AppendLine(System.Environment.OSVersion.ToString()); }
        sb.AppendLine($".NET {System.Environment.Version} · {System.Globalization.CultureInfo.CurrentCulture.Name} · screen scale {(int)(Screen.PrimaryScreen?.Bounds.Width ?? 0)}px");
        sb.AppendLine("Office: " + OfficeVersion());
        sb.AppendLine("Classic Outlook: " + (Outlook.Installed ? "installed" : "not found") + " · OCR languages: " + Ocr.Languages);
        sb.AppendLine("Google Drive: " + (GDrive.SignedIn ? "connected" : "not connected") + " · AI: " + (Ai.LabelOf(Ai.Current()) ?? "no key") + " · Office add-in: " + OfficeAddin.StatusShort());
        return sb.ToString();
    }

    public static string OfficeVersion()
    {
        try
        {
            using var c2r = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
            var v = c2r?.GetValue("VersionToReport")?.ToString();
            if (!string.IsNullOrEmpty(v)) return $"Microsoft 365 / Click-to-Run {v} ({c2r?.GetValue("Platform")})";
            foreach (var ver in new[] { "16.0", "15.0" })
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Office\{ver}\Common\InstallRoot");
                if (k?.GetValue("Path") != null) return "Office " + ver;
            }
        }
        catch { }
        return Type.GetTypeFromProgID("Word.Application") != null ? "Office (version unknown)" : "not found";
    }

    static readonly string[] SecretWords = { "key_", "_pw", "secret", "refresh", "password", "p_about", "p_terms", "win_gd_email", "cert" };

    /// <summary>One zip to share: errors (readable and raw), Nova build, Windows/Office/Outlook versions and settings without any secret or personal text.</summary>
    public static string CreateReport()
    {
        var path = Path.Combine(Paths.Logs, $"Nova-Error-Report-{DateTime.Now:yyyy-MM-dd-HHmm}.zip");
        var entries = ReadAll();
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                var e = z.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            Add("README.txt", $"Nova Enhance error report · {DateTime.Now:dd MMM yyyy HH:mm}\r\n{Program.Sign}\r\n\r\nThis report holds no API keys, passwords, emails or file contents.\r\n");
            Add("system.txt", Environment());
            Add("errors.txt", $"{entries.Count(x => x.Level == "error")} errors · {entries.Count(x => x.Level == "warn")} warnings · last {KeepDays} days\r\n\r\n" + string.Join("\r\n\r\n", entries.Select(Format)));
            Add("errors.jsonl", string.Join("\n", entries.Select(x => JsonSerializer.Serialize(x))));
            var settings = new StringBuilder();
            foreach (var (k, v) in Store.Snapshot().OrderBy(x => x.Key))
            {
                if (SecretWords.Any(w => k.Contains(w, StringComparison.OrdinalIgnoreCase))) { settings.AppendLine($"{k} = (hidden)"); continue; }
                var s = v?.ToJsonString() ?? "";
                if (s.StartsWith("\"dpapi:")) s = "(hidden)";
                settings.AppendLine($"{k} = {(s.Length > 120 ? s[..120] + "…" : s)}");
            }
            Add("settings.txt", settings.ToString());
            var smoke = Path.Combine(Paths.Logs, "smoke.txt");
            if (File.Exists(smoke)) Add("last-test.txt", File.ReadAllText(smoke));
        }
        return path;
    }

    /// <summary>Opens a new Outlook email with the report attached (the user sends it).</summary>
    public static bool EmailReport(string zip)
    {
        try
        {
            dynamic app = Outlook.Get();
            dynamic mail = app.CreateItem(0);
            mail.Subject = $"Nova Enhance error report · {Program.Shown} · {DateTime.Now:dd MMM yyyy}";
            mail.Body = "Hello,\r\n\r\nPlease find the Nova Enhance error report attached.\r\n\r\n" + Program.Sign;
            mail.Attachments.Add(zip);
            mail.Display(false);
            return true;
        }
        catch (Exception e) { Error("Error log · email", e); return false; }
    }
}
