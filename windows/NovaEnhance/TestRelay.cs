using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🌐 A · GitHub relay: Claude writes a test request into YOUR PRIVATE GitHub repository (requests/…json); Nova checks it every 30 s,
/// runs the test tool on this PC and writes the answer back (responses/…md). Nothing to install, no port opened on this PC.
/// Refuses to work with a public repository. Answers older than 7 days are deleted. Designed by A.HAMROUNI
/// </summary>
static class Relay
{
    public static bool On { get => Store.Bool("win_tc_relay", false); set { Store.SetBool("win_tc_relay", value); if (value) Start(); } }
    public static string Repo { get => Store.Str("win_tc_relay_repo", "thescorpio007/nova-test-relay"); set => Store.Set("win_tc_relay_repo", value.Trim().Trim('/')); }
    public static string Token { get => Store.Secret("win_tc_relay_token"); set => Store.SetSecret("win_tc_relay_token", value?.Trim()); }
    public static string Status = "", LastError = "";
    public static DateTime LastCheck = DateTime.MinValue;
    static Thread loop;

    static HttpClient Client()
    {
        var c = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NovaEnhance-TestRelay/" + Program.Build);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    /// <summary>Checks the repository: it must exist, be PRIVATE and be writable with the key.</summary>
    public static async Task<string> Check()
    {
        if (Token.Length < 10) throw new Exception("Paste the GitHub key first.");
        using var c = Client();
        var r = await c.GetAsync($"repos/{Repo}");
        if (!r.IsSuccessStatusCode) throw new Exception($"GitHub says {(int)r.StatusCode} for {Repo} — check the name and that the key gives access to this repository.");
        var o = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        if (o["private"]?.GetValue<bool>() != true) throw new Exception($"{Repo} is PUBLIC — Nova only uses a private repository. Make it private on GitHub (Settings → Danger zone → Change visibility).");
        var push = o["permissions"]?["push"]?.GetValue<bool>() == true;
        return $"✓ {Repo} · private · {(push ? "Nova can write the answers" : "⚠ the key can't write — give it Contents: Read and write")}";
    }

    public static void Start()
    {
        if (loop != null || !On) return;
        loop = new Thread(() => Guard.Run("test relay (GitHub)", () =>
        {
            while (true)
            {
                if (On && Token.Length > 10)
                    try { Pass().GetAwaiter().GetResult(); LastError = ""; }
                    catch (Exception e) { LastError = e.Message; Status = "⚠ " + e.Message; }
                Thread.Sleep(TimeSpan.FromSeconds(30));
            }
        })) { IsBackground = true, Name = "Nova test relay", Priority = ThreadPriority.BelowNormal };
        loop.Start();
    }

    static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    static async Task Pass()
    {
        using var c = Client();
        LastCheck = DateTime.Now;
        var list = await c.GetAsync($"repos/{Repo}/contents/requests");
        if (list.StatusCode == System.Net.HttpStatusCode.NotFound) { Status = $"✓ Connected · {LastCheck:HH:mm:ss} · no request waiting"; await Clean(c); return; }
        if (!list.IsSuccessStatusCode) throw new Exception($"GitHub {(int)list.StatusCode} reading {Repo}/requests");
        int n = 0;
        foreach (var f in JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray())
        {
            var name = f?["name"]?.ToString() ?? "";
            var sha = f?["sha"]?.ToString() ?? "";
            if (!name.EndsWith(".json")) continue;
            var id = Path.GetFileNameWithoutExtension(name);
            string answer;
            try
            {
                var raw = await c.GetAsync($"repos/{Repo}/contents/requests/{name}");
                var meta = JsonNode.Parse(await raw.Content.ReadAsStringAsync())!;
                var req = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String((meta["content"]?.ToString() ?? "").Replace("\n", ""))))!.AsObject();
                var tool = req["tool"]?.ToString() ?? "";
                var args = req["args"] as JsonObject ?? new JsonObject();
                Status = $"⏳ answering {tool}…";
                answer = await TestConnector.RunTool("GitHub relay", tool, (JsonObject)args.DeepClone());
                answer = $"# {id} · {tool} · answered {DateTime.Now:yyyy-MM-dd HH:mm:ss} · build {Program.Build}\n\n{answer}";
            }
            catch (Exception e) { answer = $"# {id} · ERROR {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n{e.Message}"; }
            var put = new JsonObject { ["message"] = "answer " + id, ["content"] = B64(answer) };
            var pr = await c.PutAsync($"repos/{Repo}/contents/responses/{id}.md", new StringContent(put.ToJsonString(), Encoding.UTF8, "application/json"));
            if (!pr.IsSuccessStatusCode) throw new Exception($"GitHub {(int)pr.StatusCode} writing the answer (the key needs Contents: Read and write)");
            var del = new HttpRequestMessage(HttpMethod.Delete, $"repos/{Repo}/contents/requests/{name}")
            { Content = new StringContent(new JsonObject { ["message"] = "done " + id, ["sha"] = sha }.ToJsonString(), Encoding.UTF8, "application/json") };
            await c.SendAsync(del);
            n++;
        }
        Status = $"✓ Connected · {LastCheck:HH:mm:ss} · {(n > 0 ? n + " request(s) answered" : "no request waiting")}";
        await Clean(c);
    }

    static DateTime lastClean = DateTime.MinValue;

    /// <summary>Answers older than 7 days are deleted (names like 20261112-191500-….md).</summary>
    static async Task Clean(HttpClient c)
    {
        if (DateTime.Now - lastClean < TimeSpan.FromHours(6)) return;
        lastClean = DateTime.Now;
        var list = await c.GetAsync($"repos/{Repo}/contents/responses");
        if (!list.IsSuccessStatusCode) return;
        foreach (var f in JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray())
        {
            var name = f?["name"]?.ToString() ?? "";
            if (name.Length < 15 || !DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var d) || d > DateTime.Now.AddDays(-7)) continue;
            var del = new HttpRequestMessage(HttpMethod.Delete, $"repos/{Repo}/contents/responses/{name}")
            { Content = new StringContent(new JsonObject { ["message"] = "clean " + name, ["sha"] = f?["sha"]?.ToString() }.ToJsonString(), Encoding.UTF8, "application/json") };
            try { await c.SendAsync(del); } catch { }
        }
    }
}

/// <summary>
/// 🌍 B · Internet connector: a Cloudflare quick tunnel (one small program, no admin rights) gives Nova's test tools a secret internet
/// address that you add in claude.ai → Settings → Connectors → Add custom connector. The address changes each time the tunnel starts.
/// Designed by A.HAMROUNI
/// </summary>
static class Remote
{
    public static bool On { get => Store.Bool("win_tc_remote", false); set { Store.SetBool("win_tc_remote", value); if (value) TestConnector.Start(); else StopTunnel(); } }
    public static string Secret
    {
        get { var k = Store.Secret("win_tc_remote_secret"); if (k.Length < 20) k = NewSecret(); return k; }
    }
    public static string NewSecret() { var k = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(); Store.SetSecret("win_tc_remote_secret", k); return k; }
    public static string PublicBase = "", Status = "";
    public static string Url => PublicBase.Length > 0 ? $"{PublicBase}/{Secret}/mcp" : "";
    static Process proc;
    static readonly object L = new();
    static string Exe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova Enhance", "tools", "cloudflared.exe");

    public static void StartTunnel()
    {
        lock (L) { if (proc != null && !proc.HasExited) return; }
        _ = Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(Exe))
                {
                    Status = "⏳ downloading the Cloudflare tunnel program (about 50 MB, once)…";
                    Directory.CreateDirectory(Path.GetDirectoryName(Exe)!);
                    using var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                    h.DefaultRequestHeaders.UserAgent.ParseAdd("NovaEnhance/" + Program.Build);
                    var bytes = await h.GetByteArrayAsync("https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe");
                    var tmp = Exe + ".part";
                    await File.WriteAllBytesAsync(tmp, bytes);
                    File.Move(tmp, Exe, true);
                }
                Status = "⏳ opening the tunnel…";
                var psi = new ProcessStartInfo(Exe, $"tunnel --no-autoupdate --url http://127.0.0.1:{TestConnector.Port}")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                var p = Process.Start(psi)!;
                lock (L) proc = p;
                void Line(string l)
                {
                    if (l == null) return;
                    var m = Regex.Match(l, @"https://[a-z0-9-]+\.trycloudflare\.com");
                    if (m.Success && PublicBase != m.Value) { PublicBase = m.Value; Status = "✓ Online — add the address below in claude.ai (it changes when the tunnel restarts)"; }
                }
                p.ErrorDataReceived += (_, e) => Line(e.Data);
                p.OutputDataReceived += (_, e) => Line(e.Data);
                p.BeginErrorReadLine(); p.BeginOutputReadLine();
                p.EnableRaisingEvents = true;
                p.Exited += (_, _) => { PublicBase = ""; if (On) Status = "⚠ the tunnel stopped (blocked by the network or antivirus?) — switch off and on to retry"; };
            }
            catch (Exception e) { Status = "⚠ " + e.Message + " — the company network or antivirus may block it; use 🌐 GitHub relay instead"; Log.Warn("test connector", "tunnel: " + e.Message); }
        });
    }

    public static void StopTunnel()
    {
        lock (L)
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
            proc = null;
        }
        PublicBase = "";
        Status = "Off";
    }
}
