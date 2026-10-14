namespace GitSpread.Core.Git;

/// <summary>État d'une branche locale par rapport à sa version sur un lien (remote), au dernier « Récupérer ».</summary>
/// <param name="Exists">Faux : la branche n'a encore jamais été envoyée sur ce lien.</param>
public sealed record RemoteBranchState(bool Exists, int Ahead, int Behind);

/// <summary>F3B : un projet lié à plusieurs repos (un remote par repo).</summary>
public sealed partial class GitService
{
    public const string PushRemotesKey = "gitspread.pushRemotes";

    /// <summary>Compare la branche locale avec <c>remote/branche</c> (sans réseau : d'après le dernier fetch).</summary>
    public async Task<RemoteBranchState> CompareWithRemoteAsync(string repositoryPath, string remote, string branch, CancellationToken ct = default)
    {
        var exists = await RunAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/{remote}/{branch}"], null, ct);
        if (!exists.Succeeded) return new RemoteBranchState(false, 0, 0);
        var (ahead, behind) = await CompareAsync(repositoryPath, branch, $"refs/remotes/{remote}/{branch}", ct);
        return new RemoteBranchState(true, ahead, behind);
    }

    /// <summary>La branche est connue sur ce lien (d'après le dernier fetch), qu'elle existe ou non sur ce poste.</summary>
    public async Task<bool> RemoteBranchExistsAsync(string repositoryPath, string remote, string branch, CancellationToken ct = default) =>
        (await RunAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", $"refs/remotes/{remote}/{branch}"], null, ct)).Succeeded;

    public Task<GitResult> UnsetUpstreamAsync(string repositoryPath, string branch, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["branch", "--unset-upstream", branch], null, ct);

    /// <summary>Liens cochés pour « Pousser », mémorisés pour la prochaine fois. Le principal seul : rien à mémoriser.</summary>
    public async Task<GitResult> SetPushRemotesAsync(string repositoryPath, IReadOnlyCollection<string> remotes, CancellationToken ct = default)
    {
        if (remotes.Count > 0) return await ActAsync(repositoryPath, ["config", "--local", PushRemotesKey, string.Join(',', remotes)], null, ct);
        var r = await ActAsync(repositoryPath, ["config", "--local", "--unset", PushRemotesKey], null, ct);
        return r.Process.ExitCode == 5 ? r with { Process = r.Process with { ExitCode = 0 } } : r;
    }
}
