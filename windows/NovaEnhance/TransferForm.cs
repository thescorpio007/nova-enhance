namespace NovaEnhance;

/// <summary>📦 Nova transfer window: the queue with progress, speed and time left, pause / resume / cancel, the log, and the finished jobs
/// with a one-click retry of failed files. Designed by A.HAMROUNI</summary>
class TransferForm : NovaForm
{
    readonly FlowLayoutPanel list;
    readonly System.Windows.Forms.Timer timer;
    readonly Dictionary<string, (Panel Card, Label Title, ProgressBar Bar, Label Info, Label Cur, FlowLayoutPanel Btns, string Shape)> cards = new();
    readonly Label head;

    public TransferForm()
    {
        Text = "📦 Nova transfer";
        Width = 900; Height = 640; MinimumSize = new Size(640, 400);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14, 10, 14, 4), BackColor = Th.Bg };
        head = Th.L("📦 Nova transfer", 13, Th.Text, true);
        top.Controls.Add(head);
        top.Controls.Add(Th.B("＋ New copy…", () => New("copy")));
        top.Controls.Add(Th.B("＋ New move…", () => New("move")));
        top.Controls.Add(Th.B("🧹 Clear finished", () => { foreach (var j in Transfer.Jobs.ToList().Where(x => x.State is "done" or "cancelled")) Transfer.Forget(j); }));
        top.Controls.Add(Th.B("⚙ Settings", () => Screens.Settings("📦 Transfer engine")));
        list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12, 4, 12, 12), BackColor = Th.Bg };
        Controls.Add(list); Controls.Add(top);
        timer = new System.Windows.Forms.Timer { Interval = 700 };
        timer.Tick += (_, _) => Fill();
        timer.Start();
        Resize += (_, _) => { foreach (var c in cards.Values) c.Card.Width = Math.Max(500, list.ClientSize.Width - 34); };
        FormClosed += (_, _) => timer.Dispose();
        Shown += (_, _) => Fill();
    }

    void New(string kind)
    {
        using var d = new FolderBrowserDialog { Description = kind == "move" ? "Folder to move" : "Folder to copy", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        using var t = new FolderBrowserDialog { Description = "Destination", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (t.ShowDialog(this) != DialogResult.OK) return;
        Transfer.Add(kind, new[] { d.SelectedPath }, t.SelectedPath);
    }

    static string Shape(XJob j) => j.State + "|" + j.Failed.Count + "|" + (j.LogFile.Length > 0);

    void Fill()
    {
        List<XJob> jobs;
        try { jobs = Transfer.Jobs.ToList(); } catch { return; }
        var active = jobs.Count(j => j.State is "queued" or "scanning" or "running" or "waiting");
        head.Text = $"📦 Nova transfer · {active} active";
        foreach (var gone in cards.Keys.Where(k => jobs.All(j => j.Id != k)).ToList()) { cards[gone].Card.Dispose(); cards.Remove(gone); }
        // newest first: running on top
        var order = jobs.OrderBy(j => j.State is "running" or "scanning" ? 0 : j.State is "queued" or "waiting" or "paused" ? 1 : 2).ThenByDescending(j => j.Added).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            var j = order[i];
            if (!cards.TryGetValue(j.Id, out var c) || c.Shape != Shape(j))
            {
                if (cards.TryGetValue(j.Id, out var old)) { old.Card.Dispose(); cards.Remove(j.Id); }
                c = Card(j);
                cards[j.Id] = c;
                list.Controls.Add(c.Card);
            }
            list.Controls.SetChildIndex(c.Card, i);
            Update(j, c);
        }
    }

    (Panel, Label, ProgressBar, Label, Label, FlowLayoutPanel, string) Card(XJob j)
    {
        var p = new Panel { Width = Math.Max(500, list.ClientSize.Width - 34), Height = 128, BackColor = Th.Card, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(12, 8, 12, 8) };
        var title = Th.L(j.Title, 10, Th.Text, true); title.Location = new Point(12, 8); title.AutoSize = false; title.Width = p.Width - 24; title.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        var bar = new ProgressBar { Location = new Point(12, 34), Width = p.Width - 24, Height = 10, Maximum = 1000, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        var info = Th.L("", 9.5f, Th.Muted); info.Location = new Point(12, 48);
        var cur = Th.L("", 8.5f, Th.Muted); cur.Location = new Point(12, 68); cur.AutoSize = false; cur.Width = p.Width - 24; cur.Height = 18; cur.AutoEllipsis = true; cur.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        var btns = new FlowLayoutPanel { Location = new Point(12, 88), AutoSize = true, BackColor = Color.Transparent };
        if (j.State is "running" or "scanning" or "queued" or "waiting") btns.Controls.Add(Th.B("⏸ Pause", () => Transfer.Pause(j)));
        if (j.State is "paused" or "cancelled") btns.Controls.Add(Th.B("▶ Resume", () => Transfer.Resume(j), true));
        if (j.State is "running" or "scanning" or "queued" or "waiting" or "paused") btns.Controls.Add(Th.B("✖ Cancel", () => Transfer.Cancel(j)));
        if (j.State == "failed") { btns.Controls.Add(Th.B("↻ Retry failed files", () => Transfer.RetryFailed(j), true)); btns.Controls.Add(Th.B("▶ Run again", () => Transfer.Resume(j))); }
        if (j.LogFile.Length > 0) btns.Controls.Add(Th.B("📄 Log", () => { if (File.Exists(j.LogFile)) Shell.Open(j.LogFile); }));
        btns.Controls.Add(Th.B("📂 Open destination", () => { if (Directory.Exists(j.Dest)) Shell.Open(j.Dest); }));
        if (j.State is "done" or "failed" or "cancelled") btns.Controls.Add(Th.B("🗑", () => Transfer.Forget(j)));
        p.Controls.AddRange(new Control[] { title, bar, info, cur, btns });
        return (p, title, bar, info, cur, btns, Shape(j));
    }

    static void Update(XJob j, (Panel Card, Label Title, ProgressBar Bar, Label Info, Label Cur, FlowLayoutPanel Btns, string Shape) c)
    {
        var pct = j.TotalBytes > 0 ? Math.Min(1.0, (double)j.DoneBytes / j.TotalBytes) : j.TotalFiles > 0 ? Math.Min(1.0, (double)j.DoneFiles / j.TotalFiles) : j.State == "done" ? 1 : 0;
        c.Bar.Value = (int)(pct * 1000);
        var left = j.Speed > 0 && j.TotalBytes > j.DoneBytes ? TimeSpan.FromSeconds((j.TotalBytes - j.DoneBytes) / j.Speed) : TimeSpan.Zero;
        var state = j.State switch
        {
            "queued" => "⏳ waiting its turn", "scanning" => "🔎 counting the files…", "waiting" => "🌐 waiting for the VPN / network drive",
            "paused" => "⏸ paused", "done" => "✅ done", "failed" => "⚠ finished with errors", "cancelled" => "✖ cancelled", _ => "",
        };
        c.Info.Text = j.State == "running"
            ? $"{pct * 100:0} % · {j.DoneFiles:N0} / {j.TotalFiles:N0} files · {Transfer.Size(j.DoneBytes)} / {Transfer.Size(j.TotalBytes)} · {Transfer.Size((long)j.Speed)}/s" +
              (left > TimeSpan.Zero ? $" · {(left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min" : left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes} min {left.Seconds} s" : $"{left.Seconds} s")} left" : "") + $" · {j.Threads} threads"
            : $"{state} · {j.DoneFiles:N0} / {j.TotalFiles:N0} files · {Transfer.Size(j.TotalBytes)}" + (j.Ended != null ? $" · {j.Ended:HH:mm}" : "");
        c.Info.ForeColor = j.State == "failed" ? Th.Warn : j.State == "done" ? Th.Good : Th.Muted;
        var cur = j.State == "running" && j.Current.Length > 0 ? j.Current + (j.Current.Length > 260 ? $"  · long path {j.Current.Length}" : "") : j.Note;
        if (c.Cur.Text != cur) c.Cur.Text = cur;
    }
}
