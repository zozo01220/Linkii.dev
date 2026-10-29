using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Linkii.Relay;

/// <summary>
/// Installateur Windows intégré au relais (DEV-POC.md §139) : le fichier téléchargé dans Linkii (LinkiiRelay-Setup.exe) est le relais lui-même.
/// Double-clic : il demande l'autorisation administrateur, puis le code d'installation, et fait tout le reste : appairage, copie dans
/// « Program Files\Linkii Relay », service Windows « LinkiiRelay » (démarrage automatique, redémarré en cas de panne), entrée dans
/// « Applications et fonctionnalités ». Relancé sur un poste déjà installé : réinstalle ou désinstalle.
///   install | uninstall | wizard (défaut)    /code=ABCD-2345  /server=https://…  /dir=C:\dossier  /silent
/// </summary>
[SupportedOSPlatform("windows")]
public static class Installer
{
    public const string ServiceName = "LinkiiRelay";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LinkiiRelay";
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Linkii Relay");

    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Valeur d'une option : « /code=X », « --code=X » ou « --code X ».</summary>
    private static string? Opt(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            foreach (var p in new[] { "/" + name + "=", "--" + name + "=" })
                if (a.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return a[p.Length..].Trim('"');
            if (a.Equals("--" + name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }

    private static bool Has(string[] args, string name) => args.Any(a => a.Equals("/" + name, StringComparison.OrdinalIgnoreCase) || a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));

    private static string Quote(string a) => a.Length > 0 && a.IndexOfAny(new[] { ' ', '"', '\t' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    private static void Say(string s = "") => Console.WriteLine(s);
    private static void Ok(string s) => Console.WriteLine("  OK    " + s);
    private static void Fail(string s) => Console.WriteLine("  ERREUR  " + s);

    private static string? Ask(string label, bool silent, string? def = null)
    {
        if (silent) return def;
        Console.Write(def == null ? $"{label} : " : $"{label} [{def}] : ");
        var s = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(s) ? def : s;
    }

    private static void Pause(bool silent)
    {
        if (silent) return;
        Say();
        Console.Write("Appuyez sur Entrée pour fermer cette fenêtre.");
        Console.ReadLine();
    }

    public static async Task<int> Run(string[] args, string cmd)
    {
        var silent = Has(args, "silent") || Console.IsInputRedirected;
        var rest = args.Length > 0 && args[0].Equals(cmd, StringComparison.OrdinalIgnoreCase) ? args.Skip(1).ToArray() : args;
        Console.Title = "Relais Linkii";

        if (!IsAdmin())   // même programme, relancé avec les droits administrateur (fenêtre d'autorisation de Windows)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = string.Join(' ', new[] { cmd }.Concat(rest).Select(Quote)) });
                return 0;
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                Say("L'autorisation administrateur a été refusée : rien n'a été installé.");
                Pause(silent);
                return 1;
            }
        }

        var dir = Opt(rest, "dir") ?? InstalledDir() ?? DefaultDir;
        int code;
        try
        {
            if (cmd == "wizard")
            {
                Say("=== Relais Linkii ===");
                Say();
                if (ServiceExists() || File.Exists(Path.Combine(dir, "Linkii.Relay.exe")))
                {
                    Say($"Le relais est déjà installé dans {dir}.");
                    Say("  1  Réinstaller ou le ré-appairer avec un nouveau code");
                    Say("  2  Le désinstaller");
                    Say("  3  Quitter");
                    var pick = Ask("Votre choix", silent, "1");
                    cmd = pick == "2" ? "uninstall" : pick == "3" ? "quit" : "install";
                }
                else cmd = "install";
            }
            code = cmd == "uninstall" ? Uninstall(dir, silent || Has(rest, "yes")) : cmd == "quit" ? 0 : await Install(rest, dir, silent);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            Say();
            Fail(e.Message);
            code = 1;
        }
        Pause(silent);
        return code;
    }

    // ---------- installation ----------

    private static async Task<int> Install(string[] args, string dir, bool silent)
    {
        var exe = Environment.ProcessPath!;
        var target = Path.Combine(dir, "Linkii.Relay.exe");
        RelayConfig.Dir = dir;
        var current = RelayConfig.Load();
        var paired = current.Token.Length > 0 && current.Server.Length > 0;

        Say("=== Installation du relais Linkii ===");
        Say();
        Say("Dans Linkii : Intégrations › Sources de données › Relais › Installer un relais.");
        Say("Linkii y affiche un code d'installation de 8 caractères (valable une heure).");
        Say();

        // 1. Appairage d'abord : si le code est refusé, rien n'est touché sur ce poste.
        var server = (Opt(args, "server") ?? RelayPairing.ServerOf(exe) ?? (paired ? current.Server : null))?.TrimEnd('/');
        var code = Opt(args, "code");
        PairResult? pairing = null;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        for (var attempt = 0; attempt < 4 && pairing == null; attempt++)
        {
            if (code == null)
            {
                if (silent) { if (paired) break; Fail("Indiquez le code : /code=ABCD-2345"); return 2; }
                code = Ask(paired ? "Code d'installation (Entrée : garder l'appairage actuel)" : "Code d'installation", silent);
                if (code == null && paired) break;
                if (code == null) continue;
            }
            if (server == null)
            {
                if (silent) { Fail("Indiquez le serveur : /server=https://…"); return 2; }
                server = Ask("Adresse du serveur Linkii (exemple : https://linkii.exemple.ch)", silent)?.TrimEnd('/');
                if (server == null) { code = null; continue; }
            }
            var (r, err) = await RelayPairing.PairAsync(http, server, code, null);
            if (r != null) { pairing = new PairResult(server, r.Token, r.Name); break; }
            Fail(err!);
            if (silent) return 1;
            Say("Vérifiez le code dans Linkii (ou créez-en un nouveau), puis réessayez.");
            Say();
            code = null;
            if (err!.Contains("Adresse du serveur") || err.Contains("injoignable")) server = null;   // l'adresse est peut-être à corriger
        }
        if (pairing == null && !paired) { Say("Installation annulée."); return 1; }
        if (pairing != null) Ok($"Relais « {pairing.Name} » appairé avec {pairing.Server}");
        else Ok($"Appairage actuel conservé ({current.Name})");

        // 2. Arrêt du service et des processus du relais, puis copie du programme.
        var existed = ServiceExists();
        StopService();
        Directory.CreateDirectory(dir);
        if (!string.Equals(Path.GetFullPath(exe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            for (var i = 0; ; i++)
            {
                try { File.Copy(exe, target, true); break; }
                catch (IOException) when (i < 20) { Thread.Sleep(500); }   // le service vient de s'arrêter : le fichier se libère
            }
        }
        Ok($"Programme copié dans {dir}");

        // 3. Configuration (jeton), lisible seulement par le système et les administrateurs.
        var cfg = current;
        if (pairing != null) { cfg.Server = pairing.Server; cfg.Token = pairing.Token; cfg.Name = pairing.Name; }
        cfg.Save();
        Exec("icacls", RelayConfig.PathOf, "/inheritance:r", "/grant:r", "*S-1-5-18:(F)", "*S-1-5-32-544:(F)");
        Ok("Configuration enregistrée");

        // 4. Service Windows : démarrage automatique, redémarré tout seul en cas de panne.
        var bin = "\"" + target + "\"";
        var (rc, outp) = existed
            ? Exec("sc.exe", "config", ServiceName, "binPath=", bin, "start=", "auto", "DisplayName=", "Relais Linkii")
            : Exec("sc.exe", "create", ServiceName, "binPath=", bin, "start=", "auto", "DisplayName=", "Relais Linkii");
        if (rc == 1072 || rc == 1073 && existed)
        {
            Fail("Windows n'a pas fini de supprimer l'ancien service LinkiiRelay : fermez la fenêtre « Services » (services.msc) et le Gestionnaire des tâches,");
            Fail("ou redémarrez le PC, puis relancez cet installateur.");
            return 1;
        }
        if (rc != 0) { Fail("Le service n'a pas pu être créé : " + outp.Trim()); return 1; }
        Exec("sc.exe", "description", ServiceName, "Lit les bases de données et API du réseau pour Linkii (connexion sortante seulement).");
        Exec("sc.exe", "failure", ServiceName, "reset=", "86400", "actions=", "restart/15000/restart/15000/restart/60000");
        Ok(existed ? "Service LinkiiRelay mis à jour" : "Service LinkiiRelay créé (démarrage automatique)");

        // 5. « Applications et fonctionnalités » : désinstallation depuis Windows.
        using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Relais Linkii");
            k.SetValue("DisplayVersion", RelayWorker.Version);
            k.SetValue("Publisher", "Linkii");
            k.SetValue("InstallLocation", dir);
            k.SetValue("DisplayIcon", target);
            k.SetValue("UninstallString", $"\"{target}\" uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(target).Length / 1024), RegistryValueKind.DWord);
        }

        // 6. Démarrage.
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25));
            Thread.Sleep(2500);
            sc.Refresh();
            if (sc.Status != ServiceControllerStatus.Running) throw new InvalidOperationException("Le service s'est arrêté juste après son démarrage. Lancez « \"" + target + "\" run » dans une console pour voir pourquoi.");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException) { throw new InvalidOperationException("Le service ne démarre pas : " + (e.InnerException?.Message ?? e.Message)); }
        Ok("Service démarré");

        Say();
        Say("Terminé. Retournez dans Linkii : le relais passe « En ligne » dans quelques secondes.");
        Say("Pour le désinstaller : Applications et fonctionnalités › Relais Linkii (ou relancez cet installateur).");
        return 0;
    }

    // ---------- désinstallation ----------

    private static int Uninstall(string dir, bool yes)
    {
        Say("=== Désinstallation du relais Linkii ===");
        Say();
        if (!yes)
        {
            var a = Ask($"Supprimer le relais installé dans {dir} ? (o/N)", false, "n");
            if (a is not ("o" or "O" or "oui" or "Oui" or "y" or "Y")) { Say("Rien n'a été supprimé."); return 0; }
        }
        StopService();
        Ok("Service arrêté");
        var stuck = false;
        if (ServiceExists())
        {
            var (rc, outp) = Exec("sc.exe", "delete", ServiceName);
            if (rc == 1072) stuck = true;   // déjà supprimé, mais un programme (services.msc…) le garde ouvert
            else if (rc != 0) throw new InvalidOperationException("Le service n'a pas pu être supprimé : " + outp.Trim());
            else Ok("Service supprimé");
        }
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);

        // Le programme en cours peut tourner depuis ce dossier : il est effacé par un petit script, une fois cette fenêtre fermée.
        if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo("cmd.exe") { Arguments = $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{dir}\"", CreateNoWindow = true, UseShellExecute = false });
            Ok($"Dossier {dir} supprimé dans quelques secondes");
        }
        Say();
        Say("Pensez à supprimer aussi le relais dans Linkii (Intégrations › Sources de données › Relais).");
        if (stuck) Say("Windows garde le service dans sa liste tant que la fenêtre « Services » ou le Gestionnaire des tâches est ouvert : fermez-les.");
        return 0;
    }

    // ---------- utilitaires ----------

    private static string? InstalledDir()
    {
        using var k = Registry.LocalMachine.OpenSubKey(UninstallKey);
        return k?.GetValue("InstallLocation") as string is { Length: > 0 } d ? d : null;
    }

    private static bool ServiceExists()
    {
        try { using var sc = new ServiceController(ServiceName); _ = sc.Status; return true; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Arrête le service (25 s au plus), puis tout processus du relais resté ouvert (hors celui-ci).</summary>
    private static void StopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending or ServiceControllerStatus.Paused) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25));
        }
        catch (InvalidOperationException) { }   // pas de service
        catch (System.ServiceProcess.TimeoutException) { }
        foreach (var p in Process.GetProcessesByName("Linkii.Relay").Where(p => p.Id != Environment.ProcessId))
        {
            try { p.Kill(true); p.WaitForExit(5000); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
        }
    }

    private static (int Code, string Output) Exec(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }
}

internal record PairResult(string Server, string Token, string Name);
