using GitSpread.Core.Accounts;
using GitSpread.Core.Git;

namespace GitSpread.Core.Projects;

/// <summary>
/// Après une opération sur un repo GitHub (renommage, transfert, suppression, branche par défaut renommée),
/// met à jour les projets locaux qui y sont liés pour qu'ils restent cohérents.
/// </summary>
public sealed class LinkedProjects(GitService git, GitCredentialResolver credentials)
{
    /// <summary>Projets locaux dont un remote (principal ou secondaire) pointe vers ce repo (hôte + propriétaire/nom, sans tenir compte de la casse).</summary>
    public static IReadOnlyList<LocalProject> Find(IEnumerable<LocalProject> projects, string host, string fullName) =>
        projects.Where(p => RemoteFor(p, host, fullName) is not null).ToList();

    /// <summary>Remote du projet qui pointe vers ce repo (le principal en priorité).</summary>
    public static ProjectRemote? RemoteFor(LocalProject project, string host, string fullName) =>
        project.AllRemotes
            .Where(r => r.Url.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Url.FullName, fullName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name == project.RemoteName ? 0 : 1)
            .FirstOrDefault();

    // Remote concerné : celui qui pointe vers le repo indiqué, sinon le principal.
    private static ProjectRemote Target(LocalProject project, string? fullName) =>
        (fullName is not null && project.Remote is { } primary ? RemoteFor(project, primary.Host, fullName) : null)
        ?? new ProjectRemote(project.RemoteName ?? "origin", project.Remote!);

    /// <summary>Le repo a changé de nom ou de propriétaire : le lien local suit (même forme d'URL).</summary>
    /// <param name="fullName">Ancien nom complet du repo : désigne le lien à mettre à jour (le principal par défaut).</param>
    public async Task<GitResult> FollowAsync(LocalProject project, string newOwner, string newName, string? fullName = null, CancellationToken ct = default)
    {
        var remote = Target(project, fullName);
        var newUrl = RemoteUrlRewriter.Rewrite(remote.Url.Url, newOwner, newName);
        return await git.SetRemoteUrlAsync(project.Path, remote.Name, newUrl, ct);
    }

    /// <summary>Le repo n'existe plus : on retire le lien mort (les fichiers et l'historique local ne bougent pas).</summary>
    public Task<GitResult> UnlinkAsync(LocalProject project, string? fullName = null, CancellationToken ct = default) =>
        git.RemoveRemoteAsync(project.Path, Target(project, fullName).Name, ct);

    /// <summary>
    /// La branche par défaut a été renommée sur GitHub (ex. master → main) : récupère la nouvelle, renomme la branche
    /// locale si elle existe, la relie à sa nouvelle version distante et met à jour la branche par défaut connue.
    /// </summary>
    public async Task<IReadOnlyList<GitResult>> FollowBranchRenameAsync(LocalProject project, GitHubAccount? account, string from, string to,
        string? fullName = null, CancellationToken ct = default)
    {
        var target = Target(project, fullName);
        var remote = target.Name;
        var steps = new List<GitResult>();
        var auth = await credentials.EnvironmentForAsync(account, target.Url, ct);
        steps.Add(await git.FetchAsync(project.Path, remote, auth, ct));
        if (!steps[^1].Succeeded) return steps;

        // Lien secondaire : les branches locales suivent le lien principal, on ne fait que mettre à jour ce que sait git du repo.
        if (remote != (project.RemoteName ?? "origin"))
        {
            steps.Add(await git.SetRemoteHeadAsync(project.Path, remote, to, ct));
            return steps;
        }

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
