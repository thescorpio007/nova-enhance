using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

// ===================================================================== Revisions: which file is the current one

/// <summary>
/// The project map: files with the same document number are grouped, and the latest revision is found from the name
/// (Rev C, R02, C01 after P05, IFC after IFT …, then the newest date). Older ones are "superseded".
/// </summary>
static class Revisions
{
    public class Doc { public string Key = "", Path = "", Rev = ""; public long MTime; public (int, string) Rank; }

    static readonly Regex RevRx = new(
        @"(?:^|[\s_\-\.\(\[]+)(?:rev(?:ision)?[\s\.\-_]*(?<r>[A-Z]{0,2}\d{0,3}[A-Z]?)|(?<r>R\d{1,3})|(?<r>[CPTD]\d{2,3})|(?<r>IFC|IFT|IFA|IFR|IFP))(?=[\s_\-\.\)\]]|$)",
        RegexOptions.IgnoreCase);

    static readonly Dictionary<string, (DateTime At, Dictionary<string, (Doc Me, Doc Latest, int Count)> Map)> cache = new(StringComparer.OrdinalIgnoreCase);

    public static Doc Parse(string path, long mtime)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        name = Regex.Replace(name, @"\s*\(\d\)$", "");                       // "… (1)" copies
        var ms = RevRx.Matches(name);
        var d = new Doc { Path = path, MTime = mtime };
        var key = name;
        if (ms.Count > 0)
        {
            var m = ms[^1];
            var r = m.Groups["r"].Value.ToUpperInvariant();
            if (r.Length > 0) { d.Rev = r; key = name.Remove(m.Index, m.Length) + (m.Index + m.Length < name.Length ? "" : ""); }
        }
        d.Key = Regex.Replace(key.ToLowerInvariant(), @"[\s_\-\.]+", " ").Trim();
        var penalty = Regex.IsMatch(path, @"\\(superseded|old|archive|_old|previous)\\", RegexOptions.IgnoreCase) ? -5 : 0;
        int cls = d.Rev switch
        {
            "" => 1,
            "IFA" or "IFR" or "IFP" => 0,
            "IFT" => 1,
            "IFC" => 3,
            _ when Regex.IsMatch(d.Rev, @"^[PD]\d") => 0,
            _ when Regex.IsMatch(d.Rev, @"^C\d") => 2,
            _ => 1
        };
        d.Rank = (cls + penalty, Regex.Replace(d.Rev, @"\d+", x => x.Value.PadLeft(4, '0')));
        return d;
    }

    static int Cmp(Doc a, Doc b)
    {
        var c = a.Rank.Item1.CompareTo(b.Rank.Item1);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Rank.Item2, b.Rank.Item2);
        return c != 0 ? c : a.MTime.CompareTo(b.MTime);
    }

    /// <summary>Every indexed file under the folder: its group and the group's latest file.</summary>
    public static Dictionary<string, (Doc Me, Doc Latest, int Count)> Map(string root)
    {
        if (string.IsNullOrEmpty(root)) return new();
        lock (cache) if (cache.TryGetValue(root, out var hit) && DateTime.Now - hit.At < TimeSpan.FromMinutes(5)) return hit.Map;
        var docs = new List<Doc>();
        try
        {
            var pre = FolderIndex.NormalRoot(root).TrimEnd('\\') + "\\";
            using var c = FolderIndex.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path,mtime FROM files WHERE lower(substr(path,1,$n))=lower($p) LIMIT 200000";
            cmd.Parameters.AddWithValue("$n", pre.Length); cmd.Parameters.AddWithValue("$p", pre);
            using var r = cmd.ExecuteReader();
            while (r.Read()) docs.Add(Parse(r.GetString(0), r.IsDBNull(1) ? 0 : r.GetInt64(1)));
        }
        catch { }
        var map = new Dictionary<string, (Doc, Doc, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in docs.GroupBy(d => (d.Key, System.IO.Path.GetExtension(d.Path).ToLowerInvariant())))
        {
            var list = g.ToList();
            var latest = list.Aggregate((a, b) => Cmp(a, b) >= 0 ? a : b);
            foreach (var d in list) map[d.Path] = (d, latest, list.Count);
        }
        lock (cache) cache[root] = (DateTime.Now, map);
        return map;
    }

    /// <summary>"" · "latest, Rev C" · "SUPERSEDED by X.pdf (Rev C)"</summary>
    public static string Tag(string root, string path)
    {
        if (!Map(root).TryGetValue(path, out var e)) return "";
        if (e.Count < 2) return e.Me.Rev.Length > 0 ? "Rev " + e.Me.Rev : "";
        if (ReferenceEquals(e.Me, e.Latest)) return "latest" + (e.Me.Rev.Length > 0 ? ", Rev " + e.Me.Rev : "") + $" of {e.Count}";
        return $"SUPERSEDED by {System.IO.Path.GetFileName(e.Latest.Path)}" + (e.Latest.Rev.Length > 0 ? $" (Rev {e.Latest.Rev})" : "");
    }

    public static bool Superseded(string root, string path) => Map(root).TryGetValue(path, out var e) && e.Count > 1 && !ReferenceEquals(e.Me, e.Latest);

    /// <summary>For "What Nova knows": documents that have several revisions.</summary>
    public static List<(string Name, string Rev, int Older)> Groups(string root) =>
        Map(root).Values.Where(v => v.Count > 1 && ReferenceEquals(v.Me, v.Latest))
            .Select(v => (System.IO.Path.GetFileName(v.Latest.Path), v.Latest.Rev, v.Count - 1)).OrderBy(x => x.Item1).ToList();

    /// <summary>What kind of document a file is, from its name and folder (for the cross-check).</summary>
    public static string KindOf(string path)
    {
        var p = path.ToLowerInvariant();
        var n = System.IO.Path.GetFileName(p);
        if (Regex.IsMatch(n, @"\bboq\b|bill of quantit|\bbq\b|pricing") || p.Contains("\\boq")) return "BOQ";
        if (Regex.IsMatch(n, @"\brfi\b|-rfi-|_rfi_")) return "RFI";
        if (Regex.IsMatch(n, @"\bspec|specification|\b\d{2} \d{2} \d{2}\b|-sp-|_sp_") || p.Contains("\\spec")) return "Specification";
        if (n.EndsWith(".msg") || n.EndsWith(".eml") || Regex.IsMatch(n, @"\bletter\b|-ltr-|_ltr_|\bemail\b")) return "Letter/email";
        if (Regex.IsMatch(n, @"\bdwg\b|drawing|-dr-|_dr_|-sd-|\bga\b|\bplan\b|section|elevation|detail") || n.EndsWith(".dwg") || p.Contains("\\drawings")) return "Drawing";
        if (Regex.IsMatch(n, @"report|\bmom\b|minutes|schedule")) return "Report";
        return "";
    }
}

// ===================================================================== 🧠 Nova Memory

class KnowCard
{
    public string Id = Guid.NewGuid().ToString("N")[..10];
    public string Q = "", A = "", Root = "", Kind = "folder";
    public List<string> Files = new();
    public Dictionary<string, long> Times = new();
    public DateTime Saved = DateTime.Now, Used;
    public int Uses;
}

class FileBoost { public string Path = "", Terms = ""; public double Score; public DateTime At = DateTime.Now; }

class MemData
{
    public List<KnowCard> Cards = new();
    public Dictionary<string, string> Vocab = new(StringComparer.OrdinalIgnoreCase);
    public List<FileBoost> Boosts = new();
    public string Style = "";
    public DateTime StyleAt;
    public int Good, Bad;
}

/// <summary>
/// What Nova learns from you, kept encrypted on this PC (Windows protection, your account only) and in your backup:
/// answers you liked (knowledge cards), the right files you pointed to, your project vocabulary, and — only when you allow it —
/// how you write. Settings → 🧠 What Nova knows shows, edits and deletes everything. Designed by A.HAMROUNI
/// </summary>
static class Memory
{
    static readonly object L = new();
    static MemData d;
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nova Enhance memory v1");
    static readonly JsonSerializerOptions J = new() { IncludeFields = true, WriteIndented = false };
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova Enhance", "memory.dat");

    public static bool On { get => Store.Bool("win_memory", true); set => Store.SetBool("win_memory", value); }
    public static bool StyleOn { get => Store.Bool("win_style", false); set => Store.SetBool("win_style", value); }
    public static bool AdviceOn { get => Store.Bool("win_advice", true); set => Store.SetBool("win_advice", value); }

    public static MemData Data
    {
        get
        {
            lock (L)
            {
                if (d != null) return d;
                try { if (File.Exists(FilePath)) d = JsonSerializer.Deserialize<MemData>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser)), J); }
                catch (Exception e) { Log.Warn("memory", "Could not read Nova Memory: " + e.Message); }
                d ??= new MemData();
                d.Vocab = new Dictionary<string, string>(d.Vocab ?? new(), StringComparer.OrdinalIgnoreCase);
                return d;
            }
        }
    }

    public static void Save()
    {
        lock (L)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Data, J)), Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(FilePath, bytes);
            }
            catch (Exception e) { Log.Error("memory save", e); }
        }
    }

    public static string Export() { lock (L) return JsonSerializer.Serialize(Data, J); }

    /// <summary>Restore from a backup: merged with what is already here.</summary>
    public static int Import(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            var x = JsonSerializer.Deserialize<MemData>(json, J);
            if (x == null) return 0;
            int n = 0;
            lock (L)
            {
                foreach (var c in x.Cards ?? new()) if (!Data.Cards.Any(k => k.Id == c.Id)) { Data.Cards.Add(c); n++; }
                foreach (var (k, v) in x.Vocab ?? new()) if (!Data.Vocab.ContainsKey(k)) { Data.Vocab[k] = v; n++; }
                foreach (var b in x.Boosts ?? new()) if (!Data.Boosts.Any(o => o.Path == b.Path && o.Terms == b.Terms)) { Data.Boosts.Add(b); n++; }
                if (string.IsNullOrEmpty(Data.Style) && !string.IsNullOrEmpty(x.Style)) { Data.Style = x.Style; Data.StyleAt = x.StyleAt; n++; }
            }
            Save();
            return n;
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------------ knowledge cards

    static HashSet<string> Key(string q) => FolderAsk.LocalTerms(q).Select(t => t.ToLowerInvariant()).ToHashSet();

    static double Similar(string a, string b)
    {
        var x = Key(a); var y = Key(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        var shared = x.Count(y.Contains);
        return (double)shared / x.Union(y).Count();
    }

    /// <summary>An answer you liked for a similar question (same folder or PDF), and which of its files changed since.</summary>
    public static (KnowCard Card, List<string> Changed) Find(string question, string root, string kind = "folder")
    {
        if (!On) return (null, null);
        KnowCard best = null; double bs = 0;
        lock (L)
            foreach (var c in Data.Cards.Where(c => c.Kind == kind && string.Equals(c.Root ?? "", root ?? "", StringComparison.OrdinalIgnoreCase)))
            {
                var s = Similar(question, c.Q);
                if (s > bs) { bs = s; best = c; }
            }
        if (best == null || bs < 0.6) return (null, null);
        var changed = new List<string>();
        foreach (var f in best.Files)
            try
            {
                if (!File.Exists(f)) changed.Add(Path.GetFileName(f) + " (moved or deleted)");
                else if (best.Times.TryGetValue(f, out var t) && t != 0 && File.GetLastWriteTimeUtc(f).Ticks != t) changed.Add(Path.GetFileName(f));
            }
            catch { }
        foreach (var f in best.Files)
            if (Revisions.Superseded(root, f)) changed.Add(Path.GetFileName(f) + " (a newer revision exists)");
        best.Uses++; best.Used = DateTime.Now;
        Save();
        return (best, changed);
    }

    /// <summary>👍 — remember the answer, its files and the vocabulary in them.</summary>
    public static void Good(string question, string answer, string root, IEnumerable<string> files, IEnumerable<string> texts, string kind = "folder")
    {
        if (!On || string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer)) return;
        var fl = files.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        lock (L)
        {
            Data.Cards.RemoveAll(c => c.Kind == kind && string.Equals(c.Root ?? "", root ?? "", StringComparison.OrdinalIgnoreCase) && Similar(c.Q, question) >= 0.8);
            var card = new KnowCard { Q = question.Trim(), A = answer.Trim(), Root = root ?? "", Kind = kind, Files = fl };
            foreach (var f in fl) try { card.Times[f] = File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks : 0; } catch { }
            Data.Cards.Add(card);
            if (Data.Cards.Count > 500) Data.Cards.RemoveAt(0);
            var terms = string.Join(" ", Key(question));
            foreach (var f in fl.Take(4)) Boost(f, terms, 1);
            foreach (var t in texts ?? Enumerable.Empty<string>()) LearnVocabulary(t);
            Data.Good++;
        }
        Save();
    }

    /// <summary>👎 — forget a remembered answer for this question; the right file (if you pointed to it) is boosted.</summary>
    public static void Bad(string question, string root, string rightFile, string kind = "folder")
    {
        lock (L)
        {
            Data.Cards.RemoveAll(c => c.Kind == kind && string.Equals(c.Root ?? "", root ?? "", StringComparison.OrdinalIgnoreCase) && Similar(c.Q, question) >= 0.6);
            if (!string.IsNullOrEmpty(rightFile)) Boost(rightFile, string.Join(" ", Key(question)), 4);
            Data.Bad++;
        }
        Save();
    }

    static void Boost(string path, string terms, double score)
    {
        var b = Data.Boosts.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && x.Terms == terms);
        if (b == null) Data.Boosts.Add(new FileBoost { Path = path, Terms = terms, Score = score });
        else { b.Score += score; b.At = DateTime.Now; }
        if (Data.Boosts.Count > 2000) Data.Boosts.RemoveAt(0);
    }

    /// <summary>Files you liked or pointed to for questions like this one: path → extra ranking.</summary>
    public static Dictionary<string, double> Boosts(IEnumerable<string> terms)
    {
        var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!On) return res;
        var t = terms.SelectMany(x => Regex.Split(x.ToLowerInvariant(), @"\s+")).Where(x => x.Length > 1).ToHashSet();
        lock (L)
            foreach (var b in Data.Boosts)
            {
                var bt = b.Terms.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (bt.Length == 0) continue;
                var share = (double)bt.Count(t.Contains) / bt.Length;
                if (share < 0.5) continue;
                res.TryGetValue(b.Path, out var v);
                res[b.Path] = v + b.Score * share;
            }
        return res;
    }

    // ------------------------------------------------------------------ project vocabulary

    static readonly Regex AbbrA = new(@"\b((?:[A-Z][a-z]+[\s\-&]+){1,6}[A-Z][a-z]+)\s*\(([A-Z][A-Z0-9&]{1,7})\)");
    static readonly Regex AbbrB = new(@"\b([A-Z][A-Z0-9&]{1,7})\s*[:=–-]\s*((?:[A-Z][a-z]+[\s\-]+){1,5}[A-Z]?[a-z]+)");

    /// <summary>"Glass Fibre Reinforced Concrete (GFRC)" → GFRC = Glass Fibre Reinforced Concrete.</summary>
    public static void LearnVocabulary(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (Match m in AbbrA.Matches(text)) Add(m.Groups[2].Value, m.Groups[1].Value);
        foreach (Match m in AbbrB.Matches(text)) Add(m.Groups[1].Value, m.Groups[2].Value);
        void Add(string abbr, string full)
        {
            full = Regex.Replace(full, @"\s+", " ").Trim();
            var initials = new string(full.Split(' ', '-', '&').Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0])).ToArray());
            // Only real abbreviations: the letters must come from the words.
            if (abbr.Length < 2 || full.Length > 70 || !initials.Contains(abbr[0])) return;
            if (abbr.Count(char.IsLetter) > 0 && abbr.Where(char.IsLetter).Count(ch => initials.Contains(ch)) < abbr.Count(char.IsLetter) - 1) return;
            lock (L) if (!Data.Vocab.ContainsKey(abbr) && Data.Vocab.Count < 3000) Data.Vocab[abbr] = full;
        }
    }

    public static void SetWord(string abbr, string meaning) { lock (L) { if (string.IsNullOrWhiteSpace(meaning)) Data.Vocab.Remove(abbr); else Data.Vocab[abbr.Trim()] = meaning.Trim(); } Save(); }

    /// <summary>The vocabulary for a question: abbreviations it uses and their meanings (both ways).</summary>
    public static List<(string Abbr, string Full)> VocabFor(string question)
    {
        var res = new List<(string, string)>();
        if (!On || string.IsNullOrEmpty(question)) return res;
        lock (L)
            foreach (var (a, f) in Data.Vocab)
                if (Regex.IsMatch(question, @"\b" + Regex.Escape(a) + @"\b", RegexOptions.IgnoreCase) || question.Contains(f, StringComparison.OrdinalIgnoreCase)) res.Add((a, f));
        return res.Take(12).ToList();
    }

    public static IEnumerable<string> VocabularyWords()
    {
        lock (L) return Data.Vocab.Keys.Concat(Data.Vocab.Values.SelectMany(v => v.Split(' '))).Where(w => w.Length > 1).ToList();
    }

    // ------------------------------------------------------------------ writing style (only when you allow it)

    public static string StyleNote() =>
        StyleOn && !string.IsNullOrWhiteSpace(Data.Style) ? " Write it the way the user writes (learned from the user's own sent emails): " + Data.Style.Trim() : "";

    /// <summary>Reads about 40 of your sent emails (only your own new text, not quoted replies) and asks your AI to describe how you write.</summary>
    public static async Task<string> LearnStyle(IWin32Window owner)
    {
        if (Ai.Active() == null) return "Add an AI key first (Settings → AI & API keys).";
        var sent = await Task.Run(() => MailIndex.SentAfter(DateTime.Now.AddMonths(-6), 300));
        var bodies = sent.Select(h => OwnText(h.Body)).Where(b => b.Length > 80).Take(40).Select(b => b.Length > 900 ? b[..900] : b).ToList();
        if (bodies.Count < 5) return "Not enough sent emails in the mail index yet (Ask Nova → build the index first).";
        var sample = string.Join("\n\n---\n\n", bodies);
        var ok = ConfirmSend.Ask(owner, $"✦ Send {bodies.Count} of your sent emails to {Ai.LabelOf(Ai.Current())}?",
            $"To learn how you write, Nova sends the text you wrote in {bodies.Count} recent sent emails (about {sample.Length:N0} characters, quoted replies removed). Only a short description of your style is kept, encrypted on this PC.",
            sample, "Send and learn", false);
        if (ok != ConfirmSend.Choice.Send) return "Cancelled — nothing was sent.";
        var sys = "Describe this person's email writing style in at most 110 words, as instructions for writing new emails in their voice: usual greeting and sign-off, " +
                  "tone and formality, sentence length, how they ask for actions and set deadlines, typical phrases, language habits (British/US spelling), use of bullet points. " +
                  "Do not quote names, projects, numbers or any confidential detail. Plain text.";
        var style = (await Ai.Chat(sys, sample, false)).Trim();
        lock (L) { Data.Style = style; Data.StyleAt = DateTime.Now; }
        StyleOn = true;
        Save();
        return "✓ Learned from " + bodies.Count + " sent emails. ✦ Draft reply now writes like you.";
    }

    /// <summary>Only what the user wrote: stops at the quoted earlier email.</summary>
    static string OwnText(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var m = Regex.Match(body, @"(\r?\n)\s*(From:|De :|-----Original|________________|On .{5,80} wrote:|Sent from my)", RegexOptions.IgnoreCase);
        var t = m.Success ? body[..m.Index] : body;
        return Regex.Replace(t, @"\s+\n", "\n").Trim();
    }

    public static void ForgetAll() { lock (L) d = new MemData(); Save(); }
}
