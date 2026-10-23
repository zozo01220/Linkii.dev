namespace Linkii.Poc;

/// <summary>
/// Fusion des doublons de présentations (§103), réglage « Fusionner automatiquement les doublons de présentations » (activé par défaut).
/// <para>1. Les contenus d'avant les présentations (PDF, PowerPoint, Google Slides, Google Docs, Canva à plusieurs pages) dont les images sont des fichiers
/// ordinaires sont rangés dans une présentation chacun.</para>
/// <para>2. Les présentations du même fichier d'origine (même aire, même clé, même date de modification) sont réunies en une seule : les contenus
/// passent aux images de celle qu'on garde (une image d'un doublon est reprise si la gardée n'a pas encore cette page), les images en trop
/// sont retirées dès que ni contenu ni écran ne les utilise.</para>
/// Aucune image n'est refaite : seuls des liens changent. Se lance au démarrage du serveur, et quand le réglage est activé.
/// </summary>
public class PresentationMerge(JsonStore store, ILogger<PresentationMerge> log)
{
    public record Outcome(int Adopted, int Merged, int Removed);

    private const string Stale = "0";   // génération d'une image gardée en attente (un écran la diffuse encore) : retirée dès que plus rien ne l'utilise

    public Outcome Run(Guid clientId)
    {
        var removedFiles = new List<MediaItem>();
        var pdfsToDelete = new List<string>();
        int adopted = 0, merged = 0;
        var presIds = new HashSet<Guid>();
        store.Write(d =>
        {

            adopted = Adopt(d, clientId, presIds);
            merged = Merge(d, clientId, presIds, pdfsToDelete);
            // anciennes images ordinaires de PDF que rien n'utilise (conversion abandonnée) : retirées
            var orphans = MediaLibrary.UnusedExports(d.Media.Where(m => m.ClientId == clientId), d.Playlists.Where(p => p.ClientId == clientId), d.Screens.Where(s => s.ClientId == clientId), "pdf").ToList();
            var ids = orphans.Select(m => m.Id).ToHashSet();
            d.Media.RemoveAll(m => ids.Contains(m.Id));
            removedFiles.AddRange(orphans);
        });
        foreach (var id in presIds) Pres.PurgeStale(store, clientId, id);
        PdfService.DeleteImageFiles(removedFiles);
        foreach (var p in pdfsToDelete) PdfService.Delete(p);
        if (adopted + merged > 0) log.LogInformation("Présentations : {Adopted} rangée(s), {Merged} doublon(s) fusionné(s) pour le client {Client}", adopted, merged, clientId);
        return new(adopted, merged, removedFiles.Count);
    }

    // ---------- 1. Contenus d'avant les présentations ----------

    private static int Adopt(Db d, Guid clientId, HashSet<Guid> presIds)
    {
        var adopted = 0;
        var contents = d.Media.Where(m => m.ClientId == clientId && m.AppId != null && (m.AppId == "pdf" || CloudFileKind.IsCloudApp(m.AppId) || m.AppId == "canva")).ToList();
        foreach (var c in contents)
        {
            var isCanva = c.AppId == "canva";
            if (isCanva && c.Settings.GetValueOrDefault("kind") == "video") continue;
            var ids = AppField.MediaIds(c.Settings.GetValueOrDefault("pages"));
            if (ids.Count < 2 && !(int.TryParse(c.Settings.GetValueOrDefault("pdfPages"), out var tp) && tp > 1 && ids.Count > 0)) continue;
            var images = ids.Select(id => d.Media.FirstOrDefault(m => m.Id == id && m.ClientId == clientId)).ToList();
            if (images.Any(i => i == null || i.AppId != null || i.Type != "image" || Pres.IsPage(i))) continue;   // déjà dans une présentation (ou fichier choisi à la main)
            var pdfId = c.Settings.GetValueOrDefault("pdf") ?? "";
            if (!isCanva && (pdfId.Length == 0 || Pres.Owns(d, pdfId))) continue;
            if (!isCanva && !PdfService.Exists(pdfId)) continue;   // PDF disparu : les images restent des fichiers ordinaires

            var opts = PdfService.Options.From(c.Settings, c.AppId);
            var total = isCanva ? images.Count : int.TryParse(c.Settings.GetValueOrDefault("pdfPages"), out var n) ? n : images.Count;
            List<int> pgs;
            if (isCanva) pgs = Enumerable.Range(1, images.Count).ToList();
            else
            {
                var (list, _) = PdfService.ParseRange(opts.Range, total);
                pgs = (list ?? Enumerable.Range(1, total).ToList()).Take(PdfService.MaxPages).ToList();
            }
            var vk = isCanva ? "none" : Pres.Variant(opts);
            var key = isCanva ? "canva:" + c.Settings.GetValueOrDefault("design") : c.AppId == "pdf" ? "sha:" + HashOf(pdfId) : c.AppId + ":" + c.Settings.GetValueOrDefault("gfile");
            var title = isCanva ? c.Settings.GetValueOrDefault("designTitle") : c.Settings.GetValueOrDefault("pdfTitle");
            var presId = Guid.NewGuid();
            var first = images[0]!;
            var pres = new MediaItem
            {
                Id = presId, ClientId = clientId, AreaId = c.AreaId, Type = Pres.Type, AddedUtc = images.Min(i => i!.AddedUtc) ?? DateTime.UtcNow,
                Name = string.IsNullOrWhiteSpace(title) ? c.Name : title!.Length > 120 ? title[..120] : title, Width = first.Width, Height = first.Height
            };
            pres.Info["src"] = isCanva ? "canva" : opts.Src; pres.Info["key"] = key; pres.Info["pages"] = total.ToString(); pres.Info["gen"] = "1"; pres.Info["synced"] = DateTime.UtcNow.ToString("o");
            if (!isCanva) pres.Info["pdfId"] = pdfId;
            if (c.Settings.GetValueOrDefault("gfileMod") is { Length: > 0 } mod) pres.Info["mod"] = mod;
            if ((c.Settings.GetValueOrDefault("gfileName") ?? title) is { Length: > 0 } file) pres.Info["file"] = file;
            d.Media.Add(pres);
            for (var i = 0; i < Math.Min(images.Count, pgs.Count); i++)
            {
                var img = images[i]!;
                img.Info.Remove("pdf"); img.Info.Remove("canva");
                img.Info["pres"] = presId.ToString(); img.Info["var"] = vk; img.Info["pg"] = pgs[i].ToString(); img.Info["gen"] = "1";
            }
            presIds.Add(presId);
            adopted++;
        }
        return adopted;
    }

    private static string HashOf(string pdfId)
    {
        try
        {
            var path = Path.Combine(AppPaths.PdfDir, pdfId + ".pdf");
            if (!File.Exists(path)) return "legacy-" + pdfId;
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
        }
        catch (IOException) { return "legacy-" + pdfId; }
    }

    // ---------- 2. Présentations du même fichier ----------

    private static int Merge(Db d, Guid clientId, HashSet<Guid> presIds, List<string> pdfsToDelete)
    {
        var merged = 0;
        var groups = d.Media.Where(m => m.ClientId == clientId && Pres.Is(m) && (m.Info.GetValueOrDefault("key") ?? "").Length > 0)
            .GroupBy(m => (m.AreaId, Key: m.Info["key"], Mod: m.Info.GetValueOrDefault("mod") ?? "", Src: m.Info.GetValueOrDefault("src") ?? ""))
            .Where(g => g.Count() > 1).ToList();
        foreach (var g in groups)
        {
            var target = g.OrderBy(p => p.AddedUtc ?? DateTime.MaxValue).First();
            var tid = target.Id.ToString();
            var tgen = target.Info.GetValueOrDefault("gen") ?? "1";
            presIds.Add(target.Id);
            foreach (var s in g.Where(p => p.Id != target.Id).ToList())
            {
                var sid = s.Id.ToString();
                // les contenus du doublon passent à l'autre (même PDF, mêmes pages)
                foreach (var c in Pres.ContentsOf(d.Media.Where(m => m.ClientId == clientId), s))
                {
                    var o = PdfService.Options.From(c.Settings, c.AppId);
                    var vk = c.AppId == "canva" ? "none" : Pres.Variant(o);
                    var old = AppField.MediaIds(c.Settings.GetValueOrDefault("pages"));
                    var oldImages = old.Select(id => d.Media.FirstOrDefault(m => m.Id == id)).ToList();
                    var fresh = new List<Guid>();
                    foreach (var img in oldImages)
                    {
                        if (img == null) continue;
                        var pg = Pres.PageOf(img);
                        var keep = d.Media.FirstOrDefault(m => Pres.IdOf(m) == tid && Pres.PageOf(m) == pg && m.Info.GetValueOrDefault("var") == (img.Info.GetValueOrDefault("var") ?? vk) && m.Info.GetValueOrDefault("gen") == tgen);
                        if (keep == null)   // l'autre n'a pas cette page : l'image du doublon est reprise
                        {
                            img.Info["pres"] = tid; img.Info["gen"] = tgen; keep = img;
                        }
                        fresh.Add(keep.Id);
                    }
                    c.Settings["pages"] = string.Join(",", fresh);
                    if (target.Info.GetValueOrDefault("pdfId") is { Length: > 0 } tp) c.Settings["pdf"] = tp;
                }
                // ce qui reste du doublon : en attente de retrait (un écran peut encore le diffuser), puis la présentation elle-même
                foreach (var img in d.Media.Where(m => Pres.IdOf(m) == sid).ToList()) { img.Info["pres"] = tid; img.Info["gen"] = Stale; }
                if (s.Info.GetValueOrDefault("pdfId") is { Length: > 0 } sp && sp != target.Info.GetValueOrDefault("pdfId")) pdfsToDelete.Add(sp);
                d.Media.Remove(s);
                merged++;
            }
        }
        return merged;
    }
}

/// <summary>Au démarrage du serveur, fusionne les doublons de présentations des organisations qui ont gardé le réglage (activé par défaut).</summary>
public class PresentationMergeWorker(PresentationMerge merge, JsonStore store, ILogger<PresentationMergeWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stop); } catch (TaskCanceledException) { return; }
        var clients = store.Read(db => db.Clients.Where(c => c.MergePresentations).Select(c => c.Id).ToList());
        foreach (var id in clients)
        {
            if (stop.IsCancellationRequested) return;
            try { merge.Run(id); }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "Fusion des présentations du client {Client}", id); }
        }
    }
}
