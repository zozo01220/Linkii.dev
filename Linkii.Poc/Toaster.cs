namespace Linkii.Poc;

/// <summary>Messages de confirmation d'action (« Paramètres enregistrés. ») ou d'erreur, affichés en popup en haut de page
/// (composant Toasts du layout). Un par circuit Blazor : chaque onglet a ses propres messages.</summary>
public class Toaster
{
    /// <summary>Action : bouton facultatif du message (ex. « Annuler » après un retrait), qui ferme le message une fois cliqué.</summary>
    public record Toast(Guid Id, string Text, bool Ok, string? ActionLabel = null, Func<Task>? Action = null);

    private readonly List<Toast> items = new();
    private readonly object gate = new();

    public event Action? Changed;

    public IReadOnlyList<Toast> Items { get { lock (gate) return items.ToList(); } }

    public void Ok(string? text) => Add(text, true);
    public void Error(string? text) => Add(text, false);
    /// <summary>Confirmation avec un bouton (« Annuler »…) ; le message reste un peu plus longtemps pour laisser le temps de cliquer.</summary>
    public void Ok(string? text, string actionLabel, Func<Task> action) => Add(text, true, actionLabel, action);

    /// <summary>Bouton du message : l'action, puis le message se ferme.</summary>
    public async Task Run(Toast t)
    {
        Dismiss(t.Id);
        if (t.Action != null) await t.Action();
    }

    public void Dismiss(Guid id)
    {
        bool removed;
        lock (gate) removed = items.RemoveAll(t => t.Id == id) > 0;
        if (removed) Changed?.Invoke();
    }

    private void Add(string? text, bool ok, string? actionLabel = null, Func<Task>? action = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var t = new Toast(Guid.NewGuid(), text.Trim(), ok, actionLabel, action);
        lock (gate)
        {
            items.RemoveAll(x => x.Text == t.Text);   // même message répété : un seul affiché
            items.Add(t);
            if (items.Count > 3) items.RemoveAt(0);
        }
        Changed?.Invoke();
        _ = DismissLater(t);
    }

    // une erreur reste plus longtemps : il faut avoir le temps de la lire
    private async Task DismissLater(Toast t)
    {
        await Task.Delay(t.Action != null ? 6000 : t.Ok ? 3500 : 8000);
        Dismiss(t.Id);
    }
}
