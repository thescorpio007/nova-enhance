using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🧪 Test connector: lets Claude (through the Claude desktop app) test Nova on this PC — off by default, this PC only, with its own key.
/// Claude gets TEST tools only: status, a search (subjects / document numbers / dates / why — names hidden), find an email in the index,
/// Activity history, errors and self-checks. Never email bodies; 🔒 private emails and folders are never shown. Every call is written in
/// 🧪 Activity history. Two parts: the running Nova answers on http://127.0.0.1:47322 (key needed, no web pages), and
/// "NovaEnhance.exe --mcp" is the small MCP server Claude desktop starts, which passes Claude's calls to Nova. Designed by A.HAMROUNI
/// </summary>
static class TestConnector
{
    public const int Port = 47322;
    public static bool On { get => Store.Bool("win_tc_on", false); set { Store.SetBool("win_tc_on", value); if (value) Start(); else if (!Remote.On) { try { listener?.Stop(); } catch { } listener = null; } } }
    public static string Key
    {
        get { var k = Store.Str("win_tc_key", ""); if (k.Length < 20) { k = NewKey(); } return k; }
    }
    public static string NewKey() { var k = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(); Store.Set("win_tc_key", k); return k; }
    public static bool Running => listener != null;
    public static string LastError = "";
    public static readonly List<string> Calls = new();   // the last calls (shown in Settings)

    static TcpListener listener;

    // ------------------------------------------------------------------ inside the running Nova

    public static void Start()
    {
        Relay.Start();
        if (Remote.On) Remote.StartTunnel();
        if (listener != null || !(On || Remote.On)) return;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, Port); listener.Start();
            var l = listener; _ = Task.Run(() => Accept(l));
            LastError = "";
        }
        catch (Exception e) { listener = null; LastError = e.Message; Log.Error("🧪 test connector", e); }
    }

    public static void Stop() { try { listener?.Stop(); } catch { } listener = null; Remote.StopTunnel(); }

    /// <summary>The PC part runs when any way in is on (Claude desktop, GitHub relay, internet).</summary>
    public static bool Needed => On || Relay.On || Remote.On;

    static async Task Accept(TcpListener l)
    {
        while (true)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(); } catch { return; }
            _ = Handle(c);
        }
    }

    static async Task Handle(TcpClient client)
    {
        using var _c = client;
        try
        {
            var stream = client.GetStream();
            var (method, path, headers, body) = await Server.Read(stream);
            if (method == null) return;
            int code = 200; string json;
            var p = path.Split('?')[0];
            JsonObject Body() { try { return string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)?.AsObject() ?? new JsonObject(); } catch { return new JsonObject(); } }
            if (p.StartsWith("/tc/"))
            {
                // Nova's own MCP part (Claude desktop): the key, and never a web page (browsers always send an Origin).
                if (method != "POST" || headers.ContainsKey("Origin") || !headers.TryGetValue("X-Nova-Key", out var k) || k != Key)
                { code = 403; json = new JsonObject { ["error"] = "forbidden" }.ToJsonString(); }
                else
                {
                    JsonObject res;
                    try { res = new JsonObject { ["text"] = await RunTool("Claude desktop", p[4..], Body()) }; }
                    catch (Exception e) { res = new JsonObject { ["error"] = e.Message }; }
                    json = res.ToJsonString();
                }
            }
            else if (Remote.On && Remote.Secret.Length >= 20 && p == "/" + Remote.Secret + "/mcp")
            {
                // 🌍 B · claude.ai custom connector through the tunnel (MCP "streamable HTTP", plain JSON answers).
                if (method != "POST") { code = 405; json = "{}"; }
                else
                {
                    var resp = await Rpc(Body(), async (tool, args) =>
                    {
                        try { return (await RunTool("claude.ai (internet)", tool, JsonNode.Parse(args)?.AsObject() ?? new JsonObject()), false); }
                        catch (Exception e) { return (e.Message, true); }
                    });
                    if (resp == null) { code = 202; json = ""; } else json = resp.ToJsonString();
                }
            }
            else { code = 404; json = "{\"error\":\"not found\"}"; }
            var bytes = Encoding.UTF8.GetBytes(json);
            var head = $"HTTP/1.1 {code} {(code switch { 200 => "OK", 202 => "Accepted", 403 => "Forbidden", 405 => "Method Not Allowed", _ => "Not Found" })}\r\n" +
                       (bytes.Length > 0 ? "Content-Type: application/json; charset=utf-8\r\n" : "") + $"Content-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            if (bytes.Length > 0) await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
        catch { }
    }

    // ------------------------------------------------------------------ every call: approval (if asked), Activity, last calls

    /// <summary>"Ask me before each request" (on by default): a window on this PC — Allow · Allow for 1 hour · Deny.</summary>
    public static bool AskEach { get => Store.Bool("win_tc_ask", true); set => Store.SetBool("win_tc_ask", value); }
    static DateTime allowUntil = DateTime.MinValue;

    internal static async Task<string> RunTool(string via, string tool, JsonObject req)
    {
        tool = tool.Replace("nova_", "");
        lock (Calls) { Calls.Insert(0, $"{DateTime.Now:dd MMM HH:mm:ss}  {via}  {tool}  {Cut(req.ToJsonString(), 120)}"); if (Calls.Count > 30) Calls.RemoveAt(30); }
        if (!await Approve(via, tool, req)) throw new Exception("Declined on the PC (Ask me before each request).");
        using var act = Activity.Begin("🧪 test connector · " + tool, via + " · " + req.ToJsonString());
        try { var t = await Tool(tool, req); act.Result = "ok · " + via; return t; }
        catch (Exception e) { act.Fail(e.Message); throw; }
    }

    static Task<bool> Approve(string via, string tool, JsonObject req)
    {
        if (!AskEach || DateTime.Now < allowUntil) return Task.FromResult(true);
        var m = Program.Marshal;
        if (m == null || !m.IsHandleCreated) return Task.FromResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var what = tool switch
        {
            "search" => $"run the email search “{S(req, "question")}”" + (S(req, "project").Length > 0 ? $" (project {S(req, "project")})" : ""),
            "find_email" => $"check whether “{S(req, "text")}” is in the email index",
            "activity" => "read the 🧪 Activity history",
            "errors" => "read the error log",
            "selfcheck" => "run the self-checks",
            "status" => "read Nova's status",
            _ => tool
        };
        try
        {
            m.BeginInvoke(() =>
            {
                bool ok = false;
                try
                {
                    using var f = new Form
                    {
                        Text = "🔌 Claude asks to test Nova", TopMost = true, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
                        MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(620, 190), BackColor = Th.Bg, ForeColor = Th.Text, Font = Th.F(10.5f), ShowInTaskbar = true
                    };
                    f.Controls.Add(new Label { Text = $"Claude ({via}) asks to {what}.\n\nThe answer holds subjects, document numbers, dates and steps — names hidden, never email bodies or 🔒 private emails.", Left = 18, Top = 16, Width = 584, Height = 100 });
                    var row = new FlowLayoutPanel { Left = 14, Top = 128, Width = 600, Height = 50, BackColor = Th.Bg };
                    row.Controls.Add(Th.B("✓ Allow", () => { f.DialogResult = DialogResult.Yes; }, true));
                    row.Controls.Add(Th.B("✓ Allow for 1 hour", () => { f.DialogResult = DialogResult.Retry; }));
                    row.Controls.Add(Th.B("✗ Deny", () => { f.DialogResult = DialogResult.No; }));
                    f.Controls.Add(row);
                    var t = new System.Windows.Forms.Timer { Interval = 180000 };
                    t.Tick += (_, _) => { t.Stop(); f.DialogResult = DialogResult.No; };
                    t.Start();
                    var r = f.ShowDialog();
                    t.Dispose();
                    if (r == DialogResult.Retry) allowUntil = DateTime.Now.AddHours(1);
                    ok = r is DialogResult.Yes or DialogResult.Retry;
                }
                catch { }
                tcs.TrySetResult(ok);
            });
        }
        catch { return Task.FromResult(false); }
        return tcs.Task;
    }

    static string Cut(string s, int n) => s.Length > n ? s[..n] + "…" : s;
    static string S(JsonObject o, string k, string d = "") => o[k]?.ToString() ?? d;
    static int I(JsonObject o, string k, int d) => int.TryParse(o[k]?.ToString(), out var v) ? v : d;
    static bool Priv(MailHit h) => Projects.Private.IsMatch((h.Subject ?? "") + " " + (h.AttNames ?? "") + " " + (h.Folder ?? ""));

    static string Line(MailHit h, Activity.Hider hide)
    {
        if (Priv(h)) return $"- {h.Date:yyyy-MM-dd} · 🔒 private email (hidden)";
        var p = ""; try { p = MailProjects.Of(h.Id); } catch { }
        return $"- {h.Date:yyyy-MM-dd HH:mm} · {(h.Sent ? "SENT by me" : "received from " + hide.Hide(h.FromName ?? ""))} · project {(p.Length > 0 ? p : "unsorted")} · folder {hide.Hide(h.Folder ?? "")}\n" +
               $"  subject: {hide.Hide(h.Subject ?? "")}\n" +
               (string.IsNullOrEmpty(h.AttNames) ? "" : $"  attachments: {hide.Hide(Cut(h.AttNames, 300))}\n") +
               (string.IsNullOrEmpty(h.Why) ? "" : $"  why: {hide.Hide(h.Why)}\n") +
               ((h.Entry ?? "").StartsWith("pst:") ? "  (from an archive read directly)\n" : "");
    }

    static async Task<string> Tool(string tool, JsonObject a)
    {
        var sb = new StringBuilder();
        switch (tool)
        {
            case "status":
            {
                sb.AppendLine($"Nova Enhance build {Program.Build} · {DateTime.Now:dd MMM yyyy HH:mm} · safe mode {(Guard.SafeMode ? "ON" : "off")}");
                sb.AppendLine("Index: " + MailIndex.OldestText);
                try { foreach (var s in MailIndex.StoreStats()) sb.AppendLine($"- mailbox/archive “{s.Store}”: {s.Indexed:N0} of {s.Total:N0} · {s.Oldest:yyyy-MM-dd} → {s.Newest:yyyy-MM-dd}"); } catch (Exception e) { sb.AppendLine("stores: " + e.Message); }
                try { foreach (var x in Archives.StoreFiles()) sb.AppendLine($"- in Outlook: {x.Key} = {x.Value} {(File.Exists(x.Value) ? "" : "⚠ FILE NOT FOUND")}"); } catch { }
                try { foreach (var x in Pst.All.Values) sb.AppendLine($"- read directly: {Path.GetFileName(x.Path)} · {x.Indexed:N0} indexed of {x.Total:N0} · {x.Status} {(File.Exists(x.Path) ? "" : "⚠ FILE NOT FOUND")}"); } catch { }
                try { sb.AppendLine("Email projects: " + string.Join(" · ", MailProjects.Counts.OrderByDescending(x => x.Value).Take(15).Select(x => $"{(x.Key.Length > 0 ? x.Key : "unsorted")} {x.Value:N0}"))); } catch { }
                sb.AppendLine("Background jobs: " + Guard.JobsLine());
                sb.AppendLine($"AI: current {Ai.LabelOf(Ai.Current())} · search/ranking {Ai.LabelOf(SmartSearch.Cloud())} · AI search {(SmartSearch.On ? "on" : "off")}");
                break;
            }
            case "search":
            {
                var q = S(a, "question").Trim();
                if (q.Length == 0) throw new Exception("question is empty");
                var proj = S(a, "project").Trim().ToUpperInvariant();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ans = await Task.Run(() => Assistant.Ask(q, null, null, cts.Token, true, proj));
                var hide = new Activity.Hider();
                sb.AppendLine($"Question: {q}{(proj.Length > 0 ? " · project button " + proj : "")} · {sw.Elapsed.TotalSeconds:0.0} s");
                if (ans.Explain.Length > 0) sb.AppendLine("Understood: " + ans.Explain);
                if (ans.Searched.Length > 0) sb.AppendLine(ans.Searched);
                if (ans.Message.Length > 0) sb.AppendLine("Message: " + ans.Message);
                var job = Activity.Current;
                if (job != null) lock (job.Steps) foreach (var st in job.Steps) sb.AppendLine($"  step {st.Ms,6} ms · {st.Name} · {hide.Hide(st.Info)}");
                if (job != null) lock (job.Think) foreach (var t in job.Think) sb.AppendLine($"  {t.Key}: {hide.Hide(Cut(t.Value, 700))}");
                sb.AppendLine($"{ans.Hits.Count} emails:");
                foreach (var h in ans.Hits) sb.Append(Line(h, hide));
                break;
            }
            case "find_email":
            {
                // Is this email (subject words or a document number) in the index at all — whatever any search does?
                var text = S(a, "text").Trim();
                if (text.Length < 3) throw new Exception("text is too short");
                var q = new MailQuery { Limit = Math.Clamp(I(a, "limit", 20), 1, 50) };
                q.Words = Regex.Split(text, @"[\s_]+").Where(w => w.Length > 0).Select(w => w.Trim('"')).ToList();
                var hits = await Task.Run(() => MailIndex.Search(q));
                var hide = new Activity.Hider();
                sb.AppendLine($"Index lookup “{text}” (all words, every folder, sent and received, all projects): {hits.Count} emails");
                foreach (var h in hits) sb.Append(Line(h, hide));
                break;
            }
            case "activity":
            {
                var days = Math.Clamp(I(a, "days", 1), 1, 30);
                var feature = S(a, "feature");
                var items = Activity.Read(days);
                if (feature.Length > 0) items = items.Where(x => (x["feature"]?.ToString() ?? "").Contains(feature, StringComparison.OrdinalIgnoreCase)).ToList();
                var hide = new Activity.Hider();
                sb.AppendLine(hide.Hide(Activity.Summary(items, days)));
                foreach (var it in items.TakeLast(Math.Clamp(I(a, "limit", 15), 1, 60)))
                {
                    var o = (JsonObject)hide.Hide(it);
                    o.Remove("ai_calls");   // the text sent to the AI holds email lines — only the timings are given here
                    sb.AppendLine(Cut(o.ToJsonString(), 4000));
                }
                break;
            }
            case "errors":
            {
                var hours = Math.Clamp(I(a, "hours", 24), 1, 24 * 30);
                var hide = new Activity.Hider();
                var list = Log.ReadAll().Where(x => x.Time > DateTime.Now.AddHours(-hours)).ToList();
                sb.AppendLine($"{list.Count} errors / warnings in the last {hours} h");
                foreach (var e in list.TakeLast(80)) sb.AppendLine(hide.Hide(Cut(Log.Format(e), 1500)));
                var crash = Path.Combine(Paths.Logs, "last-crash.txt");
                if (File.Exists(crash)) sb.AppendLine("\nlast-crash.txt:\n" + hide.Hide(Cut(File.ReadAllText(crash), 3000)));
                var freeze = Path.Combine(Paths.Logs, "ui-freeze.txt");
                if (File.Exists(freeze)) sb.AppendLine("\nui-freeze.txt (end):\n" + string.Join("\n", File.ReadAllLines(freeze).TakeLast(15)));
                break;
            }
            case "selfcheck":
            {
                void R(string what, Func<string> f) { try { sb.AppendLine("✓ " + what + ": " + f()); } catch (Exception e) { sb.AppendLine("✗ " + what + ": " + e.Message); } }
                R("email index", () => { using var c = MailIndex.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM mail"; return $"{Convert.ToInt64(cmd.ExecuteScalar()):N0} emails"; });
                R("full-text search", () => $"{MailIndex.CountIds(new MailQuery { Words = new() { "the" } }, 100)} found for “the” (100 = working)");
                R("flexible phrase", () => $"{MailIndex.CountIds(new MailQuery { Phrase = "fire hose cabinet", Near = 2 }, 100000):N0} emails with “fire hose … cabinet”");
                R("Outlook", () => Outlook.Get() != null ? "running" : "not running");
                R("archive files", () => string.Join(" · ", Archives.StoreFiles().Select(x => $"{Path.GetFileName(x.Value)} {(File.Exists(x.Value) ? "ok" : "MISSING")}")));
                R("renamed archives linked", () => Store.Str("win_store_moved", "{}"));
                R("Gemini key", () => Ai.HasKey("gemini") ? "yes" : "no");
                R("local AI (LM Studio)", () => { var d = Ai.Detect().GetAwaiter().GetResult(); return d == null ? "not running" : $"{d.Value.Name} · {string.Join(", ", d.Value.Models.Take(3))}"; });
                R("activity history", () => $"{Activity.Read(1).Count} tasks today · automatic save {(Activity.AutoEvery == 0 ? "off" : "every " + Activity.AutoEvery + " h")}");
                R("Guardian", () => Guard.JobsLine());
                break;
            }
            default: throw new Exception("unknown tool " + tool);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ "NovaEnhance.exe --mcp": the MCP server Claude desktop starts

    static readonly (string Name, string Desc, string Schema)[] Tools =
    {
        ("status", "Nova Enhance status on this PC: build, email index (mailboxes, archives, oldest date), archive files, email projects, background jobs, AI in use.", "{\"type\":\"object\",\"properties\":{}}"),
        ("search", "Runs a real email search in Nova exactly like the Search for email window (AI search on). Returns what Nova understood, every step with timings, and the emails found (date, sent/received, project, subject, attachment names, why) — names hidden, never email bodies.",
            "{\"type\":\"object\",\"properties\":{\"question\":{\"type\":\"string\",\"description\":\"the question as the user would type it\"},\"project\":{\"type\":\"string\",\"description\":\"project button code, e.g. SLV (optional)\"}},\"required\":[\"question\"]}"),
        ("find_email", "Checks whether emails with these words (subject words or a document number such as SLV-ZUC-MS-SSM-00012) are in Nova's index at all — no AI, every folder, all projects.",
            "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}},\"required\":[\"text\"]}"),
        ("activity", "Nova's Activity history: summary plus the last tasks with their steps, timings and decisions (names hidden; AI texts left out).",
            "{\"type\":\"object\",\"properties\":{\"days\":{\"type\":\"integer\",\"description\":\"1 to 30, default 1\"},\"feature\":{\"type\":\"string\",\"description\":\"e.g. email search, conversation, ai call\"},\"limit\":{\"type\":\"integer\"}}}"),
        ("errors", "Nova's error log for the last hours, plus the last crash and freeze notes (names hidden).", "{\"type\":\"object\",\"properties\":{\"hours\":{\"type\":\"integer\",\"description\":\"default 24\"}}}"),
        ("selfcheck", "Runs Nova's self-checks: index, full-text and phrase search, Outlook, archive files, renamed archives, Gemini key, local AI, activity history, background jobs.", "{\"type\":\"object\",\"properties\":{}}")
    };

    /// <summary>One MCP JSON-RPC message → its answer (null for notifications). Used by Claude desktop (stdio) and claude.ai (internet).</summary>
    internal static async Task<JsonObject> Rpc(JsonObject m, Func<string, string, Task<(string Text, bool Err)>> call)
    {
        var id = m["id"]?.DeepClone();
        var method = m["method"]?.ToString() ?? "";
        if (id == null) return null;   // notifications
        JsonNode result = null; JsonObject error = null;
        try
        {
            switch (method)
            {
                case "initialize":
                    result = new JsonObject
                    {
                        ["protocolVersion"] = m["params"]?["protocolVersion"]?.ToString() ?? "2025-06-18",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "nova-enhance-test", ["version"] = Program.Build.ToString() },
                        ["instructions"] = "Test tools for Nova Enhance for Windows on the user's PC (by A.HAMROUNI). Names are hidden as Person N; email bodies are never given."
                    };
                    break;
                case "ping": result = new JsonObject(); break;
                case "tools/list":
                    result = new JsonObject { ["tools"] = new JsonArray(Tools.Select(t => (JsonNode)new JsonObject { ["name"] = "nova_" + t.Name, ["description"] = t.Desc, ["inputSchema"] = JsonNode.Parse(t.Schema) }).ToArray()) };
                    break;
                case "tools/call":
                {
                    var name = (m["params"]?["name"]?.ToString() ?? "").Replace("nova_", "");
                    var (text, err) = await call(name, m["params"]?["arguments"]?.ToJsonString() ?? "{}");
                    result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = err };
                    break;
                }
                default: error = new JsonObject { ["code"] = -32601, ["message"] = "Method not found: " + method }; break;
            }
        }
        catch (Exception e) { error = new JsonObject { ["code"] = -32603, ["message"] = e.Message }; }
        var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (error != null) resp["error"] = error; else resp["result"] = result;
        return resp;
    }

    public static int Mcp()
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(240) };
        string line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            JsonObject m;
            try { m = JsonNode.Parse(line)!.AsObject(); } catch { continue; }
            var resp = Rpc(m, async (name, args) =>
            {
                try
                {
                    Store.Load();
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}/tc/{name}") { Content = new StringContent(args, Encoding.UTF8, "application/json") };
                    req.Headers.Add("X-Nova-Key", Store.Str("win_tc_key", ""));
                    using var res = await http.SendAsync(req);
                    var o = JsonNode.Parse(await res.Content.ReadAsStringAsync())?.AsObject();
                    if (o?["error"] is { } er) return (er.ToString() == "forbidden" ? "Nova refused: the Test connector key changed — in Nova Settings → 🔌 Test connector press “Add to Claude desktop” again, then restart Claude desktop." : "Nova: " + er, true);
                    return (o?["text"]?.ToString() ?? "", false);
                }
                catch (HttpRequestException) { return ("Nova is not running on this PC, or Settings → 🔌 Test connector is off.", true); }
                catch (TaskCanceledException) { return ("Nova did not answer within 4 minutes.", true); }
            }).GetAwaiter().GetResult();
            if (resp != null) stdout.WriteLine(resp.ToJsonString());
        }
        return 0;
    }

    // ------------------------------------------------------------------ Claude desktop setup

    public static string Exe => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>The Claude desktop settings files on this PC (normal install and Microsoft Store install).</summary>
    public static List<string> ClaudeConfigs()
    {
        var res = new List<string>();
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        if (Directory.Exists(roaming)) res.Add(Path.Combine(roaming, "claude_desktop_config.json"));
        try
        {
            var pk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (Directory.Exists(pk))
                foreach (var d in Directory.GetDirectories(pk, "Claude_*"))
                {
                    var c = Path.Combine(d, "LocalCache", "Roaming", "Claude");
                    if (Directory.Exists(c)) res.Add(Path.Combine(c, "claude_desktop_config.json"));
                }
        }
        catch { }
        if (res.Count == 0) res.Add(Path.Combine(roaming, "claude_desktop_config.json"));
        return res;
    }

    public static string Snippet() => new JsonObject
    {
        ["mcpServers"] = new JsonObject { ["nova-enhance-test"] = new JsonObject { ["command"] = Exe, ["args"] = new JsonArray("--mcp") } }
    }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    /// <summary>Adds "nova-enhance-test" to Claude desktop's settings (a copy of the old file is kept as .bak).</summary>
    public static List<string> AddToClaude()
    {
        var done = new List<string>();
        foreach (var f in ClaudeConfigs())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            JsonObject o = new();
            if (File.Exists(f))
            {
                File.Copy(f, f + ".bak", true);
                try { o = JsonNode.Parse(File.ReadAllText(f))?.AsObject() ?? new JsonObject(); } catch { throw new Exception("Claude desktop's settings file could not be read — fix it or paste the setup by hand: " + f); }
            }
            if (o["mcpServers"] is not JsonObject servers) { servers = new JsonObject(); o["mcpServers"] = servers; }
            servers["nova-enhance-test"] = new JsonObject { ["command"] = Exe, ["args"] = new JsonArray("--mcp") };
            File.WriteAllText(f, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            done.Add(f);
        }
        return done;
    }
}
