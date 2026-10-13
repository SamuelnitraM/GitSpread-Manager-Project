using GitSpread.Core.Accounts;

namespace GitSpread.Core.Projects;

public enum UnlinkedReason
{
    /// <summary>Dossier ajouté à la main, pas encore suivi par git.</summary>
    NotARepository,
    /// <summary>Aucun remote : projet uniquement local.</summary>
    NoRemote,
    /// <summary>Remote hébergé ailleurs que sur un hôte GitHub connecté (GitLab, chemin local...).</summary>
    OtherHost,
    /// <summary>Repo GitHub d'une organisation ou d'un compte non connecté.</summary>
    OtherOwner,
}

public sealed record ProjectPlacement(LocalProject Project, GitHubAccount? Account, UnlinkedReason? Reason);

/// <summary>Range chaque projet dans l'onglet du compte propriétaire de son remote.</summary>
public static class ProjectClassifier
{
    public static IReadOnlyList<ProjectPlacement> Classify(IEnumerable<LocalProject> projects, IReadOnlyList<GitHubAccount> accounts) =>
        projects.Select(p => Place(p, accounts)).ToList();

    public static ProjectPlacement Place(LocalProject project, IReadOnlyList<GitHubAccount> accounts)
    {
        if (!project.IsRepository) return new(project, null, UnlinkedReason.NotARepository);
        if (project.Remote is not { } remote) return new(project, null, UnlinkedReason.NoRemote);

        var onHost = accounts.Where(a => a.Host.Equals(remote.Host, StringComparison.OrdinalIgnoreCase)).ToList();
        if (onHost.Count == 0 || remote.Owner is null) return new(project, null, UnlinkedReason.OtherHost);

        var owner = onHost.FirstOrDefault(a => a.Login.Equals(remote.Owner, StringComparison.OrdinalIgnoreCase));
        return owner is null ? new(project, null, UnlinkedReason.OtherOwner) : new(project, owner, null);
    }
}
