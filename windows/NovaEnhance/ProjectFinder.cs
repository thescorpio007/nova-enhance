using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// ＋ Projects found in your emails: document-number prefixes, Outlook folders and subject tags in the emails no project claims yet
/// (unsorted or GEN) — each one can be ✚ added as a project, ⇢ merged into one of yours (a new code, folder or name of it) or ✗ ignored.
/// What you add or merge is used at once by email sorting, the logs, the intents and the phone. Designed by A.HAMROUNI
/// </summary>
static class ProjectFinder
{
    public class Sug
    {
        public string Kind = "", Value = "", Sample = "", Target = "", Code = "";
        public int Emails;
        public JsonObject Json() => new()
        {
            ["kind"] = Kind, ["value"] = Value, ["sample"] = Sample, ["emails"] = Emails, ["target"] = Target, ["code"] = Code,
            ["targetName"] = Target.Length > 0 ? Projects.All().FirstOrDefault(p => p.Code == Target)?.Name ?? "" : ""
        };
    }

    public static List<string> Ignored
    {
        get { try { return JsonSerializer.Deserialize<List<string>>(Store.Str("win_proj_ignored", "[]")) ?? new(); } catch { return new(); } }
        set => Store.Set("win_proj_ignored", JsonSerializer.Serialize(value ?? new()));
    }

    static readonly Regex DocLike = new(@"(?<![A-Za-z0-9])([A-Z][A-Z0-9]{1,6})-[A-Z0-9]{1,6}-[A-Z0-9\-_]{2,}");
    static readonly Regex Tag = new(@"^\s*(?:(?:re|fw|fwd|aw|tr)\s*:\s*|\[external\]\s*)*(?:\[([^\]]{3,40})\]|([^|:\[\]]{3,40}?)\s*\|)", RegexOptions.IgnoreCase);
    static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "inbox", "sent items", "sent", "deleted items", "drafts", "outbox", "junk", "junk email", "archive", "archives", "conversation history", "rss feeds",
        "sync issues", "conflicts", "local failures", "server failures", "notes", "tasks", "calendar", "contacts", "journal", "search folders", "boîte de réception", "éléments envoyés"
    };
    static readonly HashSet<string> NotProjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "RE", "FW", "FWD", "ISO", "BS", "EN", "ASTM", "UAE", "AD", "PDF", "DWG", "REV", "REF", "NO", "COM", "GEN", "INV", "PO", "TKT", "INC", "REQ", "CR", "NCR", "RFI", "WIR", "MIR", "MAR", "SDS", "MSS", "LTR", "MOM", "EI"
    };

    static string CodeFrom(string text)
    {
        var w = Regex.Matches(text ?? "", @"[A-Za-z][A-Za-z0-9]*").Select(m => m.Value).ToList();
        if (w.Count == 0) return "NEW";
        var c = w[0].Length >= 3 ? w[0] : string.Concat(w.Take(4).Select(x => x[0]));
        c = c.ToUpperInvariant();
        return c.Length > 6 ? c[..6] : c;
    }

    /// <summary>The project whose name a code is written from (“SMNRL” → Saadiyat Marina Nouran Living).</summary>
    static string Acronym(string code, List<Project> all)
    {
        code = Regex.Replace(code ?? "", "[^A-Za-z]", "").ToLowerInvariant();
        if (code.Length < 3) return "";
        foreach (var p in all)
            foreach (var n in Projects.NamesOf(p).Where(n => n.Contains(' ')))
            {
                var name = n.ToLowerInvariant();
                if (name[0] != code[0]) continue;
                int i = 0;
                foreach (var ch in name) if (i < code.Length && ch == code[i]) i++;
                var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x[0]));
                if (i == code.Length && initials.Count(ch => code.Contains(ch)) >= Math.Min(3, initials.Length)) return p.Code;
            }
        return "";
    }

    public static List<Sug> Find(int max = 12)
    {
        MailProjects.Ensure();
        var all = Projects.All().Where(p => p.Code.Length > 0).ToList();
        var known = new HashSet<string>(all.SelectMany(Projects.NamesOf), StringComparer.OrdinalIgnoreCase) { MailProjects.Gen, Tracker.UsShort };
        var ign = Ignored.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var linked = MailProjects.Config.Values.SelectMany(c => c.Folders).Where(f => f.Trim().Length > 0).ToList();
        var rows = new List<(string Subj, string Att, string Folder, string From, bool Sent)>();
        var domainProj = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        using (var c = MailIndex.Open())
        {
            using (var bt = c.CreateCommand()) { bt.CommandText = "PRAGMA busy_timeout=20000;"; bt.ExecuteNonQuery(); }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT IFNULL(m.subject,''), IFNULL(m.att_names,''), IFNULL(m.folder,''), lower(IFNULL(m.from_addr,'')), m.sent FROM mail m LEFT JOIN mproj p ON p.id=m.id WHERE IFNULL(p.proj,'') IN ('', $g) ORDER BY m.date DESC LIMIT 25000";
                cmd.Parameters.AddWithValue("$g", MailProjects.Gen);
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) == 1));
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT substr(lower(m.from_addr), instr(m.from_addr,'@')), p.proj, COUNT(*) FROM mail m JOIN mproj p ON p.id=m.id WHERE p.proj NOT IN ('', $g) AND m.sent=0 AND instr(m.from_addr,'@')>0 GROUP BY 1,2";
                cmd.Parameters.AddWithValue("$g", MailProjects.Gen);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!domainProj.TryGetValue(r.GetString(0), out var d)) domainProj[r.GetString(0)] = d = new();
                    d[r.GetString(1)] = r.GetInt32(2);
                }
            }
        }
        var mine = Me.Addrs.Where(a => a.Contains('@')).Select(a => a[a.IndexOf('@')..].ToLowerInvariant()).ToHashSet();
        var found = new Dictionary<string, (Sug S, Dictionary<string, int> Domains)>(StringComparer.OrdinalIgnoreCase);
        void Note(string kind, string value, string sample, string from)
        {
            value = value.Trim();
            if (value.Length < 2 || ign.Contains(value) || known.Contains(value)) return;
            var k = kind + "|" + value;
            if (!found.TryGetValue(k, out var x)) found[k] = x = (new Sug { Kind = kind, Value = value, Sample = sample }, new(StringComparer.OrdinalIgnoreCase));
            x.S.Emails++;
            var at = from.IndexOf('@');
            if (at > 0) { var d = from[at..]; if (!mine.Contains(d)) x.Domains[d] = x.Domains.GetValueOrDefault(d) + 1; }
        }
        foreach (var (subj, att, folder, from, sent) in rows)
        {
            foreach (var pre in DocLike.Matches(subj + " " + att).Select(m => (m.Groups[1].Value, m.Value)).DistinctBy(x => x.Item1.ToUpperInvariant()))
            {
                var p = pre.Item1.ToUpperInvariant();
                if (NotProjects.Contains(p) || !p.Any(char.IsLetter) || all.Any(pr => pr.Codes.Append(pr.Code).Any(cd => cd.StartsWith(p + "-", StringComparison.OrdinalIgnoreCase)))) continue;
                Note("prefix", p, pre.Item2, sent ? "" : from);
            }
            if (folder.Length > 0)
            {
                var last = folder.TrimEnd('\\', '/').Split('\\').Last().Trim();
                if (last.Length >= 3 && !Common.Contains(last) && !linked.Any(f => folder.Contains(f, StringComparison.OrdinalIgnoreCase))) Note("folder", last, folder, sent ? "" : from);
            }
            var tm = Tag.Match(subj);
            if (tm.Success)
            {
                var t = (tm.Groups[1].Success ? tm.Groups[1].Value : tm.Groups[2].Value).Trim();
                if (t.Length >= 3 && t.Any(char.IsLetter) && !DocLike.IsMatch(t) && Projects.Mentioned(t).Count == 0 && !Regex.IsMatch(t, @"^(external|ext|spam|important|urgent|reminder|action required|info)$", RegexOptions.IgnoreCase))
                    Note("tag", t, subj, sent ? "" : from);
            }
        }
        var res = new List<Sug>();
        var codes = new HashSet<string>(all.Select(p => p.Code), StringComparer.OrdinalIgnoreCase);
        foreach (var (s, domains) in found.Values)
        {
            if (s.Emails < (s.Kind == "prefix" ? 3 : 5)) continue;
            // ⇢ A project of yours it may belong to: the code is written from its name, or its senders write about one project of yours.
            s.Target = s.Kind == "prefix" ? Acronym(s.Value, all) : "";
            if (s.Target.Length == 0 && domains.Count > 0)
            {
                var tally = new Dictionary<string, double>();
                foreach (var (d, n) in domains)
                    if (domainProj.TryGetValue(d, out var pm))
                    {
                        var tot = (double)pm.Values.Sum();
                        foreach (var (pj, k) in pm) tally[pj] = tally.GetValueOrDefault(pj) + n * k / tot;
                    }
                var sum = tally.Values.Sum();
                if (sum > 0) { var top = tally.OrderByDescending(x => x.Value).First(); if (top.Value / sum >= 0.6 && sum >= s.Emails * 0.4) s.Target = top.Key; }
            }
            var code = s.Kind == "prefix" ? s.Value : CodeFrom(s.Value);
            var c0 = code; int i = 2;
            while (codes.Contains(code)) code = c0 + i++;
            s.Code = code;
            res.Add(s);
        }
        return res.OrderByDescending(s => s.Kind == "prefix").ThenByDescending(s => s.Emails).Take(max).ToList();
    }

    /// <summary>✚ A new project from a suggestion.</summary>
    public static string Add(string kind, string value, string code, string name)
    {
        code = Regex.Replace((code ?? "").Trim(), @"[^\p{L}\p{N}\-_]", "");
        if (code.Length < 2) return "⚠ Give the project a code (2 letters or more).";
        var all = Projects.All();
        if (all.Any(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase))) return $"⚠ {code} is already one of your projects — use ⇢ Merge.";
        var p = new Project { Code = code, Name = (name ?? "").Trim().Length > 0 ? name.Trim() : value, Order = all.Count == 0 ? 0 : all.Max(x => x.Order) + 1 };
        if (kind == "prefix" && !value.Equals(code, StringComparison.OrdinalIgnoreCase)) p.Codes.Add(value);
        all.Add(p);
        Projects.SaveAll(all);
        Link(kind, value, code);
        return $"✚ {code} added — Nova is sorting its emails now.";
    }

    /// <summary>⇢ A suggestion is one of your projects: its prefix becomes a code of it, its folder or tag one of its names.</summary>
    public static string Merge(string kind, string value, string target)
    {
        var all = Projects.All();
        var p = all.FirstOrDefault(x => x.Code.Equals(target ?? "", StringComparison.OrdinalIgnoreCase));
        if (p == null) return "⚠ Choose one of your projects.";
        if (kind == "prefix" && !p.Codes.Contains(value, StringComparer.OrdinalIgnoreCase)) { p.Codes.Add(value); Projects.SaveAll(all); }
        Link(kind, value, p.Code);
        return $"⇢ {value} is now part of {p.Code} — Nova is sorting its emails now.";
    }

    static void Link(string kind, string value, string code)
    {
        if (kind is "folder" or "tag")
        {
            var cfg = MailProjects.CfgOf(code);
            var list = kind == "folder" ? cfg.Folders : cfg.Names;
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
            MailProjects.SaveCfg(code, cfg);
        }
        MailProjects.ResortUnsorted();
    }

    public static string Ignore(string value)
    {
        var l = Ignored;
        if (!l.Contains(value, StringComparer.OrdinalIgnoreCase)) l.Add(value);
        Ignored = l;
        return $"✗ {value} will not be suggested again.";
    }

    public static JsonArray ProjectsJson() =>
        new(Projects.Known().Select(x => (JsonNode)new JsonObject { ["code"] = x.Code, ["name"] = x.Name }).ToArray());

    /// <summary>The messages of the ＋ panel (the same in the Submittal log, the Projects window and the phone).</summary>
    public static JsonObject Handle(JsonObject m)
    {
        string S(string k) => m[k]?.ToString() ?? "";
        string said = S("t") switch
        {
            "pf.add" => Add(S("kind"), S("value"), S("code"), S("name")),
            "pf.merge" => Merge(S("kind"), S("value"), S("target")),
            "pf.ignore" => Ignore(S("value")),
            _ => ""
        };
        return new JsonObject
        {
            ["t"] = "pf.list", ["said"] = said, ["items"] = new JsonArray(Find().Select(s => (JsonNode)s.Json()).ToArray()), ["projects"] = ProjectsJson()
        };
    }
}
