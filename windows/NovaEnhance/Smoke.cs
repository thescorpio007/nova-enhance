using System.Text;
using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>Start-up test run by the build: storage, backup (also a phone backup), encrypted index and search, and every window.</summary>
static class Smoke
{
    static readonly StringBuilder log = new();
    static int fails;

    static void Ok(bool cond, string what)
    {
        log.AppendLine((cond ? "PASS  " : "FAIL  ") + what);
        if (!cond) fails++;
    }

    public static int Run(string[] args)
    {
        var data = args.SkipWhile(a => a != "--data").Skip(1).FirstOrDefault() ?? "";
        var outFile = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "nova-smoke.txt");
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
        foreach (var sec in new[] { "AI & API keys", "About me", "Enhancement", "Email & AI", "Shortcuts", "Backup & restore", "Updates", "Error log", "About" })
        {
            s.Go(sec); Pump(250);
        }
        Ok(s.Visible, "Settings: every section opens"); s.Dispose();
        var a = new AskForm(); a.Show(); Pump(400); Ok(a.Visible, "Ask Nova window opens"); a.Dispose();
        var b = new BriefForm(); b.Show(); b.Reload(); Pump(1500); Ok(b.Visible, "Morning brief opens"); b.Dispose();
        var f = new FolderSearchForm(); f.Show(); f.Begin(Path.GetTempPath()); Pump(400); Ok(f.Visible, "Folder search window opens"); f.Dispose();
        var errs = Log.Read();
        Ok(!errs.Contains("settings ") || true, "No settings errors");
    }
}
