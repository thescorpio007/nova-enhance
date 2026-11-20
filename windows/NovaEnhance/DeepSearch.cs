using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🔎 Deep search (Nova Chat): "was this system approved before?" — Nova breaks the attached document into the PARTS of the system
/// (products, manufacturer, standards, document numbers), then searches in rounds, wide to deep:
///   round 1 the whole subject · round 2 each part by its exact name · round 3+ the references found (other submittals, test reports…)
/// Three sources are asked together — the local index, Google Drive's own search (it reads inside scans and pictures) and the emails —
/// and only the parts nobody found get the slow full read with OCR (the same reading as "Search inside a folder").
/// Only short extracts go to the AI, as everywhere in Nova. Designed by A.HAMROUNI
/// </summary>
static class DeepSearch
{
    static readonly Regex Ask = new(@"go(ing)?\s+deep|deep(er)?\s+(search|check|look|dive)|internal\s+(data\s*base|records?)|(approved|accepted|used|submitted)\s+(earlier|before|previously|already)|" +
        @"(previously|earlier|already)\s+(approved|accepted|submitted|used)|earlier\s+approv|identical\s+(system|material|product)|same\s+(system|material|product)\s+(was|is|been)|precedent", RegexOptions.IgnoreCase);

    /// <summary>The question asks for it ("go deep", "approved earlier", "internal database"…).</summary>
    public static bool Wanted(string question) => Ask.IsMatch(question ?? "");

    public class Part { public string Role = "", Name = ""; public List<string> Also = new(); public int Found; }

    public class Hit
    {
        public string Kind = "passage", Path = "", Loc = "", Goto = "", Remote = "", Entry = "", Store = "", Label = "", Text = "", Part = "", Via = "";
    }

    public class Outcome
    {
        public List<Hit> Hits = new();
        public List<Part> Parts = new();
        public string Subject = "", Searched = "", Project = "";
        public int Rounds, Index, Drive, Emails, DeepFiles, Leads;
        public List<string> Notes = new();
    }

    static Regex Phrase(string s) => new(string.Join(@"[\s\-_/.]*", Regex.Matches(s, @"[\p{L}\p{N}]+").Select(m => Regex.Escape(m.Value))), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Approval = new(@"\bcode\s*[:\-]?\s*(0?[1-4]|[A-D])\b|approved(\s+(as|with)\s+(noted|comments?))?|no\s+objection|not\s+approved|rejected|revise\s+and\s+resubmit|status\s*[:\-]", RegexOptions.IgnoreCase);
    static readonly Regex Reply = new(@"reply|response|comment|crs|approv|status|code", RegexOptions.IgnoreCase);

    /// <summary>The parts of the system, read by the AI from the attached document (and the chat so far).</summary>
    static async Task<(string Subject, List<string> Wide, List<Part> Parts, List<string> Refs)> Plan(string question, string attached, string history, Provider prov, CancellationToken ct)
    {
        var sys = "You prepare a records search for " + Persona.Who + ". From the document extracts and the question, list what must be searched in the user's own project records " +
                  "to find whether the same thing (a system, a material, a product, a detail) was submitted or approved before. Reply with JSON only:\n" +
                  "{\"subject\":\"the subject in 2-4 words, e.g. hardscape waterproofing\",\"wide\":[\"2-4 short search phrases for the whole subject\"]," +
                  "\"parts\":[{\"role\":\"what it is in the system, e.g. membrane, primer, protection board, manufacturer, applicator, test standard\",\"name\":\"the exact name as written, e.g. a product name with its model\"," +
                  "\"also\":[\"other spellings: without the model suffix, the brand alone if distinctive\"]}],\"refs\":[\"document or submittal numbers named in the extracts\"]}\n" +
                  "Up to 10 parts, the most identifying first (product names and models before generic words). Only names that really appear in the extracts or the question. Never invent.";
        var user = (history.Length > 0 ? "Earlier in the chat:\n" + history + "\n\n" : "") + "Document extracts:\n" + (attached.Length > 14000 ? attached[..14000] : attached) + "\n\nQuestion: " + question;
        using var _aiFor7 = Ai.For("deep", Ai.Picked(prov, "deep"));   // ⚙ AI for each feature
        var j = Ai.ParseJson(await Ai.Chat(sys, user, true, true, prov, ct));
        var parts = new List<Part>();
        foreach (var p in (j["parts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var name = (p["name"]?.ToString() ?? "").Trim();
            if (name.Length < 3 || parts.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            parts.Add(new Part { Role = (p["role"]?.ToString() ?? "").Trim(), Name = name,
                Also = (p["also"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString()?.Trim() ?? "").Where(x => x.Length >= 4 && !x.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList() });
        }
        List<string> L(string k) => (j[k] as JsonArray ?? new JsonArray()).Select(x => x?.ToString()?.Trim() ?? "").Where(x => x.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return ((j["subject"]?.ToString() ?? "").Trim(), L("wide").Take(4).ToList(), parts.Take(10).ToList(), L("refs").Take(8).ToList());
    }

    static Task RunSta(Action a)
    {
        var tcs = new TaskCompletionSource();
        var t = new Thread(() => { try { a(); tcs.SetResult(); } catch (Exception e) { tcs.SetException(e); } }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    static string Cut(string s, Regex around, int max = 1700)
    {
        s = (s ?? "").Trim();
        if (s.Length <= max) return s;
        var m = around?.Match(s);
        if (m == null || !m.Success) return s[..max] + "…";
        int a = Math.Max(0, m.Index - max / 2), b = Math.Min(s.Length, a + max);
        return (a > 0 ? "…" : "") + s[a..b] + (b < s.Length ? "…" : "");
    }

    // ---------------------------------------------------------------- 🕘 saved after every step (Search history), taken back by ▶ Continue

    static JsonObject HitJson(Hit h) => new()
    {
        ["kind"] = h.Kind, ["path"] = h.Path, ["loc"] = h.Loc, ["goto"] = h.Goto, ["remote"] = h.Remote, ["entry"] = h.Entry, ["store"] = h.Store,
        ["label"] = h.Label, ["text"] = h.Text, ["part"] = h.Part, ["via"] = h.Via
    };
    static Hit HitOf(JsonObject j)
    {
        string S(string k) => j[k]?.ToString() ?? "";
        return new Hit { Kind = S("kind").Length > 0 ? S("kind") : "passage", Path = S("path"), Loc = S("loc"), Goto = S("goto"), Remote = S("remote"), Entry = S("entry"), Store = S("store"), Label = S("label"), Text = S("text"), Part = S("part"), Via = S("via") };
    }
    static JsonArray Arr(IEnumerable<string> l) => new(l.Select(x => (JsonNode)x).ToArray());
    static List<string> Strs(JsonNode n) => (n as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? new();

    /// <summary>The sources as Nova Chat shows them (for the 🕘 history).</summary>
    public static JsonArray SourcesJson(IEnumerable<Hit> hits) => new(hits.Select(h => (JsonNode)new JsonObject
    {
        ["kind"] = h.Kind, ["label"] = (h.Part.Length > 0 ? "[" + h.Part + "] " : "") + h.Label, ["path"] = h.Path, ["loc"] = h.Loc, ["goto"] = h.Goto,
        ["remote"] = h.Remote, ["entry"] = h.Entry, ["store"] = h.Store, ["text"] = h.Text.Length > 600 ? h.Text[..600] + "…" : h.Text
    }).ToArray());

    /// <summary>[resume] = the saved state of an interrupted search: its parts, finished steps and finds are taken back. [checkpoint] gets the state after every step.</summary>
    public static async Task<Outcome> Run(string question, string attached, IEnumerable<string> attachedPaths, string history, Provider prov, Action<string> status, CancellationToken ct,
        JsonObject resume = null, Action<JsonObject, Outcome> checkpoint = null)
    {
        var o = new Outcome();
        var skip = new HashSet<string>(attachedPaths ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        // 1 · The parts of the system (taken back when the search continues).
        string subject; List<string> wide, refs; List<Part> parts;
        var doneQ = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var doneNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (resume?["parts"] is JsonArray rp)
        {
            status("▶ Deep search · taking back what was found before…");
            subject = resume["subject"]?.ToString() ?? question; wide = Strs(resume["wide"]); refs = Strs(resume["refs"]);
            parts = rp.OfType<JsonObject>().Select(x => new Part { Role = x["role"]?.ToString() ?? "", Name = x["name"]?.ToString() ?? "", Also = Strs(x["also"]), Found = x["found"]?.GetValue<int>() ?? 0 }).ToList();
            if (resume["done"] is JsonObject dq) foreach (var kv in dq) doneQ[kv.Key] = kv.Value?.GetValue<int>() ?? 0;
            foreach (var n in Strs(resume["nums"])) doneNums.Add(n);
        }
        else
        {
            status("🔎 Deep search · reading the parts of the system…");
            (subject, wide, parts, refs) = await Plan(question, attached, history, prov, ct);
        }
        o.Subject = subject; o.Parts = parts;
        void Checkpoint(string step)
        {
            if (checkpoint == null) return;
            try
            {
                var st = new JsonObject
                {
                    ["subject"] = subject, ["wide"] = Arr(wide), ["refs"] = Arr(refs), ["step"] = step,
                    ["parts"] = new JsonArray(parts.Select(p => (JsonNode)new JsonObject { ["role"] = p.Role, ["name"] = p.Name, ["also"] = Arr(p.Also), ["found"] = p.Found }).ToArray()),
                    ["done"] = new JsonObject(doneQ.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)kv.Value))), ["nums"] = Arr(doneNums),
                    ["hits"] = new JsonArray(o.Hits.Select(h => (JsonNode)HitJson(h)).ToArray())
                };
                checkpoint(st, o);
            }
            catch { }
        }
        if (parts.Count == 0 && wide.Count == 0) { o.Notes.Add("the AI could not name the parts of the system — add the product names to the question"); return o; }

        // 2 · The project: named in the question, or the attached file's own (its folder, its number, the text), or the chat so far.
        var proj = Projects.ForText(question) ?? skip.Select(p => Projects.ForText(p)).FirstOrDefault(p => p != null)
                   ?? Projects.ForText(string.Join(" ", skip.Select(Path.GetFileName)), attached.Length > 6000 ? attached[..6000] : attached) ?? Projects.ForText(history);
        var folders = (proj != null ? Projects.FoldersOf(proj) : Projects.All().SelectMany(p => p.Folders).Concat(Projects.General)).Where(f => f.Length > 0 && Directory.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        o.Project = proj?.Code ?? "all projects";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool Add(Hit h)
        {
            if (h.Text.Trim().Length < 20 || skip.Contains(h.Path)) return false;
            if (!seen.Add(h.Path + "|" + h.Loc + "|" + h.Entry + "|" + (h.Path.Length == 0 && h.Entry.Length == 0 ? h.Label : ""))) return false;
            if (h.Path.Length > 0) { fileCount.TryGetValue(h.Path, out var n); if (n >= 3) return false; fileCount[h.Path] = n + 1; }
            o.Hits.Add(h); return true;
        }
        if (resume?["hits"] is JsonArray rh) foreach (var j in rh.OfType<JsonObject>()) Add(HitOf(j));
        Checkpoint("parts");
        string LabelOf(string path, string loc, string go) => Path.GetFileName(path) + " · " + FolderIndex.Where(new PieceHit { Loc = loc ?? "", Goto = go ?? "", Text = "", Matches = new() });

        bool driveOk = GDrive.SignedIn;
        if (!driveOk) o.Notes.Add("Google Drive not asked — sign in once in Settings → Google Drive");
        var driveMap = new Dictionary<string, DriveFile>(StringComparer.OrdinalIgnoreCase);

        // One query, three sources. [exact] = the name must really be in the extract (a part); else the words are enough (the subject).
        async Task<int> Query(string q, Part part, bool exact, int perSource)
        {
            var key = (exact ? "x|" : "w|") + q;
            if (doneQ.TryGetValue(key, out var before)) return before;   // done before ▶ Continue
            var got = await QueryNow(q, part, exact, perSource);
            doneQ[key] = got;
            Checkpoint(part == null ? "subject · " + q : part.Name);
            return got;
        }
        async Task<int> QueryNow(string q, Part part, bool exact, int perSource)
        {
            ct.ThrowIfCancellationRequested();
            int got = 0;
            var rx = Phrase(q);
            var words = NovaSearch.Words(q, null);
            if (words.Count == 0) return 0;
            var label = part?.Name ?? subject;
            // 📖 The local index (text already read by any Nova feature).
            var tIndex = Task.Run(() =>
            {
                var l = new List<FolderIndex.Passage>();
                foreach (var f in folders)
                    try { l.AddRange(FolderIndex.Retrieve(f, words, exact ? 10 : 6, 2)); } catch { }
                return l;
            }, ct);
            // ☁ Google Drive's own search (inside scans and pictures too). 🔒 private words never leave.
            var tDrive = driveOk && !Projects.Private.IsMatch(q) ? GDrive.FullText(q, 25, ct) : Task.FromResult(new List<DriveFile>());
            // ✉ The emails.
            var tMail = Task.Run(() =>
            {
                try { return NovaSearch.Run(q, words, new NovaSearch.Opts { Project = proj, Docs = false, Drawings = false, Mem = false, Hist = false, MaxEmails = perSource }, _ => { }, ct).Emails; }
                catch (OperationCanceledException) { throw; }
                catch { return new List<FolderIndex.Passage>(); }
            }, ct);

            foreach (var p in (await tIndex).Where(p => !exact || rx.IsMatch(p.Text)).OrderByDescending(p => p.Score).Take(perSource))
                if (Add(new Hit { Path = p.Path, Loc = p.Loc, Goto = p.Goto, Remote = p.Remote ?? "", Label = FolderAsk.Label(p), Text = Cut(p.Text, rx), Part = label, Via = "index" })) { got++; o.Index++; }

            List<DriveFile> dfs;
            try { dfs = await tDrive; } catch (OperationCanceledException) { throw; } catch (Exception e) { dfs = new(); driveOk = false; o.Notes.Add("Google Drive: " + e.Message); }
            int dn = 0;
            foreach (var d in dfs)
            {
                if (dn >= perSource) break;
                ct.ThrowIfCancellationRequested();
                driveMap[d.Name] = d;
                // The same file on this PC (the work folder is copied to Drive): read here, so the page can be opened.
                var local = Projects.ByFileName(d.Name).Select(r => r.Path).FirstOrDefault(p => File.Exists(p) && (folders.Count == 0 || folders.Any(f => p.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))));
                if (local != null && skip.Contains(local)) continue;
                if (local != null && fileCount.ContainsKey(local)) continue;         // the index already gave it
                if (local != null)
                {
                    status($"☁ Google Drive found {d.Name} — reading it here…");
                    List<Seg> segs = null;
                    try { segs = (await Task.Run(() => Projects.Read(local, ct), ct)).Segs; } catch (OperationCanceledException) { throw; } catch { }
                    var hit = (segs ?? new()).Where(s => rx.IsMatch(s.Text ?? "")).Take(2).ToList();
                    if (hit.Count == 0 && segs != null && segs.Count > 0) hit = segs.Where(s => words.Count(w => (s.Text ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)) >= Math.Max(1, words.Count - 1)).Take(1).ToList();
                    if (hit.Count == 0)
                    {
                        // Google sees the words inside (a scan, a picture) where this PC's reading does not: said as it is.
                        if (Add(new Hit { Path = local, Label = Path.GetFileName(local) + " · found by Google Drive", Text = $"Google Drive's search finds “{q}” inside this file ({Path.GetFileName(Path.GetDirectoryName(local))}). The exact page was not read on this PC.", Part = label, Via = "drive" })) { got++; dn++; o.Drive++; }
                        continue;
                    }
                    foreach (var s in hit)
                        if (Add(new Hit { Path = local, Loc = s.Loc, Goto = s.Goto, Label = LabelOf(local, s.Loc, s.Goto), Text = Cut(s.Text, rx), Part = label, Via = "drive" })) { got++; dn++; o.Drive++; }
                }
                else if (Add(new Hit { Path = "☁ " + d.Name, Remote = d.Id + "|" + d.Mime, Label = d.Name + " · in Google Drive only", Text = $"Google Drive's search finds “{q}” inside this file. It is not in the work folder on this PC.", Part = label, Via = "drive" })) { got++; dn++; o.Drive++; }
            }

            foreach (var p in (await tMail).Where(p => !exact || rx.IsMatch(p.Text) || rx.IsMatch(p.Loc ?? "")).Take(perSource))
            {
                var bits = (p.Remote ?? "").Split('|');
                if (Add(new Hit { Kind = "email", Entry = bits.Length > 1 ? bits[1] : "", Store = bits.Length > 2 ? bits[2] : "", Label = p.Loc, Text = Cut(p.Text, rx, 1200), Part = label, Via = "email" })) { got++; o.Emails++; }
            }
            if (part != null) part.Found += got;
            return got;
        }

        // Round 1 · the whole subject.
        o.Rounds = 1;
        foreach (var w in wide.Prepend(subject).Where(x => x.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).Take(4))
        {
            status($"🔎 Round 1 · the whole subject: {w}…");
            await Query(w, null, false, 4);
        }
        // Round 2 · each part, by its exact name (then its other spellings when the exact name gives nothing).
        o.Rounds = 2;
        foreach (var p in parts)
        {
            status($"🔎 Round 2 · {(p.Role.Length > 0 ? p.Role + ": " : "")}{p.Name}…");
            if (await Query(p.Name, p, true, 4) == 0)
                foreach (var a in p.Also) { status($"🔎 Round 2 · {p.Name} → also “{a}”…"); if (await Query(a, p, true, 3) > 0) break; }
        }

        // Rounds 3-4 · the leads: document numbers named in what was found (and in the attached document) — their own files, then what THEY name.
        foreach (var p in skip) foreach (var n in Revisions.Numbers(Path.GetFileName(p))) doneNums.Add(n);
        var from = 0;
        for (int round = 3; round <= 4; round++)
        {
            ct.ThrowIfCancellationRequested();
            var nums = o.Hits.Skip(from).SelectMany(h => Revisions.Numbers(h.Text + " " + h.Label)).Concat(round == 3 ? refs.SelectMany(Revisions.Numbers) : Enumerable.Empty<string>())
                .Where(n => !doneNums.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            from = o.Hits.Count;
            if (nums.Count == 0) break;
            o.Rounds = round;
            foreach (var n in nums)
            {
                doneNums.Add(n);
                Checkpoint("reference " + n);
                status($"🔎 Round {round} · following {n}…");
                var doc = Projects.FindDoc(n, folders).FirstOrDefault(x => !x.Cad && !skip.Contains(x.Path));
                if (doc == null || fileCount.ContainsKey(doc.Path)) continue;
                List<Seg> segs = null;
                try { segs = (await Task.Run(() => Projects.Read(doc.Path, ct), ct)).Segs; } catch (OperationCanceledException) { throw; } catch { }
                if (segs == null || segs.Count == 0) continue;
                // The pages that speak of a part of the system, else the first page.
                var rxs = parts.Select(p => (p, rx: Phrase(p.Name))).ToList();
                var picks = segs.Where(s => rxs.Any(x => x.rx.IsMatch(s.Text ?? ""))).Take(2).ToList();
                if (picks.Count == 0) picks = segs.Where(s => (s.Text ?? "").Trim().Length > 40).Take(1).ToList();
                foreach (var s in picks)
                {
                    var which = rxs.FirstOrDefault(x => x.rx.IsMatch(s.Text ?? ""));
                    if (Add(new Hit { Path = doc.Path, Loc = s.Loc, Goto = s.Goto, Label = LabelOf(doc.Path, s.Loc, s.Goto), Text = Cut(s.Text, which.rx), Part = which.p?.Name ?? "reference " + n, Via = "lead" }))
                    { o.Leads++; if (which.p != null) which.p.Found++; }
                }
            }
        }

        // Deep read · the parts nobody found: every file of the submittal folders is opened and read (scans by OCR), like "Search inside a folder".
        var missing = parts.Where(p => p.Found == 0 && p.Name.Any(char.IsLetter) && Regex.Matches(p.Name, @"[\p{L}\p{N}]+").Count >= 1 && p.Name.Length >= 5).Take(6).ToList();
        if (missing.Count > 0 && folders.Count > 0 && FolderAsk.Sees != "never")
        {
            // The submittal folders of the project when it has them (much less to read); else the project folders.
            var roots = new List<string>();
            foreach (var f in folders)
                try { roots.AddRange(Directory.EnumerateDirectories(f, "*", SearchOption.TopDirectoryOnly).Where(d => Regex.IsMatch(Path.GetFileName(d), @"submittal|material|approv|\bMAR\b|\bSHD\b|\bSSM\b|\bMS\b", RegexOptions.IgnoreCase))); } catch { }
            if (roots.Count == 0) roots = folders.Take(3).ToList();
            var rxAll = string.Join("|", missing.Select(p => "(?:" + Phrase(p.Name) + ")"));
            var m = new Matcher(rxAll, "regex", false, false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(8));
            var found = new List<FileHit>();
            foreach (var root in roots)
            {
                if (limit.IsCancellationRequested) break;
                var pg = new FolderIndex.Progress();
                using var tick = new System.Threading.Timer(_ => status($"📖 Deep read · {Path.GetFileName(root.TrimEnd('\\'))} · {pg.Done:N0} / {pg.Total:N0} files · {found.Count} with a part of the system — ■ Stop keeps what is found"), null, 800, 1500);
                try { await RunSta(() => FolderIndex.Search(root, m, null, h => { lock (found) found.Add(h); }, null, pg, limit.Token, new FolderIndex.Options { Gentle = true })); }
                catch (OperationCanceledException) { }
                catch (Exception e) { o.Notes.Add("deep read: " + e.Message); }
                o.DeepFiles += pg.Done;
            }
            if (limit.IsCancellationRequested && !ct.IsCancellationRequested) o.Notes.Add("deep read stopped after 8 minutes — run 🔎 Deep search again to continue (what was read is remembered)");
            ct.ThrowIfCancellationRequested();
            foreach (var fh in found.OrderByDescending(h => h.Hits).Take(14))
                foreach (var pc in fh.Pieces.Take(2))
                {
                    var which = missing.FirstOrDefault(p => Phrase(p.Name).IsMatch(pc.Text ?? ""));
                    if (Add(new Hit { Path = fh.Path, Loc = pc.Loc, Goto = pc.Goto, Remote = fh.Remote ?? "", Label = LabelOf(fh.Path, pc.Loc, pc.Goto), Text = Cut(pc.Text, which != null ? Phrase(which.Name) : null), Part = which?.Name ?? "", Via = "deep" }) && which != null) which.Found++;
                }
        }

        Checkpoint("deep read");
        // The approval: the reply / response / comment sheet next to each document found, and the page that carries a code.
        status("🔎 Looking for the approval status of what was found…");
        int statusAdded = 0;
        foreach (var path in o.Hits.Where(h => h.Kind == "passage" && File.Exists(h.Path)).Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList())
        {
            if (statusAdded >= 10) break;
            ct.ThrowIfCancellationRequested();
            try
            {
                var dir = Path.GetDirectoryName(path)!;
                var near = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.EnumerateDirectories(dir).Where(d => Reply.IsMatch(Path.GetFileName(d))).SelectMany(d => Directory.EnumerateFiles(d, "*.pdf", SearchOption.TopDirectoryOnly)))
                    .Where(f => !f.Equals(path, StringComparison.OrdinalIgnoreCase) && Reply.IsMatch(Path.GetFileNameWithoutExtension(f)) && new FileInfo(f).Length < 60_000_000).Take(2).Prepend(path).ToList();
                foreach (var f in near)
                {
                    var segs = (await Task.Run(() => Projects.Read(f, ct), ct)).Segs;
                    var s = segs.Take(6).FirstOrDefault(x => Approval.IsMatch(x.Text ?? ""));
                    if (s != null && Add(new Hit { Path = f, Loc = s.Loc, Goto = s.Goto, Label = LabelOf(f, s.Loc, s.Goto) + " · status", Text = Cut(s.Text, Approval, 1100), Part = "approval status", Via = "status" })) statusAdded++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        o.Searched = $"🔎 Deep search · 📁 {o.Project} · {o.Rounds} round(s) · {parts.Count} part(s) of the system · 📖 index {o.Index} · ☁ Google Drive {(driveOk || o.Drive > 0 ? o.Drive.ToString() : "not asked")} · ✉ emails {o.Emails} · 🔗 references followed {o.Leads}" +
                     (o.DeepFiles > 0 ? $" · 📖 deep read {o.DeepFiles:N0} files" : "") + (o.Notes.Count > 0 ? " · ⚠ " + string.Join(" · ", o.Notes.Distinct()) : "");
        return o;
    }

    /// <summary>What the AI is told to write: the system as a table, one row per part.</summary>
    public static string Instructions(Outcome o) =>
        " This is a DEEP SEARCH of the user's own records for an earlier submission or approval of the same system. The parts of the system searched are: " +
        string.Join("; ", o.Parts.Select(p => (p.Role.Length > 0 ? p.Role + " = " : "") + p.Name + (p.Found == 0 ? " (nothing found)" : ""))) + ". " +
        "Answer in this order: (1) one line: was the same system, or parts of it, submitted or approved before — yes / partly / no evidence found; " +
        "(2) a Markdown table with one row per part of the system and the columns: Part | Product / name | Found in (document number, revision, page) | Status (approval code and date when the extracts show it, else \"status not shown\") | Source numbers; " +
        "a part with no extract is a row saying \"not found in the records searched\"; (3) what differs between the earlier documents and the attached one (products, models, thickness, standards); " +
        "(4) what to check next. Give an approval code or status ONLY when an extract shows it, and cite it. Never guess a status. An extract that only says Google Drive finds the words inside a file proves that the name is in that file, not its approval.";
}
