using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

/// <summary>Un onglet = un compte GitHub connecté : ses projets locaux et ses repos GitHub.</summary>
public sealed partial class AccountTabViewModel(GitHubAccount account, GitHubService gitHub, ILogger logger) : TabViewModel
{
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
            var repos = await gitHub.ListRepositoriesAsync(Account);
            _allRepositories = repos.OrderByDescending(r => r.PushedAt ?? DateTimeOffset.MinValue)
                .Select(r => new RepositoryItemViewModel(r)).ToList();

            var privateCount = repos.Count(r => r.Visibility != RepositoryVisibility.Public);
            var archived = repos.Count(r => r.IsArchived);
            RepositoriesSummary = $"{repos.Count} repo{(repos.Count > 1 ? "s" : "")} · {privateCount} privé{(privateCount > 1 ? "s" : "")}"
                    + (archived > 0 ? $" · {archived} archivé{(archived > 1 ? "s" : "")}" : "")
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
        Repositories.Clear();
        foreach (var repo in _allRepositories.Where(r => (ShowArchived || !r.IsArchived) && r.Matches(filter)))
            Repositories.Add(repo);
        OnPropertyChanged(nameof(HasNoRepositories));
    }
}
