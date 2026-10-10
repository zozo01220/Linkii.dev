using Microsoft.AspNetCore.SignalR;

namespace Linkii.Poc.Hubs;

public class ScreenHub(JsonStore store) : Hub
{
    public static string Group(Guid screenId) => $"screen-{screenId}";

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()!.Request.Query["access_token"].ToString();
        var id = store.Read(db => db.Screens.FirstOrDefault(s => s.Token != null && s.Token == token && Tenancy.IsActive(db, s))?.Id);
        if (id == null) { Context.Abort(); return; }
        Context.Items["sid"] = id.Value;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(id.Value));
        Touch();
        // l'écran se reconnecte : un redémarrage demandé est exécuté
        store.Write(db => { if (db.Screens.FirstOrDefault(x => x.Id == id.Value) is { } s) ScreenControl.CompleteRestarts(s, DateTime.UtcNow); });
        await base.OnConnectedAsync();
    }

    public void Ping() => Touch();

    private void Touch()
    {
        if (Context.Items["sid"] is Guid id)
            store.Read(db => { var s = db.Screens.FirstOrDefault(x => x.Id == id); if (s != null) s.LastSeenUtc = DateTime.UtcNow; return 0; });
    }
}
