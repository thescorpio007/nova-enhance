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
            case "init": Post(State()); break;
            case "create": Work("Making your brain…", () => { Brain.Create(S("pw")); Brain.BackupNow("manual"); return State(); }); break;
            case "unlock": Work("Opening…", () => { Brain.Unlock(S("pw")); return State(); }); break;
            case "pending": Work("Checking the brain from your backup…", () => new JsonObject { ["t"] = "toast", ["text"] = Brain.RestorePending(S("pw")) }); break;
            case "lock": Brain.Lock(); break;
            case "ask": Work("🧠 Reading your brain…", () => Ask(S("text"), m["history"] as JsonArray)); break;
            case "test": Work("🧪 Testing on your real emails…", () => Test(m["changes"] as JsonArray, m["days"]?.GetValue<int>() ?? 30)); break;
            case "apply":
            {
                var ch = m["changes"] as JsonArray ?? new JsonArray();
                var (b, said) = Brain.Apply(Brain.Snapshot(), ch, S("by").Length > 0 ? S("by") : "you · brain chat");
                var v = Brain.Commit(b, S("by").Length > 0 ? S("by") : "you · brain chat", (S("summary").Length > 0 ? S("summary") + " · " : "") + said);
                Post(new JsonObject { ["t"] = "toast", ["text"] = $"✓ Applied · version {v} saved (↩ undo in Versions)" });
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

    // ---------------------------------------------------------------- 💬 the brain chat

    static JsonObject Ask(string text, JsonArray history)
    {
        var b = Brain.Snapshot();
        var rules = new System.Text.StringBuilder();
        foreach (var sec in (b["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            rules.Append($"\n## {sec["icon"]} {sec["name"]} (section id: {sec["id"]}; used for: {string.Join(", ", (sec["tags"] as JsonArray)?.Select(t => t?.ToString()) ?? Array.Empty<string>())})\n");
            foreach (var r in (sec["rules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                rules.Append($"- {r["id"]} {(r["off"]?.GetValue<bool>() == true ? "(switched off) " : "")}{r["title"]}: {r["text"]}\n");
        }
        // “Why was WIR-15280 closed?” → what the Submittal log sees for that document.
        var facts = "";
        foreach (Match d in Regex.Matches(text, @"\b(?:[A-Z0-9]{2,6}-){0,5}(WIR|MIR|MAR|PQS|RFI|SDS)-\d{2,6}\b", RegexOptions.IgnoreCase))
            try { facts += SubmittalLog.Explain(d.Value) + "\n"; } catch { }
        var sys = "You are the Nova Brain assistant. Nova Brain is the user's own rule book that guides the app Nova Enhance (an assistant for an architect: Outlook emails, project files, " +
                  "submittal logs, deep search, writing). The user talks to you in their own words. Two kinds of message:\n" +
                  "1) A question (\"why did Nova…\", \"what do you do when…\"): answer from the rules (name the rule ids) and the facts given; propose no change unless asked.\n" +
                  "2) An instruction to remember, change, add or remove something: propose the smallest exact change to the rules.\n" +
                  "Rules are short, clear sentences. A rule may hold settings written as [name = value] that the app reads (e.g. [overdue.WIR = 2], [log.types = WIR, MIR], [mza.match = …], [closed.files = …]); keep that syntax exactly when you change a setting.\n" +
                  "Prefer changing an existing rule over adding a near-duplicate. If the new instruction contradicts an existing rule, say so in the reply and propose replacing it. Never invent facts about the user's projects.\n" +
                  "Reply with JSON only: {\"reply\":\"what you understood / your answer, short\",\"conflict\":\"\" or \"which rule it contradicts and why\",\"changes\":[{\"op\":\"add|change|remove\",\"section\":\"section id or a new section name\",\"id\":\"rule id for change/remove (empty for add)\",\"title\":\"2-4 words\",\"text\":\"the full new rule text\"}]}\n\n" +
                  "THE BRAIN NOW:" + rules;
        var user = new System.Text.StringBuilder();
        foreach (var h in (history ?? new JsonArray()).OfType<JsonObject>().TakeLast(8)) user.Append(h["role"]).Append(": ").Append(h["text"]).Append('\n');
        if (facts.Length > 0) user.Append("FACTS FROM THE SUBMITTAL LOG:\n").Append(facts);
        user.Append("user: ").Append(text);
        Brain.Quiet.Value = true;
        string raw;
        try { raw = Ai.Chat(sys, user.ToString(), false, true).GetAwaiter().GetResult(); }
        finally { Brain.Quiet.Value = false; }
        var j = Ai.ParseJson(raw);
        var changes = j["changes"] as JsonArray ?? new JsonArray();
        var (after, _) = Brain.Apply(b, changes, "you · brain chat");
        var diff = Brain.Diff(b, after);
        bool testable = diff.OfType<JsonObject>().Any(d => Regex.IsMatch(d["section"]?.ToString() ?? "", "submittal|tower|zone|level", RegexOptions.IgnoreCase));
        return new JsonObject
        {
            ["t"] = "answer", ["reply"] = j["reply"]?.ToString() ?? raw, ["conflict"] = j["conflict"]?.ToString() ?? "",
            ["changes"] = changes.DeepClone(), ["diff"] = diff, ["testable"] = testable, ["by"] = Ai.LastBy
        };
    }

    /// <summary>🧪 The proposed rules run on your real emails next to the current ones: what changes, item by item.</summary>
    static JsonObject Test(JsonArray changes, int days)
    {
        var now = Brain.Snapshot();
        var (after, _) = Brain.Apply(now, changes ?? new JsonArray(), "test");
        var o = new SubmittalLog.Options { From = DateTime.Today.AddDays(-days) };
        var a = SubmittalLog.Run(o, now).Items.ToDictionary(i => i.No + "|" + i.Rev);
        var b = SubmittalLog.Run(o, after).Items;
        var rows = new JsonArray();
        int changed = 0, toClosed = 0, fromClosed = 0;
        foreach (var i in b)
        {
            a.TryGetValue(i.No + "|" + i.Rev, out var old);
            var s0 = old == null ? "—" : old.Status + (old.Code.Length > 0 ? " · " + old.Code : "");
            var s1 = i.Status + (i.Code.Length > 0 ? " · " + i.Code : "");
            if (s0 == s1) continue;
            changed++;
            if (i.Status == "Closed" && old?.Status != "Closed") toClosed++;
            if (old?.Status == "Closed" && i.Status != "Closed") fromClosed++;
            if (rows.Count < 40) rows.Add(new JsonObject { ["no"] = i.No + " " + i.Rev, ["now"] = s0, ["then"] = s1, ["why"] = i.Why });
        }
        return new JsonObject { ["t"] = "test", ["days"] = days, ["rows"] = rows, ["changed"] = changed, ["toClosed"] = toClosed, ["fromClosed"] = fromClosed, ["total"] = b.Count };
    }
}
