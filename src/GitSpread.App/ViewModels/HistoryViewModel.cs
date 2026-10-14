using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

/// <summary>Étiquette d'une branche ou d'un tag sur une ligne de l'historique (nom exact, jamais modifié).</summary>
public sealed record RefPill(string Name, BranchColor Color, bool IsRemote, bool IsTag, bool IsHead);

/// <summary>Une ligne de l'historique : le dessin du graphe et le commit.</summary>
public sealed partial class HistoryRowViewModel(HistoryCommit commit, GraphRow graph, IReadOnlyList<RefPill> refs) : ObservableObject
{
    /// <summary>Estompé : ne correspond pas à la recherche en cours (F6e).</summary>
    [ObservableProperty] public partial bool IsDimmed { get; set; }

    public HistoryCommit Commit { get; } = commit;
    public GraphRow Graph { get; } = graph;
    public IReadOnlyList<RefPill> Refs { get; } = refs;
    public bool HasRefs => Refs.Count > 0;
    public string Subject => Commit.Subject;
    public string Author => Commit.Author;
    public string ShortSha => Commit.ShortSha;
    public string DateLabel => DisplayPreferences.Date(Commit.Date);
    public string DateTooltip => Commit.Date.LocalDateTime.ToString("dddd d MMMM yyyy 'à' HH:mm");
    public override string ToString() => $"{ShortSha} {Subject}";
}

/// <summary>Une branche de l'onglet « Branches » : sa couleur, son dernier commit et sa situation en clair.</summary>
public sealed class BranchRowViewModel(BranchOverview overview, BranchColor color, string state, string? defaultBranch = null)
{
    public BranchOverview Overview { get; } = overview;
    public BranchInfo Info => Overview.Info;
    public BranchColor Color { get; } = color;
    public string Name => Overview.Name;
    public string StateLabel { get; } = state;
    public string LastCommit => Overview.Subject;
    public string DateLabel => Overview.Date is { } d ? DisplayPreferences.Date(d) : "";
    public bool IsCurrent => Overview.Info.IsCurrent;
    public bool IsRemoteOnly => Overview.Info.IsRemoteOnly;
    public string? DefaultBranch { get; } = defaultBranch;

    // ------------------------------------------------------------ actions possibles (F6b)

    public bool CanSwitch => !IsCurrent;
    public string SwitchLabel => IsRemoteOnly ? "Récupérer et basculer ici" : "Basculer ici";
    public bool CanIntegrate => !Overview.IsDefault && Overview.NotInDefault > 0 && DefaultBranch is not null;
    public string IntegrateLabel => $"Intégrer dans {DefaultBranch}";
    public bool CanPush => Info.IsLocal && (Info.Upstream is null || Info.Ahead > 0);
    public string PushLabel => Info.Upstream is null ? "Envoyer sur GitHub" : $"Pousser ({Info.Ahead})";
    public bool CanDelete => !Overview.IsDefault && !IsCurrent;
    public override string ToString() => Name;
}

/// <summary>
/// Historique d'un projet (F6a) : graphe des branches, détail du commit choisi et ses modifications.
/// Sert à l'aperçu en bas de page et à la grande fenêtre. Tout est lu en local : rien n'est envoyé.
/// </summary>
public sealed partial class HistoryViewModel(IProjectHost host, bool withDetails = false) : ObservableObject
{
    private readonly List<HistoryCommit> _commits = [];
    private IReadOnlyList<BranchOverview> _branches = [];
    private string? _defaultBranch;
    private IReadOnlyList<string> _remotes = [];
    private int _version;
    private ILogger Logger => host.Services.LoggerFactory.CreateLogger<HistoryViewModel>();
    private GitService Git => host.Services.Git;

    public const double LaneWidth = 16;

    /// <summary>Vrai dans la grande fenêtre : le détail du commit choisi et ses modifications sont chargés.</summary>
    public bool WithDetails { get; } = withDetails;

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];
    public ObservableCollection<BranchRowViewModel> Branches { get; } = [];

    [ObservableProperty] public partial LocalProject? Project { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool HasMore { get; set; }
    [ObservableProperty] public partial double GraphWidth { get; set; } = LaneWidth;
    [ObservableProperty] public partial HistoryRowViewModel? SelectedRow { get; set; }

    public string Title => Project?.Name ?? "Historique";
    public string? DefaultBranch => _defaultBranch;
    public string Summary => Project is null || Rows.Count == 0 ? ""
        : $"{Branches.Count} branche{(Branches.Count > 1 ? "s" : "")} · {Rows.Count}{(HasMore ? "+" : "")} commit{(Rows.Count > 1 ? "s" : "")}";
    public bool HasMessage => Message is not null;
    public bool HasRows => Rows.Count > 0;

    /// <summary>Repo GitHub pas encore cloné : l'aperçu propose de le cloner (action fournie par le panneau du repo).</summary>
    [ObservableProperty] public partial IAsyncRelayCommand? CloneCommand { get; set; }
    public bool CanClone => CloneCommand is not null;

    partial void OnProjectChanged(LocalProject? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsRemoteCopy));
        OnPropertyChanged(nameof(RemoteLabel));
    }
    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnCloneCommandChanged(IAsyncRelayCommand? value) => OnPropertyChanged(nameof(CanClone));
    partial void OnSelectedRowChanged(HistoryRowViewModel? value) { if (WithDetails) _ = LoadDetailsAsync(value); }

    // ------------------------------------------------------------ chargement

    /// <summary>Affiche l'historique d'un projet local (ou un message s'il n'y en a pas).</summary>
    public Task ShowAsync(ProjectItemViewModel? project) => project switch
    {
        null => ShowMessage("Sélectionne un projet pour voir ses branches et son historique."),
        { IsRepository: false } => ShowMessage($"« {project.Name} » n'est pas encore suivi par git : il n'a pas encore d'historique."),
        _ => LoadAsync(project.Project),
    };

    /// <summary>
    /// Repo GitHub : l'historique de son projet local lié, sinon celui de sa copie légère en lecture seule (F6c),
    /// lue avec le compte de l'onglet. Le bouton « Cloner en local » reste proposé.
    /// </summary>
    public Task ShowRepositoryAsync(string? name, LocalProject? linked, IAsyncRelayCommand? clone,
        GitHubAccount? account = null, GitHubRepository? repository = null, string? defaultBranch = null)
    {
        CloneCommand = linked is null ? clone : null;
        if (linked is not null) return LoadAsync(linked);
        if (account is not null && repository is not null) return LoadRemoteCopyAsync(account, repository, defaultBranch, force: false);
        return ShowMessage(name is null
            ? "Sélectionne un repo pour voir son historique."
            : $"« {name} » n'est pas cloné sur ce poste : clone-le pour voir ses branches et son historique.", keepClone: true);
    }

    private Task ShowMessage(string message, bool keepClone = false)
    {
        _version++;
        if (!keepClone) CloneCommand = null;
        _remote = null;
        Project = null;
        Clear();
        Message = message;
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------ copie légère d'un repo GitHub (F6c)

    private sealed record RemoteSource(GitHubAccount Account, GitHubRepository Repository, string? DefaultBranch, IReadOnlyDictionary<string, string> Auth);
    private RemoteSource? _remote;

    /// <summary>Historique lu depuis GitHub (repo pas encore cloné) : lecture seule, aucune action sur les branches.</summary>
    public bool IsRemoteCopy => _remote is not null && Project is not null;

    /// <summary>Copie pas à jour (GitHub injoignable), ou message de lecture.</summary>
    [ObservableProperty] public partial string? RemoteNote { get; set; }
    public string RemoteLabel => _remote is { } r
        ? $"Aperçu de {r.Repository.FullName} sur GitHub, en lecture seule : ce repo n'est pas sur ce poste."
        : "";

    private async Task LoadRemoteCopyAsync(GitHubAccount account, GitHubRepository repository, string? defaultBranch, bool force)
    {
        var version = ++_version;
        var sameRepo = _remote?.Repository.FullName == repository.FullName;
        if (!sameRepo) { Project = null; Clear(); }
        IsLoading = true;
        Message = sameRepo ? null : $"Lecture de l'historique de « {repository.Name} » sur GitHub…";
        try
        {
            var copy = await host.Services.Cache.EnsureAsync(account, account.Host, repository.FullName, repository.Url + ".git", force);
            if (version != _version) return;
            _remote = new RemoteSource(account, repository, defaultBranch, copy.Auth);
            RemoteNote = copy.Warning;
            await LoadCoreAsync(new LocalProject(copy.Path), null, version);
        }
        catch (Exception e)
        {
            if (version != _version) return;
            Logger.LogWarning(e, "Copie légère de {Repo} impossible", repository.FullName);
            _remote = null;
            Project = null;
            Clear();
            Message = $"Historique de « {repository.Name} » illisible : {e.Message}";
            IsLoading = false;
        }
        OnPropertyChanged(nameof(IsRemoteCopy));
        OnPropertyChanged(nameof(RemoteLabel));
    }

    private void Clear()
    {
        _commits.Clear();
        Rows.Clear();
        Branches.Clear();
        SelectedRow = null;
        HasMore = false;
        RaiseLists();
    }

    /// <param name="selectSha">Commit à sélectionner une fois chargé (par défaut : celui déjà choisi, si c'est le même projet).</param>
    public Task LoadAsync(LocalProject project, string? selectSha = null)
    {
        if (Project?.Path != project.Path) CloneCommand = null;
        _remote = null;
        RemoteNote = null;
        OnPropertyChanged(nameof(IsRemoteCopy));
        return LoadCoreAsync(project, selectSha, ++_version);
    }

    private async Task LoadCoreAsync(LocalProject project, string? selectSha, int version)
    {
        var keep = selectSha ?? (Project?.Path == project.Path ? SelectedRow?.Commit.Sha : null);
        Project = project;
        IsLoading = true;
        try
        {
            var path = project.Path;
            var remotesTask = Git.GetRemotesAsync(path);
            // Copie légère : sa branche par défaut est celle de GitHub (HEAD du dépôt nu).
            _defaultBranch = _remote is { } remote
                ? remote.DefaultBranch ?? await Git.GetHeadBranchAsync(path)
                : await Git.GetDefaultBranchAsync(path, project.RemoteName ?? "origin");
            var branches = await Git.GetBranchOverviewAsync(path, _defaultBranch);
            var commits = await Git.ReadHistoryAsync(path, 0, PageSize);
            var remotes = (await remotesTask).Select(r => r.Name).ToList();
            if (version != _version) return; // un autre projet a été choisi entre-temps

            _remotes = remotes;
            _branches = branches;
            _commits.Clear();
            _commits.AddRange(_remote is null ? commits : commits.Select(WithoutHead)); // pas de « position actuelle » dans une copie
            HasMore = commits.Count == PageSize;
            Message = commits.Count == 0 ? $"« {project.Name} » n'a encore aucun commit." : null;
            Render(keep);
        }
        catch (Exception e)
        {
            if (version != _version) return;
            Logger.LogWarning(e, "Historique de {Path} illisible", project.Path);
            Clear();
            Message = $"Historique illisible : {e.Message}";
        }
        finally { if (version == _version) IsLoading = false; }
    }

    /// <summary>Nombre de commits chargés à chaque fois (« Afficher plus » charge la suite).</summary>
    public int PageSize { get; set; } = GitService.HistoryPageSize;

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (Project is not { } project || !HasMore || IsLoading) return;
        var version = _version;
        IsLoading = true;
        try
        {
            var more = await Git.ReadHistoryAsync(project.Path, _commits.Count, PageSize);
            if (version != _version) return;
            _commits.AddRange(_remote is null ? more : more.Select(WithoutHead));
            HasMore = more.Count == PageSize;
            Render(SelectedRow?.Commit.Sha);
        }
        catch (Exception e) { Message = $"Suite de l'historique illisible : {e.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private Task Reload() => _remote is { } r
        ? LoadRemoteCopyAsync(r.Account, r.Repository, r.DefaultBranch, force: true) // relit GitHub
        : Project is { } p ? LoadAsync(p) : Task.CompletedTask;

    private static HistoryCommit WithoutHead(HistoryCommit c) => c.IsHead ? c with { Refs = c.Refs.Where(r => r != "HEAD").ToList() } : c;

    /// <summary>Calcule le graphe et remplit les listes (la ligne choisie est conservée si elle existe encore).</summary>
    private void Render(string? keepSha)
    {
        // La branche principale d'abord, puis les branches locales, puis celles qui ne sont que sur GitHub.
        var tips = new List<(string, string)>();
        foreach (var b in _branches.OrderByDescending(b => b.IsDefault).ThenBy(b => b.Info.IsRemoteOnly))
        {
            tips.Add((b.Name, b.TipSha));
            if (b.RemoteTipSha is { } remoteTip && remoteTip != b.TipSha) tips.Add((b.Name, remoteTip));
        }
        var layout = HistoryGraph.Layout(_commits, tips, _defaultBranch, Host.Settings.MaintenanceTerms);

        Rows.Clear();
        for (var i = 0; i < _commits.Count; i++)
        {
            var c = _commits[i];
            var pills = c.Refs.Where(r => r != "HEAD").Select(r =>
            {
                var isTag = r.StartsWith("tag: ");
                var local = BranchKinds.Local(r, _remotes);
                var color = layout.BranchColors.TryGetValue(local, out var known) ? known : new BranchColor(BranchKind.Classic, -1);
                return new RefPill(isTag ? r[5..] : r, color, IsRemote: !isTag && local != r, isTag, IsHead: c.IsHead && !isTag && local == r && _branches.Any(b => b.Name == r && b.Info.IsCurrent));
            }).ToList();
            Rows.Add(new HistoryRowViewModel(c, layout.Rows[i], pills));
        }
        GraphWidth = Math.Max(1, layout.MaxLanes) * LaneWidth + 4;

        Branches.Clear();
        foreach (var b in _branches)
            Branches.Add(new BranchRowViewModel(b, layout.BranchColors.TryGetValue(b.Name, out var color) ? color : new BranchColor(BranchKind.Classic, -1),
                StateOf(b), _defaultBranch));

        SelectedRow = Rows.FirstOrDefault(r => r.Commit.Sha == keepSha) ?? (WithDetails ? Rows.FirstOrDefault() : null);
        ApplySearch(selectFirst: false);
        RaiseLists();
    }

    private void RaiseLists()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>Situation d'une branche en clair, ex. « 3 commits pas encore dans main · à jour avec GitHub ».</summary>
    internal string StateOf(BranchOverview b)
    {
        var where = Project?.Remote is { IsGitHub: false } ? "le dépôt distant" : "GitHub";
        var parts = new List<string>();
        if (b.IsDefault) parts.Add("branche principale");
        else if (b.NotInDefault is { } n && _defaultBranch is { } main)
            parts.Add(n == 0 ? $"déjà intégrée dans {main}" : $"{n} commit{(n > 1 ? "s" : "")} pas encore dans {main}");

        if (_remote is not null)
        {
            parts.Add("sur GitHub"); // copie légère : toutes les branches viennent de GitHub
            return string.Join(" · ", parts);
        }

        var info = b.Info;
        if (info.IsRemoteOnly) parts.Add($"seulement sur {where}");
        else if (info.UpstreamGone) parts.Add($"supprimée sur {where}");
        else if (info.Upstream is null && info.RemoteRef is null) parts.Add("seulement sur ce poste");
        else if (info.Upstream is null) parts.Add($"aussi sur {where}");
        else if (info is { Ahead: 0, Behind: 0 }) parts.Add($"à jour avec {where}");
        else
        {
            if (info.Ahead > 0) parts.Add($"{info.Ahead} à envoyer");
            if (info.Behind > 0) parts.Add($"{info.Behind} à récupérer");
        }
        if (info.IsCurrent) parts.Add("branche actuelle");
        return string.Join(" · ", parts);
    }

    /// <summary>Montre le dernier commit d'une branche (onglet « Branches ») ; charge la suite de l'historique au besoin.</summary>
    [RelayCommand]
    private async Task SelectBranchAsync(BranchRowViewModel? branch)
    {
        if (branch is null) return;
        for (var pages = 0; pages < 10; pages++)
        {
            if (Rows.FirstOrDefault(r => r.Commit.Sha == branch.Overview.TipSha) is { } row) { SelectedRow = row; return; }
            if (!HasMore) return;
            await LoadMoreAsync();
        }
    }

    // ------------------------------------------------------------ détail du commit choisi (grande fenêtre)

    [ObservableProperty] public partial CommitDetails? Details { get; set; }
    [ObservableProperty] public partial CommitFile? SelectedFile { get; set; }
    [ObservableProperty] public partial bool IsLoadingDiff { get; set; }
    public ObservableCollection<CommitFile> Files { get; } = [];
    public ObservableCollection<DiffLine> Diff { get; } = [];
    public bool HasDetails => Details is not null;
    public bool HasNoFiles => Details is not null && Files.Count == 0;
    public string FilesHeader => ShowAllFiles
        ? (_allFiles.Count > 0 ? $"TOUS LES FICHIERS ({_allFiles.Count})" : "TOUS LES FICHIERS")
        : $"FICHIERS MODIFIÉS ({Files.Count})";
    /// <summary>Titre de la visionneuse : modifications du fichier, ou son contenu à ce commit (F6e).</summary>
    public string DiffHeader => (ViewMode, ViewedPath) switch
    {
        (FileViewMode.Content, { } path) => $"CONTENU À CE COMMIT · {path}",
        (_, { } path) => $"MODIFICATIONS · {path}",
        _ => "MODIFICATIONS",
    };
    public string FilesLabel => Details is null ? "" : Files.Count switch
    {
        0 => "Aucun fichier modifié (commit vide ou fusion sans changement).",
        1 => "1 fichier modifié",
        var n => $"{n} fichiers modifiés",
    };
    public string AuthorLabel => Details is { } d
        ? d.Commit.Author + (d.Committer != d.Commit.Author ? $" (enregistré par {d.Committer})" : "") : "";
    public string DateLabel => Details?.Commit.Date.LocalDateTime.ToString("dddd d MMMM yyyy 'à' HH:mm") ?? "";
    public string ParentsLabel => Details?.Commit.Parents.Count switch
    {
        null => "",
        0 => "Premier commit du projet",
        1 => "",
        _ => "Fusion : réunit deux branches. Les fichiers listés sont ceux apportés par rapport à la branche d'arrivée.",
    };

    partial void OnDetailsChanged(CommitDetails? value)
    {
        foreach (var p in new[] { nameof(HasDetails), nameof(FilesLabel), nameof(AuthorLabel), nameof(DateLabel), nameof(ParentsLabel),
                     nameof(HasNoFiles), nameof(FilesHeader), nameof(NoChangedFilesShown) }) OnPropertyChanged(p);
    }
    partial void OnSelectedFileChanged(CommitFile? value)
    {
        _contentVersion++; // un contenu en cours de lecture ne doit plus rien afficher
        ContentLines.Clear();
        ImagePath = null;
        ContentNote = null;
        FileStatus = null;
        ViewedPath = value?.Path;
        ViewMode = FileViewMode.Changes;
        OnPropertyChanged(nameof(DiffHeader));
        _ = LoadDiffAsync(value);
    }

    private int _detailsVersion;

    private async Task LoadDetailsAsync(HistoryRowViewModel? row)
    {
        var version = ++_detailsVersion;
        if (row is null || Project is not { } project)
        {
            Details = null;
            Files.Clear();
            SelectedFile = null;
            Diff.Clear();
            ResetFiles(null, version);
            return;
        }
        try
        {
            var details = await Git.GetCommitDetailsAsync(project.Path, row.Commit.Sha, _remote?.Auth);
            if (version != _detailsVersion) return;
            Files.Clear();
            foreach (var f in details.Files) Files.Add(f);
            Details = details;
            ResetFiles(details, version);
            // Remis à zéro d'abord : deux commits peuvent avoir le même premier fichier (même modification), à relire quand même.
            SelectedFile = null;
            SelectedFile = Files.FirstOrDefault();
            if (SelectedFile is null) Diff.Clear();
        }
        catch (Exception e)
        {
            if (version != _detailsVersion) return;
            Logger.LogWarning(e, "Détail du commit {Sha} illisible", row.Commit.Sha);
            Details = null;
            Message = $"Détail du commit illisible : {e.Message}";
        }
    }

    private int _diffVersion;

    private async Task LoadDiffAsync(CommitFile? file)
    {
        var version = ++_diffVersion;
        Diff.Clear();
        if (file is null || Details is not { } details || Project is not { } project) return;
        IsLoadingDiff = true;
        try
        {
            var lines = await Git.GetFileDiffAsync(project.Path, details.Commit, file, _remote?.Auth);
            if (version != _diffVersion) return;
            foreach (var l in lines) Diff.Add(l);
        }
        catch (Exception e)
        {
            if (version == _diffVersion) Diff.Add(new DiffLine(DiffLineKind.Header, $"Modifications illisibles : {e.Message}"));
        }
        finally { if (version == _diffVersion) IsLoadingDiff = false; }
    }

    // ------------------------------------------------------------ historique en grand

    /// <summary>L'onglet affiche l'historique en grand à la place de son contenu, sur ce projet et ce commit.</summary>
    public event EventHandler<HistoryOpenRequest>? OpenLargeRequested;

    [RelayCommand]
    private void OpenLarge() => OpenLargeRequested?.Invoke(this,
        new HistoryOpenRequest(_remote?.Repository.FullName ?? Project?.Path, SelectedRow?.Commit.Sha));

    internal IProjectHost Host => host;
}
