using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;

namespace GitSpread.App.ViewModels;

/// <summary>Repo GitHub prêt à afficher (libellés calculés une fois pour toutes).</summary>
/// <param name="viewer">Compte de l'onglet : s'il n'est pas propriétaire, il est collaborateur du repo (F3A).</param>
public sealed partial class RepositoryItemViewModel(GitHubRepository repository, GitHubAccount? viewer = null)
{
    public GitHubRepository Repository { get; } = repository;

    public string Name => Repository.Name;

    /// <summary>Repo d'un autre compte, auquel le compte de l'onglet a accès en collaborateur.</summary>
    public bool IsCollaboration => viewer is not null && !viewer.Login.Equals(Repository.Owner, StringComparison.OrdinalIgnoreCase);
    public string OwnerLabel => $"Repo de {Repository.Owner}";
    public string? Description => Repository.Description;
    public bool HasDescription => Repository.Description is not null;
    public string Url => Repository.Url;
    public bool IsPrivate => Repository.Visibility != RepositoryVisibility.Public;
    public bool IsArchived => Repository.IsArchived;
    public bool IsFork => Repository.IsFork;

    public string VisibilityLabel => Repository.Visibility switch
    {
        RepositoryVisibility.Private => "Privé",
        RepositoryVisibility.Internal => "Interne",
        _ => "Public",
    };

    public string PushedLabel => Repository.PushedAt is { } date ? DisplayPreferences.Date(date) : "jamais";

    public string PushedTooltip => Repository.PushedAt?.LocalDateTime.ToString("dddd d MMMM yyyy 'à' HH:mm") ?? "";

    /// <summary>Nom annoncé par les lecteurs d'écran et l'automatisation de l'interface.</summary>
    public override string ToString() => Name;

    public bool Matches(string filter) =>
        filter.Length == 0
        || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (IsCollaboration && Repository.Owner.Contains(filter, StringComparison.OrdinalIgnoreCase))
        || (Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    [RelayCommand] private void Open() => Shell.Open(Url);
    [RelayCommand] private void CopyHttpsUrl() => Shell.Copy(Url + ".git");
    [RelayCommand] private void CopySshUrl() => Shell.Copy(Repository.SshUrl);
}
