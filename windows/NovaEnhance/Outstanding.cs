using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// "Waiting for my reply" done carefully. An email is outstanding only when Nova finds no answer from you:
///   sure answers (hidden): a later email you sent in the same conversation (index) · Outlook's own "You replied" mark (also set by phone / Outlook web)
///   probable answers (grey section "Probably answered"): an email you sent later with the same subject, or quoting the same reference number ·
///     you forwarded it · a colleague already replied to all while you were only in Cc
///   handled by you (hidden): ✓ Mark as handled
/// One card per conversation or topic: the latest email, with the number of earlier ones folded into it. Designed by A.HAMROUNI
/// </summary>
static class Outstanding
{
    // ================================================================ ✓ Mark as handled

    static HashSet<string> handled;

    static HashSet<string> Handled()
    {
        if (handled != null) return handled;
        handled = new HashSet<string>();
        try { foreach (var x in JsonNode.Parse(Store.Str("win_handled", "[]"))?.AsArray() ?? new JsonArray()) if (x?["e"]?.ToString() is { Length: > 0 } e) handled.Add(e); } catch { }
        return handled;
    }

    public static bool IsHandled(MailHit h) => Handled().Contains(h.Entry ?? "");

    public static void MarkHandled(MailHit h, bool on)
    {
        JsonArray a;
        try { a = JsonNode.Parse(Store.Str("win_handled", "[]"))?.AsArray() ?? new JsonArray(); } catch { a = new JsonArray(); }
        var keep = a.OfType<JsonObject>().Where(o => o["e"]?.ToString() != h.Entry).Select(o => (JsonNode)o.DeepClone()).ToList();
        if (on) keep.Add(new JsonObject { ["e"] = h.Entry, ["s"] = h.Subject, ["d"] = DateTime.Now.ToString("yyyy-MM-dd") });
        Store.Set("win_handled", new JsonArray(keep.TakeLast(3000).ToArray()).ToJsonString());
        handled = null;
    }

    // ================================================================ matching helpers

    /// <summary>Subject without RE:/FW:/AW:/TR:/RV:/SV:, [EXT]/[External], "#", extra spaces — so a reply under a new email still matches.</summary>
    public static string NormSubject(string s)
    {
        s ??= "";
        string prev;
        do
        {
            prev = s;
            s = Regex.Replace(s, @"^\s*(\[[^\]]{1,20}\]|\((ext|external)\)|(re|fw|fwd|aw|wg|tr|rv|sv|vs|antw|رد|إعادة توجيه)\s*(\[\d+\])?\s*[:：])\s*", "", RegexOptions.IgnoreCase);
        } while (s != prev);
        s = Regex.Replace(s, @"[#""'“”‘’‏‎]", " ");
        return Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
    }

    /// <summary>Reference numbers like SLV-ZUC-AR-RFI-00138, AUH-RS5-C7-MZA-1.1B-MM-00136, RFI-044, IPC 11 → used to find an answer in another email.</summary>
    public static List<string> Refs(string s)
    {
        var res = new List<string>();
        foreach (Match m in Regex.Matches(s ?? "", @"\b[A-Z][A-Z0-9]{1,9}(?:[-_./][A-Z0-9.]{1,12}){1,9}\b"))
        {
            var v = m.Value.Trim('.', '-', '_');
            if (v.Length >= 7 && Regex.IsMatch(v, @"\d{2,}") && Regex.IsMatch(v, @"[A-Z]{2,}")) res.Add(v);
        }
        return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool SameSubject(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return a.Length >= 6;
        return a.Length >= 18 && b.Length >= 18 && (a.Contains(b) || b.Contains(a));
    }

    static bool Mentions(string list, string addr) => addr.Length > 3 && (list ?? "").Contains(addr, StringComparison.OrdinalIgnoreCase);

    static bool MeInTo(MailHit h) =>
        Me.Addrs.Any(a => Mentions(h.ToAddr, a)) || (Me.Name.Length > 0 && (h.To ?? "").Contains(Me.Name, StringComparison.OrdinalIgnoreCase));

    // ================================================================ the check

    /// <summary>
    /// Takes the emails the index found as "no reply from me in the same conversation" and returns them ready to show:
    /// sure answers removed, probable answers marked (MaybeAnswered), one card per topic.
    /// </summary>
    public static List<MailHit> Check(List<MailHit> hits)
    {
        if (hits.Count == 0) return hits;
        hits = hits.Where(h => !IsHandled(h)).ToList();

        // 1 · One card per conversation / topic: the latest email; earlier ones fold into it.
        string Key(MailHit h)
        {
            var n = NormSubject(h.Subject);
            return n.Length >= 15 ? "s:" + n : "c:" + (h.Conv.Length > 0 ? h.Conv : n + "|" + h.FromAddr);
        }
        var groups = hits.GroupBy(Key).ToList();
        var kept = new List<MailHit>();
        foreach (var g in groups)
        {
            var ordered = g.OrderByDescending(h => h.Date).ToList();
            var top = ordered[0];
            top.Earlier = ordered.Count - 1;
            // A colleague answered the asker (reply-all) while you were only in Cc.
            if (top.Earlier > 0 && !MeInTo(top))
            {
                var askers = ordered.Skip(1).Select(x => x.FromAddr ?? "").Where(a => a.Length > 3 && !a.Equals(top.FromAddr, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
                if (askers.Any(a => Mentions(top.ToAddr, a)))
                    top.MaybeAnswered = $"{First(top.FromName)} replied to all on {top.Date:dd MMM} — you're in Cc";
            }
            kept.Add(top);
        }

        // 2 · Outlook's own "You replied / forwarded" mark (set even when you answered from your phone or Outlook web).
        try
        {
            object app = Outlook.Running();
            if (app != null)
            {
                dynamic ns = ((dynamic)app).GetNamespace("MAPI");
                foreach (var h in kept.OrderByDescending(x => x.Date).Take(150).ToList())
                {
                    (int verb, DateTime? when) = Outlook.LastVerb((object)ns, h.Entry, h.StoreId);
                    if (when != null && when < h.Date.AddMinutes(-1)) continue;
                    if (verb is 102 or 103) { kept.Remove(h); continue; }
                    if (verb == 104 && h.MaybeAnswered.Length == 0) h.MaybeAnswered = $"you forwarded it{(when != null ? " on " + when.Value.ToString("dd MMM") : "")}";
                }
                Outlook.Release((object)ns);
            }
        }
        catch (Exception e) when (!Outlook.Gone(e)) { Log.Warn("outstanding", "Outlook replied-mark check skipped: " + e.Message); }
        catch { }

        // 3 · An email you sent later with the same subject, or quoting the same reference — probably your answer.
        try
        {
            var since = kept.Count == 0 ? DateTime.Now : kept.Min(h => h.Date);
            var sent = MailIndex.SentAfter(since);
            foreach (var s in sent) s.Summary = NormSubject(s.Subject);   // reuse a spare field for the cleaned subject
            foreach (var h in kept.Where(x => x.MaybeAnswered.Length == 0))
            {
                var ns = NormSubject(h.Subject);
                var refs = Refs(h.Subject);
                var later = sent.Where(s => s.Date > h.Date).ToList();
                var bySubject = later.Where(s => SameSubject(ns, s.Summary)).OrderBy(s => s.Date).FirstOrDefault();
                if (bySubject != null)
                {
                    bool samePeople = Mentions(bySubject.ToAddr, h.FromAddr) || Mentions(bySubject.CcAddr, h.FromAddr);
                    h.MaybeAnswered = $"you sent “{Cut(bySubject.Subject, 70)}” on {bySubject.Date:dd MMM}" + (samePeople ? $" to {First(h.FromName)}" : "");
                    continue;
                }
                foreach (var rf in refs)
                {
                    var byRef = later.Where(s => s.Subject.Contains(rf, StringComparison.OrdinalIgnoreCase) || s.Body.Contains(rf, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Date).FirstOrDefault();
                    if (byRef != null) { h.MaybeAnswered = $"you mentioned {rf} in “{Cut(byRef.Subject, 60)}” on {byRef.Date:dd MMM}"; break; }
                }
            }
            foreach (var s in sent) s.Summary = "";
        }
        catch (Exception e) { Log.Warn("outstanding", "sent-mail check skipped: " + e.Message); }

        return kept.OrderBy(h => h.MaybeAnswered.Length > 0 ? 1 : 0).ThenByDescending(h => h.Date).ToList();
    }

    static string First(string name) { var n = (name ?? "").Trim(); var i = n.IndexOfAny(new[] { ' ', ',' }); return i > 0 ? n[..i] : n; }
    static string Cut(string s, int n) => (s ?? "").Length > n ? s[..n] + "…" : s ?? "";
}
