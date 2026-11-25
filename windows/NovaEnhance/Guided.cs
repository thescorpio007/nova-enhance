using System.Text;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🎯 Guided mode, everywhere in Nova: you work inside a strict scope and a time limit, always by picking numbered options.
/// Every answer ends with "▶ Options" (numbered next steps shown as buttons; typing the number picks it), a question outside the scope
/// gets options instead of an answer, and when the time is up Nova offers to wrap up (+10 min, a summary, or end).
/// The default time is 15 min — change it in Settings → 🎯 Guided mode, or per chat with ⏱. Designed by A.HAMROUNI
/// </summary>
static class Guided
{
    public static bool On { get => Store.Bool("win_guided", true); set => Store.SetBool("win_guided", value); }
    /// <summary>The time limit of a guided chat or search, in minutes (0 = no limit).</summary>
    public static int Minutes { get => Math.Clamp(Store.Int("win_guided_min", 15), 0, 600); set => Store.SetInt("win_guided_min", Math.Clamp(value, 0, 600)); }
    /// <summary>🔍 Ask / Find opens 🎯 Guided search first (Where › What › Narrow › Find).</summary>
    public static bool SearchFirst { get => Store.Bool("win_guided_search", true); set => Store.SetBool("win_guided_search", value); }
    /// <summary>A new chat opens with Nova's numbered menu (no analysis until you pick).</summary>
    public static bool MenuFirst { get => Store.Bool("win_guided_menu", true); set => Store.SetBool("win_guided_menu", value); }

    public static readonly int[] Choices = { 5, 10, 15, 30, 60, 0 };
    public static string MinLabel(int m) => m <= 0 ? "no limit" : m + " min";

    public const string Head = "▶ Options";

    /// <summary>The rule added to the AI's instructions while guided mode is on.</summary>
    public static string Rule(string scope) => !On ? "" :
        " 🎯 GUIDED MODE: help the user make progress inside a strict scope — " + scope + ". Answer ONLY the step the user picked or asked, short and concrete (no long preamble). " +
        "If the prompt is outside this scope, do not answer it: say in one line that it is outside the scope, then give options that bring the work back into the scope (the last option may be \"Leave the scope — free chat\"). " +
        "If the prompt is only a greeting or is unclear, give no analysis: give only the options. " +
        "ALWAYS end your answer with a line exactly \"" + Head + "\" followed by 3 to 5 numbered next steps, one per line, like \"1. Check the fire rating against the specification\" — each one short (max 12 words), specific to this case and inside the scope.";

    static readonly Regex OptLine = new(@"^\s*(?:\*\*)?(\d{1,2})[\.\)]\s*(?:\*\*)?\s*(.+?)\s*$");

    /// <summary>The numbered options at the end of an answer.</summary>
    public static List<string> Options(string text)
    {
        var res = new List<string>();
        if (string.IsNullOrEmpty(text)) return res;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var at = Array.FindLastIndex(lines, l => l.Contains("▶ Options") || Regex.IsMatch(l.Trim(), @"^(\*\*)?▶?\s*Options\s*:?\s*(\*\*)?$", RegexOptions.IgnoreCase));
        if (at < 0) return res;
        for (int i = at + 1; i < lines.Length; i++)
        {
            var m = OptLine.Match(lines[i]);
            if (m.Success) res.Add(m.Groups[2].Value.Trim().TrimEnd('*').Trim());
            else if (lines[i].Trim().Length > 0 && res.Count > 0) break;
        }
        return res.Take(8).ToList();
    }

    /// <summary>The answer without its options block (for ⧉ Copy, ✦ Draft reply, Word).</summary>
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var at = lines.FindLastIndex(l => l.Contains("▶ Options"));
        if (at < 0) return text;
        var end = at + 1;
        while (end < lines.Count && (OptLine.IsMatch(lines[end]) || lines[end].Trim().Length == 0)) end++;
        lines.RemoveRange(at, end - at);
        return string.Join("\n", lines).TrimEnd();
    }

    /// <summary>Keeps the options of the first answer when the 🔎 technical check rewrote it without them.</summary>
    public static string KeepOptions(string before, string after)
    {
        if (!On || Options(after).Count > 0) return after;
        var o = Options(before);
        return o.Count == 0 ? after : after.TrimEnd() + "\n\n" + Head + "\n" + string.Join("\n", o.Select((x, i) => $"{i + 1}. {x}"));
    }

    /// <summary>Typing a bare number ("2") picks that option of the last answer.</summary>
    public static string Pick(string typed, string lastAnswer)
    {
        var m = Regex.Match(typed ?? "", @"^\s*(?:option\s*)?#?(\d{1,2})\s*\.?\s*$", RegexOptions.IgnoreCase);
        if (!m.Success) return typed;
        var o = Options(lastAnswer);
        var n = int.Parse(m.Groups[1].Value);
        return n >= 1 && n <= o.Count ? o[n - 1] : typed;
    }

    // ------------------------------------------------------------------ the opening menus (local: no AI call, instant)

    public static readonly string[] EmailMenu =
    {
        "Give me the key technical points of this email and what they ask from me",
        "Check it against the specification and the contract clauses",
        "Find the drawings and documents it refers to (latest revision)",
        "List the actions, deadlines and who must do what",
        "Compare the attachments with what was approved before",
        "Draft a short technical reply",
    };

    public static readonly string[] ListMenu =
    {
        "Which of these emails need my answer today? Most urgent first",
        "Only the emails of one project — ask me which one",
        "Hide meeting recaps and automatic notifications",
        "Summarise what was decided and what is still open",
        "Find the emails with drawings or submittals for approval",
    };

    public static readonly string[] ChatMenu =
    {
        "Find a file, a drawing or a document in my projects",
        "Find a clause inside a contract or a specification",
        "Check a submittal against the specification",
        "Prepare a reply or a letter",
        "Summarise a project's open items",
    };

    public static readonly string[] WrapUp =
    {
        "+10 min — keep going",
        "Summarise what we settled and the next steps",
        "End here — saved in 🕘 Chat history",
    };

    public const string Css =
        ".opts{margin-top:10px;display:flex;flex-wrap:wrap;gap:6px}.opts a.opt{display:inline-block;padding:6px 11px;border:1px solid #3B3470;background:#1D1A33;color:#D9D4FF;border-radius:16px;font-size:13px;text-decoration:none}" +
        ".opts a.opt:hover{background:#2B2550;color:#fff}.opts a.opt b{color:#9C8FFF;margin-right:5px}.menu{padding:12px 14px;border:1px solid #2A3142;border-radius:10px;background:#151A24;margin:6px 0 10px}" +
        ".menu .t{color:#B9B0FF;font-weight:bold;margin-bottom:4px}.menu .s{color:#9AA3B5;font-size:12.5px}.wrapup{border-color:#6B4A12;background:#211A0E}.wrapup .t{color:#FFC27A}";

    static string Esc(string t) => System.Net.WebUtility.HtmlEncode(t ?? "");

    /// <summary>Numbered buttons: https://nova.act/{act}{n}/{index}.</summary>
    public static string Buttons(IEnumerable<string> items, string act, int index) =>
        "<div class=opts>" + string.Join("", items.Select((o, i) => $"<a class=opt href=\"https://nova.act/{act}{i + 1}/{index}\"><b>{i + 1}</b>{Esc(o)}</a>")) + "</div>";

    /// <summary>The opening menu of a new guided chat.</summary>
    public static string MenuHtml(string title, string scope, IEnumerable<string> items) =>
        $"<div class=menu><div class=t>🎯 {Esc(title)}</div><div class=s>Scope: {Esc(scope)} · ⏱ {MinLabel(Minutes)} · pick a number (or type it), or write your own step. Nothing is analysed until you pick.</div>{Buttons(items, "menu", 0)}</div>";

    public static string WrapHtml(int index) =>
        $"<div class=\"menu wrapup\"><div class=t>⏱ Time is up</div><div class=s>How do you want to finish?</div>{Buttons(WrapUp, "wrap", index)}</div>";

    // ------------------------------------------------------------------ ⏱ the time limit of each chat

    public class Clock
    {
        public DateTime Start = DateTime.Now;
        public int Min = Minutes;
        public bool Started, Ended;
        public TimeSpan Left => Min <= 0 ? TimeSpan.MaxValue : Start.AddMinutes(Min) - DateTime.Now;
        public bool Up => Started && !Ended && Min > 0 && Left <= TimeSpan.Zero;
        public void Go() { if (!Started) { Started = true; Start = DateTime.Now; } }
        public void Add(int m) { if (Min <= 0) return; if (Up) { Start = DateTime.Now; Min = m; } else Min += m; }
        public void Set(int m) { Min = m; Start = DateTime.Now; Ended = false; }
        public string Label => Min <= 0 ? "⏱ no limit" : !Started ? $"⏱ {Min} min" : Up ? "⏱ time is up" : $"⏱ {Math.Max(1, (int)Math.Ceiling(Left.TotalMinutes))} min left";
    }

    /// <summary>A ⏱ button that shows the time left and changes the limit of this chat.</summary>
    public static Button TimerButton(Func<Clock> clock, Action changed)
    {
        var b = Th.B("⏱", () => { });
        b.Visible = On;
        b.Click += (_, _) =>
        {
            var c = clock(); var mm = new ContextMenuStrip();
            foreach (var m in Choices)
                mm.Items.Add((c.Min == m ? "✓ " : "   ") + "This chat: " + MinLabel(m), null, (_, _) => { clock().Set(m); changed?.Invoke(); b.Text = clock().Label; });
            mm.Items.Add(new ToolStripSeparator());
            mm.Items.Add("+10 min", null, (_, _) => { clock().Add(10); changed?.Invoke(); b.Text = clock().Label; });
            mm.Items.Add("⚙ Default for every chat…", null, (_, _) => Screens.Settings("🎯 Guided mode"));
            mm.Show(b, new Point(0, b.Height));
        };
        var t = new System.Windows.Forms.Timer { Interval = 15000 };
        var wasUp = false;
        t.Tick += (_, _) =>
        {
            if (b.IsDisposed) { t.Dispose(); return; }
            b.Visible = On; var c = clock(); b.Text = c.Label; b.ForeColor = c.Up ? Th.Warn : Th.Text;
            if (c.Up && !wasUp) changed?.Invoke();   // ⏱ time is up → the wrap-up options appear
            wasUp = c.Up;
        };
        t.Start();
        b.Disposed += (_, _) => t.Dispose();
        b.Text = clock().Label;
        return b;
    }

    /// <summary>The words of a menu / wrap-up button (menuN / wrapN / pickN), or null.</summary>
    public static (string Kind, int N) Parse(string what)
    {
        var m = Regex.Match(what ?? "", @"^(menu|wrap|pick)(\d{1,2})$");
        return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value)) : (null, 0);
    }

    /// <summary>The prompt for the wrap-up summary.</summary>
    public const string SummaryAsk = "Time is up. Summarise in 5 lines max what we settled in this chat (facts, decisions, files found) and the 3 next steps — no new analysis.";
}

/// <summary>↔ Flexible split windows: double-click a divider to give the first side (the email, the list) almost all the room, double-click again to go back.</summary>
static class Flex
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SplitContainer, object> saved = new();

    public static void DoubleClickMax(SplitContainer s, Action moved = null)
    {
        s.MouseDoubleClick += (_, _) =>
        {
            try
            {
                if (s.Panel1Collapsed || s.Panel2Collapsed) return;
                var whole = s.Orientation == Orientation.Vertical ? s.Width : s.Height;
                var max = whole - s.Panel2MinSize - s.SplitterWidth;
                if (s.SplitterDistance < max - 20)
                {
                    saved.AddOrUpdate(s, s.SplitterDistance);
                    s.SplitterDistance = max;
                }
                else s.SplitterDistance = Math.Clamp(saved.TryGetValue(s, out var o) && o is int d ? d : whole / 2, s.Panel1MinSize, max);
                moved?.Invoke();
            }
            catch { }
        };
    }
}
