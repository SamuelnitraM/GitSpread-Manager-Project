using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitSpread.Core.Git;

namespace GitSpread.App.ViewModels;

/// <summary>Analyse et options avant d'intégrer une branche dans une autre (ex. une branche annexe dans main).</summary>
public sealed partial class IntegrationViewModel(ProjectItemViewModel project, IProjectHost host, string source, string target) : ObservableObject
{
    public string Source => source;
    public string Target => target;
    public bool IntoMain => target == project.MainBranch;
    public string Title => IntoMain ? $"Intégrer « {source} » dans « {target} »" : $"Mettre à jour « {target} » avec « {source} »";
    public string ActionLabel => IntoMain ? "Intégrer" : "Mettre à jour";

    public IntegrationAnalysis? Analysis { get; private set; }
    public ObservableCollection<string> Preview { get; } = [];
    public ObservableCollection<string> ConflictFiles { get; } = [];

    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial string Summary { get; set; } = "Analyse…";
    [ObservableProperty] public partial string? FetchNote { get; set; }
    [ObservableProperty] public partial IntegrationKind Kind { get; set; }
    [ObservableProperty] public partial string? Blocker { get; set; }

    [ObservableProperty] public partial bool StashChanges { get; set; } = true;
    [ObservableProperty] public partial bool PushTarget { get; set; }
    [ObservableProperty] public partial bool DeleteSource { get; set; }
    [ObservableProperty] public partial bool StartDespiteConflicts { get; set; }

    public bool ShowStashOption => Analysis is { WorkingTreeDirty: true } a && (a.NeedsCheckout || a.TargetIsCurrent) && a.Kind != IntegrationKind.NothingToDo;
    public bool ShowPushOption => project.CanSync && Analysis is { Kind: not IntegrationKind.NothingToDo and not IntegrationKind.Conflicts };
    public bool ShowDeleteOption => IntoMain && Analysis is { Kind: not IntegrationKind.Conflicts and not IntegrationKind.NothingToDo } a
                                    && a.SourceRef == a.Source && a.Source != a.CurrentBranch;
    public bool ShowConflictOption => Kind == IntegrationKind.Conflicts && Blocker is null;
    public bool HasConflicts => ConflictFiles.Count > 0;
    public bool HasBlocker => Blocker is not null;

    public bool CanRun => !IsLoading && Analysis is { } a && a.Kind != IntegrationKind.NothingToDo
        && a.Blocker is IntegrationBlocker.None or IntegrationBlocker.DirtyWorkingTree
        && (a.Blocker != IntegrationBlocker.DirtyWorkingTree || StashChanges)
        && (a.Kind != IntegrationKind.Conflicts || StartDespiteConflicts);

    /// <summary>Icône Segoe selon le cas : coche (simple), fusion, alerte (conflits / blocage).</summary>
    public string KindIcon => Blocker is not null || Kind == IntegrationKind.Conflicts ? "" : Kind == IntegrationKind.NothingToDo ? "" : "";

    partial void OnStashChangesChanged(bool value) => Refresh();
    partial void OnPushTargetChanged(bool value) => Refresh();
    partial void OnDeleteSourceChanged(bool value) => Refresh();
    partial void OnStartDespiteConflictsChanged(bool value) => Refresh();

    public async Task LoadAsync()
    {
        // Infos fraîches d'abord : main a peut-être avancé sur GitHub.
        if (project.CanSync)
        {
            try
            {
                var auth = await host.Services.Credentials.EnvironmentForAsync(project.Placement.Account, project.Remote);
                var fetch = await host.Services.Git.FetchAsync(project.Path, project.Project.RemoteName ?? "origin", auth);
                if (!fetch.Succeeded) FetchNote = $"Impossible de vérifier GitHub ({fetch.Explanation ?? "hors ligne ?"}) : analyse faite avec les infos déjà connues.";
            }
            catch (Exception e) { FetchNote = $"Impossible de vérifier GitHub ({e.Message}) : analyse faite avec les infos déjà connues."; }
        }

        try
        {
            Analysis = await host.Services.Integrator.AnalyzeAsync(project.Path, source, target);
            Kind = Analysis.Kind;
            Blocker = Analysis.Blocker is IntegrationBlocker.None or IntegrationBlocker.DirtyWorkingTree ? null : Analysis.Summary;
            // Des modifications en cours ne bloquent pas : elles seront mises de côté (option cochée par défaut).
            Summary = (Analysis.Blocker == IntegrationBlocker.DirtyWorkingTree ? Analysis with { Blocker = IntegrationBlocker.None } : Analysis).Summary;
            if (Analysis.TargetBehindRemote > 0)
                Summary += $" {target} sera d'abord mise à jour avec les {Analysis.TargetBehindRemote} commit(s) arrivés sur GitHub.";
            foreach (var f in Analysis.ConflictingFiles) ConflictFiles.Add(f);
            PushTarget = ShowPushOption;
        }
        catch (Exception e)
        {
            Blocker = e.Message;
            Summary = "";
        }
        IsLoading = false;
        Refresh();
    }

    public IntegrationOptions BuildOptions() => new()
    {
        StashChanges = StashChanges,
        PushTarget = PushTarget && ShowPushOption,
        DeleteSource = DeleteSource && ShowDeleteOption,
        StartDespiteConflicts = StartDespiteConflicts,
    };

    private void Refresh()
    {
        Preview.Clear();
        if (Analysis is { } a && a.Kind != IntegrationKind.NothingToDo && Blocker is null)
            foreach (var line in BranchIntegrator.Preview(a, BuildOptions())) Preview.Add(line);
        foreach (var p in new[] { nameof(CanRun), nameof(ShowStashOption), nameof(ShowPushOption), nameof(ShowDeleteOption),
                     nameof(ShowConflictOption), nameof(HasConflicts), nameof(HasBlocker), nameof(KindIcon) })
            OnPropertyChanged(p);
    }
}
