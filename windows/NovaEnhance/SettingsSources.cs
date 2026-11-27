namespace NovaEnhance;

/// <summary>2.1 💻 Local data · 2.2 🖧 Network server · 4.4 📋 Submittal log · 4.5 📄 Nova for this PDF — one shared Nova index.
/// The server (MZA DC) is the official source: shown first, an older local copy is flagged. Designed by A.HAMROUNI</summary>
partial class SettingsForm
{
    IEnumerable<(Project P, string Folder)> FoldersWhere(bool network) =>
        Projects.All().SelectMany(p => p.Folders.Select(f => (p, f))).Where(x => Projects.ReadAll.IsNetwork(x.f) == network);

    void FolderRows(bool network)
    {
        var any = false;
        foreach (var g in FoldersWhere(network).GroupBy(x => x.P))
        {
            page.Add(Th.Section($"■ {g.Key.Code} · {g.Key.Name}"));
            foreach (var (_, f) in g)
            {
                any = true;
                var st = Projects.Status.TryGetValue(f, out var s) ? s : null;
                var state = st == null ? "⏳ waiting" : st.Phase == "listed" ? $"✓ {st.Files:N0} names · {st.Read:N0} read · {st.Last:dd MMM HH:mm}"
                    : st.Phase == "listing" ? $"⟳ listing {st.Files:N0}…" : st.Phase;
                var r = page.Add(Th.Row(false));
                r.Controls.Add(Th.L((network ? "🖧 " : "💻 ") + f, 10, Th.Text));
                r.Controls.Add(Th.L("   " + state, 9.5f, st?.Phase == "listed" ? Th.Good : Th.Muted));
                var tags = g.Key.Sources.Where(x => x.Path.StartsWith(f, StringComparison.OrdinalIgnoreCase) && !x.Path.TrimEnd('\\').Equals(f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var t in tags) Note($"      ↳ {t.Path[f.TrimEnd('\\').Length..]} — tagged {t.Kind} (listed first, not twice)", Th.Muted);
            }
        }
        if (!any) Note(network ? "No server folder yet — add one in 4.1 📁 Projects & sources (e.g. S:\\…)." : "No local folder yet — add one in 4.1 📁 Projects & sources.", Th.Warn);
    }

    void LocalDataPage()
    {
        Note("Your local copies (F:, C:, D: …): listed in minutes and read inside freely. They never wait behind the server. Same file on the server and here = one document with two places — the server copy is shown first, and an older local copy is flagged “⚠ older than the server”.", Th.Text);
        var r = page.Add(Th.Row());
        r.Controls.Add(Th.B("＋ Add a folder…", () => Go("📁 Projects"), true));
        r.Controls.Add(Th.B("⟳ Check now", () => { Projects.Indexer.Refresh(); Draw("💻 Local data"); }));
        FolderRows(false);
    }

    void NetworkPage()
    {
        var up = OfficeVpn.Now == OfficeVpn.State.Up;
        Note((up ? "🟢 " : "🔴 ") + OfficeVpn.Line, up ? Th.Good : Th.Warn);
        Note("🖧 The server is the official source (MZA DC saves every update there): its files are shown first everywhere — search, 🎯 Find a file, Nova Chat, the Submittal log and the contract register.", Th.Text);
        Note("📋 Listed in parts, most important first: folders tagged Submittals, then Contract / Specifications, then Drawings, then the rest (tag folders in 4.1). After the first complete listing, only folders whose date changed are read again — every hour while the VPN is up.", Th.Muted);
        var r = page.Add(Th.Row());
        r.Controls.Add(Th.B("＋ Add or tag a folder…", () => Go("📁 Projects"), true));
        r.Controls.Add(Th.B("⟳ Check changes now", () => { Projects.Indexer.Refresh(); Draw("🖧 Network server"); }));
        r.Controls.Add(Th.B("🌐 Office VPN…", () => Go("🌐 Office VPN")));
        FolderRows(true);
    }

    void SubmittalSettingsPage()
    {
        Note("📋 The submittal log reads every submission in Outlook (Thinkproject or direct email) and checks whether MZA replied on the same thread.", Th.Text);
        Note("Its words live in your 🧠 Brain, so you can change them: SL-19 “Status words” (outstanding = Open + Overdue …) and the other SL rules.", Th.Muted);
        var r = page.Add(Th.Row());
        r.Controls.Add(Th.B("📋 Open the Submittal log", () => Screens.Submittal(), true));
        r.Controls.Add(Th.B("🧠 Its Brain rules", () => Go("🧠 Nova Brain")));
    }

    void PdfSettingsPage()
    {
        Note("📄 Nova for this PDF (Ctrl+Shift+D): text pages answer in seconds, the scanned pages that matter are read first (3 at a time), the rest in the background — kept in the shared Nova index.", Th.Text);
        Note("How it answers (every item with its reference, schedules row by row, title against content, related words) lives in your 🧠 Brain → 📄 Reading documents (RD-01 … RD-04).", Th.Muted);
        var r = page.Add(Th.Row());
        r.Controls.Add(Th.B("📄 Open Nova for this PDF", () => PdfCompanion.Open(), true));
        r.Controls.Add(Th.B("🧠 Reading documents rules", () => Go("🧠 Nova Brain")));
    }
}
