using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// Right-click on any email (Ask Nova, the morning brief, My Work, the reading pane, Ask about this email):
/// the usual options in one menu. The Menu key and Shift+F10 open it too. Designed by A.HAMROUNI
/// </summary>
static class EmailMenu
{
    static readonly Regex RefRx = new(@"\b[A-Z][A-Z0-9]{1,6}(?:-[A-Z0-9]{1,8}){2,9}\b");

    public static void Show(MailHit h, Control owner, Point screen, Action<MailHit> summary = null, Action<MailHit> draft = null, Action<MailHit> handled = null, Action<MailHit> preview = null)
    {
        if (h == null) return;
        var form = owner.FindForm();
        var m = new ContextMenuStrip { Font = Th.F(10), ShowImageMargin = false, BackColor = Color.FromArgb(0x1D, 0x21, 0x30), ForeColor = Th.Text, Renderer = new ToolStripProfessionalRenderer(new DarkColors()) };
        void Add(string text, Action a, bool bold = false) { var i = m.Items.Add(text, null, (_, _) => { try { a(); } catch (Exception e) { MessageBox.Show(e.Message, "Nova Enhance"); } }); if (bold) i.Font = Th.F(10, true); }
        void Sep() => m.Items.Add(new ToolStripSeparator());

        Add("👁 Preview", () => { if (preview != null) preview(h); else MailWindow.Open(h); }, true);
        Add("Open in Outlook", () => Outlook.Open(h.Entry, h.StoreId));
        Sep();
        Add("✦ Summary", () => { if (summary != null) summary(h); else MailWindow.Open(h, "summary"); });
        Add(h.Sent ? "✦ Draft follow-up" : "✦ Draft reply", () => { if (draft != null) draft(h); else DraftTo(h, false, form); });
        if (!h.Sent) Add("✦ Draft reply all", () => DraftTo(h, true, form));
        Add("✦ Ask about this email", () => Screens.MailAsk(h));
        Sep();
        if (!h.Sent)
        {
            if (Outstanding.IsHandled(h)) Add("↩ Undo handled", () => { Outstanding.MarkHandled(h, false); Program.Tray?.ShowBalloonTip(3000, "Nova Enhance", "↩ It shows as waiting again (after the next refresh).", ToolTipIcon.Info); });
            else Add("✓ Mark as handled", () => { if (handled != null) handled(h); else { Outstanding.MarkHandled(h, true); Work.LogMail(h, "✓ Handled"); } });
        }
        Add("📌 Save as task", () => Screens.Saved(Work.FromMail(h.Subject, null, "mail", new List<MailHit> { h }, "menu")));
        var add = new ToolStripMenuItem("➕ Add to a task");
        foreach (var t in Work.All().Where(t => t.Status != "done").OrderByDescending(t => t.Created).Take(15))
        {
            var tt = t;
            add.DropDownItems.Add(tt.Title, null, (_, _) =>
            {
                tt.Items.Add(Work.FromHit(h));
                tt.Add("Email added: " + h.Subject);
                Work.Save(tt);
                Program.Tray?.ShowBalloonTip(3000, "📌 My Work", $"Added to “{tt.Title}”", ToolTipIcon.Info);
            });
        }
        if (add.DropDownItems.Count == 0) add.DropDownItems.Add(new ToolStripLabel("No open tasks yet"));
        m.Items.Add(add);
        Sep();
        Add("💾 Save as…", () => MailFile.SaveAs(h, form));
        Add("⧉ Copy as file (.msg)", () => MailFile.Copy(h, form));
        var copy = new ToolStripMenuItem("⧉ Copy");
        copy.DropDownItems.Add("Subject", null, (_, _) => Clipboard.SetText(h.Subject ?? ""));
        if ((h.FromAddr ?? "").Length > 0) copy.DropDownItems.Add($"Sender's email — {h.FromAddr}", null, (_, _) => Clipboard.SetText(h.FromAddr));
        foreach (var r in RefRx.Matches(h.Subject ?? "").Select(x => x.Value).Distinct().Take(4))
        { var rr = r; copy.DropDownItems.Add("Reference — " + rr, null, (_, _) => Clipboard.SetText(rr)); }
        copy.DropDownItems.Add("The email text", null, (_, _) => Clipboard.SetText(h.NewPart.Length > 0 ? h.NewPart : h.Body ?? ""));
        m.Items.Add(copy);
        Sep();
        Add("🔍 Find related emails", () =>
        {
            var r = RefRx.Match(h.Subject ?? "");
            Screens.AskFor(r.Success ? $"emails about {r.Value}" : $"emails with the subject \"{Outstanding.NormSubject(h.Subject ?? "")}\"");
        });
        var dwg = Revisions.Numbers((h.Subject ?? "") + "\n" + (h.NewPart.Length > 0 ? h.NewPart : h.Body ?? "")).FirstOrDefault();
        if (dwg != null) Add("📂 Find " + dwg + " in the project files", () => Screens.FindFile(dwg));
        var names = (h.AttNames ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => !Regex.IsMatch(n, @"^image\d*\.(png|jpe?g|gif)$", RegexOptions.IgnoreCase)).ToList();
        foreach (var n in names.Take(8))
        {
            var nn = n;
            var a = new ToolStripMenuItem("📎 " + n);
            a.DropDownItems.Add("👁 Preview", null, (_, _) => MailPreview.PreviewAttachment(h, nn, form));
            a.DropDownItems.Add("💾 Save…", null, (_, _) => MailPreview.SaveAttachmentAs(h, nn, form));
            m.Items.Add(a);
        }
        foreach (ToolStripItem i in m.Items) Dark(i);
        m.Show(screen);
    }

    static void Dark(ToolStripItem i)
    {
        i.ForeColor = Th.Text;
        if (i is ToolStripMenuItem mi) foreach (ToolStripItem k in mi.DropDownItems) { k.ForeColor = Th.Text; k.BackColor = Color.FromArgb(0x1D, 0x21, 0x30); }
    }

    /// <summary>Hooks right-click (and the Menu key / Shift+F10 on the form) onto a card and everything inside it.</summary>
    public static void Hook(Control c, MailHit h, Func<Point, bool> show)
    {
        c.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) show(Cursor.Position); };
        foreach (Control k in c.Controls) Hook(k, h, show);
    }

    public static async void DraftTo(MailHit h, bool all, Form owner)
    {
        if (!Store.AiSeesFull && !ConfirmSend.LocalHandlesPrivate) { MessageBox.Show(owner, "✦ Draft reply needs Settings → Email & AI → AI sees C or D (or the local AI).", "Nova Enhance"); return; }
        var instr = Prompt.AskLong(owner, all ? "✦ Draft reply all" : "✦ Draft reply", "✎ Instructions for your AI (optional) — what to say, tone, length, language…");
        if (instr == null) return;
        try
        {
            var d = await Assistant.Draft(h, (h.Sent ? "This is a polite follow-up to my own email that got no answer. " : "") + instr, CancellationToken.None);
            Outlook.Reply(h.Entry, h.StoreId, d, all);
        }
        catch (Exception e) { MessageBox.Show(owner, "⚠ " + e.Message, "Nova Enhance"); }
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(0x2E, 0x2A, 0x55);
        public override Color MenuItemBorder => Color.FromArgb(0x7C, 0x6C, 0xFF);
        public override Color ToolStripDropDownBackground => Color.FromArgb(0x1D, 0x21, 0x30);
        public override Color ImageMarginGradientBegin => Color.FromArgb(0x1D, 0x21, 0x30);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(0x1D, 0x21, 0x30);
        public override Color ImageMarginGradientEnd => Color.FromArgb(0x1D, 0x21, 0x30);
        public override Color MenuBorder => Color.FromArgb(0x3A, 0x42, 0x56);
        public override Color SeparatorDark => Color.FromArgb(0x2E, 0x35, 0x46);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0x2E, 0x2A, 0x55);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(0x2E, 0x2A, 0x55);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(0x2E, 0x2A, 0x55);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(0x2E, 0x2A, 0x55);
    }
}

/// <summary>👁 One email in its own window (formatted preview, attachments, the usual buttons).</summary>
class MailWindow : NovaForm
{
    readonly ReadPane pane;

    MailWindow(MailHit h)
    {
        Text = "Nova Enhance · 👁 " + h.Subject;
        Width = 980; Height = 820;
        pane = new ReadPane { Dock = DockStyle.Fill };
        Controls.Add(pane);
        pane.OnSummary = async m =>
        {
            pane.ShowAi("✦ Summarising…");
            try { pane.ShowAi("✦ " + m.Subject + "\n\n" + await Assistant.Summarize(m, CancellationToken.None)); } catch (Exception e) { pane.ShowAi("⚠ " + e.Message); }
        };
        pane.OnDraft = m => EmailMenu.DraftTo(m, false, this);
        pane.OnHandled = m => { Outstanding.MarkHandled(m, true); Work.LogMail(m, "✓ Handled"); pane.ShowAi("✓ Marked as handled — it won't show as waiting again."); };
        Shown += (_, _) => pane.Show(h);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    public static void Open(MailHit h, string then = null)
    {
        var w = new MailWindow(h);
        w.Show();
        if (then == "summary") w.Shown += (_, _) => w.pane.OnSummary?.Invoke(h);
    }
}
