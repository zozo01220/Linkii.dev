using System.Runtime.InteropServices;

namespace Linkii.Player;

// Work : zone utile (sans la barre des tâches)
internal readonly record struct MonitorInfo(int Number, string Name, int X, int Y, int Width, int Height, bool Primary, int WorkX, int WorkY, int WorkWidth, int WorkHeight);

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
        var found = new List<(string Name, RECT R, RECT W, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr d) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(m, ref info)) found.Add((info.Device, info.Monitor, info.Work, (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        var ordered = found.OrderByDescending(f => f.Primary).ThenBy(f => f.R.Left).ThenBy(f => f.R.Top).ToList();
        return ordered.Select((f, i) => new MonitorInfo(i + 1, f.Name, f.R.Left, f.R.Top, f.R.Right - f.R.Left, f.R.Bottom - f.R.Top, f.Primary, f.W.Left, f.W.Top, f.W.Right - f.W.Left, f.W.Bottom - f.W.Top)).ToList();
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

/// <summary>
/// Verrou du clavier d'une borne interactive : bloque la touche Windows, Alt+Tab, Alt+Échap, Ctrl+Échap et Alt+F4 pendant que le player est au premier plan.
/// (Ctrl+Alt+Suppr reste la propriété de Windows : on ne peut pas le bloquer sans stratégie de groupe.) Le visiteur n'a de toute façon qu'un écran tactile.
/// </summary>
internal static class KeyLock
{
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct KBD { public uint VkCode, ScanCode, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);

    private const int WH_KEYBOARD_LL = 13, VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_F4 = 0x73, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_MENU = 0x12, VK_CONTROL = 0x11, VK_APPS = 0x5D;
    private static IntPtr hook;
    private static HookProc? proc;   // gardé : le ramasse-miettes ne doit pas le libérer

    public static bool On => hook != IntPtr.Zero;

    public static void Enable()
    {
        if (On) return;
        proc = (code, w, l) =>
        {
            if (code >= 0)
            {
                var k = Marshal.PtrToStructure<KBD>(l);
                var alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 || (k.Flags & 0x20) != 0;
                var ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                var v = (int)k.VkCode;
                if (v is VK_LWIN or VK_RWIN or VK_APPS || (alt && v is VK_TAB or VK_ESCAPE or VK_F4) || (ctrl && v == VK_ESCAPE)) return (IntPtr)1;
            }
            return CallNextHookEx(hook, code, w, l);
        };
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
    }

    public static void Disable()
    {
        if (!On) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero; proc = null;
    }
}
