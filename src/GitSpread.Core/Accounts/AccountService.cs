using System.Text.Json;
using GitSpread.Core.GitHub;

namespace GitSpread.Core.Accounts;

public sealed record GitHubAccount(string Login, string Host, bool IsActive, bool IsAuthenticated)
{
    public string? Scopes { get; init; }
    public string GitProtocol { get; init; } = "https";

    /// <summary>Message d'erreur de gh quand le token n'est plus valide (IsAuthenticated = false).</summary>
    public string? Error { get; init; }
}

/// <summary>Détecte les comptes GitHub connectés via gh et fournit leurs tokens à la demande.</summary>
public sealed class AccountService(GhCli gh)
{
    public const string DefaultHost = "github.com";

    /// <summary>Liste les comptes connectés (tous hôtes). Liste vide si aucun compte n'est connecté.</summary>
    public async Task<IReadOnlyList<GitHubAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        // gh renvoie un code non nul si un des comptes est en erreur, mais le JSON reste exploitable.
        var result = await gh.RunAsync(["auth", "status", "--json", "hosts"], cancellationToken: cancellationToken);
        return ParseAuthStatus(result.StandardOutput);
    }

    /// <summary>Token du compte, récupéré dans le trousseau via gh. Jamais stocké par GitSpread.</summary>
    public async Task<string> GetTokenAsync(GitHubAccount account, CancellationToken cancellationToken = default)
    {
        var output = await gh.RunCheckedAsync(
            ["auth", "token", "--hostname", account.Host, "--user", account.Login], cancellationToken: cancellationToken);
        var token = output.Trim();
        return token.Length > 0 ? token : throw new InvalidOperationException($"gh n'a renvoyé aucun token pour {account.Login}.");
    }

    /// <summary>Rend ce compte actif dans gh (gh auth switch). N'a aucun effet sur les actions de GitSpread,
    /// qui choisit toujours lui-même le compte, mais change le compte utilisé par gh dans le terminal.</summary>
    public Task SwitchAsync(GitHubAccount account, CancellationToken cancellationToken = default) =>
        gh.RunCheckedAsync(["auth", "switch", "--hostname", account.Host, "--user", account.Login], cancellationToken: cancellationToken);

    internal static IReadOnlyList<GitHubAccount> ParseAuthStatus(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return []; } // ex. « You are not logged into any GitHub hosts »

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Object)
                return [];

            var accounts = new List<GitHubAccount>();
            foreach (var host in hosts.EnumerateObject())
            foreach (var entry in host.Value.EnumerateArray())
            {
                var state = Str(entry, "state");
                accounts.Add(new GitHubAccount(
                    Login: Str(entry, "login") ?? "?",
                    Host: Str(entry, "host") ?? host.Name,
                    IsActive: entry.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True,
                    IsAuthenticated: state == "success")
                {
                    Scopes = Str(entry, "scopes"),
                    GitProtocol = Str(entry, "gitProtocol") ?? "https",
                    Error = state == "success" ? null : Str(entry, "error") ?? state,
                });
            }
            return accounts;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
