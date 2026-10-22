using System.Net;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Linkii.Player;

/// <summary>
/// Fenêtre du player : le player web (/player) dans WebView2, plein écran sur un moniteur ou fenêtre de test.
/// Le profil du navigateur est propre à l'instance : appairage, playlist et cache hors ligne ne se mélangent pas entre instances.
/// </summary>
internal sealed class PlayerWindow : Window
{
    public const int ExitRestart = 42;       // redemandé par le back-office : le superviseur relance tout de suite
    public const int ExitNoRuntime = 3;      // WebView2 absent : inutile de relancer
    public const int ExitBrowserCrash = 10;  // le navigateur a planté : le superviseur relance

    // Fonctions natives proposées au player. « sync » n'y est pas : le player reste en mode navigateur (cache du navigateur, indicateur de publication).
    private const string InitScript = @"(function () {
  try {
    var h = window.chrome && window.chrome.webview && window.chrome.webview.hostObjects;
    if (!h) return;
    window.LinkiiNative = {
      deviceInfo: function () { return h.sync.bridge.DeviceInfo(); },
      restart: function () { h.bridge.Restart(); }
    };
  } catch (e) {}
})();";

    private readonly Options options;
    private readonly InstanceStore store;
    private readonly string server;
    private readonly string playerUrl;
    private readonly WebView2 web = new();
    private readonly DispatcherTimer retry = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool offlinePage;

    public PlayerWindow(Options options, InstanceStore store, string server)
    {
        this.options = options; this.store = store; this.server = server;
        playerUrl = InstanceStore.PlayerUrl(server);

        Title = options.Instance == "default" ? "Linkii Player" : $"Linkii Player – {options.Instance}";
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x1F, 0x3A));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = web;
        web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0B, 0x1F, 0x3A);

        if (options.Kiosk)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            Cursor = System.Windows.Input.Cursors.None;
        }
        else { Width = 1280; Height = 720; }

        SourceInitialized += (_, _) => ApplyBounds();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyBounds);   // un moniteur d'une autre échelle ne doit pas redimensionner la fenêtre
        Loaded += async (_, _) => await InitWebAsync();
        Closed += (_, _) => Win.KeepAwake(false);
        retry.Tick += (_, _) => { offlinePage = false; web.CoreWebView2?.Navigate(playerUrl); };
        Win.KeepAwake(true);
    }

    /// <summary>Position et taille en pixels réels : le moniteur choisi en entier, ou la fenêtre de test demandée.</summary>
    private void ApplyBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (options.Window is { } w) { Win.SetWindowPos(hwnd, Win.NoTopMost, w.X, w.Y, w.W, w.H, Win.SWP_NOACTIVATE | Win.SWP_SHOWWINDOW); return; }
        var m = Monitors.Get(options.Monitor);
        if (options.Kiosk) { Win.SetWindowPos(hwnd, Win.TopMost, m.X, m.Y, m.Width, m.Height, Win.SWP_NOACTIVATE | Win.SWP_SHOWWINDOW); return; }
        var cw = Math.Min(1280, m.Width - 80); var ch = Math.Min(720, m.Height - 120);   // fenêtre de test sans position : centrée sur le moniteur
        Win.SetWindowPos(hwnd, Win.NoTopMost, m.X + (m.Width - cw) / 2, m.Y + (m.Height - ch) / 2, cw, ch, Win.SWP_NOACTIVATE | Win.SWP_SHOWWINDOW);
    }

    private async Task InitWebAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, store.WebViewDir,
                new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));   // un écran n'a personne pour toucher la page : la vidéo doit démarrer seule
            await web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Log.Write(store, "WebView2 absent");
            MessageBox.Show("Linkii Player a besoin de Microsoft Edge WebView2 Runtime.\n\nInstallez-le depuis https://developer.microsoft.com/microsoft-edge/webview2/ puis relancez le player.",
                "Linkii Player", MessageBoxButton.OK, MessageBoxImage.Warning);
            Application.Current.Shutdown(ExitNoRuntime);
            return;
        }
        catch (Exception ex)
        {
            Log.Write(store, "navigateur : " + ex.Message);
            Application.Current.Shutdown(ExitBrowserCrash);
            return;
        }

        var core = web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = options.DevTools;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = options.DevTools || !options.Kiosk;   // en kiosque : ni F5, ni Ctrl+W, ni recherche

        core.AddHostObjectToScript("bridge", new Bridge(options.Instance, () => Dispatcher.BeginInvoke(() => Application.Current.Shutdown(ExitRestart))));
        await core.AddScriptToExecuteOnDocumentCreatedAsync(InitScript);

        core.NewWindowRequested += (_, e) => e.Handled = true;   // le player n'ouvre jamais de fenêtre
        core.NavigationCompleted += (_, e) =>
        {
            if (offlinePage) return;   // c'est la page d'attente qui vient de se charger
            if (e.IsSuccess) retry.Stop(); else ShowOffline(e.WebErrorStatus.ToString());
        };
        core.ProcessFailed += (_, e) =>
        {
            Log.Write(store, "navigateur : " + e.ProcessFailedKind);
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) Dispatcher.BeginInvoke(() => Application.Current.Shutdown(ExitBrowserCrash));
            else Dispatcher.BeginInvoke(() => core.Reload());   // page figée ou plantée : on la recharge, le player reprend sa playlist en cache
        };

        Log.Write(store, $"ouverture {playerUrl}");
        core.Navigate(playerUrl);
    }

    /// <summary>Serveur injoignable au démarrage : message sobre et nouvelle tentative toutes les 10 s (ensuite le player gère seul le mode hors ligne).</summary>
    private void ShowOffline(string reason)
    {
        Log.Write(store, $"serveur injoignable ({reason})");
        offlinePage = true;
        var host = WebUtility.HtmlEncode(new Uri(server).Host);
        web.CoreWebView2.NavigateToString(
            "<html><body style=\"margin:0;height:100vh;display:grid;place-items:center;background:#0B1F3A;color:#fff;font-family:'Segoe UI',sans-serif\">" +
            "<div style=\"text-align:center\"><div style=\"font-size:4vmin;font-weight:500\">linkii</div>" +
            $"<p style=\"font-size:2vmin;opacity:.8\">Connexion à {host} impossible. Nouvelle tentative en cours…</p></div></body></html>");
        retry.Start();
    }
}
