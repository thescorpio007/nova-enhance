namespace NovaEnhance;

/// <summary>0.2 🕘 History: one window, two tabs — 💬 Chats (every conversation) and 🔎 Searches (every search, 60 days).
/// The two existing pages live inside it unchanged. Designed by A.HAMROUNI</summary>
class HistoryHub : NovaForm
{
    readonly Panel host;
    readonly Button chatsBtn, searchBtn;
    ChatHistoryForm chats;
    HistoryForm searches;

    public HistoryHub(bool searchesFirst)
    {
        Text = "0.2 🕘 History";
        Width = 1180; Height = 820; MinimumSize = new Size(760, 480);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 8, 12, 6), BackColor = Th.Bg, WrapContents = false };
        top.Controls.Add(Th.L("🕘 History", 13, Th.Text, true));
        chatsBtn = Th.B("💬 Chats", () => Show(false));
        searchBtn = Th.B("🔎 Searches (60 days)", () => Show(true));
        top.Controls.Add(chatsBtn); top.Controls.Add(searchBtn);
        top.Controls.Add(Th.L("   Ctrl+Shift+H", 9, Th.Muted));
        host = new Panel { Dock = DockStyle.Fill, BackColor = Th.Bg };
        Controls.Add(host); Controls.Add(top);
        Show(searchesFirst);
    }

    static void Embed(Form f, Panel into)
    {
        f.TopLevel = false; f.FormBorderStyle = FormBorderStyle.None; f.Dock = DockStyle.Fill;
        into.Controls.Add(f); f.Show();
    }

    public void Show(bool searchesTab)
    {
        foreach (Control c in host.Controls) c.Visible = false;
        if (searchesTab)
        {
            if (searches == null || searches.IsDisposed) { searches = new HistoryForm(); Embed(searches, host); }
            searches.Visible = true; searches.Reload();
        }
        else
        {
            if (chats == null || chats.IsDisposed) { chats = new ChatHistoryForm(); Embed(chats, host); }
            chats.Visible = true; chats.Reload();
        }
        chatsBtn.BackColor = searchesTab ? Th.Card : Th.Accent; chatsBtn.ForeColor = searchesTab ? Th.Text : Color.White;
        searchBtn.BackColor = searchesTab ? Th.Accent : Th.Card; searchBtn.ForeColor = searchesTab ? Color.White : Th.Text;
    }
}
