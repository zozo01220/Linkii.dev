using Microsoft.AspNetCore.Components;

namespace Linkii.Poc;

/// <summary>État de l'urgence pour l'organisation, tel que l'interface le montre : l'intégration est-elle utilisable, laquelle est en cours, combien d'écrans l'affichent.</summary>
public record EmergencySnapshot(bool Enabled, Emergency? Main, int ActiveCount, int Displayed, int Targeted)
{
    public static readonly EmergencySnapshot None = new(false, null, 0, 0, 0);
}

/// <summary>Base des composants d'urgence du back-office (bouton, pastille, bandeau, tiroir) : se met à jour seule quand une urgence change ou qu'une donnée est écrite.</summary>
public abstract class EmergencyComponent : ComponentBase, IDisposable
{
    [Inject] protected EmergencyService Svc { get; set; } = default!;
    [Inject] protected EmergencyUi Ui { get; set; } = default!;
    [Inject] protected TenantContext Ctx { get; set; } = default!;
    [Inject] protected JsonStore Root { get; set; } = default!;

    protected Guid ClientId { get; private set; }
    protected EmergencySnapshot Snap => ClientId == Guid.Empty ? EmergencySnapshot.None : Svc.Snapshot(ClientId);
    private int pending;

    protected override void OnInitialized()
    {
        ClientId = Ctx.ClientId ?? Guid.Empty;
        Svc.Changed += OnSvc;
        Root.Changed += OnStore;
        Ui.Changed += OnUi;
    }

    private void OnSvc(Guid id) { if (id == ClientId) Refresh(); }
    private void OnUi() => Refresh();
    private void OnStore()
    {
        // une écriture de données (intégration installée ou retirée, plan changé…) : un seul rafraîchissement dans la demi-seconde
        if (Interlocked.Exchange(ref pending, 1) == 1) return;
        _ = Task.Delay(500).ContinueWith(_ => { Interlocked.Exchange(ref pending, 0); Refresh(); });
    }

    private void Refresh() { try { _ = InvokeAsync(StateHasChanged); } catch (ObjectDisposedException) { } }

    /// <summary>Heure locale de l'organisation (« 14:02 »).</summary>
    protected string Hm(DateTime utc)
    {
        var tz = Root.Read(db => db.Clients.FirstOrDefault(c => c.Id == ClientId)?.Timezone) ?? "UTC";
        try { utc = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(tz)); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return utc.ToString("HH:mm");
    }

    public virtual void Dispose() { Svc.Changed -= OnSvc; Root.Changed -= OnStore; Ui.Changed -= OnUi; }
}
