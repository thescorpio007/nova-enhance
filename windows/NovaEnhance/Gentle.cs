using System.Runtime.InteropServices;

namespace NovaEnhance;

/// <summary>
/// Gentle indexing (Settings → Email & AI → Indexing): Outlook stays smooth.
///   · Working hours (default 08:00–18:00, Mon–Fri): only new mail and the last 14 days; the big backlog waits for the evening.
///   · Full speed when the PC is idle (no keyboard/mouse for 2 minutes) on mains power; slower while you work; slowest on battery.
///   · Outlook "busy" pauses are handled by the indexer itself. Designed by A.HAMROUNI
/// </summary>
static class Gentle
{
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static int StartHour { get => Math.Clamp(Store.Int("win_work_start", 8), 0, 23); set => Store.SetInt("win_work_start", value); }
    public static int EndHour { get => Math.Clamp(Store.Int("win_work_end", 18), 1, 24); set => Store.SetInt("win_work_end", value); }
    /// <summary>Working days as numbers 0 = Sunday … 6 = Saturday; default Monday–Friday.</summary>
    public static string Days { get => Store.Str("win_work_days", "1,2,3,4,5"); set => Store.Set("win_work_days", value); }
    public static bool On { get => Store.Bool("win_gentle", true); set => Store.SetBool("win_gentle", value); }

    public static bool WorkingNow
    {
        get
        {
            var now = DateTime.Now;
            var days = Days.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x.Trim(), out var d) ? d : -1);
            return days.Contains((int)now.DayOfWeek) && now.Hour >= StartHour && now.Hour < EndHour;
        }
    }

    public static TimeSpan Idle
    {
        get
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - li.dwTime));
        }
    }

    static bool OnBattery
    {
        get { try { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline; } catch { return false; } }
    }

    /// <summary>True when older mail (dated <paramref name="d"/>) should wait: working hours, not idle, older than 14 days.</summary>
    public static bool HoldBacklog(DateTime d) => On && WorkingNow && Idle < TimeSpan.FromMinutes(2) && d < DateTime.Now.AddDays(-14);

    /// <summary>Called every 25 emails while indexing.</summary>
    public static void Pause()
    {
        if (!On) { Thread.Sleep(3); return; }
        bool idle = Idle >= TimeSpan.FromMinutes(2);
        int ms = OnBattery ? (idle ? 400 : 1200) : idle ? 3 : WorkingNow ? 700 : 300;
        Thread.Sleep(ms);
    }
}
