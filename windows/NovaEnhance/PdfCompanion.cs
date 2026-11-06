using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// ✦ Nova next to your PDF viewer (PDF-XChange, Acrobat, Bluebeam, Foxit, Sumatra, Edge): Ask about this PDF (answers with
/// page numbers), Summary / Check this drawing, Search, OCR, Translate a page, Send to Nova Chat.
/// Opens with Ctrl+Shift+D, the tray menu, or the small floating ✦ button on the viewer. Designed by A.HAMROUNI
/// </summary>
static class PdfCompanion
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static bool Button { get => Store.Bool("win_pdf_button", true); set { Store.SetBool("win_pdf_button", value); if (!value) floating?.Hide(); } }

    static readonly string[] Viewers = { "pdfxedit", "pdfxcview", "acrobat", "acrord32", "revu", "foxitpdfeditor", "foxitphantompdf", "foxitreader", "foxitpdfreader", "sumatrapdf", "msedge" };

    public static (IntPtr Window, string Process, string Title) Front()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return (IntPtr.Zero, "", "");
        try
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return (IntPtr.Zero, "", "");
            var name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            return (h, name, sb.ToString());
        }
        catch { return (IntPtr.Zero, "", ""); }
    }

    public static bool IsViewer(string process, string title) =>
        Viewers.Contains(process) && (process != "msedge" || title.Contains(".pdf", StringComparison.OrdinalIgnoreCase));

    /// <summary>The document name in the viewer's title bar.</summary>
    public static string NameFromTitle(string process, string title)
    {
        var t = title.Trim().TrimStart('*').Trim();
        foreach (var suffix in new[] { " - PDF-XChange Editor", " - PDF-XChange Viewer", " - Foxit PDF Editor", " - Foxit PhantomPDF", " - Foxit Reader", " - Foxit PDF Reader", " - SumatraPDF" })
        {
            var i = t.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (i > 0) { t = t[..i]; break; }
        }
        t = Regex.Replace(t, @"^Bluebeam Revu[^-]*-\s*", "", RegexOptions.IgnoreCase);
        var m = Regex.Match(t, @"^(.*?\.pdf)\b", RegexOptions.IgnoreCase);
        if (m.Success) t = m.Groups[1].Value;
        else
        {
            var a = t.IndexOf(" - Adobe Acrobat", StringComparison.OrdinalIgnoreCase);
            if (a > 0) t = t[..a];
        }
        return t.Trim().TrimEnd('*').Trim();
    }

    /// <summary>The full path of the open PDF: Windows' recent files, then the folder index.</summary>
    public static string FindPath(string name)
    {
        if (name.Length == 0) return null;
        var bare = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        try
        {
            var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            var links = new DirectoryInfo(recent).GetFiles("*.lnk")
                .Where(f => f.Name.StartsWith(bare, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(10).ToList();
            if (links.Count > 0)
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t != null)
                {
                    dynamic sh = Activator.CreateInstance(t);
                    foreach (var l in links)
                    {
                        string target = "";
                        try { target = (string)sh.CreateShortcut(l.FullName).TargetPath; } catch { }
                        if (target.Length > 0 && File.Exists(target) && Path.GetFileNameWithoutExtension(target).Equals(bare, StringComparison.OrdinalIgnoreCase)) return target;
                    }
                }
            }
        }
        catch { }
        try { return FolderIndex.FindByName(bare + ".pdf"); } catch { return null; }
    }

    static (IntPtr Window, string Process, string Title) lastSeen;
    static DateTime lastSeenAt;
    static PdfPanel panel;
    static FloatButton floating;

    /// <summary>Ctrl+Shift+D / tray / ✦ button: the panel for the PDF in front (or the one picked).</summary>
    public static void Open(IntPtr window = default, string process = null, string title = null)
    {
        try
        {
            if (window == IntPtr.Zero) (window, process, title) = Front();
            // From the tray menu the viewer is no longer in front: use the one seen last (within 10 minutes).
            if ((window == IntPtr.Zero || !IsViewer(process ?? "", title ?? "")) && lastSeen.Window != IntPtr.Zero && DateTime.Now - lastSeenAt < TimeSpan.FromMinutes(10))
                (window, process, title) = lastSeen;
            string path = null, name = "";
            if (window != IntPtr.Zero && IsViewer(process, title))
            {
                name = NameFromTitle(process, title);
                path = FindPath(name);
            }
            if (path == null)
            {
                using var d = new OpenFileDialog { Filter = "PDF|*.pdf|All files|*.*", Title = name.Length > 0 ? $"Where is “{name}”?" : "Which PDF?", FileName = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name : (name.Length > 0 ? name + ".pdf" : "") };
                var last = Store.Str("win_pdf_last_dir");
                if (last.Length > 0 && Directory.Exists(last)) d.InitialDirectory = last;
                if (d.ShowDialog() != DialogResult.OK) return;
                path = d.FileName;
            }
            Store.Set("win_pdf_last_dir", Path.GetDirectoryName(path) ?? "");
            if (panel == null || panel.IsDisposed) panel = new PdfPanel();
            panel.OpenFile(path, window);
            Screens.Show(panel);
        }
        catch (Exception e) { Log.Error("PDF panel", e); }
    }

    /// <summary>Every half second: show the small ✦ button on a PDF viewer in front, hide it otherwise.</summary>
    public static void Watch()
    {
        var t = new System.Windows.Forms.Timer { Interval = 500 };
        t.Tick += (_, _) =>
        {
            try
            {
                var (h, p, title) = Front();
                if (h == IntPtr.Zero) return;   // Nova itself (or the button) is in front: leave it as it is
                if (IsViewer(p, title)) { lastSeen = (h, p, title); lastSeenAt = DateTime.Now; }
                if (!Button) return;
                if (!IsViewer(p, title) || IsIconic(h) || !GetWindowRect(h, out var r) || r.Right - r.Left < 400) { floating?.Hide(); return; }
                if (floating == null || floating.IsDisposed) floating = new FloatButton();
                floating.Target = (h, p, title);
                var x = Math.Max(r.Left, r.Right - 260);
                var y = Math.Max(r.Top, 0) + 4;
                if (floating.Location != new Point(x, y)) floating.Location = new Point(x, y);
                if (!floating.Visible) floating.Show();
            }
            catch { }
        };
        t.Start();
    }

    /// <summary>The small round ✦ on the viewer's title bar. Never takes the focus.</summary>
    class FloatButton : Form
    {
        public (IntPtr, string, string) Target;
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; return cp; }   // NOACTIVATE · TOOLWINDOW · TOPMOST
        }

        public FloatButton()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            Size = new Size(30, 26); BackColor = Th.Accent;
            var l = new Label { Text = "✦", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = Th.F(11, true), Cursor = Cursors.Hand };
            new ToolTip().SetToolTip(l, "Nova: ask about this PDF, summary, check drawing, OCR, search (Ctrl+Shift+D)");
            l.Click += (_, _) => { var (h, p, t) = Target; PdfCompanion.Open(h, p, t); };
            Controls.Add(l);
            Region = new Region(new Rectangle(0, 0, 30, 26));
        }
    }
}

/// <summary>The ✦ Nova panel for one PDF.</summary>
class PdfPanel : NovaForm
{
    readonly Stack s;
    readonly Label title, where, status;
    readonly TextBox ask, find;
    readonly NumericUpDown page;
    readonly LinkLabel answer;
    readonly FlowLayoutPanel hits;
    string path = "";
    List<Seg> segs;
    string lastAnswer = "", lastQuestion = "";
    CancellationTokenSource cts;

    public PdfPanel()
    {
        Text = "Nova Enhance · this PDF";
        Width = 470; Height = Math.Min(900, Screen.PrimaryScreen!.WorkingArea.Height - 40);
        MinimumSize = new Size(380, 420);
        StartPosition = FormStartPosition.Manual;
        s = new Stack();
        Controls.Add(s);
        title = s.Add(Th.Wrap("", 12, Th.Text));
        title.Font = Th.F(12, true);
        where = s.Add(Th.Wrap("", 8.5f, Th.Muted));
        where.Cursor = Cursors.Hand;
        where.Click += (_, _) => { if (File.Exists(path)) Shell.Select(path); };
        status = s.Add(Th.Wrap("", 9, Th.AccentText));

        s.Add(Th.Section("Ask about this PDF"));
        ask = s.Add(Th.Box(false), true);
        ask.PlaceholderText = "e.g. What is the fixing detail of the glass balustrade?";
        ask.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ask(ask.Text, false); } };
        var r0 = s.Add(Th.Row());
        r0.Controls.Add(Th.B("🔍 Ask", () => Ask(ask.Text, false), true));
        r0.Controls.Add(Th.B("✦ Summary / check drawing", Summary));

        var r1 = s.Add(Th.Row());
        r1.Controls.Add(Th.B("🔤 OCR this PDF", Ocr));
        r1.Controls.Add(Th.B("💬 Send to Nova Chat", () => { if (File.Exists(path)) Screens.HubAttach(path); }));
        r1.Controls.Add(Th.B("📁 Search the project folder", () => Screens.FolderSearch(Path.GetDirectoryName(path))));

        var r2 = s.Add(Th.Row(false));
        r2.Controls.Add(Th.B("🌐 Translate page", TranslatePage));
        page = new NumericUpDown { Minimum = 1, Maximum = 9999, Value = 1, Width = 64, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(10) };
        r2.Controls.Add(page);
        r2.Controls.Add(Th.L("→ " + Store.TrLang, 9.5f, Th.Muted));

        answer = s.Add(new LinkLabel
        {
            AutoSize = true, Font = Th.F(10), ForeColor = Th.Text, LinkColor = Th.AccentText, ActiveLinkColor = Th.Accent, BackColor = Th.Card,
            Padding = new Padding(8), Margin = new Padding(0, 8, 0, 4), Visible = false, UseMnemonic = false, Tag = "w"
        });
        answer.LinkClicked += (_, e) => { if (e.Link?.LinkData is int p) GoTo(p); };
        var r3 = s.Add(Th.Row());
        r3.Controls.Add(Th.B("⧉ Copy", () => { if (lastAnswer.Length > 0) Clipboard.SetText(lastAnswer); }));
        r3.Controls.Add(Th.B("More detail", () => { if (lastQuestion.Length > 0) Ask(lastQuestion, true); else Summary(); }));

        s.Add(Th.Section("Search in this PDF"));
        find = s.Add(Th.Box(false), true);
        find.PlaceholderText = "A word, code or reference — Enter";
        find.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Find(find.Text); } };
        hits = s.Add(new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent });
        s.Add(Th.Wrap("The text of this PDF goes to the AI only when you press Ask, Summary or Translate. Search and OCR run on this PC.", 8.5f, Th.Muted));
        s.Add(Th.Signature());
    }

    public void OpenFile(string file, IntPtr viewer)
    {
        if (!file.Equals(path, StringComparison.OrdinalIgnoreCase)) { segs = null; answer.Visible = false; hits.Controls.Clear(); lastAnswer = lastQuestion = ""; }
        path = file;
        title.Text = "📄 " + Path.GetFileName(file);
        where.Text = Path.GetDirectoryName(file) + "   (click to show the file)";
        status.Text = "Ready — ask, summarise, search or OCR this PDF.";
        // Next to the viewer: on its right if there is room, otherwise at the right edge of the screen.
        var wa = Screen.FromHandle(viewer == IntPtr.Zero ? Handle : viewer).WorkingArea;
        Location = new Point(wa.Right - Width - 8, wa.Top + 20);
        Height = Math.Min(Height, wa.Height - 40);
        _ = Text_();
    }

    async Task<List<Seg>> Text_()
    {
        if (segs != null) return segs;
        status.Text = "Reading the PDF…";
        var p = path;
        List<Seg> got = null;
        await RunSta(() => got = FileText.Extract(p));
        if (p != path) return null;
        segs = (got ?? new()).Where(x => (x.Text ?? "").Trim().Length > 0).ToList();
        var pages = segs.Select(PageOf).Where(x => x > 0).DefaultIfEmpty(1).Max();
        page.Maximum = Math.Max(1, pages);
        status.Text = segs.Count == 0 ? "⚠ No readable text — try 🔤 OCR this PDF" : $"✓ {pages} page{(pages == 1 ? "" : "s")} read · {segs.Sum(x => x.Text.Length):N0} characters";
        return segs;
    }

    static Task RunSta(Action a)
    {
        var tcs = new TaskCompletionSource();
        var t = new Thread(() => { try { a(); tcs.SetResult(); } catch (Exception e) { tcs.SetException(e); } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    static int PageOf(Seg x)
    {
        var m = Regex.Match(x.Goto ?? "", @"page:(\d+)");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        m = Regex.Match(x.Loc ?? "", @"(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    void GoTo(int p) => Opener.Open(new FileHit { Path = path }, new PieceHit { Loc = "page " + p, Goto = "page:" + p, Text = "", Matches = new() }, "");

    /// <summary>Shows an answer; [p.3] / [p.3, 5] become links that open the PDF at that page.</summary>
    void ShowAnswer(string text)
    {
        lastAnswer = text;
        answer.Links.Clear();
        answer.Text = text;
        foreach (Match m in Regex.Matches(text, @"\[(?:p\.?|page\s*)\s*(\d+)(?:\s*[,–-]\s*(?:p\.?\s*)?\d+)*\]", RegexOptions.IgnoreCase))
            answer.Links.Add(m.Index, m.Length, int.Parse(m.Groups[1].Value));
        answer.Visible = true;
        s.FitAll();
    }

    static string Extracts(List<Seg> list, int max)
    {
        var sb = new StringBuilder();
        foreach (var g in list.GroupBy(PageOf).OrderBy(g => g.Key))
        {
            var t = string.Join("\n", g.Select(x => x.Text.Trim()));
            sb.Append("[p.").Append(g.Key <= 0 ? 1 : g.Key).Append("]\n").Append(t).Append("\n\n");
            if (sb.Length > max) break;
        }
        return sb.Length > max ? sb.ToString(0, max) : sb.ToString();
    }

    async void Ask(string q, bool detailed)
    {
        q = (q ?? "").Trim();
        if (q.Length == 0) { ask.Focus(); return; }
        cts?.Cancel(); var my = cts = new CancellationTokenSource();
        try
        {
            var all = await Text_();
            if (all == null || all.Count == 0) { status.Text = "⚠ No readable text in this PDF — press 🔤 OCR this PDF first"; return; }
            var prov = Ai.Active() ?? throw new AiException("Add an AI key in Settings → AI & API keys");
            lastQuestion = q;
            status.Text = $"Asking {prov.Label}…";
            var terms = FolderAsk.LocalTerms(q);
            var pick = all.Sum(x => x.Text.Length) <= 40000 ? all
                : all.OrderByDescending(x => terms.Count(w => x.Text.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(25).ToList();
            var sys = "You answer questions about ONE construction document (specification, drawing, report, letter). Use ONLY the page texts given; each starts with its page as [p.N]. " +
                      "After each fact put its page in square brackets, e.g. [p.3]. Quote exact values (dimensions, materials, ratings, references, revisions). " +
                      (detailed ? "Give a complete, structured answer. " : "Answer in 2 to 6 short lines, the direct answer first. ") +
                      "If the document does not say, write so plainly. Reply in the language of the question." + Modes.ProfileNote();
            var a = await Ai.Chat(sys, $"Document: {Path.GetFileName(path)}\n\n{Extracts(pick, 60000)}\nQuestion: {q}", !detailed, false, prov, my.Token);
            if (my.IsCancellationRequested) return;
            History.Add("ask", prov.Id, Store.Model(prov.Id), "pdf", Path.GetFileName(path) + " · " + q, a);
            ShowAnswer(a);
            status.Text = $"✓ Answer from this PDF · {prov.Label} — click a [p.N] to open that page";
        }
        catch (Exception e) { if (!my.IsCancellationRequested) status.Text = "⚠ " + e.Message; }
    }

    async void Summary()
    {
        cts?.Cancel(); var my = cts = new CancellationTokenSource();
        try
        {
            var all = await Text_();
            if (all == null || all.Count == 0) { status.Text = "⚠ No readable text in this PDF — press 🔤 OCR this PDF first"; return; }
            var prov = Ai.Active() ?? throw new AiException("Add an AI key in Settings → AI & API keys");
            lastQuestion = "";
            status.Text = $"✦ Reading and checking with {prov.Label}…";
            var sys = "You are an experienced architect checking a construction document for the user. From the page texts (each starts with [p.N]) write:\n" +
                      "1. WHAT IT IS — document/drawing number, title, revision, date, scale, discipline, project (from the title block) [p.N].\n" +
                      "2. KEY CONTENT — what it shows or requires: main elements, materials, dimensions, ratings, standards, in short bullets with [p.N].\n" +
                      "3. NOTES & REFERENCES — referenced drawings, specs, RFIs, details.\n" +
                      "4. CHECK — possible issues for the user to verify: missing or unclear dimensions/notes, references to other revisions, contradictions, items marked TBC/hold, missing approvals or stamps. Advice only, never invent; say 'none found' if nothing.\n" +
                      "Keep it compact. Use the language of the document (English unless it is in Arabic or French)." + Modes.ProfileNote();
            var a = await Ai.Chat(sys, $"Document: {Path.GetFileName(path)}\n\n{Extracts(all, 60000)}", false, false, prov, my.Token);
            if (my.IsCancellationRequested) return;
            History.Add("ask", prov.Id, Store.Model(prov.Id), "pdf-check", Path.GetFileName(path), a);
            ShowAnswer(a);
            status.Text = $"✓ Summary and check · {prov.Label}";
        }
        catch (Exception e) { if (!my.IsCancellationRequested) status.Text = "⚠ " + e.Message; }
    }

    async void TranslatePage()
    {
        try
        {
            var all = await Text_();
            var p = (int)page.Value;
            var text = string.Join("\n", (all ?? new()).Where(x => PageOf(x) == p || (p == 1 && PageOf(x) == 0)).Select(x => x.Text));
            if (text.Trim().Length == 0) { status.Text = $"⚠ Page {p} has no readable text — try 🔤 OCR this PDF"; return; }
            if (text.Length > 20000) text = text[..20000];
            status.Text = $"🌐 Translating page {p} into {Store.TrLang}…";
            var prov = Ai.Active() ?? throw new AiException("Add an AI key in Settings → AI & API keys");
            var r = await Ai.Enhance(prov, "translate", text, CancellationToken.None);
            lastQuestion = "";
            ShowAnswer($"[p.{p}] → {Store.TrLang}\n\n" + r);
            status.Text = $"✓ Page {p} translated";
        }
        catch (Exception e) { status.Text = "⚠ " + e.Message; }
    }

    async void Ocr()
    {
        try
        {
            status.Text = "🔤 OCR on this PC — big drawings take a moment per page…";
            var p = path;
            List<(int, string)> pages = null;
            await Task.Run(() => pages = NovaEnhance.Ocr.Pdf(p, null, 200));
            if (pages == null || pages.Count == 0) { status.Text = "⚠ OCR found no text (or no OCR language is installed in Windows)"; return; }
            var dir = Path.Combine(Paths.Root, "OCR");
            Directory.CreateDirectory(dir);
            var outFile = Path.Combine(dir, Path.GetFileNameWithoutExtension(p) + " — OCR.txt");
            File.WriteAllText(outFile, string.Join("\n\n", pages.Select(x => $"===== page {x.Item1} =====\n{x.Item2}")), new UTF8Encoding(false));
            // Use the OCR text for Ask/Summary/Search too.
            segs = pages.Select(x => new Seg($"page {x.Item1}", x.Item2, "page:" + x.Item1)).Where(x => x.Text.Trim().Length > 0).ToList();
            status.Text = $"✓ OCR done · {pages.Count} page{(pages.Count == 1 ? "" : "s")} · saved in Documents\\Nova Enhance\\OCR";
            var unclear = pages.Count(x => NovaEnhance.Ocr.IsUnclear(x.Item2));
            if (unclear > 0 && Ai.HasKey("gemini") &&
                MessageBox.Show(this, $"{unclear} page(s) are hard to read for the PC OCR (drawings, stamps, handwriting, Arabic).\n\nSend those page images to Gemini vision for a better reading?", "Nova Enhance", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                foreach (var (pg, _) in pages.Where(x => NovaEnhance.Ocr.IsUnclear(x.Item2)).ToList())
                {
                    status.Text = $"Gemini vision · page {pg}…";
                    var pic = await Task.Run(() => NovaEnhance.Ocr.Picture(p, pg));
                    var txt = await Ai.Vision(pic.Data, pic.Mime);
                    segs.RemoveAll(x => PageOf(x) == pg);
                    segs.Add(new Seg($"page {pg}", txt, "page:" + pg));
                }
                File.WriteAllText(outFile, string.Join("\n\n", segs.OrderBy(PageOf).Select(x => $"===== page {PageOf(x)} =====\n{x.Text}")), new UTF8Encoding(false));
                status.Text = "✓ OCR done with Gemini vision for the unclear pages · saved in Documents\\Nova Enhance\\OCR";
            }
            Shell.Select(outFile);
        }
        catch (Exception e) { status.Text = "⚠ OCR: " + e.Message; Log.Error("PDF OCR", e); }
    }

    async void Find(string q)
    {
        q = (q ?? "").Trim();
        hits.Controls.Clear();
        if (q.Length == 0) return;
        var all = await Text_();
        int n = 0;
        foreach (var x in all ?? new())
        {
            var i = x.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            while (i >= 0 && n < 60)
            {
                var a = Math.Max(0, i - 60);
                var b = Math.Min(x.Text.Length, i + q.Length + 60);
                var snip = Regex.Replace(x.Text[a..b], @"\s+", " ").Trim();
                var pg = PageOf(x);
                var btn = Th.B($"p.{(pg <= 0 ? 1 : pg)}  ·  …{snip}…", () => GoTo(pg <= 0 ? 1 : pg));
                btn.Font = Th.F(9); btn.TextAlign = ContentAlignment.MiddleLeft; btn.AutoSize = false; btn.AutoEllipsis = true; btn.Width = Math.Max(300, s.ClientSize.Width - 50); btn.Height = 28;
                hits.Controls.Add(btn);
                n++;
                i = x.Text.IndexOf(q, i + q.Length, StringComparison.OrdinalIgnoreCase);
            }
        }
        if (n == 0) hits.Controls.Add(Th.L($"“{q}” not found in this PDF" + (all?.Count == 0 ? " (no readable text — try OCR)" : ""), 9.5f, Th.Muted));
        else status.Text = $"{n}{(n >= 60 ? "+" : "")} place{(n == 1 ? "" : "s")} found — click one to open that page";
    }
}
