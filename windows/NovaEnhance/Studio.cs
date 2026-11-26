using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🛠 Nova Studio: Nova develops itself with YOUR AI, without a web session.
///  C · Live extensions — 🧠 rules and ⭐ prompts made by the AI, applied at once (no build).
///  A · GitHub pipeline — the AI reads Nova's source on GitHub, proposes exact edits; on ✓ GO they are committed and GitHub builds, tests and signs.
///  B · Coding agent on this PC — a coding agent (Claude Code) edits, builds and checks its own copy of the source here; on ✓ GO it is pushed.
/// Every change: you approve → tests pass → signed build → ⬆ update, and ⟲ back to the previous build in one click.
/// The Studio AI reads code and diagnostics freely; documents, emails and screenshots only when you attach them; never passwords,
/// keys, tokens, PIN, VPN, OTP or 🔒 private files. Designed by A.HAMROUNI
/// </summary>
static class Studio
{
    public static string Mode { get => Store.Str("win_studio_mode", "C"); set => Store.Set("win_studio_mode", value); }
    public static string Repo { get => Store.Str("win_studio_repo", "thescorpio007/nova-enhance"); set => Store.Set("win_studio_repo", value.Trim()); }
    public static string Token { get => Store.Secret("win_studio_gh"); set => Store.SetSecret("win_studio_gh", value?.Trim()); }
    public static string AiId { get => Store.Str("win_studio_ai", Ai.HasKey("claude") ? "claude" : Ai.Active()?.Id ?? "claude"); set => Store.Set("win_studio_ai", value); }
    public static int Budget { get => Store.Int("win_studio_budget", 50); set => Store.SetInt("win_studio_budget", value); }
    public static Provider AiProvider => Ai.ById(AiId);
    public static string SourceDir => Path.Combine(Paths.Root, "Studio", "source");

    /// <summary>📊 This month's use (characters sent and received ÷ 4 ≈ tokens) — the budget line.</summary>
    public static long TokensThisMonth
    {
        get => Store.Str("win_studio_month", "") == DateTime.Today.ToString("yyyyMM") ? Store.Long("win_studio_tokens", 0) : 0;
        set { Store.Set("win_studio_month", DateTime.Today.ToString("yyyyMM")); Store.SetLong("win_studio_tokens", value); }
    }
    static void Count(string a, string b) => TokensThisMonth += ((a?.Length ?? 0) + (b?.Length ?? 0)) / 4;
    /// <summary>≈ USD at a coding model's list price (3 $ per million input, 15 $ per million output — about 6 $ per million mixed).</summary>
    public static double SpentUsd => TokensThisMonth * 6.0 / 1_000_000;

    static async Task<string> Ask(string system, string user, CancellationToken ct)
    {
        if (SpentUsd >= Budget) throw new Exception($"💳 This month's Studio budget ({Budget} USD) is used — raise it in 🛠 Nova Studio if you want to go on.");
        if (!Ai.HasKey(AiProvider.Id)) throw new Exception("🔑 Add the Studio AI key first (🛠 Nova Studio → ② Set up → AI key).");
        var r = await Ai.Chat(system, user, false, true, AiProvider, ct);
        Count(system + user, r);
        return r;
    }

    // ------------------------------------------------------------------ ② the setup checks

    static HttpClient Gh()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("NovaEnhance-Studio");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (Token.Length > 0) h.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return h;
    }
    static async Task<JsonNode> GhGet(HttpClient h, string url, CancellationToken ct)
    {
        var r = await h.GetAsync(url.StartsWith("http") ? url : "https://api.github.com/" + url.TrimStart('/'), ct);
        var t = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new Exception($"GitHub {(int)r.StatusCode}: {Clip(t, 160)}");
        return JsonNode.Parse(t);
    }
    static async Task<JsonNode> GhSend(HttpClient h, HttpMethod m, string url, JsonObject body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(m, "https://api.github.com/" + url.TrimStart('/')) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        var r = await h.SendAsync(req, ct);
        var t = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new Exception($"GitHub {(int)r.StatusCode}: {Clip(t, 160)}");
        return JsonNode.Parse(t);
    }
    static string Clip(string s, int n) => (s ?? "").Length > n ? s[..n] + "…" : s ?? "";

    public static async Task<string> TestAi(CancellationToken ct)
    {
        var p = AiProvider;
        if (!Ai.HasKey(p.Id)) return $"⚠ No {p.Label} key yet";
        var r = await Ai.Chat("Reply with the single word OK.", "Test", true, false, p, ct);
        return $"✓ {Ai.LabelOf(p)} answered “{Clip(r.Trim(), 20)}” · budget {Budget} USD / month · used ≈ {SpentUsd:0.00} USD";
    }

    public static async Task<string> TestGitHub(CancellationToken ct)
    {
        if (Token.Length < 10) return "⚠ No GitHub key yet";
        using var h = Gh();
        var me = await GhGet(h, "user", ct);
        var r = await GhGet(h, "repos/" + Repo, ct);
        var push = r["permissions"]?["push"]?.GetValue<bool>() == true;
        var priv = r["private"]?.GetValue<bool>() == true;
        return $"{(push ? "✓" : "⚠")} {me["login"]} · {Repo} · {(priv ? "🔒 private" : "public")} · {(push ? "can push" : "the key can't push — give it Contents: Read and write")}";
    }

    public static async Task<string> BuildCheck(CancellationToken ct)
    {
        using var h = Gh();
        var wf = await GhGet(h, $"repos/{Repo}/actions/workflows", ct);
        var n = wf["total_count"]?.GetValue<int>() ?? 0;
        var runs = await GhGet(h, $"repos/{Repo}/actions/runs?per_page=1&branch=main", ct);
        var last = (runs["workflow_runs"] as JsonArray)?.FirstOrDefault();
        var concl = last?["conclusion"]?.ToString() ?? last?["status"]?.ToString() ?? "—";
        var title = Clip(last?["display_title"]?.ToString() ?? "", 50);
        var good = n > 0 && concl is "success" or "in_progress" or "queued";
        return $"{(good ? "✓" : "⚠")} {n} workflow(s) · last build #{last?["run_number"]} {concl} · {title}";
    }

    /// <summary>🧰 Git · .NET SDK · Node.js · the coding agent — their versions (empty = missing).</summary>
    public static Dictionary<string, string> Tools()
    {
        string V(string exe, string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
                if (p == null) return "";
                var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return p.ExitCode == 0 ? Clip(o.Trim().Split('\n')[0].Trim(), 40) : "";
            }
            catch { return ""; }
        }
        return new()
        {
            ["Git"] = V("git", "--version"),
            [".NET SDK"] = V("dotnet", "--version") is { Length: > 0 } d && Regex.IsMatch(d, @"^\d") ? d : "",
            ["Node.js"] = V("node", "--version"),
            ["Coding agent"] = V("cmd.exe", "/c claude --version"),
        };
    }

    /// <summary>Installs a tool with Windows' own installer (winget) — Windows asks you to allow it.</summary>
    public static Task<string> Install(string tool, Action<string> say) => Task.Run(() =>
    {
        var (file, args) = tool switch
        {
            "Git" => ("winget", "install --id Git.Git -e --accept-package-agreements --accept-source-agreements"),
            ".NET SDK" => ("winget", "install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements"),
            "Node.js" => ("winget", "install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements"),
            "Coding agent" => ("cmd.exe", "/c npm install -g @anthropic-ai/claude-code"),
            _ => (null, null),
        };
        if (file == null) return "?";
        say?.Invoke($"⏳ Installing {tool}… (accept Windows' prompt if it asks)");
        try
        {
            var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal });
            p?.WaitForExit(20 * 60_000);
            return p?.ExitCode == 0 ? $"✓ {tool} installed — restart Nova if it is not found yet" : $"⚠ {tool}: the installer ended with code {p?.ExitCode}";
        }
        catch (Exception e) { return $"⚠ {tool}: {e.Message}"; }
    });

    // ------------------------------------------------------------------ ⚡ C · Live extensions (no build)

    public class Ext { public string Kind = "", Section = "", Title = "", Text = "", Name = "", Question = "", Why = ""; }

    public static async Task<List<Ext>> ProposeExt(string ask, string attached, CancellationToken ct)
    {
        var secs = string.Join(", ", (Brain.Snapshot()["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(s => $"{s["id"]} ({s["name"]})"));
        var sys = "You extend the Nova Enhance app WITHOUT changing its code: only with 🧠 Nova Brain rules (plain sentences Nova follows; settings as [name = value]) " +
                  "or ⭐ saved prompts (a question Nova runs on demand). Brain sections: " + secs + ". " +
                  "Never include passwords, keys or private data. If the wish needs real code, say so in 'note' and return no items. " +
                  "Reply JSON only: {\"items\":[{\"kind\":\"rule|prompt\",\"section\":\"<section id>\",\"title\":\"…\",\"text\":\"<the rule>\",\"name\":\"<prompt name>\",\"question\":\"<prompt question>\",\"why\":\"…\"}],\"note\":\"…\"}";
        var j = Ai.ParseJson(await Ask(sys, ask + (attached.Length > 0 ? "\n\nATTACHED:\n" + attached : ""), ct));
        var res = (j["items"] as JsonArray ?? new JsonArray()).Select(x => new Ext
        {
            Kind = x?["kind"]?.ToString() ?? "", Section = x?["section"]?.ToString() ?? "", Title = x?["title"]?.ToString() ?? "", Text = x?["text"]?.ToString() ?? "",
            Name = x?["name"]?.ToString() ?? "", Question = x?["question"]?.ToString() ?? "", Why = x?["why"]?.ToString() ?? "",
        }).Where(e => e.Kind is "rule" or "prompt").ToList();
        if (j["note"]?.ToString() is { Length: > 0 } note) res.Insert(0, new Ext { Kind = "note", Text = note });
        return res;
    }

    public static string ApplyExt(Ext e)
    {
        if (e.Kind == "prompt")
        {
            Prompts.Save(new SavedPrompt { Name = e.Name.Length > 0 ? e.Name : e.Title, Question = e.Question.Length > 0 ? e.Question : e.Text, Where = "ask" });
            return $"✓ ⭐ Prompt “{e.Name}” saved";
        }
        if (!Brain.Unlocked) throw new Exception("Open 🧠 Nova Brain first (its password).");
        var b = Brain.Snapshot();
        var sec = (b["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(s => s["id"]?.ToString() == e.Section)
                  ?? (b["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().First();
        var rules = sec["rules"] as JsonArray ?? new JsonArray(); sec["rules"] = rules;
        var ids = Brain.Rules(b).Select(x => x.Rule["id"]?.ToString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = $"{sec["prefix"] ?? "ST"}-S{DateTime.Now:MMddHHmmss}";
        for (var c = 'b'; ids.Contains(id); c++) id = $"{sec["prefix"] ?? "ST"}-S{DateTime.Now:MMddHHmmss}-{c}";
        rules.Add(new JsonObject { ["id"] = id, ["title"] = e.Title, ["text"] = e.Text, ["by"] = "🛠 Nova Studio", ["when"] = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
        Brain.Commit(b, "🛠 Nova Studio", e.Title);
        return $"✓ 🧠 Rule {id} “{e.Title}” added to {sec["name"]}";
    }

    // ------------------------------------------------------------------ 🐙 A · GitHub pipeline (the AI reads the source on GitHub, proposes exact edits)

    public class Edit { public string Path = "", Old = "", New = "", Why = ""; }
    public class Plan { public string Summary = "", Note = ""; public List<Edit> Edits = new(); public Dictionary<string, (string Sha, string Text)> Files = new(); }

    public static async Task<Plan> Propose(string ask, string attached, Action<string> say, CancellationToken ct)
    {
        using var h = Gh();
        say?.Invoke("📂 Reading Nova's source tree on GitHub…");
        var tree = await GhGet(h, $"repos/{Repo}/git/trees/main?recursive=1", ct);
        var paths = (tree["tree"] as JsonArray ?? new JsonArray()).Select(x => x?["path"]?.ToString() ?? "")
            .Where(p => Regex.IsMatch(p, @"\.(cs|js|html|css|kt|kts|xml|yml|iss|csproj|md)$", RegexOptions.IgnoreCase) && !p.Contains("/bin/") && !p.Contains("/obj/")).ToList();
        say?.Invoke("✦ Choosing the files to read…");
        var pick = Ai.ParseJson(await Ask("You are the developer of the Nova Enhance app (C# WinForms .NET 8 in windows/NovaEnhance, web pages in windows/NovaEnhance/Chat, Android Kotlin in app/). " +
            "Pick the files you must read to make the change (at most 8). Reply JSON only: {\"files\":[\"path\",…]}", "WISH: " + ask + (attached.Length > 0 ? "\n\nATTACHED:\n" + Clip(attached, 6000) : "") + "\n\nFILES:\n" + string.Join("\n", paths), ct));
        var want = (pick["files"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "").Where(paths.Contains).Take(8).ToList();
        var plan = new Plan();
        var sb = new StringBuilder();
        foreach (var p in want)
        {
            say?.Invoke("📄 Reading " + p + "…");
            var f = await GhGet(h, $"repos/{Repo}/contents/{Uri.EscapeDataString(p).Replace("%2F", "/")}?ref=main", ct);
            var text = Encoding.UTF8.GetString(Convert.FromBase64String((f["content"]?.ToString() ?? "").Replace("\n", "")));
            plan.Files[p] = (f["sha"]?.ToString() ?? "", text);
            sb.Append("=== ").Append(p).Append(" ===\n").Append(text.Length > 120_000 ? text[..120_000] + "\n…(cut)" : text).Append("\n\n");
        }
        say?.Invoke("✦ Writing the change…");
        var sys = "You are the developer of the Nova Enhance app. Make the smallest correct change for the wish. Rules: keep the existing style; plain English UI text; " +
                  "keep 'Designed by A.HAMROUNI'; never weaken the security rules (🔒 private files, passwords, keys, consent before sending data); keep the smoke tests passing. " +
                  "Each edit replaces an EXACT piece of an existing file (old must occur exactly once; copy it character for character) — or creates a new file when old is empty. " +
                  "Reply JSON only: {\"summary\":\"<what changes, for the user, 2-4 lines>\",\"edits\":[{\"path\":\"…\",\"old\":\"…\",\"new\":\"…\",\"why\":\"…\"}],\"note\":\"<risks or what to test>\"}";
        var j = Ai.ParseJson(await Ask(sys, "WISH: " + ask + (attached.Length > 0 ? "\n\nATTACHED:\n" + Clip(attached, 20000) : "") + "\n\nFILES:\n" + sb, ct));
        plan.Summary = j["summary"]?.ToString() ?? ""; plan.Note = j["note"]?.ToString() ?? "";
        foreach (var e in j["edits"] as JsonArray ?? new JsonArray())
            plan.Edits.Add(new Edit { Path = e?["path"]?.ToString() ?? "", Old = e?["old"]?.ToString() ?? "", New = e?["new"]?.ToString() ?? "", Why = e?["why"]?.ToString() ?? "" });
        // ✓ every edit must apply exactly once
        foreach (var e in plan.Edits)
        {
            if (e.Old.Length == 0) continue;
            if (!plan.Files.TryGetValue(e.Path, out var f)) throw new Exception($"The AI edited {e.Path}, which it did not read — ask again.");
            var n = Regex.Matches(f.Text, Regex.Escape(e.Old)).Count;
            if (n != 1) throw new Exception($"An edit in {e.Path} does not match the file exactly ({n} matches) — ask again or say “be exact”.");
        }
        return plan;
    }

    /// <summary>✓ GO: the edits become one commit on main (GitHub then builds, tests and signs).</summary>
    public static async Task<string> Push(Plan plan, string message, CancellationToken ct)
    {
        using var h = Gh();
        var refj = await GhGet(h, $"repos/{Repo}/git/ref/heads/main", ct);
        var head = refj["object"]?["sha"]?.ToString();
        var commit = await GhGet(h, $"repos/{Repo}/git/commits/{head}", ct);
        var baseTree = commit["tree"]?["sha"]?.ToString();
        var files = new Dictionary<string, string>();
        foreach (var e in plan.Edits)
        {
            var text = files.TryGetValue(e.Path, out var t) ? t : plan.Files.TryGetValue(e.Path, out var f) ? f.Text : "";
            files[e.Path] = e.Old.Length == 0 && text.Length == 0 ? e.New : text.Replace(e.Old, e.New);
        }
        var items = new JsonArray();
        foreach (var (p, text) in files)
        {
            var blob = await GhSend(h, HttpMethod.Post, $"repos/{Repo}/git/blobs", new JsonObject { ["content"] = text, ["encoding"] = "utf-8" }, ct);
            items.Add(new JsonObject { ["path"] = p, ["mode"] = "100644", ["type"] = "blob", ["sha"] = blob["sha"]?.ToString() });
        }
        var tree = await GhSend(h, HttpMethod.Post, $"repos/{Repo}/git/trees", new JsonObject { ["base_tree"] = baseTree, ["tree"] = items }, ct);
        var c = await GhSend(h, HttpMethod.Post, $"repos/{Repo}/git/commits", new JsonObject
        {
            ["message"] = message + "\n\nMade with 🛠 Nova Studio (" + Ai.LabelOf(AiProvider) + ") · Designed by A.HAMROUNI",
            ["tree"] = tree["sha"]?.ToString(), ["parents"] = new JsonArray(head),
        }, ct);
        await GhSend(h, HttpMethod.Patch, $"repos/{Repo}/git/refs/heads/main", new JsonObject { ["sha"] = c["sha"]?.ToString() }, ct);
        return c["sha"]?.ToString() ?? "";
    }

    /// <summary>⏳ The GitHub build of a commit: (finished?, the line to show).</summary>
    public static async Task<(bool Done, bool Ok, string Line)> BuildOf(string sha, CancellationToken ct)
    {
        using var h = Gh();
        var runs = await GhGet(h, $"repos/{Repo}/actions/runs?head_sha={sha}", ct);
        var r = (runs["workflow_runs"] as JsonArray)?.FirstOrDefault();
        if (r == null) return (false, false, "⏳ waiting for GitHub to start the build…");
        var st = r["status"]?.ToString(); var cc = r["conclusion"]?.ToString();
        if (st != "completed") return (false, false, $"⏳ GitHub build #{r["run_number"]} {st}…");
        return (true, cc == "success", cc == "success" ? $"✓ Build #{r["run_number"]} passed (tests ✓) — ⬆ Updates will offer it in a few minutes" : $"✗ Build #{r["run_number"]} {cc} — nothing is installed; ask the Studio to fix it (paste: “the build failed”)");
    }

    // ------------------------------------------------------------------ 🤖 B · Coding agent on this PC

    static async Task<(int Code, string Out)> Run(string file, string args, string dir, Action<string> line, CancellationToken ct, Dictionary<string, string> env = null)
    {
        var psi = new ProcessStartInfo(file, args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        void Read(StreamReader r) { string l; while ((l = r.ReadLine()) != null) { lock (sb) sb.AppendLine(l); line?.Invoke(l); } }
        var t1 = Task.Run(() => Read(p.StandardOutput)); var t2 = Task.Run(() => Read(p.StandardError));
        using (ct.Register(() => { try { p.Kill(true); } catch { } })) await p.WaitForExitAsync();
        await Task.WhenAll(t1, t2);
        return (p.ExitCode, sb.ToString());
    }

    /// <summary>The agent's own copy of the source (cloned once, brought up to date before every task). Never your project folders.</summary>
    public static async Task<string> Source(Action<string> say, CancellationToken ct)
    {
        var url = $"https://x-access-token:{Token}@github.com/{Repo}.git";
        if (!Directory.Exists(Path.Combine(SourceDir, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SourceDir)!);
            say?.Invoke("📥 Copying Nova's source to this PC (once)…");
            var (c, o) = await Run("git", $"clone {url} \"{SourceDir}\"", Path.GetDirectoryName(SourceDir)!, null, ct);
            if (c != 0) throw new Exception("git clone failed: " + Clip(o.Replace(Token, "***"), 200));
        }
        else
        {
            say?.Invoke("⟳ Bringing the source up to date…");
            await Run("git", $"remote set-url origin {url}", SourceDir, null, ct);
            await Run("git", "fetch origin main", SourceDir, null, ct);
            await Run("git", "reset --hard origin/main", SourceDir, null, ct);
        }
        return SourceDir;
    }

    public static async Task<(string Diff, bool Built, string BuildOut)> Agent(string task, string attached, Action<string> line, CancellationToken ct)
    {
        var dir = await Source(line, ct);
        var key = Store.Key("claude");
        if (key.Length == 0) throw new Exception("🔑 The coding agent uses your Anthropic (Claude) key — add it in Settings → AI & API keys.");
        var brief = "You develop the Nova Enhance app in this folder (C# .NET 8 WinForms in windows/NovaEnhance, web pages in windows/NovaEnhance/Chat, Android in app/). " +
                    "Make the smallest correct change. Keep 'Designed by A.HAMROUNI'. Never weaken security (🔒 private files, passwords, keys, consent before data leaves the PC). " +
                    "Build with: dotnet build windows/NovaEnhance/NovaEnhance.csproj -c Release. Do not commit or push — Nova does that after the user approves.";
        var file = Path.Combine(Path.GetTempPath(), "nova-studio-task.txt");
        File.WriteAllText(file, task + (attached.Length > 0 ? "\n\nATTACHED:\n" + attached : ""));
        line?.Invoke("🤖 The agent is working… (its steps appear below)");
        var (code, _) = await Run("cmd.exe", $"/c claude -p \"Do the task in {file}\" --append-system-prompt \"{brief.Replace("\"", "'")}\" --permission-mode acceptEdits " +
                                  "--allowedTools \"Read,Edit,Write,Glob,Grep,Bash(dotnet build:*),Bash(git status:*),Bash(git diff:*)\"",
            dir, line, ct, new() { ["ANTHROPIC_API_KEY"] = key });
        Count(task + attached, "");
        if (code != 0) line?.Invoke($"⚠ The agent ended with code {code}");
        var (_, diff) = await Run("git", "diff --stat", dir, null, ct);
        line?.Invoke("🧪 Building here…");
        var (bc, bo) = await Run("dotnet", "build windows/NovaEnhance/NovaEnhance.csproj -c Release -nologo -v q", dir, null, ct);
        return (diff.Trim(), bc == 0, string.Join("\n", bo.Split('\n').Where(l => l.Contains(" error ")).Take(12)));
    }

    public static async Task<string> PushLocal(string message, CancellationToken ct)
    {
        await Run("git", "add -A", SourceDir, null, ct);
        var (c1, o1) = await Run("git", $"-c user.name=\"Nova Studio\" -c user.email=\"studio@nova.local\" commit -m \"{message.Replace("\"", "'")}\" -m \"Made with Nova Studio (coding agent) · Designed by A.HAMROUNI\"", SourceDir, null, ct);
        if (c1 != 0) throw new Exception("Nothing to commit: " + Clip(o1, 120));
        var (c2, o2) = await Run("git", "push origin HEAD:main", SourceDir, null, ct);
        if (c2 != 0) throw new Exception("Push failed: " + Clip(o2.Replace(Token, "***"), 200));
        var (_, sha) = await Run("git", "rev-parse HEAD", SourceDir, null, ct);
        return sha.Trim();
    }

    // ------------------------------------------------------------------ 🩺 what the Studio AI may read freely (read-only diagnostics)

    public static async Task<string> Diagnostics()
    {
        var sb = new StringBuilder();
        try { sb.AppendLine(await ClaudeChecks.Errors(new JsonObject { ["hours"] = 24 })); } catch (Exception e) { sb.AppendLine("errors: " + e.Message); }
        try { sb.AppendLine(ClaudeChecks.Perf()); } catch { }
        try { sb.AppendLine(ClaudeChecks.Updates()); } catch { }
        var t = sb.ToString();
        return t.Length > 30_000 ? t[..30_000] + "\n…" : t;
    }
}
