using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Linkii.Player;

/// <summary>
/// Alimentation du PC, à la demande du back-office : redémarrer, éteindre, couper ou rallumer les moniteurs (la veille de l'écran).
/// Un PC peut afficher plusieurs écrans (une instance du player chacun) : ses moniteurs ne s'éteignent que quand toutes les instances dorment.
/// </summary>
internal static class PcPower
{
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    private const uint WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170, MOUSEEVENTF_MOVE = 0x0001;
    private static readonly IntPtr Broadcast = new(0xFFFF);

    public static void Reboot() => Shutdown("/r");
    public static void Off() => Shutdown("/s");

    /// <summary>shutdown.exe : /f ferme les applications qui retiennent l'arrêt (un poste d'affichage n'a rien à sauvegarder), 2 s pour laisser partir l'accusé.</summary>
    private static void Shutdown(string flag)
    {
        try { Process.Start(new ProcessStartInfo("shutdown.exe", $"{flag} /f /t 2") { UseShellExecute = false, CreateNoWindow = true }); }
        catch { /* droit refusé : la commande sera sans suite côté back-office */ }
    }

    private static EventWaitHandle? mine;

    private static string EventName(string instance) => @"Local\LinkiiPlayer.asleep." + instance;

    private static bool Asleep(string instance) => EventWaitHandle.TryOpenExisting(EventName(instance), out var h) && h.WaitOne(0);

    /// <summary>Instance qui s'endort (false) ou se réveille (true) : les moniteurs suivent, une fois toutes les instances du PC d'accord.</summary>
    public static void Display(string instance, bool on)
    {
        try
        {
            mine ??= new EventWaitHandle(false, EventResetMode.ManualReset, EventName(instance));
            if (on)
            {
                mine.Reset();
                mouse_event(MOUSEEVENTF_MOVE, 0, 0, 0, UIntPtr.Zero);   // un mouvement fictif rallume les moniteurs
                PostMessage(Broadcast, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(-1));
                return;
            }
            mine.Set();
            var others = InstanceStore.All().Select(i => i.Name).Append("default").Distinct().Where(n => n != instance);
            if (others.Any(n => Launcher.Running(n) && !Asleep(n))) return;   // un autre écran de ce PC est éveillé : il a besoin du moniteur
            PostMessage(Broadcast, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2));
        }
        catch { /* le fond noir du player tient lieu de veille */ }
    }
}
