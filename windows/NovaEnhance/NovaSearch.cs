using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🔎 One Nova search behind every feature: for any request it looks — in about a second, nothing re-read — through your emails,
/// your project file names, the text of every file any feature has read before, 🧠 Nova Memory, past answers in 🕘 Chat history
/// and your projects; then opens only the few unread files that the names point to (remembered for all features). The AI only
/// writes the answer from what is found. Designed by A.HAMROUNI
/// </summary>
static class NovaSearch
{
    public class Opts
    {
        public List<string> Folders;          // null = the project of the question (or all projects)
        public Project Project;
        public bool Docs = true, Emails = true, Mem = true, Hist = true, Drawings = true, ReadFiles = true;
        public string ExcludeConv = "";       // the email thread already given
        public HashSet<string> ExcludeEntries;
        public int MaxDocs = 14, MaxEmails = 5, MaxFiles = 8;
    }

    public class Result
    {
        public List<FolderIndex.Passage> Drawings = new(), Docs = new(), Emails = new(), Memory = new(), History = new();
        public Project Project;
        public List<string> Folders = new();
        public List<string> Notes = new();
        public List<FolderIndex.Passage> All => Memory.Concat(Drawings).Concat(Docs).Concat(Emails).Concat(History).ToList();
        public string Summary =>
            "🔎 Nova searched: " + string.Join(" · ", new[]
            {
                Project != null ? $"📁 {Project.Code}" : "📁 all projects",
                $"📂 {Drawings.Count + Docs.Count} document extract(s)", $"✉ {Emails.Count} email(s)",
                Memory.Count > 0 ? "🧠 1 remembered answer" : "", History.Count > 0 ? $"🕘 {History.Count} past answer(s)" : ""
            }.Where(x => x.Length > 0)) + (Notes.Count > 0 ? "\n    " + string.Join(" · ", Notes) : "");
    }

    static readonly Regex Stop = new("^(the|and|for|with|this|that|from|please|email|emails|what|which|about|check|find|send|give|nova|are|was|were|have|has|you|your|our|any|all|can|could|would|should|tell|show|me|my|is|in|on|of|to|a|an|it|be|by|at|or|as|do|does|did|how|why|when|where|who)$");

    static List<(DateTime At, List<Conversations.Item> Items)> histCache = new();

    static List<Conversations.Item> HistoryItems()
    {
        lock (histCache)
        {
            if (histCache.Count > 0 && DateTime.Now - histCache[0].At < TimeSpan.FromMinutes(3)) return histCache[0].Items;
            var items = Conversations.All();
            histCache.Clear(); histCache.Add((DateTime.Now, items));
            return items;
        }
    }

    public static List<string> Words(string question, IEnumerable<string> terms) =>
        (terms ?? Enumerable.Empty<string>()).Append(question ?? "")
            .SelectMany(t => Regex.Split(t.ToLowerInvariant(), @"[^\p{L}\p{N}\-]+"))
            .Select(w => w.Trim('-')).Where(w => w.Length >= 3 && !Stop.IsMatch(w)).Distinct().ToList();

    /// <summary>The search. Runs off the UI thread (Task.Run); status messages arrive as it goes.</summary>
    public static Result Run(string question, List<string> terms, Opts o, Action<string> status, CancellationToken ct)
    {
        o ??= new Opts();
        var r = new Result();
        var words = Words(question, terms);
        r.Project = o.Project ?? Projects.ForText(question ?? "");
        r.Folders = o.Folders?.Where(f => f.Length > 0).ToList()
                    ?? (r.Project != null ? Projects.FoldersOf(r.Project) : Projects.All().SelectMany(p => p.Folders).Concat(Projects.General).ToList());
        var covered = r.Folders.Where(Projects.Covers).ToList();

        // 🧠 Nova Memory: an answer you approved before (any feature).
        if (o.Mem)
            try
            {
                var (card, changed, _) = Memory.FindAny(question);
                if (card != null)
                    r.Memory.Add(new FolderIndex.Passage
                    {
                        Kind = "memory", Path = "", Loc = $"🧠 Nova Memory · an answer you approved on {card.Saved:dd MMM yyyy}",
                        Text = $"Question then: {card.Q}\nAnswer then: {card.A}" + (changed.Count > 0 ? "\n⚠ Changed since: " + string.Join(", ", changed.Take(5)) : ""), Score = 100
                    });
            }
            catch { }

        // 📐 Drawing / document numbers named in the request: their latest file.
        if (o.Drawings && o.Docs)
            foreach (var n in Revisions.Numbers(question).Take(8))
            {
                ct.ThrowIfCancellationRequested();
                var best = Projects.FindDoc(n, r.Folders.Count > 0 ? r.Folders : Projects.All().SelectMany(p => p.Folders).ToList()).FirstOrDefault(x => x.Status == "latest");
                if (best == null) { r.Notes.Add($"📐 {n} not found by name"); continue; }
                status?.Invoke($"📐 Reading {Path.GetFileName(best.Path)}…");
                try
                {
                    var (segs, _) = Projects.Read(best.Path, ct);
                    var text = string.Join("\n", segs.Select(x => x.Text)).Trim();
                    r.Drawings.Add(new FolderIndex.Passage { Kind = "file", Path = best.Path, Root = Root(best.Path, r.Folders), Loc = segs.FirstOrDefault()?.Loc ?? "", Goto = segs.FirstOrDefault()?.Goto ?? "", Text = text.Length > 1800 ? text[..1800] : text.Length > 0 ? text : "(drawing with no readable text)", Score = 50 });
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

        // 📖 + 📂 Project documents: text already read by any feature (instant), then the likely unread files found by name.
        if (o.Docs && words.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in r.Folders.Where(f => Directory.Exists(f) || GDrive.IsRoot(f)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    foreach (var p in FolderIndex.Retrieve(f, terms ?? words, 8, 3, Memory.Boosts(words)))
                        if (seen.Add(p.Path + "|" + p.Loc)) r.Docs.Add(p);
                }
                catch { }
            }
            if (o.ReadFiles && covered.Count > 0)
                foreach (var p in Projects.Ask(covered, words, o.MaxFiles, 14, status, ct))
                    if (seen.Add(p.Path + "|" + p.Loc))
                        r.Docs.Add(new FolderIndex.Passage { Kind = "file", Path = p.Path, Root = Root(p.Path, covered), Loc = p.Loc, Goto = p.Goto, Text = p.Text, Score = p.Score });
            // Best first: what the names pointed to and what matches most words; one file can't take every place.
            r.Docs = r.Docs.Select(p => (p, sc: words.Count(w => (p.Text ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)) + (Path.GetFileName(p.Path ?? "").ToLowerInvariant() is var nm && words.Any(w => nm.Contains(w)) ? 2 : 0)))
                .OrderByDescending(x => x.sc).GroupBy(x => x.p.Path, StringComparer.OrdinalIgnoreCase).SelectMany(g => g.Take(3)).OrderByDescending(x => x.sc)
                .Select(x => x.p).Take(o.MaxDocs).ToList();
        }

        // ✉ Emails: the most specific words first (numbers, long words), the project code when there is one.
        if (o.Emails && words.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            status?.Invoke("✉ Searching your emails…");
            var spec = words.OrderByDescending(w => w.Any(char.IsDigit)).ThenByDescending(w => w.Length).ToList();
            var tries = new List<List<string>>();
            if (r.Project != null) tries.Add(spec.Take(2).Prepend(r.Project.Code.ToLowerInvariant()).ToList());
            tries.Add(spec.Take(3).ToList()); tries.Add(spec.Take(2).ToList());
            var hits = new List<MailHit>();
            foreach (var t in tries)
            {
                try { hits = MailIndex.Search(new MailQuery { Words = t, Limit = 20 }); } catch { hits = new(); }
                hits = hits.Where(h => (o.ExcludeConv.Length == 0 || h.Conv != o.ExcludeConv) && (o.ExcludeEntries == null || !o.ExcludeEntries.Contains(h.Entry ?? ""))).ToList();
                if (hits.Count >= 2) break;
            }
            foreach (var h in hits.OrderByDescending(h => h.Date).Take(o.MaxEmails))
            {
                var body = Store.AiSeesFull ? (h.NewPart.Length > 1500 ? h.NewPart[..1500] : h.NewPart) : h.FirstLines(400);
                r.Emails.Add(new FolderIndex.Passage
                {
                    Kind = "email", Path = "", Remote = "mail|" + h.Entry + "|" + h.StoreId,
                    Loc = $"✉ {(h.Sent ? "You" : h.FromName)} · {h.Date:dd MMM yyyy} · {h.Subject}",
                    Text = $"From: {(h.Sent ? "me" : h.FromName)} · {h.Date:dd MMM yyyy}\nSubject: {h.Subject}\n{body}"
                });
            }
        }

        // 🕘 Past answers: an earlier conversation on the same subject.
        if (o.Hist && words.Count >= 2)
            try
            {
                var need = Math.Max(2, (int)Math.Ceiling(words.Count * 0.6));
                foreach (var it in HistoryItems().Select(i => (i, n: words.Count(w => i.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))).Where(x => x.n >= need).OrderByDescending(x => x.n).ThenByDescending(x => x.i.Updated).Take(2).Select(x => x.i))
                {
                    string text = it.Text;
                    if (!it.NovaChat)
                    {
                        var c = Conversations.Load(it.Path);
                        if (c != null)
                        {
                            var pairs = c.Msgs.Select((m, k) => (m, k)).Where(x => x.m.Role == "user").Select(x => (q: x.m.Text, a: c.Msgs.Skip(x.k + 1).FirstOrDefault(y => y.Role != "user")?.Text ?? ""))
                                .OrderByDescending(x => words.Count(w => (x.q + " " + x.a).Contains(w, StringComparison.OrdinalIgnoreCase))).FirstOrDefault();
                            if (pairs.q != null) text = $"Question then: {pairs.q}\nAnswer then: {pairs.a}";
                        }
                    }
                    r.History.Add(new FolderIndex.Passage { Kind = "history", Path = "", Remote = "history|" + it.Path, Loc = $"🕘 {Conversations.Icon(it.Kind)} {it.Title} · {it.Updated:dd MMM yyyy}", Text = text.Length > 1500 ? text[..1500] : text });
                }
            }
            catch { }
        return r;
    }

    static string Root(string path, List<string> folders) =>
        folders.FirstOrDefault(f => path.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) ?? Path.GetDirectoryName(path) ?? "";

    /// <summary>For the AI rules: what the extra sources are.</summary>
    public const string SourceRule =
        " Sources marked 🧠 Nova Memory are answers the user approved before, and 🕘 sources are earlier conversations: use them as hints, but prefer the documents and emails and say if they disagree. " +
        "Sources marked ✉ are related emails.";
}
