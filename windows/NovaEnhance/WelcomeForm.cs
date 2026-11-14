namespace NovaEnhance;

/// <summary>
/// ✦ First start (universal Nova): name, role, country, field, languages and the AI key — or restore your own backup.
/// Shown once, when nothing is set up on this PC. Designed by A.HAMROUNI
/// </summary>
class WelcomeForm : Form
{
    public WelcomeForm()
    {
        Text = "Nova Enhance · Welcome";
        BackColor = Th.Bg; ForeColor = Th.Text; Font = Th.F(10f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1000, 760); MinimumSize = new Size(820, 600);
        try { Icon = Th.AppIcon; } catch { }
        var page = new Stack { Dock = DockStyle.Fill, Padding = new Padding(26, 18, 26, 18) };
        Controls.Add(page);

        page.Add(Th.L("✦ Welcome to Nova Enhance", 18f, Th.Text, true));
        page.Add(Th.L("Tell Nova about you — every correction, search, brief and draft will follow your field and your way of writing. Everything stays on this PC.", 10f, Th.Muted));

        page.Add(Th.Section("1 · You"));
        var r1 = page.Add(Th.Row(false));
        TextBox Field(string label, string hint, int w)
        {
            r1.Controls.Add(Th.L(label, 10, Th.Text));
            var b = Th.Box(false); b.Width = w; b.PlaceholderText = hint; r1.Controls.Add(b); return b;
        }
        var name = Field("Name", "your name", 180);
        var role = Field("Role", "e.g. pharmacist, teacher, site engineer", 280);
        var country = Field("Country", "optional", 180);

        page.Add(Th.Section("2 · Your field — picks the right words, codes and examples"));
        var grid = page.Add(Th.Row());
        var field = "general";
        foreach (var p in Packs.All)
        {
            var id = p.Id;
            grid.Controls.Add(Th.Radio($"{p.Icon} {p.Name} — {p.Desc}", id == "general", () => { field = id; role.PlaceholderText = "e.g. " + Packs.ById(id).Person; }));
        }
        var other = page.Add(Th.Row(false));
        other.Controls.Add(Th.L("✎ Other field (optional)", 10, Th.Text));
        var otherBox = Th.Box(false); otherBox.Width = 380; otherBox.PlaceholderText = "describe it, e.g. aviation maintenance, hotel management";
        other.Controls.Add(otherBox);

        page.Add(Th.Section("3 · Languages"));
        var lr = page.Add(Th.Row(false));
        var lang = "English";
        foreach (var l in new[] { "English", "Arabic", "French", "German", "Spanish" }) { var ll = l; lr.Controls.Add(Th.Radio(l, l == "English", () => lang = ll)); }

        page.Add(Th.Section("4 · Your AI"));
        page.Add(Th.L("Nova uses your own AI account. A free Gemini key works well (aistudio.google.com → Get API key).", 9.5f, Th.Muted));
        var kr = page.Add(Th.Row(false));
        kr.Controls.Add(Th.L("Gemini key", 10, Th.Text));
        var key = Th.Box(false, 1, true); key.Width = 360; kr.Controls.Add(key);
        kr.Controls.Add(Th.B("🔑 Get a free key", () => Shell.Open("https://aistudio.google.com/apikey")));
        kr.Controls.Add(Th.B("Other AI…", () => Screens.Settings("AI & API keys")));

        var st = page.Add(Th.L("", 10f, Th.Warn));
        var go = page.Add(Th.Row(false));
        go.Controls.Add(Th.B("✓ Start using Nova", () =>
        {
            if (name.Text.Trim().Length > 0) Store.Set("p_name", name.Text.Trim());
            Persona.Role = role.Text; Persona.Country = country.Text;
            Store.Set("win_field", field);
            var fields = new List<string> { Packs.ById(field).Name };
            if (otherBox.Text.Trim().Length > 0) fields.Add(otherBox.Text.Trim().Replace("|", " "));
            Store.ProfileFields = string.Join("|", fields);
            if (otherBox.Text.Trim().Length > 0 && Persona.Role.Length == 0) Persona.Role = otherBox.Text.Trim();
            Store.Set("p_langs", lang);
            if (key.Text.Trim().Length > 10) Store.SetKey("gemini", key.Text.Trim());
            Store.SetBool("p_asked", true);
            Close();
            if (key.Text.Trim().Length <= 10) Screens.Settings("AI & API keys");
        }, true));
        go.Controls.Add(Th.B("📂 Restore my backup instead", () => { Close(); Screens.Settings("Backup & restore"); }));
        page.Add(Th.L("Restoring a backup brings back everything from your own setup — field, words, rules, prompts, email projects and keys.", 9.5f, Th.Muted));
        page.Add(Th.L(Program.Sign + " · " + Program.Shown, 9f, Th.Muted));
    }
}
