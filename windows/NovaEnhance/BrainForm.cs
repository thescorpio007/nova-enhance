using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>🧠 Nova Brain window: open/make with its own password, sections and rules, 💬 talk to the brain (it proposes the exact change,
/// tests it on your real emails, nothing changes without ✓ Apply), versions, backups and safe restore. Designed by A.HAMROUNI</summary>
class BrainForm : WebPage
{
    public BrainForm() : base("🧠 Nova Brain", "brain.html", 1500, 900)
    {
        Brain.Changed += OnChanged;
        FormClosed += (_, _) => Brain.Changed -= OnChanged;
    }

    void OnChanged() => Post(State());

    static JsonObject State() => new()
    {
        ["t"] = "state", ["exists"] = Brain.Exists, ["unlocked"] = Brain.Unlocked, ["pending"] = Brain.HasPending, ["version"] = Brain.Version,
        ["rules"] = Brain.RuleCount, ["lastBackup"] = Brain.LastBackupText, ["keepDaily"] = Brain.KeepDaily, ["keepMonthly"] = Brain.KeepMonthly,
        ["drive"] = Backup.DriveCopy, ["brain"] = Brain.Unlocked ? WithLog(Brain.Snapshot()) : null, ["sign"] = Program.Sign
    };

    /// <summary>✎ My log settings shown as a read-only section (edited in the 📋 log, never saved into the brain file).</summary>
    static JsonObject WithLog(JsonObject b)
    {
        try { var c = b.DeepClone().AsObject(); (c["sections"] as JsonArray)?.Add(LogPrefs.BrainSection()); return c; } catch { return b; }
    }

    protected override void OnMessage(JsonObject m)
    {
        string S(string k) => m[k]?.ToString() ?? "";
        switch (S("t"))
        {
            case "init":
                Post(State());
                Post(new JsonObject { ["t"] = "chatLoad", ["chat"] = BrainTrainer.Chat.DeepClone(), ["lessons"] = Lessons() });
                Flush();
                break;
            case "stop": cts?.Cancel(); break;
            case "open": NovaChat.OpenSource(new JsonObject { ["kind"] = "email", ["entry"] = S("entry"), ["store"] = S("store") }); break;
            case "chatClear": BrainTrainer.Chat.Clear(); break;
            case "health": Run("🩺 Health check of every rule", (st, de, ct) => BrainTrainer.Health(st, ct)); break;
            case "create": Work("Making your brain…", () => { Brain.Create(S("pw")); Brain.BackupNow("manual"); return State(); }); break;
            case "unlock": Work("Opening…", () => { Brain.Unlock(S("pw")); return State(); }); break;
            case "pending": Work("Checking the brain from your backup…", () => new JsonObject { ["t"] = "toast", ["text"] = Brain.RestorePending(S("pw")) }); break;
            case "lock": Brain.Lock(); break;
            case "ask":
            {
                var text = S("text");
                var files = m["files"] as JsonArray ?? new JsonArray();
                if (files.Count > 0) { AskWithFiles(text, files.DeepClone().AsArray()); break; }
                Run("🎓 The trainer is reading your brain", (st, de, ct) => BrainTrainer.Ask(text, st, de, ct));
                break;
            }
            // 📎 Example files for the trainer: pick · paste · drop · the email selected in Outlook
            case "pickFiles":
            {
                using var d = new OpenFileDialog { Multiselect = true, Title = "Examples for the Brain trainer (emails .msg / .eml, PDF, Word, Excel, text, pictures)" };
                if (d.ShowDialog(this) == DialogResult.OK) Post(Files(d.FileNames));
                break;
            }
            case "pasteFile":
                try { Post(Files(new[] { FileTalk.SaveBytes(S("name"), Convert.FromBase64String(S("b64"))) })); }
                catch (Exception e) { Post(new JsonObject { ["t"] = "toast", ["text"] = "⚠ Could not add the file: " + e.Message }); }
                break;
            case "pasteClipboard": { var got = FileTalk.FromClipboard(); if (got.Count > 0) Post(Files(got)); break; }
            case "outlookMail":
                try
                {
                    var h = MailIndex.CurrentMail();
                    if (h == null) { Post(new JsonObject { ["t"] = "toast", ["text"] = "Select an email in Outlook first" }); break; }
                    Post(Files(new[] { BrainTrainer.MailFile(h) }));
                }
                catch (Exception e) { Post(new JsonObject { ["t"] = "toast", ["text"] = "⚠ " + e.Message }); }
                break;
            case "test":
            {
                var ch = m["changes"] as JsonArray ?? new JsonArray(); var q = S("question"); var kind = S("kind"); var card = S("card");
                var mf = (m["mustFind"] as JsonArray)?.DeepClone().AsArray(); var mn = (m["mustNotFind"] as JsonArray)?.DeepClone().AsArray();
                Run("🧪 Testing on your real data (nothing is saved)", async (st, de, ct) => { var r = await BrainTrainer.Test(ch, q, kind, st, de, ct, mf, mn); r["card"] = card; return r; });
                break;
            }
            case "verdict": { var r = BrainTrainer.Verdict(S("id"), m["yes"]?.GetValue<int>() ?? 0, m["no"]?.GetValue<int>() ?? 0, m["secs"]?.GetValue<int>() ?? 0); r["card"] = S("card"); Post(r); break; }
            case "apply":
            {
                var ch = m["changes"] as JsonArray ?? new JsonArray();
                // 🔒 Only a proposal that passed its 🧪 TEST (exactly these changes) can be applied.
                var passed = BrainTrainer.Passed(S("test"), ch);
                if (passed == null) { Post(new JsonObject { ["t"] = "toast", ["text"] = "🧪 Test it first — ✓ Apply opens after a passed test (an edit needs a new test)" }); break; }
                var by = S("by").Length > 0 ? S("by") : "you · brain trainer · 🧪 tested";
                var before = Brain.Snapshot();
                var (b, said) = Brain.Apply(before, ch, by);
                BrainTrainer.MarkTested(b, Brain.Diff(before, b), ch, passed);
                var v = Brain.Commit(b, by, (S("summary").Length > 0 ? S("summary") + " · " : "") + said + $" · 🧪 passed ({passed.Yes} ✓, +{passed.Plus} / −{passed.Minus}, tested in {passed.Seconds / 60}:{passed.Seconds % 60:00}) “{Agent.Clip(passed.Question, 80)}”");
                BrainTrainer.Chat.Add(new JsonObject { ["role"] = "trainer", ["text"] = $"✓ Applied as version {v}: {said}" });
                Post(new JsonObject { ["t"] = "toast", ["text"] = $"✓ Applied · version {v} saved with its test (↩ undo in Versions)" });
                break;
            }
            case "saveSection":
            {
                var b = Brain.Snapshot();
                var sec = (b["sections"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.ToString() == S("id"));
                if (sec == null) return;
                var before = b.DeepClone().AsObject();
                if (S("name").Length > 0) sec["name"] = S("name");
                sec["rules"] = (m["rules"] as JsonArray)?.DeepClone() ?? new JsonArray();
                foreach (var r in (sec["rules"] as JsonArray)!.OfType<JsonObject>())
                {
                    if (string.IsNullOrWhiteSpace(r["id"]?.ToString())) r["id"] = "";
                    r["when"] ??= DateTimeOffset.Now.ToUnixTimeMilliseconds(); r["by"] ??= "you · edit";
                }
                // New lines get an id like the others.
                foreach (var r in (sec["rules"] as JsonArray)!.OfType<JsonObject>().Where(x => x["id"]!.ToString().Length == 0).ToList())
                {
                    (sec["rules"] as JsonArray)!.Remove(r);
                    var (b3, _) = Brain.Apply(b, new JsonArray(new JsonObject { ["op"] = "add", ["section"] = sec["id"]?.ToString(), ["title"] = r["title"]?.ToString(), ["text"] = r["text"]?.ToString() }), "you · edit");
                    b = b3; sec = (b["sections"] as JsonArray)!.OfType<JsonObject>().First(s => s["id"]?.ToString() == S("id"));
                }
                var d = Brain.Diff(before, b);
                if (d.Count == 0 && S("name").Length == 0) { Post(new JsonObject { ["t"] = "toast", ["text"] = "Nothing changed" }); return; }
                var v = Brain.Commit(b, "you · edit", $"✎ {sec["name"]}: {d.Count} rule(s) changed");
                Post(new JsonObject { ["t"] = "toast", ["text"] = $"✓ Saved · version {v}" });
                break;
            }
            case "newSection":
            {
                var b = Brain.Snapshot();
                var name = S("name").Trim(); if (name.Length == 0) return;
                var secs = b["sections"] as JsonArray ?? new JsonArray();
                var id = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "");
                if (id.Length == 0) id = "s" + DateTime.Now.Ticks % 100000;
                secs.Add(new JsonObject { ["id"] = id, ["icon"] = S("icon"), ["name"] = name, ["tags"] = new JsonArray("all"), ["rules"] = new JsonArray() });
                Brain.Commit(b, "you · edit", "＋ section " + name);
                break;
            }
            case "versions": Post(new JsonObject { ["t"] = "versions", ["list"] = Brain.Versions() }); break;
            case "viewVersion":
                Work("Comparing…", () =>
                {
                    var v = m["v"]!.GetValue<int>();
                    var after = Brain.LoadVersion(v);
                    JsonObject before;
                    try { before = Brain.LoadVersion(v - 1); } catch { before = new JsonObject { ["sections"] = new JsonArray() }; }
                    return new JsonObject { ["t"] = "diff", ["v"] = v, ["diff"] = Brain.Diff(before, after) };
                });
                break;
            case "backTo": Work("Going back…", () => { var v = Brain.BackTo(m["v"]!.GetValue<int>()); return new JsonObject { ["t"] = "toast", ["text"] = $"↩ Back · saved as version {v}" }; }); break;
            case "backups": Work("Checking every backup…", () => new JsonObject { ["t"] = "backups", ["list"] = Brain.Backups() }); break;
            case "backupNow": Work("💾 Backing up…", () => { var p = Brain.BackupNow("manual"); return new JsonObject { ["t"] = "toast", ["text"] = "💾 Saved and checked: " + Path.GetFileName(p), ["refresh"] = "backups" }; }); break;
            case "restoreBackup":
                Work("Checking before restoring…", () =>
                {
                    var f = Path.Combine(Brain.BackupsDir, Path.GetFileName(S("file")));
                    return new JsonObject { ["t"] = "toast", ["text"] = Brain.RestoreFile(File.ReadAllText(f), S("pw").Length > 0 ? S("pw") : CurrentPw(), "backup " + Path.GetFileName(f)), ["refresh"] = "backups" };
                });
                break;
            case "restoreFile":
            {
                using var d = new OpenFileDialog { Filter = "Nova Brain (*.nbrain)|*.nbrain", Title = "Restore the brain from a file" };
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var path = d.FileName;
                Work("Checking before restoring…", () => new JsonObject { ["t"] = "toast", ["text"] = Brain.RestoreFile(File.ReadAllText(path), S("pw").Length > 0 ? S("pw") : CurrentPw(), Path.GetFileName(path)) });
                break;
            }
            case "changePw": Work("Locking again with the new password…", () => { Brain.ChangePassword(S("old"), S("new")); return new JsonObject { ["t"] = "toast", ["text"] = "🔑 New password set (older backups keep the old one)" }; }); break;
            case "keep": Brain.KeepDaily = m["daily"]?.GetValue<int>() ?? 30; Brain.KeepMonthly = m["monthly"]?.GetValue<int>() ?? 12; Post(State()); break;
            case "drive": Backup.DriveCopy = m["on"]?.GetValue<bool>() ?? false; Post(State()); break;
            case "folder": try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Brain.BackupsDir) { UseShellExecute = true }); } catch { } break;
        }
    }

    static string CurrentPw() => Store.Secret("win_brain_pw");

    // ---------------------------------------------------------------- 🎓 trainer / 🧪 test / 🩺 health: long work with ● ● ● steps

    CancellationTokenSource cts;

    void Run(string title, Func<Action<string>, Action<string>, CancellationToken, Task<JsonObject>> f)
    {
        cts?.Cancel(); cts = new CancellationTokenSource();
        var ct = cts.Token;
        Post(new JsonObject { ["t"] = "work", ["title"] = title });
        _ = Task.Run(async () =>
        {
            using var busy = Busy.Begin(title);
            try
            {
                var r = await f(s => Post(new JsonObject { ["t"] = "wstep", ["text"] = s, ["step"] = true }), s => Post(new JsonObject { ["t"] = "wstep", ["text"] = s }), ct);
                Post(new JsonObject { ["t"] = "wdone" });
                Post(r);
            }
            catch (OperationCanceledException) { Post(new JsonObject { ["t"] = "wfail", ["text"] = "Stopped." }); }
            catch (Exception e) { Log.Error("Nova Brain", e); Post(new JsonObject { ["t"] = "wfail", ["text"] = e.Message }); }
        });
    }

    /// <summary>Proposals from the connector, and “🎓 Teach the Brain” from the phone (emails as examples).</summary>
    public void Flush()
    {
        if (!Ready) return;
        lock (BrainTrainer.FromConnector) { foreach (var p in BrainTrainer.FromConnector) Post(p); BrainTrainer.FromConnector.Clear(); }
        lock (BrainTrainer.FromPhone) { foreach (var p in BrainTrainer.FromPhone) Post(p); BrainTrainer.FromPhone.Clear(); }
    }

    static JsonObject Files(IEnumerable<string> paths) => new()
    {
        ["t"] = "bfiles", ["files"] = new JsonArray(paths.Select(p => (JsonNode)new JsonObject
        {
            ["path"] = p, ["name"] = Path.GetFileName(p), ["private"] = Projects.Private.IsMatch(Path.GetFileName(p))
        }).ToArray())
    };

    /// <summary>📎 Reads each example (on this window, so pictures ask your consent first), then the trainer learns from them.</summary>
    async void AskWithFiles(string text, JsonArray files)
    {
        cts?.Cancel(); cts = new CancellationTokenSource();
        var ct = cts.Token;
        Post(new JsonObject { ["t"] = "work", ["title"] = "🎓 The trainer is reading your examples" });
        using var busy = Busy.Begin("🎓 Brain trainer · examples");
        try
        {
            var ex = new List<(string, bool, string)>();
            foreach (var f in files.OfType<JsonObject>())
            {
                var path = f["path"]?.ToString() ?? ""; var name = f["name"]?.ToString() ?? Path.GetFileName(path); var good = f["good"]?.GetValue<bool>() != false;
                Post(new JsonObject { ["t"] = "wstep", ["text"] = $"📄 Reading {(good ? "✓" : "✗")} {name}", ["step"] = true });
                // 🔒 private words: the file is never read nor sent to the AI
                if (Projects.Private.IsMatch(name)) { ex.Add((name, good, "(🔒 not read — private words in the name)")); continue; }
                string body;
                try { body = await FileTalk.Text(path, this, ct, s => Post(new JsonObject { ["t"] = "wstep", ["text"] = s }), text); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { body = "(could not read: " + e.Message + ")"; }
                if (Projects.Private.IsMatch(body)) { ex.Add((name, good, "(🔒 not sent to the AI — private words inside)")); continue; }
                ex.Add((name, good, body));
            }
            var r = await Task.Run(() => BrainTrainer.Ask(text, s => Post(new JsonObject { ["t"] = "wstep", ["text"] = s, ["step"] = true }), s => Post(new JsonObject { ["t"] = "wstep", ["text"] = s }), ct, ex), ct);
            Post(new JsonObject { ["t"] = "wdone" });
            Post(r);
        }
        catch (OperationCanceledException) { Post(new JsonObject { ["t"] = "wfail", ["text"] = "Stopped." }); }
        catch (Exception e) { Log.Error("Nova Brain · examples", e); Post(new JsonObject { ["t"] = "wfail", ["text"] = e.Message }); }
    }

    static JsonArray Lessons() => new(Agent.Feedback.Patterns().Select(p => (JsonNode)new JsonObject { ["what"] = p.What, ["count"] = p.Count, ["teach"] = p.Teach }).ToArray());
}
