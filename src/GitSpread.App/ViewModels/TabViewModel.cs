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
    public bool HasSelection => SelectedProject is not null;
    public string ProjectsSummary => $"{_allProjects.Count} projet{(_allProjects.Count > 1 ? "s" : "")}";

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(HasNoProjects));

    partial void OnSelectedProjectChanged(ProjectItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value is not null) _ = value.LoadDetailsAsync();
    }

    public void SetProjects(IEnumerable<ProjectItemViewModel> projects)
    {
        var selectedPath = SelectedProject?.Path;
        _allProjects = projects.ToList();
        ApplyFilter();
        SelectedProject = Projects.FirstOrDefault(p => p.Path == selectedPath);
        OnPropertyChanged(nameof(ProjectCount));
        OnPropertyChanged(nameof(ProjectsSummary));
        OnPropertyChanged(nameof(HasNoProjects));
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
