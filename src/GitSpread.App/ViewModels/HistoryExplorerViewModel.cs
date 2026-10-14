using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;

namespace GitSpread.App.ViewModels;

/// <summary>
/// Un élément de l'onglet « Projets » de l'historique : un projet local, ou un repo GitHub pas encore cloné sur ce poste.
/// </summary>
public sealed record ExplorerItem(string Group, string Name, string Detail, ProjectItemViewModel? Project, GitHubRepository? Repository)
{
    /// <summary>Identifiant stable : chemin du projet local, ou nom complet du repo GitHub.</summary>
    public string Key => Project?.Path ?? Repository!.FullName;
    public override string ToString() => Name;
}

/// <summary>Demande d'ouverture de l'historique en grand, sur un projet (ou un repo) et un commit.</summary>
public sealed record HistoryOpenRequest(string? Key, string? Sha);

/// <summary>
/// Historique en grand (F6a), affiché à la place du contenu de l'onglet : à gauche les projets locaux et les repos
/// GitHub de l'onglet, et les branches ; au centre le graphe ; à droite le commit choisi et ses modifications.
/// </summary>
public sealed partial class HistoryExplorerViewModel : ObservableObject
{
    public const string LocalGroup = "Projets locaux";
    public const string GitHubGroup = "Repos sur GitHub (pas encore sur ce poste)";

    private readonly TabViewModel _tab;
    private readonly IProjectHost _host;
    private string? _initialKey;
    private readonly string? _initialSha;

    public HistoryExplorerViewModel(TabViewModel tab, IProjectHost host, string? key, string? selectSha)
    {
        _tab = tab;
        _host = host;
        _initialKey = key;
        _initialSha = selectSha;
        History = new HistoryViewModel(host, withDetails: true) { PageSize = host.Settings.HistoryPageSize };
        Sections = new PanelSections(host);
        History.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HistoryViewModel.Project)) OnPropertyChanged(nameof(Title)); };
        Refresh();
    }

    public HistoryViewModel History { get; }

    /// <summary>Parties repliables de la colonne du commit choisi (état mémorisé, comme dans les panneaux).</summary>
    public PanelSections Sections { get; }
    public ObservableCollection<ExplorerItem> Items { get; } = [];

    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial ExplorerItem? SelectedItem { get; set; }

    /// <summary>0 : onglet « Projets », 1 : onglet « Branches ».</summary>
    [ObservableProperty] public partial int LeftTab { get; set; } = 1;

    public string Title => History.Project?.Name ?? SelectedItem?.Name ?? "Historique";

    partial void OnFilterChanged(string value) => Refresh();

    partial void OnSelectedItemChanged(ExplorerItem? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanAct));
        if (value is null || _quiet) return;
        if (!value.Key.Equals(_shownKey, StringComparison.OrdinalIgnoreCase)) LastAction = null; // compte rendu d'un autre projet
        _shownKey = value.Key;
        var sha = _initialKey is not null && value.Key.Equals(_initialKey, StringComparison.OrdinalIgnoreCase) ? _initialSha : null;
        _initialKey = null;
        _ = ShowAsync(value, sha);
    }

    private Task ShowAsync(ExplorerItem item, string? sha) => item switch
    {
        { Project: { IsRepository: true } p } => History.LoadAsync(p.Project, sha),
        { Project: { } p } => History.ShowAsync(p),
        { Repository: { } r } => History.ShowRepositoryAsync(r.Name, null, new AsyncRelayCommand(() => CloneAsync(r)),
            (_tab as AccountTabViewModel)?.Account, r), // copie légère en lecture seule (F6c)
        _ => Task.CompletedTask,
    };

    /// <summary>Cloner un repo GitHub depuis l'historique : une fois cloné, son historique s'affiche.</summary>
    private async Task CloneAsync(GitHubRepository repository)
    {
        if (_tab is not AccountTabViewModel account) return;
        var dialog = new CloneViewModel(account.Account, repository, _host);
        _host.Dialogs.ShowClone(dialog);
        if (dialog.ClonedPath is not { } path) return;
        _initialKey = path;
        await _host.RescanAsync(path); // l'onglet reçoit le nouveau projet, la liste se met à jour
        SelectedItem = Items.FirstOrDefault(i => i.Key.Equals(path, StringComparison.OrdinalIgnoreCase)) ?? SelectedItem;
    }

    /// <summary>Choisit le projet de départ (celui de l'aperçu), sinon le premier projet suivi par git.</summary>
    public void Start()
    {
        SelectedItem = Items.FirstOrDefault(i => i.Key.Equals(_initialKey, StringComparison.OrdinalIgnoreCase))
                       ?? Items.FirstOrDefault(i => i.Project is { IsRepository: true })
                       ?? Items.FirstOrDefault();
        if (SelectedItem is null) LeftTab = 0;
        _waitingForItems = SelectedItem is null; // ouvert au lancement (F6d), avant la fin de l'analyse des dossiers
    }

    private bool _waitingForItems;

    /// <summary>Reconstruit la liste (projets de l'onglet, puis repos GitHub qui ne sont pas encore sur ce poste), sélection conservée.</summary>
    public void Refresh()
    {
        var filter = Filter.Trim();
        var selectedKey = SelectedItem?.Key;
        var items = _tab.AllProjects.Where(p => p.Matches(filter)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ExplorerItem(LocalGroup, p.Name, p.RemoteLabel, p, null))
            .ToList();
        if (_tab is AccountTabViewModel account)
            items.AddRange(account.AllRepositories
                .Where(r => LinkedProjects.Find(_host.LocalProjects, account.Account.Host, r.FullName).Count == 0)
                .Where(r => filter.Length == 0 || r.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new ExplorerItem(GitHubGroup, r.Name, r.Owner.Equals(account.Login, StringComparison.OrdinalIgnoreCase) ? r.FullName : $"{r.FullName} · collaborateur", null, r)));

        Items.Clear();
        foreach (var i in items) Items.Add(i);
        if (selectedKey is not null)
        {
            // Même élément, nouvelle instance : on le resélectionne sans recharger l'historique.
            var again = Items.FirstOrDefault(i => i.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase));
            if (again is not null && !again.Equals(SelectedItem)) SetSelectionQuietly(again);
        }
        if (_waitingForItems && SelectedItem is null && Items.Count > 0) Start();
    }

    private bool _quiet;
    private string? _shownKey;
    private void SetSelectionQuietly(ExplorerItem item)
    {
        _quiet = true;
        try { SelectedItem = item; }
        finally { _quiet = false; }
    }

    [RelayCommand]
    private void Close() => _tab.CloseExplorer();

    // ------------------------------------------------------------ actions sur les branches (F6b)

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial ActionLog? LastAction { get; set; }
    public bool HasLastAction => LastAction is not null;

    /// <summary>Les actions ne concernent que les projets suivis par git sur ce poste.</summary>
    public bool CanAct => SelectedItem?.Project is { IsRepository: true } && !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanAct));
    partial void OnLastActionChanged(ActionLog? value) => OnPropertyChanged(nameof(HasLastAction));

    /// <summary>Lance une action du projet choisi, montre son compte rendu, puis relit l'historique.</summary>
    private async Task RunOnProjectAsync(Func<ProjectItemViewModel, Task> action)
    {
        if (SelectedItem?.Project is not { IsRepository: true } project || IsBusy) return;
        IsBusy = true;
        try
        {
            await project.EnsureDetailsAsync();
            project.LastAction = null;
            await action(project);
            if (project.LastAction is { } log) LastAction = log; // rien si l'action a été annulée
        }
        finally { IsBusy = false; }
        await History.ReloadCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task SwitchBranchAsync(BranchRowViewModel? branch) =>
        branch is null ? Task.CompletedTask : RunOnProjectAsync(p => p.SwitchToBranchAsync(branch.Info));

    [RelayCommand]
    private Task IntegrateBranchAsync(BranchRowViewModel? branch) =>
        branch?.DefaultBranch is not { } main ? Task.CompletedTask : RunOnProjectAsync(p => p.IntegrateAsync(branch.Name, main));

    [RelayCommand]
    private Task PushBranchAsync(BranchRowViewModel? branch) =>
        branch is null ? Task.CompletedTask : RunOnProjectAsync(p => p.PushBranchAsync(branch.Info));

    [RelayCommand]
    private Task RenameBranchAsync(BranchRowViewModel? branch) =>
        branch is null ? Task.CompletedTask
            : RunOnProjectAsync(p => p.RenameBranchAsync(branch.Info, History.Branches.Select(b => b.Name).ToList(), History.DefaultBranch));

    [RelayCommand]
    private Task DeleteBranchAsync(BranchRowViewModel? branch) =>
        branch is null ? Task.CompletedTask : RunOnProjectAsync(p => p.DeleteBranchAsync(branch.Info, History.DefaultBranch));

    /// <summary>Nouvelle branche à partir du commit choisi dans l'historique.</summary>
    [RelayCommand]
    private Task CreateBranchHereAsync() =>
        History.SelectedRow is not { } row ? Task.CompletedTask : RunOnProjectAsync(p => p.CreateBranchFromAsync(row.Commit.Sha, row.ShortSha));
}
