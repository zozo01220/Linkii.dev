namespace Linkii.Poc;

/// <summary>
/// Échanges entre le serveur Linkii et le relais Linkii (§139, lot 4). Fichier partagé : compilé aussi dans Linkii.Relay.
/// Le relais, installé dans le réseau du client, appelle le serveur (connexion sortante HTTPS seulement) :
///   POST /api/relay/pair  { code, name, machine, version }        → { token, name }   (une fois, avec le code affiché dans le back-office)
///   GET  /api/relay/next  (en-tête X-Relay-Token, attente 25 s)   → 200 RelayJob, ou 204 s'il n'y a rien à faire
///   POST /api/relay/reply (en-tête X-Relay-Token) RelayReply      → 200
/// Le serveur n'ouvre jamais de connexion vers le client : rien à ouvrir dans le pare-feu.
/// </summary>
public record RelayJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>sql (base de données) | http (API ou fichier à une adresse du réseau interne).</summary>
    public string Kind { get; init; } = "sql";
    /// <summary>test | discover | read.</summary>
    public string Op { get; init; } = "read";
    public DataSql.Target? Db { get; init; }
    public string? Table { get; init; }
    public string? Sql { get; init; }
    public int MaxRows { get; init; } = 5000;
    public string? Url { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public int MaxBytes { get; init; } = 2_000_000;
}

public record RelayReply
{
    public Guid Id { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }
    /// <summary>Test : message lisible (version, droits).</summary>
    public string? Message { get; init; }
    public List<string>? Headers { get; init; }
    public List<List<string?>>? Rows { get; init; }
    public List<string>? Tables { get; init; }
    /// <summary>http : code de la réponse et contenu (base64).</summary>
    public int Status { get; init; }
    public string? Body64 { get; init; }
}

public record RelayPairRequest(string Code, string? Name, string? Machine, string? Version);
public record RelayPairResponse(string Token, string Name);
