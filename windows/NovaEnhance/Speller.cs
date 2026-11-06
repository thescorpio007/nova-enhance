using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

// ===================================================================== Windows spell checking (spellcheck.h), on this PC

[ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISpellCheckerFactory
{
    IEnumString SupportedLanguages { get; }
    int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
}

[ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISpellChecker
{
    string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
    IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
    IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
    void Add([MarshalAs(UnmanagedType.LPWStr)] string word);
    void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);
}

[ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IEnumSpellingError
{
    [PreserveSig] int Next(out ISpellingError value);
}

[ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISpellingError
{
    uint StartIndex { get; }
    uint Length { get; }
    int CorrectiveAction { get; }
    string Replacement { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
}

[ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
class SpellCheckerFactoryClass { }

/// <summary>A mistake in a box: red = spelling (this PC), blue = grammar (your AI).</summary>
class Mistake
{
    public int Start, Length;
    public bool Grammar;
    public string Word = "", Reason = "";
    public List<string> Fixes = new();
}

/// <summary>
/// Spelling with Windows' own checker (English, Arabic, French — instant, on this PC) and grammar with your AI when you pause.
/// Your words are never marked: Settings → About me terms, words you add, codes and references, names from your emails,
/// and the vocabulary Nova Memory learned. Designed by A.HAMROUNI
/// </summary>
static class Speller
{
    public static bool On { get => Store.Bool("win_spell", true); set => Store.SetBool("win_spell", value); }
    public static bool GrammarOn { get => Store.Bool("win_grammar", true); set => Store.SetBool("win_grammar", value); }
    public static bool SmartSearch { get => Store.Bool("win_smart_search", true); set => Store.SetBool("win_smart_search", value); }

    static ISpellChecker en, fr, ar;
    static bool tried;
    static HashSet<string> known;
    static DateTime knownAt;

    static void Init()
    {
        if (tried) return;
        tried = true;
        try
        {
            var f = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
            ISpellChecker Make(params string[] tags)
            {
                foreach (var t in tags) try { if (f.IsSupported(t) != 0) return f.CreateSpellChecker(t); } catch { }
                return null;
            }
            en = Make(System.Globalization.CultureInfo.CurrentCulture.Name.StartsWith("en") ? System.Globalization.CultureInfo.CurrentCulture.Name : "en-GB", "en-GB", "en-US");
            fr = Make("fr-FR");
            ar = Make("ar-SA", "ar-AE", "ar");
        }
        catch (Exception e) { Log.Warn("spelling", "Windows spell checker not available: " + e.Message); }
    }

    public static bool Available { get { Init(); return en != null; } }

    /// <summary>Words never marked: your terms, words you added, the vocabulary Nova learned, and names from your emails.</summary>
    static HashSet<string> Known()
    {
        if (known != null && DateTime.Now - knownAt < TimeSpan.FromMinutes(10)) return known;
        var k = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in Regex.Split(Store.ProfileTerms + " " + Store.Str("win_my_words"), @"[\s,;|]+")) if (w.Length > 1) k.Add(w.Trim('.', '"'));
        foreach (var w in Memory.VocabularyWords()) k.Add(w);
        foreach (var w in Regex.Split(Me.Name, @"[\s,]+")) if (w.Length > 1) k.Add(w);
        try { foreach (var n in MailIndex.KnownNames(3000)) foreach (var w in Regex.Split(n, @"[\s,.'()-]+")) if (w.Length > 1) k.Add(w); } catch { }
        known = k; knownAt = DateTime.Now;
        return k;
    }

    public static void AddWord(string w)
    {
        var words = Store.Str("win_my_words");
        if (!Regex.IsMatch(words, @"(^|\|)" + Regex.Escape(w) + @"($|\|)", RegexOptions.IgnoreCase)) Store.Set("win_my_words", words.Length == 0 ? w : words + "|" + w);
        try { en?.Add(w); } catch { }
        known = null;
    }

    static bool Skip(string word, HashSet<string> k) =>
        word.Length < 2 || k.Contains(word) || Regex.IsMatch(word, @"\d") || (word.Length <= 6 && word == word.ToUpperInvariant()) || word.Contains('_');

    static List<string> Suggest(ISpellChecker c, string w)
    {
        var res = new List<string>();
        try
        {
            var e = c.Suggest(w);
            var arr = new string[1];
            while (res.Count < 5 && e.Next(1, arr, IntPtr.Zero) == 0) res.Add(arr[0]);
        }
        catch { }
        return res;
    }

    /// <summary>Spelling mistakes in a text (paths, e-mail addresses, links, codes and your words are skipped).</summary>
    public static List<Mistake> Spelling(string text)
    {
        var res = new List<Mistake>();
        if (!On || string.IsNullOrWhiteSpace(text)) return res;
        if (Regex.IsMatch(text, @"^[A-Za-z]:\\|^\\\\|^https?://")) return res;
        Init();
        if (en == null) return res;
        var k = Known();
        try
        {
            void Run(ISpellChecker c, bool arabic)
            {
                var errs = c.Check(text);
                while (errs.Next(out var e) == 0 && e != null)
                {
                    int s = (int)e.StartIndex, l = (int)e.Length;
                    if (s < 0 || s + l > text.Length) continue;
                    var w = text.Substring(s, l);
                    bool isArabic = Regex.IsMatch(w, @"\p{IsArabic}");
                    if (arabic != isArabic || Skip(w, k)) continue;
                    // Part of a path, address, link or code? Leave it.
                    var a = s > 0 ? text[s - 1] : ' '; var b = s + l < text.Length ? text[s + l] : ' ';
                    if ("\\/@:._-".Contains(a) || "\\/@:_-".Contains(b)) continue;
                    if (!arabic && fr != null) { try { var fe = fr.Check(w); if (fe.Next(out var fx) != 0 || fx == null) continue; } catch { } }
                    var m = new Mistake { Start = s, Length = l, Word = w };
                    if (e.CorrectiveAction == 2 && !string.IsNullOrEmpty(e.Replacement)) m.Fixes.Add(e.Replacement);
                    foreach (var x in Suggest(c, w)) if (!m.Fixes.Contains(x)) m.Fixes.Add(x);
                    res.Add(m);
                }
            }
            Run(en, false);
            if (ar != null && Regex.IsMatch(text, @"\p{IsArabic}")) Run(ar, true);
        }
        catch (Exception e) { Log.Warn("spelling", e.Message); }
        return res.OrderBy(m => m.Start).ToList();
    }

    /// <summary>Grammar by your AI (only when Settings allows it): mistakes with the fix and a one-line reason.</summary>
    public static async Task<List<Mistake>> Grammar(string text, CancellationToken ct)
    {
        var res = new List<Mistake>();
        if (!GrammarOn || Ai.Active() == null || text.Trim().Length < 20) return res;
        var sys = "You check grammar for a professional (architect). Find real grammar mistakes only: agreement, tense, articles, prepositions, wrong word, capitalisation of days/months/names. " +
                  "Ignore spelling of codes, project names, abbreviations and style choices. Reply with JSON only: {\"issues\":[{\"bad\":\"exact words from the text\",\"fix\":\"corrected words\",\"why\":\"one short reason\"}]}. " +
                  "\"bad\" must be copied exactly from the text (2 to 6 words around the mistake). Empty list if the text is fine.";
        var j = Ai.ParseJson(await Ai.Chat(sys, text, true, true, null, ct));
        foreach (var it in (j["issues"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var bad = it["bad"]?.ToString() ?? ""; var fix = it["fix"]?.ToString() ?? "";
            if (bad.Length == 0 || fix.Length == 0 || bad == fix) continue;
            var i = text.IndexOf(bad, StringComparison.Ordinal);
            if (i < 0) continue;
            res.Add(new Mistake { Start = i, Length = bad.Length, Word = bad, Grammar = true, Reason = it["why"]?.ToString() ?? "", Fixes = new() { fix } });
        }
        return res;
    }

    /// <summary>Smart search: the text with spelling mistakes replaced by the first suggestion, and what changed.</summary>
    public static (string Text, List<(string From, string To)> Changes) Correct(string text)
    {
        var changes = new List<(string, string)>();
        if (!SmartSearch) return (text, changes);
        var ms = Spelling(text).Where(m => m.Fixes.Count > 0).OrderByDescending(m => m.Start).ToList();
        foreach (var m in ms)
        {
            text = text[..m.Start] + m.Fixes[0] + text[(m.Start + m.Length)..];
            changes.Insert(0, (m.Word, m.Fixes[0]));
        }
        return (text, changes);
    }

    /// <summary>For Ask / Search: fixes typos in the box first and shows "✓ corrected … ↩ search exactly as typed" under it.</summary>
    public static void FixBeforeSearch(TextBox box, Action rerun)
    {
        if (box is SpellBox sb && sb.ExactOnce) { sb.ExactOnce = false; return; }
        var typed = box.Text;
        var (fixedText, changes) = Correct(typed);
        if (changes.Count == 0) return;
        box.Text = fixedText;
        box.SelectionStart = fixedText.Length;
        if (box is SpellBox s)
            s.Note($"✓ Searched for “{string.Join(", ", changes.Select(c => c.To))}” — {changes.Count} typo{(changes.Count == 1 ? "" : "s")} corrected ({string.Join(", ", changes.Select(c => c.From + " → " + c.To))})   ↩ search exactly as typed",
                () => { s.ExactOnce = true; box.Text = typed; rerun?.Invoke(); });
    }
}

/// <summary>
/// Every Nova text box: red wavy line under spelling mistakes, blue under grammar; click a line for the fixes,
/// ＋ Add to my words, Ignore, ✦ Fix all. Ctrl+Space fixes the next mistake.
/// </summary>
class SpellBox : TextBox
{
    /// <summary>Grammar is checked in prose boxes (multi-line, or questions marked as prose).</summary>
    public bool Prose;
    /// <summary>Paths, file patterns and codes: no checking.</summary>
    public bool Off;
    public bool ExactOnce;
    List<Mistake> spelling = new(), grammar = new();
    readonly HashSet<string> ignored = new(StringComparer.OrdinalIgnoreCase);
    readonly System.Windows.Forms.Timer quick, slow;
    CancellationTokenSource gcts;
    string grammarFor = "";
    Label hint, note;
    Action noteUndo;

    public SpellBox()
    {
        quick = new System.Windows.Forms.Timer { Interval = 350 };
        quick.Tick += (_, _) => { quick.Stop(); CheckSpelling(); };
        slow = new System.Windows.Forms.Timer { Interval = 1600 };
        slow.Tick += (_, _) => { slow.Stop(); CheckGrammar(); };
        TextChanged += (_, _) =>
        {
            if (Off || UseSystemPasswordChar || ReadOnly) return;
            quick.Stop(); quick.Start();
            if (Prose || Multiline) { slow.Stop(); slow.Start(); }
            grammar.Clear();
        };
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Left && SelectionLength == 0) ShowMenuAt(GetCharIndexFromPosition(e.Location)); };
        KeyDown += (_, e) =>
        {
            if (note != null && !note.IsDisposed && note.Visible) note.Visible = false;
            if (e.Control && e.KeyCode == Keys.Space) { e.SuppressKeyPress = true; FixNext(); }
        };
        GotFocus += (_, _) => UpdateHint();
        LostFocus += (_, _) => { if (hint != null) hint.Visible = false; };
        Disposed += (_, _) => { quick.Dispose(); slow.Dispose(); hint?.Dispose(); note?.Dispose(); };
    }

    IEnumerable<Mistake> All => spelling.Concat(grammar).Where(m => !ignored.Contains(m.Word)).OrderBy(m => m.Start);

    void CheckSpelling()
    {
        if (IsDisposed || Off) return;
        spelling = Speller.Spelling(Text);
        Invalidate();
        UpdateHint();
    }

    async void CheckGrammar()
    {
        var t = Text;
        if (IsDisposed || Off || t.Length > 3000 || t == grammarFor || t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 5) return;
        grammarFor = t;
        gcts?.Cancel();
        var my = gcts = new CancellationTokenSource();
        try
        {
            var g = await Speller.Grammar(t, my.Token);
            if (my.IsCancellationRequested || IsDisposed || Text != t) return;
            // Grammar lines that only repeat a spelling mistake are dropped.
            grammar = g.Where(x => !spelling.Any(s => s.Start >= x.Start && s.Start < x.Start + x.Length && x.Length <= s.Length + 2)).ToList();
            Invalidate();
            UpdateHint();
        }
        catch { }
    }

    /// <summary>A clickable line under the box (smart search), until you type again.</summary>
    public void Note(string text, Action undo)
    {
        var f = FindForm();
        if (f == null) return;
        if (note == null || note.IsDisposed)
        {
            note = new Label { AutoSize = true, BackColor = Color.FromArgb(0x1D, 0x21, 0x30), ForeColor = Color.FromArgb(0xB9, 0xB0, 0xFF), Font = Th.F(9), Padding = new Padding(6, 3, 6, 3), Cursor = Cursors.Hand };
            note.Click += (_, _) => { note.Visible = false; var u = noteUndo; noteUndo = null; u?.Invoke(); };
            f.Controls.Add(note);
        }
        noteUndo = undo;
        note.Text = text;
        note.MaximumSize = new Size(Math.Max(300, Width + 200), 0);
        note.Location = f.PointToClient(PointToScreen(new Point(0, Height + 2)));
        note.Visible = true;
        note.BringToFront();
        var t = new System.Windows.Forms.Timer { Interval = 15000 };
        t.Tick += (_, _) => { t.Dispose(); if (note != null && !note.IsDisposed) note.Visible = false; };
        t.Start();
    }

    void UpdateHint()
    {
        if (Parent == null || IsDisposed) return;
        var n1 = spelling.Count(m => !ignored.Contains(m.Word)); var n2 = grammar.Count(m => !ignored.Contains(m.Word));
        if (n1 + n2 == 0 || !Focused) { if (hint != null) hint.Visible = false; return; }
        if (hint == null || hint.IsDisposed)
        {
            hint = new Label { AutoSize = true, BackColor = Color.FromArgb(0x1D, 0x21, 0x30), ForeColor = Th.Text, Font = Th.F(8.5f), Padding = new Padding(6, 2, 6, 2), Cursor = Cursors.Hand };
            hint.Click += (_, _) => FixAll();
        }
        hint.Text = $"{(n1 > 0 ? $"{n1} spelling" : "")}{(n1 > 0 && n2 > 0 ? " · " : "")}{(n2 > 0 ? $"{n2} grammar" : "")} · ✦ Fix all (Ctrl+Space = next)";
        var f = FindForm();
        if (f == null) return;
        if (hint.Parent != f) { hint.Parent?.Controls.Remove(hint); f.Controls.Add(hint); }
        var pt = f.PointToClient(PointToScreen(new Point(0, Height + 1)));
        if (note != null && !note.IsDisposed && note.Visible) pt.Y += note.Height + 2;
        hint.Location = pt;
        hint.Visible = true;
        hint.BringToFront();
    }

    void Replace(Mistake m, string with)
    {
        if (m.Start + m.Length > Text.Length || Text.Substring(m.Start, m.Length) != m.Word) { CheckSpelling(); return; }
        var caret = SelectionStart;
        Text = Text[..m.Start] + with + Text[(m.Start + m.Length)..];
        SelectionStart = Math.Min(Text.Length, caret + (caret > m.Start ? with.Length - m.Length : 0));
        Focus();
    }

    void FixNext()
    {
        var m = All.FirstOrDefault(x => x.Fixes.Count > 0 && x.Start + x.Length >= SelectionStart) ?? All.FirstOrDefault(x => x.Fixes.Count > 0);
        if (m != null) Replace(m, m.Fixes[0]);
    }

    void FixAll()
    {
        var t = Text;
        foreach (var m in All.Where(x => x.Fixes.Count > 0).OrderByDescending(x => x.Start))
            if (m.Start + m.Length <= t.Length && t.Substring(m.Start, m.Length) == m.Word) t = t[..m.Start] + m.Fixes[0] + t[(m.Start + m.Length)..];
        if (t != Text) { Text = t; SelectionStart = t.Length; }
        Focus();
    }

    void ShowMenuAt(int index)
    {
        var m = All.FirstOrDefault(x => index >= x.Start && index <= x.Start + x.Length);
        if (m == null) return;
        var menu = new ContextMenuStrip { Font = Th.F(10), ShowImageMargin = false };
        menu.Items.Add(new ToolStripLabel((m.Grammar ? "GRAMMAR" : "SPELLING") + $" · “{m.Word}”") { ForeColor = Color.Gray, Font = Th.F(8.5f, true) });
        foreach (var f in m.Fixes.Take(5))
        {
            var fx = f;
            var it = menu.Items.Add(fx, null, (_, _) => Replace(m, fx));
            it.Font = Th.F(10, true);
        }
        if (m.Fixes.Count == 0) menu.Items.Add(new ToolStripLabel("(no suggestion)") { ForeColor = Color.Gray });
        if (m.Grammar && m.Reason.Length > 0) menu.Items.Add(new ToolStripLabel(m.Reason) { ForeColor = Color.Gray });
        menu.Items.Add(new ToolStripSeparator());
        if (!m.Grammar) menu.Items.Add($"＋ Add “{m.Word}” to my words", null, (_, _) => { Speller.AddWord(m.Word); CheckSpelling(); });
        menu.Items.Add("Ignore here", null, (_, _) => { ignored.Add(m.Word); Invalidate(); UpdateHint(); });
        menu.Items.Add("✦ Fix all mistakes in this box     Ctrl+Space = next", null, (_, _) => FixAll());
        var pos = GetPositionFromCharIndex(m.Start);
        menu.Show(this, new Point(pos.X, pos.Y + Font.Height + 4));
    }

    protected override void WndProc(ref Message msg)
    {
        base.WndProc(ref msg);
        if (msg.Msg == 0x000F && !Off && !UseSystemPasswordChar) DrawLines();   // WM_PAINT
    }

    protected override void OnTextChanged(EventArgs e)
    {
        // Positions move while typing: drop lines that no longer match until the next check.
        spelling = spelling.Where(m => m.Start + m.Length <= Text.Length && Text.Substring(m.Start, m.Length) == m.Word).ToList();
        base.OnTextChanged(e);
    }

    void DrawLines()
    {
        var list = All.ToList();
        if (list.Count == 0) return;
        try
        {
            using var g = Graphics.FromHwnd(Handle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var len = Text.Length;
            foreach (var m in list)
            {
                if (m.Start + m.Length > len) continue;
                using var pen = new Pen(m.Grammar ? Color.FromArgb(0x6F, 0xA8, 0xFF) : Color.FromArgb(0xFF, 0x5C, 0x5C), 1.4f);
                // One wavy segment per line the word sits on.
                int segStart = m.Start;
                for (int i = m.Start; i < m.Start + m.Length; i++)
                {
                    bool last = i == m.Start + m.Length - 1;
                    var pi = GetPositionFromCharIndex(i);
                    var pn = last ? Point.Empty : GetPositionFromCharIndex(i + 1);
                    if (last || pn.Y != pi.Y)
                    {
                        var a = GetPositionFromCharIndex(segStart);
                        var endX = last || pn.Y != pi.Y ? pi.X + TextRenderer.MeasureText(g, Text[i].ToString(), Font, Size.Empty, TextFormatFlags.NoPadding).Width : pn.X;
                        Wave(g, pen, a.X, endX, a.Y + Font.Height - 1);
                        segStart = i + 1;
                    }
                }
            }
        }
        catch { }
    }

    static void Wave(Graphics g, Pen pen, int x1, int x2, int y)
    {
        if (x2 - x1 < 3) return;
        var pts = new List<PointF>();
        for (int x = x1, k = 0; x <= x2; x += 2, k++) pts.Add(new PointF(x, y + (k % 2 == 0 ? 0 : 2)));
        if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
    }
}
