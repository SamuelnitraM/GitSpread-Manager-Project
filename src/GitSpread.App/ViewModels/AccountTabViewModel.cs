using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

/// <summary>Un onglet = un compte GitHub connecté : ses projets locaux et ses repos GitHub.</summary>
public sealed partial class AccountTabViewModel(GitHubAccount account, IProjectHost host, ILogger logger) : TabViewModel
{
    private GitHubService gitHub => host.Services.GitHub;
    private List<RepositoryItemViewModel> _allRepositories = [];

    [ObservableProperty] public partial GitHubAccount Account { get; set; } = account;

    public override string Title => Account.Login;
    public override bool IsAccountTab => true;
    public override string Key => Core.Settings.AppSettings.AccountKey(Account.Host, Account.Login);
    public string Login => Account.Login;
    public bool IsActive => Account.IsActive;
    public string HostLabel => Account.Host == AccountService.DefaultHost ? "" : Account.Host;

    partial void OnAccountChanged(GitHubAccount value) => OnPropertyChanged(nameof(IsActive));

    /// <summary>Vrai sur l'onglet du compte actif quand un autre compte du même hôte peut prendre la main.</summary>
    [ObservableProperty] public partial bool CanSwitch { get; set; }

    /// <summary>Faux : projets locaux. Vrai : repos sur GitHub.</summary>
    [ObservableProperty] public partial bool ShowRepositories { get; set; }

    public ObservableCollection<RepositoryItemViewModel> Repositories { get; } = [];

    // ------------------------------------------------------------ collaborations (F3A)

    /// <summary>Repos d'autres comptes sur lesquels ce compte est collaborateur.</summary>
    public IReadOnlyList<GitHubRepository> Collaborations { get; private set; } = [];

    /// <summary>Invitations à collaborer reçues par ce compte, en attente de réponse.</summary>
    public ObservableCollection<GitHubInvitation> ReceivedInvitations { get; } = [];
    public bool HasReceivedInvitations => ReceivedInvitations.Count > 0;
    public string ReceivedInvitationsLabel => ReceivedInvitations.Count == 1
        ? "1 invitation à collaborer reçue"
        : $"{ReceivedInvitations.Count} invitations à collaborer reçues";

    [ObservableProperty] public partial string? InvitationMessage { get; set; }
    public bool HasInvitationMessage => InvitationMessage is not null;
    partial void OnInvitationMessageChanged(string? value) => OnPropertyChanged(nameof(HasInvitationMessage));

    [RelayCommand]
    private async Task AcceptInvitationAsync(GitHubInvitation invitation)
    {
        try
        {
            await gitHub.AcceptInvitationAsync(Account, invitation.Id);
            InvitationMessage = $"Tu es maintenant collaborateur de {invitation.FullName} avec {Login}.";
        }
        catch (Exception e) { InvitationMessage = $"Impossible d'accepter l'invitation à {invitation.FullName} : {e.Message}"; }
        await host.ReloadCollaborationsAsync();
    }

    [RelayCommand]
    private async Task DeclineInvitationAsync(GitHubInvitation invitation)
    {
        if (!host.Dialogs.Confirm($"Refuser l'invitation de {invitation.Inviter} à collaborer sur {invitation.FullName} ?\n\n"
                                  + "Pour y accéder plus tard, il faudra qu'on t'invite de nouveau.", "Refuser l'invitation", destructive: true))
            return;
        try
        {
            await gitHub.DeclineInvitationAsync(Account, invitation.Id);
            InvitationMessage = $"Invitation à {invitation.FullName} refusée.";
        }
        catch (Exception e) { InvitationMessage = $"Impossible de refuser l'invitation à {invitation.FullName} : {e.Message}"; }
        await LoadRepositoriesAsync();
    }

    private async Task<T?> OptionalAsync<T>(Func<Task<T>> load, string what) where T : class
    {
        try { return await load(); }
        catch (Exception e)
        {
            logger.LogWarning(e, "Chargement des {What} de {Login} impossible", what, Account.Login);
            return null;
        }
    }

    /// <summary>Repo sélectionné dans « Repos sur GitHub » et son panneau de gestion (F4).</summary>
    [ObservableProperty] public partial RepositoryItemViewModel? SelectedRepository { get; set; }
    [ObservableProperty] public partial GitHubRepositoryViewModel? RepositoryPanel { get; set; }
    public bool HasRepositoryPanel => RepositoryPanel is not null;
    partial void OnRepositoryPanelChanged(GitHubRepositoryViewModel? value) => OnPropertyChanged(nameof(HasRepositoryPanel));

    partial void OnSelectedRepositoryChanged(RepositoryItemViewModel? value)
    {
        if (value is null) return; // la liste se vide le temps d'un filtrage : le panneau reste affiché
        if (RepositoryPanel?.FullName == value.Repository.FullName) return;
        RepositoryPanel = new GitHubRepositoryViewModel(this, value.Repository, host, logger);
        _ = RepositoryPanel.LoadAsync();
    }

    [ObservableProperty] public partial bool IsLoadingRepositories { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string RepositoriesSummary { get; set; } = "";
    [ObservableProperty] public partial bool ShowArchived { get; set; }

    public bool HasError => Error is not null;
    public int RepositoryCount => _allRepositories.Count;
    public bool HasNoRepositories => !IsLoadingRepositories && Error is null && Repositories.Count == 0;

    partial void OnShowArchivedChanged(bool value) => ApplyFilter();
    partial void OnErrorChanged(string? value) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(HasNoRepositories)); }
    partial void OnIsLoadingRepositoriesChanged(bool value) => OnPropertyChanged(nameof(HasNoRepositories));

    public async Task LoadRepositoriesAsync()
    {
        if (!Account.IsAuthenticated)
        {
            Error = $"La connexion de ce compte n'est plus valide ({Account.Error}). "
                  + $"Dans un terminal : gh auth refresh --hostname {Account.Host} --user {Account.Login}";
            return;
        }

        IsLoadingRepositories = true;
        Error = null;
        try
        {
            // Collaborations et invitations reçues (F3A) : secondaires, leur échec ne bloque pas la liste des repos.
            var collaborationsTask = OptionalAsync(() => gitHub.ListCollaborationsAsync(Account), "collaborations");
            var invitationsTask = OptionalAsync(() => gitHub.ListReceivedInvitationsAsync(Account), "invitations reçues");
            var repos = await gitHub.ListRepositoriesAsync(Account);
            Collaborations = await collaborationsTask ?? Collaborations;
            if (await invitationsTask is { } invitations)
            {
                ReceivedInvitations.Clear();
                foreach (var i in invitations.Where(i => !i.IsExpired)) ReceivedInvitations.Add(i);
                OnPropertyChanged(nameof(HasReceivedInvitations));
                OnPropertyChanged(nameof(ReceivedInvitationsLabel));
            }

            _allRepositories = repos.Concat(Collaborations).OrderByDescending(r => r.PushedAt ?? DateTimeOffset.MinValue)
                .Select(r => new RepositoryItemViewModel(r, Account)).ToList();

            var privateCount = repos.Count(r => r.Visibility != RepositoryVisibility.Public);
            var archived = repos.Count(r => r.IsArchived);
            RepositoriesSummary = $"{repos.Count} repo{(repos.Count > 1 ? "s" : "")} · {privateCount} privé{(privateCount > 1 ? "s" : "")}"
                    + (archived > 0 ? $" · {archived} archivé{(archived > 1 ? "s" : "")}" : "")
                    + (Collaborations.Count > 0 ? $" · collaborateur sur {Collaborations.Count} autre{(Collaborations.Count > 1 ? "s" : "")}" : "")
                    + (repos.Count >= GitHubService.DefaultLimit ? $" (limité aux {GitHubService.DefaultLimit} plus récents)" : "");
            OnPropertyChanged(nameof(RepositoryCount));
            ApplyFilter();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Chargement des repos de {Login} impossible", Account.Login);
            Error = e.Message;
        }
        finally
        {
            IsLoadingRepositories = false;
        }
    }

    protected override void ApplyFilter()
    {
        base.ApplyFilter();
        var filter = Filter.Trim();
        var selected = RepositoryPanel?.FullName;
        Repositories.Clear();
        foreach (var repo in _allRepositories.Where(r => (ShowArchived || !r.IsArchived) && r.Matches(filter)))
            Repositories.Add(repo);
        SelectedRepository = Repositories.FirstOrDefault(r => r.Repository.FullName.Equals(selected, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasNoRepositories));
    }

    /// <summary>Rouvre le panneau sur un repo (ex. après un renommage), en gardant le compte rendu de l'action.</summary>
    public void Reselect(string fullName, ActionLog? log)
    {
        var item = Repositories.FirstOrDefault(r => r.Repository.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        SelectedRepository = item;
        if (RepositoryPanel is { } panel) panel.LastAction = log;
    }

    /// <summary>Le repo affiché n'existe plus sur ce compte (supprimé, transféré) : on ferme son panneau.</summary>
    public void CloseRepositoryPanel()
    {
        RepositoryPanel = null;
        SelectedRepository = null;
    }
}
