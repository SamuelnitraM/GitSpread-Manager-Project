namespace GitSpread.Core.Git;

/// <summary>Réglages GitSpread propres à un projet, rangés dans son .git/config (section [gitspread]).</summary>
public sealed partial class GitService
{
    public const string PushAccountKey = "gitspread.pushAccount";

    /// <summary>Compte qui échange avec GitHub pour ce projet (« Envoyer avec le compte »). Null : le propriétaire du repo.</summary>
    public async Task<GitResult> SetPushAccountAsync(string repositoryPath, string? login, CancellationToken ct = default)
    {
        if (login is not null) return await ActAsync(repositoryPath, ["config", "--local", PushAccountKey, login], null, ct);

        // « --unset » échoue (code 5) si la clé n'existe pas : ce n'est pas une erreur ici.
        var r = await ActAsync(repositoryPath, ["config", "--local", "--unset", PushAccountKey], null, ct);
        return r.Process.ExitCode == 5 ? r with { Process = r.Process with { ExitCode = 0 } } : r;
    }
}
