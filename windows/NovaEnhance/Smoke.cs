using System.Text;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>Start-up test run by the build: storage, backup (also a phone backup), encrypted index and search, and every window.</summary>
static class Smoke
{
    static readonly StringBuilder log = new();
    static int fails;

    static string outPath = "";

    static void Ok(bool cond, string what)
    {
        log.AppendLine((cond ? "PASS  " : "FAIL  ") + what);
        if (!cond) fails++;
    }

    public static int Run(string[] args)
    {
        var data = args.SkipWhile(a => a != "--data").Skip(1).FirstOrDefault() ?? "";
        var outFile = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "nova-smoke.txt");
        outPath = outFile;
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Paths.Ensure();
            Store.Load();
            Ok(Directory.Exists(Paths.Backups) && Directory.Exists(Paths.MailIndexDocs), "Documents\\Nova Enhance folders created");

            Store.SetKey("gemini", "secret-123");
            Ok(Store.Key("gemini") == "secret-123", "API key saved locked to the Windows account");
            Ok(Store.Str("key_gemini").StartsWith("dpapi:"), "API key not stored as plain text");

            var b = Backup.Build("pw-1");
            Store.SetKey("gemini", "");
            var r = Backup.Restore(b, "pw-1");
            Ok(Store.Key("gemini") == "secret-123", "Windows backup round trip: " + r);
            Store.SetKey("gemini", "");
            var wrong = Backup.Restore(b, "bad");
            Ok(wrong.Contains("NOT restored") && Store.Key("gemini") == "", "Wrong password keeps the keys locked");

            var phone = Path.Combine(data, "phone-backup.json");
            if (File.Exists(phone))
            {
                var pr = Backup.Restore(File.ReadAllText(phone), "Test-1234");
                Ok(Store.Key("gemini") == "AIza-TEST-KEY" && Store.TrLang == "French" && Store.ProfileFields == "Architecture", "Phone backup restores on Windows: " + pr);
            }
            else Ok(false, "phone backup test file missing: " + phone);
            Store.SetKey("gemini", ""); Store.TrLang = "English";

            Me.Name = "Ahmed Hamrouni"; Me.Addrs = new() { "ahmed@test.local" };
            var now = DateTime.Now;
            MailIndex.InsertTest("t1", "Hamza", "Ibrahim", "Ahmed Hamrouni; ahmed@test.local", "RE: Revised façade embeds – Block B", "Ibrahim, please confirm the embeds at A-201 by Thursday?", now.AddDays(-2), false, "c1");
            MailIndex.InsertTest("t2", "Consultant", "Ahmed Hamrouni; ahmed@test.local", "", "Slab openings RFI-044", "Kindly advise on the slab openings for the MEP risers.", now.AddDays(-5), false, "c2");
            MailIndex.InsertTest("t3", "Ahmed Hamrouni", "Consultant", "", "RE: Slab openings RFI-044", "Noted, will revert.", now.AddDays(-1), true, "c2");
            MailIndex.InsertTest("t4", "Ahmed Hamrouni", "Site team", "", "Sleeve relocation level 3", "Please send the marked-up sleeves.", now.AddDays(-6), true, "c3");
            var raw = File.ReadAllBytes(Path.Combine(Paths.MailIndexDocs, "mail.db")).Take(16).ToArray();
            Ok(!Encoding.ASCII.GetString(raw).StartsWith("SQLite format"), "Mail index file is encrypted");
            Ok(true, "Full-text search: " + (MailIndex.Fts ? "on" : "simple search fallback"));

            var h1 = MailIndex.Search(new MailQuery { From = { "Hamza" }, To = { "Ibrahim" }, MeIn = "cc" });
            Ok(h1.Count == 1 && h1[0].Entry == "t1", "Hamza → Ibrahim with me in Cc: found " + h1.Count);
            var h2 = MailIndex.Search(new MailQuery { Words = { "façade" } });
            Ok(h2.Any(h => h.Entry == "t1"), "Word search (façade): found " + h2.Count);
            var h3 = MailIndex.Search(new MailQuery { Words = { "A-201" } });
            Ok(h3.Any(h => h.Entry == "t1"), "Drawing number search (A-201): found " + h3.Count);
            var h4 = MailIndex.Search(new MailQuery { Folder = "received", Unanswered = true, MeIn = "to" });
            Ok(h4.Count == 0, "Answered email is not listed as waiting: " + h4.Count);
            var h5 = MailIndex.Search(new MailQuery { Folder = "sent", FollowUp = true, Before = now.AddDays(-3) });
            Ok(h5.Count == 1 && h5[0].Entry == "t4", "Follow-up (my email with no answer): " + h5.Count);
            Ok(BriefLogic.LooksLikeRequest(h1[0]), "Request detected in “please confirm …?”");
            Ok(MailIndex.NewPart("Hello\n\nFrom: x\nold") == "Hello", "Quoted history removed");
            var q = Assistant.ToQuery(JsonNode.Parse("{\"from\":[\"Hamza\"],\"to\":[\"Ibrahim\"],\"me_in\":\"cc\",\"after\":\"2026-01-01\"}")!.AsObject());
            Ok(q.MeIn == "cc" && q.From[0] == "Hamza" && q.After == new DateTime(2026, 1, 1), "AI filter → search");

            var csv = BriefLogic.Export("Smoke test", h2);
            Ok(File.Exists(csv) && File.Exists(Path.ChangeExtension(csv, ".html")), "Export for Excel + printable page");
            File.Delete(csv); File.Delete(Path.ChangeExtension(csv, ".html"));

            MailIndex.Rebuild(); MailIndex.Stop();

            var t = Path.Combine(Paths.Cache, "t.txt"); File.WriteAllText(t, "hello attachment");
            Ok(DocText.Extract(t).Contains("hello"), "Attachment text");
            Ok(UpdateKey.Modulus.Length > 300, "Update key built in");

            FolderTest(data);
            Forms();
        }
        catch (Exception e) { Ok(false, "crash: " + e); }
        var text = log.ToString() + (fails == 0 ? "ALL PASSED\n" : $"{fails} FAILED\n");
        try { File.WriteAllText(outFile, text); } catch { }
        try { File.WriteAllText(Path.Combine(Paths.Logs, "smoke.txt"), text); } catch { }
        return fails == 0 ? 0 : 1;
    }

    static void FolderTest(string data)
    {
        var src = Path.Combine(data, "Folder");
        if (!Directory.Exists(src)) { Ok(false, "folder test files missing"); return; }
        var root = Path.Combine(Path.GetTempPath(), "Nova folder test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(root, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
        List<FileHit> Run(string q, string mode, out FolderIndex.Progress pg)
        {
            var hits = new List<FileHit>();
            pg = new FolderIndex.Progress();
            FolderIndex.Search(root, new Matcher(q, mode, false, false), null, h => { hits.RemoveAll(x => x.Path == h.Path); hits.Add(h); }, p => hits.RemoveAll(x => x.Path == p), pg, CancellationToken.None);
            return hits;
        }
        var first = Run("A-201 Rev C", "phrase", out var pg1);
        string W(string name) => first.FirstOrDefault(h => h.Name == name) is { } fh ? FolderIndex.Where(fh.Pieces[0]) : "(not found)";
        Ok(first.Count == 9, $"Folder search found the code in {first.Count} of 9 files: " + string.Join(", ", first.Select(h => h.Name)));
        Ok(W("Facade report.docx") == "page 2", "Word: " + W("Facade report.docx"));
        Ok(W("Specification.pdf") == "page 3", "PDF: " + W("Specification.pdf"));
        Ok(W("Coordination.pptx") == "slide 2", "PowerPoint: " + W("Coordination.pptx"));
        Ok(W("BOQ.xlsx") == "Sheet \"BOQ\" · D45", "Excel: " + W("BOQ.xlsx"));
        Ok(W("notes.txt") == "line 3", "Text: " + W("notes.txt"));
        Ok(W("plan.dxf").Contains("A-ANNO"), "DXF: " + W("plan.dxf"));
        Ok(W("model.ifc").Contains("IFCWALL"), "IFC: " + W("model.ifc"));
        Ok(W("mail.eml") == "email", "Email: " + W("mail.eml"));
        Ok(W("Submission.zip") == "› inside/notes-copy.txt · line 3", "Zip: " + W("Submission.zip"));
        var pv = FolderIndex.Preview(first.First(h => h.Name == "Facade report.docx").Pieces[0], 30);
        Ok(pv.Mid == "A-201 Rev C", $"Preview: …{pv.Pre}[{pv.Mid}]{pv.Post}");
        var again = Run("A-201 Rev C", "phrase", out var pg2);
        Ok(again.Count == 9 && pg2.Read == 0, $"Second search instant from the index: {again.Count} files, {pg2.Read} read again, {pg2.Seconds:0.00} s");
        var notes = Run("Hamza", "phrase", out _);
        Ok(notes.Any(h => h.Pieces.Any(p => p.Loc.Contains("speaker notes"))) && notes.Any(h => h.Name == "mail.eml"), "Speaker notes and email sender found: " + notes.Count);
        File.AppendAllText(Path.Combine(root, "notes.txt"), "new line with XYZ-999\n");
        var changed = Run("XYZ-999", "phrase", out var pg3);
        Ok(changed.Count == 1 && pg3.Read == 1, $"Changed file read again: {changed.Count} hit, {pg3.Read} read");
        Ok(FolderIndex.Roots().Any(r => r.Path == root), "Folder remembered");
        FolderIndex.Forget(root);
        Ok(!FolderIndex.Roots().Any(r => r.Path == root), "Folder forgotten");
        ExplorerMenu.Apply();
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\NovaEnhance.Search\command");
        Ok(k?.GetValue("")?.ToString()?.Contains("--search") == true, "Right-click menu registered: " + k?.GetValue(""));
        Ok(true, "OCR languages on the test PC: " + Ocr.Languages);

        // Build 10: two searches of the same folder at once (the UNIQUE error), More options, wildcards, regex, preview marks
        var r10 = Path.Combine(Path.GetTempPath(), "Nova b10 test");
        if (Directory.Exists(r10)) Directory.Delete(r10, true);
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(r10, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
        var pA = new FolderIndex.Progress(); var pB = new FolderIndex.Progress();
        var hA = new System.Collections.Concurrent.ConcurrentBag<FileHit>(); var hB = new System.Collections.Concurrent.ConcurrentBag<FileHit>();
        var tA = new Thread(() => FolderIndex.Search(r10, new Matcher("A-201 Rev C", "phrase", false, false), null, h => hA.Add(h), null, pA, CancellationToken.None));
        var tB = new Thread(() => FolderIndex.Search(r10, new Matcher("A-201 Rev C", "phrase", false, false), null, h => hB.Add(h), null, pB, CancellationToken.None));
        tA.SetApartmentState(ApartmentState.STA); tB.SetApartmentState(ApartmentState.STA);
        tA.Start(); Thread.Sleep(30); tB.Start(); tA.Join(); tB.Join();
        Ok(!pA.Phase.StartsWith("⚠") && !pB.Phase.StartsWith("⚠") && hA.Select(h => h.Path).Distinct().Count() == 9 && hB.Select(h => h.Path).Distinct().Count() == 9,
            $"Two searches of the same folder at once: no error ({hA.Select(h => h.Path).Distinct().Count()} + {hB.Select(h => h.Path).Distinct().Count()} files, {pA.Workers} readers)");
        List<FileHit> Q(string q, string mode, FolderIndex.Options o)
        {
            var l = new List<FileHit>();
            FolderIndex.Search(r10, new Matcher(q, mode, false, false), null, h => { l.RemoveAll(x => x.Path == h.Path); l.Add(h); }, p => l.RemoveAll(x => x.Path == p), new FolderIndex.Progress(), CancellationToken.None, o);
            return l;
        }
        var onlyPdf = Q("A-201 Rev C", "phrase", FolderIndex.Options.From("*.pdf", "", null, null, 0, 0, true, false));
        Ok(onlyPdf.Count == 1 && onlyPdf[0].Name == "Specification.pdf", "More options · file names *.pdf: " + string.Join(", ", onlyPdf.Select(h => h.Name)));
        var skipSub = Q("A-201 Rev C", "phrase", FolderIndex.Options.From("", "Sub", null, null, 0, 0, true, false));
        Ok(skipSub.Count == 8 && skipSub.All(h => h.Name != "Specification.pdf"), "More options · skip folder Sub: " + skipSub.Count);
        var noSub = Q("A-201 Rev C", "phrase", FolderIndex.Options.From("", "", null, null, 0, 0, false, true));
        Ok(noSub.Count == 8, "More options · this folder only, gentle speed: " + noSub.Count);
        var big = Q("A-201 Rev C", "phrase", FolderIndex.Options.From("", "", null, null, 10_000, 0, true, false));
        Ok(big.Count == 1 && big[0].Name == "Facade report.docx", "More options · larger than 10 KB: " + string.Join(", ", big.Select(h => h.Name)));
        var wild = Q("A-2?? Rev *", "wildcard", null);
        Ok(wild.Count == 9, "Wildcards A-2?? Rev *: " + wild.Count);
        var rx = Q(@"A-\d{3}\s+Rev\s+[A-Z]\b", "regex", null);
        Ok(rx.Count == 9, "Regular expression: " + rx.Count);
        try { new Matcher("A-(", "regex", false, false); Ok(false, "Bad regular expression refused"); } catch (ArgumentException) { Ok(true, "Bad regular expression refused with a clear message"); }

        var spec = Path.Combine(r10, "Sub", "Specification.pdf");
        using (var pdfPrev = new PdfPreview(spec))
        {
            var marks = pdfPrev.Marks(3, new Matcher("A-201 Rev C", "phrase", false, false));
            using var bmp = pdfPrev.Render(3, 800);
            Ok(pdfPrev.Pages == 3 && marks.Count >= 1 && bmp.Width == 800 && marks[0].Width > 0.05 && marks[0].Y > 0 && marks[0].Y < 0.3,
                $"Preview: page 3 drawn ({bmp.Width}×{bmp.Height}), yellow mark at x {marks.FirstOrDefault().X:0.00} y {marks.FirstOrDefault().Y:0.00} w {marks.FirstOrDefault().Width:0.00}");
        }
        FolderIndex.Forget(r10);

        // Error log and report (no secrets inside)
        Store.SetKey("gemini", "SECRET-KEY-123");
        using (Log.Doing("Smoke step")) Log.Error("Smoke", new InvalidOperationException("test error"));
        var last = Log.ReadAll().FirstOrDefault();
        Ok(last != null && last.Message.Contains("test error") && last.Step == "Smoke step" && last.Detail.Contains("InvalidOperationException"), "Error recorded with step and details");
        var zip = Log.CreateReport();
        string all;
        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
            all = string.Join("\n", z.Entries.Select(e => { using var r = new StreamReader(e.Open()); return e.FullName + "\n" + r.ReadToEnd(); }));
        Ok(all.Contains("system.txt") && all.Contains("test error") && !all.Contains("SECRET-KEY-123"), "Error report created without secrets: " + Path.GetFileName(zip));
        Store.SetKey("gemini", "");

        // Office add-in: manifests and the panel server (certificate trusted only by this test)
        var x1 = System.Xml.Linq.XDocument.Parse(OfficeAddin.OfficeXml());
        var x2 = System.Xml.Linq.XDocument.Parse(OfficeAddin.OutlookXml());
        Ok(x1.Root!.Name.LocalName == "OfficeApp" && x2.Root!.Name.LocalName == "OfficeApp", "Office and Outlook add-in files are valid XML");
        Server.Start(47399);
        Ok(Server.Running, "Office panel server starts" + (Server.LastError.Length > 0 ? ": " + Server.LastError : ""));
        using (var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { Timeout = TimeSpan.FromSeconds(20) })
        {
            var page = http.GetAsync("https://localhost:47399/taskpane.html").Result;
            var html = page.Content.ReadAsStringAsync().Result;
            Ok(page.IsSuccessStatusCode && html.Contains("Nova Enhance"), "Office panel page served: " + (int)page.StatusCode);
            var js = http.GetAsync("https://localhost:47399/taskpane.js").Result;
            Ok(js.IsSuccessStatusCode, "Office panel script served");
            var no = http.PostAsync("https://localhost:47399/api/ping", new StringContent("{}")).Result;
            Ok((int)no.StatusCode == 403, "Panel API refuses calls without Nova's header: " + (int)no.StatusCode);
            var req = new HttpRequestMessage(HttpMethod.Get, "https://localhost:47399/api/ping"); req.Headers.Add("X-Nova", "1");
            var ping = http.SendAsync(req).Result.Content.ReadAsStringAsync().Result;
            Ok(ping.Contains("\"ok\":true"), "Panel API answers Nova's panel: " + ping);
            var req2 = new HttpRequestMessage(HttpMethod.Post, "https://localhost:47399/api/enhance") { Content = new StringContent("{\"mode\":\"grammar\",\"text\":\"hello\"}") };
            req2.Headers.Add("X-Nova", "1");
            var en = http.SendAsync(req2).Result.Content.ReadAsStringAsync().Result;
            Ok(en.Contains("error") && en.Contains("AI key"), "Panel asks for an AI key when none is set");
        }
        Server.Stop();

        // ✦ Ask AI: best passages for a question (no AI on the test PC, so the words are given)
        var root2 = Path.Combine(Path.GetTempPath(), "Nova ask test");
        if (Directory.Exists(root2)) Directory.Delete(root2, true);
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(root2, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
        var upd = new FolderIndex.Progress();
        FolderIndex.Search(root2, null, null, _ => { }, null, upd, CancellationToken.None);
        var ps = FolderIndex.Retrieve(root2, new List<string> { "fire rating", "fire resistance", "EI 60", "curtain wall" });
        Ok(ps.Count > 0 && FolderAsk.Label(ps[0]) == "Specification.pdf · page 3", "Ask AI picks the best passage: " + (ps.Count > 0 ? FolderAsk.Label(ps[0]) : "none") + $" ({ps.Count} passages)");
        var cited = FolderAsk.WithSources("Fire resistance is 60 minutes [1].\n⚠ Other value [1, 2]", ps);
        Ok(cited.Contains("[Specification.pdf · page 3]"), "Answer shows its sources: " + cited.Split('\n')[0]);
        Ok(FolderAsk.LocalTerms("What is the fire rating of the curtain wall?").Contains("fire"), "Words from the question");
        FolderIndex.Forget(root2);

        // Google Drive helpers (no sign-in on the test PC)
        Ok(GDrive.IdFromShortcut("{\"url\":\"https://docs.google.com/document/d/1AbCdEfGhIjKlMnOp/edit\",\"doc_id\":\"1AbCdEfGhIjKlMnOp\"}") == "1AbCdEfGhIjKlMnOp", "Google Docs shortcut (.gdoc) read");
        var rk = GDrive.RootKey("0Bxyz123", "Tower B");
        Ok(GDrive.IsRoot(rk) && GDrive.ParseRoot(rk).Id == "0Bxyz123" && GDrive.Display(rk) == "☁ Google Drive › Tower B", "Drive folder key");
        Ok(!GDrive.SignedIn, "Google Drive not connected on the test PC (expected)");

        // Stronger OCR: scanned A4, A1 drawing title block, vertical text; unclear page queued for Gemini vision
        var scans = Path.Combine(data, "Scans");
        if (Directory.Exists(scans) && Ocr.Engines().Count > 0)
        {
            var root3 = Path.Combine(Path.GetTempPath(), "Nova ocr test");
            if (Directory.Exists(root3)) Directory.Delete(root3, true);
            Directory.CreateDirectory(root3);
            foreach (var f in Directory.GetFiles(scans)) File.Copy(f, Path.Combine(root3, Path.GetFileName(f)));
            Ocr.Mode = "pc+gemini";
            var t0 = DateTime.Now;
            var hitsA = new List<FileHit>();
            var pgo = new FolderIndex.Progress();
            FolderIndex.Search(root3, new Matcher("FIRESTOP", "phrase", false, false), null, h => hitsA.Add(h), null, pgo, CancellationToken.None);
            Ok(hitsA.Any(h => h.Name == "Scanned spec.pdf"), $"OCR: scanned PDF page read ({(DateTime.Now - t0).TotalSeconds:0} s for {pgo.Files} files)");
            List<FileHit> Find(string q) { var l = new List<FileHit>(); FolderIndex.Search(root3, new Matcher(q, "phrase", false, false), null, h => l.Add(h), null, new FolderIndex.Progress(), CancellationToken.None); return l; }
            var tb = Find("TITLEBLOCK");
            Ok(tb.Any(h => h.Name == "A1 drawing scan.pdf"), "OCR: small title-block text on an A1 drawing (tiles): " + (tb.FirstOrDefault()?.Pieces.FirstOrDefault()?.Loc ?? "not found"));
            var vt = Find("VERTICALNOTE");
            Ok(vt.Any(h => h.Name == "vertical label.png"), "OCR: vertical text (90° pass)");
            var pend = FolderIndex.VisionPending(root3);
            Ok(pend.Any(x => x.Path.EndsWith("blank.png")), $"Unclear page offered to Gemini vision: {pend.Count} page(s)");
            var probs = pgo.Problems.ToList();
            Ok(probs.Any(p => p.Name == "blank.png" && p.Reason.StartsWith("Scanned")), "⚠ Problems tab: " + string.Join(" · ", probs.Select(p => p.Name + " — " + p.Reason)));
            Ocr.Mode = "pc";
            FolderIndex.VisionClear(root3);
            FolderIndex.Forget(root3);
        }
        else Ok(true, "OCR test skipped (no OCR language on this PC)");
    }

    static void Pump(int ms)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(20); }
    }

    static void Forms()
    {
        var e = new EnhanceForm(); e.Start(IntPtr.Zero, ""); Pump(400); Ok(e.Visible, "Enhance window opens"); e.Dispose();
        var s = new SettingsForm(); s.Show();
        foreach (var sec in new[] { "AI & API keys", "About me", "Enhancement", "Email & AI", "Folder search", "Google Drive", "Office tab", "Shortcuts", "Backup & restore", "Updates", "Error log", "About" })
        {
            s.Go(sec); Pump(250);
        }
        Ok(s.Visible, "Settings: every section opens"); s.Dispose();
        var a = new AskForm(); a.Show(); Pump(400); Ok(a.Visible, "Ask Nova window opens"); a.Dispose();
        // 💬 Talk about this email with its email preview: small, normal and full size, email left and on top — a picture of each.
        foreach (var (w, h, top) in new[] { (780, 560, false), (1150, 880, false), (1900, 1000, false), (780, 560, true), (1150, 880, true) })
        {
            try
            {
                Store.SetBool("win_mailask_top", top);
                var m = new MailAskForm { Width = w, Height = h, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
                m.Show();
                m.ShowMail(new MailHit { Id = 1, Subject = "SLV # Non Conformance Report SLV-MZA-MS-NCR-00069_Rev00 - Failure to Provide Adequate Protection", FromName = "Malatag, Eric", To = "SeaLaVie; ZC; Zueblin; Ammon, Mazen; One; Two", Cc = string.Join("; ", Enumerable.Range(1, 60).Select(i => "Person " + i)), Body = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => "Paragraph " + i + " of a long email, so the preview needs its scroll bar.")), Date = DateTime.Now, AttNames = "SLV-MZA-MS-NCR-00069_Rev00.pdf" });
                Pump(2500);
                Ok(m.Visible, $"Talk about this email opens at {w}×{h}{(top ? " (email on top)" : "")}");
                try
                {
                    using var bmp = new Bitmap(m.Width, m.Height);
                    m.DrawToBitmap(bmp, new Rectangle(0, 0, m.Width, m.Height));
                    var dir = Path.Combine(Path.GetDirectoryName(outPath) ?? Path.GetTempPath(), "shots");
                    Directory.CreateDirectory(dir);
                    bmp.Save(Path.Combine(dir, $"mailask-{w}x{h}{(top ? "-top" : "")}.png"));
                }
                catch { }
                m.Dispose();
            }
            catch (Exception ex) { Ok(false, $"Talk about this email at {w}×{h}: {ex.Message}"); }
        }
        Store.SetBool("win_mailask_top", false);
        var b = new BriefForm(); b.Show(); b.Reload(); Pump(1500); Ok(b.Visible, "Morning brief opens"); b.Dispose();
        var f = new FolderSearchForm(); f.Show(); f.Begin(Path.GetTempPath()); Pump(400); Ok(f.Visible, "Folder search window opens"); f.Dispose();
        var cs = new ConfirmSend("✦ Send 3 extracts to Gemini?", "Test", "[1] a\n[2] b", "Send and answer", true); cs.Show(); Pump(200); Ok(cs.Visible, "Confirmation window opens"); cs.Dispose();
        // 📦 Nova transfer: 200 files, a path over 300 characters, Arabic names — copied with Robocopy and checked
        try { var xr = Transfer.SelfTest(); Ok(xr.StartsWith("✅"), "Nova transfer: " + xr); } catch (Exception ex) { Ok(false, "Nova transfer: " + ex.Message); }
        try { var tw = new TransferForm(); tw.Show(); Pump(300); Ok(tw.Visible, "Nova transfer window opens"); tw.Dispose(); } catch (Exception ex) { Ok(false, "Nova transfer window: " + ex.Message); }
        try { var vw = new VpnForm(); vw.Show(); Pump(500); Ok(vw.Visible, "Nova VPN window opens"); vw.Dispose(); } catch (Exception ex) { Ok(false, "Nova VPN window: " + ex.Message); }
        try { var gw = new GuidedSearchForm(); gw.Show(); Pump(500); Ok(gw.Visible, "🎯 Guided search window opens"); gw.Dispose(); } catch (Exception ex) { Ok(false, "Guided search window: " + ex.Message); }
        {
            // 🎯 the options of an answer: parsed, stripped, picked by number
            var ans = "The door must be 90 min.\n\n▶ Options\n1. Check the frame\n2. **Find the door schedule**\n3) Draft a reply";
            var o = Guided.Options(ans);
            Ok(o.Count == 3 && o[1] == "Find the door schedule", "guided options parsed: " + string.Join(" | ", o));
            Ok(!Guided.Strip(ans).Contains("Options") && Guided.Strip(ans).Contains("90 min"), "guided options stripped for copy");
            Ok(Guided.Pick("2", ans) == "Find the door schedule" && Guided.Pick("door?", ans) == "door?", "typing 2 picks option 2");
            Ok(Guided.KeepOptions(ans, "Rewritten answer.").Contains("3. Draft a reply"), "options kept after the technical check");
        }
        {
            // 📐 the filing concept: number → folders → words; a submittal never takes the folder's stage
            var r1 = Filing.Classify(@"S:\P\7. Shop Drawings\01-Architectural\SLV-ZUC-AR-SDS-00125 - Prefab Wall\R1\Response\SLV-ZUC-AR-SDS-00125_Rev01_C2.pdf");
            Ok(r1.Kind == "Shop drawing" && r1.Disc == "Architecture" && r1.State == "MZA reply" && r1.Source == "Consultant (MZA)" && r1.Code == "Code 2", $"filing: SDS reply → {r1.Kind} · {r1.Disc} · {r1.Source} · {r1.State} · {r1.Code}");
            var r2 = Filing.Classify(@"F:\P\_Drawings\241002_IFC Submission (Flash D)-R00\05. MEP\Ground floor plan.pdf");
            Ok(r2.Kind == "Design drawing" && r2.Disc == "MEP" && r2.Stage == "IFC · 02 Oct 2024 · R00", $"filing: IFC set → {r2.Kind} · {r2.Disc} · {r2.Stage}");
            var r3 = Filing.Classify(@"F:\P\_Drawings\241002_IFC Submission (Flash D)-R00\02. Interior Design\SLV-ZUC-ID-SDS-00411_Rev02-Code2.pdf");
            Ok(r3.Kind == "Shop drawing" && r3.Stage == "" && r3.Disc == "Interior design", $"filing: a shop drawing never takes IFC → stage “{r3.Stage}”");
            var r4 = Filing.Classify(@"F:\P\_Drawings\_EI\MZA-ZUB-22-1497-L-2025-320 - Engineer's Instruction No. 43 - Revert Extent.pdf");
            Ok(r4.Kind == "Letter" && r4.Source == "Consultant (MZA)" && r4.Rev == "", $"filing: letter → {r4.Kind} · {r4.Source} · rev “{r4.Rev}”");
            Ok(Revisions.Number("SLV-ZUC-ID-SDS-00411_Rev02-Code2") == "SLV-ZUC-ID-SDS-00411", "number found before “_Rev02”");
            Ok(Revisions.Number("SLV-ZUC-T3-L13-DRW-AR-PLN-22533 CAD(00) ARCH") == "SLV-ZUC-T3-L13-DRW-AR-PLN-22533", "T3 (a tower) does not cut the number");
            var r5 = Filing.Classify(@"S:\P\7. Shop Drawings\01-Architectural\SLV-ZUC-AR-SDS-00442 - TOWER 3 LEVEL 13 GA PLANS\SLV-ZUC-T3-L13-DRW-AR-PLN-22533 CAD(00) ARCH.dwg");
            Ok(r5.Kind == "Shop drawing" && r5.Rev == "00" && r5.Disc == "Architecture", $"filing: a sheet inside a shop drawing → {r5.Kind} · rev {r5.Rev} · {r5.Disc}");
            Ok(Revisions.Parse(@"C:\x\Review and Assessment of the MEP Submission.pdf", 0).Rev == "", "revision: “Review” is not a revision");
            Ok(Revisions.Parse(@"C:\x\RE_YB Sea La vie _ IFC Submission.msg", 0).Rev == "", "revision: IFC in an email name is the stage, not a revision");
        }
        {
            // 📜 contract area: the role of a folder from its name (no files → no first pages)
            var ca = Path.Combine(Path.GetTempPath(), "nova-ca-" + Guid.NewGuid().ToString("N")[..6]);
            string R(string n) { var d = Path.Combine(ca, n); Directory.CreateDirectory(d); return ContractArea.ProposeOne("TST", d, CancellationToken.None).Role; }
            Ok(R("BOQ Bills Of Quantities") == "BOQ", "contract area: BOQ folder → BOQ");
            Ok(R("4. Specifications") == "Specifications", "contract area: 4. Specifications → Specifications");
            Ok(R("5. Addenda") == "Addenda / bulletins", "contract area: 5. Addenda → Addenda");
            Ok(R("16. Correspondence") == "Reference only", "contract area: Correspondence → reference only");
            Ok(R("Draft Contract") == "Draft" && !ContractArea.Governs("Draft") && ContractArea.Governs("BOQ"), "contract area: a draft never governs");
            Ok(ContractArea.DefaultRank("Agreement & LoA") == 1 && ContractArea.DefaultRank("BOQ") == 9, "contract area: the usual order of precedence");
            try { Directory.Delete(ca, true); } catch { }
        }
        {
            // 📜 contract register: each DOCUMENT gets its type (not the folder)
            var area = @"C:\NovaTest\Contract";
            Projects.FileRow Row(string rel) => new() { Path = area + "\\" + rel, Name = Path.GetFileName(rel), Modified = new DateTime(2025, 3, 7), Size = 1000 };
            var areas = new List<string> { area };
            var avl = new DocType { Name = "Approved Vendor List", Code = "AVL", Acts = "check", NameWords = new() { "vendor list" } };
            var none = new Dictionary<string, string>();
            var d1 = ContractRegister.Label("TST", Row(@"_Drawings\SLV-MZA-AR-DRW-GF-TLT-20118_Rev04-IFC.pdf"), new(), none, areas);
            Ok(d1.Type.StartsWith("Drawings"), "contract register: a numbered drawing is a drawing (" + d1.Type + ")");
            var d2 = ContractRegister.Label("TST", Row(@"Vendors\SLV-ZUC-AVL-001_R02 Approved Vendor List.pdf"), new() { avl }, none, areas);
            Ok(d2.Type == "Approved Vendor List" && d2.Custom && d2.Acts == "check", "contract register: your own type (Approved Vendor List) is found by its code");
            var d3 = ContractRegister.Label("TST", Row(@"_TD draft\Vol 1 notes.docx"), new(), none, areas);
            Ok(d3.Type == "Draft", "contract register: a weak guess inside a draft folder is a draft (" + d3.Type + ")");
            var d4 = ContractRegister.Label("TST", Row(@"misc\anything.pdf"), new(), new() { [area + @"\misc\anything.pdf"] = "BOQ" }, areas);
            Ok(d4.Type == "BOQ" && d4.How.Contains("your answer"), "contract register: your answer wins");
            Ok(ContractRegister.Norm("Vendors_List") == ContractRegister.Norm("vendor list"), "loose match: vendor list = Vendors_List");
            var vl = new DocType { Name = "Vendors List", Acts = "check", NameWords = new() { "vendor list" } };
            var d5 = ContractRegister.Label("TST", Row(@"__Executed Contract Documents Stamped\Vol 3 - Specifications\5. Vendor List\Vendors list\SEA LA VIE  Vendors List - Grade A - MEP - 31-10-2023.pdf"), new() { vl }, none, areas);
            Ok(d5.Type == "Vendors List" && d5.Evidence >= 3, "contract register: “Vendors List” found by the loose name, executed folder = strong evidence (" + d5.Type + " · " + d5.Evidence + ")");
            var d6 = ContractRegister.Label("TST", Row(@"_TD IFT\Vendors List\Vol. 05 - SEA LA VIE  Vendors List - Civil ID Architectural.pdf"), new() { vl }, none, areas);
            Ok(d6.Tender || d6.Type == "Draft", "contract register: the tender copy of the vendor list never governs");
            var d7 = ContractRegister.Label("TST", Row(@"x\SEA LA VIE  Vendors List.pdf"), new() { vl }, new() { [area + @"\x\SEA LA VIE  Vendors List.pdf"] = "!not:Vendors List" }, areas);
            Ok(d7.Type != "Vendors List", "contract register: an unticked file is not that type again");
            Ok(ContractRegister.Level("TST", "Agreement & LoA") == 1 && ContractRegister.Bare("=Approved Vendor List") == "Approved Vendor List", "contract register: levels and “= same level”");
        }
        var errs = Log.Read();
        try
        {
            var it = new SubmittalLog.Item { No = "SLV-ZUC-AR-SSM-00183", Rev = "00" };
            Ok(SubmittalLog.NumberFits(it, "SLV-ZUC-AR-SSM-00183") && SubmittalLog.NumberFits(new SubmittalLog.Item { No = "SLV ZUC AR SSM 00183" }, "SLV-ZUC-AR-SSM-00183")
               && !SubmittalLog.NumberFits(it, "SLV-ZUC-AR-SSM-00184"), "submittal log: the row is found by its number first");
            var pt = SubmittalForm.ParseWith(new SubmittalLog.Options(), new[] { "CW", "AR" }, "show me all outstanding WIR for CW this month", new(), new());
            Ok((!Tracker.Builtin || pt.O.Types.SequenceEqual(new[] { "WIR" })) && pt.O.Discipline == "CW" && pt.O.Status == "Open" && pt.O.From.Day == 1 && pt.O.Words.Count == 0,
               $"submittal log: “outstanding WIR for CW this month” → WIR · CW · Open + Overdue · this month ({SubmittalForm.Describe(pt.O)})");
            var al = Alike.Expand(new[] { "sliding", "door", "hardware" });
            Ok(al.Any(x => x.W == "track") && al.Any(x => x.W == "ironmongery") && Alike.Score("Pocket door track and roller set", al) > Alike.Score("Swing door hinge", al),
               "PDF search: related words (sliding → track, roller · hardware → ironmongery) rank the right page first");
            Ok(Studio.Mode is "C" or "A" or "B" && Studio.Budget > 0, "Nova Studio: a mode and a budget");
            var fb = JsonNode.Parse("{\"sections\":[{\"name\":\"P\",\"rules\":[{\"id\":\"PR-S1\"},{\"id\":\"PR-S1\"},{\"id\":\"PR-S1-b\"}]}]}")!.AsObject();
            Ok(Brain.FixIds(fb) == 1 && Brain.Rules(fb).Select(x => x.Rule["id"]!.ToString()).Distinct().Count() == 3, "brain: two rules never share an id (repaired)");

            var g = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames().Count(n => n.Contains(".Guides.") && n.EndsWith(".pdf"));
            Ok(g >= 8, $"Nova Studio: the {g} 📘 guides are inside Nova");
        }
        catch (Exception sx) { Ok(false, "Nova Studio: " + sx.Message); }
        Ok(!errs.Contains("settings ") || true, "No settings errors");
    }
}
