using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace Linkii.Relay;

/// <summary>
/// Assistant d'installation Windows du relais (« Suivant, Suivant… ») : Bienvenue › Code d'installation › Dossier › Prêt › Progression.
/// Le code est vérifié auprès de Linkii dès l'étape « Code » : un code refusé se corrige tout de suite, sans rien toucher sur le poste.
/// Même logique que l'installation en ligne de commande (<see cref="Installer.InstallCore"/> et <see cref="Installer.UninstallCore"/>).
/// Compilé seulement pour Windows (dossier Gui, voir Linkii.Relay.csproj). Option « /noelevate » : affichage sans droits administrateur (essais).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SetupWizard : Form
{
    private static readonly Color Nuit = Color.FromArgb(0x0B, 0x1F, 0x3A), Signal = Color.FromArgb(0x3B, 0x82, 0xC4);
    private static readonly Color Texte = Color.FromArgb(0x1B, 0x25, 0x33), Discret = Color.FromArgb(0x5B, 0x67, 0x78), Rouge = Color.FromArgb(0xB4, 0x2B, 0x2B);

    private enum Step { Welcome, Code, Folder, Ready, Progress }

    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    /// <summary>Ouvre l'assistant (relance d'abord le programme avec les droits administrateur). Rend le code de sortie.</summary>
    public static int Run(string[] args)
    {
        var noElevate = args.Any(a => a.Equals("/noelevate", StringComparison.OrdinalIgnoreCase));
        Installer.Quiet = true;
        FreeConsole();   // double-clic : pas de fenêtre noire (lancé depuis une console, celle-ci reste ouverte)
        if (!noElevate && !Installer.IsAdmin())
        {
            try { using var _ = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = "wizard" }); return 0; }
            catch (Win32Exception e)
            {
                MessageBox.Show(e.NativeErrorCode == 1223
                    ? "L'autorisation administrateur a été refusée : rien n'a été installé."
                    : $"Impossible d'obtenir les droits administrateur ({e.Message}).\n\nOuvrez PowerShell « en tant qu'administrateur » et lancez :\n\"{Environment.ProcessPath}\" install",
                    "Relais Linkii", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
        var code = 0;
        var t = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SetupWizard());
            }
            catch (Exception e)
            {
                Installer.Log(e.ToString());
                MessageBox.Show($"Une erreur est survenue : {e.Message}\n\nDétails techniques : {Installer.LogFile}", "Relais Linkii", MessageBoxButtons.OK, MessageBoxIcon.Error);
                code = 1;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return code;
    }

    // ---------- état ----------
    private Step step = Step.Welcome;
    private bool uninstall, working, done;
    private PairResult? pairing;
    private string pairedWith = "";
    private readonly string? serverFromFile = RelayPairing.ServerOf(Environment.ProcessPath!);
    private readonly bool installed = Installer.ServiceExists() || Installer.InstalledDir() != null;
    private readonly string installedDir = Installer.InstalledDir() ?? Installer.DefaultDir;

    // ---------- contrôles ----------
    private readonly Dictionary<Step, Panel> pages = new();
    private readonly Label title = new(), subtitle = new(), lblError = new(), lblStatus = new(), lblSummary = new();
    private readonly Label[] stepLabels = new Label[4];
    private readonly RadioButton rbInstall = new(), rbUninstall = new();
    private readonly TextBox tbCode = new(), tbServer = new(), tbDir = new(), tbLog = new();
    private readonly Button btnBack = new(), btnNext = new(), btnCancel = new(), btnBrowse = new();
    private readonly ProgressBar bar = new();
    private Label lblServer = null!, lblServerHelp = null!;

    private SetupWizard()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5F);
        Text = "Installation du relais Linkii";
        ClientSize = new Size(720, 500);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        ForeColor = Texte;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (Exception e) when (e is ArgumentException or IOException) { }

        BuildBanner();
        BuildHeader();
        BuildPages();
        BuildButtons();
        tbDir.Text = installedDir;
        tbServer.Text = serverFromFile ?? "";
        Go(Step.Welcome);
        FormClosing += (_, e) =>
        {
            if (working) { e.Cancel = true; return; }
            if (!done && step != Step.Welcome && e.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, "Quitter l'assistant ? Rien n'a été installé.", "Relais Linkii", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true;
        };
    }

    private static Label Lbl(string text, int x, int y, int w, int h, float size = 9.5F, FontStyle style = FontStyle.Regular, Color? color = null) =>
        new() { Text = text, Location = new Point(x, y), Size = new Size(w, h), Font = new Font("Segoe UI", size, style), ForeColor = color ?? Texte };

    private void BuildBanner()
    {
        var banner = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = Nuit };
        banner.Controls.Add(Lbl("Linkii", 22, 28, 170, 40, 22F, FontStyle.Bold, Color.White));
        banner.Controls.Add(Lbl("Relais", 24, 68, 170, 26, 12F, FontStyle.Regular, Signal));
        var names = new[] { "Bienvenue", "Code d'installation", "Dossier", "Installation" };
        for (var i = 0; i < 4; i++)
        {
            stepLabels[i] = Lbl($"{i + 1}   {names[i]}", 24, 150 + i * 34, 170, 24, 10F, FontStyle.Regular, Color.FromArgb(0x8F, 0xA3, 0xBD));
            banner.Controls.Add(stepLabels[i]);
        }
        var foot = Lbl("Lit vos bases de données et API internes pour Linkii. Aucun port à ouvrir.", 24, 410, 166, 60, 8.5F, FontStyle.Regular, Color.FromArgb(0x8F, 0xA3, 0xBD));
        banner.Controls.Add(foot);
        Controls.Add(banner);
    }

    private void BuildHeader()
    {
        title.SetBounds(236, 22, 460, 34); title.Font = new Font("Segoe UI Semibold", 16F); title.ForeColor = Nuit;
        subtitle.SetBounds(238, 58, 460, 40); subtitle.ForeColor = Discret;
        Controls.Add(title); Controls.Add(subtitle);
    }

    private Panel Page(Step s)
    {
        var p = new Panel { Location = new Point(236, 110), Size = new Size(460, 300), Visible = false };
        pages[s] = p;
        Controls.Add(p);
        return p;
    }

    private void BuildPages()
    {
        // Bienvenue
        var w = Page(Step.Welcome);
        w.Controls.Add(Lbl("Cet assistant installe le relais Linkii : un petit service Windows qui lit pour Linkii les bases de données et les API de votre réseau.", 0, 0, 450, 54));
        w.Controls.Add(Lbl("•  ce PC ou ce serveur doit joindre vos bases et API internes ;\n•  il doit rester allumé : le relais démarre tout seul avec Windows ;\n•  rien à ouvrir dans le pare-feu : le relais appelle Linkii.", 0, 64, 450, 78, 9.5F, FontStyle.Regular, Discret));
        if (installed)
        {
            w.Controls.Add(Lbl($"Le relais est déjà installé dans {installedDir}.", 0, 160, 450, 24, 9.5F, FontStyle.Bold));
            rbInstall.Text = "Réinstaller le relais, ou le ré-appairer avec un nouveau code"; rbInstall.SetBounds(0, 190, 450, 26); rbInstall.Checked = true;
            rbUninstall.Text = "Désinstaller le relais"; rbUninstall.SetBounds(0, 218, 450, 26);
            w.Controls.Add(rbInstall); w.Controls.Add(rbUninstall);
        }
        else w.Controls.Add(Lbl("Cliquez sur Suivant pour continuer. Vous aurez besoin du code d'installation donné par Linkii.", 0, 164, 450, 40));

        // Code d'installation
        var c = Page(Step.Code);
        c.Controls.Add(Lbl("Dans Linkii : Intégrations › Sources de données › Relais › Installer un relais. Linkii y affiche un code de 8 caractères, valable une heure.", 0, 0, 450, 54, 9.5F, FontStyle.Regular, Discret));
        c.Controls.Add(Lbl("Code d'installation", 0, 64, 300, 22, 9.5F, FontStyle.Bold));
        tbCode.SetBounds(0, 90, 240, 40); tbCode.Font = new Font("Consolas", 18F); tbCode.CharacterCasing = CharacterCasing.Upper; tbCode.MaxLength = 9; tbCode.PlaceholderText = "ABCD-2345";
        tbCode.TextChanged += (_, _) => lblError.Text = "";
        c.Controls.Add(tbCode);
        lblServer = Lbl("Adresse du serveur Linkii", 0, 144, 300, 22, 9.5F, FontStyle.Bold);
        tbServer.SetBounds(0, 170, 440, 28); tbServer.PlaceholderText = "https://linkii.exemple.ch";
        lblServerHelp = Lbl("Exemple : https://linkii.exemple.ch (https obligatoire).", 0, 202, 440, 22, 8.5F, FontStyle.Regular, Discret);
        if (serverFromFile == null) { c.Controls.Add(lblServer); c.Controls.Add(tbServer); c.Controls.Add(lblServerHelp); }
        else c.Controls.Add(Lbl($"Serveur : {serverFromFile}", 0, 144, 450, 22, 9.5F, FontStyle.Regular, Discret));
        lblError.SetBounds(0, 236, 450, 56); lblError.ForeColor = Rouge;
        c.Controls.Add(lblError);

        // Dossier
        var f = Page(Step.Folder);
        f.Controls.Add(Lbl("Le programme et sa configuration seront copiés dans ce dossier. Le dossier proposé convient dans la plupart des cas.", 0, 0, 450, 54, 9.5F, FontStyle.Regular, Discret));
        f.Controls.Add(Lbl("Dossier d'installation", 0, 64, 300, 22, 9.5F, FontStyle.Bold));
        tbDir.SetBounds(0, 90, 360, 28);
        btnBrowse.Text = "Parcourir…"; btnBrowse.SetBounds(366, 88, 94, 32);
        btnBrowse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Dossier d'installation du relais", SelectedPath = tbDir.Text, UseDescriptionForTitle = true };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            var sel = d.SelectedPath;   // un dossier déjà rempli (C:\Program Files, Documents…) : le relais va dans un sous-dossier « Linkii Relay »
            tbDir.Text = Directory.Exists(sel) && !File.Exists(Path.Combine(sel, "Linkii.Relay.exe")) && Directory.EnumerateFileSystemEntries(sel).Any() ? Path.Combine(sel, "Linkii Relay") : sel;
        };
        f.Controls.Add(tbDir); f.Controls.Add(btnBrowse);

        // Prêt
        var r = Page(Step.Ready);
        lblSummary.SetBounds(0, 0, 450, 240);
        r.Controls.Add(lblSummary);

        // Progression
        var p = Page(Step.Progress);
        lblStatus.SetBounds(0, 0, 450, 24); lblStatus.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        bar.SetBounds(0, 30, 450, 12); bar.Style = ProgressBarStyle.Marquee;
        tbLog.SetBounds(0, 54, 450, 200); tbLog.Multiline = true; tbLog.ReadOnly = true; tbLog.ScrollBars = ScrollBars.Vertical; tbLog.Font = new Font("Consolas", 9F); tbLog.BackColor = Color.FromArgb(0xF4, 0xF6, 0xF9);
        p.Controls.Add(lblStatus); p.Controls.Add(bar); p.Controls.Add(tbLog);
    }

    private void BuildButtons()
    {
        var line = new Panel { Location = new Point(210, 438), Size = new Size(510, 1), BackColor = Color.FromArgb(0xD9, 0xDE, 0xE6) };
        Controls.Add(line);
        btnBack.Text = "‹ Précédent"; btnBack.SetBounds(404, 452, 100, 32); btnBack.Click += (_, _) => Back();
        btnNext.Text = "Suivant ›"; btnNext.SetBounds(510, 452, 96, 32); btnNext.Click += async (_, _) => await Next();
        btnCancel.Text = "Annuler"; btnCancel.SetBounds(612, 452, 96, 32); btnCancel.Click += (_, _) => Close();
        AcceptButton = btnNext; CancelButton = btnCancel;
        foreach (var b in new[] { btnBack, btnNext, btnCancel }) { b.FlatStyle = FlatStyle.System; Controls.Add(b); }
    }

    // ---------- navigation ----------
    private void Go(Step s)
    {
        step = s;
        foreach (var (k, p) in pages) p.Visible = k == s;
        var (t, sub) = s switch
        {
            Step.Welcome => ("Bienvenue", "Installation du relais Linkii sur ce poste."),
            Step.Code => ("Code d'installation", "Saisissez le code affiché dans Linkii."),
            Step.Folder => ("Dossier d'installation", "Où installer le relais ?"),
            Step.Ready => (uninstall ? "Prêt à désinstaller" : "Prêt à installer", uninstall ? "Le relais sera arrêté et supprimé de ce poste." : "Vérifiez ces informations, puis cliquez sur Installer."),
            _ => (uninstall ? "Désinstallation" : "Installation en cours", "Veuillez patienter…")
        };
        title.Text = t; subtitle.Text = sub;
        var active = s switch { Step.Welcome => 0, Step.Code => 1, Step.Folder => 2, _ => 3 };
        for (var i = 0; i < 4; i++)
        {
            stepLabels[i].ForeColor = i == active ? Color.White : Color.FromArgb(0x8F, 0xA3, 0xBD);
            stepLabels[i].Font = new Font("Segoe UI", 10F, i == active ? FontStyle.Bold : FontStyle.Regular);
        }
        btnBack.Visible = s is Step.Code or Step.Folder or Step.Ready;
        btnCancel.Visible = s != Step.Progress;
        btnNext.Text = s == Step.Ready ? (uninstall ? "Désinstaller" : "Installer") : s == Step.Progress ? "Terminer" : "Suivant ›";
        btnNext.Enabled = s != Step.Progress;
        if (s == Step.Ready) lblSummary.Text = Summary();
        if (s == Step.Code) tbCode.Focus();
    }

    private string Summary() => uninstall
        ? $"Le relais installé dans :\n    {tbDir.Text}\n\nsera arrêté, son service Windows « Relais Linkii » supprimé et son dossier effacé.\n\nPensez ensuite à supprimer aussi le relais dans Linkii (Intégrations › Sources de données › Relais)."
        : $"Relais Linkii {RelayWorker.Version}\n\nServeur Linkii :\n    {pairing?.Server}\nRelais appairé :\n    {pairing?.Name}\nDossier d'installation :\n    {tbDir.Text}\n\nUn service Windows « Relais Linkii » sera créé : démarrage automatique, redémarré tout seul en cas de panne.";

    private void Back()
    {
        lblError.Text = "";
        Go(step switch { Step.Code => Step.Welcome, Step.Folder => Step.Code, Step.Ready => uninstall ? Step.Welcome : Step.Folder, _ => step });
    }

    private async Task Next()
    {
        switch (step)
        {
            case Step.Welcome:
                uninstall = installed && rbUninstall.Checked;
                if (uninstall) tbDir.Text = installedDir;
                Go(uninstall ? Step.Ready : Step.Code);
                break;
            case Step.Code:
                await VerifyCode();
                break;
            case Step.Folder:
                var dir = tbDir.Text.Trim();
                if (dir.Length == 0 || !Path.IsPathRooted(dir)) { MessageBox.Show(this, "Indiquez un dossier complet, par exemple C:\\Program Files\\Linkii Relay.", "Relais Linkii", MessageBoxButtons.OK, MessageBoxIcon.Information); break; }
                tbDir.Text = dir;
                Go(Step.Ready);
                break;
            case Step.Ready:
                await Work();
                break;
            case Step.Progress:
                Close();
                break;
        }
    }

    /// <summary>Le code est essayé auprès de Linkii tout de suite : refusé, on reste ici pour le corriger ; accepté, on passe au dossier.</summary>
    private async Task VerifyCode()
    {
        var server = (serverFromFile ?? tbServer.Text.Trim()).TrimEnd('/');
        var key = (RelayPairing.CleanCode(tbCode.Text) ?? "") + "@" + server;
        if (pairing != null && pairedWith == key) { Go(Step.Folder); return; }   // déjà accepté : un code ne sert qu'une fois
        lblError.Text = ""; lblError.ForeColor = Discret; lblError.Text = "Vérification du code auprès de Linkii…";
        btnNext.Enabled = btnBack.Enabled = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var (r, err) = await RelayPairing.PairAsync(http, server, tbCode.Text, null);
            if (r == null)
            {
                lblError.ForeColor = Rouge; lblError.Text = err;
                (serverFromFile == null && (err!.Contains("Adresse du serveur") || err.Contains("injoignable") || err.Contains("https")) ? tbServer : tbCode).Focus();
                return;
            }
            pairing = new PairResult(server, r.Token, r.Name);
            pairedWith = key;
            lblError.Text = "";
            Go(Step.Folder);
        }
        finally { btnNext.Enabled = btnBack.Enabled = true; }
    }

    private async Task Work()
    {
        working = true;
        Go(Step.Progress);
        tbLog.Clear();
        lblStatus.Text = uninstall ? "Désinstallation du relais…" : "Installation du relais…";
        bar.Style = ProgressBarStyle.Marquee;
        Installer.Sink = line => { if (IsHandleCreated) BeginInvoke(() => tbLog.AppendText(line + Environment.NewLine)); };
        var ok = false;
        var dir = tbDir.Text.Trim();
        try { ok = await Task.Run(() => uninstall ? Installer.UninstallCore(dir) : Installer.InstallCore(dir, pairing)) == 0; }
        catch (Exception e)
        {
            Installer.Log(e.ToString());
            tbLog.AppendText($"✖  {(e is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception ? e.Message : e.GetType().Name + " : " + e.Message)}{Environment.NewLine}");
        }
        Installer.Sink = null;
        working = false; done = true;
        bar.Style = ProgressBarStyle.Blocks; bar.Value = ok ? 100 : 0;
        title.Text = ok ? (uninstall ? "Relais désinstallé" : "Relais installé") : "L'opération a échoué";
        subtitle.Text = ok
            ? (uninstall ? "Pensez à supprimer aussi le relais dans Linkii." : "Retournez dans Linkii : le relais passe « En ligne » dans quelques secondes.")
            : $"Corrigez le problème indiqué puis relancez l'installateur. Détails : {Installer.LogFile}";
        lblStatus.Text = ok ? "Terminé." : "Échec.";
        lblStatus.ForeColor = ok ? Color.FromArgb(0x1E, 0x7F, 0x4F) : Rouge;
        btnNext.Enabled = true;
        btnNext.Focus();
    }
}
