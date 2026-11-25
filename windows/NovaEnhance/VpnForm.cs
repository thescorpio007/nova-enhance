using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace NovaEnhance;

/// <summary>
/// 🌐 Nova VPN — the office connection with Nova's own engine (OpenConnect, run hidden by the 🛡 helper): status, traffic, every option of
/// OpenConnect's Edit window (all its protocols), and the live log in plain words. Passwords, OTP secrets and session codes are never shown.
/// Designed by A.HAMROUNI
/// </summary>
class VpnForm : NovaForm
{
    readonly Label state, line1, line2, badges, ends, rx, tx, speed, drops;
    readonly Panel shield;
    readonly TextBox log;
    readonly Button connect, disconnect;
    readonly System.Windows.Forms.Timer timer;
    long lastRx = -1, lastTx = -1; DateTime lastAt = DateTime.Now;
    string tab = "log";

    public VpnForm()
    {
        Text = "🌐 Nova VPN — Office connection";
        Width = 1180; Height = 860; MinimumSize = new Size(960, 640);

        // ── status ──
        var hero = new TableLayoutPanel { Dock = DockStyle.Top, Height = 150, ColumnCount = 3, BackColor = Th.Card, Padding = new Padding(14) };
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hero.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        shield = new Panel { Width = 100, Height = 100, Margin = new Padding(0, 6, 10, 0) };
        shield.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var c = OfficeVpn.Now is OfficeVpn.State.Up ? Th.Good : OfficeVpn.Now is OfficeVpn.State.Connecting or OfficeVpn.State.UpNoServer ? Th.Yellow : Th.Bad;
            using var b = new SolidBrush(Color.FromArgb(60, c)); e.Graphics.FillEllipse(b, 2, 2, 94, 94);
            using var p = new Pen(c, 3); e.Graphics.DrawEllipse(p, 2, 2, 94, 94);
            TextRenderer.DrawText(e.Graphics, "🛡", Th.F(30), new Rectangle(0, 0, 100, 100), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        var mid = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        state = Th.L("…", 17, Th.Text, true);
        line1 = Th.L("", 9.5f, Th.Muted); line2 = Th.L("", 9.5f, Th.Muted); badges = Th.L("", 9.5f, Th.AccentText);
        mid.Controls.AddRange(new Control[] { state, line1, line2, badges });
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        connect = Th.B("🟢 Connect", DoConnect, true);
        disconnect = Th.B("■ Disconnect", () => { OfficeVpn.Disconnect(); Note("Disconnecting…"); });
        right.Controls.Add(connect); right.Controls.Add(disconnect);
        right.Controls.Add(Th.B("↻ Reconnect", () => Task.Run(() => { OfficeVpn.Disconnect(); Thread.Sleep(4000); OfficeVpn.Connect(); })));
        ends = Th.L("", 8.5f, Th.Muted); right.Controls.Add(ends);
        hero.Controls.Add(shield, 0, 0); hero.Controls.Add(mid, 1, 0); hero.Controls.Add(right, 2, 0);

        // ── traffic ──
        var stats = new TableLayoutPanel { Dock = DockStyle.Top, Height = 70, ColumnCount = 4, Padding = new Padding(10, 8, 10, 0), BackColor = Th.Bg };
        for (int i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        Label Stat(string title, int col)
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, BackColor = Th.Card, Margin = new Padding(4), Padding = new Padding(8, 4, 8, 4) };
            p.Controls.Add(Th.L(title, 8.5f, Th.Muted));
            var v = Th.L("—", 12, Th.Text, true); p.Controls.Add(v);
            stats.Controls.Add(p, col, 0);
            return v;
        }
        rx = Stat("Received", 0); tx = Stat("Sent", 1); speed = Stat("Speed now", 2); drops = Stat("Drops today", 3);

        // ── options | log ──
        var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Th.Line, SplitterWidth = 5 };
        Flex.DoubleClickMax(split);   // ↔ double-click the divider: the first side takes almost all the room, again to go back
        var left = new Stack { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 6, 12, 10), BackColor = Th.Bg };
        split.Panel1.Controls.Add(left);
        BuildOptions(left);

        var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Th.Bg, Padding = new Padding(6, 6, 6, 0) };
        log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(10, 12, 17), ForeColor = Color.FromArgb(0xB8, 0xBF, 0xD3), BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f), WordWrap = true };
        foreach (var (t, id) in new[] { ("Live log", "log"), ("History", "hist"), ("Engine", "eng"), ("🧪 Test", "test") })
            tabs.Controls.Add(Th.B(t, () => { tab = id; Refresh2(true); }));
        tabs.Controls.Add(Th.B("⧉ Copy", () => { try { if (log.Text.Length > 0) Clipboard.SetText(log.Text); } catch { } }));
        split.Panel2.Controls.Add(log); split.Panel2.Controls.Add(tabs);

        var sig = Th.L("Engine: OpenConnect (LGPL, built into Nova) · runs hidden through the 🛡 VPN helper · " + Program.Sign, 8.5f, Color.FromArgb(0x6B, 0x73, 0x85));
        sig.Dock = DockStyle.Bottom; sig.Padding = new Padding(16, 4, 0, 6);
        Controls.Add(split); Controls.Add(sig); Controls.Add(stats); Controls.Add(hero);
        Shown += (_, _) => { try { split.SplitterDistance = Math.Max(440, Width * 46 / 100); } catch { } Refresh2(true); };
        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => Refresh2(false);
        timer.Start();
        FormClosed += (_, _) => timer.Dispose();
    }

    // ------------------------------------------------------------------ the options (as in OpenConnect's Edit window)

    TextBox name, gw, port, user, pw, group, ca, pin, otp, ucert, ukey, iface, script, proxy;
    ComboBox proto, otpMode, logLevel, idle;
    NumericUpDown rto, dtlsWait;
    CheckBox dtls, auto, boot, pause;

    void BuildOptions(Stack s)
    {
        TextBox Box(string label, string v, int w = 300, bool secret = false)
        {
            var r = s.Add(Th.Row(false));
            r.Controls.Add(new Label { Text = label, Width = 150, Height = 28, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Th.Muted, Font = Th.F(9.5f) });
            var t = new TextBox { Text = v, Width = w, BackColor = Th.Card2, ForeColor = Th.Text, BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = secret };
            r.Controls.Add(t);
            return t;
        }
        ComboBox Combo(string label, string[] items, int sel, int w = 300)
        {
            var r = s.Add(Th.Row(false));
            r.Controls.Add(new Label { Text = label, Width = 150, Height = 28, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Th.Muted, Font = Th.F(9.5f) });
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = w, BackColor = Th.Card2, ForeColor = Th.Text, FlatStyle = FlatStyle.Flat };
            c.Items.AddRange(items); c.SelectedIndex = Math.Clamp(sel, 0, items.Length - 1);
            r.Controls.Add(c);
            return c;
        }
        TextBox FileBox(string label, string v, string filter)
        {
            var t = Box(label, v, 250);
            var b = Th.B("…", () => { using var d = new OpenFileDialog { Filter = filter }; if (d.ShowDialog(this) == DialogResult.OK) t.Text = d.FileName; });
            b.MinimumSize = new Size(0, 26);
            ((FlowLayoutPanel)t.Parent).Controls.Add(b);
            ((FlowLayoutPanel)t.Parent).Controls.Add(Th.B("✕", () => t.Text = ""));
            return t;
        }
        NumericUpDown Num(string label, int v, int min, int max, string unit)
        {
            var r = s.Add(Th.Row(false));
            r.Controls.Add(new Label { Text = label, Width = 150, Height = 28, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Th.Muted, Font = Th.F(9.5f) });
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(v, min, max), Width = 80, BackColor = Th.Card2, ForeColor = Th.Text };
            r.Controls.Add(n); r.Controls.Add(Th.L(unit, 9.5f, Th.Muted));
            return n;
        }

        s.Add(Th.Section("Connection"));
        name = Box("Name", Store.Str("win_vpn_name", "MZA office"));
        proto = Combo("VPN protocol", OfficeVpn.Protocols.Select(p => p.Name).ToArray(), Array.FindIndex(OfficeVpn.Protocols, p => p.Id == OfficeVpn.Proto));
        gw = Box("Gateway", OfficeVpn.Gateway);
        port = Box("Port", OfficeVpn.Port.ToString(), 90);
        user = Box("Username", OfficeVpn.User);
        pw = Box("Password", OfficeVpn.Password.Length > 0 ? "••••••••" : "", 200, true);
        s.Add(Th.L("Encrypted on this PC — never in a backup, never sent to the phone or the AI.", 8.5f, Th.Muted));
        group = Box("Groupname", OfficeVpn.Group, 200);
        pin = Box("Server certificate", OfficeVpn.Pin, 420);
        ca = FileBox("CA certificate", OfficeVpn.CaFile, "Certificates|*.pem;*.crt;*.cer|All files|*.*");
        otpMode = Combo("OTP token", new[] { "None", "HOTP (RFC 4226)", "TOTP (RFC 6238)" }, OfficeVpn.OtpMode switch { "hotp" => 1, "totp" => 2, _ => 0 }, 160);
        otp = Box("OTP secret", OfficeVpn.OtpSecret.Length > 0 ? "••••••••" : "", 200, true);
        s.Add(Th.Section("Local certificate"));
        ucert = FileBox("User certificate", OfficeVpn.UserCert, "Certificates|*.pem;*.crt;*.p12;*.pfx|All files|*.*");
        ukey = FileBox("User key", OfficeVpn.UserKey, "Keys|*.pem;*.key|All files|*.*");
        s.Add(Th.Section("Advanced"));
        iface = Box("Interface name", OfficeVpn.Iface, 200);
        script = FileBox("vpnc script", OfficeVpn.Script, "Scripts|*.js;*.bat;*.cmd|All files|*.*");
        s.Add(Th.L("Empty = the built-in script (routes and DNS). A custom script must be in Program Files: it runs with administrator rights.", 8.5f, Th.Muted));
        logLevel = Combo("Log level", new[] { "Normal", "Detailed (verbose)", "Errors only (quiet)" }, OfficeVpn.LogLevel switch { "verbose" => 1, "quiet" => 2, _ => 0 }, 200);
        rto = Num("Reconnect timeout", OfficeVpn.ReconnectTimeout, 10, 3600, "s — rejoins the same session after a short drop");
        dtlsWait = Num("DTLS attempt period", OfficeVpn.DtlsWait, 5, 120, "s");
        proxy = Box("Proxy", OfficeVpn.Proxy, 260);
        s.Add(Th.L("Empty = no proxy · e.g. http://proxy:8080 or socks5://host:1080", 8.5f, Th.Muted));

        s.Add(Th.Section("Options"));
        dtls = s.Add(Th.Check("Try the fast UDP channel (DTLS) — falls back to SSL by itself", OfficeVpn.Dtls, _ => { }));
        auto = s.Add(Th.Check("Reconnect automatically (drop, sleep, Wi-Fi change)", OfficeVpn.Auto, _ => { }));
        boot = s.Add(Th.Check("Connect when Windows starts", OfficeVpn.StartWithWindows, _ => { }));
        idle = Combo("Disconnect when idle", new[] { "Never", "1 h", "2 h", "4 h" }, OfficeVpn.IdleHours switch { 1 => 1, 2 => 2, 4 => 3, _ => 0 }, 120);
        pause = s.Add(Th.Check("Pause 📦 transfers while it is down, resume after", Transfer.PauseOnVpn, _ => { }));

        var br = s.Add(Th.Row(false));
        br.Controls.Add(Th.B("💾 Save", () => { Save(); Note("✓ Saved."); }, true));
        br.Controls.Add(Th.B("🛡 Set up the VPN helper", async () =>
        {
            Note("🛡 Setting up… answer Windows' question with Yes");
            var r = await Task.Run(VpnHelper.Install);
            Note(r.Length == 0 ? "✓ VPN helper and engine set up." : "⚠ " + r);
        }));
        s.Add(Th.Signature());
    }

    void Save()
    {
        Store.Set("win_vpn_name", name.Text.Trim());
        OfficeVpn.Proto = OfficeVpn.Protocols[Math.Max(0, proto.SelectedIndex)].Id;
        var g = gw.Text.Trim().Replace("https://", "").TrimEnd('/');
        if (g.Contains(':') && int.TryParse(g[(g.LastIndexOf(':') + 1)..], out var gp)) { g = g[..g.LastIndexOf(':')]; port.Text = gp.ToString(); gw.Text = g; }
        OfficeVpn.Host = g + (int.TryParse(port.Text.Trim(), out var pn) && pn > 0 && pn != 443 ? ":" + pn : "");
        OfficeVpn.User = user.Text.Trim();
        if (pw.Text.Length > 0 && pw.Text != "••••••••") OfficeVpn.Password = pw.Text;
        OfficeVpn.Group = group.Text.Trim();
        var pv = pin.Text.Trim(); if (pv.Length > 0 && !pv.StartsWith("pin-sha256:")) pv = "pin-sha256:" + pv; OfficeVpn.Pin = pv;
        OfficeVpn.CaFile = ca.Text.Trim();
        OfficeVpn.OtpMode = otpMode.SelectedIndex switch { 1 => "hotp", 2 => "totp", _ => "none" };
        if (otp.Text.Length > 0 && otp.Text != "••••••••") OfficeVpn.OtpSecret = otp.Text.Trim();
        OfficeVpn.UserCert = ucert.Text.Trim(); OfficeVpn.UserKey = ukey.Text.Trim();
        OfficeVpn.Iface = iface.Text.Trim().Length > 0 ? iface.Text.Trim() : "Nova VPN";
        OfficeVpn.Script = script.Text.Trim();
        OfficeVpn.LogLevel = logLevel.SelectedIndex switch { 1 => "verbose", 2 => "quiet", _ => "normal" };
        OfficeVpn.ReconnectTimeout = (int)rto.Value; OfficeVpn.DtlsWait = (int)dtlsWait.Value;
        OfficeVpn.Proxy = proxy.Text.Trim();
        OfficeVpn.Dtls = dtls.Checked; OfficeVpn.Auto = auto.Checked; OfficeVpn.StartWithWindows = boot.Checked;
        OfficeVpn.IdleHours = idle.SelectedIndex switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };
        Transfer.PauseOnVpn = pause.Checked;
    }

    void DoConnect()
    {
        Save();
        // this window drives Nova's own engine
        OfficeVpn.Order = "cli"; OfficeVpn.On = true;
        if (!VpnHelper.Installed) { Note("⚠ Press 🛡 Set up the VPN helper first (Windows asks once) — it installs the engine."); return; }
        if (OfficeVpn.Password.Length == 0) { Note("⚠ Type your password and press Save."); return; }
        OfficeVpn.Connect();
        Note("🟡 Connecting…");
    }

    string note = ""; DateTime noteAt;
    void Note(string s) { note = s; noteAt = DateTime.Now; Refresh2(false); }

    // ------------------------------------------------------------------ live view

    void Refresh2(bool full)
    {
        try
        {
            var now = OfficeVpn.Now;
            var info = OfficeVpn.ReadEngineLog();
            state.Text = now switch
            {
                OfficeVpn.State.Up => "🟢 Connected",
                OfficeVpn.State.UpNoServer => OfficeVpn.Since > DateTime.Now.AddSeconds(-20) ? "🟡 Connected — setting up the office routes…" : "🟡 Connected — office server not answering",
                OfficeVpn.State.Connecting => "🟡 Connecting…",
                OfficeVpn.State.Off => "⚪ Office VPN is off",
                _ => "🔴 Not connected",
            };
            state.ForeColor = now == OfficeVpn.State.Up ? Th.Good : now is OfficeVpn.State.Connecting or OfficeVpn.State.UpNoServer ? Th.Yellow : Th.Bad;
            var (ph, pp) = OfficeVpn.ProbeTarget();
            line1.Text = $"{Store.Str("win_vpn_name", "Office")} · {OfficeVpn.Gateway}:{OfficeVpn.Port} · {OfficeVpn.Protocols.FirstOrDefault(p => p.Id == OfficeVpn.Proto).Name}" +
                         (now == OfficeVpn.State.Up && OfficeVpn.Since != default ? $" · since {OfficeVpn.Since:HH:mm} ({Dur(DateTime.Now - OfficeVpn.Since)})" : "");
            line2.Text = now == OfficeVpn.State.Up
                ? $"Office IP {(info.Ip.Length > 0 ? info.Ip : "—")}{(info.Route.Length > 0 ? " · route " + info.Route : "")} · office server {ph} answers" + (info.Firewall.Length > 0 ? " · " + info.Firewall : "")
                : (info.Reason.Length > 0 ? "⚠ " + info.Reason : OfficeVpn.Line);
            badges.Text = now == OfficeVpn.State.Up
                ? (info.Dtls == true ? "⚡ fast UDP channel (DTLS)" : OfficeVpn.Dtls ? "SSL channel (the firewall did not offer UDP)" : "SSL channel (UDP off)") + (OfficeVpn.Pin.Length > 0 ? " · 🔐 certificate pinned" : "")
                : (note.Length > 0 && DateTime.Now - noteAt < TimeSpan.FromSeconds(30) ? note : "");
            if (now == OfficeVpn.State.Up) note = "";
            ends.Text = info.Ends.Length > 0 && now == OfficeVpn.State.Up ? "Session ends at the firewall: " + info.Ends : "";
            connect.Enabled = now is not OfficeVpn.State.Up;
            shield.Invalidate();

            // traffic of the VPN adapter
            // Nova's own adapter first (by its name), then another VPN adapter
            var nics = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).ToList();
            var nic = nics.FirstOrDefault(n => n.Name.Equals(OfficeVpn.Iface, StringComparison.OrdinalIgnoreCase))
                      ?? nics.FirstOrDefault(n => n.Name.Contains(OfficeVpn.Gateway, StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(n.Description, "OpenConnect|Wintun|Fortinet SSL", RegexOptions.IgnoreCase));
            if (nic != null)
            {
                var st = nic.GetIPStatistics();
                rx.Text = Size(st.BytesReceived); tx.Text = Size(st.BytesSent);
                var secs = Math.Max(0.5, (DateTime.Now - lastAt).TotalSeconds);
                if (lastRx >= 0) speed.Text = $"↓ {Size((long)((st.BytesReceived - lastRx) / secs))}/s · ↑ {Size((long)((st.BytesSent - lastTx) / secs))}/s";
                lastRx = st.BytesReceived; lastTx = st.BytesSent; lastAt = DateTime.Now;
            }
            else { rx.Text = tx.Text = speed.Text = "—"; lastRx = -1; }
            drops.Text = OfficeVpn.DropsToday + (OfficeVpn.LastBackSeconds >= 0 ? $" · back in {OfficeVpn.LastBackSeconds} s" : "");

            if (tab == "log" || full) ShowTab();
        }
        catch { }
    }

    void ShowTab()
    {
        string text = tab switch
        {
            "hist" => string.Join(Environment.NewLine, Log.ReadAll().Where(e => e.Area == "office vpn").Take(200).Select(e => Log.Format(e).Split('\n')[0])),
            "eng" => Engine(),
            "test" => testText.Length > 0 ? testText : "🧪 Testing… (up to 30 s)",
            _ => Tail(),
        };
        if (tab == "test" && testText.Length == 0 && !testing) { testing = true; Task.Run(() => { var t = OfficeVpn.Diagnose(); testText = t; testing = false; try { BeginInvoke(() => { if (tab == "test") ShowTab(); }); } catch { } }); }
        if (log.Text != text) { log.Text = text; if (tab == "log") { log.SelectionStart = log.TextLength; log.ScrollToCaret(); } }
    }
    string testText = ""; bool testing;

    static string Tail()
    {
        try
        {
            if (!File.Exists(OfficeVpn.EngineLog)) return "The engine has not run yet. Press 🟢 Connect.";
            using var fs = new FileStream(OfficeVpn.EngineLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd().Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0 && !Regex.IsMatch(l, "cookie|SVPN", RegexOptions.IgnoreCase)).TakeLast(300);
            return string.Join(Environment.NewLine, lines) + Environment.NewLine + "… session codes and passwords are never shown";
        }
        catch (Exception e) { return e.Message; }
    }

    static string Engine()
    {
        var exe = OfficeVpn.Cli();
        var v = "";
        try
        {
            if (exe != null)
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                v = p!.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(5000);
            }
        }
        catch (Exception e) { v = e.Message; }
        return $"Engine: {exe ?? "not installed — press 🛡 Set up the VPN helper"}{Environment.NewLine}{v.Trim()}{Environment.NewLine}{Environment.NewLine}" +
               $"🛡 VPN helper: {VpnHelper.State()}{Environment.NewLine}" +
               "OpenConnect is free software (GNU LGPL 2.1), included unchanged from the official OpenConnect-GUI 1.6.2 release. " +
               "Its licence is in the engine folder (LICENSE); source code: https://www.infradead.org/openconnect/ and https://github.com/openconnect/openconnect-gui";
    }

    static string Size(long b) => b < 1024 ? b + " B" : b < 1 << 20 ? $"{b / 1024.0:0} KB" : b < 1L << 30 ? $"{b / 1048576.0:0.0} MB" : $"{b / 1073741824.0:0.00} GB";
    static string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{(int)t.TotalMinutes} min";
}
