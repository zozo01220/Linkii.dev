using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Linkii.Player;

/// <summary>
/// Fenêtre du player : le player web (/player) dans WebView2, plein écran sur un moniteur ou fenêtre de test (paramètres de l'écran, back-office).
/// Le profil du navigateur est propre à l'instance : appairage, playlist et cache hors ligne ne se mélangent pas entre instances.
/// </summary>
internal sealed class PlayerWindow : Window
{
    public const int ExitRestart = 42;       // redemandé par le back-office : le superviseur relance tout de suite
    public const int ExitRemoved = 43;       // écran supprimé dans le back-office : le superviseur efface l'instance et s'arrête
    public const int ExitNoRuntime = 3;      // WebView2 absent : inutile de relancer
    public const int ExitBrowserCrash = 10;  // le navigateur a planté : le superviseur relance

    // Fonctions natives proposées au player. « sync » n'y est pas : le player reste en mode navigateur (cache du navigateur, indicateur de publication).
    private const string InitScript = @"(function () {
  try {
    var h = window.chrome && window.chrome.webview && window.chrome.webview.hostObjects;
    if (!h) return;
    window.LinkiiNative = {
      deviceInfo: function () { return h.sync.bridge.DeviceInfo(); },
      restart: function () { h.bridge.Restart(); },
      config: function (json) { h.bridge.Config(json); },
      unpaired: function () { return h.sync.bridge.Unpaired(); }
    };
  } catch (e) {}
})();";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly Options options;
    private readonly InstanceStore store;
    private readonly string server;
    private readonly string playerUrl;
    private readonly WebView2 web = new();
    private readonly DispatcherTimer retry = new() { Interval = TimeSpan.FromSeconds(10) };
    private InstanceSettings settings;
    private bool offlinePage;

    public PlayerWindow(Options options, InstanceStore store, string server)
    {
        this.options = options; this.store = store; this.server = server;
        settings = store.Load();
        playerUrl = InstanceStore.PlayerUrl(server);

        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x1F, 0x3A));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = web;
        web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0B, 0x1F, 0x3A);
        ApplyStyle();

        SourceInitialized += (_, _) => ApplyBounds();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyBounds);   // un moniteur d'une autre échelle ne doit pas redimensionner la fenêtre
        Loaded += async (_, _) => await InitWebAsync();
        Closed += (_, _) => Win.KeepAwake(false);
        retry.Tick += (_, _) => { offlinePage = false; web.CoreWebView2?.Navigate(playerUrl); };
        Win.KeepAwake(true);
    }

    /// <summary>Plein écran (kiosque) ou fenêtre de test, selon les paramètres de l'écran.</summary>
    private void ApplyStyle()
    {
        Title = settings.Name is { Length: > 0 } n ? $"Linkii Player – {n}" : "Linkii Player";
        var kiosk = !settings.Windowed;
        WindowStyle = kiosk ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = kiosk ? ResizeMode.NoResize : ResizeMode.CanResize;
        Topmost = kiosk && settings.Configured;   // code d'appairage : pas au premier plan, on peut passer au navigateur (Alt+Tab) pour le saisir
        Cursor = kiosk ? System.Windows.Input.Cursors.None : null;
        if (web.CoreWebView2 is { } core) core.Settings.AreBrowserAcceleratorKeysEnabled = options.DevTools || !kiosk;   // en kiosque : ni F5, ni Ctrl+W, ni recherche
    }

    /// <summary>Position et taille en pixels réels : le moniteur choisi en entier, ou une fenêtre à la taille de la résolution (réduite si le moniteur est plus petit).</summary>
    private void ApplyBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var m = Monitors.Get(settings.Monitor);
        if (!settings.Windowed) { Win.SetWindowPos(hwnd, Topmost ? Win.TopMost : Win.NoTopMost, m.X, m.Y, m.Width, m.Height, Win.SWP_NOACTIVATE | Win.SWP_SHOWWINDOW); return; }
        double w = Math.Max(320, settings.Width), h = Math.Max(180, settings.Height);
        var k = Math.Min(1, Math.Min((m.WorkWidth - 80) / w, (m.WorkHeight - 120) / h));
        int cw = (int)(w * k), ch = (int)(h * k);

        // Petites fenêtres (résolution « Test ») : rangées en grille sur le moniteur, une case par écran de ce PC, sans se chevaucher (2 × 2 en 960 × 540).
        var cols = Math.Max(1, (int)Math.Round(m.WorkWidth / (double)cw));
        var rows = Math.Max(1, (int)Math.Round(m.WorkHeight / (double)ch));
        if (cols * rows > 1)
        {
            var g = Math.Min(1, Math.Min(m.WorkWidth / (double)(cols * cw), m.WorkHeight / (double)(rows * ch)));
            cw = (int)(cw * g); ch = (int)(ch * g);
            var slot = Slot(m.Number) % (cols * rows);
            Win.SetWindowPos(hwnd, Win.NoTopMost, m.WorkX + slot % cols * cw, m.WorkY + slot / cols * ch, cw, ch, Win.SWP_NOACTIVATE | Win.SWP_SHOWWINDOW);
            return;
        }
        var shift = options.Instance == "default" ? 0 : 40 * (1 + options.Instance.Sum(c => c) % 5);   // grandes fenêtres sur un moniteur : décalées
        var x = Math.Clamp(m.WorkX + (m.WorkWidth - cw) / 2 + shift, m.WorkX, m.WorkX + m.WorkWidth - cw);
        var y = Math.Clamp(m.WorkY + (m.WorkHeight - ch) / 2 + shift, m.WorkY, m.WorkY + m.WorkHeight - ch);
        Win.SetWindowPos(hwnd, Win.NoTopMost, x, y, cw, ch, Win.SWP_NOACTIVATE | Win.SWP_SHOWWINDOW);
    }

    /// <summary>Case de cette fenêtre dans la grille : celle déjà prise, sinon la première libre parmi les écrans « en fenêtre » du même moniteur (gardée).</summary>
    private int Slot(int monitor)
    {
        // au démarrage de Windows, toutes les instances se placent en même temps : une à la fois, pour ne pas prendre la même case
        using var turn = new Mutex(false, @"Local\LinkiiPlayer.slots");
        try { turn.WaitOne(3000); } catch (AbandonedMutexException) { }
        try { return PickSlot(monitor); } finally { try { turn.ReleaseMutex(); } catch (ApplicationException) { } }
    }

    private int PickSlot(int monitor)
    {
        var taken = InstanceStore.All().Where(i => i.Name != options.Instance && i.Settings.Windowed && i.Settings.Slot is not null && Monitors.Get(i.Settings.Monitor).Number == monitor)
            .Select(i => i.Settings.Slot!.Value).ToHashSet();
        if (settings.Slot is { } mine && !taken.Contains(mine)) return mine;
        var free = 0;
        while (taken.Contains(free)) free++;
        settings.Slot = free;
        store.Save(settings);
        return free;
    }

    private sealed record OpenDto(string? Instance, string? Token, string? Name, bool AutoStart, bool Windowed, int Monitor, int Width, int Height);
    private sealed record ConfigDto(string? Name, bool AutoStart, bool Windowed, int Monitor, int Width, int Height, List<OpenDto>? Open);

    /// <summary>Paramètres de l'appareil reçus du back-office : gardés, appliqués aussitôt (démarrage automatique, fenêtre, moniteur), écrans ajoutés ouverts.</summary>
    private void OnConfig(string json)
    {
        ConfigDto? c;
        try { c = JsonSerializer.Deserialize<ConfigDto>(json, Json); } catch { return; }
        if (c == null) return;
        var next = new InstanceSettings
        {
            Configured = true, Slot = settings.Slot, Name = c.Name, AutoStart = c.AutoStart, Windowed = c.Windowed, Monitor = Math.Max(1, c.Monitor),
            Width = c.Width > 0 ? c.Width : settings.Width, Height = c.Height > 0 ? c.Height : settings.Height
        };   // Token : plus besoin, l'écran est connecté
        var display = !next.SameDisplay(settings) || next.Name != settings.Name || !settings.Configured;   // premier appairage : le plein écran passe au premier plan
        var changed = display || next.AutoStart != settings.AutoStart || settings.Token != null;
        settings = next;
        if (changed)
        {
            store.Save(settings);
            AutoStart.Set(options.Instance, settings.AutoStart, options.Persistent);
            Log.Write(store, $"paramètres reçus : démarrage auto {(settings.AutoStart ? "oui" : "non")}, {(settings.Windowed ? $"fenêtre {settings.Width}×{settings.Height}" : "plein écran")}, moniteur {settings.Monitor}");
        }
        if (display)
        {
            ApplyStyle();
            Dispatcher.BeginInvoke(ApplyBounds, DispatcherPriority.ApplicationIdle);   // après le changement de style : WPF replace la fenêtre à sa façon, on la remet à sa place ensuite
        }

        foreach (var o in c.Open ?? new())
        {
            if (o.Instance is not { Length: > 0 } inst || o.Token is not { Length: > 0 } tok || !System.Text.RegularExpressions.Regex.IsMatch(inst, "^[A-Za-z0-9_-]{1,40}$")) continue;
            Launcher.Open(inst, new InstanceSettings { Name = o.Name, Token = tok, AutoStart = o.AutoStart, Windowed = o.Windowed, Monitor = Math.Max(1, o.Monitor), Width = o.Width, Height = o.Height });
        }
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
        core.Settings.AreBrowserAcceleratorKeysEnabled = options.DevTools || settings.Windowed;

        core.AddHostObjectToScript("bridge", new Bridge(options.Instance,
            () => Dispatcher.BeginInvoke(() => Application.Current.Shutdown(ExitRestart)),
            json => Dispatcher.BeginInvoke(() => OnConfig(json)),
            () =>
            {
                Log.Write(store, "écran supprimé dans le back-office : fermeture");
                AutoStart.Set(options.Instance, false, options.Persistent);
                Dispatcher.BeginInvoke(() => Application.Current.Shutdown(ExitRemoved));
            }));
        await core.AddScriptToExecuteOnDocumentCreatedAsync(InitScript);
        if (settings.Token is { } token)
        {
            // écran ajouté depuis le back-office : déjà appairé, le player démarre directement sur sa liste de lecture
            var origin = new Uri(server).GetLeftPart(UriPartial.Authority);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                $"try {{ if (location.origin === {JsonSerializer.Serialize(origin)}) localStorage.setItem('lk_token', {JsonSerializer.Serialize(token)}); }} catch (e) {{}}");
        }

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
