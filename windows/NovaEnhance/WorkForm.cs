namespace NovaEnhance;

/// <summary>📌 My Work: tasks on the left (overdue · due today · waiting for others · done this week · by project), the task on the right.</summary>
class WorkForm : NovaForm
{
    readonly FlowLayoutPanel list;
    readonly TextBox find;
    readonly Label counts;
    readonly Stack detail;
    string currentId, project;
    List<WorkTask> tasks = new();

    public WorkForm()
    {
        Text = "Nova Enhance · 📌 My Work";
        Width = 1250; Height = 860; MinimumSize = new Size(900, 600);
        var left = new Panel { Dock = DockStyle.Left, Width = 320, BackColor = Color.FromArgb(0x0E, 0x11, 0x17), Padding = new Padding(10) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 118, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = left.BackColor };
        var nb = Th.B("＋ New task", () => { var t = Work.NewEmpty(); Reload(t.Id); }, true);
        nb.Width = 290; nb.AutoSize = false; nb.Height = 34;
        top.Controls.Add(nb);
        find = Th.Box(false); find.Width = 290; find.PlaceholderText = "🔍 Search tasks…";
        find.TextChanged += (_, _) => DrawList();
        top.Controls.Add(find);
        counts = Th.L("", 9, Th.AccentText, true); counts.MaximumSize = new Size(290, 0);
        top.Controls.Add(counts);
        list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = left.BackColor };
        left.Controls.Add(list);
        left.Controls.Add(top);
        detail = new Stack();
        Controls.Add(detail);
        Controls.Add(left);
        Shown += (_, _) => Reload(currentId);
        Activated += (_, _) => { if (Visible) DrawList(); };
    }

    public void Reload(string id = null)
    {
        tasks = Work.All();
        _ = Task.Run(() => Work.PullOutlook(tasks));
        if (id != null) currentId = id;
        if (currentId == null || tasks.All(t => t.Id != currentId)) currentId = tasks.Where(t => t.Status != "done").OrderByDescending(t => t.Created).FirstOrDefault()?.Id ?? tasks.FirstOrDefault()?.Id;
        DrawList();
        DrawDetail();
    }

    void DrawList()
    {
        tasks = Work.All();
        var q = find.Text.Trim();
        var shown = tasks.Where(t => q.Length == 0 || t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Question.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || t.Notes.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Items.Any(i => (i.Subject ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)))
                         .Where(t => project == null || t.Tags.Contains(project)).ToList();
        counts.Text = $"🔴 {tasks.Count(t => t.Overdue)} overdue · 🟠 {tasks.Count(t => t.DueToday)} due today · ⏸ {tasks.Count(t => t.Status == "waiting")} waiting for others" + (project != null ? $"\nProject: {project}  (click again to show all)" : "");
        list.SuspendLayout();
        foreach (Control c in list.Controls) c.Dispose();
        list.Controls.Clear();
        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        void Group(string title, IEnumerable<WorkTask> items)
        {
            var l = items.ToList();
            if (l.Count == 0) return;
            var h = Th.L(title, 8.5f, Th.Muted, true); h.Margin = new Padding(2, 12, 0, 4);
            list.Controls.Add(h);
            foreach (var t in l)
            {
                var tag = t.Status == "done" ? $"✓ {t.Done:ddd}" : t.Overdue ? "overdue" : t.DueToday ? "today" : t.Due != null ? t.Due.Value.ToString("ddd dd") : t.Status == "waiting" ? $"{(DateTime.Now - t.Created).Days} d" : $"{t.OpenCount}";
                var b = Th.B($"{t.Title}", () => { currentId = t.Id; DrawList(); DrawDetail(); });
                b.AutoSize = false; b.Width = 286; b.Height = 32; b.TextAlign = ContentAlignment.MiddleLeft; b.AutoEllipsis = true; b.Font = Th.F(9.5f);
                b.BackColor = t.Id == currentId ? Th.Card2 : list.BackColor;
                b.ForeColor = t.Status == "done" ? Th.Muted : Th.Text;
                var tip = new ToolTip(); tip.SetToolTip(b, tag);
                b.Text = $"{t.Title}   ·  {tag}";
                if (t.Overdue) b.ForeColor = Th.Bad;
                list.Controls.Add(b);
            }
        }
        Group("TO DO", shown.Where(t => t.Status == "todo").OrderBy(t => t.Due ?? DateTime.MaxValue).ThenByDescending(t => t.Created));
        Group("WAITING FOR OTHERS", shown.Where(t => t.Status == "waiting").OrderBy(t => t.Created));
        Group("DONE THIS WEEK", shown.Where(t => t.Status == "done" && t.Done >= monday).OrderByDescending(t => t.Done));
        Group("DONE EARLIER", shown.Where(t => t.Status == "done" && t.Done < monday).OrderByDescending(t => t.Done).Take(30));
        var projects = tasks.SelectMany(t => t.Tags).GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(12).ToList();
        if (projects.Count > 0)
        {
            var h = Th.L("BY PROJECT", 8.5f, Th.Muted, true); h.Margin = new Padding(2, 12, 0, 4);
            list.Controls.Add(h);
            foreach (var g in projects)
            {
                var name = g.Key;
                var b = Th.B($"🏷 {name}   {g.Count()}", () => { project = project == name ? null : name; DrawList(); });
                b.AutoSize = false; b.Width = 286; b.Height = 28; b.TextAlign = ContentAlignment.MiddleLeft; b.Font = Th.F(9.5f);
                b.BackColor = project == name ? Th.Card2 : list.BackColor;
                list.Controls.Add(b);
            }
        }
        if (tasks.Count == 0) list.Controls.Add(Th.Wrap("No tasks yet. In Ask Nova, the brief, folder search, Nova Chat or the PDF panel press 📌 Save as task — or ＋ New task.", 9.5f, Th.Muted));
        list.ResumeLayout();
    }

    void DrawDetail()
    {
        var t = tasks.FirstOrDefault(x => x.Id == currentId);
        detail.SuspendLayout();
        foreach (Control c in detail.Controls) c.Dispose();
        detail.Controls.Clear();
        if (t == null) { detail.Add(Th.Wrap("Choose a task on the left.", 10, Th.Muted)); detail.ResumeLayout(); return; }

        var title = detail.Add(Th.Wrap(t.Title, 15, Th.Text));
        title.Font = Th.F(15, true); title.Cursor = Cursors.Hand;
        title.Click += (_, _) => { var n = Prompt.Ask(this, "Rename", "Task name:", t.Title); if (!string.IsNullOrWhiteSpace(n)) { t.Title = n.Trim(); t.Add("✎ Renamed"); Work.Save(t); Work.SyncOutlook(t); Reload(t.Id); } };
        var status = t.Status switch { "done" => "✓ Done", "waiting" => "⏸ Waiting for others", _ => "To do" };
        detail.Add(Th.Wrap($"📅 Created {t.Created:ddd dd MMM yyyy HH:mm}" + (t.Mail ? $" · last refreshed {t.Refreshed:ddd dd MMM HH:mm}" : "") +
                           (t.Due != null ? $" · due {t.Due:ddd dd MMM}" : "") + (t.Remind != null && t.Status != "done" ? $" · ⏰ {t.Remind:ddd dd MMM HH:mm}" : "") + $" · {status}" +
                           (t.OutlookTask.Length > 0 ? " · ✓ in Outlook To-Do" : ""), 9.5f, t.Overdue ? Th.Bad : Th.Muted));
        if (t.Question.Length > 0) detail.Add(Th.Wrap("🔍 “" + t.Question + "”", 10, Th.Text));
        var people = t.Items.Where(i => i.Kind == "mail" && i.From.Length > 0).Select(i => i.From.Split(' ')[0]).Distinct().Take(5).ToList();
        detail.Add(Th.Wrap(string.Join("   ", t.Tags.Select(x => "🏷 " + x)) + (people.Count > 0 ? "   👤 " + string.Join(" · ", people) : "") +
                           (t.Items.Any(i => i.Kind != "text") ? $"   📊 {t.DoneCount} of {t.Items.Count(i => i.Kind != "text")} done" : ""), 9.5f, Th.AccentText));

        var diff = detail.Add(Th.Wrap("", 10, Th.Text));
        diff.BackColor = Th.Card2; diff.Padding = new Padding(8); diff.Visible = false;
        if (t.Mail)
        {
            var r = detail.Add(Th.Row());
            r.Controls.Add(Th.B("↻ Refresh now", async () =>
            {
                diff.Visible = true; diff.Text = "↻ Checking again — your replies are checked live in Outlook…";
                try { diff.Text = await Work.Refresh(t, CancellationToken.None); var keep = diff.Text; Reload(t.Id); ShowDiff(keep); }
                catch (Exception e) { diff.Text = "⚠ " + e.Message; }
            }, true));
            r.Controls.Add(Th.B("✉ Open all waiting", () =>
            {
                foreach (var i in t.Items.Where(i => i.Kind == "mail" && i.State == "open").Take(15)) try { Outlook.Open(i.Entry, i.Store); } catch { }
            }));
        }

        foreach (var i in t.Items.OrderBy(i => i.State == "open" ? (i.IsNew ? 1 : 0) : 2).ThenByDescending(i => i.Date))
        {
            if (i.Kind == "text")
            {
                var tx = detail.Add(Th.Wrap(i.Text, 10, Th.Text));
                tx.BackColor = Th.Card; tx.Padding = new Padding(10);
                continue;
            }
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
            var cb = Th.Check((i.Kind == "mail" ? $"{i.Subject} — {i.From} · {i.Date:dd MMM HH:mm}" : $"📄 {i.Subject}  {i.Loc}") + (i.IsNew ? "   ➕ new" : ""),
                i.State != "open", v =>
                {
                    i.State = v ? "done" : "open";
                    i.Why = v ? $"ticked by you {DateTime.Now:ddd HH:mm}" : "";
                    t.Add((v ? "☑ " : "☐ ") + i.Subject);
                    Work.Save(t); Work.SyncOutlook(t);
                });
            cb.ForeColor = i.State == "open" ? Th.Text : Th.Muted;
            cb.MaximumSize = new Size(Math.Max(400, detail.ClientSize.Width - 80), 0);
            row.Controls.Add(cb);
            var sub = i.Why.Length > 0 ? "✓ " + i.Why : (i.Summary.Length > 0 ? "✦ " + i.Summary : "") + (i.Att.Length > 0 ? "  📎 " + i.Att.Split(';')[0] : "");
            if (sub.Length > 0)
            {
                var sl = Th.L(sub, 8.5f, i.Why.Length > 0 ? Th.Good : Th.Muted);
                sl.Margin = new Padding(20, 0, 0, 0); sl.MaximumSize = new Size(Math.Max(400, detail.ClientSize.Width - 100), 0);
                row.Controls.Add(sl);
            }
            var open = Th.B(i.Kind == "mail" ? "Open" : "Open file", () =>
            {
                try
                {
                    if (i.Kind == "mail") Outlook.Open(i.Entry, i.Store);
                    else Opener.Open(new FileHit { Path = i.Path }, new PieceHit { Loc = i.Loc, Goto = i.Goto, Text = "", Matches = new() }, "");
                }
                catch (Exception e) { MessageBox.Show(this, e.Message, "Nova Enhance"); }
            });
            open.Font = Th.F(8.5f); open.Margin = new Padding(20, 2, 0, 4);
            row.Controls.Add(open);
            detail.Add(row);
        }

        detail.Add(Th.Section("My notes"));
        var notes = detail.Add(Th.Box(true, 5), true);
        notes.Text = t.Notes;
        notes.Leave += (_, _) =>
        {
            if (notes.Text == t.Notes) return;
            t.Notes = notes.Text; t.NotesEdited = DateTime.Now; t.Add("✎ Notes edited");
            Work.Save(t); Work.SyncOutlook(t);
        };
        if (t.NotesEdited > DateTime.MinValue) detail.Add(Th.L($"✎ edited {t.NotesEdited:ddd dd MMM HH:mm}", 8.5f, Th.Muted));

        var bar = detail.Add(Th.Row());
        bar.Controls.Add(Th.B("📅 Due date", () => { var d = PickDate("Due date", t.Due ?? DateTime.Today.AddDays(1), false); if (d != null) { t.Due = d == DateTime.MinValue ? null : d.Value.Date; t.Add(t.Due == null ? "📅 Due date removed" : $"📅 Due date set to {d:ddd dd MMM}"); Work.Save(t); Work.SyncOutlook(t); Reload(t.Id); } }));
        bar.Controls.Add(Th.B("⏰ Remind me", () => { var d = PickDate("Remind me at", t.Remind ?? DateTime.Today.AddDays(1).AddHours(9), true); if (d != null) { t.Remind = d == DateTime.MinValue ? null : d; t.Reminded = false; t.Add(t.Remind == null ? "⏰ Reminder removed" : $"⏰ Reminder set for {d:ddd dd MMM HH:mm}"); Work.Save(t); Work.SyncOutlook(t); Reload(t.Id); } }));
        bar.Controls.Add(Th.B(t.Status == "waiting" ? "⇄ Status: To do" : "⇄ Status: Waiting for others", () =>
        {
            t.Status = t.Status == "waiting" ? "todo" : "waiting";
            t.Add(t.Status == "waiting" ? "⏸ Waiting for others" : "⇄ Back to To do");
            Work.Save(t); Work.SyncOutlook(t); Reload(t.Id);
        }));
        bar.Controls.Add(Th.B(t.Status == "done" ? "↩ Reopen" : "✓ Done", () =>
        {
            if (t.Status == "done") { t.Status = "todo"; t.Add("↩ Reopened"); } else { t.Status = "done"; t.Done = DateTime.Now; t.Add("✓ Done"); }
            Work.Save(t); Work.SyncOutlook(t); Reload(t.Id);
        }));
        bar.Controls.Add(Th.B("⇩ Export", () => Shell.Open(Work.Export(t))));
        bar.Controls.Add(Th.B("📊 Weekly report", () => Shell.Open(Work.WeeklyReport()), true));
        bar.Controls.Add(Th.B("🗑 Delete", () =>
        {
            if (MessageBox.Show(this, $"Delete “{t.Title}”? (Its Outlook task is deleted too.)", "Nova Enhance", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Work.Delete(t); currentId = null; Reload();
        }));

        detail.Add(Th.Section("Log"));
        foreach (var l in t.Log.OrderByDescending(l => l.T).Take(60)) detail.Add(Th.Wrap($"{l.T:ddd dd MMM yyyy HH:mm}   {l.Text}", 9, Th.Muted));
        detail.Add(Th.Signature());
        detail.ResumeLayout();
    }

    void ShowDiff(string text)
    {
        foreach (Control c in detail.Controls)
            if (c is Label l && l.BackColor == Th.Card2) { l.Text = text; l.Visible = true; break; }
    }

    DateTime? PickDate(string title, DateTime value, bool withTime)
    {
        using var f = new NovaForm { Text = title, Width = 360, Height = 190, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent };
        var s = new Stack(); f.Controls.Add(s);
        var p = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = withTime ? "ddd dd MMM yyyy  HH:mm" : "ddd dd MMM yyyy", Value = value < DateTimePicker.MinimumDateTime ? DateTime.Today : value, Width = 280, Font = Th.F(11) };
        s.Add(p);
        DateTime? res = null;
        var r = s.Add(Th.Row(false));
        r.Controls.Add(Th.B("OK", () => { res = p.Value; f.Close(); }, true));
        r.Controls.Add(Th.B("Clear", () => { res = DateTime.MinValue; f.Close(); }));
        r.Controls.Add(Th.B("Cancel", f.Close));
        f.ShowDialog(this);
        return res;
    }
}
