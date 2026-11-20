using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>📁 Your projects (code, full name, aliases — ✎ rename) and ＋ Projects found in your emails. Designed by A.HAMROUNI</summary>
class ProjectsForm : WebPage
{
    public ProjectsForm() : base("📁 Projects", "projects.html", 1180, 780) { }

    JsonObject Rows(string said = "") => new()
    {
        ["t"] = "pr.list", ["said"] = said, ["sign"] = Program.Sign,
        ["list"] = new JsonArray(Projects.All().Where(p => p.Code.Length > 0).OrderBy(p => p.Order).Select(p => (JsonNode)new JsonObject
        {
            ["code"] = p.Code, ["name"] = p.Name, ["client"] = p.Client, ["emails"] = MailProjects.CountOf(p.Code),
            ["aliases"] = new JsonArray(Projects.NamesOf(p).Where(n => n != p.Code && n != p.Name).Select(n => (JsonNode)n).ToArray())
        }).ToArray())
    };

    protected override void OnMessage(JsonObject m)
    {
        switch (m["t"]?.ToString())
        {
            case "init": Post(Rows()); break;
            case "pr.list": Post(Rows()); break;
            case "pr.rename":
            {
                string old = m["old"]?.ToString() ?? "", code = m["code"]?.ToString()?.Trim() ?? "", name = m["name"]?.ToString() ?? "", client = m["client"]?.ToString()?.Trim() ?? "";
                _ = Task.Run(() =>
                {
                    var ok = code.Length >= 2 && Projects.Rename(old, code, name);
                    if (ok)
                    {
                        // 🧭 The client on this project (name · email domain) — the Nova agent uses it (“the client”, “Charbel”).
                        var all = Projects.All();
                        var pp = all.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
                        if (pp != null && pp.Client != client) { pp.Client = client; Projects.SaveAll(all); }
                    }
                    Post(Rows(ok ? (old == code ? $"✓ {code} saved" : $"✓ {old} is now {code} — its emails, corrections and rules follow; “{old}” stays as an alias") : $"⚠ {code} can't be used (empty, or another project has it)"));
                });
                break;
            }
            case "pf.load" or "pf.add" or "pf.merge" or "pf.ignore":
            {
                var msg = m;
                _ = Task.Run(() => { try { Post(ProjectFinder.Handle(msg)); } catch (Exception e) { Post(new JsonObject { ["t"] = "pf.list", ["said"] = "⚠ " + e.Message }); } });
                break;
            }
        }
    }
}
