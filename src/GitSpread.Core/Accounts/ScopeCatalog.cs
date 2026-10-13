namespace GitSpread.Core.Accounts;

/// <summary>Une autorisation OAuth de GitHub, expliquée en clair.</summary>
public sealed record ScopeInfo(string Name, string Label, string Description)
{
    /// <summary>Accordée d'office par gh et impossible à retirer (repo, read:org, gist).</summary>
    public bool IsRequired { get; init; }
}

/// <summary>Autorisations utiles à GitSpread, et lesquelles en couvrent d'autres.</summary>
public static class ScopeCatalog
{
    public const string DeleteRepo = "delete_repo";
    public const string AdminPublicKey = "admin:public_key";

    public static readonly IReadOnlyList<ScopeInfo> Known =
    [
        new("repo", "Repos (lecture et écriture)", "Lister, créer et envoyer du code sur tes repos, y compris privés.") { IsRequired = true },
        new("read:org", "Organisations (lecture)", "Voir les organisations dont tu fais partie.") { IsRequired = true },
        new("gist", "Gists", "Créer et modifier des gists.") { IsRequired = true },
        new("workflow", "GitHub Actions", "Envoyer des modifications de workflows (.github/workflows)."),
        new(DeleteRepo, "Suppression de repos", "Supprimer définitivement un repo depuis GitSpread. À n'ajouter que si tu en as besoin."),
        new(AdminPublicKey, "Clés SSH", "Ajouter et retirer des clés SSH sur le compte."),
    ];

    /// <summary>Autorisations plus larges qui incluent celle demandée.</summary>
    private static readonly Dictionary<string, string[]> CoveredBy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read:public_key"] = ["write:public_key", AdminPublicKey],
        ["write:public_key"] = [AdminPublicKey],
        ["read:org"] = ["write:org", "admin:org"],
        ["write:org"] = ["admin:org"],
        ["public_repo"] = ["repo"],
        ["repo:status"] = ["repo"],
    };

    public static bool IsCovered(string scope, IReadOnlySet<string> granted) =>
        granted.Contains(scope) || CoveredBy.TryGetValue(scope, out var wider) && wider.Any(granted.Contains);

    public static ScopeInfo Describe(string scope) =>
        Known.FirstOrDefault(s => s.Name.Equals(scope, StringComparison.OrdinalIgnoreCase))
        ?? new ScopeInfo(scope, scope, "Autorisation GitHub.");
}
