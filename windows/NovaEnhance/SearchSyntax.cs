using System.Globalization;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🔎 The Windows / Outlook search rules, in every Nova search box: "exact phrase", AND, OR, NOT / -word, ( ), wildcard *,
/// from: to: cc: subject: attachment: hasattachment: received: / date:, kind: ext: folder:, and Nova's project: and rev:.
/// Quoted phrases, subject: and -words are HARD rules — checked on every result after the AI, so nothing can break them.
/// Exact phrases forgive spaces, hyphens, capitals and plurals ("block wall" = "Block-Wall", "block walls"). Designed by A.HAMROUNI
/// </summary>
class SearchSyntax
{
    public string Free = "";                       // what is left for the AI / word search
    public List<string> Phrases = new(), Subject = new(), Not = new(), Attach = new(), From = new(), To = new(), Cc = new();
    public List<List<string>> Or = new();          // groups written with OR
    public List<string> Wild = new();              // block* · SSM-001*
    public List<string> Kinds = new(), Exts = new(), Folders = new();
    public string Project = "", Rev = "";
    public bool? HasAttach;
    public DateTime? After, Before;
    public bool Any => Phrases.Count + Subject.Count + Not.Count + Attach.Count + From.Count + To.Count + Cc.Count + Or.Count + Wild.Count + Kinds.Count + Exts.Count + Folders.Count > 0 ||
                       Project.Length > 0 || Rev.Length > 0 || HasAttach != null || After != null || Before != null;

    static readonly Regex Tok = new(@"-?(?:[A-Za-z]+:)?""[^""]*""|\(|\)|[^\s()]+", RegexOptions.Compiled);

    public static SearchSyntax Parse(string q)
    {
        var s = new SearchSyntax();
        var free = new List<string>();
        q = (q ?? "").Replace('“', '"').Replace('”', '"').Replace('„', '"');
        var toks = Tok.Matches(q).Select(m => m.Value).ToList();
        bool neg = false;
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t is "(" or ")") continue;
            if (t == "AND") continue;
            if (t == "NOT") { neg = true; continue; }
            if (t == "OR")
            {
                // a OR b: the last free item and the next one become one either-or group.
                if (i + 1 < toks.Count && free.Count > 0)
                {
                    var prev = free[^1]; free.RemoveAt(free.Count - 1);
                    var g = s.Or.FirstOrDefault(x => x.Contains(prev)) ?? new List<string> { prev };
                    if (!s.Or.Contains(g)) s.Or.Add(g);
                    var next = Unquote(toks[++i]);
                    g.Add(next);
                    free.Add(prev); free.Add(next);   // still searched (any of them)
                }
                continue;
            }
            bool minus = neg || (t.StartsWith('-') && t.Length > 1 && !Regex.IsMatch(t, @"^-\d"));
            neg = false;
            if (t.StartsWith('-') && minus) t = t[1..];
            var fm = Regex.Match(t, @"^([A-Za-z]+):(.*)$");
            if (fm.Success && fm.Groups[2].Value.Length > 0)
            {
                var key = fm.Groups[1].Value.ToLowerInvariant();
                var val = Unquote(fm.Groups[2].Value);
                // received:last week — the value can be two words.
                if (key is "received" or "date" or "sent" or "datemodified" or "modified" && Regex.IsMatch(val, "^(last|this|past)$", RegexOptions.IgnoreCase) && i + 1 < toks.Count) val += " " + toks[++i];
                switch (key)
                {
                    case "from": s.From.Add(val); continue;
                    case "to": s.To.Add(val); continue;
                    case "cc": s.Cc.Add(val); continue;
                    case "subject": if (minus) s.Not.Add(val); else s.Subject.Add(val); continue;
                    case "attachment" or "attachments" or "file": s.Attach.Add(val); continue;
                    case "hasattachment" or "hasattachments": s.HasAttach = !Regex.IsMatch(val, "^(no|false|0)$", RegexOptions.IgnoreCase); continue;
                    case "received" or "date" or "sent" or "datemodified" or "modified": Dates(s, val); continue;
                    case "kind" or "type": s.Kinds.Add(val.ToLowerInvariant()); continue;
                    case "ext": s.Exts.Add("." + val.TrimStart('.').ToLowerInvariant()); continue;
                    case "folder" or "path": s.Folders.Add(val); continue;
                    case "project": s.Project = val.ToUpperInvariant(); continue;
                    case "rev" or "revision": s.Rev = val; continue;
                }
                if (!fm.Value.Contains("://")) { /* unknown field: keep as text */ }
            }
            var v = Unquote(t);
            if (v.Length == 0) continue;
            if (minus) { s.Not.Add(v); continue; }
            if (t.StartsWith('"')) { s.Phrases.Add(v); free.Add(v); continue; }
            if (v.Contains('*')) { s.Wild.Add(v); free.Add(v.Replace("*", "")); continue; }
            free.Add(v);
        }
        s.Free = string.Join(" ", free.Select(f => f.Contains(' ') ? "\"" + f + "\"" : f));
        return s;
    }

    static string Unquote(string v) => v.Trim().Trim('"').Trim();

    static void Dates(SearchSyntax s, string v)
    {
        var now = DateTime.Now; v = v.Trim().ToLowerInvariant();
        DateTime? D(string x) => DateTime.TryParseExact(x, new[] { "d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "d-M-yyyy", "d.M.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        Match m;
        if (v == "today") { s.After = now.Date; return; }
        if (v == "yesterday") { s.After = now.Date.AddDays(-1); s.Before = now.Date; return; }
        if (v is "this week") { s.After = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)); return; }
        if (v is "last week") { var mon = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)); s.After = mon.AddDays(-7); s.Before = mon; return; }
        if (v is "this month") { s.After = new DateTime(now.Year, now.Month, 1); return; }
        if (v is "last month") { var f = new DateTime(now.Year, now.Month, 1); s.After = f.AddMonths(-1); s.Before = f; return; }
        if (v is "this year") { s.After = new DateTime(now.Year, 1, 1); return; }
        if (v is "last year") { s.After = new DateTime(now.Year - 1, 1, 1); s.Before = new DateTime(now.Year, 1, 1); return; }
        if ((m = Regex.Match(v, @"^(\d{4})$")).Success) { var y = int.Parse(v); s.After = new DateTime(y, 1, 1); s.Before = new DateTime(y + 1, 1, 1); return; }
        if ((m = Regex.Match(v, @"^([<>]=?)(.+)$")).Success && D(m.Groups[2].Value) is { } d1)
        {
            if (m.Groups[1].Value.StartsWith('>')) s.After = m.Groups[1].Value == ">" ? d1.AddDays(1) : d1; else s.Before = m.Groups[1].Value == "<" ? d1 : d1.AddDays(1);
            return;
        }
        if ((m = Regex.Match(v, @"^(.+?)\.\.(.+)$")).Success && D(m.Groups[1].Value) is { } a && D(m.Groups[2].Value) is { } b) { s.After = a; s.Before = b.AddDays(1); return; }
        if (D(v) is { } d2) { s.After = d2; s.Before = d2.AddDays(1); }
    }

    // ------------------------------------------------------------------ checking (forgiving: spaces, hyphens, capitals, plurals)

    /// <summary>"block wall" → matches "Block-Wall", "block walls", "BLOCK  WALL" (not "blockwork").</summary>
    public static Regex PhraseRx(string phrase) => PhraseRx(phrase, 0);

    /// <summary>gap = how many other words may sit between the words of the phrase ("fire hose reel cabinet" for "fire hose cabinet").</summary>
    public static Regex PhraseRx(string phrase, int gap)
    {
        var words = Regex.Split(phrase.Trim(), @"[\s\-_./]+").Where(w => w.Length > 0).ToList();
        string W(string w)
        {
            var e = Regex.Escape(w).Replace(@"\*", @"[\p{L}\p{N}]*");
            return Regex.IsMatch(w, @"[A-Za-z]$") && !w.EndsWith("*") ? e + "(?:e?s)?" : e;
        }
        var join = gap > 0 ? @"[\s\-_./]*(?:[\p{L}\p{N}]{1,20}[\s\-_./]+){0," + gap + "}" : @"[\s\-_./]*";
        var core = string.Join(join, words.Select(W));
        return new Regex(@"(?<![\p{L}\p{N}])" + core + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    static bool Has(string text, string phrase) { try { return PhraseRx(phrase).IsMatch(text ?? ""); } catch { return (text ?? "").Contains(phrase, StringComparison.OrdinalIgnoreCase); } }

    /// <summary>The hard rules for an email.</summary>
    public bool Pass(MailHit h)
    {
        var subj = h.Subject ?? ""; var att = h.AttNames ?? ""; var all = subj + "\n" + att + "\n" + (h.Body ?? "");
        foreach (var p in Phrases) if (!Has(all, p)) return false;
        foreach (var p in Subject) if (!Has(subj, p)) return false;
        foreach (var p in Attach) if (!Has(att, p)) return false;
        foreach (var w in Wild) if (!Has(all, w)) return false;
        foreach (var g in Or) if (!g.Any(x => Has(all, x))) return false;
        foreach (var n in Not) if (Has(subj + "\n" + att + "\n" + h.NewPart, n)) return false;
        foreach (var f in From) if (!((h.FromName ?? "") + " " + (h.FromAddr ?? "")).Contains(f, StringComparison.OrdinalIgnoreCase) && !(f.Equals("me", StringComparison.OrdinalIgnoreCase) && h.Sent)) return false;
        foreach (var t in To) if (!((h.To ?? "") + " " + (h.ToAddr ?? "")).Contains(t, StringComparison.OrdinalIgnoreCase) && !(t.Equals("me", StringComparison.OrdinalIgnoreCase) && !h.Sent)) return false;
        foreach (var c in Cc) if (!((h.Cc ?? "") + " " + (h.CcAddr ?? "")).Contains(c, StringComparison.OrdinalIgnoreCase)) return false;
        if (HasAttach == true && att.Length == 0) return false;
        if (HasAttach == false && att.Length > 0) return false;
        if (After is { } a && h.Date < a) return false;
        if (Before is { } b && h.Date >= b) return false;
        return true;
    }

    /// <summary>The hard rules for a file (name, path, date, type, revision).</summary>
    public bool PassFile(string path, DateTime modified, string rev = "", string text = "")
    {
        var name = Path.GetFileName(path) ?? ""; var hay = path + "\n" + text;
        foreach (var p in Phrases) if (!Has(hay, p)) return false;
        foreach (var p in Subject) if (!Has(name, p)) return false;
        foreach (var w in Wild) if (!Has(hay, w)) return false;
        foreach (var g in Or) if (!g.Any(x => Has(hay, x))) return false;
        foreach (var n in Not) if (Has(path, n)) return false;
        foreach (var f in Folders) if (!Has(Path.GetDirectoryName(path) ?? "", f)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (Exts.Count > 0 && !Exts.Contains(ext)) return false;
        if (Kinds.Count > 0 && !Kinds.Any(k => KindOk(k, ext))) return false;
        if (After is { } a && modified < a) return false;
        if (Before is { } b && modified >= b) return false;
        if (Rev.Length > 0 && !Rev.Equals("latest", StringComparison.OrdinalIgnoreCase) && !rev.Equals(Rev, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    static bool KindOk(string k, string ext) => k switch
    {
        "pdf" => ext == ".pdf",
        "word" or "doc" or "document" or "documents" => ext is ".doc" or ".docx" or ".rtf" or ".odt" || (k is "document" or "documents" && ext == ".pdf"),
        "excel" or "xls" or "sheet" or "spreadsheet" => ext is ".xls" or ".xlsx" or ".xlsm" or ".csv",
        "ppt" or "powerpoint" or "presentation" => ext is ".ppt" or ".pptx",
        "cad" or "dwg" or "drawing" or "drawings" => ext is ".dwg" or ".dxf" or ".dgn" || (k is "drawing" or "drawings" && ext == ".pdf"),
        "revit" or "bim" or "model" => ext is ".rvt" or ".rfa" or ".ifc" or ".nwd" or ".nwc",
        "picture" or "image" or "photo" or "pictures" => ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".heic",
        "email" or "msg" => ext is ".msg" or ".eml",
        "video" => ext is ".mp4" or ".mov" or ".avi" or ".mkv",
        _ => ext.TrimStart('.') == k
    };

    /// <summary>What the "Understood as" line shows: 🔒 must contain "block wall" · ✗ MEP · 📁 SLV …</summary>
    public string Describe()
    {
        var p = new List<string>();
        foreach (var x in Phrases) p.Add($"🔒 must contain \"{x}\"");
        foreach (var x in Subject) p.Add($"🔒 subject has \"{x}\"");
        foreach (var x in Attach) p.Add($"📎 attachment \"{x}\"");
        foreach (var g in Or) p.Add("either " + string.Join(" or ", g.Select(x => $"\"{x}\"")));
        foreach (var x in Wild) p.Add($"✱ {x}");
        foreach (var x in Not) p.Add($"✗ {x}");
        foreach (var x in From) p.Add($"from {x}");
        foreach (var x in To) p.Add($"to {x}");
        foreach (var x in Cc) p.Add($"cc {x}");
        if (HasAttach == true) p.Add("📎 with attachments"); if (HasAttach == false) p.Add("no attachments");
        if (After != null) p.Add($"from {After:dd MMM yyyy}");
        if (Before != null) p.Add($"before {Before:dd MMM yyyy}");
        foreach (var x in Kinds) p.Add($"kind {x}"); foreach (var x in Exts) p.Add($"ext {x}"); foreach (var x in Folders) p.Add($"folder \"{x}\"");
        if (Project.Length > 0) p.Add($"📁 {Project}");
        if (Rev.Length > 0) p.Add($"rev {Rev}");
        return string.Join(" · ", p);
    }

    // ------------------------------------------------------------------ the "?" help (same in every search box)

    public static readonly (string Type, string Means, string Example, bool Mail, bool Files)[] Help =
    {
        ("\"block wall\"", "Exact phrase — must be in every result (spaces, hyphens, capitals, plurals forgiven)", "\"block wall\" shop drawing", true, true),
        ("AND / space", "All the words", "FHC AND approved", true, true),
        ("OR", "Either word", "blockwork OR \"block wall\"", true, true),
        ("NOT  /  -word", "Leave out", "SDS -MEP", true, true),
        ("( )", "Group", "(FHC OR \"fire hose\") approved", true, true),
        ("*", "Wildcard", "SSM-001*", true, true),
        ("from:  to:  cc:", "A person (me = you)", "from:Mark", true, false),
        ("subject:", "Only in the subject", "subject:\"block wall\"", true, false),
        ("hasattachment:yes", "With files", "hasattachment:yes SDS", true, false),
        ("attachment:", "In an attachment name", "attachment:SDS-00656", true, false),
        ("received:  date:", "Date: today · this week · last month · 2025 · >01/05/2026 · 01/01/2025..31/03/2025", "received:last week", true, true),
        ("project:", "Nova: one project only", "project:SLV", true, true),
        ("kind:  ext:", "File type: pdf · word · excel · cad · revit · picture", "kind:pdf", false, true),
        ("folder:", "Inside a folder name", "folder:\"Shop drawings\"", false, true),
        ("rev:", "Nova: a revision (or latest)", "rev:C0", false, true)
    };

    /// <summary>A small "?" button: the table above with examples you can click (they go into the box).</summary>
    public static Button HelpButton(TextBox box, bool mail, Action run = null)
    {
        var b = Th.B("?", null);
        b.Margin = new Padding(6, 0, 0, 0);
        new ToolTip().SetToolTip(b, "Search tricks (the same as Windows / Outlook search)");
        b.Click += (_, _) =>
        {
            using var f = new NovaForm { Text = "🔎 Search tricks — the same as Windows / Outlook search", Width = 900, Height = 600, StartPosition = FormStartPosition.CenterParent };
            var st = new Stack();
            f.Controls.Add(st);
            st.Add(Th.Wrap("Type these in any Nova search box. Words in \"quotes\", subject: and -words are hard rules: every result must follow them, even when the ✦ AI chooses. Click an example to use it.", 10, Th.Text));
            foreach (var h in Help.Where(x => mail ? x.Mail : x.Files))
            {
                var row = st.Add(new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Th.Card, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 2, 0, 2) }, true);
                var t = Th.L(h.Type, 10.5f, Th.AccentText, true); t.AutoSize = false; t.Width = 170; t.Height = 24;
                var m = Th.L(h.Means, 9.5f, Th.Text); m.AutoSize = false; m.Width = 430; m.Height = 34;
                var ex = h.Example;
                var e = Th.B(ex, () => { box.Text = ex; f.Close(); box.Focus(); run?.Invoke(); });
                e.Font = Th.F(9.5f);
                row.Controls.Add(t); row.Controls.Add(m); row.Controls.Add(e);
            }
            st.Add(Th.Wrap("Without these tricks, ✦ AI search still understands plain questions (short forms like FHC, BOH, SS; dates like “before May 2026”; the project you name).", 9.5f, Th.Muted));
            f.ShowDialog(box.FindForm());
        };
        return b;
    }
}
