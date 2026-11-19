using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🧭 What you mean, before any search: a group of emails (“the unsorted ones”, “unread”, “waiting for my reply”), a log or register
/// (“which WIRs in tower 4 are still open?”, “EI log this month”), a window (“open the brain”), a follow-up on the last log
/// (“only the overdue ones”, “now tower 2”, “export it”) — or a normal search. No AI needed: about a millisecond.
/// The phrases live in the 🧠 Nova Brain, section 🧭 What I mean. Designed by A.HAMROUNI
/// </summary>
static class Intent
{
    public enum Kind { Search, Group, Log, Follow, Open }

    public class Result
    {
        public Kind Kind = Kind.Search;
        public string Group = "", Window = "", Rest = "", Said = "";
        public SubmittalLog.Options Log;
        public bool Export;
    }

    static readonly string[] Stop = { "show", "me", "the", "all", "my", "ones", "one", "emails", "email", "mails", "mail", "list", "give", "please", "find", "get", "of", "in", "for", "a", "an", "are", "is", "which", "what", "that", "those", "these", "to", "from", "and", "with", "only", "now", "just", "messages", "message", "see", "open", "display" };

    static bool Has(string q, string key, string def)
    {
        foreach (var p in Brain.ListOf(key, def))
            if (p.Length > 1 && Regex.IsMatch(q, @"(?<![\p{L}\p{N}])" + Regex.Escape(p) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    static string Without(string q, string key, string def)
    {
        foreach (var p in Brain.ListOf(key, def)) q = Regex.Replace(q, @"(?<![\p{L}\p{N}])" + Regex.Escape(p) + @"(?![\p{L}\p{N}])", " ", RegexOptions.IgnoreCase);
        var words = Regex.Split(q, @"[^\p{L}\p{N}\-]+").Where(w => w.Length > 1 && !Stop.Contains(w.ToLowerInvariant())).ToList();
        return string.Join(" ", words);
    }

    const string DUnsorted = "unsorted, not sorted, no project, without project, unassigned";
    const string DUnread = "unread, not read, new emails";
    const string DWaiting = "waiting for my reply, need my reply, needs my reply, not answered, unanswered, to reply, I didn't answer";
    const string DNoAnswer = "no answer to me, no reply to me, they didn't reply, nobody replied, follow up";
    const string DPending = "pending, outstanding, still open, not closed, waiting";

    /// <summary>What the text asks for. <paramref name="lastLog"/>: the log shown just before (for follow-ups).</summary>
    public static Result Read(string q, string project, SubmittalLog.Options lastLog)
    {
        q = (q ?? "").Trim();
        var r = new Result();
        if (q.Length == 0) return r;
        // Windows by name.
        foreach (var (key, def, win, said) in new[]
        {
            ("intent.brain", "open the brain, nova brain, my rules", "brain", "🧠 Opening Nova Brain"),
            ("intent.history", "search history, my searches, my history", "history", "🕘 Opening your search history"),
            ("intent.settings", "open settings, settings", "settings", "⚙ Opening Settings"),
            ("intent.log", "submittal log, open the log", "log", "📋 Opening the Submittal log")
        })
            if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5 && Has(q, key, def)) { r.Kind = Kind.Open; r.Window = win; r.Said = said; if (win == "log") r.Log = SubmittalLog.FromWords(q, project); return r; }

        // A follow-up on the last log: short, no new type, a filter word.
        // Projects by code, old code or name (“only RS5” → RS5-C7, “Nouran”, “Khalifa City School”).
        var projs = Projects.Mentioned(q);
        foreach (var p in SubmittalLog.KnownProjects())
            if (!projs.Contains(p, StringComparer.OrdinalIgnoreCase) && Regex.IsMatch(q, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(p)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase)) projs.Add(p);
        bool allProj = Regex.IsMatch(q, @"\b(all|every|any) projects?\b", RegexOptions.IgnoreCase);
        if (lastLog != null && q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 10 && !SubmittalLog.TypeWords.Any(t => t.Rx.IsMatch(q))
            && (projs.Count > 0 || allProj || Regex.IsMatch(q, @"\b(only|now|just|instead|also|filter|export|excel|overdue|closed|open|pending|tower|zone|level|floor|townhouses?|this (week|month)|last (week|month|\d+ days)|since|from|between|discipline|code)\b|\bT\d\b|\bL\d{1,2}\b", RegexOptions.IgnoreCase)))
        {
            var o = lastLog.Copy();
            if (projs.Count > 0) o.Projects = projs;
            if (allProj) o.Projects = new();
            var n = SubmittalLog.FromWords(q, project);
            if (n.Tower.Length > 0) o.Tower = n.Tower;
            if (n.Zone.Length > 0) o.Zone = n.Zone;
            if (n.LevelFrom > -99) { o.LevelFrom = n.LevelFrom; o.LevelTo = n.LevelTo; }
            if (n.Status.Length > 0) o.Status = n.Status;
            if (Has(q, "intent.pending", DPending)) o.Status = "Open";
            if (Regex.IsMatch(q, @"all (towers|statuses)|any tower|every tower", RegexOptions.IgnoreCase)) o.Tower = "";
            if (Regex.IsMatch(q, @"this (week|month)|last (week|month|\d+ days)|since|between|\d{1,2}\s*(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)", RegexOptions.IgnoreCase)) { o.From = n.From; o.To = n.To; }
            if (Regex.Match(q, @"\b(AR|CW|ES|MS|PLB|ID|LS|MEP|ST|HSE)\b") is { Success: true } d) o.Discipline = d.Value;
            r.Kind = Kind.Follow; r.Log = o; r.Export = Regex.IsMatch(q, @"\b(export|excel|xlsx|download)\b", RegexOptions.IgnoreCase);
            r.Said = "📋 " + Describe(o) + (r.Export ? " · ⬇ Excel" : "");
            return r;
        }

        // A log or a register.
        if (SubmittalLog.Wanted(q))
        {
            var o = SubmittalLog.FromWords(q, project);
            if (Has(q, "intent.pending", DPending)) o.Status = "Open";
            r.Kind = Kind.Log; r.Log = o; r.Export = Regex.IsMatch(q, @"\b(export|excel|xlsx)\b", RegexOptions.IgnoreCase);
            r.Said = "📋 " + Describe(o);
            return r;
        }

        // A group of emails.
        foreach (var (key, def, group, said) in new[]
        {
            ("intent.unsorted", DUnsorted, "unsorted", "❔ Emails not sorted into a project"),
            ("intent.waiting", DWaiting, "waiting", "⏳ Emails waiting for your reply"),
            ("intent.noanswer", DNoAnswer, "noanswer", "📤 Your emails nobody answered"),
            ("intent.unread", DUnread, "unread", "✉ Unread emails")
        })
            if (Has(q, key, def))
            {
                r.Kind = Kind.Group; r.Group = group; r.Rest = Without(q, key, def);
                r.Said = said + (r.Rest.Length > 0 ? " · with “" + r.Rest + "”" : "");
                return r;
            }
        return r;
    }

    public static string Describe(SubmittalLog.Options o)
    {
        var parts = new List<string> { o.Types.Count > 0 ? string.Join(", ", o.Types) : "all types" };
        if (o.Projects.Count > 0) parts.Add(string.Join("+", o.Projects));
        if (o.Tower.Length > 0) parts.Add(o.Tower);
        if (o.Zone.Length > 0) parts.Add(o.Zone);
        if (o.LevelFrom > -99) parts.Add(o.LevelFrom == o.LevelTo ? "L" + o.LevelFrom : $"L{o.LevelFrom}–{o.LevelTo}");
        if (o.Discipline.Length > 0) parts.Add(o.Discipline);
        if (o.Status.Length > 0) parts.Add(o.Status.ToLowerInvariant());
        parts.Add($"{o.From:dd MMM} – {o.To:dd MMM yyyy}");
        return string.Join(" · ", parts);
    }

    /// <summary>The emails of a group, at once (no AI).</summary>
    public static List<MailHit> GroupHits(Result r, string project)
    {
        var q = new MailQuery { Limit = 500, Project = r.Group == "unsorted" ? "?" : project ?? "" };
        if (r.Group == "unread") q.Unread = true;
        if (r.Group == "waiting") q.Unanswered = true;
        if (r.Group == "noanswer") q.FollowUp = true;
        if (r.Rest.Length > 0) q.Words = r.Rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (r.Group is "waiting" or "noanswer") q.After = DateTime.Today.AddDays(-60);
        var list = MailIndex.Search(q);
        if (r.Group == "unsorted")
            try { list = list.Where(h => string.IsNullOrEmpty(MailProjects.Of(h.Id))).ToList(); } catch { }
        return list;
    }
}
