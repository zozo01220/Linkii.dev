namespace Linkii.Poc;

/// <summary>Un choix d'un critère de filtre (valeur vide = « Tous »), avec son effectif et une info-bulle facultatifs.</summary>
public record FilterOption(string Value, string Label, int? Count = null, string? Hint = null);

/// <summary>Un critère du menu « Filtres » (voir FilterMenu) : sa clé, son titre, sa valeur courante et ses choix.</summary>
public record FilterGroup(string Key, string Title, string Value, IReadOnlyList<FilterOption> Options);

/// <summary>Un filtre actif qui n'est pas un critère (ex. : la portée « toute l'aire »), avec le moyen de le retirer.</summary>
public record FilterExtra(string Label, Action Clear);
