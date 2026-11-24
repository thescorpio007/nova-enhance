using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🔗 Claude ↔ your installed Outlook (direct, live — not Nova's index). Only while YOU allow it (tray → 🔗 Allow Claude: 2 h / 8 h /
/// today; it switches itself off). Read-only in Outlook: Claude can never send, delete, move or reply. Every answer is encrypted for
/// Claude's session key (RSA-OAEP + AES-256-GCM) before it reaches GitHub — the relay only holds scrambled data. 🔒 Private words,
/// TrustWalletBackup and the admin PIN are never sent. Every email Claude reads is listed in Activity. Claude may fix Nova's index
/// directly (re-index a folder, re-sort an email's project) — logged with ↩ undo; Brain rules still need your 🧪 TEST + ✓ Apply.
/// Designed by A.HAMROUNI
/// </summary>
static class ClaudeAccess
{
    public static DateTime Until
    {
        get => long.TryParse(Store.Str("win_claude_until", "0"), out var t) && t > 0 ? new DateTime(t) : DateTime.MinValue;
        set => Store.Set("win_claude_until", value.Ticks.ToString());
    }
    public static bool On => DateTime.Now < Until;

    public static void Allow(double hours)
    {
        Until = hours <= 0 ? DateTime.MinValue : hours >= 24 ? DateTime.Today.AddDays(1).AddSeconds(-1) : DateTime.Now.AddHours(hours);
        using var a = Activity.Begin("🔗 Claude · Outlook", hours <= 0 ? "access stopped" : $"access allowed until {Until:dd MMM HH:mm}");
    }

    public static string Line => On ? $"🔗 Claude can read Outlook until {Until:HH:mm}" : "";
}

static class ClaudeOutlook
{
    // ------------------------------------------------------------------ encryption for Claude's session key

    /// <summary>The answer, readable only with Claude's private key: RSA-OAEP-SHA256 wraps a fresh AES-256-GCM key.</summary>
    public static string Seal(string text, string pubB64)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubB64), out _);
        var key = RandomNumberGenerator.GetBytes(32); var iv = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(text); var ct = new byte[plain.Length]; var tag = new byte[16];
        using (var g = new AesGcm(key, 16)) g.Encrypt(iv, plain, ct, tag);
        var k = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
        return "ENC1:" + new JsonObject { ["k"] = Convert.ToBase64String(k), ["iv"] = Convert.ToBase64String(iv), ["tag"] = Convert.ToBase64String(tag), ["ct"] = Convert.ToBase64String(ct) }.ToJsonString();
    }

    // ------------------------------------------------------------------ the tools

    public static readonly Dictionary<string, string> Help = new()
    {
        ["ol.search"] = "search Outlook live: words (all, subject or text), subject, from, to, cc, after, before (yyyy-mm-dd), folder (part of the path), store (part of the name), limit (≤150)",
        ["ol.read"] = "one email in full: id (from ol.search)",
        ["ol.thread"] = "every email of the same conversation: id",
        ["ol.folders"] = "every mailbox / archive with its mail folders and counts, vs Nova's index",
        ["ol.compare"] = "like ol.search, and says for each email whether Nova's index has it (missing ones first)",
        ["nova.followup"] = "Nova's own 📨 follow-up tool on its index (what the agent uses): to (name / address / domain), project, after, before, from — every request and the reply per document",
        ["fix.reindex"] = "Nova re-reads a folder completely (fills gaps): folder (part of the path) or id (its folder)",
        ["fix.project"] = "puts emails into a project in Nova: ids (list), project (code) — ↩ undo in Activity / Email projects",
        ["nova.health"] = "runs the 🧠 Brain health check (every contradiction, vague rules, untested) — read only",
        ["fix.brain"] = "applies 🧠 Brain changes directly (summary, changes [op add|change|remove, section, id, title, text]) — only while you allow Claude; a version is saved first (↩ undo in Brain → Versions & backups); a broken [setting] is refused",
        ["nova.transfers"] = "📦 the Nova transfer queue — the last jobs (speed, threads, buffered / VPN mode, files done and failed) and the end of the newest log — read only",
        ["nova.vpn"] = "🧪 the 🌐 Office VPN test (FortiClient, gateway, adapter, office server, FortiClient window, recent VPN log) — read only, never connects, never shows the password",
        ["fix.status"] = "Claude's connection: allowed until when",
    };

    static readonly Dictionary<int, (string Entry, string Store)> handles = new();
    static int nextHandle = 1;
    static int H(string entry, string store) { lock (handles) { var f = handles.FirstOrDefault(x => x.Value.Entry == entry); if (f.Key > 0) return f.Key; handles[nextHandle] = (entry, store); return nextHandle++; } }
    static (string Entry, string Store) Of(JsonObject a) { int.TryParse(a["id"]?.ToString()?.TrimStart('#'), out var id); lock (handles) return handles.TryGetValue(id, out var v) ? v : throw new Exception("unknown id — search first (ids last for this Nova session)"); }

    static bool Private(string s) => Projects.Private.IsMatch(s ?? "") || (s ?? "").Contains("TrustWalletBackup", StringComparison.OrdinalIgnoreCase);
    static string Q(string s) => (s ?? "").Replace("'", "''");
    static string D(string s) => DateTime.TryParse(s, out var d) ? d.ToString("g") : null;

    public static async Task<string> Run(string tool, JsonObject a)
    {
        if (!ClaudeAccess.On) throw new Exception("🔗 Claude's Outlook access is off — on the PC: tray → 🔗 Allow Claude (2 h / 8 h / today), or My PC → ⚙ on the phone");
        using var act = Activity.Begin("🔗 Claude · " + tool, a.ToJsonString());
        var r = tool switch
        {
            "ol.search" => await Task.Run(() => Search(a, false)),
            "ol.compare" => await Task.Run(() => Search(a, true)),
            "ol.read" => await Task.Run(() => Read(a)),
            "ol.thread" => await Task.Run(() => Thread(a)),
            "ol.folders" => await Task.Run(Folders),
            "nova.followup" => await Followup(a),
            "fix.reindex" => await Task.Run(() => Reindex(a)),
            "fix.project" => await Task.Run(() => Project(a)),
            "fix.brain" => OnUi(() => FixBrain(a)),
            "nova.health" => await Health(),
            "nova.vpn" => await Task.Run(OfficeVpn.Diagnose),
            "nova.transfers" => Transfer.Report(),
            "fix.status" => $"{ClaudeAccess.Line} · Nova {Program.Shown}",
            _ => throw new Exception("unknown tool " + tool + " · tools: " + string.Join(", ", Help.Keys))
        };
        act.Result = "ok";
        return r;
    }

    /// <summary>🧠 You asked Claude to apply Brain changes itself (while its access is on): the change is checked for broken [settings],
    /// saved as a new version (↩ undo in Versions & backups), shown in the trainer and as a tray note, and logged in Activity.</summary>
    /// <summary>🩺 The Brain health check, as Claude reads it: every contradiction and every rule note.</summary>
    static async Task<string> Health()
    {
        if (!Brain.Unlocked) throw new Exception("the 🧠 Nova Brain is locked on the PC — open it first");
        var h = await BrainTrainer.Health(_ => { }, CancellationToken.None);
        var sb = new StringBuilder($"🩺 Brain version {Brain.Version} · strong {h["strong"]} · weak {h["weak"]} · to fix {h["bad"]} · untested {h["untested"]}\n");
        foreach (var c in (h["contradictions"] as JsonArray ?? new()).OfType<JsonObject>())
            sb.AppendLine($"⛔ {string.Join(" ↔ ", (c["ids"] as JsonArray)?.Select(x => x?.ToString()) ?? Array.Empty<string>())}: {c["why"]} → {c["fix"]}");
        foreach (var r in (h["rows"] as JsonArray ?? new()).OfType<JsonObject>())
            if ((r["notes"] as JsonArray)?.Count > 0) sb.AppendLine($"· {r["id"]}: {string.Join(" · ", (r["notes"] as JsonArray)!.Select(x => x?.ToString()))}");
        return sb.ToString();
    }

    static string OnUi(Func<string> f)
    {
        if (Program.UiContext == null) return f();
        string r = null; Exception err = null;
        Program.UiContext.Send(_ => { try { r = f(); } catch (Exception e) { err = e; } }, null);
        if (err != null) throw err;
        return r;
    }

    static string FixBrain(JsonObject a)
    {
        if (!Brain.Unlocked) throw new Exception("the 🧠 Nova Brain is locked on the PC — open it first");
        var ch = (a["changes"] as JsonArray)?.DeepClone().AsArray() ?? throw new Exception("changes is missing");
        var summary = a["summary"]?.ToString() ?? "";
        var before = Brain.Snapshot();
        var (after, said) = Brain.Apply(before, ch, "Claude · connector");
        var diff = Brain.Diff(before, after);
        if (diff.Count == 0) return "No rule would change.";
        var chk = BrainTrainer.Checks(before, diff);
        var syntax = (chk["syntax"] as JsonArray ?? new()).Select(x => x?.ToString()).ToList();
        if (syntax.Count > 0) throw new Exception("not applied — " + string.Join("; ", syntax));
        var v = Brain.Commit(after, "Claude · connector (applied directly while you allowed Claude)", (summary.Length > 0 ? summary + " · " : "") + said);
        BrainTrainer.Chat.Add(new JsonObject { ["role"] = "trainer", ["text"] = $"🔗 Claude applied version {v}: {said} — ↩ undo in Versions & backups" });
        Program.Balloon("🧠 Nova Brain · version " + v, "Claude applied: " + Agent.Clip(summary.Length > 0 ? summary : said, 150) + " (↩ undo in Versions & backups)");
        try { Screens.BrainNudge(); } catch { }
        var notes = (chk["duplicates"] as JsonArray ?? new()).Select(x => x?["why"]?.ToString()).Concat((chk["vague"] as JsonArray ?? new()).Select(x => x?.ToString())).Where(x => !string.IsNullOrEmpty(x)).ToList();
        return $"✓ Applied as version {v}: {said}" + (notes.Count > 0 ? "\nNotes: " + string.Join("; ", notes) : "");
    }

    /// <summary>📨 The agent's follow-up tool exactly as Nova runs it (to test it against Outlook) — 🔒 private lines left out.</summary>
    static async Task<string> Followup(JsonObject a)
    {
        var t = await Agent.Tool("followup", a, new Agent.Session(), _ => { }, CancellationToken.None);
        return string.Join("\n", t.Split('\n').Where(l => !Private(l)));
    }

    /// <summary>🔗 Nova's own live check for Find related: the emails in Outlook now whose subject has this number (entry ids).</summary>
    public static List<(string Entry, string StoreId, string Subject)> LiveSubject(string key, int seconds = 30)
    {
        var res = new List<(string, string, string)>();
        dynamic app = Outlook.Running();
        if (app == null || string.IsNullOrWhiteSpace(key)) return res;
        dynamic ns = app.GetNamespace("MAPI");
        var filter = $"@SQL=\"urn:schemas:httpmail:subject\" LIKE '%{Q(key.Trim())}%'";
        var started = DateTime.Now;
        foreach (var (f, _, _, sid) in MailFolders((object)ns, "", ""))
        {
            if ((DateTime.Now - started).TotalSeconds > seconds) break;
            try
            {
                dynamic items = f.Items.Restrict(filter);
                for (dynamic it = items.GetFirst(); it != null; it = items.GetNext())
                {
                    try { if ((int)it.Class == 43) res.Add((Outlook.S(() => it.EntryID), sid, Outlook.S(() => it.Subject))); }
                    catch { }
                    finally { Outlook.Release(it); }
                }
                Outlook.Release(items);
            }
            catch { }
        }
        return res;
    }

    /// <summary>📨 Nova's own live check (no Claude access needed): every email these people sent since a date, read in Outlook now —
    /// subject, date and where it is (🔒 private ones left out). Used to re-check “no reply” before Nova shows it.</summary>
    public static List<(DateTime Date, string Subject, string Entry, string StoreId, string From)> LiveFrom(List<string> who, DateTime after, int seconds = 60)
    {
        var res = new List<(DateTime, string, string, string, string)>();
        dynamic app = Outlook.Running();
        if (app == null || who.Count == 0) return res;
        dynamic ns = app.GetNamespace("MAPI");
        var ors = who.Where(w => w.Trim().Length > 2).Select(w => $"(\"urn:schemas:httpmail:fromname\" LIKE '%{Q(w.Trim())}%' OR \"urn:schemas:httpmail:fromemail\" LIKE '%{Q(w.Trim())}%' OR \"http://schemas.microsoft.com/mapi/proptag/0x0C1F001F\" LIKE '%{Q(w.Trim())}%')").ToList();
        var filter = $"@SQL=({string.Join(" OR ", ors)}) AND \"urn:schemas:httpmail:datereceived\" >= '{after:g}'";
        var started = DateTime.Now;
        foreach (var (f, _, _, sid) in MailFolders((object)ns, "", ""))
        {
            if ((DateTime.Now - started).TotalSeconds > seconds) break;
            try
            {
                dynamic items = f.Items.Restrict(filter);
                for (dynamic it = items.GetFirst(); it != null; it = items.GetNext())
                {
                    try
                    {
                        if ((int)it.Class != 43) continue;
                        string subj = Outlook.S(() => it.Subject);
                        if (Private(subj)) continue;
                        res.Add(((DateTime)it.ReceivedTime, subj, Outlook.S(() => it.EntryID), sid, Outlook.S(() => it.SenderName)));
                    }
                    catch { }
                    finally { Outlook.Release(it); }
                }
                Outlook.Release(items);
            }
            catch { }
        }
        return res;
    }

    /// <summary>Every mail folder of every store (not public folders, not Deleted / Junk / Sync Issues).</summary>
    static List<(dynamic Folder, string Path, string Store, string StoreId)> MailFolders(dynamic ns, string storeLike, string folderLike)
    {
        var res = new List<(dynamic, string, string, string)>();
        dynamic stores = ns.Stores;
        for (int i = 1; i <= (int)stores.Count; i++)
        {
            dynamic st = stores.Item(i);
            int type = -1; try { type = (int)st.ExchangeStoreType; } catch { }
            string sn = Outlook.S(() => st.DisplayName), sid = Outlook.S(() => st.StoreID);
            if (type == 2 || (storeLike.Length > 0 && !sn.Contains(storeLike, StringComparison.OrdinalIgnoreCase))) continue;
            void Walk(dynamic f, int depth, string path)
            {
                if (depth > 7) return;
                string fn = Outlook.S(() => f.Name);
                if (depth > 0 && Regex.IsMatch(fn, "^(Deleted Items|Junk Email|Sync Issues|Conversation History|Outbox|Drafts|RSS)", RegexOptions.IgnoreCase)) return;
                var here = depth == 0 ? "" : (path.Length > 0 ? path + " › " : "") + fn;
                int t = -1; try { t = (int)f.DefaultItemType; } catch { }
                if (t == 0 && depth > 0 && (folderLike.Length == 0 || here.Contains(folderLike, StringComparison.OrdinalIgnoreCase)) && !Private(here)) res.Add((f, sn + " › " + here, sn, sid));
                try { dynamic subs = f.Folders; for (int j = 1; j <= (int)subs.Count; j++) Walk(subs.Item(j), depth + 1, here); } catch { }
            }
            try { Walk(st.GetRootFolder(), 0, ""); } catch { }
        }
        return res;
    }

    static string Filter(JsonObject a)
    {
        var p = new List<string>();
        foreach (var w in (a["words"]?.ToString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).Take(6))
            p.Add($"(\"urn:schemas:httpmail:subject\" LIKE '%{Q(w)}%' OR \"urn:schemas:httpmail:textdescription\" LIKE '%{Q(w)}%')");
        if (a["subject"]?.ToString() is { Length: > 0 } s) p.Add($"\"urn:schemas:httpmail:subject\" LIKE '%{Q(s)}%'");
        if (a["from"]?.ToString() is { Length: > 0 } f) p.Add($"(\"urn:schemas:httpmail:fromname\" LIKE '%{Q(f)}%' OR \"urn:schemas:httpmail:fromemail\" LIKE '%{Q(f)}%' OR \"http://schemas.microsoft.com/mapi/proptag/0x0C1F001F\" LIKE '%{Q(f)}%')");
        if (a["to"]?.ToString() is { Length: > 0 } t) p.Add($"(\"urn:schemas:httpmail:displayto\" LIKE '%{Q(t)}%' OR \"http://schemas.microsoft.com/mapi/proptag/0x0E04001F\" LIKE '%{Q(t)}%')");
        if (a["cc"]?.ToString() is { Length: > 0 } cc) p.Add($"\"urn:schemas:httpmail:displaycc\" LIKE '%{Q(cc)}%'");
        if (D(a["after"]?.ToString()) is { } af) p.Add($"\"urn:schemas:httpmail:datereceived\" >= '{af}'");
        if (D(a["before"]?.ToString()) is { } bf) p.Add($"\"urn:schemas:httpmail:datereceived\" < '{bf}'");
        if (p.Count == 0) throw new Exception("give at least one of: words, subject, from, to, cc, after");
        return "@SQL=" + string.Join(" AND ", p);
    }

    static string Search(JsonObject a, bool compare)
    {
        dynamic app = Outlook.Running() ?? throw new Exception("Outlook is not running on the PC");
        dynamic ns = app.GetNamespace("MAPI");
        int limit = Math.Clamp(int.TryParse(a["limit"]?.ToString(), out var l) ? l : 60, 1, 150);
        var filter = Filter(a);
        var hits = new List<(DateTime D, string Line, string Entry)>();
        int folders = 0, scanned = 0;
        var started = DateTime.Now;
        foreach (var (f, path, sn, sid) in MailFolders((object)ns, a["store"]?.ToString() ?? "", a["folder"]?.ToString() ?? ""))
        {
            if ((DateTime.Now - started).TotalSeconds > 90) break;
            folders++;
            try
            {
                dynamic items = f.Items.Restrict(filter);
                int n = (int)items.Count; scanned += n;
                if (n == 0) continue;
                items.Sort("[ReceivedTime]", true);
                int k = 0;
                for (dynamic it = items.GetFirst(); it != null && k < limit; it = items.GetNext(), k++)
                {
                    try
                    {
                        if ((int)it.Class != 43) continue;
                        string subj = Outlook.S(() => it.Subject), entry = Outlook.S(() => it.EntryID);
                        if (Private(subj)) continue;
                        DateTime d = it.ReceivedTime;
                        string from = Outlook.S(() => it.SenderName), fa = "";
                        try { fa = Outlook.S(() => it.SenderEmailAddress); if (!fa.Contains('@')) { dynamic s = it.Sender; dynamic u = s?.GetExchangeUser(); if (u != null) fa = Outlook.S(() => u.PrimarySmtpAddress); } } catch { }
                        string atts = ""; try { var al = new List<string>(); dynamic aa = it.Attachments; for (int x = 1; x <= Math.Min(8, (int)aa.Count); x++) al.Add(Outlook.S(() => aa.Item(x).FileName)); atts = string.Join("; ", al); } catch { }
                        string body = ""; try { body = Regex.Replace(Outlook.S(() => it.Body), @"\s+", " "); } catch { }
                        hits.Add((d, $"#{H(entry, sid)} · {d:yyyy-MM-dd HH:mm} · {path}\n  From: {from} <{fa}>\n  To: {Outlook.S(() => it.To)}\n  Cc: {Outlook.S(() => it.CC)}\n  Subject: {subj}" +
                                       (atts.Length > 0 ? $"\n  Files: {atts}" : "") + $"\n  {(body.Length > 300 ? body[..300] + "…" : body)}", entry));
                    }
                    catch { }
                }
            }
            catch (Exception e) { hits.Add((DateTime.MinValue, $"⚠ {path}: {e.Message}", "")); }
        }
        var top = hits.OrderByDescending(h => h.D).Take(limit).ToList();
        var sb = new StringBuilder($"Outlook live · {folders} folders · {scanned} match(es) · showing {top.Count} newest · filter {filter}\n\n");
        if (compare)
        {
            var known = MailIndex.KnownEntries(top.Where(h => h.Entry.Length > 0).Select(h => h.Entry).ToList());
            int missing = top.Count(h => h.Entry.Length > 0 && !known.Contains(h.Entry));
            sb.Insert(0, $"⚖ {missing} of {top.Count} NOT in Nova's index\n");
            foreach (var h in top.OrderBy(h => h.Entry.Length > 0 && known.Contains(h.Entry))) sb.Append(h.Entry.Length > 0 && !known.Contains(h.Entry) ? "❌ NOT IN NOVA · " : "✓ in Nova · ").Append(h.Line).Append("\n\n");
        }
        else foreach (var h in top) sb.Append(h.Line).Append("\n\n");
        return sb.ToString();
    }

    static string Read(JsonObject a)
    {
        var (entry, store) = Of(a);
        dynamic app = Outlook.Running() ?? throw new Exception("Outlook is not running on the PC");
        dynamic ns = app.GetNamespace("MAPI");
        dynamic it = Outlook.Item(ns, entry, store);
        string subj = Outlook.S(() => it.Subject);
        if (Private(subj)) throw new Exception("🔒 private — not sent");
        string body = Outlook.S(() => it.Body);
        if (Private(body)) body = "(🔒 the text has private words — not sent)";
        string atts = ""; try { var al = new List<string>(); dynamic aa = it.Attachments; for (int x = 1; x <= (int)aa.Count; x++) al.Add(Outlook.S(() => aa.Item(x).FileName)); atts = string.Join("; ", al); } catch { }
        string folder = ""; try { folder = Outlook.S(() => it.Parent.FolderPath); } catch { }
        using var act = Activity.Begin("🔗 Claude read", subj);
        return $"From: {Outlook.S(() => it.SenderName)} <{Outlook.S(() => it.SenderEmailAddress)}>\nTo: {Outlook.S(() => it.To)}\nCc: {Outlook.S(() => it.CC)}\nDate: {(DateTime)it.ReceivedTime:yyyy-MM-dd HH:mm}\nFolder: {folder}\nSubject: {subj}\nFiles: {atts}\nIn Nova's index: {(MailIndex.KnownEntries(new() { entry }).Count > 0 ? "yes" : "NO")}\n\n{(body.Length > 20000 ? body[..20000] + "\n…(cut)" : body)}";
    }

    static string Thread(JsonObject a)
    {
        var (entry, store) = Of(a);
        dynamic app = Outlook.Running() ?? throw new Exception("Outlook is not running on the PC");
        dynamic ns = app.GetNamespace("MAPI");
        dynamic it = Outlook.Item(ns, entry, store);
        string topic = Outlook.S(() => it.ConversationTopic);
        var a2 = new JsonObject { ["limit"] = 80 };
        var filter = $"@SQL=\"urn:schemas:httpmail:thread-topic\" = '{Q(topic)}'";
        var lines = new List<(DateTime, string)>();
        foreach (var (f, path, sn, sid) in MailFolders((object)ns, "", ""))
            try
            {
                dynamic items = f.Items.Restrict(filter);
                for (dynamic x = items.GetFirst(); x != null; x = items.GetNext())
                    try { if ((int)x.Class == 43) { DateTime d = x.ReceivedTime; lines.Add((d, $"#{H(Outlook.S(() => x.EntryID), sid)} · {d:yyyy-MM-dd HH:mm} · {Outlook.S(() => x.SenderName)} <{Outlook.S(() => x.SenderEmailAddress)}> → {Outlook.S(() => x.To)}{(Outlook.S(() => x.CC) is { Length: > 0 } cc ? " · cc " + cc : "")} · {Outlook.S(() => x.Subject)} · {path}")); } } catch { }
            }
            catch { }
        return $"Conversation “{topic}” · {lines.Count} email(s)\n" + string.Join("\n", lines.OrderBy(l => l.Item1).Select(l => l.Item2));
    }

    static string Folders()
    {
        var sb = new StringBuilder();
        foreach (var p in MailIndex.StoreProbe())
        {
            sb.AppendLine($"## {p.Store} · {p.Kind} · {p.Items:N0} in Outlook · {p.Indexed:N0} in Nova · newest {p.Newest}{(p.File.Length > 0 ? " · " + p.File : "")}");
            if (p.Problem.Length > 0) sb.AppendLine("   " + p.Problem);
            foreach (var l in p.Lines) sb.AppendLine("   - " + l);
        }
        return sb.ToString();
    }

    static string Reindex(JsonObject a)
    {
        string like = a["folder"]?.ToString() ?? "";
        if (like.Length == 0 && a["id"] != null)
        {
            var (entry, store) = Of(a);
            dynamic app = Outlook.Running(); dynamic ns = app.GetNamespace("MAPI");
            dynamic it = Outlook.Item(ns, entry, store);
            like = Outlook.S(() => it.Parent.Name);
        }
        if (like.Length < 2) throw new Exception("give folder (part of the path) or id");
        var n = MailIndex.ForgetWatermarks(like);
        MailIndex.Now();
        using var act = Activity.Begin("🔗 Claude fixed", $"re-read “{like}” completely ({n} folder(s)) — fills the gaps; nothing is deleted");
        return $"✓ Nova re-reads {n} folder(s) matching “{like}” completely (only missing emails are added). It runs now at full speed.";
    }

    static string Project(JsonObject a)
    {
        var code = Projects.Canon(a["project"]?.ToString() ?? "");
        if (code.Length == 0) throw new Exception("unknown project " + a["project"]);
        var ids = (a["ids"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString()?.TrimStart('#') ?? "").ToList();
        var entries = ids.Select(i => { lock (handles) return int.TryParse(i, out var k) && handles.TryGetValue(k, out var v) ? v.Entry : ""; }).Where(e => e.Length > 0).ToList();
        var hits = MailIndex.ByEntries(entries);
        if (hits.Count == 0) throw new Exception("these emails are not in Nova's index yet — fix.reindex their folder first");
        MailProjects.Move(hits, code);
        using var act = Activity.Begin("🔗 Claude fixed", $"{hits.Count} email(s) → project {code} (↩ undo in Settings → Email projects)");
        return $"✓ {hits.Count} email(s) put into {code} — ↩ undo in Settings → Email & AI → Email projects.";
    }
}
