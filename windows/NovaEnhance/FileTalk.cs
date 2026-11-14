using System.Collections.Specialized;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 📎 Files in any Nova conversation: attach, paste (Ctrl+V — a screenshot or files you copied) or drop up to 6 files, then ask —
/// read it (OCR), advise on it, or compare 2–4 of them into a comparison sheet (exported to Excel). Everything read is kept
/// in the one Nova store, so every feature knows it afterwards. Designed by A.HAMROUNI
/// </summary>
static class FileTalk
{
    public const int Max = 6;
    public static string PastedDir => Path.Combine(Paths.Root, "Pasted");
    static readonly string[] Pictures = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    /// <summary>What is on the clipboard as files: files you copied, or a screenshot saved as a .png. Empty when neither.</summary>
    public static List<string> FromClipboard()
    {
        var res = new List<string>();
        try
        {
            if (Clipboard.ContainsFileDropList())
                foreach (string f in Clipboard.GetFileDropList()) if (File.Exists(f)) res.Add(f);
            if (res.Count == 0 && Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                if (img != null) res.Add(SaveImage(img));
            }
        }
        catch (Exception e) { Log.Warn("attach", "clipboard: " + e.Message); }
        return res;
    }

    public static string SaveImage(Image img)
    {
        Directory.CreateDirectory(PastedDir);
        var p = Path.Combine(PastedDir, $"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        img.Save(p, System.Drawing.Imaging.ImageFormat.Png);
        return p;
    }

    /// <summary>Saves bytes pasted or dropped in a web page (Nova Chat) as a file.</summary>
    public static string SaveBytes(string name, byte[] data)
    {
        Directory.CreateDirectory(PastedDir);
        var safe = Regex.Replace(string.IsNullOrWhiteSpace(name) ? $"Pasted {DateTime.Now:yyyy-MM-dd HHmmss}.png" : name, @"[\\/:*?""<>|]+", " ").Trim();
        var p = Path.Combine(PastedDir, safe);
        if (File.Exists(p)) p = Path.Combine(PastedDir, Path.GetFileNameWithoutExtension(safe) + $" {DateTime.Now:HHmmss}" + Path.GetExtension(safe));
        File.WriteAllBytes(p, data);
        return p;
    }

    public static bool IsPicture(string path) => Pictures.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// The text of an attached file: from the one Nova store (read once by any feature), else read now — Word, Excel, PDF,
    /// PowerPoint, emails, zip, CAD text, and OCR for scans and pictures. When the PC finds no text in a picture or a scanned PDF,
    /// your vision AI reads it (asked first unless the local AI does it).
    /// </summary>
    static DateTime lookOk = DateTime.MinValue;

    /// <summary>Asks once before pictures or recordings leave the PC (Yes = allowed for 30 minutes). The local AI never needs it.</summary>
    static bool Consent(IWin32Window owner, string what, string who, bool local)
    {
        if (local || DateTime.Now < lookOk) return true;
        if (owner == null) return false;
        DialogResult ans = DialogResult.No;
        void AskIt() => ans = MessageBox.Show(owner, $"Send {what} to {who} so Nova can look at it / listen to it?\n\nYes = allowed for the next 30 minutes · No = Nova uses only the text it read on this PC.",
            "Nova Enhance · 👁 look at your files", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (owner is Control oc && oc.InvokeRequired) oc.Invoke((Action)AskIt); else AskIt();
        if (ans != DialogResult.Yes) return false;
        lookOk = DateTime.Now.AddMinutes(30);
        return true;
    }

    static readonly System.Text.RegularExpressions.Regex DrawingName = new(@"(^|[\-_ ])(DRW|DWG|DET|PLN|ELE|ELV|SEC|SK|SKT|LAY|GA|SHD|SDS|SHOP)([\-_ \d]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// What Nova knows about an attached file for a question — like a web AI: the text (read on this PC, OCR for scans), and with a
    /// question it also LOOKS at pictures, drawing pages and CAD drawings (vision), and LISTENS to voice notes and videos (transcript).
    /// </summary>
    public static async Task<string> Text(string path, IWin32Window owner, CancellationToken ct, Action<string> status = null, string question = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        // 🎙 Voice notes and videos.
        if (Media.Is(path))
        {
            var known = FolderIndex.Known(path);
            if (known is { Count: > 0 } && known[0].Loc == "transcript") return "🎙 Transcript:\n" + known[0].Text;
            if (Store.Key("gemini").Length == 0) return "(🎙 a recording — transcribing needs a Gemini key: Settings → AI & API keys)";
            var len = Media.Length(path);
            if (!Consent(owner, $"the recording {Path.GetFileName(path)} ({(int)len.TotalMinutes}:{len.Seconds:00})", "Gemini", false)) return "(🎙 recording not transcribed — you chose to keep it on this PC)";
            try
            {
                var t = await Media.Transcribe(path, status, ct);
                if (t.Length > 0) FolderIndex.Remember(path, new List<Seg> { new("transcript", t) });
                return t.Length > 0 ? $"🎙 Transcript ({(int)len.TotalMinutes}:{len.Seconds:00}):\n{t}" : "(🎙 no speech found)";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { return "(🎙 could not transcribe: " + e.Message + ")"; }
        }
        var baseText = await TextOnly(path, owner, ct, status);
        if (string.IsNullOrWhiteSpace(question)) return baseText;
        var who = Ai.LookerLabel; var local = Ai.LooksLocally;
        // 📐 CAD: texts, layers and blocks, and the drawing as a picture to look at.
        if (ext is ".dwg" or ".dxf")
        {
            status?.Invoke("📐 Opening " + Path.GetFileName(path) + "…");
            string dxf = null;
            try { dxf = await Task.Run(() => Cad.ToDxf(path, ct), ct); } catch (OperationCanceledException) { throw; } catch { }
            if (dxf == null) return baseText + "\n(📐 Only the texts inside the DWG could be read — install the free ODA File Converter (or AutoCAD) so Nova can open and look at DWG drawings.)";
            Cad.Drawing d;
            try { d = await Task.Run(() => Cad.Read(dxf), ct); } catch (Exception e) { return baseText + "\n(📐 could not read the drawing: " + e.Message + ")"; }
            var sb = new StringBuilder();
            sb.Append($"📐 CAD drawing: {d.Entities:N0} objects · layers: {string.Join(", ", d.Layers.Take(60))}\n");
            if (d.Blocks.Count > 0) sb.Append("Blocks: ").Append(string.Join(", ", d.Blocks.Take(40))).Append('\n');
            sb.Append("Texts and dimensions in the drawing:\n").Append(string.Join(" · ", d.Texts.Take(800))).Append('\n');
            if (d.Png != null && Consent(owner, $"a picture of the drawing {Path.GetFileName(path)}", who, local))
                try { status?.Invoke("👁 Looking at the drawing…"); sb.Append("\n👁 What Nova sees in the drawing:\n").Append(await Ai.Look(d.Png, "image/png", question, ct)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { sb.Append("\n(👁 could not look at it: " + e.Message + ")"); }
            return sb.ToString();
        }
        // 🖼 Pictures and 📄 drawing PDFs: Nova looks at them with the question in mind.
        var drawingPdf = ext == ".pdf" && (DrawingName.IsMatch(Path.GetFileNameWithoutExtension(path)) || baseText.Length < 1500);
        if (!(IsPicture(path) || drawingPdf)) return baseText;
        var pages = new List<int>();
        if (IsPicture(path)) pages.Add(0);
        else
        {
            // The pages that match the question best (from their text), else the first ones.
            var words = System.Text.RegularExpressions.Regex.Split((question ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}\-]+").Where(w => w.Length >= 3).ToList();
            var byPage = System.Text.RegularExpressions.Regex.Matches(baseText, @"\[page (\d+)\][^\[]*").Select(m => (P: int.Parse(m.Groups[1].Value), S: words.Count(w => m.Value.ToLowerInvariant().Contains(w)))).ToList();
            pages = byPage.Where(x => x.S > 0).OrderByDescending(x => x.S).Select(x => x.P).Distinct().Take(3).ToList();
            if (pages.Count == 0) pages = new() { 1, 2 };
        }
        if (!Consent(owner, IsPicture(path) ? $"the picture {Path.GetFileName(path)}" : $"{pages.Count} page(s) of {Path.GetFileName(path)}", who, local)) return baseText;
        var look = new StringBuilder();
        foreach (var pg in pages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                status?.Invoke($"👁 Looking at {Path.GetFileName(path)}{(pg > 0 ? " · page " + pg : "")}…");
                var pic = await Task.Run(() => Ocr.Picture(LongPath.Usable(path), pg), ct);
                if (pic.Data == null || pic.Data.Length == 0) continue;
                look.Append(pg > 0 ? $"[page {pg}] " : "").Append(await Ai.Look(pic.Data, pic.Mime, question, ct)).Append("\n\n");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { look.Append("(👁 could not look at it: " + e.Message + ")\n"); break; }
        }
        return look.Length > 0 ? baseText + "\n\n👁 What Nova sees:\n" + look.ToString().Trim() : baseText;
    }

    /// <summary>The text only (read once, kept in the one Nova store).</summary>
    public static async Task<string> TextOnly(string path, IWin32Window owner, CancellationToken ct, Action<string> status = null)
    {
        status?.Invoke("📎 Reading " + Path.GetFileName(path) + "…");
        List<Seg> segs;
        try { segs = (await Task.Run(() => Projects.Read(path, ct), ct)).Segs; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return "(could not read: " + e.Message + ")"; }
        var text = string.Join("\n", segs.Select(s => (s.Loc.Length > 0 ? "[" + s.Loc + "] " : "") + s.Text)).Trim();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (text.Replace("[", "").Trim().Length >= 30 || !(IsPicture(path) || ext == ".pdf")) return text.Length > 0 ? text : "(no readable text)";

        // Little or no text: a picture or a scan → vision.
        if (!Ai.HasKey("gemini") && !(Ai.VisionBy == "local" && Ai.HasKey("local")) && Ai.Current() == null) return "(no readable text — add an AI key for vision reading)";
        var local = Ai.VisionBy == "local" && Ai.HasKey("local");
        if (!local && owner != null)
        {
            var q = $"The PC found no text in {Path.GetFileName(path)}.\n\nSend {(IsPicture(path) ? "this picture" : "its pages (up to 5)")} to your vision AI to read it?";
            DialogResult ans = DialogResult.No;
            void AskIt() => ans = MessageBox.Show(owner, q, "Nova Enhance · 📎 read with vision", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (owner is Control oc && oc.InvokeRequired) oc.Invoke((Action)AskIt); else AskIt();
            if (ans != DialogResult.Yes) return "(scanned — no text found on this PC; vision reading declined)";
        }
        var sb = new StringBuilder();
        try
        {
            if (IsPicture(path))
            {
                status?.Invoke("👁 Vision is reading " + Path.GetFileName(path) + "…");
                var pic0 = await Task.Run(() => Ocr.Picture(path, 0), ct);
                if (pic0.Data != null) sb.Append(await Ai.Vision(pic0.Data, pic0.Mime, ct));
            }
            else
                for (int pg = 1; pg <= 5; pg++)
                {
                    ct.ThrowIfCancellationRequested();
                    status?.Invoke($"👁 Vision is reading {Path.GetFileName(path)} · page {pg}…");
                    (byte[] Data, string Mime) pic;
                    var page = pg;
                    try { pic = await Task.Run(() => Ocr.Picture(path, page), ct); } catch (OperationCanceledException) { throw; } catch { break; }
                    if (pic.Data == null || pic.Data.Length == 0) break;
                    sb.Append($"[page {pg}]\n").Append(await Ai.Vision(pic.Data, pic.Mime, ct)).Append("\n\n");
                }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { sb.Append("(vision reading failed: " + e.Message + ")"); }
        var v = sb.ToString().Trim();
        if (v.Length > 30) FolderIndex.Remember(path, new List<Seg> { new("read by vision", v) });
        return v.Length > 0 ? v : "(no readable text)";
    }

    /// <summary>What the AI must do with attached files (added to every conversation's rules).</summary>
    public const string Rules =
        " ATTACHED FILES are given as [F1], [F2]… with their text (read on the PC, OCR for scans). " +
        "If the user asks to READ / OCR / transcribe / extract: give the full text of the file, cleaned and in reading order (keep tables as tables), nothing invented. " +
        "If the user asks to COMPARE files: start with a comparison table in Markdown — first line '| Item | <short name of F1> | <short name of F2> | … | Note |', " +
        "one row per item compared (price, quantities, specification values, scope, exclusions, programme, dates, revisions, dimensions … whatever fits the files), " +
        "the exact value from each file in its column ('—' when the file doesn't say), and in Note: '✓ best', '⚠ different', '⚠ missing in F2' etc. " +
        "After the table: a short 'What changed / key differences' list, then a recommendation. For two revisions of the same document, list what was added, removed and changed. " +
        "If the user asks for ADVICE or a check: answer as a " + Persona.Expert + ", citing [F1]… for every fact taken from the files. " +
        "Parts marked “👁 What Nova sees” are what a vision AI saw in the picture / drawing (use them like your own eyes, say when unsure); “🎙 Transcript” is what was said in a recording (with minutes); " +
        "“📐 CAD drawing” lists the layers, blocks, texts and dimensions found in a DWG/DXF. ";

    // ------------------------------------------------------------------ the comparison sheet

    /// <summary>Markdown tables in an answer: rows of cells (the header first).</summary>
    public static List<List<string[]>> Tables(string text)
    {
        var res = new List<List<string[]>>();
        List<string[]> cur = null;
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith("|") && l.Count(c => c == '|') >= 2)
            {
                if (Regex.IsMatch(l, @"^\|?\s*:?-{2,}")) continue;   // |---|---|
                var cells = l.Trim('|').Split('|').Select(c => Regex.Replace(c.Trim(), @"\*\*(.+?)\*\*", "$1")).ToArray();
                (cur ??= new()).Add(cells);
            }
            else if (cur != null) { if (cur.Count > 1) res.Add(cur); cur = null; }
        }
        if (cur != null && cur.Count > 1) res.Add(cur);
        return res;
    }

    public static string TableHtml(List<string[]> t)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder("<div class=tblwrap><table class=cmp><tr>");
        foreach (var h in t[0]) sb.Append("<th>").Append(E(h)).Append("</th>");
        sb.Append("</tr>");
        foreach (var r in t.Skip(1))
        {
            var note = r.LastOrDefault() ?? "";
            sb.Append(note.Contains("⚠") ? "<tr class=diff>" : note.Contains("✓") ? "<tr class=best>" : "<tr>");
            for (int i = 0; i < t[0].Length; i++) sb.Append("<td>").Append(E(i < r.Length ? r[i] : "")).Append("</td>");
            sb.Append("</tr>");
        }
        return sb.Append("</table></div>").ToString();
    }

    public const string TableCss =
        ".tblwrap{overflow-x:auto;margin:8px 0}table.cmp{border-collapse:collapse;font-size:13px;min-width:60%}" +
        "table.cmp th{background:#2E2A55;color:#fff;text-align:left;padding:6px 10px;border:1px solid #3A4256;position:sticky;top:0}" +
        "table.cmp td{padding:5px 10px;border:1px solid #2E3546;vertical-align:top}table.cmp tr.diff td{background:rgba(255,170,60,.10)}" +
        "table.cmp tr.best td{background:rgba(80,200,120,.10)}";

    /// <summary>📊 The comparison sheet as a real Excel file (header bold and frozen, filters), opened at once.</summary>
    public static string ExportXlsx(string text, string title)
    {
        var tables = Tables(text);
        if (tables.Count == 0) throw new InvalidOperationException("There is no table in this answer — ask Nova to “compare … in a table”.");
        var t = tables.OrderByDescending(x => x.Count).First();
        var safe = Regex.Replace(title ?? "Comparison", @"[\\/:*?""<>|]+", " ").Trim();
        if (safe.Length > 60) safe = safe[..60].Trim();
        var path = Path.Combine(Prompts.Folder, $"{safe} — comparison {DateTime.Now:dd MMM yyyy HHmm}.xlsx");
        Prompts.Xlsx(path, title ?? "Comparison", t[0], t.Skip(1).Select(r => Enumerable.Range(0, t[0].Length).Select(i => i < r.Length ? r[i] : "").ToArray()).ToList());
        return path;
    }

    /// <summary>⧉ The table as tab-separated text: pastes into Excel or a Word table.</summary>
    public static string Tsv(string text)
    {
        var t = Tables(text).OrderByDescending(x => x.Count).FirstOrDefault();
        return t == null ? "" : string.Join("\r\n", t.Select(r => string.Join("\t", r)));
    }
}

/// <summary>📎 The attach row of a conversation: button, chips (✗ removes), Ctrl+V and drag-and-drop onto the window.</summary>
class AttachBar : FlowLayoutPanel
{
    public readonly List<string> Files = new();
    public event Action Changed;
    bool shortHint;
    /// <summary>A narrow pane: the hint shrinks to one short line (the full text on hover).</summary>
    public bool ShortHint { get => shortHint; set { if (shortHint == value) return; shortHint = value; Draw(); } }

    public AttachBar()
    {
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; WrapContents = true; BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, 4);
        Draw();
    }

    /// <summary>Ctrl+V in the text box (screenshot or copied files) and files dropped anywhere on the window.</summary>
    public void Hook(TextBoxBase box, Form form)
    {
        box.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.V && (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()))
            {
                var got = FileTalk.FromClipboard();
                if (got.Count > 0) { Add(got); e.SuppressKeyPress = true; e.Handled = true; }
            }
        };
        void Drop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true || e.Data?.GetDataPresent(DataFormats.Bitmap) == true) e.Effect = DragDropEffects.Copy; };
            c.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] fs) Add(fs.Where(File.Exists));
                else if (e.Data?.GetData(DataFormats.Bitmap) is Image img) Add(new[] { FileTalk.SaveImage(img) });
            };
        }
        Drop(form); Drop(box);
        foreach (Control c in form.Controls) if (c is not WebBrowser) Drop(c);
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Files.Count >= FileTalk.Max) { MessageBox.Show(FindForm(), $"Up to {FileTalk.Max} files per question.", "Nova Enhance"); break; }
            if (!Files.Contains(p, StringComparer.OrdinalIgnoreCase)) Files.Add(p);
        }
        Draw(); Changed?.Invoke();
    }

    public void Clear() { Files.Clear(); Draw(); Changed?.Invoke(); }

    void Draw()
    {
        SuspendLayout();
        foreach (Control c in Controls) c.Dispose();
        Controls.Clear();
        var add = Th.B("📎 Attach", () =>
        {
            using var d = new OpenFileDialog { Multiselect = true, Title = "Files for Nova — read (OCR), advise or compare (up to 6)" };
            if (d.ShowDialog(FindForm()) == DialogResult.OK) Add(d.FileNames);
        });
        add.Font = Th.F(9);
        Controls.Add(add);
        if (Files.Count == 0)
        {
            const string Long = "or Ctrl+V / drop any file — PDF, Word, Excel, photos, drawings, DWG/DXF, voice notes, videos — then ask: read it · check it · compare them";
            var hint = Th.L(ShortHint ? "· Ctrl+V · drop any file" : Long, 9, Th.Muted);
            new ToolTip().SetToolTip(hint, Long);
            hint.MaximumSize = new Size(Math.Max(240, Width - 130), 0);
            hint.Margin = new Padding(6, 8, 0, 0);
            Controls.Add(hint);
        }
        foreach (var f in Files.ToList())
        {
            var ff = f;
            var chip = Th.B((FileTalk.IsPicture(f) ? "🖼 " : "📄 ") + Short(Path.GetFileName(f)) + "  ✗", () => { Files.Remove(ff); Draw(); Changed?.Invoke(); });
            chip.Font = Th.F(9);
            new ToolTip().SetToolTip(chip, f + "\n(click to remove)");
            Controls.Add(chip);
        }
        ResumeLayout();
    }

    static string Short(string s) => s.Length > 34 ? s[..20] + "…" + s[^12..] : s;
}
