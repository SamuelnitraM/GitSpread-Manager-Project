using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

public enum RepointMode { Repoint, NativeTransfer }
public enum RepointStep { Form, Running, Done, Failed }

/// <summary>
/// Étapes 4 et 5 : relier un projet à un autre repo (même compte ou autre compte), ou transférer le repo
/// GitHub existant vers l'autre compte.
/// </summary>
public sealed partial class RepointViewModel : ObservableObject
{
    private readonly ProjectItemViewModel _project;
    private readonly IProjectHost _host;
    private readonly ILogger _logger;
    private readonly bool _ready;
    private List<GitHubRepository> _allRepos = [];
    private CancellationTokenSource? _analysisCts;

    public RepointViewModel(ProjectItemViewModel project, IProjectHost host, GitHubAccount? targetAccount)
    {
        _project = project;
        _host = host;
        _logger = host.Services.LoggerFactory.CreateLogger<RepointViewModel>();

        Accounts = host.Accounts.Where(a => a.IsAuthenticated).ToList();
        SelectedAccount = Accounts.FirstOrDefault(a => a.Login == targetAccount?.Login && a.Host == targetAccount.Host)
                          ?? Accounts.FirstOrDefault(a => a.Login == project.Placement.Account?.Login)
                          ?? Accounts.FirstOrDefault();
        NewName = project.Remote?.Repository ?? RepositoryName.FromFolder(project.Name);
        CreateNew = true;
        NewBranchName = $"import-{project.BranchLabel}".Replace(' ', '-');
        _ready = true;
        UpdatePreview();
    }

    public event EventHandler? CloseRequested;

    public string ProjectName => _project.Name;
    public string CurrentLink => _project.Remote switch
    {
        null => "aucun",
        { IsGitHub: true } r => $"{r.FullName} ({r.Url})",
        var r => r.Url,
    };
    public bool HasCurrentLink => _project.HasRemote;
    public bool HasUncommittedChanges => !_project.IsClean;
    public IReadOnlyList<GitHubAccount> Accounts { get; }

    /// <summary>Le transfert natif n'a de sens que pour un repo GitHub appartenant à un compte connecté, vers un autre compte.</summary>
    public bool CanNativeTransfer => _project.Placement.Account is { } owner && _project.Remote is { IsGitHub: true }
                                     && SelectedAccount is { } t && t.Login != owner.Login && t.Host == owner.Host;
    public string SourceAccountLogin => _project.Placement.Account?.Login ?? "";

    public ObservableCollection<GitHubRepository> Repositories { get; } = [];
    public ObservableCollection<string> Preview { get; } = [];
    public ObservableCollection<string> Progress { get; } = [];

    [ObservableProperty] public partial RepointMode Mode { get; set; } = RepointMode.Repoint;
    [ObservableProperty] public partial GitHubAccount? SelectedAccount { get; set; }
    [ObservableProperty] public partial bool CreateNew { get; set; }
    [ObservableProperty] public partial string NewName { get; set; }
    [ObservableProperty] public partial bool IsPrivate { get; set; } = true;
    [ObservableProperty] public partial string RepoFilter { get; set; } = "";
    [ObservableProperty] public partial GitHubRepository? SelectedRepository { get; set; }
    [ObservableProperty] public partial bool IsLoadingRepos { get; set; }
    [ObservableProperty] public partial bool KeepOldLink { get; set; } = true;
    [ObservableProperty] public partial bool PushAllBranches { get; set; } = true;

    [ObservableProperty] public partial string? TargetSummary { get; set; }
    [ObservableProperty] public partial bool TargetDiverges { get; set; }
    [ObservableProperty] public partial bool IsAnalyzingTarget { get; set; }
    [ObservableProperty] public partial bool UseNewBranch { get; set; }
    [ObservableProperty] public partial string NewBranchName { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsForm))] public partial RepointStep Step { get; set; } = RepointStep.Form;
    [ObservableProperty] public partial string? ValidationMessage { get; set; }
    [ObservableProperty] public partial string? ResultMessage { get; set; }

    public bool IsForm => Step == RepointStep.Form;
    public bool Done { get; private set; }
    public bool HasTargetSummary => TargetSummary is not null;
    public string Title => SelectedAccount is { } a && a.Login != _project.Placement.Account?.Login && _project.Placement.Account is not null
        ? $"Passer « {_project.Name} » sur le compte {a.Login}"
        : $"Changer le repo lié à « {_project.Name} »";

    partial void OnSelectedAccountChanged(GitHubAccount? value)
    {
        if (!_ready) return;
        OnPropertyChanged(nameof(CanNativeTransfer));
        OnPropertyChanged(nameof(Title));
        if (!CanNativeTransfer) Mode = RepointMode.Repoint;
        _ = LoadRepositoriesAsync();
        UpdatePreview();
    }
    partial void OnModeChanged(RepointMode value) { if (_ready) UpdatePreview(); }
    partial void OnCreateNewChanged(bool value) { if (!_ready) return; ResetTarget(); if (!value) _ = AnalyzeTargetAsync(); UpdatePreview(); }
    partial void OnNewNameChanged(string value) { if (_ready) UpdatePreview(); }
    partial void OnIsPrivateChanged(bool value) { if (_ready) UpdatePreview(); }
    partial void OnRepoFilterChanged(string value) => ApplyRepoFilter();
    partial void OnSelectedRepositoryChanged(GitHubRepository? value)
    {
        if (!_ready) return;
        if (value is not null && CreateNew) CreateNew = false;
        ResetTarget();
        _ = AnalyzeTargetAsync();
        UpdatePreview();
    }
    partial void OnKeepOldLinkChanged(bool value) { if (_ready) UpdatePreview(); }
    partial void OnPushAllBranchesChanged(bool value) { if (_ready) UpdatePreview(); }
    partial void OnUseNewBranchChanged(bool value) { if (_ready) UpdatePreview(); }
    partial void OnNewBranchNameChanged(string value) { if (_ready) UpdatePreview(); }
    partial void OnTargetSummaryChanged(string? value) => OnPropertyChanged(nameof(HasTargetSummary));

    public Task LoadAsync() => LoadRepositoriesAsync();

    private async Task LoadRepositoriesAsync()
    {
        if (SelectedAccount is not { } account) return;
        IsLoadingRepos = true;
        try
        {
            var currentFull = _project.Remote?.FullName;
            _allRepos = (await _host.Services.GitHub.ListRepositoriesAsync(account))
                .Where(r => !r.IsArchived && !r.FullName.Equals(currentFull, StringComparison.OrdinalIgnoreCase))
                .ToList();
            ApplyRepoFilter();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Repos de {Login} indisponibles", account.Login);
            _allRepos = [];
            ApplyRepoFilter();
            ValidationMessage = $"Impossible de lister les repos de {account.Login} : {e.Message}";
        }
        finally { IsLoadingRepos = false; }
    }

    private void ApplyRepoFilter()
    {
        var selected = SelectedRepository;
        Repositories.Clear();
        foreach (var r in _allRepos.Where(r => r.Name.Contains(RepoFilter.Trim(), StringComparison.OrdinalIgnoreCase))) Repositories.Add(r);
        if (selected is not null && Repositories.Contains(selected)) SelectedRepository = selected;
    }

    private void ResetTarget()
    {
        _analysisCts?.Cancel();
        TargetSummary = null;
        TargetDiverges = false;
        UseNewBranch = false;
    }

    private async Task AnalyzeTargetAsync()
    {
        if (CreateNew || SelectedRepository is not { } repo || SelectedAccount is not { } account) return;
        var cts = _analysisCts = new CancellationTokenSource();
        IsAnalyzingTarget = true;
        try
        {
            var analysis = await _host.Services.Transfer.AnalyzeTargetAsync(_project.Path, account, repo.Url + ".git", cts.Token);
            if (cts.IsCancellationRequested) return;
            TargetSummary = analysis.Summary;
            TargetDiverges = analysis.HasDivergingHistory;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!cts.IsCancellationRequested) TargetSummary = $"Impossible d'examiner le repo cible : {e.Message}"; }
        finally
        {
            if (_analysisCts == cts) IsAnalyzingTarget = false;
            UpdatePreview();
        }
    }

    private RepointPlan? BuildPlan()
    {
        if (SelectedAccount is not { } account) return null;
        if (!CreateNew && SelectedRepository is null) return null;
        return new RepointPlan(_project.Path, account, CreateNew ? NewName.Trim() : SelectedRepository!.Name)
        {
            TargetUrl = CreateNew ? null : SelectedRepository!.Url + ".git",
            Visibility = IsPrivate ? RepositoryVisibility.Private : RepositoryVisibility.Public,
            OldRemote = KeepOldLink ? OldRemoteAction.KeepAsOldOrigin : OldRemoteAction.Remove,
            Scope = PushAllBranches ? PushScope.AllBranchesAndTags : PushScope.CurrentBranch,
            PushToNewBranch = TargetDiverges && UseNewBranch ? NewBranchName.Trim() : null,
            RemoteName = _project.Project.RemoteName ?? "origin",
        };
    }

    private void UpdatePreview()
    {
        Preview.Clear();
        if (Mode == RepointMode.NativeTransfer && SelectedAccount is { } to && _project.Remote is { } remote)
        {
            Preview.Add($"gh api --method POST repos/{remote.FullName}/transfer -f new_owner={to.Login}   (avec le compte {SourceAccountLogin})");
            Preview.Add($"git remote set-url {_project.Project.RemoteName ?? "origin"} https://{to.Host}/{to.Login}/{remote.Repository}.git");
            return;
        }
        if (BuildPlan() is { } plan)
            foreach (var line in TransferService.Preview(plan, _project.Remote?.Url, _project.BranchLabel)) Preview.Add(line);
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        ValidationMessage = Validate();
        if (ValidationMessage is not null) return;
        if (Mode == RepointMode.NativeTransfer && !_host.Dialogs.Confirm(
                $"Transférer {_project.Remote!.FullName} vers {SelectedAccount!.Login} ?\n\nLe repo (issues, étoiles, historique) changera de propriétaire. "
                + $"{SelectedAccount.Login} devra accepter la demande reçue par e-mail.", "Transfert GitHub"))
            return;

        Step = RepointStep.Running;
        Progress.Clear();
        var progress = new Progress<string>(Progress.Add);
        try
        {
            var result = Mode == RepointMode.NativeTransfer
                ? await _host.Services.Transfer.TransferOwnershipAsync(_project.Path, _project.Placement.Account!, SelectedAccount!,
                    _project.Remote!.Repository!, _project.Project.RemoteName ?? "origin", progress)
                : await _host.Services.Transfer.RepointAsync(BuildPlan()!, progress);
            ResultMessage = result.Message;
            Done = true;
            Step = RepointStep.Done;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Changement de repo de {Path} impossible", _project.Path);
            ResultMessage = e.Message;
            Done = Progress.Any(p => p.StartsWith("Liaison")) || Mode == RepointMode.NativeTransfer;
            Step = RepointStep.Failed;
        }
    }

    private string? Validate()
    {
        if (SelectedAccount is null) return "Choisis un compte.";
        if (Mode == RepointMode.NativeTransfer) return null;
        if (CreateNew && !RepositoryName.IsValid(NewName.Trim())) return "Nom de repo invalide : lettres, chiffres, « - », « _ » et « . » uniquement.";
        if (!CreateNew && SelectedRepository is null) return "Choisis un repo dans la liste, ou « Créer un nouveau repo ».";
        if (IsAnalyzingTarget) return "Vérification du repo cible en cours, un instant…";
        if (TargetDiverges && !UseNewBranch) return "Le repo cible a un historique différent : coche « Envoyer sur une nouvelle branche » ou choisis un autre repo.";
        if (TargetDiverges && UseNewBranch && string.IsNullOrWhiteSpace(NewBranchName)) return "Donne un nom à la nouvelle branche.";
        return null;
    }

    [RelayCommand] private void BackToForm() => Step = RepointStep.Form;
    [RelayCommand] private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
