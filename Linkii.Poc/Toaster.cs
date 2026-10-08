namespace Linkii.Poc;

/// <summary>Messages de confirmation d'action (« Paramètres enregistrés. ») ou d'erreur, affichés en popup en haut de page
/// (composant Toasts du layout). Un par circuit Blazor : chaque onglet a ses propres messages.</summary>
public class Toaster
{
    public record Toast(Guid Id, string Text, bool Ok);

    private readonly List<Toast> items = new();
    private readonly object gate = new();

    public event Action? Changed;

    public IReadOnlyList<Toast> Items { get { lock (gate) return items.ToList(); } }

    public void Ok(string? text) => Add(text, true);
    public void Error(string? text) => Add(text, false);

    public void Dismiss(Guid id)
    {
        bool removed;
        lock (gate) removed = items.RemoveAll(t => t.Id == id) > 0;
        if (removed) Changed?.Invoke();
    }

    private void Add(string? text, bool ok)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var t = new Toast(Guid.NewGuid(), text.Trim(), ok);
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
        await Task.Delay(t.Ok ? 3500 : 8000);
        Dismiss(t.Id);
    }
}
