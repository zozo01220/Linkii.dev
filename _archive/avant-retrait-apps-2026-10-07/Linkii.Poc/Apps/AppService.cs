namespace Linkii.Poc;

/// <summary>
/// Les apps du client connecté : installation depuis le catalogue, instances (paramétrées, dans la médiathèque), désinstallation.
/// Les paramètres d'une instance sont un brouillon : ils ne partent sur les écrans qu'à la publication d'une playlist.
/// </summary>
public class AppService(TenantStore store, AppCatalog catalog, AppProviders providers, SecretBox box, SafeHttp? http = null)
{
    public IReadOnlyList<AppManifest> Catalog => catalog.All;
    public AppManifest? Find(string? appId) => catalog.Find(appId);

    public List<AppInstall> Installed() { ApplyDefaults(); return store.Read(d => d.AppInstalls.OrderBy(a => a.InstalledUtc).ToList()); }
    public bool IsInstalled(string appId) { ApplyDefaults(); return store.Read(d => d.AppInstalls.Any(a => a.AppId == appId)); }

    /// <summary>
    /// Active les apps « par défaut » (Image, Vidéo, Diaporamas) une seule fois par client, espaces existants compris.
    /// Une app désactivée ensuite par le client ne se réactive pas (Tenant.DefaultAppsApplied).
    /// </summary>
    private void ApplyDefaults()
    {
        var ids = catalog.All.Where(a => a.DefaultOn).Select(a => a.Id).ToList();
        if (ids.Count == 0 || store.Read(d => ids.All(d.Tenant.DefaultAppsApplied.Contains))) return;
        store.Write(d =>
        {
            foreach (var id in ids.Where(id => !d.Tenant.DefaultAppsApplied.Contains(id)))
            {
                if (!d.AppInstalls.Any(a => a.AppId == id)) d.AppInstalls.Add(new AppInstall { AppId = id, Version = catalog.Find(id)!.Version });
                d.Tenant.DefaultAppsApplied.Add(id);
            }
        });
    }
    public MediaItem? Instance(Guid id) => store.Read(d => d.Media.FirstOrDefault(m => m.Id == id && m.AppId != null));   // même si l'app a été désactivée
    public List<MediaItem> Instances(string appId) => store.Read(d => d.Media.Where(m => m.AppId == appId).OrderBy(m => m.Name).ToList());

    /// <summary>Ajoute l'app au client (sans effet si elle l'est déjà).</summary>
    public AppInstall Install(string appId)
    {
        var m = catalog.Find(appId) ?? throw new InvalidOperationException("App inconnue.");
        return store.Write(d =>
        {
            var existing = d.AppInstalls.FirstOrDefault(a => a.AppId == appId);
            if (existing != null) return existing;
            var a = new AppInstall { AppId = appId, Version = m.Version };
            d.AppInstalls.Add(a);
            return a;
        });
    }

    /// <summary>Rend l'app disponible dans les playlists du client (Réglages › Applications).</summary>
    public void Enable(string appId) => Install(appId);

    /// <summary>Retire l'app des choix proposés dans les playlists. Non destructif : les contenus déjà créés restent en place et à l'antenne.</summary>
    public void Disable(string appId) => store.Write(d => { d.AppInstalls.RemoveAll(a => a.AppId == appId); });

    /// <summary>Retire l'app et ses instances. Les playlists qui les utilisent passent en « brouillon non publié » ; l'écran ne change qu'à la prochaine publication.</summary>
    public int Uninstall(string appId) => store.Write(d =>
    {
        var ids = d.Media.Where(m => m.AppId == appId).Select(m => m.Id).ToHashSet();
        foreach (var id in ids) RemoveFromDrafts(d, id);
        d.Media.RemoveAll(m => m.AppId == appId);
        d.AppInstalls.RemoveAll(a => a.AppId == appId);
        return ids.Count;
    });

    public void DeleteInstance(Guid mediaId) => store.Write(d =>
    {
        RemoveFromDrafts(d, mediaId);
        d.Media.RemoveAll(m => m.Id == mediaId && m.AppId != null);
    });

    private static void RemoveFromDrafts(ClientDb d, Guid mediaId)
    {
        foreach (var p in d.Playlists)
            if (p.Draft.RemoveAll(i => i.MediaId == mediaId) > 0) p.Touch();
    }

    /// <summary>Mémorise une information constatée sur une instance (durée d'une vidéo…). Ne touche ni aux paramètres ni aux playlists.</summary>
    public void SetInfo(Guid mediaId, string key, string value) => store.Write(d =>
    {
        var m = d.Media.FirstOrDefault(x => x.Id == mediaId && x.AppId != null);
        if (m != null) m.Info[key] = value;
    });

    /// <summary>Playlists dont le brouillon contient cette instance.</summary>
    public List<Playlist> PlaylistsUsing(Guid mediaId) => store.Read(d => d.Playlists.Where(p => p.Draft.Any(i => i.MediaId == mediaId)).ToList());

    /// <summary>
    /// Crée ou met à jour une instance après validation. <paramref name="values"/> contient une entrée par champ : un secret laissé vide est conservé.
    /// Les playlists qui contiennent l'instance passent en « brouillon non publié ».
    /// </summary>
    public (MediaItem? Item, List<string> Errors) SaveInstance(string appId, Guid? instanceId, string name, IReadOnlyDictionary<string, string> values)
    {
        var m = catalog.Find(appId) ?? throw new InvalidOperationException("App inconnue.");
        if (string.IsNullOrWhiteSpace(name)) return (null, new() { "Donnez un nom à cette instance." });
        if (name.Trim().Length > 80) return (null, new() { "Le nom est trop long (80 caractères maximum)." });

        return store.Write<(MediaItem?, List<string>)>(d =>
        {
            if (instanceId == null && !d.AppInstalls.Any(a => a.AppId == appId)) return (null, new List<string> { "Activez d'abord cette app dans Réglages › Applications." });   // une app désactivée garde ses contenus modifiables
            var item = instanceId is { } id ? d.Media.FirstOrDefault(x => x.Id == id && x.AppId == appId) : new MediaItem { Type = "app", AppId = appId };
            if (item == null) return (null, new List<string> { "Instance introuvable." });

            var stored = item.Secrets.Keys.ToHashSet();
            var errors = AppCatalog.Validate(m, values, stored);
            if (errors.Count > 0) return (null, errors);

            var settings = new Dictionary<string, string>();
            var secrets = new Dictionary<string, string>(item.Secrets);
            foreach (var f in m.Settings)
            {
                if (!AppCatalog.IsVisible(f, values)) { secrets.Remove(f.Key); continue; }   // un champ masqué n'est ni enregistré ni conservé
                var v = values.TryGetValue(f.Key, out var raw) ? raw.Trim() : "";
                if (f.Type == "youtube" && YouTube.Canonical(v) is { } canon) v = canon;   // lien normalisé : le player en extrait l'identifiant
                if (f.IsSecret) { if (v.Length > 0) secrets[f.Key] = box.Protect(v); }
                else settings[f.Key] = v;
            }
            if (!item.Settings.OrderBy(k => k.Key).SequenceEqual(settings.OrderBy(k => k.Key))) item.Info = new();   // autre lien : la durée connue ne vaut plus
            item.Name = name.Trim();
            item.AppVersion = m.Version;
            item.Settings = settings;
            item.Secrets = secrets;
            if (instanceId == null) d.Media.Add(item);
            foreach (var p in d.Playlists.Where(p => p.Draft.Any(i => i.MediaId == item.Id))) p.Touch();   // les écrans de ces playlists ont une mise à jour à publier
            return (item, errors);
        });
    }

    /// <summary>
    /// Remplace les liens courts lnkd.in (publication LinkedIn partagée) par l'adresse complète de la publication : le player ne peut pas
    /// suivre une redirection d'un autre site. Rend les liens qui n'ont pas pu être résolus. Appel réseau : à faire avant <see cref="SaveInstance"/>.
    /// </summary>
    public async Task<List<string>> ResolveShortLinks(string appId, Dictionary<string, string> values)
    {
        var failed = new List<string>();
        if (appId != "linkedin" || http == null || !values.TryGetValue("posts", out var posts) || posts.Length == 0) return failed;
        var rx = new System.Text.RegularExpressions.Regex(@"https?://lnkd.in/S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var url in rx.Matches(posts).Select(m => m.Value).Distinct().ToList())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var res = await http.Send(url, cts.Token);
                var final = res.RequestMessage?.RequestUri;
                if (final == null || !final.Host.EndsWith("linkedin.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
                posts = posts.Replace(url, new UriBuilder(final) { Query = "" }.Uri.AbsoluteUri);   // sans les paramètres de suivi
            }
            catch { failed.Add(url); }
        }
        values["posts"] = posts;
        return failed;
    }

    // ---------- Test de la configuration ----------

    /// <summary>Valeurs en clair vues par le serveur : saisies du formulaire, complétées par les secrets déjà enregistrés de l'instance.</summary>
    public IReadOnlyDictionary<string, string> RuntimeValues(AppManifest m, IReadOnlyDictionary<string, string> values, MediaItem? instance)
    {
        var v = new Dictionary<string, string>();
        foreach (var f in m.Settings.Where(f => AppCatalog.IsVisible(f, values)))
        {
            var typed = values.TryGetValue(f.Key, out var raw) ? raw.Trim() : "";
            if (f.IsSecret && typed.Length == 0 && instance != null && instance.Secrets.TryGetValue(f.Key, out var enc)) typed = box.Unprotect(enc);
            v[f.Key] = typed;
        }
        if (instance != null)   // secrets enregistrés hors formulaire (jeton d'une connexion OAuth)
            foreach (var (k, enc) in instance.Secrets) if (!v.ContainsKey(k)) v[k] = box.Unprotect(enc);
        return v;
    }

    /// <summary>Teste une configuration non enregistrée. Retourne (réussi, message).</summary>
    public async Task<(bool Ok, string Message)> Test(AppManifest m, IReadOnlyDictionary<string, string> values, MediaItem? instance)
    {
        var provider = providers.Find(m.Provider);
        if (provider == null) return (false, "Cette app n'a rien à tester.");
        var errors = AppCatalog.Validate(m, values, instance?.Secrets.Keys.ToHashSet() ?? new HashSet<string>());
        if (errors.Count > 0) return (false, errors[0]);
        try
        {
            var tenant = store.Read(d => d.Tenant);
            return (true, await provider.Test(new AppRuntime(tenant, RuntimeValues(m, values, instance))));
        }
        catch (Exception ex) { return (false, "Échec : " + ex.Message); }
    }
}
