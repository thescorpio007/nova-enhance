using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>📋 Submittal log window: filters, numbers, the table (click = open the email), ⬇ Excel. Designed by A.HAMROUNI</summary>
class SubmittalForm : WebPage
{
    SubmittalLog.Result last;
    SubmittalLog.Options start, cur;
    bool exportNext;
    CancellationTokenSource cts;

    public SubmittalForm(SubmittalLog.Options o = null, bool export = false) : base("📋 Submittal log", "submittal.html", 1500, 900) { start = o; exportNext = export; }

    /// <summary>New choices from the search box (“only the overdue ones”, “now tower 2”, “export it”).</summary>
    public void Apply(SubmittalLog.Options o, bool export)
    {
        exportNext = export;
        if (!Ready) { start = o; return; }
        Post(new JsonObject { ["t"] = "opt", ["opt"] = OptJson(o) });
        Run(o);
    }

    static JsonArray A(IEnumerable<string> l) => new(l.Select(x => (JsonNode)x).ToArray());

    protected override void OnMessage(JsonObject m)
    {
        switch (m["t"]?.ToString())
        {
            case "init":
            {
                var o = start ?? new SubmittalLog.Options();
                var projects = new List<string>();
                try { projects = MailProjects.Counts.OrderByDescending(x => x.Value).Select(x => x.Key).Where(k => k.Length > 0).Take(10).ToList(); } catch { }
                Post(new JsonObject
                {
                    ["t"] = "setup", ["projects"] = A(projects), ["types"] = A(SubmittalLog.Enabled()),
                    ["opt"] = OptJson(o), ["sign"] = Program.Sign, ["brain"] = Brain.Unlocked ? $"🧠 rules: Nova Brain v{Brain.Version}" : "🧠 rules: built-in (Nova Brain locked)"
                });
                Run(o);
                break;
            }
            case "run": Run(OptOf(m["opt"] as JsonObject)); break;
            case "ask":
            {
                // 🧭 A follow-up typed in the log window.
                var it = Intent.Read(m["text"]?.ToString() ?? "", cur?.Projects.FirstOrDefault() ?? "", cur ?? new SubmittalLog.Options());
                if (it.Log == null) { Post(new JsonObject { ["t"] = "error", ["text"] = "Not a log request — try “only the overdue ones”, “now tower 2”, “EI this month”, “export it”" }); return; }
                exportNext = it.Export;
                Post(new JsonObject { ["t"] = "opt", ["opt"] = OptJson(it.Log), ["said"] = it.Said });
                Run(it.Log);
                break;
            }
            case "stop": cts?.Cancel(); break;
            case "open":
                NovaChat.OpenSource(new JsonObject { ["kind"] = "email", ["entry"] = m["entry"]?.ToString(), ["store"] = m["store"]?.ToString() });
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

    static JsonObject OptJson(SubmittalLog.Options o) => new()
    {
        ["projects"] = A(o.Projects), ["types"] = A(o.Types), ["from"] = o.From.ToString("yyyy-MM-dd"), ["to"] = o.To.ToString("yyyy-MM-dd"),
        ["tower"] = o.Tower, ["zone"] = o.Zone, ["disc"] = o.Discipline, ["status"] = o.Status,
        ["lfrom"] = o.LevelFrom > -99 ? o.LevelFrom.ToString() : "", ["lto"] = o.LevelTo < 999 ? o.LevelTo.ToString() : ""
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
        if (int.TryParse(j["lfrom"]?.ToString(), out var a)) o.LevelFrom = a;
        if (int.TryParse(j["lto"]?.ToString(), out var b)) o.LevelTo = b;
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
        Work("📋 Reading every submission and its MZA reply in your emails…", () =>
        {
            var r = SubmittalLog.Run(o, null, ct);
            last = r;
            if (exportNext) { exportNext = false; BeginInvoke(new Action(() => Excel(r))); }
            return new JsonObject
            {
                ["t"] = "result", ["kpi"] = SubmittalLog.Kpis(r),
                ["items"] = new JsonArray(r.Items.Take(3000).Select(i => (JsonNode)SubmittalLog.Json(i)).ToArray()),
                ["towers"] = A(r.Items.Select(i => i.Tower).Where(x => x.Length > 0 && !x.StartsWith("TH")).Distinct().OrderBy(x => x)),
                ["discs"] = A(r.Items.Select(i => i.Disc).Where(x => x.Length > 0).Distinct().OrderBy(x => x))
            };
        });
    }
}
