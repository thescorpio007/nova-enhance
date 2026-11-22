using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🔗 Excel and Word exports with links to the emails: “✉ Open” (a nova: link — opens the email in Nova's view on this PC, from any
/// mailbox or archive) and “📎 Email file” (the email saved as an .msg file in the Emails folder beside the export — anyone at work opens
/// it in their own Outlook, with its attachments). The export folder is also zipped, ready to send. 🔒 Private emails are never saved.
/// Designed by A.HAMROUNI
/// </summary>
static class Export
{
    public const char L1 = '\u0001', L2 = '\u0002';

    /// <summary>A cell that is a link (Excel: =HYPERLINK, Word: a hyperlink).</summary>
    public static string Link(string url, string text) => L1 + url + L2 + text;

    public static string NovaUrl(long id) => "nova:mail/" + id;

    static string Safe(string s, int max)
    {
        s = Regex.Replace(s ?? "", @"[\\/:*?""<>|\x00-\x1F]+", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '.');
        return s.Length > max ? s[..max].Trim() : s;
    }

    /// <summary>One folder per export: …\Exports\&lt;title date&gt;\ (the file, its Emails folder, and a zip next to it).</summary>
    public static (string Dir, string Name) Folder(string root, string title)
    {
        var t = Safe(title, 60);
        var name = $"{(t.Length > 0 ? t : "Nova export")} — {DateTime.Now:dd MMM yyyy HHmm}";
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return (dir, name);
    }

    static bool Private(MailHit h) => Projects.Private.IsMatch((h.Subject ?? "") + " " + (h.Folder ?? "")) || (h.Folder ?? "").Contains("TrustWalletBackup", StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves the emails as .msg files in dir\Emails (newest first, at most <paramref name="max"/>) → id → “Emails\file.msg”.</summary>
    public static Dictionary<long, string> SaveEmails(string dir, IEnumerable<MailHit> mails, int max = 300)
    {
        var res = new Dictionary<long, string>();
        var list = mails.Where(h => h != null && h.Id > 0 && !string.IsNullOrEmpty(h.Entry) && !h.Entry.StartsWith("pst:") && !Private(h))
            .GroupBy(h => h.Id).Select(g => g.First()).OrderByDescending(h => h.Date).Take(max).ToList();
        if (list.Count == 0) return res;
        var ed = Path.Combine(dir, "Emails");
        Directory.CreateDirectory(ed);
        using var busy = Busy.Begin($"Saving {list.Count} email file(s) for the export");
        foreach (var h in list)
        {
            var doc = Regex.Match(h.Subject ?? "", @"[A-Z0-9]{2,}(?:-[A-Z0-9]{1,8}){3,}") is { Success: true } m ? m.Value : Safe(Regex.Replace(h.Subject ?? "email", @"^\s*((re|fw|fwd)\s*:\s*)+", "", RegexOptions.IgnoreCase), 50);
            var file = Safe($"{doc} {h.Date:yyyy-MM-dd HHmm}", 90) + ".msg";
            var path = Path.Combine(ed, file);
            try
            {
                if (!File.Exists(path))
                {
                    var (e, st) = (h.Entry, h.StoreId);
                    var mv = MailIndex.MovedTo(e);
                    if (!string.IsNullOrEmpty(mv.Entry)) (e, st) = mv;
                    Outlook.SaveMsg(e, st, path);
                }
                res[h.Id] = "Emails\\" + file;
            }
            catch (Exception x) { Log.Warn("export · email file", x.Message); }
        }
        return res;
    }

    /// <summary>The export folder as one zip (beside it) — to send to a colleague.</summary>
    public static string Zip(string dir)
    {
        var zip = dir + ".zip";
        try { if (File.Exists(zip)) File.Delete(zip); ZipFile.CreateFromDirectory(dir, zip, CompressionLevel.Optimal, true); return zip; }
        catch (Exception e) { Log.Warn("export · zip", e.Message); return ""; }
    }

    public static void Readme(string dir)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, "READ ME.txt"),
                "Made by Nova Enhance · Designed by A.HAMROUNI\r\n\r\n" +
                "✉ Open  — opens the email in Nova on the PC that made this file.\r\n" +
                "📎 Email file — the email itself (with its attachments), in the Emails folder: opens in anyone's Outlook.\r\n" +
                "Keep the Emails folder next to the Excel / Word file (or send the .zip) so the links keep working.\r\n", Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>First export: the email files hold the full text and attachments — said once.</summary>
    public static void Notice()
    {
        if (Store.Bool("win_export_notice", false)) return;
        Store.SetBool("win_export_notice", true);
        try
        {
            MessageBox.Show("Excel and Word exports now link every row to its email:\n\n" +
                            "✉ Open — opens the email in Nova on this PC.\n" +
                            "📎 Email file — the email saved as a file (full text and attachments) in an \"Emails\" folder beside the export, " +
                            "so a colleague can open it in their own Outlook. A .zip of the whole export is made too.\n\n" +
                            "🔒 Emails with private words are never saved. Check what you send before sharing it.", "Nova Enhance · exports", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch { }
    }

    static List<MailHit> MailsOf(Agent.Result r)
    {
        var have = r.Rows.Where(x => x.Mail != null).Select(x => x.Mail).ToList();
        var miss = r.Rows.Where(x => x.Mail == null && x.Id > 0).Select(x => x.Id).Distinct().ToList();
        if (miss.Count > 0) try { have.AddRange(MailIndex.ByIds(miss, miss.Count + 5, 0)); } catch { }
        return have;
    }

    // ------------------------------------------------------------------ Excel (the agent's tables)

    /// <summary>⬇ The agent's table as Excel, with ✉ Open and 📎 Email file links → the .xlsx path.</summary>
    public static string AgentExcel(Agent.Result r, string title)
    {
        Notice();
        var (dir, name) = Folder(Path.Combine(Paths.Root, "Exports"), title);
        var mails = MailsOf(r);
        var files = SaveEmails(dir, mails, 400);
        var byId = mails.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var head = r.Columns.Concat(new[] { "✉ Open (my PC)", "📎 Email file (anyone)" }).ToArray();
        var rows = r.Rows.Select(x =>
        {
            var cells = x.Cells.Concat(Enumerable.Repeat("", Math.Max(0, r.Columns.Count - x.Cells.Count))).Take(r.Columns.Count).ToList();
            cells.Add(x.Id > 0 ? Link(NovaUrl(x.Id), "✉ Open") : "");
            cells.Add(x.Id > 0 && files.TryGetValue(x.Id, out var f) ? Link(f, "📎 " + Path.GetFileNameWithoutExtension(f)) : "");
            return cells.ToArray();
        }).ToList();
        var widths = r.Columns.Select((c, i) => Math.Clamp(r.Rows.Select(x => i < x.Cells.Count ? x.Cells[i].Length : 0).DefaultIfEmpty(10).Max() + 2, Math.Max(10, c.Length + 2), 70)).Concat(new[] { 14, 40 }).ToArray();
        var sub = Regex.Replace(r.Text ?? "", @"\s+", " ");
        if (sub.Length > 300) sub = sub[..300] + "…";
        var sheets = new List<SubmittalLog.XSheet>
        {
            new("Result", "🧭 " + title, sub, head, widths, rows, -1),
            new("About", "ℹ About this export", "", null, new[] { 22, 120 }, new List<string[]>
            {
                new[] { "Made by", "Nova Enhance · 🧭 agent · " + Program.Shown + " · Designed by A.HAMROUNI" },
                new[] { "Made on", DateTime.Now.ToString("dd MMM yyyy HH:mm") },
                new[] { "✉ Open", "opens the email in Nova on the PC that made this file" },
                new[] { "📎 Email file", $"the email itself in the Emails folder ({files.Count} saved) — opens in anyone's Outlook; send the .zip to keep the links working" }
            }, -1)
        };
        var path = Path.Combine(dir, name + ".xlsx");
        SubmittalLog.WriteBook(path, sheets);
        Readme(dir);
        Zip(dir);
        return path;
    }

    // ------------------------------------------------------------------ Word (the agent's answer)

    static string Xe(string s) => System.Security.SecurityElement.Escape(Regex.Replace(s ?? "", @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "")) ?? "";

    /// <summary>⇩ The agent's answer as Word: the text and the table, with ✉ Open and 📎 Email file links → the .docx path.</summary>
    public static string AgentWord(Agent.Result r, string title)
    {
        Notice();
        var (dir, name) = Folder(Path.Combine(Paths.Root, "Exports"), title);
        var mails = MailsOf(r);
        var files = SaveEmails(dir, mails, 400);
        var rels = new List<(string Id, string Target)>();
        string Rel(string target) { var id = "rL" + (rels.Count + 1); rels.Add((id, target)); return id; }
        string Run(string t, bool bold = false, bool white = false) =>
            $"<w:r><w:rPr>{(bold ? "<w:b/>" : "")}{(white ? "<w:color w:val=\"FFFFFF\"/>" : "")}</w:rPr><w:t xml:space=\"preserve\">{Xe(t)}</w:t></w:r>";
        string Hyper(string url, string t) => $"<w:hyperlink r:id=\"{Rel(url)}\"><w:r><w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr><w:t xml:space=\"preserve\">{Xe(t)}</w:t></w:r></w:hyperlink>";
        string Para(string inner, string style = "") => $"<w:p>{(style.Length > 0 ? $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>" : "")}{inner}</w:p>";
        string Cell(string inner, bool head = false) => $"<w:tc><w:tcPr>{(head ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"2B2F3A\"/>" : "")}</w:tcPr><w:p>{inner}</w:p></w:tc>";
        var body = new StringBuilder();
        body.Append(Para(Run("🧭 " + title, true), "Title"));
        foreach (var line in (r.Text ?? "").Split('\n')) body.Append(Para(Run(line.TrimEnd())));
        if (r.Rows.Count > 0)
        {
            var head = r.Columns.Concat(new[] { "✉ Open", "📎 Email file" }).ToList();
            body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>" +
                        string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(b => $"<w:{b} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"D5D8DE\"/>")) +
                        "</w:tblBorders></w:tblPr>");
            body.Append("<w:tr>").Append(string.Concat(head.Select(h => Cell(Run(h, true, true), true)))).Append("</w:tr>");
            foreach (var x in r.Rows)
            {
                body.Append("<w:tr>");
                for (int i = 0; i < r.Columns.Count; i++) body.Append(Cell(Run(i < x.Cells.Count ? x.Cells[i] : "")));
                body.Append(Cell(x.Id > 0 ? Hyper(NovaUrl(x.Id), "✉ Open") : ""));
                body.Append(Cell(x.Id > 0 && files.TryGetValue(x.Id, out var f) ? Hyper(f.Replace('\\', '/'), "📎 " + Path.GetFileNameWithoutExtension(f)) : ""));
                body.Append("</w:tr>");
            }
            body.Append("</w:tbl>");
        }
        body.Append(Para(Run($"✉ Open = opens the email in Nova on the PC that made this file · 📎 Email file = the email itself in the Emails folder (opens in anyone's Outlook) · Made by Nova Enhance · Designed by A.HAMROUNI")));
        var path = Path.Combine(dir, name + ".docx");
        const string H = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
        using (var fs = File.Create(path))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void W(string n, string xml) { using var sw = new StreamWriter(z.CreateEntry(n).Open(), new UTF8Encoding(false)); sw.Write(xml); }
            W("[Content_Types].xml", H + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>");
            W("_rels/.rels", H + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            W("word/_rels/document.xml.rels", H + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rS\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                string.Concat(rels.Select(x => $"<Relationship Id=\"{x.Id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{Xe(x.Target)}\" TargetMode=\"External\"/>")) + "</Relationships>");
            W("word/styles.xml", H + "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\" w:eastAsia=\"Calibri\"/><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"80\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:rPr><w:b/><w:sz w:val=\"32\"/></w:rPr></w:style><w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/></w:style></w:styles>");
            W("word/document.xml", H + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>" + body +
                "<w:sectPr><w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/><w:pgMar w:top=\"1000\" w:right=\"1000\" w:bottom=\"1000\" w:left=\"1000\" w:header=\"500\" w:footer=\"500\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>");
        }
        Readme(dir);
        Zip(dir);
        return path;
    }

    // ------------------------------------------------------------------ nova: links (✉ Open in Excel / Word)

    /// <summary>Registers nova: links for this Windows user (Excel / Word open the email in Nova).</summary>
    public static void RegisterLinks()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\nova");
            k.SetValue("", "URL:Nova Enhance email");
            k.SetValue("URL Protocol", "");
            using var c = k.CreateSubKey(@"shell\open\command");
            c.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch (Exception e) { Log.Warn("nova: links", e.Message); }
    }

    /// <summary>nova:mail/123 → the email in Nova's view (it has “Open in Outlook”); nova:mail/123?outlook → straight in Outlook.</summary>
    public static void OpenLink(string url)
    {
        try
        {
            var m = Regex.Match(url ?? "", @"mail/(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out var id)) return;
            var h = MailIndex.ByIds(new[] { id }, 1, 0).FirstOrDefault();
            if (h == null) { MessageBox.Show("This email is not in Nova's index on this PC any more.", "Nova Enhance"); return; }
            if (url.Contains("outlook", StringComparison.OrdinalIgnoreCase)) Outlook.Open(h.Entry, h.StoreId);
            else Screens.MailAsk(h);
        }
        catch (Exception e) { Log.Error("nova: link", e); }
    }
}
