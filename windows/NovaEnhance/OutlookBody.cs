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
        int seen = 0;
        for (int i = lines.Length - 1; i >= 0 && seen < 6; i--)
        {
            if (lines[i].Trim().Length == 0) continue;
            seen++;
            if (Sig.ClosingRx.IsMatch(lines[i])) { end = starts[i]; break; }
        }
        // Blank lines at the end stay as they are.
        while (end > 0 && char.IsWhiteSpace(all[end - 1])) end--;
        return end > 0 ? (0, end) : null;
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
            var sp = Span(doc);
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
            var sp = Span(doc);
            if (sp == null) return false;
            dynamic r = doc.Range(sp.Value.Start, sp.Value.End);
            r.Text = (text ?? "").Replace("\r\n", "\n").TrimEnd().Replace("\n", "\r");
            return true;
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
            var sp = Span(doc);
            if (sp == null) return "";
            string t = doc.Range(sp.Value.Start, sp.Value.End).Text ?? "";
            return t.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n").Replace("\a", "").Replace("\v", "\r\n").Trim();
        }
        catch (Exception e) { Log.Warn("Outlook body", "read: " + e.Message); return ""; }
    }

    /// <summary>Puts the new text in place of your text only — closing, signature and earlier emails untouched.</summary>
    public static bool Replace(string text)
    {
        try
        {
            dynamic doc = Editor();
            if (doc == null) return false;
            var sp = Span(doc);
            if (sp == null) return false;
            dynamic r = doc.Range(sp.Value.Start, sp.Value.End);
            r.Text = (text ?? "").Replace("\r\n", "\n").TrimEnd().Replace("\n", "\r");
            return true;
        }
        catch (Exception e) { Log.Warn("Outlook body", "replace: " + e.Message); return false; }
    }

    /// <summary>The window in front is Outlook.</summary>
    public static bool IsOutlook(IntPtr h) => Selection.AppName(h).Contains("Outlook", StringComparison.OrdinalIgnoreCase);
}
