using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>
/// The Nova Enhance add-on for Firefox Developer Edition, Chrome and Edge. Nova writes it to Documents\Nova Enhance\Browser Add-on:
///   · "Chrome and Edge" — a folder loaded once with "Load unpacked" (developer mode); it reloads itself when Nova updates the folder.
///   · NovaEnhance-Firefox.xpi — installed once in Firefox Developer Edition (unsigned add-ons allowed); it updates from Nova on this PC.
/// The add-on talks only to Nova on this PC (http://127.0.0.1:47321) with a key that only this add-on knows. Designed by A.HAMROUNI
/// </summary>
static class BrowserAddon
{
    public const int Port = 47321;
    const string GeckoId = "nova-enhance@hamrouni.local";

    public static bool On { get => Store.Bool("win_browser", false); set => Store.SetBool("win_browser", value); }
    public static string Folder { get { var p = Path.Combine(Paths.Root, "Browser Add-on"); Directory.CreateDirectory(p); return p; } }
    public static string ChromeFolder => Path.Combine(Folder, "Chrome and Edge");
    public static string Xpi => Path.Combine(Folder, "NovaEnhance-Firefox.xpi");
    public static string Version => $"1.0.{Math.Max(1, Program.Build)}";

    public static string Key
    {
        get
        {
            var k = Store.Str("win_browser_key");
            if (k.Length < 20) { k = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(); Store.Set("win_browser_key", k); }
            return k;
        }
    }

    static readonly string[] Files = { "background.js", "content.js", "panel.html", "panel.js", "panel.css", "icon-16.png", "icon-32.png", "icon-64.png", "icon-128.png" };

    static byte[] Resource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Browser." + name, StringComparison.OrdinalIgnoreCase));
        if (res == null) return null;
        using var s = asm.GetManifestResourceStream(res);
        using var ms = new MemoryStream();
        s!.CopyTo(ms);
        return ms.ToArray();
    }

    static string Config() => $"/* Written by Nova Enhance on this PC — do not share. */\nvar NOVA = {{ port: {Port}, key: \"{Key}\" }};\n";

    static JsonObject Commands() => new()
    {
        ["fix-grammar"] = new JsonObject { ["suggested_key"] = new JsonObject { ["default"] = "Alt+G" }, ["description"] = "Fix grammar of the selected text" },
        ["translate"] = new JsonObject { ["suggested_key"] = new JsonObject { ["default"] = "Alt+T" }, ["description"] = "Translate the selected text (last language)" }
    };

    static JsonObject Icons() => new() { ["16"] = "icon-16.png", ["32"] = "icon-32.png", ["64"] = "icon-64.png", ["128"] = "icon-128.png" };

    static JsonArray Content() => new()
    {
        new JsonObject { ["matches"] = new JsonArray("<all_urls>"), ["js"] = new JsonArray("content.js"), ["all_frames"] = true, ["run_at"] = "document_idle" }
    };

    public static string ChromeManifest() => new JsonObject
    {
        ["manifest_version"] = 3,
        ["name"] = "Nova Enhance",
        ["version"] = Version,
        ["description"] = "Fix grammar, rewrite and translate selected text, and summarise or ask about the page — with the Nova Enhance app on this PC. Designed by A.HAMROUNI",
        ["author"] = "A.HAMROUNI",
        ["icons"] = Icons(),
        ["permissions"] = new JsonArray("contextMenus", "storage", "tabs", "sidePanel", "alarms"),
        ["host_permissions"] = new JsonArray($"http://127.0.0.1:{Port}/*", "<all_urls>"),
        ["background"] = new JsonObject { ["service_worker"] = "background.js" },
        ["action"] = new JsonObject { ["default_title"] = "Nova Enhance", ["default_icon"] = Icons() },
        ["side_panel"] = new JsonObject { ["default_path"] = "panel.html" },
        ["content_scripts"] = Content(),
        ["commands"] = Commands()
    }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static string FirefoxManifest() => new JsonObject
    {
        ["manifest_version"] = 2,
        ["name"] = "Nova Enhance",
        ["version"] = Version,
        ["description"] = "Fix grammar, rewrite and translate selected text, and summarise or ask about the page — with the Nova Enhance app on this PC. Designed by A.HAMROUNI",
        ["author"] = "A.HAMROUNI",
        ["icons"] = Icons(),
        ["permissions"] = new JsonArray("contextMenus", "storage", "tabs", "alarms", $"http://127.0.0.1:{Port}/*", "<all_urls>"),
        ["background"] = new JsonObject { ["scripts"] = new JsonArray("config.js", "background.js") },
        ["browser_action"] = new JsonObject { ["default_title"] = "Nova Enhance", ["default_icon"] = Icons() },
        ["sidebar_action"] = new JsonObject { ["default_title"] = "Nova Enhance", ["default_panel"] = "panel.html", ["default_icon"] = Icons(), ["open_at_install"] = false },
        ["content_scripts"] = Content(),
        ["commands"] = Commands(),
        ["browser_specific_settings"] = new JsonObject
        {
            ["gecko"] = new JsonObject { ["id"] = GeckoId, ["strict_min_version"] = "115.0", ["update_url"] = $"http://127.0.0.1:{Port}/firefox/updates.json" }
        }
    }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static string UpdatesJson() => new JsonObject
    {
        ["addons"] = new JsonObject
        {
            [GeckoId] = new JsonObject
            {
                ["updates"] = new JsonArray(new JsonObject { ["version"] = Version, ["update_link"] = $"http://127.0.0.1:{Port}/firefox/nova-enhance.xpi" })
            }
        }
    }.ToJsonString();

    /// <summary>Writes both versions of the add-on. Returns the folder.</summary>
    public static string Write()
    {
        var dir = ChromeFolder;
        Directory.CreateDirectory(dir);
        foreach (var f in Files)
        {
            var b = Resource(f);
            if (b != null) File.WriteAllBytes(Path.Combine(dir, f), b);
        }
        File.WriteAllText(Path.Combine(dir, "config.js"), Config(), new UTF8Encoding(false));
        // Chrome's service worker loads config.js itself (importScripts); the panel loads it with a script tag.
        File.WriteAllText(Path.Combine(dir, "manifest.json"), ChromeManifest(), new UTF8Encoding(false));

        var tmp = Xpi + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var f in Files)
            {
                var b = Resource(f);
                if (b == null) continue;
                using var s = z.CreateEntry(f, CompressionLevel.Optimal).Open();
                s.Write(b);
            }
            using (var s = z.CreateEntry("config.js").Open()) s.Write(Encoding.UTF8.GetBytes(Config()));
            using (var s = z.CreateEntry("manifest.json").Open()) s.Write(Encoding.UTF8.GetBytes(FirefoxManifest()));
        }
        File.Move(tmp, Xpi, true);
        File.WriteAllText(Path.Combine(Folder, "How to install.txt"), Steps(), new UTF8Encoding(false));
        Store.Set("win_browser_written", Version);
        return Folder;
    }

    /// <summary>After Nova updates: refresh the add-on files once (Chrome/Edge reload themselves; Firefox updates within a day).</summary>
    public static void Refresh()
    {
        try { if (Store.Str("win_browser_written") != Version || !File.Exists(Path.Combine(ChromeFolder, "manifest.json")) || !File.Exists(Xpi)) Write(); }
        catch (Exception e) { Log.Error("Browser add-on · write", e); }
    }

    public static void Enable()
    {
        On = true;
        Write();
        Server.StartBrowser();
    }

    public static string Steps() => $@"Nova Enhance — browser add-on
{Program.Sign}

Chrome or Edge (once)
  1. Open chrome://extensions  (Edge: edge://extensions)
  2. Turn on ""Developer mode"" (top right in Chrome, left side in Edge).
  3. Click ""Load unpacked"" and choose this folder:
     {ChromeFolder}
  4. Click the puzzle icon on the toolbar and pin ✦ Nova Enhance.
  Shortcuts: Alt+G fix grammar · Alt+T translate (change them at chrome://extensions/shortcuts).
  Chrome may remind you about developer-mode extensions at start-up — click the X; the add-on stays.

Firefox Developer Edition (once)
  1. Open about:config → accept the warning, then set:
       xpinstall.signatures.required  →  false   (allows Nova's own add-on)
       extensions.checkUpdateSecurity →  false   (lets it update itself from Nova on this PC)
  2. Open about:addons → ⚙ (gear) → Install Add-on From File… → choose
     {Xpi}
  3. Click ✦ on the toolbar to open the Nova sidebar. Shortcuts: Alt+G and Alt+T
     (change them in about:addons → ⚙ → Manage Extension Shortcuts).

Keep the Nova Enhance app running (it starts with Windows): the add-on uses its AI key and settings.
";
}
