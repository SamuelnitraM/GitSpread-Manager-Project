using GitSpread.Core.Accounts;

namespace GitSpread.Core.Git;

/// <summary>Choisit comment git doit s'authentifier pour un projet donné.</summary>
public sealed class GitCredentialResolver(AccountService accounts)
{
    /// <summary>
    /// HTTPS vers l'hôte du compte : token du compte injecté pour cette seule commande.
    /// SSH : les clés de l'utilisateur (~/.ssh) s'appliquent. Sinon : config git de l'utilisateur, sans prompt.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> EnvironmentForAsync(GitHubAccount? account, RemoteUrl? remote,
        CancellationToken cancellationToken = default)
    {
        if (account is null || remote is not { Protocol: RemoteProtocol.Https }
            || !remote.Host.Equals(account.Host, StringComparison.OrdinalIgnoreCase))
            return GitCredentialEnvironment.NonInteractive();

        var token = await accounts.GetTokenAsync(account, cancellationToken);
        return GitCredentialEnvironment.For(account.Login, token, account.Host);
    }
}
