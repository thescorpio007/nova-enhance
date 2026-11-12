using System.Text;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 👁 Preview: the email as Outlook shows it (tables, colours, pictures in the email, signature, links) rendered inside Nova,
/// and its attachments in Nova's viewer. Read from Outlook on this PC; nothing goes to the AI. Designed by A.HAMROUNI
/// </summary>
static class MailPreview
{
    static string Folder(MailHit h)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(h.Entry ?? ""))).ToLowerInvariant()[..16];
        var dir = Path.Combine(Paths.Cache, "mail preview", key);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static Task<T> Sta<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return Guard.Limit(tcs.Task, 60, "Outlook");
    }

    static string H(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    /// <summary>Writes the email as a web page next to its pictures and returns the file (null when Outlook is closed).</summary>
    public static Task<string> Render(MailHit h) => Sta(() =>
    {
        object running = Outlook.Running();
        if (running == null) return null;
        var dir = Folder(h);
        var file = Path.Combine(dir, "mail.html");
        if (File.Exists(file) && File.GetLastWriteTime(file) > DateTime.Now.AddHours(-12)) return file;
        dynamic app = running;
        dynamic ns = app.GetNamespace("MAPI");
        dynamic item = ns.GetItemFromID(h.Entry, h.StoreId);
        try
        {
            string html = "";
            try { html = (string)item.HTMLBody; } catch { }
            if (string.IsNullOrWhiteSpace(html))
                html = "<pre style='white-space:pre-wrap;font-family:Segoe UI,Arial;font-size:14px'>" + H(Outlook.S(() => item.Body)) + "</pre>";
            // Pictures inside the email (cid:…) are saved next to the page.
            try
            {
                dynamic atts = item.Attachments;
                int n = (int)atts.Count;
                for (int i = 1; i <= n; i++)
                {
                    dynamic a = atts.Item(i);
                    try
                    {
                        string cid = "";
                        try { cid = Convert.ToString(a.PropertyAccessor.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x3712001F")) ?? ""; } catch { }
                        if (cid.Length == 0) continue;
                        var name = "img" + i + Path.GetExtension(Outlook.S(() => a.FileName));
                        a.SaveAsFile(Path.Combine(dir, name));
                        html = Regex.Replace(html, "cid:" + Regex.Escape(cid), name, RegexOptions.IgnoreCase);
                    }
                    catch { }
                    finally { Outlook.Release(a); }
                }
                Outlook.Release(atts);
            }
            catch { }
            // No scripts; our own character set; a white page like Outlook.
            html = Regex.Replace(html, @"<script\b.*?</script>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            html = Regex.Replace(html, @"<meta[^>]+charset[^>]*>", "", RegexOptions.IgnoreCase);
            var head = "<meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"script-src 'none'\"><style>html{background:#fff}body{font-family:Calibri,'Segoe UI',Arial,sans-serif;font-size:14.5px;margin:18px 22px;color:#222}img{max-width:100%;height:auto}</style>";
            html = Regex.IsMatch(html, "<head[^>]*>", RegexOptions.IgnoreCase)
                ? Regex.Replace(html, "<head[^>]*>", m => m.Value + head, RegexOptions.IgnoreCase)
                : "<html><head>" + head + "</head><body>" + html + "</body></html>";
            File.WriteAllText(file, html, new UTF8Encoding(false));
            return file;
        }
        finally { Outlook.Release(item); Outlook.Release(ns); }
    });

    /// <summary>Saves one attachment (by its file name) into a folder and returns the full path.</summary>
    public static Task<string> SaveAttachment(MailHit h, string fileName, string dir) => (h.Entry ?? "").StartsWith("pst:")
        ? Task.Run(() => Pst.SaveAttachment(h.Entry, fileName, dir))   // 📂 from a .pst read directly
        : Sta(() =>
    {
        dynamic app = Outlook.Get();
        dynamic ns = app.GetNamespace("MAPI");
        dynamic item = ns.GetItemFromID(h.Entry, h.StoreId);
        try
        {
            dynamic atts = item.Attachments;
            int n = (int)atts.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic a = atts.Item(i);
                try
                {
                    var name = Outlook.S(() => a.FileName);
                    if (!name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, name);
                    a.SaveAsFile(path);
                    return path;
                }
                finally { Outlook.Release(a); }
            }
            throw new FileNotFoundException("The attachment was not found in the email: " + fileName);
        }
        finally { Outlook.Release(item); Outlook.Release(ns); }
    });

    /// <summary>👁 an attachment in Nova's viewer (PDF, drawings, images, Word, Excel, PowerPoint…).</summary>
    public static async void PreviewAttachment(MailHit h, string name, IWin32Window owner)
    {
        try
        {
            var path = await SaveAttachment(h, name, Path.Combine(Folder(h), "attachments"));
            var hit = new FileHit { Path = path, Root = Path.GetDirectoryName(path), Kind = Path.GetExtension(path).TrimStart('.').ToLowerInvariant() };
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".pdf" or ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp" or ".gif" or ".docx" or ".doc" or ".pptx" or ".ppt"))
            {
                var segs = await Task.Run(() => { List<Seg> s = null; var t = new Thread(() => { try { s = FileText.Extract(path); } catch { } }); t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join(); return s ?? new(); });
                hit.Pieces = segs.Where(x => (x.Text ?? "").Trim().Length > 0).Take(400).Select(x => new PieceHit { Loc = x.Loc, Goto = x.Goto, Text = x.Text, Matches = new() }).ToList();
            }
            new PreviewWindow(hit, null, 0, false) { Text = $"Nova Enhance · 👁 {name} · from {h.FromName}, {h.Date:dd MMM}" }.Show();
        }
        catch (Exception e) { MessageBox.Show(owner, "Could not preview the attachment: " + e.Message + "\n\n(Outlook needs to be open.)", "Nova Enhance"); Log.Error("attachment preview", e); }
    }

    public static async void SaveAttachmentAs(MailHit h, string name, IWin32Window owner)
    {
        try
        {
            using var d = new SaveFileDialog { Title = "Save the attachment", FileName = name, Filter = "All files|*.*", OverwritePrompt = true };
            var last = Store.Str("win_msg_dir");
            if (last.Length > 0 && Directory.Exists(last)) d.InitialDirectory = last;
            if (d.ShowDialog(owner) != DialogResult.OK) return;
            var tmp = await SaveAttachment(h, name, Path.Combine(Folder(h), "attachments"));
            File.Copy(tmp, d.FileName, true);
            Store.Set("win_msg_dir", Path.GetDirectoryName(d.FileName) ?? "");
            Work.LogMail(h, "💾 Saved attachment " + Path.GetFileName(d.FileName));
            Program.Balloon("Nova Enhance", "✓ Attachment saved: " + Path.GetFileName(d.FileName));
        }
        catch (Exception e) { MessageBox.Show(owner, "Could not save the attachment: " + e.Message + "\n\n(Outlook needs to be open.)", "Nova Enhance"); Log.Error("attachment save", e); }
    }
}
