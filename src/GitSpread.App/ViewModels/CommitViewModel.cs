using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.GitHub;
using GitSpread.Core.Git;

namespace GitSpread.App.ViewModels;

public sealed partial class ChangeItemViewModel(FileChange change) : ObservableObject
{
    public FileChange Change { get; } = change;
    public string Path => Change.OriginalPath is { } from ? $"{Change.Path}   ← {from}" : Change.Path;
    public ChangeKind Kind => Change.Kind;

    public string KindLabel => Change.Kind switch
    {
        ChangeKind.Added => "Ajouté",
        ChangeKind.Deleted => "Supprimé",
        ChangeKind.Renamed => "Renommé",
        ChangeKind.Copied => "Copié",
        ChangeKind.Untracked => Change.Path.EndsWith('/') ? "Nouveau dossier" : "Nouveau",
        ChangeKind.Conflicted => "Conflit",
        ChangeKind.TypeChanged => "Type changé",
        _ => "Modifié",
    };

    /// <summary>Dossier de dépendances ou de compilation : décoché par défaut.</summary>
    public bool IsGenerated => Change.IsLikelyGenerated;

    [ObservableProperty] public partial bool IsSelected { get; set; } = !change.IsLikelyGenerated;
}

/// <summary>Compte lié au projet, ajouté au commit comme co-auteur (GitHub l'affiche alors comme contributeur).</summary>
public sealed partial class CoAuthorChoice(string login, string trailer) : ObservableObject
{
    public string Login { get; } = login;
    public string Trailer { get; } = trailer;
    public string Label => $"Ajouter {Login} comme co-auteur";
    [ObservableProperty] public partial bool IsChecked { get; set; }
}

/// <summary>Fenêtre de commit : choix des fichiers, message, vérification de l'identité.</summary>
public sealed partial class CommitViewModel : ObservableObject
{
    private readonly ProjectItemViewModel _project;

    public CommitViewModel(ProjectItemViewModel project, IProjectHost host)
    {
        _project = project;
        _host = host;
        Identity = new IdentityViewModel(project.Path, host);
        Changes.CollectionChanged += (_, _) => Refresh();
        _git = host.Services.Git;
        GitignoreSuggestion = GitignoreGuesser.Guess(project.Path);
    }

    private readonly GitService _git;
    private readonly IProjectHost _host;

    public string? GitignoreSuggestion { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowGitignoreAdvice), nameof(CanAddGitignore), nameof(AddGitignoreLabel))]
    public partial bool HasGitignore { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGitignoreResult))] public partial string? GitignoreResult { get; set; }
    [ObservableProperty] public partial bool GitignoreFailed { get; set; }
    public bool HasGitignoreResult => GitignoreResult is not null;

    public bool HasGeneratedFolders => Changes.Any(c => c.IsGenerated);
    public bool ShowGitignoreAdvice => !HasGitignore && (HasGeneratedFolders || GitignoreSuggestion is not null);
    public bool CanAddGitignore => !HasGitignore && GitignoreSuggestion is not null;
    public string AddGitignoreLabel => $"Ajouter un .gitignore « {GitignoreSuggestion} »";

    public string ProjectName => _project.Name;
    public ObservableCollection<ChangeItemViewModel> Changes { get; } = [];
    public IdentityViewModel Identity { get; }
    public bool CanPushAfter => _project.CanSync;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCommit), nameof(CommandsPreview))]
    public partial string Message { get; set; } = "";

    [ObservableProperty] public partial bool PushAfterCommit { get; set; }
    [ObservableProperty] public partial string? IdentityResult { get; set; }
    [ObservableProperty] public partial bool IdentityFailed { get; set; }

    public bool HasChanges => Changes.Count > 0;
    public bool AllSelected => Changes.All(c => c.IsSelected);
    public bool CanCommit => Message.Trim().Length > 0 && Changes.Any(c => c.IsSelected);
    public string SelectionSummary => $"{Changes.Count(c => c.IsSelected)} fichier(s) sur {Changes.Count} sélectionné(s)";

    public IReadOnlyCollection<string> SelectedPathspecs =>
        Changes.Where(c => c.IsSelected).SelectMany(c => c.Change.Pathspecs).Distinct().ToList();

    public string CommandsPreview
    {
        get
        {
            var message = (Message.Trim().Length == 0 ? "…" : Message.Trim().Split('\n')[0])
                          + (CoAuthors.Any(c => c.IsChecked) ? " (+ Co-authored-by)" : "");
            if (AllSelected) return $"git add --all\ngit commit -m \"{message}\"";
            var files = string.Join(' ', SelectedPathspecs.Select(p => p.Contains(' ') ? $"\"{p}\"" : p));
            return $"git add --all -- {files}\ngit commit -m \"{message}\" -- {files}";
        }
    }

    public async Task LoadAsync()
    {
        await LoadChangesAsync();
        await Identity.LoadAsync(_project.Placement.NetworkAccount);
        await LoadCoAuthorsAsync();
    }

    // ------------------------------------------------------------ co-auteurs (F3B)

    /// <summary>Autres comptes liés au projet, qu'on peut créditer comme co-auteurs (décochés par défaut).</summary>
    public ObservableCollection<CoAuthorChoice> CoAuthors { get; } = [];
    public bool HasCoAuthors => CoAuthors.Count > 0;

    /// <summary>Message envoyé à git : celui saisi, suivi des lignes « Co-authored-by » cochées.</summary>
    public string FinalMessage => CoAuthors.Where(c => c.IsChecked).Select(c => c.Trailer).ToList() is { Count: > 0 } trailers
        ? Message.Trim() + "\n\n" + string.Join("\n", trailers)
        : Message.Trim();

    private async Task LoadCoAuthorsAsync()
    {
        var placement = _project.Placement;
        var related = placement.AccessAccounts.Concat(placement.SecondaryLinks.Select(l => l.Account).OfType<Core.Accounts.GitHubAccount>())
            .DistinctBy(a => (a.Host, a.Login.ToLowerInvariant())).ToList();
        if (related.Count < 2) return; // un seul compte : personne d'autre à créditer

        var email = (await _git.GetIdentityAsync(_project.Path)).Email;
        foreach (var account in related)
        {
            if (await _host.GetAccountUserAsync(account) is not { } user) continue;
            if (email is not null && (user.OwnsEmail(email) || _host.Settings.IsKnownEmail(account.Host, account.Login, email))) continue; // c'est l'auteur
            var choice = new CoAuthorChoice(account.Login, $"Co-authored-by: {user.Name ?? user.Login} <{user.NoReplyEmail}>");
            choice.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CommandsPreview));
            CoAuthors.Add(choice);
        }
        OnPropertyChanged(nameof(HasCoAuthors));
    }

    private async Task LoadChangesAsync()
    {
        HasGitignore = File.Exists(Path.Combine(_project.Path, ".gitignore"));
        Changes.Clear();
        foreach (var change in await _git.GetChangesAsync(_project.Path))
        {
            var item = new ChangeItemViewModel(change);
            item.PropertyChanged += OnItemChanged;
            Changes.Add(item);
        }
        OnPropertyChanged(nameof(HasGeneratedFolders));
        OnPropertyChanged(nameof(ShowGitignoreAdvice));
    }

    [RelayCommand]
    private async Task AddGitignoreAsync()
    {
        var account = _project.Placement.NetworkAccount ?? _host.Accounts.FirstOrDefault(a => a.IsAuthenticated);
        if (GitignoreSuggestion is not { } template) return;
        if (account is null)
        {
            (GitignoreResult, GitignoreFailed) = ("Connecte un compte GitHub pour télécharger le modèle de .gitignore.", true);
            return;
        }
        try
        {
            var content = await _host.Services.GitHub.GetGitignoreTemplateAsync(account, template);
            await File.WriteAllTextAsync(Path.Combine(_project.Path, ".gitignore"), content);
            (GitignoreResult, GitignoreFailed) = ($".gitignore « {template} » ajouté : les fichiers qu'il exclut ne seront plus proposés.", false);
            await LoadChangesAsync();
        }
        catch (Exception e)
        {
            (GitignoreResult, GitignoreFailed) = ($"Impossible d'ajouter le .gitignore : {e.Message}", true);
        }
    }

    [RelayCommand]
    private void ToggleAll()
    {
        var select = !AllSelected;
        foreach (var c in Changes) c.IsSelected = select;
    }

    [RelayCommand]
    private async Task UseSuggestedIdentityAsync()
    {
        var results = await Identity.ApplySuggestedAsync();
        IdentityFailed = !results.All(r => r.Succeeded);
        IdentityResult = !IdentityFailed
            ? "Identité enregistrée pour ce projet uniquement : " + string.Join("  ·  ", results.Select(r => r.Command))
            : results.First(r => !r.Succeeded).Output;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CommandsPreview));
    }
}
