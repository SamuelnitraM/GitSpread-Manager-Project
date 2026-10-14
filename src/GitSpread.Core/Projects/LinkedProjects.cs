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

    // Remote concerné : celui qui pointe vers le repo indiqué (quel que soit l'hôte du lien principal), sinon le principal.
    // Si le repo est indiqué mais qu'aucun lien n'y pointe, on refuse plutôt que de toucher à un autre lien.
    private static ProjectRemote Target(LocalProject project, string? fullName, string? host = null)
    {
        if (fullName is null) return new ProjectRemote(project.RemoteName ?? "origin", project.Remote!);
        return project.AllRemotes
                   .Where(r => string.Equals(r.Url.FullName, fullName, StringComparison.OrdinalIgnoreCase))
                   .Where(r => host is null || r.Url.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
                   .OrderBy(r => r.Name == project.RemoteName ? 0 : 1)
                   .FirstOrDefault()
               ?? throw new InvalidOperationException($"Aucun lien de « {project.Name} » ne pointe vers {fullName}.");
    }

    /// <summary>Le repo a changé de nom ou de propriétaire : le lien local suit (même forme d'URL).</summary>
    /// <param name="fullName">Ancien nom complet du repo : désigne le lien à mettre à jour (le principal par défaut).</param>
    public async Task<GitResult> FollowAsync(LocalProject project, string newOwner, string newName, string? fullName = null, CancellationToken ct = default,
        string? host = null)
    {
        var remote = Target(project, fullName, host);
        var newUrl = RemoteUrlRewriter.Rewrite(remote.Url.Url, newOwner, newName);
        return await git.SetRemoteUrlAsync(project.Path, remote.Name, newUrl, ct);
    }

    /// <summary>Le repo n'existe plus : on retire le lien mort (les fichiers et l'historique local ne bougent pas).</summary>
    public Task<GitResult> UnlinkAsync(LocalProject project, string? fullName = null, CancellationToken ct = default, string? host = null) =>
        git.RemoveRemoteAsync(project.Path, Target(project, fullName, host).Name, ct);

    /// <summary>
    /// Une branche a été renommée sur GitHub (ex. master → main) : récupère la nouvelle, renomme la branche
    /// locale si elle existe et la relie à sa nouvelle version distante. Pour la branche par défaut
    /// (<paramref name="isDefault"/>), met aussi à jour la branche principale connue de git.
    /// </summary>
    public async Task<IReadOnlyList<GitResult>> FollowBranchRenameAsync(LocalProject project, GitHubAccount? account, string from, string to,
        string? fullName = null, bool isDefault = true, CancellationToken ct = default)
    {
        var target = Target(project, fullName, account?.Host);
        var remote = target.Name;
        var steps = new List<GitResult>();
        var auth = await credentials.EnvironmentForAsync(account, target.Url, ct);
        steps.Add(await git.FetchAsync(project.Path, remote, auth, ct));
        if (!steps[^1].Succeeded) return steps;
        if (await git.ForgetRemoteBranchAsync(project.Path, remote, from, ct) is { } forgotten) steps.Add(forgotten); // l'ancien nom n'existe plus sur GitHub

        // Lien secondaire : les branches locales suivent le lien principal, on ne fait que mettre à jour ce que sait git du repo.
        if (remote != (project.RemoteName ?? "origin"))
        {
            if (isDefault) steps.Add(await git.SetRemoteHeadAsync(project.Path, remote, to, ct));
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
        if (isDefault) steps.Add(await git.SetRemoteHeadAsync(project.Path, remote, to, ct));
        return steps;
    }
}
