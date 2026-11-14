using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// ✉ The email you are writing in Outlook: only YOUR text — from the top down to the closing ("Best Regards,"), your signature
/// or the earlier emails below, whichever comes first. Nova reads it and puts the result back into exactly that part, so the
/// closing, the signature (logo, phone numbers) and the quoted thread are never touched. Designed by A.HAMROUNI
/// </summary>
static class OutlookBody
{
    static readonly Regex Quote = new(@"\r\s*(From|De|Von|Van|Da|Från|Od|من)\s*:\s", RegexOptions.IgnoreCase);
    static readonly Regex Divider = new(@"\r[\-_*=]{8,}");

    /// <summary>The Word editor of the email being written (an open window, or the reply written inside the main window), or null.</summary>
    static dynamic Editor()
    {
        dynamic app = Outlook.Running();
        if (app == null) return null;
        try
        {
            dynamic insp = app.ActiveInspector();
            if (insp != null)
            {
                bool sent = true; try { sent = (bool)insp.CurrentItem.Sent; } catch { }
                if (!sent) return insp.WordEditor;
            }
        }
        catch { }
        try { dynamic ex = app.ActiveExplorer(); dynamic ed = ex?.ActiveInlineResponseWordEditor; if (ed != null) return ed; } catch { }
        return null;
    }

    /// <summary>The range of your own text (start, end) in the editor, or null.</summary>
    static (int Start, int End)? Span(dynamic doc)
    {
        string all = doc.Content.Text ?? "";
        int end = all.Length;
        try { if ((bool)doc.Bookmarks.Exists("_MailAutoSig")) end = Math.Min(end, (int)doc.Bookmarks.Item("_MailAutoSig").Range.Start); } catch { }
        var q = Quote.Match(all); if (q.Success) end = Math.Min(end, q.Index);
        var dv = Divider.Match(all); if (dv.Success) end = Math.Min(end, dv.Index);
        // The closing ("Best Regards,", "Thanks,") just above the signature stays yours, untouched.
        var head = all[..Math.Min(end, all.Length)];
        var lines = head.Split('\r');
        var starts = new int[lines.Length];
        for (int i = 1; i < lines.Length; i++) starts[i] = starts[i - 1] + lines[i - 1].Length + 1;
        var cut = SigLine(lines);
        if (cut >= 0) end = starts[cut];
        // Blank lines at the end stay as they are.
        while (end > 0 && char.IsWhiteSpace(all[end - 1])) end--;
        return end > 0 ? (0, end) : null;
    }

    static readonly Regex SigMark = new(@"^\s*(tel|t|fax|f|mob|mobile|m|phone|cell|direct|email|e-mail|e)\s*[.:]\s*[\d+(]|^\s*p\.?\s*o\.?\s*box\b|^\s*(www\.|https?://)\S+\s*$", RegexOptions.IgnoreCase);

    /// <summary>
    /// The first line of the closing / signature in your text (-1 = none): "Best Regards," within the last 20 lines; else your own
    /// name on a line of its own; else the block (after the last empty line) holding tel. / fax. / P.O. Box / www.
    /// </summary>
    public static int SigLine(string[] lines)
    {
        var idx = Enumerable.Range(0, lines.Length).Reverse().Where(i => lines[i].Trim().Length > 0).Take(20).ToList();
        foreach (var i in idx) if (Sig.ClosingRx.IsMatch(lines[i].Trim('\a', '\v', ' ', '\t'))) return i;
        var me = (Me.Name ?? "").Trim();
        if (me.Length > 3) foreach (var i in idx) if (string.Equals(lines[i].Trim(), me, StringComparison.OrdinalIgnoreCase)) return i;
        foreach (var i in idx.OrderBy(i => i))
            if (SigMark.IsMatch(lines[i]))
            {
                int b = i;
                while (b > 0 && lines[b - 1].Trim().Length > 0 && lines[b - 1].Trim().Length < 60) b--;
                return b;
            }
        return -1;
    }

    /// <summary>The Word editor of one email item (its open window, or the reply written inside the main window).</summary>
    public static dynamic EditorFor(dynamic item)
    {
        try
        {
            dynamic app = Outlook.Running();
            dynamic ex = app?.ActiveExplorer();
            dynamic inline = null; try { inline = ex?.ActiveInlineResponse; } catch { }
            if (inline != null && Same(inline, item)) return ex.ActiveInlineResponseWordEditor;
        }
        catch { }
        try { return item.GetInspector.WordEditor; } catch { return null; }
    }

    static bool Same(object a, object b)
    {
        IntPtr pa = IntPtr.Zero, pb = IntPtr.Zero;
        try { pa = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(a); pb = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(b); return pa == pb; }
        catch { return false; }
        finally { if (pa != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(pa); if (pb != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(pb); }
    }

    /// <summary>Your own text in this editor ("" when none).</summary>
    public static string ReadFrom(dynamic doc)
    {
        try
        {
            if (doc == null) return "";
            (int Start, int End)? sp = Span(doc);   // typed: through "dynamic" a null check would fail
            if (sp == null) return "";
            string t = doc.Range(sp.Value.Start, sp.Value.End).Text ?? "";
            return t.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n").Replace("\a", "").Replace("\v", "\r\n").Trim();
        }
        catch (Exception e) { Log.Warn("Outlook body", "read: " + e.Message); return ""; }
    }

    /// <summary>Replaces your own text in this editor — closing, signature and earlier emails untouched.</summary>
    public static bool ReplaceIn(dynamic doc, string text)
    {
        try
        {
            if (doc == null) return false;
            (int Start, int End)? sp = Span(doc);   // typed: through "dynamic" a null check would fail
            if (sp == null) return false;
            // Last guard: if what would be replaced still looks like a signature (your name alone on a line, tel. / fax. / P.O. Box / www),
            // Nova does not touch it.
            string cur = doc.Range(sp.Value.Start, sp.Value.End).Text ?? "";
            var me = (Me.Name ?? "").Trim();
            if (cur.Split('\r', '\n', '\v').Any(l => SigMark.IsMatch(l) || me.Length > 3 && string.Equals(l.Trim(), me, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Warn("Outlook body", "replace refused: the part to replace still looks like a signature");
                return false;
            }
            return Put(doc, sp.Value.Start, sp.Value.End, text);
        }
        catch (Exception e) { Log.Warn("Outlook body", "replace: " + e.Message); return false; }
    }

    /// <summary>Your text in the email being written, or "" (not Outlook, nothing written, or Outlook busy).</summary>
    public static string Read()
    {
        try
        {
            dynamic doc = Editor();
            if (doc == null) return "";
            (int Start, int End)? sp = Span(doc);   // typed: through "dynamic" a null check would fail
            if (sp == null) return "";
            string t = doc.Range(sp.Value.Start, sp.Value.End).Text ?? "";
            return t.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n").Replace("\a", "").Replace("\v", "\r\n").Trim();
        }
        catch (Exception e) { Log.Warn("Outlook body", "read: " + e.Message); return ""; }
    }

    /// <summary>Puts the new text in place of your text only — closing, signature and earlier emails untouched.</summary>
    public static bool Replace(string text) => ReplaceIn(Editor(), text);

    /// <summary>
    /// Outlook's HTML emails are made of blocks; Word sometimes refuses to delete across them ("The range cannot be deleted").
    /// So: 1 · the whole part at once · 2 · as if typed over the selection · 3 · paragraph by paragraph (each block keeps its place).
    /// </summary>
    static bool Put(dynamic doc, int start, int end, string text)
    {
        var t = (text ?? "").Replace("\r\n", "\n").TrimEnd();
        string why1 = "", why2 = "";
        try { dynamic r = doc.Range(start, end); r.Text = t.Replace("\n", "\r"); return true; }
        catch (Exception e) { why1 = e.Message; }
        try
        {
            dynamic r = doc.Range(start, end);
            r.Select();
            dynamic sel = doc.Application.Selection;
            sel.TypeText(t.Replace("\n", "\r"));
            Log.Write("Outlook body: replaced as typed (" + why1 + ")");
            return true;
        }
        catch (Exception e) { why2 = e.Message; }
        try
        {
            // Your paragraphs (with text) in order, and the new ones: each old one takes the matching new one.
            var olds = new List<(int S, int E)>();
            dynamic ps = doc.Range(start, end).Paragraphs;
            int n = (int)ps.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic pr = ps.Item(i).Range;
                int ps0 = Math.Max(start, (int)pr.Start), pe = Math.Min(end, (int)pr.End);
                string pt = doc.Range(ps0, pe).Text ?? "";
                while (pe > ps0 && (pt.EndsWith("\r") || pt.EndsWith("\a"))) { pe--; pt = pt[..^1]; }
                if (pt.Trim().Length > 0) olds.Add((ps0, pe));
            }
            var news = t.Split('\n').Select(x => x.TrimEnd()).Where(x => x.Trim().Length > 0).ToList();
            if (olds.Count == 0) return false;
            // Extra new paragraphs join the last old one (as new lines); extra old ones are emptied.
            if (news.Count > olds.Count) { var tail = string.Join("\r", news.Skip(olds.Count - 1)); news = news.Take(olds.Count - 1).Append(tail).ToList(); }
            for (int i = olds.Count - 1; i >= 0; i--)                 // from the end, so the earlier positions stay right
            {
                dynamic r = doc.Range(olds[i].S, olds[i].E);
                r.Text = i < news.Count ? news[i] : "";
            }
            Log.Write($"Outlook body: replaced paragraph by paragraph ({why1} · {why2})");
            return true;
        }
        catch (Exception e) { Log.Warn("Outlook body", $"replace refused 3 ways: {why1} · {why2} · {e.Message}"); return false; }
    }

    /// <summary>The window in front is Outlook.</summary>
    public static bool IsOutlook(IntPtr h) => Selection.AppName(h).Contains("Outlook", StringComparison.OrdinalIgnoreCase);
}
