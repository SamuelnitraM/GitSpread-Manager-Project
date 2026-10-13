using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

public enum PublishStep { Form, Running, Done, Failed }

/// <summary>Publier un projet local (avec ou sans git) dans un nouveau repo GitHub du compte choisi.</summary>
public sealed partial class PublishViewModel : ObservableObject
{
    public const string NoTemplate = "(aucun)";

    private readonly ProjectItemViewModel _project;
    private readonly IProjectHost _host;
    private readonly ILogger _logger;

    public PublishViewModel(ProjectItemViewModel project, IProjectHost host)
    {
        _project = project;
        _host = host;
        _logger = host.Services.LoggerFactory.CreateLogger<PublishViewModel>();
        Identity = new IdentityViewModel(project.Path, host);

        Accounts = host.Accounts.Where(a => a.IsAuthenticated).ToList();
        SelectedAccount = project.Placement.Account
                          ?? Accounts.FirstOrDefault(a => a.IsActive)
                          ?? Accounts.FirstOrDefault();
        RepositoryName = Core.GitHub.RepositoryName.FromFolder(project.Name);
        HasGitignore = File.Exists(Path.Combine(project.Path, ".gitignore"));
        GuessedTemplate = HasGitignore ? null : GitignoreGuesser.Guess(project.Path);
        SelectedTemplate = GuessedTemplate ?? NoTemplate;
        _ready = true;
        UpdatePreview();
    }

    private readonly bool _ready;

    public event EventHandler? CloseRequested;

    public string ProjectName => _project.Name;
    public bool IsRepository => _project.IsRepository;
    public IReadOnlyList<GitHubAccount> Accounts { get; }
    public bool HasAccounts => Accounts.Count > 0;
    public IdentityViewModel Identity { get; }
    public bool HasGitignore { get; }
    public string? GuessedTemplate { get; }
    public string GitignoreHint => GuessedTemplate is { } t ? $"Proposé d'après le contenu du dossier : {t}" : "Aucun type de projet reconnu automatiquement.";

    public ObservableCollection<string> Templates { get; } = [NoTemplate];
    public ObservableCollection<string> Preview { get; } = [];
    public ObservableCollection<string> Progress { get; } = [];

    [ObservableProperty] public partial GitHubAccount? SelectedAccount { get; set; }
    [ObservableProperty] public partial string RepositoryName { get; set; }
    [ObservableProperty] public partial bool IsPrivate { get; set; } = true;
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial string SelectedTemplate { get; set; }
    [ObservableProperty] public partial string CommitMessage { get; set; } = "Premier commit";
    [ObservableProperty] public partial bool UseSuggestedIdentity { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsForm))] public partial PublishStep Step { get; set; } = PublishStep.Form;
    [ObservableProperty] public partial string? ValidationMessage { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial string? RepositoryUrl { get; set; }

    public bool IsForm => Step == PublishStep.Form;
    public bool Published { get; private set; }

    partial void OnSelectedAccountChanged(GitHubAccount? value)
    {
        if (!_ready) return;
        _ = LoadIdentityAsync();
        UpdatePreview();
    }

    private async Task LoadIdentityAsync()
    {
        await Identity.LoadAsync(SelectedAccount, IsRepository);
        UseSuggestedIdentity = Identity.HasWarning && Identity.CanUseSuggested;
    }
    partial void OnRepositoryNameChanged(string value) { if (_ready) UpdatePreview(); }
    partial void OnIsPrivateChanged(bool value) { if (_ready) UpdatePreview(); }
    partial void OnSelectedTemplateChanged(string value) { if (_ready) UpdatePreview(); }
    partial void OnCommitMessageChanged(string value) { if (_ready) UpdatePreview(); }
    partial void OnUseSuggestedIdentityChanged(bool value) { if (_ready) UpdatePreview(); }

    public async Task LoadAsync()
    {
        if (SelectedAccount is not { } account) return;
        await LoadIdentityAsync();
        if (HasGitignore) return;
        try
        {
            foreach (var t in await _host.Services.GitHub.ListGitignoreTemplatesAsync(account))
                if (!Templates.Contains(t)) Templates.Add(t);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Modèles .gitignore indisponibles");
            if (GuessedTemplate is { } guess && !Templates.Contains(guess)) Templates.Add(guess);
        }
    }

    private PublishPlan? BuildPlan() => SelectedAccount is not { } account ? null
        : new PublishPlan(_project.Path, account, RepositoryName.Trim(), IsPrivate ? RepositoryVisibility.Private : RepositoryVisibility.Public)
        {
            Description = Description,
            InitializeRepository = !IsRepository,
            GitignoreTemplate = HasGitignore || SelectedTemplate == NoTemplate ? null : SelectedTemplate,
            Identity = UseSuggestedIdentity ? Identity.Suggested : null,
            CommitPendingChanges = true,
            CommitMessage = CommitMessage.Trim().Length > 0 ? CommitMessage.Trim() : "Premier commit",
        };

    private void UpdatePreview()
    {
        Preview.Clear();
        if (BuildPlan() is { } plan)
            foreach (var line in ProjectPublisher.Preview(plan)) Preview.Add(line);
    }

    [RelayCommand]
    private async Task PublishAsync()
    {
        ValidationMessage = SelectedAccount is null ? "Connecte d'abord un compte GitHub (bouton « + »)."
            : !Core.GitHub.RepositoryName.IsValid(RepositoryName.Trim()) ? "Nom de repo invalide : lettres, chiffres, « - », « _ » et « . » uniquement."
            : null;
        if (ValidationMessage is not null || BuildPlan() is not { } plan) return;

        Step = PublishStep.Running;
        Progress.Clear();
        try
        {
            RepositoryUrl = await _host.Services.Publisher.PublishAsync(plan, new Progress<string>(Progress.Add));
            Published = true;
            Step = PublishStep.Done;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Publication de {Path} impossible", _project.Path);
            ErrorMessage = e.Message;
            // Le repo a pu être créé avant l'échec : la liste doit être rafraîchie quoi qu'il arrive.
            Published = Progress.Any(p => p.StartsWith("Liaison"));
            Step = PublishStep.Failed;
        }
    }

    [RelayCommand] private void BackToForm() => Step = PublishStep.Form;
    [RelayCommand] private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void OpenRepository() { if (RepositoryUrl is { } url) Shell.Open(url.EndsWith(".git") ? url[..^4] : url); }
}
