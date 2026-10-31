namespace Linkii.Poc;

/// <summary>
/// Une notification du centre de notifications (cloche de la barre des accès rapides et page « Notifications »).
/// Elle est écrite par <see cref="AlertService"/> pour toute l'organisation ; lu / supprimé se retiennent par personne.
/// </summary>
public class Notification : IClientOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    /// <summary>Type d'alerte (voir <see cref="Alerts.All"/>) : donne la catégorie affichée.</summary>
    public string Kind { get; set; } = "";
    /// <summary>info, ok, warn ou error : couleur de la pastille.</summary>
    public string Severity { get; set; } = Notifs.Info;
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Page ouverte par le bouton d'action (chemin relatif).</summary>
    public string Path { get; set; } = "/";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Nombre de fois que le même événement s'est produit (regroupement par <see cref="DedupeKey"/>).</summary>
    public int Count { get; set; } = 1;
    public string? DedupeKey { get; set; }
    /// <summary>Éléments concernés (noms d'écrans en panne) : la notification se résout quand ils sont tous rétablis.</summary>
    public List<string> Subjects { get; set; } = new();
    public DateTime? ResolvedUtc { get; set; }
    public List<Guid> ReadBy { get; set; } = new();
    public List<Guid> DeletedBy { get; set; } = new();
}

public static class Notifs
{
    /// <summary>Icône (nom de <c>Ico</c>) et libellé de la catégorie d'une notification.</summary>
    public static (string Icon, string Label) Category(string kind) => kind switch
    {
        Alerts.Screens => ("monitor", "Écrans"),
        Alerts.Sync => ("refresh", "Synchronisations"),
        Alerts.Members => ("team", "Équipe et partage"),
        Alerts.Billing => ("card", "Abonnement"),
        _ => ("star", "Nouveautés et système"),
    };

    public const string Info = "info", Ok = "ok", Warn = "warn", Error = "error";
    /// <summary>Durée de conservation ; les notifications d'abonnement restent un an.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(90), KeepBilling = TimeSpan.FromDays(365);
    /// <summary>Les notifications que le groupement par jour regroupe sous un en-tête.</summary>
    public static string DayLabel(DateTime utc, DateTime nowUtc, string timezone)
    {
        var tz = TimeZoneInfo.Utc;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(timezone); } catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        var d = TimeZoneInfo.ConvertTimeFromUtc(utc, tz).Date;
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz).Date;
        return d == today ? "Aujourd'hui" : d == today.AddDays(-1) ? "Hier" : "Plus ancien";
    }

    /// <summary>« il y a 2 min », « 09:12 », « hier 17:05 », « lundi 16:48 », sinon la date.</summary>
    public static string When(DateTime utc, DateTime nowUtc, string timezone)
    {
        var age = nowUtc - utc;
        if (age < TimeSpan.FromMinutes(1)) return "à l'instant";
        if (age < TimeSpan.FromHours(1)) return $"il y a {(int)age.TotalMinutes} min";
        var tz = TimeZoneInfo.Utc;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(timezone); } catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        var l = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz).Date;
        var fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        if (l.Date == today) return l.ToString("HH:mm", fr);
        if (l.Date == today.AddDays(-1)) return "hier " + l.ToString("HH:mm", fr);
        if (today - l.Date < TimeSpan.FromDays(7)) return l.ToString("dddd HH:mm", fr);
        return l.ToString("d MMM", fr);
    }
}

/// <summary>Lit et écrit les notifications d'une organisation. Ne gère que la base : les réglages d'alertes sont vérifiés par <see cref="AlertService"/>.</summary>
public class NotificationCenter(JsonStore store)
{
    /// <summary>Un même événement (même <see cref="Notification.DedupeKey"/>) se regroupe pendant ce délai.</summary>
    private static readonly TimeSpan Merge = TimeSpan.FromHours(6);

    /// <param name="readBy">Personne qui vient d'agir : elle n'a pas à la lire.</param>
    public void Add(Guid clientId, string kind, string severity, string title, string body, string path = "/", Guid? readBy = null,
        string? dedupe = null, IEnumerable<string>? subjects = null)
    {
        var now = DateTime.UtcNow;
        store.Write(db =>
        {
            db.Notifications.RemoveAll(n => n.ClientId == clientId && now - n.CreatedUtc > (n.Kind == Alerts.Billing ? Notifs.KeepBilling : Notifs.Keep));
            if (dedupe != null && db.Notifications.FirstOrDefault(n => n.ClientId == clientId && n.DedupeKey == dedupe && n.ResolvedUtc == null && now - n.UpdatedUtc < Merge) is { } same)
            {
                same.Count++; same.UpdatedUtc = now; same.Title = title; same.Body = body; same.Severity = severity;
                same.ReadBy.Clear(); same.DeletedBy.Clear();
                if (readBy is { } who) same.ReadBy.Add(who);
                return;
            }
            var n = new Notification { ClientId = clientId, Kind = kind, Severity = severity, Title = title, Body = body, Path = path, DedupeKey = dedupe, Subjects = subjects?.ToList() ?? new() };
            if (readBy is { } r) n.ReadBy.Add(r);
            db.Notifications.Add(n);
        });
    }

    /// <summary>Des écrans sont de retour : les notifications « hors ligne » qui ne les concernent plus se résolvent (rangées dans les lues).</summary>
    public void Back(Guid clientId, IEnumerable<string> names)
    {
        var back = names.ToHashSet();
        if (back.Count == 0) return;
        var now = DateTime.UtcNow;
        store.Write(db =>
        {
            var admins = db.Users.Where(u => u.ClientId == clientId && u.Role == Roles.ClientAdmin).Select(u => u.Id).ToList();
            foreach (var n in db.Notifications.Where(n => n.ClientId == clientId && n.Kind == Alerts.Screens && n.ResolvedUtc == null && n.Subjects.Count > 0 && n.Subjects.Any(back.Contains)))
            {
                n.Subjects.RemoveAll(back.Contains);
                n.UpdatedUtc = now;
                if (n.Subjects.Count == 0)
                {
                    n.ResolvedUtc = now; n.Severity = Notifs.Ok; n.Body = "Rétabli : " + n.Body;
                    foreach (var a in admins) if (!n.ReadBy.Contains(a)) n.ReadBy.Add(a);
                }
                else
                {
                    n.Title = n.Subjects.Count == 1 ? $"Écran hors ligne : {n.Subjects[0]}" : $"{n.Subjects.Count} écrans hors ligne";
                    n.Body = string.Join(", ", n.Subjects.Take(3)) + (n.Subjects.Count > 3 ? "…" : "");
                }
            }
        });
    }

    /// <summary>Notifications visibles par cette personne, les plus récentes d'abord.</summary>
    public List<Notification> For(Guid clientId, Guid userId) => store.Read(db => db.Notifications
        .Where(n => n.ClientId == clientId && !n.DeletedBy.Contains(userId)).OrderByDescending(n => n.UpdatedUtc).ToList());

    public int Unread(Guid clientId, Guid userId) => store.Read(db => db.Notifications.Count(n => n.ClientId == clientId && !n.DeletedBy.Contains(userId) && !n.ReadBy.Contains(userId)));

    public void SetRead(Guid clientId, Guid userId, IEnumerable<Guid> ids, bool read)
    {
        var set = ids.ToHashSet();
        store.Write(db =>
        {
            foreach (var n in db.Notifications.Where(n => n.ClientId == clientId && set.Contains(n.Id)))
                if (read) { if (!n.ReadBy.Contains(userId)) n.ReadBy.Add(userId); } else n.ReadBy.Remove(userId);
        });
    }

    public void ReadAll(Guid clientId, Guid userId) => store.Write(db =>
    {
        foreach (var n in db.Notifications.Where(n => n.ClientId == clientId && !n.ReadBy.Contains(userId) && !n.DeletedBy.Contains(userId))) n.ReadBy.Add(userId);
    });

    public void Delete(Guid clientId, Guid userId, IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        store.Write(db =>
        {
            foreach (var n in db.Notifications.Where(n => n.ClientId == clientId && set.Contains(n.Id)))
                if (!n.DeletedBy.Contains(userId)) n.DeletedBy.Add(userId);
            // une notification supprimée par tous les administrateurs disparaît pour de bon
            var admins = db.Users.Where(u => u.ClientId == clientId && u.Role == Roles.ClientAdmin).Select(u => u.Id).ToList();
            db.Notifications.RemoveAll(n => n.ClientId == clientId && admins.Count > 0 && admins.All(n.DeletedBy.Contains));
        });
    }

    /// <summary>Une fois : message de bienvenue du centre dans chaque organisation (type « Nouveautés et système »).</summary>
    public static void Seed(Db db)
    {
        const string key = "notifications-welcome-v1";
        if (db.Done.Contains(key)) return;
        db.Done.Add(key);
        foreach (var c in db.Clients)
            db.Notifications.Add(new Notification
            {
                ClientId = c.Id, Kind = Alerts.News, Severity = Notifs.Info, Title = "Nouveau : le centre de notifications",
                Body = "Retrouvez ici tout ce qui demande votre attention. Choisissez de quoi être prévenu dans Réglages › Alertes et pause.", Path = "/settings?tab=supervision",
            });
    }
}
