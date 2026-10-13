using System.Text.Json;
using GitSpread.Core.Accounts;

namespace GitSpread.Core.GitHub;

public enum RepositoryVisibility { Public, Private, Internal }

public sealed record GitHubRepository(
    string Name,
    string Owner,
    string Url,
    string SshUrl,
    RepositoryVisibility Visibility)
{
    public string FullName => $"{Owner}/{Name}";
    public string? Description { get; init; }
    public bool IsArchived { get; init; }
    public bool IsFork { get; init; }
    public DateTimeOffset? PushedAt { get; init; }
}

/// <summary>Opérations côté GitHub, toujours exécutées avec le token du compte concerné.</summary>
public sealed partial class GitHubService(GhCli gh, AccountService accounts)
{
    public const int DefaultLimit = 200;

    private const string RepoFields = "name,owner,url,sshUrl,visibility,description,isArchived,isFork,pushedAt";

    /// <summary>
    /// Repos dont le compte est propriétaire. gh utilise le token du compte (GH_TOKEN) et non le compte
    /// actif : sans ça, les repos privés d'un compte inactif seraient invisibles.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepository>> ListRepositoriesAsync(
        GitHubAccount account, int limit = DefaultLimit, CancellationToken cancellationToken = default)
    {
        var env = await EnvironmentForAsync(account, cancellationToken);
        var json = await gh.RunCheckedAsync(
            ["repo", "list", account.Login, "--limit", limit.ToString(), "--json", RepoFields], env, cancellationToken);
        return ParseRepositories(json);
    }

    /// <summary>Profil du compte (id, nom, e-mail public) : sert à proposer la bonne identité de commit.</summary>
    public async Task<GitHubUser> GetUserAsync(GitHubAccount account, CancellationToken cancellationToken = default)
    {
        var env = await EnvironmentForAsync(account, cancellationToken);
        using var doc = JsonDocument.Parse(await gh.RunCheckedAsync(["api", "user"], env, cancellationToken));
        var root = doc.RootElement;
        return new GitHubUser(root.GetProperty("id").GetInt64(), root.GetProperty("login").GetString()!)
        {
            Name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name ? name : null,
            Email = root.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
        };
    }

    public async Task<bool> RepositoryExistsAsync(GitHubAccount account, string fullName, CancellationToken cancellationToken = default)
    {
        var env = await EnvironmentForAsync(account, cancellationToken);
        return (await gh.RunAsync(["repo", "view", fullName, "--json", "name"], env, cancellationToken)).Succeeded;
    }

    /// <summary>Crée un repo vide appartenant au compte et renvoie son URL HTTPS de clonage.
    /// Le code local est poussé ensuite par git, avec le token de ce compte.</summary>
    public async Task<string> CreateRepositoryAsync(GitHubAccount account, string name, RepositoryVisibility visibility,
        string? description = null, CancellationToken cancellationToken = default)
    {
        var env = await EnvironmentForAsync(account, cancellationToken);
        List<string> args = ["repo", "create", $"{account.Login}/{name}", visibility == RepositoryVisibility.Public ? "--public" : "--private"];
        if (!string.IsNullOrWhiteSpace(description)) args.AddRange(["--description", description.Trim()]);
        var output = await gh.RunCheckedAsync(args, env, cancellationToken);

        // gh affiche l'URL du repo créé sur la dernière ligne.
        var url = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0)
                  ?? $"https://{account.Host}/{account.Login}/{name}";
        return url.EndsWith(".git") ? url : url + ".git";
    }

    /// <summary>Demande le transfert natif d'un repo vers un autre propriétaire (API GitHub, token du propriétaire actuel).</summary>
    public async Task TransferRepositoryAsync(GitHubAccount owner, string fullName, string newOwner, CancellationToken cancellationToken = default)
    {
        var env = await EnvironmentForAsync(owner, cancellationToken);
        await gh.RunCheckedAsync(["api", "--method", "POST", $"repos/{fullName}/transfer", "-f", $"new_owner={newOwner}"], env, cancellationToken);
    }

    /// <summary>Noms des modèles .gitignore proposés par GitHub (Node, VisualStudio, Python...).</summary>
    public async Task<IReadOnlyList<string>> ListGitignoreTemplatesAsync(GitHubAccount account, CancellationToken cancellationToken = default)
    {
        var env = await EnvironmentForAsync(account, cancellationToken);
        using var doc = JsonDocument.Parse(await gh.RunCheckedAsync(["api", "gitignore/templates"], env, cancellationToken));
        return doc.RootElement.EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    public async Task<string> GetGitignoreTemplateAsync(GitHubAccount account, string template, CancellationToken cancellationToken = default)
    {
        var env = await EnvironmentForAsync(account, cancellationToken);
        using var doc = JsonDocument.Parse(await gh.RunCheckedAsync(["api", $"gitignore/templates/{template}"], env, cancellationToken));
        return doc.RootElement.GetProperty("source").GetString() ?? "";
    }

    /// <summary>Ajoute une clé publique SSH (authentification) au compte. Nécessite le scope admin:public_key.</summary>
    public async Task AddSshKeyAsync(GitHubAccount account, string publicKeyPath, string title, CancellationToken cancellationToken = default)
    {
        var env = await EnvironmentForAsync(account, cancellationToken);
        await gh.RunCheckedAsync(["ssh-key", "add", publicKeyPath, "--title", title, "--type", "authentication"], env, cancellationToken);
    }

    /// <summary>Environnement qui fait agir gh avec ce compte, quel que soit le compte actif.</summary>
    public async Task<IReadOnlyDictionary<string, string>> EnvironmentForAsync(GitHubAccount account, CancellationToken ct = default)
    {
        var token = await accounts.GetTokenAsync(account, ct);
        return account.Host == AccountService.DefaultHost
            ? new Dictionary<string, string> { ["GH_TOKEN"] = token, ["GH_HOST"] = account.Host }
            : new Dictionary<string, string> { ["GH_ENTERPRISE_TOKEN"] = token, ["GH_HOST"] = account.Host };
    }

    internal static IReadOnlyList<GitHubRepository> ParseRepositories(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => new GitHubRepository(
                Name: e.GetProperty("name").GetString()!,
                Owner: e.GetProperty("owner").GetProperty("login").GetString()!,
                Url: e.GetProperty("url").GetString()!,
                SshUrl: e.TryGetProperty("sshUrl", out var ssh) ? ssh.GetString() ?? "" : "",
                Visibility: ParseVisibility(e.TryGetProperty("visibility", out var v) ? v.GetString() : null))
            {
                Description = e.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                    && d.GetString() is { Length: > 0 } desc ? desc : null,
                IsArchived = e.TryGetProperty("isArchived", out var a) && a.GetBoolean(),
                IsFork = e.TryGetProperty("isFork", out var f) && f.GetBoolean(),
                PushedAt = e.TryGetProperty("pushedAt", out var p) && p.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(p.GetString(), out var date) ? date : null,
            })
            .ToList();
    }

    private static RepositoryVisibility ParseVisibility(string? value) => value?.ToUpperInvariant() switch
    {
        "PRIVATE" => RepositoryVisibility.Private,
        "INTERNAL" => RepositoryVisibility.Internal,
        _ => RepositoryVisibility.Public,
    };
}
