using System.Diagnostics;
using System.Windows;

namespace Linkii.Player;

/// <summary>
/// Linkii Player pour Windows. Un lancement normal démarre un superviseur sans fenêtre qui lance et relance le processus d'affichage
/// (plantage, fenêtre figée, redémarrage demandé par le back-office). Une instance = un écran du back-office : un profil, un appairage.
/// Rien à régler sur le PC : l'adresse du serveur est intégrée, le reste (démarrage automatique, fenêtre, moniteur) vient du back-office.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var o = Options.Parse(args);
        if (o.Uninstall) { Uninstall(); return 0; }

        var store = new InstanceStore(o.Instance);

        // une seule fois par instance et par session ; le processus d'affichage (--child) appartient au superviseur déjà propriétaire
        Mutex? mutex = null;
        if (!o.Child)
        {
            mutex = new Mutex(true, @"Local\LinkiiPlayer." + o.Instance, out var created);
            if (!created) return 0;   // déjà lancée
        }

        return o.Child ? RunWindow(o, store) : Supervise(o, store);
    }

    private static int Supervise(Options o, InstanceStore store)
    {
        Log.Write(store, $"superviseur {Device.Version}, instance {o.Instance}");
        AutoStart.Set(o.Instance, store.Load().AutoStart, o.Persistent);   // démarrage automatique : oui par défaut, puis selon le back-office
        var crashes = new Queue<DateTime>();
        while (true)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var a in o.Persistent) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--child");
            using var child = Process.Start(psi)!;

            var frozen = 0;
            while (!child.WaitForExit(10_000))
            {
                // fenêtre qui ne répond plus pendant une minute : on la ferme, la boucle la relance
                var responding = true;
                try { responding = child.Responding; } catch { /* pas encore de fenêtre */ }
                frozen = responding ? 0 : frozen + 1;
                if (frozen >= 6) { Log.Write(store, "affichage figé : arrêt"); try { child.Kill(true); } catch { } break; }
            }
            child.WaitForExit();
            var code = child.ExitCode;
            Log.Write(store, $"affichage terminé, code {code}");
            if (code == PlayerWindow.ExitRemoved) { store.Delete(); return 0; }   // écran supprimé : l'instance disparaît du PC
            if (code is 0 or PlayerWindow.ExitNoRuntime) return code;   // fermé à la main, ou WebView2 à installer
            if (code == PlayerWindow.ExitRestart) continue;

            // plantage : relance après une courte pause, plus longue si cela se répète
            var now = DateTime.UtcNow;
            crashes.Enqueue(now);
            while (crashes.Count > 0 && now - crashes.Peek() > TimeSpan.FromMinutes(1)) crashes.Dequeue();
            Thread.Sleep(crashes.Count >= 5 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(2));
        }
    }

    private static int RunWindow(Options o, InstanceStore store)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write(store, "erreur : " + e.Exception);
            e.Handled = true;
            app.Shutdown(PlayerWindow.ExitBrowserCrash);   // le superviseur relance
        };
        return app.Run(new PlayerWindow(o, store, InstanceStore.Server()));
    }

    /// <summary>Désinstallation : les instances en cours s'arrêtent et plus aucune ne démarre avec la session.</summary>
    private static void Uninstall()
    {
        AutoStart.RemoveAll();
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath!)))
        {
            try { if (p.Id != me) p.Kill(true); } catch { }
            finally { p.Dispose(); }
        }
    }
}
