using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NovaEnhance;

/// <summary>Short forms used in UAE construction emails and documents, both ways (FHC ↔ fire hose cabinet).</summary>
static class Abbrev
{
    static readonly (string Short, string[] Long)[] List =
    {
        ("FHC", new[] { "fire hose cabinet", "fire hose cabinets" }), ("FHR", new[] { "fire hose reel" }), ("FLS", new[] { "fire and life safety", "fire life safety" }),
        ("FACP", new[] { "fire alarm control panel" }), ("SS", new[] { "stainless steel" }), ("MS", new[] { "mild steel", "method statement" }), ("GI", new[] { "galvanized iron", "galvanised" }),
        ("BOH", new[] { "back of house" }), ("FOH", new[] { "front of house" }), ("MEP", new[] { "mechanical electrical plumbing" }), ("HVAC", new[] { "air conditioning", "ventilation" }),
        ("RFI", new[] { "request for information" }), ("WIR", new[] { "work inspection request", "inspection request" }), ("MIR", new[] { "material inspection request" }),
        ("SSM", new[] { "material submittal", "sample submittal", "material sample" }), ("MAT", new[] { "material submittal" }), ("SDS", new[] { "shop drawing submittal", "shop drawing" }),
        ("SHD", new[] { "shop drawing" }), ("NCR", new[] { "non conformance report", "non-conformance" }), ("SI", new[] { "site instruction" }), ("CVI", new[] { "confirmation of verbal instruction" }),
        ("VO", new[] { "variation order", "variation" }), ("TQ", new[] { "technical query" }), ("MOM", new[] { "minutes of meeting", "minutes" }), ("TRN", new[] { "transmittal" }),
        ("IFC", new[] { "issued for construction", "for construction" }), ("IFT", new[] { "issued for tender", "tender" }), ("IFA", new[] { "issued for approval" }), ("ABD", new[] { "as built", "as-built" }),
        ("GFRC", new[] { "glass fibre reinforced concrete", "glass fiber reinforced concrete" }), ("GRC", new[] { "glass reinforced concrete" }), ("ACP", new[] { "aluminium composite panel", "aluminum composite panel" }),
        ("AAC", new[] { "autoclaved aerated concrete", "aac block" }), ("CW", new[] { "curtain wall" }), ("BMU", new[] { "building maintenance unit", "facade access" }),
        ("FFL", new[] { "finished floor level" }), ("SSL", new[] { "structural slab level" }), ("TOS", new[] { "top of slab" }), ("FCL", new[] { "false ceiling level", "ceiling" }),
        ("RCP", new[] { "reflected ceiling plan" }), ("GA", new[] { "general arrangement" }), ("DD", new[] { "detailed design" }), ("SD", new[] { "schematic design" }),
        ("PQ", new[] { "prequalification", "pre-qualification" }), ("PQS", new[] { "prequalification submittal" }), ("BOQ", new[] { "bill of quantities" }), ("LOI", new[] { "letter of intent" }),
        ("ADCD", new[] { "abu dhabi civil defence", "civil defense" }), ("DCD", new[] { "dubai civil defence" }), ("ADM", new[] { "abu dhabi municipality", "municipality" }),
        ("DM", new[] { "dubai municipality" }), ("ETISALAT", new[] { "e&", "telecom" }), ("ICT", new[] { "telecom", "information and communication" }), ("TA", new[] { "technical approval" }),
        ("NOC", new[] { "no objection certificate" }), ("DLP", new[] { "defects liability period" }), ("PC", new[] { "practical completion" }), ("HO", new[] { "handover", "hand over" }),
        ("LV", new[] { "low voltage" }), ("HV", new[] { "high voltage" }), ("DB", new[] { "distribution board" }), ("UPVC", new[] { "upvc" }), ("WC", new[] { "toilet", "washroom" })
    };

    /// <summary>Construction phrases that stay together (with the other ways people write them).</summary>
    static readonly string[][] Compounds =
    {
        new[] { "block wall", "blockwork", "block work", "masonry wall", "block masonry" },
        new[] { "curtain wall", "curtainwall", "CW" },
        new[] { "false ceiling", "suspended ceiling", "gypsum ceiling" },
        new[] { "raised floor", "access floor" },
        new[] { "shop drawing", "SDS", "SHD", "shop dwg" },
        new[] { "method statement", "MS" },
        new[] { "fire hose cabinet", "FHC", "hose cabinet" },
        new[] { "fire hose reel", "FHR", "hose reel" },
        new[] { "material submittal", "SSM", "MAT", "material sample", "sample submittal" },
        new[] { "inspection request", "WIR", "work inspection" },
        new[] { "expansion joint", "movement joint" },
        new[] { "waterproofing", "water proofing", "waterproof membrane" },
        new[] { "precast", "pre-cast" },
        new[] { "handrail", "hand rail", "balustrade" },
        new[] { "stone cladding", "stone facade" },
        new[] { "gypsum board", "gypsum partition", "drywall", "dry wall" },
        new[] { "door hardware", "ironmongery" },
        new[] { "glass railing", "glass balustrade" },
        new[] { "back of house", "BOH" },
        new[] { "front of house", "FOH" },
        new[] { "stainless steel", "SS" },
        new[] { "bill of quantities", "BOQ" },
        new[] { "variation order", "VO" },
        new[] { "request for information", "RFI" },
        new[] { "non conformance", "NCR", "non-conformance report" },
        new[] { "minutes of meeting", "MOM", "meeting minutes" },
        new[] { "technical query", "TQ" },
        new[] { "site instruction", "SI" }
    };

    static bool In(string text, string phrase) { try { return SearchSyntax.PhraseRx(phrase).IsMatch(text); } catch { return text.Contains(phrase, StringComparison.OrdinalIgnoreCase); } }

    /// <summary>The term and its other forms (short ↔ long), e.g. "FHC" → fire hose cabinet; "back of house" → BOH; "block wall" → blockwork.</summary>
    public static List<string> Forms(string term)
    {
        var t = (term ?? "").Trim();
        var res = new List<string> { t };
        foreach (var c in Compounds)
            if (c.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase) || (x.Contains(' ') && In(t, x)))) res.AddRange(c);
        foreach (var (sh, lo) in List)
        {
            if (t.Equals(sh, StringComparison.OrdinalIgnoreCase)) res.AddRange(lo);
            else if (lo.Any(l => l.Equals(t, StringComparison.OrdinalIgnoreCase) || (t.Length > 5 && l.StartsWith(t, StringComparison.OrdinalIgnoreCase)))) { res.Add(sh); res.AddRange(lo); }
        }
        return res.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static readonly Regex Filler = new("^(the|and|for|with|this|that|from|please|check|find|show|email|emails|mail|about|what|which|are|was|were|have|has|you|your|our|any|all|can|in|on|of|to|a|an|is|it|be|by|at|or|as|do|me|my|types?|areas?|related|regarding|limited|only|project|projects|before|after|since|latest|new|send|sent|list|get|give|need|some|there)$");

    /// <summary>A question → groups of terms (each group = one idea with its other forms), without AI. Phrases stay together.</summary>
    public static List<List<string>> Groups(string question)
    {
        var low = " " + (question ?? "").ToLowerInvariant() + " ";
        var groups = new List<List<string>>();
        // Construction phrases first ("block wall" stays one idea, with blockwork / block work…).
        foreach (var c in Compounds)
        {
            var hit = c.FirstOrDefault(x => (x.Contains(' ') || x.Length > 4) && In(low, x));
            if (hit == null) continue;
            groups.Add(c.ToList());
            low = SearchSyntax.PhraseRx(hit).Replace(low, " ");
        }
        // Long forms next ("back of house" before "house").
        foreach (var (sh, lo) in List)
            foreach (var l in lo)
                if (low.Contains(" " + l + " ") || low.Contains(" " + l + "s ")) { groups.Add(Forms(l)); low = low.Replace(l, " "); break; }
        foreach (var w in Regex.Split(low, @"[^\p{L}\p{N}\-]+").Where(w => w.Length >= 2))
        {
            if (Filler.IsMatch(w) || Regex.IsMatch(w, @"^\d{4}$")) continue;
            if (groups.Any(g => g.Contains(w, StringComparer.OrdinalIgnoreCase))) continue;
            groups.Add(Forms(w));
        }
        return groups.Take(8).ToList();
    }
}

/// <summary>⏸ Pause / ▶ Resume for a running search: the search waits between its steps.</summary>
static class SearchGate
{
    static TaskCompletionSource<bool> tcs;
    public static bool Paused => tcs != null;
    public static void Pause() { tcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
    public static void Resume() { var t = tcs; tcs = null; t?.TrySetResult(true); }
    public static Task Wait(CancellationToken ct) { var t = tcs; return t == null ? Task.CompletedTask : t.Task.WaitAsync(ct); }

    /// <summary>An AI step that must not hang the search: after the time limit Nova carries on without it.</summary>
    public static async Task<T> Limit<T>(Func<CancellationToken, Task<T>> step, int seconds, CancellationToken ct)
    {
        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
        to.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { return await step(to.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"the AI took more than {seconds} s"); }
    }
}

/// <summary>
/// ✦ AI search: the question is planned (ideas + their short forms + codes), the index is searched in several passes — every idea,
/// then most of them, then any — and scored (subject and attachment names count more, newer counts more); meaning search adds
/// what is said in other words; then the AI keeps the best few with a reason each. "Searched: …" shows what was done.
/// Designed by A.HAMROUNI
/// </summary>
static class SmartSearch
{
    public static bool On { get => Store.Bool("win_ai_search", true); set => Store.SetBool("win_ai_search", value); }

    public class Result { public List<MailHit> Hits = new(); public string Searched = ""; }

    /// <summary>Groups from the AI plan (JSON "groups"), completed with the short forms Nova knows.</summary>
    public static List<List<string>> FromPlan(JsonObject f, string question)
    {
        var res = new List<List<string>>();
        if (f?["groups"] is JsonArray ga)
            foreach (var g in ga)
            {
                var terms = (g as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Trim().Length > 1).SelectMany(Abbrev.Forms).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
                if (terms is { Count: > 0 }) res.Add(terms);
            }
        if (res.Count == 0) res = Abbrev.Groups(f?["clean"]?.ToString() ?? question);
        // 🧠 Your own words for things (from Nova Memory).
        try { foreach (var g in res) foreach (var (a, full) in Memory.VocabFor(string.Join(" ", g)).Take(3)) foreach (var v in new[] { a, full }) if (!g.Contains(v, StringComparer.OrdinalIgnoreCase)) g.Add(v); } catch { }
        return res.Take(8).ToList();
    }

    static MailQuery Clone(MailQuery q) => new()
    {
        From = q.From.ToList(), To = q.To.ToList(), Cc = q.Cc.ToList(), MeIn = q.MeIn, After = q.After, Before = q.Before, Folder = q.Folder,
        HasAttach = q.HasAttach, Unread = q.Unread, Unanswered = q.Unanswered, FollowUp = q.FollowUp, Limit = 400, Project = q.Project
    };

    /// <summary>✦ AI search may see the subject + first lines of the candidates (to pick the best) — even when "What the AI may see" is C.</summary>
    public static bool RankSees { get => Store.Bool("win_ai_rank_lines", true); set => Store.SetBool("win_ai_rank_lines", value); }

    /// <summary>The AI for planning and ranking searches: your cloud AI (Gemini first) — a local model is too slow for this.</summary>
    public static Provider Cloud()
    {
        var c = Ai.Current();
        if (c != null && c.Id != "local") return c;
        if (Ai.HasKey("gemini")) return Ai.ById("gemini");
        return Ai.All.FirstOrDefault(p => p.Id != "local" && Ai.HasKey(p.Id)) ?? c;
    }

    static readonly Dictionary<string, Regex> rxCache = new(StringComparer.OrdinalIgnoreCase);
    static Regex Rx(string term) { lock (rxCache) { if (!rxCache.TryGetValue(term, out var r)) rxCache[term] = r = SearchSyntax.PhraseRx(term); return r; } }

    /// <summary>Where an idea is in the email: subject 3 · attachment name 2.5 · the new text 1 · only in quoted history / signature 0.2.</summary>
    static (double Score, string Term, string Where) Place(MailHit h, List<string> terms, bool inIndex)
    {
        string subj = h.Subject ?? "", att = h.AttNames ?? "", np = h.NewPart;
        foreach (var t in terms) if (Rx(t).IsMatch(subj)) return (3, t, "subject");
        foreach (var t in terms) if (Rx(t).IsMatch(att)) return (2.5, t, "attachment");
        foreach (var t in terms) if (Rx(t).IsMatch(np)) return (1, t, "text");
        return (inIndex ? 0.2 : 0, "", "");
    }

    public static async Task<Result> Mail(string question, MailQuery baseQ, List<List<string>> groups, CancellationToken ct, bool rerank = true, SearchSyntax must = null)
    {
        var r = new Result();
        int queries = 0, n = groups.Count;
        // 1 · Every matching email for each idea, in any of its forms — all years (ids only, fast).
        var gIds = new List<HashSet<long>>();
        var dates = new Dictionary<long, long>();
        // Very common ideas (e.g. "SSM" in 47,000 SLV emails) are not collected one by one: the rarer ideas find the
        // candidates, the common one only adds to the score (checked in each candidate's subject / attachments / text).
        var common = new HashSet<int>();
        if (n >= 2)
            for (int gi = 0; gi < n; gi++)
            {
                int total = 0;
                foreach (var t in groups[gi].Take(6))
                {
                    var q0 = Clone(baseQ);
                    if (t.Contains(' ') || t.Contains('-')) q0.Phrase = t; else q0.Words = new() { t };
                    try { total += await Task.Run(() => MailIndex.CountIds(q0, 20001), ct); } catch (OperationCanceledException) { throw; } catch { }
                    if (total > 20000) break;
                }
                if (total > 20000) common.Add(gi);
            }
        if (common.Count == n) common.Clear();
        for (int gi = 0; gi < n; gi++)
        {
            var set = new HashSet<long>();
            if (common.Contains(gi)) { gIds.Add(set); continue; }
            foreach (var t in groups[gi].Take(6))
            {
                ct.ThrowIfCancellationRequested();
                await SearchGate.Wait(ct);
                var q = Clone(baseQ);
                if (t.Contains(' ') || t.Contains('-')) q.Phrase = t; else q.Words = new() { t };
                try { foreach (var (id, d) in await Task.Run(() => MailIndex.SearchIds(q, 40000), ct)) { set.Add(id); dates[id] = d; } }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Log.Warn("ai search", "ids: " + e.Message); continue; }
                queries++;
            }
            gIds.Add(set);
        }
        Activity.Current?.Mark("find every match (all years)", $"{queries} searches · " + string.Join(" · ", gIds.Select((g, i) => common.Contains(i) ? $"{groups[i][0]} (very common — scored only)" : $"{groups[i][0]} {g.Count:N0}")));
        var count = new Dictionary<long, int>();
        foreach (var set in gIds) foreach (var id in set) count[id] = count.TryGetValue(id, out var c) ? c + 1 : 1;
        // Meaning search: what is said in other words.
        var semantic = new HashSet<long>();
        if (Semantic.Ready)
            try { foreach (var h in await Semantic.Mail(question, 40, ct)) if (Pass(h, baseQ)) { semantic.Add(h.Id); count.TryAdd(h.Id, 0); dates[h.Id] = new DateTimeOffset(h.Date).ToUnixTimeMilliseconds(); } }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Log.Warn("ai search", "meaning: " + e.Message); }
        // 2 · Read the best-matching candidates (most ideas first) — up to 3,000, old and new alike.
        var cand = count.OrderByDescending(x => x.Value + (semantic.Contains(x.Key) ? 1 : 0)).ThenByDescending(x => dates.TryGetValue(x.Key, out var d) ? d : 0).Select(x => x.Key).Take(3000).ToList();
        var light = await Task.Run(() => MailIndex.ByIds(cand, 3000, 8000), ct);
        Activity.Current?.Mark("read candidates", $"{light.Count:N0} of {count.Count:N0}");
        await SearchGate.Wait(ct);
        // 3 · Score by WHERE each idea is; hard rules ("quotes", subject:, -words) and the project checked here.
        var scored = new List<(MailHit H, double S, int Strong, (double Score, string Term, string Where)[] P)>();
        foreach (var h in light)
        {
            if (must != null && !must.Pass(h)) continue;
            if (baseQ.Project.Length > 1 && !MailProjects.Fits(h, baseQ.Project)) continue;
            var places = new (double, string, string)[n];
            double s = 0; int strong = 0;
            for (int gi = 0; gi < n; gi++)
            {
                places[gi] = Place(h, groups[gi], gIds[gi].Contains(h.Id));
                s += places[gi].Item1;
                if (places[gi].Item1 >= 1) strong++;
            }
            var days = (DateTime.Now - h.Date).TotalDays;
            s += days < 30 ? 0.5 : days < 180 ? 0.25 : 0;
            if (semantic.Contains(h.Id)) s += 2;
            scored.Add((h, s, strong, places));
        }
        // Precision: many candidates → the most specific idea must be in the subject, an attachment name or the new text.
        string precision = "";
        if (scored.Count > 200 && n >= 2)
        {
            var rare = Enumerable.Range(0, n).Where(i => !common.Contains(i)).OrderBy(i => gIds[i].Count).DefaultIfEmpty(0).First();
            var kept = scored.Where(x => x.P[rare].Score >= 1 || semantic.Contains(x.H.Id)).ToList();
            if (kept.Count >= 3) { scored = kept; precision = $" · precision: “{groups[rare][0]}” required in subject / attachment / new text"; }
        }
        var need = Math.Max(1, (int)Math.Ceiling(n * 0.6));
        var strict = scored.Where(x => x.Strong == n).ToList();
        var most = scored.Where(x => x.Strong >= need).ToList();
        var pass = strict.Count >= 5 || n <= 1 ? "every idea" : most.Count >= 5 ? $"most ideas ({need} of {n})" : "any idea";
        var pool = (strict.Count >= 5 || n <= 1 ? strict : most.Count >= 5 ? most : scored.Where(x => x.Strong >= 1 || semantic.Contains(x.H.Id)).ToList())
            .OrderByDescending(x => x.S).ThenByDescending(x => x.H.Date).Take(60).ToList();
        // Why each email is there (shown under it, even without the AI).
        foreach (var x in pool)
        {
            var why = x.P.Where(p => p.Score >= 1).Select(p => $"{p.Term} in {p.Where}").Take(3).ToList();
            x.H.Why = why.Count > 0 ? "✓ " + string.Join(" · ", why) : semantic.Contains(x.H.Id) ? "🧠 same meaning" : "";
        }
        // 4 · The cloud AI keeps the best few, with a reason each (subjects and first lines only).
        Activity.Current?.Mark("score by place" + (precision.Length > 0 ? " + precision" : ""), $"{scored.Count:N0} kept by the rules · matched {pass} · pool {pool.Count}");
        var picked = pool.Select(x => x.H).ToList();
        var reranked = false; var slow = false;
        await SearchGate.Wait(ct);
        var ai = Cloud();
        if (rerank && pool.Count > 6 && ai != null && (Store.AiSeesLines || RankSees))
            try
            {
                var sb = new StringBuilder($"Question: {question}\nCandidate emails:\n");
                for (int i = 0; i < pool.Count; i++) sb.Append($"[{i + 1}] {pool[i].H.Date:dd MMM yyyy} · {(pool[i].H.Sent ? "Me" : pool[i].H.FromName)} · {pool[i].H.Subject}{(pool[i].H.AttNames.Length > 0 ? " · 📎 " + pool[i].H.AttNames : "")} · “{pool[i].H.FirstLines(160)}”\n");
                var sys = "You pick the emails that really answer the user's question, most relevant first. Reply with JSON only: {\"keep\":[{\"i\":3,\"why\":\"4 to 10 words: what this email is\"}]}. " +
                          "Keep at most 15 (fewer is better when only a few fit). Drop emails about another subject that only mention the words in passing. " +
                          "Understand construction short forms (FHC fire hose cabinet, SS stainless steel, BOH back of house, SSM material submittal, SDS shop drawing, WIR inspection request, blockwork = block wall…).";
                var j = Ai.ParseJson(await SearchGate.Limit(t => Ai.Chat(sys, sb.ToString(), true, true, ai, t), 25, ct));
                var keep = new List<MailHit>();
                foreach (var k in j["keep"]?.AsArray() ?? new JsonArray())
                    if (int.TryParse(k?["i"]?.ToString(), out var i) && i >= 1 && i <= pool.Count && !keep.Contains(pool[i - 1].H))
                    {
                        var h = pool[i - 1].H;
                        var w = k?["why"]?.ToString() ?? "";
                        h.Why = w.Length > 0 ? w + (h.Why.Length > 0 ? "   " + h.Why : "") : h.Why;
                        keep.Add(h);
                    }
                if (keep.Count > 0) { picked = keep; reranked = true; }
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException) { slow = true; }
            catch (Exception e) { Log.Warn("ai search", "rerank: " + e.Message); }
        Activity.Current?.Mark(reranked ? "AI ranking" : slow ? "AI ranking (too slow, skipped)" : "no AI ranking", $"{picked.Count} kept");
        picked = reranked ? picked : picked.Take(30).ToList();
        // The full emails for the ones kept (the list above read only their beginning).
        var full = await Task.Run(() => MailIndex.ByIds(picked.Select(h => h.Id)), ct);
        foreach (var h in full) h.Why = picked.First(x => x.Id == h.Id).Why;
        r.Hits = full;
        var years = r.Hits.Count > 0 ? (r.Hits.Min(h => h.Date).Year == r.Hits.Max(h => h.Date).Year ? $" · {r.Hits.Min(h => h.Date).Year}" : $" · {r.Hits.Min(h => h.Date).Year}–{r.Hits.Max(h => h.Date).Year}") : "";
        r.Searched = "Searched: " + string.Join(" · ", groups.Select(g => string.Join(" / ", g.Take(3)))) +
                     $"  —  {queries} searches · all years · matched {pass}" + precision + (semantic.Count > 0 ? $" · meaning search {semantic.Count}" : "") +
                     $" · {count.Count:N0} candidates → {r.Hits.Count} best{years}" + (reranked ? $" (✦ {Ai.LabelOf(ai)} picked)" : slow ? " (the AI was slow — best scored shown)" : "");
        return r;
    }

    static bool Pass(MailHit h, MailQuery q)
    {
        if (q.After is { } a && h.Date < a) return false;
        if (q.Before is { } b && h.Date > b) return false;
        if (q.Folder == "sent" && !h.Sent) return false;
        if (q.Folder == "received" && h.Sent) return false;
        if (q.From.Count > 0 && !q.From.Any(f => (h.FromName + " " + h.FromAddr).Contains(f, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }
}

/// <summary>
/// 🧠 Meaning search (optional, off by default): each email (subject + first lines) and each part of the files Nova has read gets a
/// "meaning fingerprint" (an embedding) — by your local AI (nothing leaves the PC), Gemini or OpenAI — so a question finds what is said
/// in other words. Built gently in the background, kept encrypted with the email index. Designed by A.HAMROUNI
/// </summary>
static class Semantic
{
    public static string By { get => Store.Str("win_embed", "off"); set { Store.Set("win_embed", value); wake.Set(); } }
    public static string LocalModel { get => Store.Str("win_embed_model", "nomic-embed-text"); set => Store.Set("win_embed_model", value); }
    public static bool On => By != "off";
    public static bool Ready => On && Done > 200;
    public static volatile int Done, Total, DocsDone;
    public static volatile string Phase = "off";
    const int Dims = 256;

    static readonly AutoResetEvent wake = new(false);
    static Thread thread;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static void Start()
    {
        if (thread != null) return;
        thread = new Thread(() => Guard.Run("meaning search", Loop)) { IsBackground = true, Name = "Nova meaning", Priority = ThreadPriority.Lowest };
        thread.Start();
    }

    // ------------------------------------------------------------------ embeddings

    static float[] Norm(float[] v) { double s = 0; foreach (var x in v) s += x * x; var n = (float)Math.Sqrt(s); if (n > 0) for (int i = 0; i < v.Length; i++) v[i] /= n; return v; }

    public static async Task<List<float[]>> Embed(List<string> texts, CancellationToken ct, bool query = false)
    {
        var by = By;
        if (by == "gemini")
        {
            var key = Store.Key("gemini");
            if (key.Length == 0) throw new AiException("Meaning search with Gemini needs a Gemini key");
            var reqs = new JsonArray();
            foreach (var t in texts)
                reqs.Add(new JsonObject
                {
                    ["model"] = "models/gemini-embedding-001", ["taskType"] = query ? "RETRIEVAL_QUERY" : "RETRIEVAL_DOCUMENT", ["outputDimensionality"] = Dims,
                    ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = t }) }
                });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:batchEmbedContents?key=" + key)
            { Content = new StringContent(new JsonObject { ["requests"] = reqs }.ToJsonString(), Encoding.UTF8, "application/json") };
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new AiException("Gemini embeddings: " + (int)resp.StatusCode + " " + (body.Length > 200 ? body[..200] : body));
            return (JsonNode.Parse(body)?["embeddings"]?.AsArray() ?? new JsonArray()).Select(e => Norm(e?["values"]?.AsArray().Select(x => (float)x.GetValue<double>()).ToArray() ?? Array.Empty<float>())).ToList();
        }
        string url, key2, model;
        if (by == "openai") { url = "https://api.openai.com/v1/embeddings"; key2 = Store.Key("openai"); model = "text-embedding-3-small"; if (key2.Length == 0) throw new AiException("Meaning search with OpenAI needs an OpenAI key"); }
        else { url = Ai.LocalUrl.TrimEnd('/') + "/embeddings"; key2 = Store.Key("local"); model = LocalModel; }
        var ob = new JsonObject { ["model"] = model, ["input"] = new JsonArray(texts.Select(t => (JsonNode)t).ToArray()) };
        if (by == "openai") ob["dimensions"] = Dims;
        using var rq = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(ob.ToJsonString(), Encoding.UTF8, "application/json") };
        if (key2.Length > 0) rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key2);
        using var rs = await http.SendAsync(rq, ct);
        var b2 = await rs.Content.ReadAsStringAsync(ct);
        if (!rs.IsSuccessStatusCode) throw new AiException($"Embeddings ({by}): " + (int)rs.StatusCode + " " + (b2.Length > 200 ? b2[..200] : b2));
        return (JsonNode.Parse(b2)?["data"]?.AsArray() ?? new JsonArray()).Select(e =>
        {
            var v = e?["embedding"]?.AsArray().Select(x => (float)x.GetValue<double>()).ToArray() ?? Array.Empty<float>();
            return Norm(v);
        }).ToList();
    }

    static byte[] Pack(float[] v) { var b = new byte[v.Length * 2]; for (int i = 0; i < v.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), (Half)v[i]); return b; }

    static float Dot(byte[] b, float[] q)
    {
        int n = Math.Min(b.Length / 2, q.Length);
        float s = 0;
        for (int i = 0; i < n; i++) s += (float)BitConverter.ToHalf(b, i * 2) * q[i];
        return s;
    }

    // ------------------------------------------------------------------ storing (emails in the mail index, document parts in the folder index)

    static void Tables()
    {
        using (var c = MailIndex.Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS emb(id INTEGER PRIMARY KEY, src TEXT, v BLOB);"; cmd.ExecuteNonQuery(); }
        using (var c = FolderIndex.Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS embseg(id INTEGER PRIMARY KEY, src TEXT, v BLOB);"; cmd.ExecuteNonQuery(); }
    }

    static bool MayRun() => !Gentle.On || !Gentle.WorkingNow || Gentle.Idle > TimeSpan.FromMinutes(3) || By == "local";

    static void Loop()
    {
        Thread.Sleep(TimeSpan.FromSeconds(120));
        bool tables = false;
        while (true)
        {
            try
            {
                if (!On) { Phase = "off"; wake.WaitOne(TimeSpan.FromMinutes(5)); continue; }
                if (!tables) { Tables(); tables = true; }
                var by = By;
                Count(by);
                if (!MayRun()) { Phase = $"⏸ paused — you are working (continues after {Gentle.EndHour}:00 or when the PC is idle)"; wake.WaitOne(TimeSpan.FromMinutes(2)); continue; }
                // Emails, newest first.
                var batch = new List<(long Id, string Text)>();
                using (var c = MailIndex.Open())
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT m.id, m.subject, substr(m.body,1,900), m.att_names FROM mail m LEFT JOIN emb e ON e.id=m.id AND e.src=$b WHERE e.id IS NULL ORDER BY m.date DESC LIMIT 96";
                    cmd.Parameters.AddWithValue("$b", by);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) batch.Add((r.GetInt64(0), $"{(r.IsDBNull(1) ? "" : r.GetString(1))}\n{(r.IsDBNull(3) ? "" : r.GetString(3))}\n{(r.IsDBNull(2) ? "" : r.GetString(2))}"));
                }
                if (batch.Count > 0)
                {
                    Phase = "building (emails)";
                    var vs = Embed(batch.Select(b => b.Text.Length > 2000 ? b.Text[..2000] : b.Text).ToList(), CancellationToken.None).GetAwaiter().GetResult();
                    using var c = MailIndex.Open();
                    using var tx = c.BeginTransaction();
                    using var ins = c.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT OR REPLACE INTO emb(id,src,v) VALUES($i,$b,$v)";
                    var pi = ins.Parameters.Add("$i", SqliteType.Integer); var pb = ins.Parameters.Add("$b", SqliteType.Text); var pv = ins.Parameters.Add("$v", SqliteType.Blob);
                    for (int i = 0; i < Math.Min(batch.Count, vs.Count); i++) { pi.Value = batch[i].Id; pb.Value = by; pv.Value = Pack(vs[i]); ins.ExecuteNonQuery(); }
                    tx.Commit();
                    Done += batch.Count;
                    cache = null;
                    Thread.Sleep(by == "local" ? 200 : 1500);
                    continue;
                }
                // Then the parts of files Nova has read (first 4 parts of each file).
                var segs = new List<(long Id, string Text)>();
                using (var c = FolderIndex.Open())
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT s.id, f.path, s.loc, substr(s.text,1,1200) FROM segs s JOIN files f ON f.id=s.file_id LEFT JOIN embseg e ON e.id=s.id AND e.src=$b " +
                                      "WHERE e.id IS NULL AND s.id IN (SELECT id FROM segs s2 WHERE s2.file_id=s.file_id ORDER BY id LIMIT 4) LIMIT 96";
                    cmd.Parameters.AddWithValue("$b", by);
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) segs.Add((r.GetInt64(0), $"{Path.GetFileName(r.GetString(1))} · {(r.IsDBNull(2) ? "" : r.GetString(2))}\n{(r.IsDBNull(3) ? "" : r.GetString(3))}"));
                }
                if (segs.Count > 0)
                {
                    Phase = "building (documents)";
                    var vs = Embed(segs.Select(b => b.Text).ToList(), CancellationToken.None).GetAwaiter().GetResult();
                    using var c = FolderIndex.Open();
                    using var tx = c.BeginTransaction();
                    using var ins = c.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT OR REPLACE INTO embseg(id,src,v) VALUES($i,$b,$v)";
                    var pi = ins.Parameters.Add("$i", SqliteType.Integer); var pb = ins.Parameters.Add("$b", SqliteType.Text); var pv = ins.Parameters.Add("$v", SqliteType.Blob);
                    for (int i = 0; i < Math.Min(segs.Count, vs.Count); i++) { pi.Value = segs[i].Id; pb.Value = by; pv.Value = Pack(vs[i]); ins.ExecuteNonQuery(); }
                    tx.Commit();
                    DocsDone += segs.Count;
                    docCache = null;
                    Thread.Sleep(by == "local" ? 200 : 1500);
                    continue;
                }
                Phase = "✓ up to date";
                wake.WaitOne(TimeSpan.FromMinutes(5));
            }
            catch (Exception e)
            {
                Phase = "⚠ " + e.Message;
                Log.Warn("meaning search", e.Message);
                wake.WaitOne(TimeSpan.FromMinutes(5));
            }
        }
    }

    static DateTime counted;
    static void Count(string by)
    {
        if (DateTime.Now - counted < TimeSpan.FromMinutes(1)) return;
        counted = DateTime.Now;
        try
        {
            using var c = MailIndex.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM mail), (SELECT COUNT(*) FROM emb WHERE src=$b)";
            cmd.Parameters.AddWithValue("$b", by);
            using var r = cmd.ExecuteReader();
            if (r.Read()) { Total = r.GetInt32(0); Done = r.GetInt32(1); }
        }
        catch { }
        try
        {
            using var c = FolderIndex.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM embseg WHERE src=$b";
            cmd.Parameters.AddWithValue("$b", by);
            DocsDone = Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch { }
    }

    public static string Line() => !On ? "" : $"🧠 Meaning search ({By}): {Done:N0} of {Total:N0} emails · {DocsDone:N0} document parts · {Phase}";

    // ------------------------------------------------------------------ searching by meaning

    static List<(long Id, byte[] V)> cache, docCache;
    static DateTime cacheAt;

    static List<(long, byte[])> Load(bool docs)
    {
        if (DateTime.Now - cacheAt > TimeSpan.FromMinutes(15)) { cache = null; docCache = null; }
        var cur = docs ? docCache : cache;
        if (cur != null) return cur;
        var list = new List<(long, byte[])>();
        using var c = docs ? FolderIndex.Open() : MailIndex.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, v FROM {(docs ? "embseg" : "emb")} WHERE src=$b";
        cmd.Parameters.AddWithValue("$b", By);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), (byte[])r[1]));
        cacheAt = DateTime.Now;
        if (docs) docCache = list; else cache = list;
        return list;
    }

    static async Task<List<long>> Top(string question, bool docs, int k, CancellationToken ct)
    {
        var q = (await Embed(new() { question }, ct, true)).FirstOrDefault();
        if (q == null || q.Length == 0) return new();
        var all = await Task.Run(() => Load(docs), ct);
        return await Task.Run(() => all.Select(x => (x.Item1, S: Dot(x.Item2, q))).OrderByDescending(x => x.S).Take(k).Where(x => x.S > 0.3f).Select(x => x.Item1).ToList(), ct);
    }

    public static async Task<List<MailHit>> Mail(string question, int k, CancellationToken ct) => MailIndex.ByIds(await Top(question, false, k, ct));

    /// <summary>Document parts that mean what the question asks (for the one Nova search).</summary>
    public static async Task<List<FolderIndex.Passage>> Docs(string question, IEnumerable<string> folders, int k, CancellationToken ct)
    {
        var res = new List<FolderIndex.Passage>();
        if (!Ready || DocsDone < 50) return res;
        var ids = await Top(question, true, k * 3, ct);
        if (ids.Count == 0) return res;
        var roots = folders.Select(f => f.TrimEnd('\\') + "\\").ToList();
        using var c = FolderIndex.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT s.id, f.path, s.loc, s.go, s.text, f.kind FROM segs s JOIN files f ON f.id=s.file_id WHERE s.id IN ({string.Join(",", ids)})";
        using var r = cmd.ExecuteReader();
        var byId = new Dictionary<long, FolderIndex.Passage>();
        while (r.Read())
        {
            var path = r.GetString(1);
            if (roots.Count > 0 && !roots.Any(x => path.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
            byId[r.GetInt64(0)] = new FolderIndex.Passage { Path = path, Loc = r.IsDBNull(2) ? "" : r.GetString(2), Goto = r.IsDBNull(3) ? "" : r.GetString(3), Text = r.IsDBNull(4) ? "" : r.GetString(4), Kind = r.IsDBNull(5) ? "file" : r.GetString(5) };
        }
        foreach (var id in ids) if (byId.TryGetValue(id, out var p)) res.Add(p);
        return res.Take(k).ToList();
    }
}
