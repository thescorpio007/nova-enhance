namespace NovaEnhance;

/// <summary>🧭 Nova agent · “Are these the kind of emails you mean?” — tick ✓ the ones that match, then Continue. Designed by A.HAMROUNI</summary>
class AgentConfirmForm : NovaForm
{
    (List<long>, List<long>, string)? result;

    public static (List<long>, List<long>, string)? Ask(IWin32Window owner, string question, List<MailHit> ex)
    {
        using var f = new AgentConfirmForm(question, ex);
        f.ShowDialog(owner);
        return f.result;
    }

    AgentConfirmForm(string question, List<MailHit> ex)
    {
        Text = "🧭 Nova agent · check these examples";
        Width = 900; Height = 520; StartPosition = FormStartPosition.CenterParent;
        var s = new Stack(); Controls.Add(s);
        s.Add(Th.L("✓ / ✗  " + question, 11, Th.Text, true));
        s.Add(Th.L("Tick the ones that match (unticked = not what I mean) · double-click to open the email", 9, Th.Muted));
        var box = new CheckedListBox { Width = 850, Height = 300, BackColor = Th.Card, ForeColor = Th.Text, Font = Th.F(9.5f), BorderStyle = BorderStyle.None, CheckOnClick = true, HorizontalScrollbar = true };
        foreach (var h in ex) box.Items.Add($"{h.Date:dd MMM yyyy} · {(h.Sent ? "me" : h.FromName)} → {h.To}{(string.IsNullOrEmpty(h.Cc) ? "" : " · cc " + h.Cc)} · {h.Subject}", true);
        box.DoubleClick += (_, _) => { if (box.SelectedIndex >= 0) try { Outlook.Open(ex[box.SelectedIndex].Entry, ex[box.SelectedIndex].StoreId); } catch { } };
        s.Add(box);
        var note = Th.Box(false); note.Width = 850; note.PlaceholderText = "anything to add? e.g. “only formal approval requests, not comments”";
        s.Add(note);
        var r = s.Add(Th.Row());
        r.Controls.Add(Th.B("Continue →", () =>
        {
            var yes = new List<long>(); var no = new List<long>();
            for (int i = 0; i < ex.Count; i++) (box.GetItemChecked(i) ? yes : no).Add(ex[i].Id);
            result = (yes, no, note.Text); Close();
        }, true));
        r.Controls.Add(Th.B("■ Stop", () => { result = null; Close(); }));
    }

    public override bool AllowedWhileLocked => false;
}
