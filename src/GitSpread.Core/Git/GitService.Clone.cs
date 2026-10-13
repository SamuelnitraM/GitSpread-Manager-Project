namespace GitSpread.Core.Git;

public sealed partial class GitService
{
    /// <summary>Clone un repo dans <paramref name="parentDirectory"/>\<paramref name="folderName"/>, avec le bon compte.</summary>
    public async Task<GitResult> CloneAsync(string parentDirectory, string url, string folderName,
        IReadOnlyDictionary<string, string> auth, CancellationToken ct = default)
    {
        Directory.CreateDirectory(parentDirectory);
        return await ActAsync(parentDirectory, ["clone", "--origin", "origin", url, folderName], auth, ct, TimeSpan.FromMinutes(30));
    }

    /// <summary>Renomme une branche locale (git branch -m).</summary>
    public Task<GitResult> RenameLocalBranchAsync(string repositoryPath, string from, string to, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["branch", "-m", from, to], null, ct);

    public Task<GitResult> SetUpstreamAsync(string repositoryPath, string branch, string upstream, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["branch", $"--set-upstream-to={upstream}", branch], null, ct);

    /// <summary>Indique à git quelle est la branche par défaut du distant (refs/remotes/origin/HEAD), sans réseau.</summary>
    public Task<GitResult> SetRemoteHeadAsync(string repositoryPath, string remote, string branch, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["remote", "set-head", remote, branch], null, ct);
}
