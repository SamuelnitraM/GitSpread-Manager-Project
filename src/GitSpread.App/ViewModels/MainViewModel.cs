using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Processes;
using GitSpread.Core.Projects;
using GitSpread.Core.Settings;
using GitSpread.Core.Ssh;
using System.IO;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

/// <summary>Services partagés par les fenêtres (composés une seule fois au démarrage).</summary>
public sealed record AppServices(
    AccountService Accounts,
    GitHubService GitHub,
    LoginService Login,
    GitService Git,
    GitCredentialResolver Credentials,
    ProjectPublisher Publisher,
    BranchIntegrator Integrator,
    TransferService Transfer,
    SshKeyService SshKeys,
    ProjectScanner Scanner,
    SettingsStore SettingsStore,
    ILoggerFactory LoggerFactory,
    string LogDirectory);

public sealed partial class MainViewModel : ObservableObject, IProjectHost
{
    private readonly AppServices _services;
    private readonly ILogger _logger;
    private readonly UnlinkedTabViewModel _unlinked = new();
    private IReadOnlyList<GitHubAccount> _accounts = [];
    private IReadOnlyList<LocalProject> _projects = [];
    private string _scanStatus = "";

    public MainViewModel(AppServices services, IDialogService dialogs)
    {
        _services = services;
        Dialogs = dialogs;
        _logger = services.LoggerFactory.CreateLogger<MainViewModel>();
        Settings = services.SettingsStore.Load();
    }

    public AppServices Services => _services;
    public IDialogService Dialogs { get; }
    public IReadOnlyList<GitHubAccount> Accounts => _accounts;
    public void SaveSettings() => _services.SettingsStore.Save(Settings);

    private readonly Dictionary<string, Task<GitHubUser?>> _users = new(StringComparer.OrdinalIgnoreCase);

    public Task<GitHubUser?> GetAccountUserAsync(GitHubAccount account)
    {
        var key = AppSettings.AccountKey(account.Host, account.Login);
        if (!_users.TryGetValue(key, out var task) || task is { IsCompleted: true, Result: null })
            _users[key] = task = LoadUserAsync(account);
        return task;
    }

    private async Task<GitHubUser?> LoadUserAsync(GitHubAccount account)
    {
        try { return await _services.GitHub.GetUserAsync(account); }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Profil GitHub de {Login} indisponible", account.Login);
            return null;
        }
    }
    public AppSettings Settings { get; private set; }
    public string LogDirectory => _services.LogDirectory;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    public IEnumerable<AccountTabViewModel> AccountTabs => Tabs.OfType<AccountTabViewModel>();

    [ObservableProperty] public partial TabViewModel? SelectedTab { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? GlobalError { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    public bool HasGlobalError => GlobalError is not null;
    public bool HasNoRootFolders => Settings.RootFolders.Count == 0;
    partial void OnGlobalErrorChanged(string? value) => OnPropertyChanged(nameof(HasGlobalError));

    /// <summary>Détecte les comptes, charge leurs repos et analyse les dossiers de projets.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        GlobalError = null;
        _users.Clear();
        try
        {
            _accounts = await _services.Accounts.GetAccountsAsync();
            RebuildAccountTabs();

            if (_accounts.Count == 0)
            {
                GlobalError = "Aucun compte GitHub connecté. Clique sur « + » à côté des onglets pour en connecter un.";
            }

            await Task.WhenAll(AccountTabs.Select(t => t.LoadRepositoriesAsync()).Append(ScanAsync()));
        }
        catch (ToolNotFoundException e)
        {
            GlobalError = e.Tool == GhCli.Executable
                ? "La GitHub CLI (gh) est introuvable. Installe-la depuis https://cli.github.com puis relance GitSpread."
                : e.Message;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Actualisation impossible");
            GlobalError = $"Détection des comptes impossible : {e.Message}";
        }
        finally
        {
            IsLoading = false;
            UpdateStatus();
        }
    }

    /// <summary>Relance uniquement l'analyse des dossiers (après un changement de dossiers par exemple).</summary>
    public async Task ScanAsync()
    {
        foreach (var tab in Tabs) tab.IsScanning = true;
        try
        {
            var result = await _services.Scanner.ScanAsync(Settings.ToScanOptions(), SshConfig.Load());
            _projects = result.Projects;
            _scanStatus = $"{result.Projects.Count} projet{(result.Projects.Count > 1 ? "s" : "")} trouvé{(result.Projects.Count > 1 ? "s" : "")} en {result.Duration.TotalMilliseconds:0} ms"
                + (result.MissingRoots.Count > 0 ? $" · dossier introuvable : {string.Join(", ", result.MissingRoots)}" : "");
            _logger.LogInformation("Scan : {Count} projets en {Ms} ms", result.Projects.Count, (int)result.Duration.TotalMilliseconds);
            DistributeProjects();
        }
        finally
        {
            foreach (var tab in Tabs) tab.IsScanning = false;
            UpdateStatus();
        }
    }

    public async Task UpdateRootFoldersAsync(IEnumerable<string> folders, int depth)
    {
        Settings.RootFolders = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Settings.ScanDepth = depth;
        _services.SettingsStore.Save(Settings);
        OnPropertyChanged(nameof(HasNoRootFolders));
        await ScanAsync();
    }

    /// <summary>Ajoute des projets un par un (avec ou sans git), où qu'ils soient.</summary>
    public async Task AddProjectsAsync(IEnumerable<string> paths)
    {
        var added = paths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Where(p => !Settings.ExtraProjects.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        if (added.Count == 0) return;
        Settings.ExtraProjects.AddRange(added);
        _services.SettingsStore.Save(Settings);
        await RescanAsync(added[0]);
    }

    public async Task RemoveExtraProjectAsync(string path)
    {
        Settings.ExtraProjects.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        _services.SettingsStore.Save(Settings);
        await ScanAsync();
    }

    public async Task RescanAsync(string? revealPath = null)
    {
        await ScanAsync();
        if (revealPath is null) return;
        foreach (var tab in Tabs)
            if (tab.Projects.FirstOrDefault(p => p.Path.Equals(revealPath, StringComparison.OrdinalIgnoreCase)) is { } project)
            {
                SelectedTab = tab;
                if (tab is AccountTabViewModel account) account.ShowRepositories = false;
                tab.SelectedProject = project;
                return;
            }
    }

    /// <summary>Premier lancement : propose les dossiers de projets courants trouvés sur la machine.</summary>
    public void ApplyFirstRunDefaults()
    {
        if (_services.SettingsStore.Exists) return;
        Settings.RootFolders = SettingsStore.SuggestRootFolders();
        _services.SettingsStore.Save(Settings);
        OnPropertyChanged(nameof(HasNoRootFolders));
    }

    // ------------------------------------------------------------ changement de compte actif

    /// <summary>Comptes vers lesquels le compte actif de cet onglet peut basculer (même hôte).</summary>
    public IReadOnlyList<AccountTabViewModel> SwitchTargets(AccountTabViewModel from) =>
        AccountTabs.Where(t => t != from && t.Account.IsAuthenticated
            && t.Account.Host.Equals(from.Account.Host, StringComparison.OrdinalIgnoreCase)).ToList();

    public async Task SwitchToAsync(AccountTabViewModel target)
    {
        IsLoading = true;
        try
        {
            await _services.Accounts.SwitchAsync(target.Account);
            _accounts = await _services.Accounts.GetAccountsAsync();
            foreach (var tab in AccountTabs)
                if (_accounts.FirstOrDefault(a => a.Login == tab.Login && a.Host == tab.Account.Host) is { } updated)
                    tab.Account = updated;
            UpdateSwitchAvailability();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Changement de compte impossible");
            GlobalError = $"Impossible de changer de compte : {e.Message}";
        }
        finally
        {
            IsLoading = false;
            UpdateStatus();
        }
    }

    // ------------------------------------------------------------ interne

    private void RebuildAccountTabs()
    {
        var previous = SelectedTab?.Title;
        var existing = AccountTabs.ToDictionary(t => (t.Login, t.Account.Host));

        Tabs.Clear();
        foreach (var account in _accounts)
        {
            if (existing.TryGetValue((account.Login, account.Host), out var tab)) tab.Account = account;
            else tab = new AccountTabViewModel(account, _services.GitHub, _logger);
            Tabs.Add(tab);
        }
        Tabs.Add(_unlinked);
        ApplyTabOrder();
        UpdateSwitchAvailability();

        SelectedTab = Tabs.FirstOrDefault(t => t.Title == previous)
                   ?? AccountTabs.FirstOrDefault(t => t.IsActive)
                   ?? Tabs.FirstOrDefault();
        DistributeProjects();
    }

    // ------------------------------------------------------------ ordre des onglets

    /// <summary>Déplace un onglet avant ou après un autre, et mémorise le nouvel ordre.</summary>
    public void MoveTab(TabViewModel tab, TabViewModel target, bool after)
    {
        var from = Tabs.IndexOf(tab);
        var to = Tabs.IndexOf(target) + (after ? 1 : 0);
        if (from < 0 || to < 0) return;
        if (from < to) to--;
        if (from == to) return;
        var selected = SelectedTab;
        Tabs.Move(from, to);
        SelectedTab = selected;
        Settings.TabOrder = Tabs.Select(t => t.Key).ToList();
        SaveSettings();
    }

    private void ApplyTabOrder()
    {
        if (Settings.TabOrder.Count == 0) return;
        int Rank(TabViewModel t) => Settings.TabOrder.IndexOf(t.Key) is var i and >= 0 ? i : int.MaxValue;
        var ordered = Tabs.OrderBy(Rank).ToList(); // tri stable : les nouveaux comptes gardent leur place relative
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Tabs.IndexOf(ordered[i]);
            if (current != i) Tabs.Move(current, i);
        }
    }

    public void SetTheme(string theme)
    {
        Settings.Theme = theme;
        SaveSettings();
    }

    private void UpdateSwitchAvailability()
    {
        foreach (var tab in AccountTabs) tab.CanSwitch = tab.IsActive && SwitchTargets(tab).Count > 0;
    }

    private void DistributeProjects()
    {
        // Le compte rendu de la dernière action survit à la nouvelle analyse (ex. « Initialiser git » change l'état du projet).
        var previousLogs = Tabs.SelectMany(t => t.Projects).Where(p => p.LastAction is not null)
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().LastAction, StringComparer.OrdinalIgnoreCase);
        ProjectItemViewModel Item(ProjectPlacement p) =>
            new(p, this) { LastAction = previousLogs.GetValueOrDefault(p.Project.Path) };

        var placements = ProjectClassifier.Classify(_projects, _accounts);
        foreach (var tab in AccountTabs)
            tab.SetProjects(placements
                .Where(p => p.Account is { } a && a.Login == tab.Login && a.Host == tab.Account.Host)
                .Select(Item));
        _unlinked.SetProjects(placements.Where(p => p.Account is null).Select(Item));
    }

    private void UpdateStatus()
    {
        var active = _accounts.FirstOrDefault(a => a.IsActive)?.Login;
        var parts = new List<string>();
        if (_accounts.Count > 0)
            parts.Add($"{_accounts.Count} compte{(_accounts.Count > 1 ? "s" : "")} · actif dans gh : {active ?? "aucun"}");
        if (_scanStatus.Length > 0) parts.Add(_scanStatus);
        Status = string.Join("   ·   ", parts);
    }
}
