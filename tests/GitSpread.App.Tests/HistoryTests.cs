using GitSpread.App.ViewModels;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitSpread.App.Tests;

/// <summary>F6a : aperçu de l'historique et grande fenêtre.</summary>
public sealed class HistoryTests : IDisposable
{
    private readonly TempRepo _repo = new("historique app");

    /// <summary>main (2 commits) + fix/paiement fusionnée + feature/login pas encore intégrée.</summary>
    private async Task<LocalProject> ProjectWithBranchesAsync()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("switch", "-q", "-c", "fix/paiement");
        await _repo.CommitFileAsync("paiement.txt", "ok\n", "Corrige le paiement");
        await _repo.Git("switch", "-q", "main");
        await _repo.Git("merge", "-q", "--no-ff", "fix/paiement", "-m", "Merge branch 'fix/paiement'");
        await _repo.Git("switch", "-q", "-c", "feature/login");
        await _repo.CommitFileAsync("login.txt", "a\nb\n", "Ajoute le login");
        await _repo.Git("switch", "-q", "main");
        return ProjectScanner.ReadProject(_repo.Path)!;
    }

    [Fact]
    public async Task Shows_branches_in_plain_words_with_their_own_names_and_colours()
    {
        var project = await ProjectWithBranchesAsync();
        var history = new HistoryViewModel(new TestHost(realGit: true));

        await history.LoadAsync(project);

        Assert.Equal(["Ajoute le login", "Merge branch 'fix/paiement'", "Corrige le paiement", "Premier commit"], history.Rows.Select(r => r.Subject));
        Assert.Equal("3 branches · 4 commits", history.Summary);
        var main = history.Branches.Single(b => b.Name == "main");
        Assert.Equal("branche principale · seulement sur ce poste · branche actuelle", main.StateLabel);
        Assert.Equal(BranchKind.Default, main.Color.Kind);
        Assert.Equal("1 commit pas encore dans main · seulement sur ce poste", history.Branches.Single(b => b.Name == "feature/login").StateLabel);
        var fix = history.Branches.Single(b => b.Name == "fix/paiement");
        Assert.Equal("déjà intégrée dans main · seulement sur ce poste", fix.StateLabel);
        Assert.Equal(BranchKind.Maintenance, fix.Color.Kind);

        // Les étiquettes gardent le nom exact des branches.
        Assert.Equal(["feature/login"], history.Rows[0].Refs.Select(r => r.Name));
        Assert.True(history.Rows[1].Refs.Single() is { Name: "main", IsHead: true });
        Assert.Null(history.SelectedRow); // l'aperçu ne charge pas de détail
    }

    [Fact]
    public async Task The_large_window_shows_the_selected_commit_its_files_and_the_diff()
    {
        var project = await ProjectWithBranchesAsync();
        var history = new HistoryViewModel(new TestHost(realGit: true), withDetails: true);

        await history.LoadAsync(project);
        await TestHost.WaitUntil(() => history.Diff.Count > 0);

        Assert.Equal("Ajoute le login", history.Details!.Commit.Subject); // premier commit choisi d'office
        Assert.Equal("1 fichier modifié", history.FilesLabel);
        Assert.Equal("login.txt", history.SelectedFile!.Path);
        Assert.Equal([DiffLineKind.Hunk, DiffLineKind.Added, DiffLineKind.Added], history.Diff.Select(d => d.Kind));
        Assert.Equal("FICHIERS MODIFIÉS (1)", history.FilesHeader);
        Assert.Equal("MODIFICATIONS · login.txt", history.DiffHeader);

        history.SelectedRow = history.Rows[1]; // la fusion
        await TestHost.WaitUntil(() => history.Details?.Commit.Subject.StartsWith("Merge") == true && history.SelectedFile?.Path == "paiement.txt");
        Assert.Contains("réunit deux branches", history.ParentsLabel);
        Assert.Equal(["feature/login", "main"], history.Details!.Branches.Order());

        await history.SelectBranchCommand.ExecuteAsync(history.Branches.Single(b => b.Name == "fix/paiement"));
        Assert.Equal("Corrige le paiement", history.SelectedRow!.Subject);
    }

    [Fact]
    public async Task Loads_older_commits_on_demand()
    {
        var project = await ProjectWithBranchesAsync();
        var history = new HistoryViewModel(new TestHost(realGit: true)) { PageSize = 2 };

        await history.LoadAsync(project);
        Assert.Equal(2, history.Rows.Count);
        Assert.True(history.HasMore);
        Assert.Equal("3 branches · 2+ commits", history.Summary);

        await history.LoadMoreCommand.ExecuteAsync(null);
        await history.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(4, history.Rows.Count);
        Assert.False(history.HasMore);
    }

    [Fact]
    public async Task The_preview_follows_the_selected_project_and_opens_the_large_window()
    {
        var project = await ProjectWithBranchesAsync();
        var host = new TestHost(realGit: true);
        var tab = new UnlinkedTabViewModel { History = new HistoryViewModel(host) };
        var item = new ProjectItemViewModel(new ProjectPlacement(project, null, UnlinkedReason.NoRemote), host);
        var noGit = new ProjectItemViewModel(new ProjectPlacement(new LocalProject(_repo.Sub("sans git")) { IsRepository = false }, null, UnlinkedReason.NotARepository), host);
        tab.SetProjects([item, noGit]);

        tab.SelectedProject = item;
        await TestHost.WaitUntil(() => tab.History.Rows.Count == 4);

        tab.SelectedProject = noGit;
        await TestHost.WaitUntil(() => tab.History.HasMessage);
        Assert.Contains("pas encore suivi par git", tab.History.Message);
        Assert.Empty(tab.History.Rows);

        tab.SelectedProject = item;
        await TestHost.WaitUntil(() => tab.History.Rows.Count == 4);
        tab.History.SelectedRow = tab.History.Rows[2];
        tab.History.OpenLargeCommand.Execute(null);

        // L'historique en grand remplace le contenu de l'onglet (pas de nouvelle fenêtre).
        Assert.Empty(host.Shown);
        Assert.True(tab.IsExplorerOpen);
        var explorer = tab.Explorer!;
        Assert.Equal([HistoryExplorerViewModel.LocalGroup], explorer.Items.Select(p => p.Group).Distinct());
        await TestHost.WaitUntil(() => explorer.History.Details is not null);
        Assert.Equal("Corrige le paiement", explorer.History.SelectedRow!.Subject); // ouvert sur le commit choisi dans l'aperçu
        Assert.Equal(project.Name, explorer.Title);

        explorer.CloseCommand.Execute(null);
        Assert.False(tab.IsExplorerOpen);
    }

    [Fact]
    public async Task The_large_history_lists_local_projects_then_github_repositories_not_yet_cloned()
    {
        var project = await ProjectWithBranchesAsync();
        var host = new TestHost(realGit: true);
        host.Runner.When(["repo", "list"], """
            [{"name":"stock","owner":{"login":"SmartDev-Web"},"url":"https://github.com/SmartDev-Web/stock","sshUrl":"","visibility":"PRIVATE","isArchived":false,"isFork":false},
             {"name":"site","owner":{"login":"SmartDev-Web"},"url":"https://github.com/SmartDev-Web/site","sshUrl":"","visibility":"PUBLIC","isArchived":false,"isFork":false}]
            """).When(["api"], "[]");
        // « stock » est déjà cloné sur ce poste : il apparaît comme projet local, pas en double.
        var local = project with { RemoteName = "origin", Remote = Core.Git.RemoteUrlParser.Parse("https://github.com/SmartDev-Web/stock.git") };
        host.LocalProjects = [local];
        var tab = new AccountTabViewModel(TestHost.Pro, host, NullLogger.Instance) { History = new HistoryViewModel(host) };
        tab.SetProjects([new ProjectItemViewModel(new ProjectPlacement(local, TestHost.Pro, null), host)]);
        await tab.LoadRepositoriesAsync();

        tab.History.OpenLargeCommand.Execute(null);
        var explorer = tab.Explorer!;

        Assert.Equal([(HistoryExplorerViewModel.LocalGroup, project.Name), (HistoryExplorerViewModel.GitHubGroup, "site")],
            explorer.Items.Select(i => (i.Group, i.Name)));
        Assert.Null(explorer.Items[1].Project); // repo sur GitHub seulement : historique lu depuis sa copie légère (test suivant)
    }

    [Fact]
    public async Task A_github_repository_not_on_this_machine_shows_a_read_only_history()
    {
        // « GitHub » = dépôt nu local (le ViewModel ajoute .git à l'adresse du repo).
        var bare = await _repo.BareAsync("stock.git");
        await ProjectWithBranchesAsync();
        await _repo.Git("push", "-q", bare, "main", "feature/login", "fix/paiement");
        var host = new TestHost(realGit: true);
        var stock = new GitHubRepository("stock", "SmartDev-Web", bare[..^4], "", RepositoryVisibility.Private);
        var clone = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Task.CompletedTask);
        var history = new HistoryViewModel(host, withDetails: true);

        await history.ShowRepositoryAsync("stock", null, clone, TestHost.Pro, stock, defaultBranch: "main");

        Assert.True(history is { IsRemoteCopy: true, CanClone: true, HasMessage: false });
        Assert.Contains("en lecture seule", history.RemoteLabel);
        Assert.Equal(4, history.Rows.Count);
        Assert.DoesNotContain(history.Rows, r => r.Commit.IsHead); // pas de « position actuelle » dans une copie
        Assert.Equal("1 commit pas encore dans main · sur GitHub", history.Branches.Single(b => b.Name == "feature/login").StateLabel);
        Assert.Equal(host.Services.Cache.PathFor("github.com", "SmartDev-Web/stock"), history.Project!.Path); // jamais dans les dossiers de projets
        await TestHost.WaitUntil(() => history.Diff.Count > 0); // le contenu des fichiers est lu à la demande
        Assert.Equal("login.txt", history.SelectedFile!.Path);

        // Ouvrir en grand vise le repo (son nom complet), pas le dossier de la copie.
        HistoryOpenRequest? request = null;
        history.OpenLargeRequested += (_, r) => request = r;
        history.OpenLargeCommand.Execute(null);
        Assert.Equal("SmartDev-Web/stock", request!.Key);

        // Une copie déjà là est réutilisée (et mise à jour) : nouveau commit sur « GitHub », « Relire » le montre.
        await _repo.CommitFileAsync("nouveau.txt", "n", "Nouveau sur GitHub");
        await _repo.Git("push", "-q", bare, "main");
        await history.ReloadCommand.ExecuteAsync(null);
        Assert.Equal("Nouveau sur GitHub", history.Rows[0].Subject);
    }

    [Fact]
    public async Task A_github_repository_that_cannot_be_read_still_offers_to_clone_it()
    {
        var host = new TestHost(realGit: true);
        var missing = new GitHubRepository("absent", "SmartDev-Web", Path.Combine(_repo.Root, "absent"), "", RepositoryVisibility.Private);
        var history = new HistoryViewModel(host);

        await history.ShowRepositoryAsync("absent", null, new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Task.CompletedTask), TestHost.Pro, missing);

        Assert.True(history is { IsRemoteCopy: false, CanClone: true });
        Assert.Contains("illisible", history.Message);
    }

    public void Dispose() => _repo.Dispose();
}
