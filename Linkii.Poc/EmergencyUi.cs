namespace Linkii.Poc;

/// <summary>
/// État de l'interface d'urgence du back-office, partagé dans la session : le bouton « Urgence » de la barre du haut, la pastille et le bandeau rouges, le bouton
/// flottant du mobile et le tiroir (formulaire, rapport, historique) l'ouvrent ou le ferment ici. Maquette : maquettes/urgence.html.
/// </summary>
public class EmergencyUi
{
    /// <summary>null, form (déclencher), report (rapport d'une urgence), history ou templates (modèles de message).</summary>
    public string? Open { get; private set; }
    /// <summary>Urgence dont le rapport est ouvert, ou dont la levée attend la confirmation.</summary>
    public Guid? ReportId { get; private set; }
    public Guid? LiftId { get; private set; }
    public event Action? Changed;

    public void OpenForm() { Open = "form"; Changed?.Invoke(); }
    public void OpenReport(Guid id) { Open = "report"; ReportId = id; Changed?.Invoke(); }
    public void OpenTemplates() { Open = "templates"; Changed?.Invoke(); }
    public void OpenHistory() { Open = "history"; Changed?.Invoke(); }
    public void Close() { Open = null; ReportId = null; Changed?.Invoke(); }
    /// <summary>Un clic sur la pastille ou le bandeau : la fenêtre de confirmation pour lever l'urgence.</summary>
    public void AskLift(Guid id) { LiftId = id; Changed?.Invoke(); }
    public void CancelLift() { LiftId = null; Changed?.Invoke(); }
}
