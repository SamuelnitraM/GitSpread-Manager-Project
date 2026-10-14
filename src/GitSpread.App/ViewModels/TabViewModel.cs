using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitSpread.App.ViewModels;

/// <summary>Retour visuel pendant un glisser-déposer sur un onglet.</summary>
public enum DropHint { None, InsertBefore, InsertAfter, Project }

/// <summary>Base des onglets : une liste de projets locaux filtrable avec un projet sélectionné.</summary>
public abstract partial class TabViewModel : ObservableObject
{
    private List<ProjectItemViewModel> _allProjects = [];

    public abstract string Title { get; }
    public abstract bool IsAccountTab { get; }

    /// <summary>Identifiant stable, pour mémoriser l'ordre des onglets.</summary>
    public abstract string Key { get; }

    [ObservableProperty] public partial DropHint DropHint { get; set; }
    [ObservableProperty] public partial bool IsBeingDragged { get; set; }

    /// <summary>Nom annoncé par les lecteurs d'écran et l'automatisation de l'interface.</summary>
    public override string ToString() => Title;

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];

    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial ProjectItemViewModel? SelectedProject { get; set; }
    [ObservableProperty] public partial bool IsScanning { get; set; }

    public int ProjectCount => _allProjects.Count;
    public bool HasNoProjects => !IsScanning && _allProjects.Count == 0;
    public bool ShowScanning => IsScanning && _allProjects.Count == 0;
    public bool HasSelection => SelectedProject is not null;
    public string ProjectsSummary => $"{_allProjects.Count} projet{(_allProjects.Count > 1 ? "s" : "")}";

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnIsScanningChanged(bool value) { OnPropertyChanged(nameof(HasNoProjects)); OnPropertyChanged(nameof(ShowScanning)); }

    partial void OnSelectedProjectChanged(ProjectItemViewModel? oldValue, ProjectItemViewModel? newValue)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (oldValue is not null) oldValue.DetailsLoaded -= OnProjectDetailsLoaded;
        if (newValue is not null)
        {
            newValue.DetailsLoaded += OnProjectDetailsLoaded;
            _ = newValue.LoadDetailsAsync();
        }
        if (!_rebuilding) UpdateHistory();
    }

    // Vrai pendant la reconstruction de la liste : le projet choisi reste le même, l'aperçu ne doit pas se recharger.
    private bool _rebuilding;

    public IReadOnlyList<ProjectItemViewModel> AllProjects => _allProjects;

    // ------------------------------------------------------------ aperçu de l'historique (F6a)

    /// <summary>Aperçu de l'historique en bas de page, fourni par la fenêtre principale.</summary>
    public HistoryViewModel? History
    {
        get => _history;
        set
        {
            if (_history is not null) _history.OpenLargeRequested -= OnOpenLargeRequested;
            _history = value;
            if (value is not null) value.OpenLargeRequested += OnOpenLargeRequested;
        }
    }
    private HistoryViewModel? _history;

    /// <summary>Historique en grand, affiché à la place du contenu de l'onglet (les onglets restent accessibles).</summary>
    [ObservableProperty] public partial HistoryExplorerViewModel? Explorer { get; set; }
    public bool IsExplorerOpen => Explorer is not null;
    partial void OnExplorerChanged(HistoryExplorerViewModel? value) => OnPropertyChanged(nameof(IsExplorerOpen));

    private void OnOpenLargeRequested(object? sender, HistoryOpenRequest request) => OpenExplorer(request.Key, request.Sha);

    /// <summary>Ouvre l'historique en grand sur un projet (par défaut, celui de l'aperçu) et un commit.</summary>
    public void OpenExplorer(string? key = null, string? sha = null)
    {
        if (History is null) return;
        Explorer = new HistoryExplorerViewModel(this, History.Host, key ?? ExplorerKey(), sha);
        Explorer.Start();
    }

    /// <summary>Élément à montrer en grand quand l'aperçu n'affiche aucun projet (ex. repo GitHub pas encore cloné).</summary>
    protected virtual string? ExplorerKey() => SelectedProject?.Path;

    /// <summary>Retour à l'affichage habituel de l'onglet.</summary>
    public void CloseExplorer() => Explorer = null;

    /// <summary>Vrai si l'aperçu montre l'historique d'un projet local (et non celui d'un repo GitHub).</summary>
    protected virtual bool ShowsProjectHistory => true;

    /// <summary>Affiche dans l'aperçu l'historique de ce qui est sélectionné dans l'onglet.</summary>
    protected virtual void UpdateHistory() => _ = History?.ShowAsync(SelectedProject);

    // Après une action (commit, envoi, branche…), l'aperçu se met à jour s'il montre ce projet.
    private void OnProjectDetailsLoaded(object? sender, EventArgs e)
    {
        if (History is { IsLoading: false, Project: { } shown } && sender is ProjectItemViewModel p && p.Path == shown.Path)
            _ = History.ReloadCommand.ExecuteAsync(null);
    }

    public void SetProjects(IEnumerable<ProjectItemViewModel> projects)
    {
        var selectedPath = SelectedProject?.Path;
        _allProjects = projects.ToList();
        _rebuilding = true;
        try
        {
            ApplyFilter();
            SelectedProject = Projects.FirstOrDefault(p => p.Path == selectedPath);
        }
        finally { _rebuilding = false; }
        // Une seule relecture, sur le nouvel état du projet (même projet : le commit choisi est conservé).
        if (ShowsProjectHistory) UpdateHistory();
        OnPropertyChanged(nameof(ProjectCount));
        OnPropertyChanged(nameof(ProjectsSummary));
        OnPropertyChanged(nameof(HasNoProjects));
        OnPropertyChanged(nameof(ShowScanning));
        Explorer?.Refresh();
    }

    protected virtual void ApplyFilter()
    {
        var filter = Filter.Trim();
        Projects.Clear();
        foreach (var p in _allProjects.Where(p => p.Matches(filter))) Projects.Add(p);
    }
}

/// <summary>Projets sans remote, hébergés ailleurs, ou appartenant à un compte non connecté.</summary>
public sealed class UnlinkedTabViewModel : TabViewModel
{
    public const string TabKey = "unlinked";

    public override string Title => "Non liés / autres";
    public override bool IsAccountTab => false;
    public override string Key => TabKey;
}
