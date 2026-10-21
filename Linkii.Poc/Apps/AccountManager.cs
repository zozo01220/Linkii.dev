namespace Linkii.Poc;

/// <summary>
/// Connexion et déconnexion des comptes de l'organisation, quel que soit le fournisseur. Sert à Intégrations (Comptes connectés),
/// à l'annulation d'un assistant d'ajout (rien ne reste) et au retrait d'une intégration (le compte part avec elle).
/// Un compte Google ou Microsoft est partagé par plusieurs intégrations : il ne part qu'avec la dernière (<see cref="UsedByOthers"/>).
/// </summary>
public class AccountManager(TenantStore store, MicrosoftAuth ms, GoogleAuth google, CanvaService canva, DropboxAuth dropbox, FacebookAuth facebook)
{
    /// <summary>Retire le compte de l'organisation (type de <see cref="ConnectedAccounts"/>).</summary>
    public async Task Disconnect(string kind)
    {
        var clientId = store.ClientId;
        switch (kind)
        {
            case ConnectedAccounts.Microsoft: ms.Disconnect(clientId); break;
            case ConnectedAccounts.Google: await google.Disconnect(clientId); break;
            case ConnectedAccounts.Canva: canva.Disconnect(clientId); break;
            case ConnectedAccounts.Dropbox: await dropbox.Disconnect(clientId); break;
            case ConnectedAccounts.Facebook: facebook.Disconnect(clientId); break;
            case ConnectedAccounts.Nextcloud or ConnectedAccounts.Sftp: store.Write(d => d.Tenant.DriveAccounts.RemoveAll(x => x.Source == kind)); break;
            case ConnectedAccounts.HomeAssistant: store.Write(d => { d.Tenant.HomeAssistant = null; }); break;
            default:   // serveurs CalDAV et Exchange
                store.Write(d =>
                {
                    if (d.Tenant.CalendarAccounts.FirstOrDefault(x => x.Source == kind) is { } a)
                    {
                        a.Url = ""; a.User = ""; a.Secret = ""; a.Label = ""; a.LastError = ""; a.CheckedUtc = null;
                    }
                });
                break;
        }
    }

    /// <summary>Une autre intégration que <paramref name="integrationId"/>, déjà dans Mes intégrations, utilise ce compte.</summary>
    public bool UsedByOthers(string kind, string integrationId, IReadOnlySet<string> inMine) =>
        ConnectedAccounts.Kind(kind).Integrations.Any(i => i != integrationId && inMine.Contains(i));
}
