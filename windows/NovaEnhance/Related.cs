using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🔗 Find related: every email about one document number, all revisions (Rev00 … Rev03, R00 … R01), every folder and archive.
/// In Nova: a timeline per revision (submitted → MZA reply with its code → forwarded → reminders). In Outlook: Outlook's own search
/// across all mailboxes and open archives. From the ✦ Nova tab, Outlook's right-click, or any Nova row. Designed by A.HAMROUNI
/// </summary>
static class Related
{
    // A document number ends at anything that is not a letter or digit: “…-MAR-00182_Rev00” → SLV-ZUC-AR-MAR-00182 (was: stopped at
    // the “_” and kept “SLV-ZUC-AR-MAR”, then searched the whole subject as words — every drawing came up).
    static readonly Regex DocRx = new(@"(?<![A-Za-z0-9-])[A-Z0-9]{2,}(?:-[A-Z0-9]{1,8}){3,}(?![A-Za-z0-9])");
    static readonly Regex AutoRx = new(@"^\s*(automatic reply|auto(matic)?[- ]?reply|out of (the )?office|undeliverable|delivery (status|has failed)|read:|not read:)", RegexOptions.IgnoreCase);
    static readonly Regex MzaResponse = new(@"with\s+MZA\s+Response", RegexOptions.IgnoreCase);
    static readonly Regex RevTail = new(@"(?:[_\-\s]*(?:Rev|R)\s?\d{1,2})$", RegexOptions.IgnoreCase);
    static readonly Regex ReplyRx = new(@"^\s*((re|aw|antw)\s*:\s*|\[external\]\s*)+", RegexOptions.IgnoreCase);
    static readonly Regex FwdRx = new(@"^\s*(fw|fwd|tr)\s*:", RegexOptions.IgnoreCase);
    static readonly Regex Strip = new(@"^\s*((re|aw|antw|fw|fwd|tr|reminder)\s*[:\-]\s*|\[external\]\s*)+", RegexOptions.IgnoreCase);

    /// <summary>The document number without its revision (SLV-ZUC-AR-SSM-00047_Rev03 → SLV-ZUC-AR-SSM-00047), from the subject,
    /// then the attachment names, then the text. "" when the email has none.</summary>
    public static string BaseOf(MailHit h)
    {
        if (h == null) return "";
        List<string> types;
        try { types = SubmittalLog.Enabled(); } catch { types = new(); }
        foreach (var t in new[] { h.Subject, h.AttNames, h.NewPart.Length > 0 ? h.NewPart[..Math.Min(h.NewPart.Length, 2000)] : "" })
        {
            var text = t ?? "";
            var cands = DocRx.Matches(text)
                .Where(m => Regex.IsMatch(m.Value, @"\d{2,}") && !Regex.IsMatch(text[Math.Max(0, m.Index - 14)..m.Index], @"int\s*[:.]", RegexOptions.IgnoreCase))
                .Select(m => RevTail.Replace(m.Value, "").ToUpperInvariant()).Distinct().ToList();
            if (cands.Count == 0) continue;
            // Several numbers: the one with a logged type (MAR, SDS, WIR…) is the document; the others are letters / internal refs.
            return cands.FirstOrDefault(c => c.Split('-').Any(seg => types.Contains(seg, StringComparer.OrdinalIgnoreCase))) ?? cands[0];
        }
        return "";
    }

    static string Clean(string subj) => Regex.Replace(Strip.Replace(subj ?? "", ""), @"\s+", " ").Trim();

    /// <summary>✦ Nova tab / Outlook right-click: the email open or selected in Outlook.</summary>
    public static void FromOutlook(bool inOutlook)
    {
        MailHit h = null;
        try { using var step = UiWatch.Outlook("reading the selected email"); h = Outlook.CurrentMail(); }
        catch (Exception e) { MessageBox.Show("Outlook: " + e.Message, "Nova Enhance"); return; }
        if (h == null) { MessageBox.Show("Select or open an email in Outlook first.", "Nova Enhance · Find related"); return; }
        Find(h, inOutlook);
    }

    /// <summary>From any email: its document number → the timeline (Nova) or Outlook's search; no number → the subject is used.</summary>
    public static void Find(MailHit h, bool inOutlook)
    {
        var b = BaseOf(h);
        if (b.Length == 0)
        {
            // No document number: Outlook searches the exact subject; Nova shows the email's own thread (never a word search).
            var subj = Clean(h?.Subject);
            if (subj.Length == 0) { MessageBox.Show("This email has no document number or subject to search for.", "Nova Enhance"); return; }
            if (inOutlook) InOutlook(subj);
            else { Program.Balloon("🔗 Find related", "No document number in this email — showing its thread instead."); Screens.AskFor($"the whole thread of “{Outstanding.NormSubject(h.Subject ?? "")}”"); }
            return;
        }
        if (inOutlook) InOutlook(b); else Screens.Timeline(b, Clean(h?.Subject));
    }

    /// <summary>🔎 Outlook's own search (all mailboxes and open archives) for the number — results in Outlook's list.</summary>
    public static void InOutlook(string key)
    {
        void Go()
        {
            try
            {
                using var step = UiWatch.Outlook("searching in Outlook");
                dynamic app = Outlook.Get();
                dynamic ex = null;
                try { ex = app.ActiveExplorer(); } catch { }
                if (ex == null)
                {
                    dynamic ns = app.GetNamespace("MAPI");
                    dynamic inbox = ns.GetDefaultFolder(6);
                    inbox.Display();
                    ex = app.ActiveExplorer();
                }
                ex.Search("\"" + key.Replace("\"", "") + "\"", 2);   // 2 = olSearchScopeAllOutlookItems
                try { ex.Activate(); } catch { }
                using (var act = Activity.Begin("🔗 Find related · in Outlook", key)) act.Result = "Outlook search opened";
            }
            catch (Exception e) { MessageBox.Show("Outlook could not search: " + e.Message + "\n\nTry 🔗 In Nova — it reads Nova's own index.", "Nova Enhance"); }
        }
        if (Program.UiContext != null) Program.UiContext.Post(_ => Go(), null); else Go();
    }

    // ------------------------------------------------------------------ the timeline (Nova)

    public static int? Rev(string text, string key)
    {
        var m = Regex.Match(text ?? "", Regex.Escape(key) + @"[\s_\-]*(?:Rev|R)\s?(\d{1,2})\b", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    static string CodeOf(MailHit h)
    {
        var a = h.AttNames ?? "";
        var m = Regex.Match(a + " " + h.Subject, @"-C([1-5])(?![0-9])|\bCode\s*-?\s*([1-5])\b", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match((h.Body ?? "").Length > 0 ? h.Body[..Math.Min(h.Body.Length, 4000)] : "", @"Approval result\s*[:\-]?\s*Code\s*-?\s*([1-5])|\bCode\s*-?\s*([1-5])\b", RegexOptions.IgnoreCase);
        return m.Success ? "Code " + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : "";
    }

    /// <summary>Every email about the number, grouped by revision (newest first), each one with what it is.</summary>
    public static JsonObject Timeline(string key)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mails = MailIndex.SubmittalsLike(new[] { "%" + key + "%" }, new[] { "%" + key + "%" }, DateTime.Today.AddYears(-8), DateTime.Now.AddDays(1), 600, 2000)
            .GroupBy(h => (h.Subject, h.Date, h.FromAddr)).Select(g => g.First())
            // exactly this number (00557 is not 005570), no automatic replies / receipts
            .Where(h => Regex.IsMatch((h.Subject ?? "") + " " + (h.AttNames ?? ""), Regex.Escape(key) + @"(?![0-9])", RegexOptions.IgnoreCase) && !AutoRx.IsMatch(h.Subject ?? ""))
            .OrderBy(h => h.Date).ToList();
        var mza = Brain.ListOf("mza.match", Tracker.Us);
        bool Us(MailHit h) => h.Sent || mza.Any(m => (h.FromName ?? "").Contains(m, StringComparison.OrdinalIgnoreCase) || (h.FromAddr ?? "").Contains(m, StringComparison.OrdinalIgnoreCase));
        var revs = new JsonArray();
        int? lastRev = null;
        foreach (var g in mails.GroupBy(h => Rev(h.Subject, key) ?? Rev(h.AttNames, key) ?? -1).OrderByDescending(g => g.Key))
        {
            var events = new JsonArray();
            string code = "", closedAt = "";
            foreach (var h in g.OrderBy(x => x.Date))
            {
                var s = h.Subject ?? "";
                var c = Us(h) ? CodeOf(h) : "";
                string icon, what;
                // Thinkproject's “…_with MZA Response” copy is MZA's coded answer, though Thinkproject sends it.
                if (MzaResponse.IsMatch(s)) { c = CodeOf(h); icon = c is "Code 3" or "Code 4" ? "⛔" : "✅"; what = $"{Tracker.UsShort} response" + (c.Length > 0 ? " · " + c : ""); if (c.Length > 0) { code = c; closedAt = h.Date.ToString("dd MMM yyyy"); } }
                else if (FwdRx.IsMatch(s)) { icon = "↪"; what = "Forwarded"; }
                else if (!ReplyRx.IsMatch(s) && !Us(h)) { icon = "📥"; what = "Submitted"; }
                else if (c.Length > 0) { icon = c is "Code 3" or "Code 4" ? "⛔" : "✅"; what = $"{Tracker.UsShort} reply · {c}"; code = c; closedAt = h.Date.ToString("dd MMM yyyy"); }
                else if (Us(h)) { icon = "💬"; what = ReplyRx.IsMatch(s) ? $"{Tracker.UsShort} reply" : $"Sent by {Tracker.UsShort}"; }
                else { icon = "💬"; what = "Reply"; }
                if (Regex.IsMatch(s + " " + h.FirstLines(200), @"\breminder\b|\bgentle reminder\b|\bfollow[- ]?up\b", RegexOptions.IgnoreCase) && icon != "✅") { icon = "⏰"; what = "Reminder"; }
                events.Add(new JsonObject
                {
                    ["id"] = h.Id, ["date"] = h.Date.ToString("dd MMM yyyy HH:mm"), ["icon"] = icon, ["what"] = what,
                    ["who"] = h.Sent ? "me" : h.FromName ?? "", ["subject"] = s, ["folder"] = h.Folder ?? "",
                    ["atts"] = string.Join("; ", (h.AttNames ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => !Regex.IsMatch(n, @"^image\d*\.(png|jpe?g|gif)$", RegexOptions.IgnoreCase)).Take(4))
                });
            }
            var label = g.Key < 0 ? "No revision in the subject" : $"Rev{g.Key:00}";
            var status = code.Length > 0 ? $"{code} · closed {closedAt}" : g.Key >= 0 && lastRev != null && g.Key < lastRev ? "superseded" : "Open — no coded reply yet";
            revs.Add(new JsonObject { ["rev"] = label, ["status"] = status, ["code"] = code, ["superseded"] = g.Key >= 0 && lastRev != null && g.Key < lastRev, ["events"] = events });
            if (g.Key >= 0) lastRev ??= g.Key;
        }
        // 📄 The files with this number in the project folders and 🗂 sources (contract weight first, latest revision on top).
        var files = new JsonArray();
        try
        {
            var proj = mails.Select(Projects.ForMail).FirstOrDefault(p => p != null);
            var folders = (proj != null ? new[] { proj } : Projects.All()).SelectMany(p => p.Folders.Concat((p.Sources ?? new()).Select(x => x.Path)))
                .Where(f => !Projects.Private.IsMatch(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var d in Projects.FindDoc(key, folders).Where(d => !Projects.Private.IsMatch(d.Path))
                .OrderBy(d => d.Status == "latest" ? 0 : 1).ThenBy(d => ProjectSources.Of(d.Path) is { } x ? ProjectSources.Rank(x.S.Kind) : 7).Take(20))
                files.Add(new JsonObject { ["path"] = d.Path, ["name"] = System.IO.Path.GetFileName(d.Path), ["rev"] = d.Rev, ["status"] = d.Status, ["src"] = ProjectSources.Tag(d.Path), ["cad"] = d.Cad });
        }
        catch (Exception e) { Log.Warn("find related · files", e.Message); }
        var title = mails.Select(h => Clean(h.Subject)).FirstOrDefault(t => t.Length > 0) ?? key;
        title = Regex.Replace(title, @"^.*?" + Regex.Escape(key) + @"[\s_\-]*(?:Rev|R)?\s?\d{0,2}[\s_\-]*", "", RegexOptions.IgnoreCase).Trim(' ', '-', '_');
        return new JsonObject
        {
            ["t"] = "timeline", ["key"] = key, ["title"] = title, ["count"] = mails.Count, ["revs"] = revs, ["files"] = files,
            ["secs"] = Math.Round(sw.Elapsed.TotalSeconds, 1)
        };
    }

    /// <summary>The timeline as the agent's table (for ⬇ Excel with email links and ⧉ Copy).</summary>
    public static Agent.Result AsTable(JsonObject t)
    {
        var r = new Agent.Result { Text = $"🔗 {t["key"]} · {t["title"]} — {t["count"]} email(s)", Columns = new() { "Revision", "Status", "Date", "What", "From", "Subject", "Files" } };
        foreach (var rv in (t["revs"] as JsonArray ?? new()).OfType<JsonObject>())
            foreach (var e in (rv["events"] as JsonArray ?? new()).OfType<JsonObject>())
                r.Rows.Add(new Agent.Row
                {
                    Id = long.TryParse(e["id"]?.ToString(), out var id) ? id : 0,
                    Cells = new() { rv["rev"]?.ToString() ?? "", rv["status"]?.ToString() ?? "", e["date"]?.ToString() ?? "", e["icon"] + " " + e["what"], e["who"]?.ToString() ?? "", e["subject"]?.ToString() ?? "", e["atts"]?.ToString() ?? "" }
                });
        foreach (var f in (t["files"] as JsonArray ?? new()).OfType<JsonObject>())
            r.Rows.Add(new Agent.Row { Id = 0, Cells = new() { "📄 File", f["status"]?.ToString() ?? "", f["rev"]?.ToString() is { Length: > 0 } rv2 ? "rev " + rv2 : "", f["src"]?.ToString() ?? "", "", f["name"]?.ToString() ?? "", f["path"]?.ToString() ?? "" } });
        return r;
    }
}

/// <summary>🔗 The timeline window. Designed by A.HAMROUNI</summary>
class RelatedForm : WebPage
{
    string key, hint;
    JsonObject last;

    public RelatedForm(string k, string subjectHint) : base("🔗 Find related", "related.html", 1150, 820) { key = k; hint = subjectHint ?? ""; }

    public void Load2(string k, string subjectHint)
    {
        key = k; hint = subjectHint ?? "";
        if (Ready) Run();
    }

    void Run() => Work($"Finding every email about {key}…", () =>
    {
        var t = Related.Timeline(key); last = t;
        // 🔎 Then Outlook is asked live: an email with this number that Nova's index does not have yet → said, and brought in.
        var k = key;
        _ = Task.Run(() =>
        {
            try
            {
                var live = ClaudeOutlook.LiveSubject(k, 30).Where(x => !Regex.IsMatch(x.Subject ?? "", @"^\s*(automatic reply|out of office|undeliverable)", RegexOptions.IgnoreCase))
                    .Where(x => Regex.IsMatch(x.Subject ?? "", Regex.Escape(k) + @"(?![0-9])", RegexOptions.IgnoreCase)).ToList();
                if (live.Count == 0) { Post(new JsonObject { ["t"] = "live", ["text"] = "🔎 Outlook checked live: no email with this number in Outlook's open folders." }); return; }
                var known = MailIndex.KnownEntries(live.Select(x => x.Entry).ToList());
                var missing = live.Where(x => !known.Contains(x.Entry)).ToList();
                if (missing.Count > 0) MailIndex.SyncNow();
                Post(new JsonObject { ["t"] = "live", ["text"] = missing.Count == 0
                    ? $"✓ Outlook checked live: {live.Count} email(s) with this number — all in this timeline."
                    : $"⚠ Outlook checked live: {missing.Count} email(s) only in Outlook ({string.Join(" · ", missing.Take(3).Select(x => x.Subject))}) — Nova is adding them; press ↻ in a minute." });
            }
            catch (Exception e) { Log.Warn("find related · live", e.Message); }
        });
        return t;
    });

    protected override void OnMessage(JsonObject m)
    {
        switch (m["t"]?.ToString())
        {
            case "init": case "again": Run(); break;
            case "open":
            {
                if (!long.TryParse(m["id"]?.ToString(), out var id)) break;
                var h = MailIndex.ByIds(new[] { id }, 1, 0).FirstOrDefault();
                if (h == null) break;
                if (m["outlook"]?.ToString() is "true" or "True") Outlook.Open(h.Entry, h.StoreId); else Screens.MailAsk(h);
                break;
            }
            case "file":
            {
                // only a file this timeline listed is opened
                var pth = m["path"]?.ToString() ?? "";
                if (last?["files"] is JsonArray fs && fs.OfType<JsonObject>().Any(f => f["path"]?.ToString() == pth)) Shell.Open(pth);
                break;
            }
            case "outlook": Related.InOutlook(key); break;
            case "excel":
                if (last != null) try { Shell.Open(Export.AgentExcel(Related.AsTable(last), "Related " + key)); } catch (Exception e) { Post(new JsonObject { ["t"] = "error", ["text"] = e.Message }); }
                break;
            case "copy":
                if (last != null)
                {
                    var t = Related.AsTable(last);
                    Clipboard.SetText(string.Join("\r\n", new[] { string.Join("\t", t.Columns) }.Concat(t.Rows.Select(r => string.Join("\t", r.Cells)))));
                    Post(new JsonObject { ["t"] = "toast", ["text"] = "⧉ Copied — paste into Excel, Word or an email" });
                }
                break;
            case "ask": Screens.Hub($"Give me the full history of {key}{(hint.Length > 0 ? " (" + hint + ")" : "")}: every revision, who submitted, MZA's code and date, what is still open."); break;
        }
    }
}
