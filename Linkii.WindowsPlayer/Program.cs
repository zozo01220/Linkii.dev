using System.Diagnostics;
using System.Windows;

namespace Linkii.Player;

/// <summary>
/// Linkii Player pour Windows. Un lancement normal démarre un superviseur sans fenêtre qui lance et relance le processus d'affichage
/// (plantage, fenêtre figée, redémarrage demandé par le back-office). Une instance = un nom, un profil, un appairage.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var o = Options.Parse(args, out var error);
        if (error != null) { Info(error + "\n\n" + Options.Usage, true); return 2; }
        if (o.Help) { Info(Options.Usage); return 0; }
        if (o.ListMonitors)
        {
            Info(string.Join("\n", Monitors.All().Select(m => $"{m.Number}  {m.Name}  {m.Width} × {m.Height}  à ({m.X}, {m.Y}){(m.Primary ? "  principal" : "")}")));
            return 0;
        }

        var store = new InstanceStore(o.Instance);
        if (o.AutoStart != null)
        {
            AutoStart.Set(o.Instance, o.AutoStart == "on", o.Persistent);
            Log.Write(store, "démarrage automatique : " + o.AutoStart);
            if (o.AutoStart == "off") return 0;   // rien à lancer : on retire seulement l'entrée
        }

        // une seule fois par instance et par session ; le processus d'affichage (--child) appartient au superviseur déjà propriétaire
        Mutex? mutex = null;
        if (!o.Child)
        {
            mutex = new Mutex(true, @"Local\LinkiiPlayer." + o.Instance, out var created);
            if (!created) return 0;   // déjà lancée
        }

        if (o.Reset && !o.Child) { store.Reset(); Log.Write(store, "appairage oublié (--reset)"); }

        return o.Child || o.NoSupervisor ? RunWindow(o, store) : Supervise(o, store);
    }

    private static int Supervise(Options o, InstanceStore store)
    {
        Log.Write(store, $"superviseur {Device.Version}, instance {o.Instance}");
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

        var server = InstanceStore.NormalizeServer(o.Server) ?? InstanceStore.NormalizeServer(store.SavedServer) ?? InstanceStore.NormalizeServer(InstanceStore.InstalledServer());
        if (server == null)
        {
            server = InstanceStore.NormalizeServer(new ServerPrompt().Ask());
            if (server == null) return 0;   // annulé
        }
        if (store.SavedServer != server) store.SavedServer = server;

        return app.Run(new PlayerWindow(o, store, server));
    }

    private static void Info(string text, bool error = false) =>
        MessageBox.Show(text, "Linkii Player", MessageBoxButton.OK, error ? MessageBoxImage.Error : MessageBoxImage.Information);
}
