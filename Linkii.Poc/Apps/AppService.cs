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
    /// Active les apps « par défaut » (Image, Vidéo, Diaporamas, widgets) une seule fois par client, espaces existants compris.
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

    /// <summary>Rend l'app disponible dans les listes de lecture (page Intégrations), ou le widget dans l'édition des écrans (page Widgets).</summary>
    public void Enable(string appId) => Install(appId);

    /// <summary>Retire l'app des choix proposés. Non destructif : les contenus et widgets déjà en place restent à l'antenne.</summary>
    public void Disable(string appId) => store.Write(d => { d.AppInstalls.RemoveAll(a => a.AppId == appId); });

    /// <summary>
    /// Active ou désactive une source de calendriers partagés (Intégrations). L'app « Calendrier partagé » est proposée
    /// dans les listes de lecture tant qu'au moins une source est active ; les contenus existants restent diffusés.
    /// </summary>
    public void SetCalendarSource(string source, bool on)
    {
        var any = store.Write(d =>
        {
            var a = d.Tenant.CalendarAccounts.FirstOrDefault(x => x.Source == source);
            if (a == null) d.Tenant.CalendarAccounts.Add(a = new CalendarAccount { Source = source });
            a.Enabled = on;
            return d.Tenant.CalendarAccounts.Any(x => x.Enabled);
        });
        if (any) Enable("agenda"); else Disable("agenda");
    }

    /// <summary>Active ou désactive une source Drive. L'app « Dossier Drive » est proposée tant qu'au moins une source est active.</summary>
    public void SetDriveSource(string source, bool on)
    {
        var any = store.Write(d =>
        {
            d.Tenant.DrivesEnabled.Remove(source);
            if (on) d.Tenant.DrivesEnabled.Add(source);
            return d.Tenant.DrivesEnabled.Count > 0;
        });
        if (any) Enable("drive"); else Disable("drive");
    }

    /// <summary>Retire l'app et ses instances. Les playlists qui les utilisent passent en « brouillon non publié » ; l'écran ne change qu'à la prochaine publication.</summary>
    public int Uninstall(string appId)
    {
        var orphans = new List<MediaItem>();
        var count = store.Write(d =>
        {
            var instances = d.Media.Where(m => m.AppId == appId).ToList();
            var ids = instances.Select(m => m.Id).ToHashSet();
            foreach (var id in ids) RemoveFromDrafts(d, id);
            d.Media.RemoveAll(m => m.AppId == appId);
            d.AppInstalls.RemoveAll(a => a.AppId == appId);
            orphans = PurgeExports(d, instances);
            return ids.Count;
        });
        DeleteFiles(orphans);
        return count;
    }

    public void DeleteInstance(Guid mediaId)
    {
        var pdfs = new List<string>();
        var orphans = store.Write(d =>
        {
            RemoveFromDrafts(d, mediaId);
            var instances = d.Media.Where(m => m.Id == mediaId && m.AppId != null).ToList();
            d.Media.RemoveAll(m => m.Id == mediaId && m.AppId != null);
            pdfs.AddRange(instances.Where(i => i.AppId == "pdf").Select(i => i.Settings.GetValueOrDefault("pdf") ?? ""));
            return PurgeExports(d, instances);
        });
        DeleteFiles(orphans);
        foreach (var p in pdfs) PdfService.Delete(p);   // le PDF d'origine suit son contenu
    }

    /// <summary>
    /// Exports Canva que ces contenus utilisaient : retirés de la médiathèque s'ils ne servent plus nulle part, y compris sur les écrans
    /// (un export encore diffusé reste jusqu'à la prochaine publication ; « Nettoyer les exports Canva » le retrouvera ensuite).
    /// </summary>
    private static List<MediaItem> PurgeExports(ClientDb d, IEnumerable<MediaItem> removedInstances)
    {
        var referenced = removedInstances.SelectMany(i => i.Settings.Values.SelectMany(AppField.MediaIds)).ToHashSet();
        if (referenced.Count == 0) return new();
        var orphans = MediaLibrary.UnusedCanva(d.Media, d.Playlists, d.Screens).Concat(MediaLibrary.UnusedExports(d.Media, d.Playlists, d.Screens, "pdf"))
            .Where(m => referenced.Contains(m.Id)).ToList();
        var ids = orphans.Select(m => m.Id).ToHashSet();
        d.Media.RemoveAll(m => ids.Contains(m.Id));
        return orphans;
    }

    private static void DeleteFiles(IEnumerable<MediaItem> items)
    {
        foreach (var o in items.Where(o => o.FileName != null))
        {
            try { File.Delete(Path.Combine(AppPaths.MediaDir, o.FileName!)); } catch (IOException) { }
            Thumbnails.Delete(o.FileName!);
        }
    }

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

        var orphans = new List<MediaItem>();
        var oldPdfs = new List<string>();
        var result = store.Write<(MediaItem?, List<string>)>(d =>
        {
            if (instanceId == null && !d.AppInstalls.Any(a => a.AppId == appId)) return (null, new List<string> { "Activez d'abord cette intégration dans la page Intégrations." });   // une app désactivée garde ses contenus modifiables
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
            // Fichiers exportés depuis Canva remplacés par un nouvel export : retirés de la médiathèque s'ils ne servent plus nulle part.
            var dropped = MediaIdsOf(m, item.Settings).Except(MediaIdsOf(m, settings)).ToHashSet();
            if (dropped.Count > 0)
            {
                var stillUsed = d.Media.Where(x => x.AppId != null && x.Id != item.Id && catalog.Find(x.AppId) != null)
                    .SelectMany(x => MediaIdsOf(catalog.Find(x.AppId)!, x.Settings)).ToHashSet();
                orphans = d.Media.Where(x => dropped.Contains(x.Id) && (x.Info.ContainsKey("canva") || x.Info.ContainsKey("pdf")) && !stillUsed.Contains(x.Id)).ToList();
                var orphanIds = orphans.Select(o => o.Id).ToHashSet();
                d.Media.RemoveAll(x => orphanIds.Contains(x.Id));
            }
            if (appId == "pdf" && item.Settings.GetValueOrDefault("pdf") is { Length: > 0 } oldPdf && oldPdf != settings.GetValueOrDefault("pdf")) oldPdfs.Add(oldPdf);   // autre PDF : l'ancien n'est plus utile
            item.Name = name.Trim();
            item.AppVersion = m.Version;
            item.Settings = settings;
            item.Secrets = secrets;
            if (instanceId == null) d.Media.Add(item);
            foreach (var p in d.Playlists.Where(p => p.Draft.Any(i => i.MediaId == item.Id))) p.Touch();   // les écrans de ces playlists ont une mise à jour à publier
            return (item, errors);
        });
        foreach (var o in orphans.Where(o => o.FileName != null))
            try { File.Delete(Path.Combine(AppPaths.MediaDir, o.FileName!)); } catch (IOException) { }
        foreach (var p in oldPdfs) PdfService.Delete(p);
        return result;
    }

    private static IEnumerable<Guid> MediaIdsOf(AppManifest m, IReadOnlyDictionary<string, string> settings) =>
        m.Settings.Where(f => f.IsMedia).SelectMany(f => AppField.MediaIds(settings.GetValueOrDefault(f.Key)));

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

    /// <summary>
    /// Aperçu d'une configuration non enregistrée (zone « Aperçu » de la fenêtre de configuration) : le résultat du test et, si le fournisseur
    /// en a, les données que recevrait l'écran (articles, événements…). Les erreurs deviennent un message, jamais une exception.
    /// </summary>
    public async Task<(bool Ok, string Message, System.Text.Json.JsonElement? Data)> Preview(AppManifest m, IReadOnlyDictionary<string, string> values, MediaItem? instance)
    {
        var (ok, message) = await Test(m, values, instance);
        if (!ok || providers.Find(m.Provider) is not { } provider || !m.Data) return (ok, message, null);
        try
        {
            var tenant = store.Read(d => d.Tenant);
            var data = await provider.Fetch(new AppRuntime(tenant, RuntimeValues(m, values, instance)));
            return (true, message, data == null ? null : System.Text.Json.JsonSerializer.SerializeToElement(data));
        }
        catch (Exception ex) { return (false, "Échec : " + ex.Message, null); }
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
