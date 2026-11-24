using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>📋 Submittal log window: filters, numbers, the table (click = open the email), ⬇ Excel. Designed by A.HAMROUNI</summary>
class SubmittalForm : WebPage
{
    SubmittalLog.Result last;
    SubmittalLog.Options start, cur;
    bool exportNext;
    CancellationTokenSource cts;
    // 🧭 Nova agent in this window
    Agent.Session agSess;
    Agent.Result agLast;
    string agQ = "", agChat = "", openChat = "";
    CancellationTokenSource agCts;
    TaskCompletionSource<(List<long>, List<long>, string)?> agWait;

    public SubmittalForm(SubmittalLog.Options o = null, bool export = false, string chatId = "") : base("📋 " + Tracker.LogName, "submittal.html", 1500, 900) { start = o; exportNext = export; openChat = chatId ?? ""; }

    /// <summary>New choices from the search box (“only the overdue ones”, “now tower 2”, “export it”).</summary>
    public void Apply(SubmittalLog.Options o, bool export)
    {
        exportNext = export;
        if (!Ready) { start = o; return; }
        Post(new JsonObject { ["t"] = "opt", ["opt"] = OptJson(o) });
        Run(o);
    }

    static JsonArray A(IEnumerable<string> l) => new(l.Select(x => (JsonNode)x).ToArray());

    static JsonArray P(IEnumerable<(string Code, string Name)> l) => new(l.Select(x => (JsonNode)new JsonObject { ["code"] = x.Code, ["name"] = x.Name }).ToArray());

    /// <summary>Everything the page needs to draw its rows (and the ✎ Edit panels).</summary>
    static JsonObject Setup(SubmittalLog.Options o) => new()
    {
        ["t"] = "setup", ["projects"] = P(SubmittalLog.LogProjects()), ["allProjects"] = P(SubmittalLog.LogProjects(true)), ["types"] = A(SubmittalLog.Enabled()),
        ["allTypes"] = new JsonArray(SubmittalLog.Types.Select(t => (JsonNode)new JsonObject { ["code"] = t.Code, ["label"] = t.Label }).ToArray()),
        ["prefs"] = LogPrefs.Get(), ["allStatuses"] = A(LogPrefs.AllStatuses),
        ["builtin"] = Tracker.Builtin, ["us"] = Tracker.UsShort, ["name"] = Tracker.LogName, ["field"] = Packs.Current.Name,
        ["opt"] = OptJson(o), ["sign"] = Program.Sign, ["brain"] = Brain.Unlocked ? $"🧠 rules: Nova Brain v{Brain.Version}" : "🧠 rules: built-in (Nova Brain locked)"
    };

    protected override void OnMessage(JsonObject m)
    {
        switch (m["t"]?.ToString())
        {
            case "init":
            {
                var o = start ?? new SubmittalLog.Options();
                Post(Setup(o));
                Run(o);
                // ✚ New projects found in your emails (for the dashed chips and the ＋ panel) — after the log, quietly.
                // 📋 Opened from Nova Chat (“Show in the log”): the same conversation, here.
                if (openChat.Length > 0)
                {
                    agChat = openChat; agSess = Agent.SessionFor(agChat, null);
                    Post(new JsonObject { ["t"] = "ag.load", ["turns"] = NovaChat.AgentTurns(agChat) });
                }
                _ = Task.Run(() => { try { Thread.Sleep(1500); Post(ProjectFinder.Handle(new JsonObject { ["t"] = "pf.load" })); } catch (Exception e) { Log.Warn("projects found", e.Message); } });
                break;
            }
            case "prefs.save":
            {
                // ✎ Edit: your choices go to My log settings, then the chips and the log are drawn again.
                if (m["prefs"] is JsonObject p) LogPrefs.Save(p.DeepClone().AsObject());
                var o = cur ?? new SubmittalLog.Options();
                var hidden = LogPrefs.HiddenProjects;
                o.Projects.RemoveAll(x => hidden.Contains(x));
                o.Types.RemoveAll(x => LogPrefs.HiddenTypes.Contains(x));
                Post(Setup(o));
                Run(o);
                Post(new JsonObject { ["t"] = "busy", ["text"] = "✓ Saved in My log settings (also on the phone after the next update, and in 🧠 Nova Brain · My log settings)" });
                break;
            }
            case "projectsWin": Screens.ProjectsFound(); break;
            case "setup": Post(Setup(cur ?? new SubmittalLog.Options())); break;
            case "pf.load" or "pf.add" or "pf.merge" or "pf.ignore":
            {
                var msg = m;
                _ = Task.Run(() => { try { Post(ProjectFinder.Handle(msg)); } catch (Exception e) { Post(new JsonObject { ["t"] = "pf.list", ["said"] = "⚠ " + e.Message }); } });
                break;
            }
            case "run": Run(OptOf(m["opt"] as JsonObject)); break;
            case "ask":
            {
                // 🧭 A follow-up typed in the log window.
                var it = Intent.Read(m["text"]?.ToString() ?? "", cur?.Projects.FirstOrDefault() ?? "", cur ?? new SubmittalLog.Options());
                // 🧭 Anything that is not a quick log choice goes to the Nova agent (AI + your Brain + the log + emails + threads).
                if (it.Log == null || Agent.Wanted(m["text"]?.ToString() ?? "")) { Post(new JsonObject { ["t"] = "ag.start", ["text"] = m["text"]?.ToString() ?? "" }); return; }
                exportNext = it.Export;
                Post(new JsonObject { ["t"] = "opt", ["opt"] = OptJson(it.Log), ["said"] = it.Said });
                Run(it.Log);
                break;
            }
            case "stop": cts?.Cancel(); break;
            case "ag.ask": AgentAsk(m["text"]?.ToString() ?? ""); break;
            case "ag.confirmed":
            {
                List<long> Ids(string k) => (m[k] as JsonArray)?.Select(x => long.TryParse(x?.ToString(), out var v) ? v : 0).Where(v => v > 0).ToList() ?? new();
                agWait?.TrySetResult((Ids("yes"), Ids("no"), m["note"]?.ToString() ?? ""));
                break;
            }
            case "ag.stop": agWait?.TrySetResult(null); agCts?.Cancel(); break;
            case "ag.reset": agSess = null; agLast = null; agChat = ""; agQ = ""; break;
            case "ag.csv":
                if (agLast != null) { var path = Agent.Csv(agLast, agQ); try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { } Post(new JsonObject { ["t"] = "saved", ["path"] = path }); }
                break;
            case "ag.chat": if (agChat.Length > 0) Screens.HubOpenChat(agChat); else Screens.Hub(agQ); break;   // 💬 the same conversation, with everything
            case "ag.prompt": Prompts.AskSave(this, new SavedPrompt { Name = agQ.Length > 50 ? agQ[..50] : agQ, Question = agQ, Where = "ask" }); break;
            case "open":
                NovaChat.OpenSource(new JsonObject { ["kind"] = "email", ["entry"] = m["entry"]?.ToString(), ["store"] = m["store"]?.ToString() }, m["ctrl"]?.ToString() is "true" or "True");
                break;
            case "excel":
                if (last == null) return;
                Excel(last);
                break;

            case "folder":
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Prompts.Folder) { UseShellExecute = true }); } catch { }
                break;
            case "brain": Screens.Brain(); break;
        }
    }

    async void AgentAsk(string text)
    {
        if (text.Trim().Length == 0) return;
        agQ = agQ.Length == 0 ? text : agQ;
        agCts?.Cancel(); agCts = new CancellationTokenSource();
        var ct = agCts.Token;
        if (agChat.Length == 0) agChat = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Random.Shared.Next(100, 999);
        agSess ??= Agent.SessionFor(agChat, null);
        var host = new Agent.Host
        {
            Progress = s => Post(new JsonObject { ["t"] = "ag.step", ["text"] = s }),
            Detail = s => Post(new JsonObject { ["t"] = "ag.detail", ["text"] = s }),
            Confirm = async (q, ex) =>
            {
                agWait = new TaskCompletionSource<(List<long>, List<long>, string)?>();
                Post(new JsonObject { ["t"] = "ag.confirm", ["question"] = q, ["items"] = Agent.Examples(ex) });
                return await agWait.Task;
            }
        };
        using var busy = Busy.Begin("🧭 Nova agent · " + (text.Length > 50 ? text[..50] + "…" : text));
        try
        {
            var r = await Agent.Run(text, agSess, host, "log", ct);
            agLast = r;
            agChat = NovaChat.SaveAgentTurn(agChat, text, r);   // kept as a Nova Chat conversation (💬 Continue opens it)
            Post(new JsonObject { ["t"] = "ag.result", ["result"] = Agent.Json(r) });
        }
        catch (OperationCanceledException) { Post(new JsonObject { ["t"] = "ag.error", ["text"] = "Stopped." }); }
        catch (Exception e) { Log.Warn("agent", e.Message); Post(new JsonObject { ["t"] = "ag.error", ["text"] = e.Message }); }
    }

    static JsonObject OptJson(SubmittalLog.Options o) => new()
    {
        ["projects"] = A(o.Projects), ["types"] = A(o.Types), ["from"] = o.From.ToString("yyyy-MM-dd"), ["to"] = o.To.ToString("yyyy-MM-dd"),
        ["tower"] = o.Tower, ["zone"] = o.Zone, ["disc"] = o.Discipline, ["status"] = o.Status,
        ["lfrom"] = o.LevelFrom > -99 ? (o.LevelFrom == 0 ? "GF" : o.LevelFrom.ToString()) : "", ["lto"] = o.LevelTo < 999 && o.LevelTo != o.LevelFrom ? (o.LevelTo == 0 ? "GF" : o.LevelTo.ToString()) : ""
    };

    static SubmittalLog.Options OptOf(JsonObject j)
    {
        var o = new SubmittalLog.Options();
        if (j == null) return o;
        List<string> L(string k) => (j[k] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? new();
        o.Projects = L("projects"); o.Types = L("types");
        if (DateTime.TryParse(j["from"]?.ToString(), out var f)) o.From = f;
        if (DateTime.TryParse(j["to"]?.ToString(), out var t)) o.To = t.Date.AddDays(1).AddSeconds(-1);
        o.Tower = j["tower"]?.ToString() ?? ""; o.Zone = j["zone"]?.ToString() ?? ""; o.Discipline = j["disc"]?.ToString() ?? ""; o.Status = j["status"]?.ToString() ?? "";
        if (SubmittalLog.LevelOf(j["lfrom"]?.ToString()) is int a) o.LevelFrom = a;     // 5 · L5 · GF · FF · B1 · roof
        if (SubmittalLog.LevelOf(j["lto"]?.ToString()) is int b) o.LevelTo = b;
        if (o.LevelFrom > -99 && o.LevelTo == 999) o.LevelTo = o.LevelFrom;
        return o;
    }

    void Excel(SubmittalLog.Result r)
    {
        Work("⬇ Writing the Excel log…", () =>
        {
            var title = (r.Opt.Types.Count > 0 && r.Opt.Types.Count <= 4 ? string.Join("-", r.Opt.Types) : "Submittal") + " log " + (r.Opt.Projects.Count > 0 ? string.Join("+", r.Opt.Projects) : "all projects");
            var path = SubmittalLog.Excel(r, title);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
            return new JsonObject { ["t"] = "saved", ["path"] = path };
        });
    }

    void Run(SubmittalLog.Options o)
    {
        cur = o;
        cts?.Cancel();
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        Work(Tracker.Builtin ? $"📋 Reading every submission and its {Tracker.UsShort} reply in your emails…" : "📋 Reading every item of your field and its reply in your emails…", () =>
        {
            var r = SubmittalLog.Run(o, null, ct);
            last = r;
            if (exportNext) { exportNext = false; BeginInvoke(new Action(() => Excel(r))); }
            return new JsonObject
            {
                ["t"] = "result", ["kpi"] = SubmittalLog.Kpis(r),
                ["items"] = new JsonArray(r.Items.Take(3000).Select(i => (JsonNode)SubmittalLog.Json(i)).ToArray()),
                ["towers"] = A(r.Items.Select(i => i.Tower).Where(x => x.Length > 0 && !x.StartsWith("TH")).Distinct().OrderBy(x => x)),
                ["discs"] = A(r.Items.Select(i => i.Disc).Where(x => x.Length > 0).Distinct().OrderBy(x => x)),
                ["discCounts"] = new JsonObject(r.Discs.Where(x => x.Key.Length > 0).Select(x => KeyValuePair.Create(x.Key, (JsonNode)x.Value))),
                ["statCounts"] = new JsonObject(r.Statuses.Select(x => KeyValuePair.Create(x.Key, (JsonNode)x.Value))),
                ["projects"] = A(r.Items.Select(i => i.Project).Where(x => x.Length > 0).Distinct().OrderBy(x => x))
            };
        });
    }
}
