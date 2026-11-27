using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🔢 Every Nova feature has a number that never moves: group.page (4.3 = group 4 Projects &amp; documents, page 3 Find a file).
/// The same number is shown in Settings, the tray menu, window titles, the error log and reports to Claude, so “4.3” always
/// means the same thing in your notes and in our chats. A new feature takes the next free number; a removed one is not reused.
/// Designed by A.HAMROUNI
/// </summary>
static class Features
{
    /// <summary>One feature: its number, its name, the settings page that holds its options (the page's key, "" = none),
    /// and words that point to it (for the settings search and for tagging log lines).</summary>
    public record F(string Num, string Name, string Page, string Words, bool Advanced = false);

    public static readonly (string Num, string Name)[] Groups =
    {
        ("0", "🌐 Everywhere"), ("1", "🧠 Brain & AI"), ("2", "🗄 Indexing"), ("3", "📧 Email"), ("4", "📁 Projects & documents"),
        ("5", "✍ Writing"), ("6", "📱 Phone"), ("7", "🛠 Nova Studio"), ("8", "🛡 Safety & upkeep"),
    };

    public static readonly F[] All =
    {
        new("0.1", "📌 My Work", "", "my work|task|work item"),
        new("0.2", "🕘 History", "", "history|past chats|past searches"),
        new("1.0", "💬 Nova Chat & AI Hub", "", "nova chat|ai hub|hub|chat"),
        new("1.1", "🧠 Nova Brain", "🧠 Nova Brain", "brain|rule|trainer|health check"),
        new("1.2", "🧠 What Nova knows", "🧠 What Nova knows", "knows|memory|remember|good answer"),
        new("1.3", "🔑 AI & API keys", "AI & API keys", "api key|gemini|chatgpt|openai|claude|deepseek|grok|quota|key"),
        new("1.4", "⚙ AI for each feature", "⚙ AI for each feature", "ai for each|per feature|model"),
        new("1.5", "✦ Prompts", "✦ Prompts", "prompt|favourite question"),
        new("1.6", "👤 About me", "About me", "about me|profile|my field|role"),
        new("2.1", "💻 Local data", "💻 Local data", "local|f:\\\\|local folder|local copy"),
        new("2.2", "🖧 Network server", "🖧 Network server", "network|server|s:\\\\|official|mza dc|listing"),
        new("2.3", "🌐 Office VPN", "🌐 Office VPN", "vpn|forti|openconnect|office network"),
        new("2.4", "📶 Progress & Nova index", "📶 Indexing progress", "progress|nova index|read inside|ocr|database|index"),
        new("2.5", "📦 Transfer engine", "📦 Transfer engine", "transfer|copy engine|offline copy", true),
        new("3.1", "📧 Email & AI", "Email & AI", "email & ai|what is sent|mail ai"),
        new("3.2", "📥 Email index", "📥 Email index", "email index|sync|repair|outlook index"),
        new("3.3", "🗂 Mailboxes & archives", "🗂 Mailboxes & archives", "mailbox|archive|pst|ost"),
        new("3.4", "📂 Folders in the index", "📂 Folders in the index", "outlook folder|folders in the index"),
        new("3.5", "📁 Email projects", "📁 Email projects", "email project|project code|sorting"),
        new("3.6", "🔍 Search for email", "", "search for email|find email|ask nova"),
        new("3.7", "💬 Talk about this email", "", "talk about this email|mail ask"),
        new("3.8", "☀ Morning brief", "", "morning brief|brief"),
        new("4.1", "📁 Projects & sources", "📁 Projects", "projects|sources|add a folder|project folder|folder search"),
        new("4.2", "📜 Contract area", "📜 Contract area", "contract|register|stamp|precedence|vendor"),
        new("4.3", "🎯 Find a file", "🎯 Guided mode", "find a file|guided|quick find|file finder|drawing register"),
        new("4.4", "📋 Submittal log", "📋 Submittal log", "submittal|wir|mir|mar|rfi|log"),
        new("4.5", "📄 Nova for this PDF", "📄 Nova for this PDF", "pdf|this pdf|scan|ocr this pdf"),
        new("4.6", "📁 Search inside a folder", "", "search inside a folder|folder search"),
        new("5.1", "✦ Enhancement", "Enhancement", "enhance|enhancement|mode|rewrite"),
        new("5.2", "🔤 Spelling & grammar", "Spelling & grammar", "spelling|grammar|spell"),
        new("5.3", "🪟 AI window", "AI window", "ai window"),
        new("5.4", "📎 Office tab", "Office tab", "office tab|word|excel|outlook add-in|ribbon"),
        new("5.5", "🌐 Browser add-on", "Browser add-on", "browser|chrome|edge add-on"),
        new("5.6", "⌨ Shortcuts", "Shortcuts", "shortcut|hotkey|ctrl+"),
        new("6.1", "📱 Phone link", "📱 Phone link", "phone|android|qr|wi-fi link"),
        new("6.2", "☁ Google Drive & phone sync", "Google Drive", "google drive|drive|phone sync|cloud"),
        new("7.1", "🛠 Nova Studio", "🛠 Nova Studio", "studio|coding|github|budget|develop"),
        new("7.2", "🔌 Test connector", "🔌 Test connector", "test connector|relay|claude access", true),
        new("7.3", "🧪 Activity history", "🧪 Activity history", "activity", true),
        new("8.1", "🔐 Admin", "🔐 Admin", "admin|pin|lock"),
        new("8.2", "💾 Backup & restore", "Backup & restore", "backup|restore"),
        new("8.3", "⬆ Updates", "Updates", "update|version|build|roll back"),
        new("8.4", "🩺 Error log", "Error log", "error|log|freeze|crash"),
        new("8.5", "ℹ About", "About", "about|feature map|advanced tools"),
    };

    /// <summary>Old page names that now live inside another page (links from everywhere keep working).</summary>
    public static readonly Dictionary<string, string> Joined = new()
    {
        ["🗄 Nova index"] = "📶 Indexing progress",
        ["🗂 Project sources"] = "📁 Projects",
        ["Folder search"] = "📁 Projects",
    };

    public static F ByPage(string page) => All.FirstOrDefault(f => f.Page == page) ?? (Joined.TryGetValue(page ?? "", out var j) ? All.FirstOrDefault(f => f.Page == j) : null);
    public static F ByNum(string num) => All.FirstOrDefault(f => f.Num == num);
    public static string Title(string num) => ByNum(num) is { } f ? $"{f.Num} {f.Name}" : num;

    /// <summary>🔎 The settings search: a number (“4.3”, “4”) or words (“vpn”, “budget”, “backup”).</summary>
    public static List<F> Find(string q)
    {
        q = (q ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0) return All.ToList();
        if (Regex.IsMatch(q, @"^\d+(\.\d*)?$")) return All.Where(f => f.Num == q || f.Num.StartsWith(q.TrimEnd('.') + (q.Contains('.') ? "" : "."))).ToList();
        return All.Where(f => f.Name.ToLowerInvariant().Contains(q) || f.Words.Split('|').Any(w => w.Contains(q) || q.Contains(w))).ToList();
    }

    /// <summary>A log area with its feature number in front (“4.5 PDF”), so the error log and reports point to the feature.</summary>
    public static string Tag(string area)
    {
        if (string.IsNullOrEmpty(area) || Regex.IsMatch(area, @"^\d\.\d ")) return area;
        var a = area.ToLowerInvariant();
        string n =
            Regex.IsMatch(a, "submittal") ? "4.4" : Regex.IsMatch(a, @"\bpdf\b|ocr") ? "4.5" : Regex.IsMatch(a, "guided|find a file|finder") ? "4.3" :
            Regex.IsMatch(a, "contract|register") ? "4.2" : Regex.IsMatch(a, "project|listing|read inside|folder index") ? "4.1" :
            Regex.IsMatch(a, "brain|trainer") ? "1.1" : Regex.IsMatch(a, "nova chat|hub") ? "1.0" : Regex.IsMatch(a, "vpn") ? "2.3" :
            Regex.IsMatch(a, "sync|mail index|repair|outlook") ? "3.2" : Regex.IsMatch(a, "brief") ? "3.8" : Regex.IsMatch(a, "phone|drive") ? "6.1" :
            Regex.IsMatch(a, "studio") ? "7.1" : Regex.IsMatch(a, "backup") ? "8.2" : Regex.IsMatch(a, "update") ? "8.3" :
            Regex.IsMatch(a, "settings") ? "8" : Regex.IsMatch(a, "enhance|spell") ? "5.1" : "";
        return n.Length > 0 ? n + " " + area : area;
    }

    /// <summary>A window title with its feature number in front (“4.5 · Nova Enhance · this PDF”).</summary>
    public static string Titled(string t)
    {
        if (string.IsNullOrEmpty(t) || Regex.IsMatch(t, @"^\d(\.\d)? ")) return t;
        var n = Regex.IsMatch(t, "Nova Chat|AI Hub") ? "1.0" : Regex.IsMatch(t, @"this PDF|\bPDF\b") ? "4.5" : Regex.IsMatch(t, "Submittal log") ? "4.4"
            : Regex.IsMatch(t, "Find a file|Guided") ? "4.3" : Regex.IsMatch(t, "History") ? "0.2" : Regex.IsMatch(t, "inside a folder|Folder search") ? "4.6"
            : Regex.IsMatch(t, "Search for email|Ask Nova") ? "3.6" : Regex.IsMatch(t, "Talk about") ? "3.7" : Regex.IsMatch(t, "Morning brief") ? "3.8"
            : Regex.IsMatch(t, "My Work") ? "0.1" : Regex.IsMatch(t, "Nova Brain") ? "1.1" : Regex.IsMatch(t, "Studio") ? "7.1"
            : Regex.IsMatch(t, "Contract") ? "4.2" : Regex.IsMatch(t, @"\bEnhance\b(?! ·)") ? "5.1" : "";
        return n.Length > 0 ? n + " · " + t : t;
    }

    /// <summary>📋 The feature map (8.5 About): every number, its feature, and where its settings are.</summary>
    public static string Map() => string.Join("\n", Groups.Select(g => $"{g.Num} · {g.Name}\n" + string.Join("\n",
        All.Where(f => f.Num.StartsWith(g.Num + ".")).Select(f => $"    {f.Num}  {f.Name}" + (f.Page.Length > 0 ? "" : "   (a window — no settings page of its own)") + (f.Advanced ? "   · advanced" : "")))));
}

/// <summary>📋 A submittal record read from its folder on the server: the package the contractor sent (PDF + the receipt .msg) and the
/// “Response” sub-folder with MZA's answer (the PDF with “Code N” in its name + the .msg sent back). Designed by A.HAMROUNI</summary>
static class SubmittalRecord
{
    public static string From(string query, IEnumerable<(string Path, DateTime Modified)> files)
    {
        var no = Regex.Match(query ?? "", @"[A-Z]{2,6}-[A-Z]{2,6}-[A-Z]{2,4}-[A-Z]{2,4}-\d{3,6}|(?<![A-Z])(MAR|MIR|WIR|SSM|SDS|RFI|PQS|SHD|MS)-?\d{3,6}", RegexOptions.IgnoreCase).Value;
        if (no.Length == 0) return "";
        var mine = files.Where(f => f.Path.Contains(no, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0) return "";
        bool Resp(string p) => p.Contains("\\Response\\", StringComparison.OrdinalIgnoreCase) || p.Contains("\\Reply\\", StringComparison.OrdinalIgnoreCase);
        var got = mine.Where(f => !Resp(f.Path)).ToList();
        var resp = mine.Where(f => Resp(f.Path)).ToList();
        var code = resp.Select(f => Regex.Match(Path.GetFileName(f.Path), @"Code\s*[-_ ]?\s*(\d)", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success)?.Groups[1].Value ?? "";
        var s = $"📋 {no.ToUpperInvariant()}: 📥 received {(got.Count > 0 ? got.Min(f => f.Modified).ToString("dd MMM") : "—")} ({got.Count} file(s))";
        s += resp.Count > 0 ? $" · 📤 response {resp.Max(f => f.Modified):dd MMM}" + (code.Length > 0 ? $" · Code {code}" : "") : " · 📤 no response folder yet";
        return s;
    }
}
