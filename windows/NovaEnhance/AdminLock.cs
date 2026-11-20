using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace NovaEnhance;

/// <summary>
/// 🔐 Admin lock: when it is on, nothing in Nova opens without your PIN — except ✦ Enhance selected text and 📄 Nova for this PDF,
/// which then work on their own text / PDF only (no Brain, emails, files, history, private words; your tone and language stay).
/// Locks after your idle time (Settings → 🔐 Admin), when Windows locks or sleeps, when Nova starts, or with 🔐 Lock now.
/// The PIN is kept only as a salted hash protected by Windows for your account (never in the backup, never on the phone).
/// Forgot it: a one-time code goes to your personal email and to the Nova app on your paired phone. Designed by A.HAMROUNI
/// </summary>
static class AdminLock
{
    // ------------------------------------------------------------------ stored PIN (Store + a copy outside the settings file)

    static string CopyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova Enhance", "admin.lock");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("nova-admin-lock-v1");

    static string Stored
    {
        get
        {
            var s = Store.Secret("win_admin_pin");
            if (s.Length == 0)
                try { if (File.Exists(CopyPath)) s = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(CopyPath), Entropy, DataProtectionScope.CurrentUser)); } catch { }
            return s;
        }
        set
        {
            Store.SetSecret("win_admin_pin", value ?? "");
            try
            {
                if (string.IsNullOrEmpty(value)) { if (File.Exists(CopyPath)) File.Delete(CopyPath); }
                else { Directory.CreateDirectory(Path.GetDirectoryName(CopyPath)!); File.WriteAllBytes(CopyPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser)); }
            }
            catch (Exception e) { Log.Warn("admin lock", e.Message); }
        }
    }

    static string Hash(string pin, byte[] salt, int iter = 150_000) =>
        $"pbkdf2${iter}${Convert.ToBase64String(salt)}${Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(pin, salt, iter, HashAlgorithmName.SHA256, 32))}";

    static bool Check(string pin)
    {
        var s = Stored.Split('$');
        if (s.Length != 4 || !int.TryParse(s[1], out var iter)) return false;
        var mine = Hash(pin ?? "", Convert.FromBase64String(s[2]), iter).Split('$')[3];
        return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(mine), Convert.FromBase64String(s[3]));
    }

    /// <summary>The lock is on (a PIN is set).</summary>
    public static bool Enabled => Stored.Length > 0;

    public static string Email { get => Store.Str("win_admin_mail", ""); set => Store.Set("win_admin_mail", (value ?? "").Trim()); }

    /// <summary>Idle minutes before Nova locks itself (1–480, default 15).</summary>
    public static int IdleMinutes { get => Math.Clamp(Store.Int("win_admin_idle", 15), 1, 480); set => Store.SetInt("win_admin_idle", Math.Clamp(value, 1, 480)); }

    public static bool ValidPin(string pin) => pin != null && pin.Length >= 6 && pin.Length <= 12 && pin.All(char.IsDigit);

    /// <summary>Set or change the PIN (changing needs the lock open). Empty = lock off.</summary>
    public static string SetPin(string pin)
    {
        if (Enabled && Locked) return "🔐 Unlock first";
        if (string.IsNullOrEmpty(pin)) { Stored = ""; Note("🔐 admin lock switched off"); return "🔓 Admin lock is off"; }
        if (!ValidPin(pin)) return "⚠ The PIN needs 6 to 12 digits";
        Stored = Hash(pin, RandomNumberGenerator.GetBytes(16));
        unlocked = true; lastUse = DateTime.Now;
        Note("🔐 PIN set");
        return "🔐 PIN saved — Nova locks after " + IdleMinutes + " min idle, when Windows locks or sleeps, and when it starts";
    }

    // ------------------------------------------------------------------ locked / unlocked

    static bool unlocked;
    static DateTime lastUse = DateTime.Now;
    public static bool Locked => Enabled && !unlocked;

    /// <summary>Enhance / Nova for this PDF while locked: no Brain, emails, files, history or private words (set at the start of their work).</summary>
    static readonly AsyncLocal<bool> sandbox = new();
    public static bool InSandbox => sandbox.Value;
    public static void Sandbox() { if (Locked) sandbox.Value = true; }

    static readonly List<Form> hidden = new();

    /// <summary>🔐 Lock now: every Nova window except Enhance and the PDF panel is hidden until you unlock.</summary>
    public static void Lock(string why = "lock now")
    {
        if (!Enabled || !unlocked) return;
        unlocked = false;
        Alive(false);
        Note("🔐 locked · " + why);
        Program.UiContext?.Post(_ =>
        {
            try
            {
                foreach (Form f in Application.OpenForms.Cast<Form>().ToList())
                    if (f is NovaForm nf && nf.GetType() != typeof(NovaForm) && !nf.AllowedWhileLocked && f.Visible) { hidden.Add(f); f.Hide(); }
            }
            catch { }
        }, null);
    }

    static void Unlocked()
    {
        unlocked = true; lastUse = DateTime.Now;
        Alive(true);
        Note("🔓 unlocked");
        foreach (var f in hidden.ToList()) try { if (!f.IsDisposed) f.Show(); } catch { }
        hidden.Clear();
    }

    /// <summary>Asks for the PIN when locked. True = go ahead.</summary>
    public static bool Pass(string what = "")
    {
        if (!Locked) { lastUse = DateTime.Now; return true; }
        using var f = new PinForm(what);
        var ok = f.ShowDialog() == DialogResult.OK;
        if (ok) Unlocked();
        return ok;
    }

    static int fails; static DateTime waitUntil = DateTime.MinValue;

    /// <summary>Checks a typed PIN (5 wrong ones = wait 1 minute, then longer). Every wrong one is in 🧪 Activity.</summary>
    public static string TryPin(string pin)
    {
        if (DateTime.Now < waitUntil) return $"⏳ Too many wrong PINs — wait {(int)Math.Ceiling((waitUntil - DateTime.Now).TotalSeconds)} s";
        if (Check(pin)) { fails = 0; return ""; }
        fails++;
        Note($"⚠ wrong PIN ({fails})");
        if (fails >= 5) { waitUntil = DateTime.Now.AddMinutes(Math.Min(30, fails - 4)); return "⏳ Too many wrong PINs — wait a minute"; }
        return "⚠ Wrong PIN";
    }

    static void Note(string text) { try { using var j = Activity.Begin("🔐 admin lock", text); } catch { } }

    // ------------------------------------------------------------------ kept open across a Guardian restart

    /// <summary>“Open at this moment” (protected by Windows): a restart by the 🛡 Guardian within your idle time keeps Nova open.</summary>
    static void Alive(bool open)
    {
        try { Store.SetSecret("win_admin_alive", open ? DateTime.UtcNow.Ticks.ToString() : ""); } catch { }
    }

    static DateTime aliveAt = DateTime.MinValue;

    static bool WasOpenJustNow()
    {
        if (!long.TryParse(Store.Secret("win_admin_alive"), out var t)) return false;
        var ago = DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc);
        return ago >= TimeSpan.Zero && ago < TimeSpan.FromMinutes(IdleMinutes) && IdleMinutesNow() < IdleMinutes;
    }

    // ------------------------------------------------------------------ automatic locking

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    static double IdleMinutesNow()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return 0;
        return unchecked((uint)Environment.TickCount - li.dwTime) / 60000.0;
    }

    /// <summary>Started with Nova: locked at start, then on idle, Windows lock and sleep.</summary>
    public static void Start()
    {
        // A normal start (or a Windows restart) locks; a crash restart by the Guardian keeps Nova open if you were using it.
        unlocked = Enabled && Program.Restored && WasOpenJustNow();
        if (unlocked) { lastUse = DateTime.Now; Note("🔓 still open after the 🛡 Guardian restart"); } else Alive(false);
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect) Lock("Windows locked");
            // Your Windows password (or Hello) unlocked the laptop → Nova opens too (option C).
            else if (e.Reason is SessionSwitchReason.SessionUnlock && Enabled && !unlocked)
                Program.UiContext?.Post(_ => { if (!unlocked) { Unlocked(); Note("🔓 unlocked with Windows"); } }, null);
        };
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Suspend) Lock("sleep"); };
        var t = new System.Windows.Forms.Timer { Interval = 20_000 };
        t.Tick += (_, _) =>
        {
            if (!Enabled || !unlocked) return;
            // Idle = nobody touched the laptop for your idle time.
            if (IdleMinutesNow() >= IdleMinutes) Lock($"{IdleMinutes} min idle");
            else if (DateTime.Now - aliveAt > TimeSpan.FromSeconds(50)) { aliveAt = DateTime.Now; Alive(true); }   // still in use: a crash restart now keeps Nova open
        };
        t.Start();
    }

    // ------------------------------------------------------------------ forgot the PIN: one-time code (email + phone)

    static string code = ""; static DateTime codeUntil = DateTime.MinValue; static int codeFails; static DateTime codeBlockedUntil = DateTime.MinValue;

    /// <summary>The live code for the paired phone (Nova app → My PC → 🔐), "" when none.</summary>
    public static string PhoneCode() => code.Length > 0 && DateTime.Now < codeUntil ? code : "";
    public static DateTime CodeUntil => codeUntil;

    public static string SendCode()
    {
        if (DateTime.Now < codeBlockedUntil) return $"⏳ Recovery is blocked until {codeBlockedUntil:HH:mm} (3 wrong codes)";
        code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000");
        codeUntil = DateTime.Now.AddMinutes(10); codeFails = 0;
        Note("🔑 recovery code sent");
        var mail = Email.Length > 3 ? (MailCode(Email, code) ? "📧 sent to " + Mask(Email) : "⚠ the email could not be sent (Outlook closed?)") : "📧 no recovery email set";
        return $"{mail} · 📱 also in the Nova app on your phone → My PC → 🔐 · valid 10 minutes";
    }

    static string Mask(string e) { var at = e.IndexOf('@'); return at > 2 ? e[..2] + new string('•', at - 2) + e[at..] : e; }

    static bool MailCode(string to, string c)
    {
        try
        {
            dynamic app = Outlook.Running();
            if (app == null) return false;
            dynamic m = app.CreateItem(0);
            m.To = to;
            m.Subject = "Nova Enhance · PIN recovery code";
            m.Body = $"Your one-time code to reset the Nova Enhance PIN: {c}\n\nIt works once and expires in 10 minutes. If you did not ask for it, someone tried to reset the Nova PIN on your laptop.\n\nDesigned by A.HAMROUNI";
            m.DeleteAfterSubmit = true;   // no copy left in Sent Items
            m.Send();
            return true;
        }
        catch (Exception e) { Log.Warn("admin lock · recovery email", e.Message); return false; }
    }

    /// <summary>The code from the email / phone, then a new PIN.</summary>
    public static string Recover(string c, string newPin)
    {
        if (DateTime.Now < codeBlockedUntil) return $"⏳ Recovery is blocked until {codeBlockedUntil:HH:mm}";
        if (code.Length == 0 || DateTime.Now > codeUntil) return "⚠ No valid code — ask for a new one";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes((c ?? "").Trim().PadRight(6)[..6])))
        {
            codeFails++;
            Note($"⚠ wrong recovery code ({codeFails})");
            if (codeFails >= 3) { code = ""; codeBlockedUntil = DateTime.Now.AddHours(1); return "⛔ 3 wrong codes — recovery is blocked for an hour"; }
            return "⚠ Wrong code";
        }
        if (!ValidPin(newPin)) return "⚠ The new PIN needs 6 to 12 digits";
        code = "";
        Stored = Hash(newPin, RandomNumberGenerator.GetBytes(16));
        Note("🔑 PIN reset with the recovery code");
        return "";
    }
}

/// <summary>🔐 The PIN window (and ✉ Forgot PIN).</summary>
class PinForm : Form
{
    public PinForm(string what)
    {
        Text = "🔐 Nova Enhance is locked";
        BackColor = Th.Bg; ForeColor = Th.Text; Font = Th.F(10);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; TopMost = true; ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen; Width = 460; Height = 330;
        try { Icon = Th.AppIcon; } catch { }
        var s = new Stack(); Controls.Add(s);
        s.Add(Th.L("🔐 Enter your Nova PIN", 13, Th.Text, true));
        s.Add(Th.Wrap((what.Length > 0 ? what + " — " : "") + "only ✦ Enhance text and 📄 Nova for this PDF work while Nova is locked.", 9, Th.Muted));
        var pin = Th.Box(false); pin.UseSystemPasswordChar = true; pin.MaxLength = 12; pin.Width = 200; pin.Font = Th.F(14);
        s.Add(pin);
        var msg = s.Add(Th.L("", 9.5f, Th.Yellow));
        var row = s.Add(Th.Row());
        void Go()
        {
            var r = AdminLock.TryPin(pin.Text);
            if (r.Length == 0) { DialogResult = DialogResult.OK; return; }
            msg.Text = r; pin.SelectAll(); pin.Focus();
        }
        row.Controls.Add(Th.B("🔓 Unlock", Go, true));
        row.Controls.Add(Th.B("Cancel", () => DialogResult = DialogResult.Cancel));
        row.Controls.Add(Th.B("Forgot PIN?", () =>
        {
            msg.Text = AdminLock.SendCode();
            var c = Prompt.Ask(this, "🔑 Recovery code", "The 6-digit code from your email or the Nova app on your phone:");
            if (string.IsNullOrWhiteSpace(c)) return;
            var n = Prompt.Ask(this, "🔐 New PIN", "Your new PIN (6 to 12 digits):", "", true);
            if (n == null) return;
            var r = AdminLock.Recover(c, n.Trim());
            if (r.Length == 0) { msg.Text = "✓ New PIN saved"; DialogResult = DialogResult.OK; }
            else msg.Text = r;
        }));
        pin.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Go(); } };
        Shown += (_, _) => { Activate(); pin.Focus(); };
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Th.DarkTitle(this); }
}
