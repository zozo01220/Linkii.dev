using System.Runtime.InteropServices;

namespace Linkii.Player;

internal readonly record struct MonitorInfo(int Number, string Name, int X, int Y, int Width, int Height, bool Primary);

/// <summary>Moniteurs en pixels réels (le programme est « per-monitor DPI » : voir app.manifest).</summary>
internal static class Monitors
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size; public RECT Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool EnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    /// <summary>Numérotation : 1 = moniteur principal, puis de gauche à droite (et de haut en bas à position égale).</summary>
    public static List<MonitorInfo> All()
    {
        var found = new List<(string Name, RECT R, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr d) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(m, ref info)) found.Add((info.Device, info.Monitor, (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        var ordered = found.OrderByDescending(f => f.Primary).ThenBy(f => f.R.Left).ThenBy(f => f.R.Top).ToList();
        return ordered.Select((f, i) => new MonitorInfo(i + 1, f.Name, f.R.Left, f.R.Top, f.R.Right - f.R.Left, f.R.Bottom - f.R.Top, f.Primary)).ToList();
    }

    public static MonitorInfo Get(int number)
    {
        var all = All();
        return all.FirstOrDefault(m => m.Number == number) is { Width: > 0 } hit ? hit : all[0];   // numéro inconnu : le principal
    }
}

internal static class Win
{
    public static readonly IntPtr TopMost = new(-1), NoTopMost = new(-2);
    public const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001, ES_DISPLAY_REQUIRED = 0x00000002;

    /// <summary>Écran et session éveillés tant que le player tourne : pas de veille Windows, pas d'écran noir au bout de quelques minutes.</summary>
    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
}
