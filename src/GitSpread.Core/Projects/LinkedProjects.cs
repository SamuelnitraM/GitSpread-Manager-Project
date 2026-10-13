using GitSpread.Core.Accounts;
using GitSpread.Core.Git;

namespace GitSpread.Core.Projects;

/// <summary>
/// Après une opération sur un repo GitHub (renommage, transfert, suppression, branche par défaut renommée),
/// met à jour les projets locaux qui y sont liés pour qu'ils restent cohérents.
/// </summary>
public sealed class LinkedProjects(GitService git, GitCredentialResolver credentials)
{
    /// <summary>Projets locaux dont le remote principal pointe vers ce repo (hôte + propriétaire/nom, sans tenir compte de la casse).</summary>
    public static IReadOnlyList<LocalProject> Find(IEnumerable<LocalProject> projects, string host, string fullName) =>
        projects.Where(p => p.Remote is { } r
                            && r.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(r.FullName, fullName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Le repo a changé de nom ou de propriétaire : le lien local suit (même forme d'URL).</summary>
    public async Task<GitResult> FollowAsync(LocalProject project, string newOwner, string newName, CancellationToken ct = default)
    {
        var newUrl = RemoteUrlRewriter.Rewrite(project.Remote!.Url, newOwner, newName);
        return await git.SetRemoteUrlAsync(project.Path, project.RemoteName ?? "origin", newUrl, ct);
    }

    /// <summary>Le repo n'existe plus : on retire le lien mort (les fichiers et l'historique local ne bougent pas).</summary>
    public Task<GitResult> UnlinkAsync(LocalProject project, CancellationToken ct = default) =>
        git.RemoveRemoteAsync(project.Path, project.RemoteName ?? "origin", ct);

    /// <summary>
    /// La branche par défaut a été renommée sur GitHub (ex. master → main) : récupère la nouvelle, renomme la branche
    /// locale si elle existe, la relie à sa nouvelle version distante et met à jour la branche par défaut connue.
    /// </summary>
    public async Task<IReadOnlyList<GitResult>> FollowBranchRenameAsync(LocalProject project, GitHubAccount? account, string from, string to,
        CancellationToken ct = default)
    {
        var remote = project.RemoteName ?? "origin";
        var steps = new List<GitResult>();
        var auth = await credentials.EnvironmentForAsync(account, project.Remote, ct);
        steps.Add(await git.FetchAsync(project.Path, remote, auth, ct));
        if (!steps[^1].Succeeded) return steps;

        var branches = await git.ListBranchesAsync(project.Path, ct);
        if (branches.Any(b => b.IsLocal && b.Name == from) && branches.All(b => !(b.IsLocal && b.Name == to)))
        {
            steps.Add(await git.RenameLocalBranchAsync(project.Path, from, to, ct));
            if (!steps[^1].Succeeded) return steps;
        }
        if ((await git.ListBranchesAsync(project.Path, ct)).Any(b => b.IsLocal && b.Name == to))
            steps.Add(await git.SetUpstreamAsync(project.Path, to, $"{remote}/{to}", ct));
        steps.Add(await git.SetRemoteHeadAsync(project.Path, remote, to, ct));
        return steps;
    }
}
