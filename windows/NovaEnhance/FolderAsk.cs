using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// ✦ Ask AI about a folder: the AI turns the question into search words, Nova finds the best passages in the folder (on this PC),
/// and — only when allowed — the AI answers from those passages with their sources.
/// </summary>
static class FolderAsk
{
    /// <summary>ask (default: confirm each time) · never (passages only) · send (without asking)</summary>
    public static string Sees { get => Store.Str("win_fs_ai", "ask"); set => Store.Set("win_fs_ai", value); }

    const string ExpandSystem =
        "Turn the user's question about project documents (specifications, drawings, reports, BOQs, correspondence) into search terms. " +
        "Reply with JSON only: {\"terms\":[\"…\"]} with 6 to 18 short terms: the key nouns, codes and exact references from the question, plus the usual " +
        "technical synonyms, abbreviations, standards and units used in construction documents (for example fire rating → fire resistance, FRL, EI 60, REI; " +
        "slab thickness → slab depth, thk, mm). If the question is not in English, include the English terms as well as the original words. No sentences.";

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","of","for","to","in","on","is","are","what","which","where","when","who","how","find","show","me","my","please","about",
        "and","or","with","required","this","that","there","does","do","any","all","give","list","tell"
    };

    public static List<string> LocalTerms(string q) =>
        Regex.Matches(q ?? "", @"[\p{L}\p{N}][\p{L}\p{N}\-\./]*").Select(m => m.Value.Trim('.', '-')).Where(w => w.Length > 1 && !Stop.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();

    /// <summary>Only the question (and the previous question for a follow-up) goes to the AI here.</summary>
    public static async Task<List<string>> Expand(string question, string previousQuestion, CancellationToken ct)
    {
        var local = LocalTerms(question);
        if (Ai.Active() == null) return local;
        try
        {
            var user = (string.IsNullOrWhiteSpace(previousQuestion) ? "" : "Previous question (context): " + previousQuestion + "\n") + "Question: " + question;
            var j = Ai.ParseJson(await Ai.Chat(ExpandSystem, user, true, true, null, ct));
            var terms = (j["terms"] as JsonArray)?.Select(x => x?.ToString()?.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList() ?? new();
            return terms.Concat(local).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
        }
        catch (Exception e) { Log.Error("ask folder terms", e); return local; }
    }

    public static string Label(FolderIndex.Passage p)
    {
        var where = FolderIndex.Where(new PieceHit { Loc = p.Loc, Goto = p.Goto, Text = p.Text, Matches = new() });
        return $"{Path.GetFileName(p.Path)} · {where}";
    }

    public static string Extracts(List<FolderIndex.Passage> ps)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ps.Count; i++)
            sb.Append('[').Append(i + 1).Append("] ").Append(Label(ps[i])).Append('\n').Append(ps[i].Text.Trim()).Append("\n\n");
        return sb.ToString();
    }

    const string ShortRules =
        "You answer questions about project documents using ONLY the numbered extracts given. Reply in the language of the question. " +
        "Answer in 2 to 5 short lines. Give exact values (dimensions, ratings, codes, clause numbers, dates). After each fact put its source number in square brackets, e.g. [3]. " +
        "If two extracts disagree, write that line starting with ⚠ and cite both. " +
        "If the extracts do not contain the answer, reply with: NOT FOUND — then one short line suggesting other words to search.";

    const string DetailRules =
        "You answer questions about project documents using ONLY the numbered extracts given. Reply in the language of the question. " +
        "Give a detailed answer: all relevant requirements and values, short quotes of the key wording, the clause/page they come from, and how the documents relate. " +
        "Cite the source number in square brackets after each point, e.g. [3]. Point out every conflict or inconsistency with a line starting with ⚠. " +
        "End with a short 'Missing / to confirm' line if something needed is not in the extracts. Never invent facts.";

    /// <summary>The extracts go to the AI here — only after the user agreed (or chose "send without asking").</summary>
    public static async Task<string> Answer(string question, List<FolderIndex.Passage> ps, bool detailed, string history, CancellationToken ct)
    {
        var sys = (detailed ? DetailRules : ShortRules) + Modes.ProfileNote();
        var user = (string.IsNullOrWhiteSpace(history) ? "" : "Earlier in this conversation:\n" + history + "\n\n") +
                   "Extracts:\n" + Extracts(ps) + "Question: " + question;
        var a = await Ai.Chat(sys, user, !detailed, false, null, ct);
        History.Add("ask", Ai.Active()?.Id ?? "", "", detailed ? "folder-detail" : "folder", question, a);
        return a;
    }

    /// <summary>[3] → [Specification.pdf · page 14]</summary>
    public static string WithSources(string answer, List<FolderIndex.Passage> ps) =>
        Regex.Replace(answer ?? "", @"\[(\d+(?:\s*[,;]\s*\d+)*)\]", m =>
        {
            var parts = m.Groups[1].Value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x.Trim(), out var n) && n >= 1 && n <= ps.Count ? "[" + Label(ps[n - 1]) + "]" : null).Where(x => x != null).ToList();
            return parts.Count == 0 ? m.Value : string.Join(" ", parts);
        });

    /// <summary>Which passages the answer used (in order of first citation), then the others.</summary>
    public static List<int> CitedOrder(string answer, int count)
    {
        var order = new List<int>();
        foreach (Match m in Regex.Matches(answer ?? "", @"\d+"))
            if (int.TryParse(m.Value, out var n) && n >= 1 && n <= count && !order.Contains(n - 1) && Regex.IsMatch(answer, @"\[[^\]]*\b" + n + @"\b[^\]]*\]")) order.Add(n - 1);
        for (int i = 0; i < count; i++) if (!order.Contains(i)) order.Add(i);
        return order;
    }

    public static string Save(string question, string answer, List<FolderIndex.Passage> ps, string root)
    {
        var dir = Path.Combine(Paths.Root, "Search Results");
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, $"Ask AI {History.SafeName(question, 40)} {DateTime.Now:yyyy-MM-dd-HHmm}.txt");
        var sb = new StringBuilder();
        sb.Append("Question: ").Append(question).Append("\r\nFolder: ").Append(GDrive.Display(root)).Append("\r\nDate: ").Append(DateTime.Now.ToString("dd MMM yyyy HH:mm")).Append("\r\n\r\n");
        sb.Append(WithSources(answer, ps).Replace("\n", "\r\n")).Append("\r\n\r\nSOURCES\r\n");
        for (int i = 0; i < ps.Count; i++) sb.Append($"[{i + 1}] {ps[i].Path} · {Label(ps[i])}\r\n    {Regex.Replace(ps[i].Text, @"\s+", " ")}\r\n");
        sb.Append("\r\n").Append(Program.Sign).Append("\r\n");
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(true));
        return f;
    }
}

/// <summary>"Send N extracts to Gemini?" — shown before anything from the folder leaves the PC.</summary>
class ConfirmSend : NovaForm
{
    public enum Choice { Send, PassagesOnly, Cancel }
    public Choice Result = Choice.Cancel;

    public ConfirmSend(string title, string text, string details, string sendLabel, bool offerPassages)
    {
        Text = "Nova Enhance";
        Width = 600; Height = 330;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; TopMost = true;
        var s = new Stack();
        Controls.Add(s);
        s.Add(Th.L(title, 12.5f, Th.Text, true));
        s.Add(Th.Wrap(text, 10, Color.FromArgb(0xC9, 0xCE, 0xD8)));
        var box = s.Add(Th.Box(true, 8), true);
        box.ReadOnly = true; box.Text = details.Replace("\r\n", "\n").Replace("\n", "\r\n"); box.Visible = false; box.Font = Th.F(9);
        var see = Th.B("See what will be sent ▾", null);
        see.Click += (_, _) => { box.Visible = !box.Visible; Height = box.Visible ? 560 : 330; s.FitAll(); };
        s.Add(see);
        var row = s.Add(Th.Row(false));
        var ok = Th.B(sendLabel, () => { Result = Choice.Send; DialogResult = DialogResult.OK; }, true);
        row.Controls.Add(ok);
        if (offerPassages) row.Controls.Add(Th.B("Show passages only", () => { Result = Choice.PassagesOnly; DialogResult = DialogResult.OK; }));
        row.Controls.Add(Th.B("Cancel", () => { Result = Choice.Cancel; DialogResult = DialogResult.Cancel; }));
        AcceptButton = ok;
    }

    public static Choice Ask(IWin32Window owner, string title, string text, string details, string sendLabel, bool offerPassages)
    {
        using var f = new ConfirmSend(title, text, details, sendLabel, offerPassages);
        f.ShowDialog(owner);
        return f.Result;
    }
}
