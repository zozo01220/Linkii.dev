using System.Text.Json;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Drives Nextcloud, SFTP et Dropbox : lecture des réponses, connexions et comptes.</summary>
public class DriveRemotesTests
{
    const string Multistatus = """
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:s="http://sabredav.org/ns">
          <d:response><d:href>/nextcloud/remote.php/webdav/Affichage/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype><d:getlastmodified>Fri, 09 Oct 2026 08:00:00 GMT</d:getlastmodified><d:getetag>"abc"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>/nextcloud/remote.php/webdav/Affichage/Hall%20d%27entr%C3%A9e/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype><d:getlastmodified>Fri, 09 Oct 2026 09:00:00 GMT</d:getlastmodified><d:getetag>"f1"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>/nextcloud/remote.php/webdav/Affichage/affiche%201.JPG</d:href>
            <d:propstat><d:prop><d:resourcetype/><d:getcontentlength>1234</d:getcontentlength><d:getlastmodified>Sat, 10 Oct 2026 10:30:00 GMT</d:getlastmodified><d:getetag>"e1"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>/nextcloud/remote.php/webdav/Affichage/notes.pdf</d:href>
            <d:propstat><d:prop><d:resourcetype/><d:getcontentlength>99</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        </d:multistatus>
        """;

    static NextcloudSession Nextcloud() => new(new SafeHttp(), "https://cloud.exemple.ch/nextcloud/", "marie", "secret");

    [Fact]
    public void Nextcloud_lit_les_dossiers_et_les_fichiers()
    {
        var (items, self) = Nextcloud().Parse(Multistatus, "/Affichage");
        Assert.Equal("/Affichage", self!.Ref);
        Assert.True(self.Folder);
        Assert.Equal(3, items.Count);

        var folder = items.Single(i => i.Folder);
        Assert.Equal("Hall d'entrée", folder.Name);
        Assert.Equal("/Affichage/Hall d'entrée", folder.Ref);

        var image = items.Single(i => i.Name == "affiche 1.JPG");
        Assert.Equal("image/jpg", image.Mime);
        Assert.Equal(1234, image.Size);
        Assert.Equal("e1", image.Version);
        Assert.Equal("2026-10-10T10:30:00Z", image.Time);
        Assert.Equal("https://cloud.exemple.ch/nextcloud/apps/files/?dir=%2FAffichage", image.WebUrl);
    }

    [Fact]
    public void Nextcloud_une_reponse_qui_n_est_pas_du_webdav_est_refusee()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Nextcloud().Parse("<html>Bienvenue</html", "/"));
        Assert.Contains("Nextcloud", ex.Message);
    }

    [Fact]
    public void Nextcloud_a_la_racine_le_dossier_lui_meme_n_est_pas_dans_la_liste()
    {
        const string root = """
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/nextcloud/remote.php/webdav/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
              <d:response><d:href>/nextcloud/remote.php/webdav/a.mp4</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>5</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            </d:multistatus>
            """;
        var (items, self) = Nextcloud().Parse(root, "/");
        Assert.Equal("/", self!.Ref);
        Assert.Equal("/a.mp4", Assert.Single(items).Ref);
        Assert.Equal("video/mp4", items[0].Mime);
    }

    [Fact]
    public void Dropbox_lit_un_dossier_et_un_fichier()
    {
        using var doc = JsonDocument.Parse("""
            {"entries":[
              {".tag":"folder","name":"Hall","id":"id:F1","path_display":"/Affichage/Hall"},
              {".tag":"file","name":"affiche.png","id":"id:A1","path_display":"/Affichage/affiche.png","size":2048,"rev":"r1","content_hash":"h1","server_modified":"2026-10-10T08:00:00Z"}
            ]}
            """);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().Select(DropboxSession.ToItem).ToList();

        Assert.True(entries[0].Folder);
        Assert.Equal("id:F1", entries[0].Ref);
        Assert.Equal("https://www.dropbox.com/home/Affichage/Hall", entries[0].WebUrl);

        Assert.False(entries[1].Folder);
        Assert.Equal("h1", entries[1].Version);   // l'empreinte du contenu prime sur la révision
        Assert.Equal("image/png", entries[1].Mime);
        Assert.Equal("Affichage", entries[1].Where);
    }

    [Theory]
    [InlineData("photo.JPG", "image/jpg")]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("notes.docx", "application/octet-stream")]
    public void Le_type_vient_de_l_extension(string name, string mime) => Assert.Equal(mime, DriveMime.Of(name));

    [Fact]
    public void Identifiants_SFTP_aller_retour_et_valeurs_vides()
    {
        var back = SftpCredentials.Parse(new SftpCredentials("mdp", "-----BEGIN KEY-----", "phrase").ToJson());
        Assert.Equal(("mdp", "-----BEGIN KEY-----", "phrase"), (back.Password, back.Key, back.Passphrase));
        Assert.True(back.Any);
        Assert.False(SftpCredentials.Parse("pas du json").Any);
    }

    [Fact]
    public void Empreinte_SFTP_au_format_OpenSSH() =>
        Assert.StartsWith("SHA256:", SftpSession.Fingerprint(System.Security.Cryptography.SHA256.HashData([1, 2, 3])));

    [Fact]
    public async Task Un_serveur_SFTP_sur_le_reseau_interne_est_refuse()
    {
        using var session = new SftpSession("127.0.0.1", 22, "u", new("p", "", ""), "", _ => { });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.Test(default));
        Assert.Contains("réseau interne", ex.Message);
    }

    [Fact]
    public void Les_sources_distantes_sont_configurees_selon_leur_connexion()
    {
        var t = new Tenant { Name = "Test" };
        Assert.All(new[] { "nextcloud", "sftp", "dropbox" }, s => Assert.False(DriveSources.Configured(t, s)));

        t.DriveAccounts.Add(new DriveAccount { Source = "nextcloud", Url = "https://c.ch", User = "m", Secret = "dp:x" });
        t.DriveAccounts.Add(new DriveAccount { Source = "sftp", Url = "h.ch", User = "m" });   // sans secret : pas prête
        t.DriveAccounts.Add(new DriveAccount { Source = "dropbox", Secret = "dp:y", User = "m@x.ch" });
        Assert.True(DriveSources.Configured(t, "nextcloud"));
        Assert.False(DriveSources.Configured(t, "sftp"));
        Assert.True(DriveSources.Configured(t, "dropbox"));
    }

    [Fact]
    public void Comptes_connectes_listent_les_serveurs_de_fichiers_et_gardent_un_compte_Dropbox_perdu()
    {
        var t = new Tenant { Name = "Test" };
        t.DriveAccounts.Add(new DriveAccount { Source = "nextcloud", Url = "https://c.ch", User = "marie", Secret = "dp:x" });
        t.DriveAccounts.Add(new DriveAccount { Source = "sftp", Url = "h.ch", Port = 2222, User = "sync", Secret = "dp:y" });
        t.DriveAccounts.Add(new DriveAccount { Source = "dropbox", User = "m@x.ch", LostUtc = DateTime.UtcNow });

        var accounts = ConnectedAccounts.Of(t);
        Assert.Equal(new[] { "nextcloud", "sftp", "dropbox" }, accounts.Select(a => a.Id));
        Assert.Equal("h.ch:2222", accounts.Single(a => a.Id == "sftp").Detail);
        Assert.True(accounts.Single(a => a.Id == "dropbox").Lost);
        Assert.Equal("/dropbox/connect?tab=comptes", ConnectedAccounts.ConnectUrl("dropbox"));
        Assert.Equal("dropbox", ConnectedAccounts.For("drv-dropbox"));
    }

    [Fact]
    public void Chaque_source_a_un_emplacement_de_depart_et_la_recherche_n_est_pas_partout()
    {
        Assert.All(DriveSources.All, s => Assert.NotEmpty(DriveSources.Roots(s.Id)));
        Assert.True(DriveSources.CanSearch("dropbox"));
        Assert.False(DriveSources.CanSearch("sftp"));
        Assert.False(DriveSources.CanSearch("nextcloud"));
        Assert.All(new[] { "nextcloud", "sftp", "dropbox" }, s => Assert.True(DriveSources.IsRemote(s)));
        Assert.False(DriveSources.IsRemote("gdrive"));
    }
}
