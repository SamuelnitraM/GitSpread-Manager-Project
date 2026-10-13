using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.Settings;

namespace GitSpread.Core.Projects;

public enum UnlinkedReason
{
    /// <summary>Dossier ajouté à la main, pas encore suivi par git.</summary>
    NotARepository,
    /// <summary>Aucun remote : projet uniquement local.</summary>
    NoRemote,
    /// <summary>Remote hébergé ailleurs que sur un hôte GitHub connecté (GitLab, chemin local...).</summary>
    OtherHost,
    /// <summary>Repo GitHub d'une organisation ou d'un compte non connecté (dont aucun compte connecté n'est collaborateur).</summary>
    OtherOwner,
}

/// <param name="Account">Compte connecté propriétaire du repo, s'il y en a un.</param>
public sealed record ProjectPlacement(LocalProject Project, GitHubAccount? Account, UnlinkedReason? Reason)
{
    /// <summary>Comptes connectés collaborateurs du repo (F3A) : le projet apparaît aussi dans leurs onglets.</summary>
    public IReadOnlyList<GitHubAccount> Collaborators { get; init; } = [];

    /// <summary>Comptes connectés pouvant échanger avec ce repo : le propriétaire puis les collaborateurs.</summary>
    public IReadOnlyList<GitHubAccount> AccessAccounts => Account is null ? Collaborators : [Account, .. Collaborators];

    /// <summary>
    /// Compte utilisé pour récupérer et envoyer : celui choisi pour ce projet (« Envoyer avec le compte »)
    /// s'il a toujours accès au repo, sinon le propriétaire, sinon le premier collaborateur.
    /// </summary>
    public GitHubAccount? NetworkAccount =>
        AccessAccounts.FirstOrDefault(a => a.Login.Equals(Project.PushAccount, StringComparison.OrdinalIgnoreCase))
        ?? AccessAccounts.FirstOrDefault();

    public bool IsCollaborator(GitHubAccount account) => Collaborators.Any(c => c.Is(account));

    /// <summary>Projet sans remote rangé à la main dans le compte <see cref="Account"/> (glissé sur son onglet) : pas encore sur GitHub.</summary>
    public bool IsAssigned { get; init; }

    // ------------------------------------------------------------ liens (F3B : un projet lié à plusieurs repos)

    /// <summary>Remotes autres que le principal, chacun avec le compte connecté qui peut y envoyer.</summary>
    public IReadOnlyList<ProjectLink> SecondaryLinks { get; init; } = [];

    public ProjectLink? PrimaryLink => Project.Remote is { } remote
        ? new ProjectLink(Project.RemoteName ?? "origin", remote, NetworkAccount, IsPrimary: true)
        : null;

    /// <summary>Tous les liens du projet : le principal d'abord.</summary>
    public IReadOnlyList<ProjectLink> Links => PrimaryLink is { } primary ? [primary, .. SecondaryLinks] : SecondaryLinks;

    /// <summary>Lié à au moins un autre repo GitHub que le principal.</summary>
    public bool HasSecondaryLinks => SecondaryLinks.Count > 0;

    /// <summary>Le compte a un lien secondaire vers un de ses repos (ou un repo où il collabore) : le projet apparaît aussi dans son onglet.</summary>
    public bool HasLinkFor(GitHubAccount account) => SecondaryLinks.Any(l => account.Is(l.Account));

    /// <summary>Comptes à qui le projet est rattaché, d'une façon ou d'une autre (propriétaire, collaborateur, lien secondaire).</summary>
    public bool BelongsTo(GitHubAccount account) => account.Is(Account) || IsCollaborator(account) || HasLinkFor(account);
}

/// <summary>Un lien du projet vers un repo : un remote git.</summary>
/// <param name="Account">Compte connecté utilisé pour échanger avec ce repo (propriétaire, sinon collaborateur), ou null.</param>
public sealed record ProjectLink(string Name, RemoteUrl Url, GitHubAccount? Account, bool IsPrimary)
{
    public string RepositoryLabel => Url is { IsGitHub: true, FullName: { } full } ? full : Url.FullName is { } f ? $"{Url.Host}/{f}" : Url.Url;
}

/// <summary>Repos sur lesquels chaque compte connecté est collaborateur (sans en être propriétaire).</summary>
public sealed class CollaborationIndex
{
    private readonly Dictionary<string, List<GitHubAccount>> _byRepo = new(StringComparer.OrdinalIgnoreCase);

    public static readonly CollaborationIndex Empty = new();

    public void Add(GitHubAccount collaborator, string fullName)
    {
        var key = Key(collaborator.Host, fullName);
        if (!_byRepo.TryGetValue(key, out var list)) _byRepo[key] = list = [];
        if (!list.Any(a => a.Is(collaborator))) list.Add(collaborator);
    }

    public IReadOnlyList<GitHubAccount> For(string host, string fullName) =>
        _byRepo.TryGetValue(Key(host, fullName), out var list) ? list : [];

    private static string Key(string host, string fullName) => $"{host}/{fullName}";
}

/// <summary>Range chaque projet dans l'onglet du compte propriétaire de son remote, et dans ceux de ses collaborateurs.</summary>
public static class ProjectClassifier
{
    /// <param name="assignedAccount">Compte (clé « hôte/login ») dans lequel un projet sans remote a été rangé à la main.</param>
    public static IReadOnlyList<ProjectPlacement> Classify(IEnumerable<LocalProject> projects, IReadOnlyList<GitHubAccount> accounts,
        CollaborationIndex? collaborations = null, Func<string, string?>? assignedAccount = null) =>
        projects.Select(p => Place(p, accounts, collaborations, assignedAccount)).ToList();

    public static ProjectPlacement Place(LocalProject project, IReadOnlyList<GitHubAccount> accounts, CollaborationIndex? collaborations = null,
        Func<string, string?>? assignedAccount = null)
    {
        if (!project.IsRepository || project.Remote is null)
        {
            // Pas encore sur GitHub : rangé dans le compte choisi par l'utilisateur, sinon dans « Non liés ».
            if (assignedAccount?.Invoke(project.Path) is { } key
                && accounts.FirstOrDefault(a => AppSettings.AccountKey(a.Host, a.Login) == key) is { } account)
                return new(project, account, null) { IsAssigned = true };
            return new(project, null, project.IsRepository ? UnlinkedReason.NoRemote : UnlinkedReason.NotARepository);
        }
        var remote = project.Remote;
        collaborations ??= CollaborationIndex.Empty;

        // Liens secondaires (F3B) : un projet rattaché à un compte par un lien secondaire apparaît aussi dans son onglet.
        var secondary = project.AllRemotes.Where(r => r.Name != (project.RemoteName ?? "origin"))
            .Select(r => new ProjectLink(r.Name, r.Url, AccountFor(r.Url, accounts, collaborations), IsPrimary: false))
            .ToList();
        var reachable = secondary.Any(l => l.Account is not null);

        var onHost = accounts.Where(a => a.Host.Equals(remote.Host, StringComparison.OrdinalIgnoreCase)).ToList();
        if (onHost.Count == 0 || remote.Owner is null || remote.FullName is not { } fullName)
            return new(project, null, reachable ? null : UnlinkedReason.OtherHost) { SecondaryLinks = secondary };

        var owner = onHost.FirstOrDefault(a => a.Login.Equals(remote.Owner, StringComparison.OrdinalIgnoreCase));
        // Les comptes connus de l'index sont remplacés par leur version à jour (jeton, compte actif…).
        var collaborators = collaborations.For(remote.Host, fullName)
            .Select(c => onHost.FirstOrDefault(a => a.Is(c)))
            .OfType<GitHubAccount>()
            .Where(c => owner is null || !c.Is(owner))
            .ToList();

        // Repo d'un compte non connecté (ou d'une organisation) : rangé chez ses collaborateurs connectés ou ses autres liens.
        if (owner is null && collaborators.Count == 0)
            return new(project, null, reachable ? null : UnlinkedReason.OtherOwner) { SecondaryLinks = secondary };
        return new(project, owner, null) { Collaborators = collaborators, SecondaryLinks = secondary };
    }

    /// <summary>Compte connecté qui peut échanger avec ce repo : son propriétaire, sinon un collaborateur connecté.</summary>
    public static GitHubAccount? AccountFor(RemoteUrl url, IReadOnlyList<GitHubAccount> accounts, CollaborationIndex? collaborations = null)
    {
        if (url.Owner is null || url.FullName is not { } fullName) return null;
        var onHost = accounts.Where(a => a.Host.Equals(url.Host, StringComparison.OrdinalIgnoreCase)).ToList();
        return onHost.FirstOrDefault(a => a.Login.Equals(url.Owner, StringComparison.OrdinalIgnoreCase))
               ?? (collaborations ?? CollaborationIndex.Empty).For(url.Host, fullName).Select(c => onHost.FirstOrDefault(a => a.Is(c))).OfType<GitHubAccount>().FirstOrDefault();
    }
}
