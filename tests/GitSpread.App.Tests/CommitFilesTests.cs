using GitSpread.App.ViewModels;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;

namespace GitSpread.App.Tests;

/// <summary>F6e : tous les fichiers du projet à un commit, leur contenu, l'enregistrement, la restauration, et la recherche.</summary>
public sealed class CommitFilesTests : IDisposable
{
    private readonly TempRepo _repo = new("fichiers app");

    /// <summary>v1 : README, src/app.cs, src/ui/vue.xaml, docs/guide.md · v2 : modifie src/app.cs, supprime docs/guide.md, ajoute logo.png.</summary>
    private async Task<LocalProject> ProjectAsync()
    {
        await _repo.WithCommitAsync();
        _repo.Write("src/app.cs", "ligne 1\nligne 2\n");
        _repo.Write("src/ui/vue.xaml", "<Vue />\n");
        _repo.Write("docs/guide.md", "# Guide\n");
        await _repo.Git("add", "--all");
        await _repo.Git("commit", "-q", "-m", "Version 1");
        _repo.Write("src/app.cs", "ligne 1\nligne 2 modifiée\n");
        File.Delete(Path.Combine(_repo.Path, "docs", "guide.md"));
        File.WriteAllBytes(Path.Combine(_repo.Path, "logo.png"), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
        await _repo.Git("add", "--all");
        await _repo.Git("commit", "-q", "-m", "Version 2");
        return ProjectScanner.ReadProject(_repo.Path)!;
    }

    private static async Task<HistoryViewModel> LargeHistoryAsync(TestHost host, LocalProject project)
    {
        var history = new HistoryViewModel(host, withDetails: true);
        await history.LoadAsync(project);
        await TestHost.WaitUntil(() => history.Details?.Commit.Subject == "Version 2" && history.Diff.Count > 0);
        return history;
    }

    [Fact]
    public async Task Shows_the_whole_project_at_a_commit_with_lazy_folders_and_change_badges()
    {
        var host = new TestHost(realGit: true);
        var history = await LargeHistoryAsync(host, await ProjectAsync());
        Assert.Equal("Modifiés (3)", history.ChangedTabLabel);
        Assert.False(history.ShowAllFiles); // « Modifiés » par défaut

        history.ShowAllFiles = true;
        await TestHost.WaitUntil(() => history.Tree.Count > 0);
        Assert.Equal("Tous les fichiers (4)", history.AllTabLabel);
        Assert.Equal("TOUS LES FICHIERS (4)", history.FilesHeader);
        Assert.Equal(["src", "logo.png", "README.md"], history.Tree.Select(n => n.Label)); // dossiers d'abord, guide.md supprimé
        Assert.Equal("A", history.Tree.Single(n => n.Label == "logo.png").StatusLetter);

        var src = history.Tree[0];
        Assert.Equal("2 fichiers · 1 modifié", src.Detail);
        Assert.Equal("…", src.Children.Single().Label); // pas encore construit
        src.IsExpanded = true;
        Assert.Equal(["ui", "app.cs"], src.Children.Select(n => n.Label));
        Assert.True(src.Children[1] is { HasChange: true, StatusLetter: "M", Path: "src/app.cs" });

        Assert.True(new PanelSections(host).HistoryAllFiles); // choix mémorisé

        history.TreeFilter = "VUE";
        Assert.Equal(["src/ui/vue.xaml"], history.Tree.Select(n => n.Label)); // recherche à plat, chemin complet
        history.TreeFilter = "introuvable";
        Assert.Equal("Aucun fichier ne correspond.", history.TreeNote);
    }

    [Fact]
    public async Task A_modified_file_shows_its_changes_first_then_the_whole_file()
    {
        var history = await LargeHistoryAsync(new TestHost(realGit: true), await ProjectAsync());
        history.ShowAllFiles = true;
        await TestHost.WaitUntil(() => history.Tree.Count > 0);
        var src = history.Tree[0];
        src.IsExpanded = true;

        history.SelectedNode = src.Children.Single(n => n.Label == "app.cs");
        await TestHost.WaitUntil(() => history.ViewedPath == "src/app.cs" && history.Diff.Count > 0);
        Assert.True(history is { IsShowingChanges: true, CanShowWholeFile: true, CanShowChanges: false, CanRestoreFile: true });
        Assert.Equal("MODIFICATIONS · src/app.cs", history.DiffHeader);

        await history.ShowWholeFileCommand.ExecuteAsync(null);
        Assert.Equal([new FileLine(1, "ligne 1"), new FileLine(2, "ligne 2 modifiée")], history.ContentLines);
        Assert.True(history is { IsShowingContent: true, CanShowChanges: true, ContentNoteShown: false });
        Assert.Equal("CONTENU À CE COMMIT · src/app.cs", history.DiffHeader);

        history.ShowChangesCommand.Execute(null);
        Assert.True(history.IsShowingChanges);

        // Un fichier que ce commit n'a pas touché : son contenu directement.
        history.SelectedNode = history.Tree.Single(n => n.Label == "README.md");
        await TestHost.WaitUntil(() => history.ContentLines.Count > 0);
        Assert.Equal("# Projet", history.ContentLines.Single().Text);
        Assert.False(history.CanShowChanges);

        // Une image : aperçu.
        history.SelectedNode = history.Tree.Single(n => n.Label == "logo.png");
        await TestHost.WaitUntil(() => history.HasImage);
        Assert.Equal(FileViewMode.Content, history.ViewMode); // ajoutée par ce commit, mais le diff d'une image ne montre rien d'utile
    }

    [Fact]
    public async Task A_file_deleted_by_the_commit_cannot_be_read_saved_or_restored()
    {
        var history = await LargeHistoryAsync(new TestHost(realGit: true), await ProjectAsync());
        history.SelectedFile = history.Files.Single(f => f.Path == "docs/guide.md");
        await TestHost.WaitUntil(() => history.Diff.Count > 0);

        Assert.Equal("D", history.SelectedFile.StatusLetter);
        Assert.True(history is { ViewedFileExists: false, CanShowWholeFile: false, CanRestoreFile: false });
    }

    [Fact]
    public async Task Saves_an_old_version_where_the_user_chooses()
    {
        var host = new TestHost(realGit: true);
        var history = await LargeHistoryAsync(host, await ProjectAsync());
        history.SelectedRow = history.Rows.Single(r => r.Subject == "Version 1");
        await TestHost.WaitUntil(() => history.Details?.Commit.Subject == "Version 1" && history.ViewedPath is not null);
        history.SelectedFile = history.Files.Single(f => f.Path == "src/app.cs");

        host.SavePath = null; // annulé
        await history.SaveVersionCommand.ExecuteAsync(null);
        Assert.Null(history.FileStatus);

        host.SavePath = Path.Combine(_repo.Sub("copies"), "app v1.cs");
        await history.SaveVersionCommand.ExecuteAsync(null);
        Assert.Equal("ligne 1\nligne 2\n", File.ReadAllText(host.SavePath).Replace("\r\n", "\n"));
        Assert.StartsWith("Version du commit", history.FileStatus);
        Assert.Equal("ligne 1\nligne 2 modifiée\n", File.ReadAllText(Path.Combine(_repo.Path, "src", "app.cs")).Replace("\r\n", "\n")); // projet intact
    }

    [Fact]
    public async Task Restores_an_old_version_into_the_project_after_confirmation()
    {
        await ProjectAsync();
        var host = new TestHost(realGit: true);
        var project = new ProjectItemViewModel(ProjectClassifier.Place(ProjectScanner.ReadProject(_repo.Path)!, host.Accounts), host);
        var v1 = await _repo.Git("rev-parse", "HEAD~1");
        var file = Path.Combine(_repo.Path, "src", "app.cs");

        host.ConfirmAnswer = false;
        await project.RestoreFileAsync(v1, v1[..7], "hier", "src/app.cs");
        Assert.Contains("ligne 2 modifiée", File.ReadAllText(file));

        host.ConfirmAnswer = true;
        await project.RestoreFileAsync(v1, v1[..7], "hier", "src/app.cs");
        Assert.Contains("comme il était au commit", host.Confirms[^1]);
        Assert.True(project.LastAction!.Succeeded, project.LastAction.Message);
        Assert.Contains("git restore --source=", project.LastAction.Commands);
        Assert.DoesNotContain("modifiée", File.ReadAllText(file));

        // Le fichier a maintenant un changement pas commité : une nouvelle restauration est refusée, rien n'est perdu.
        File.WriteAllText(file, "travail en cours");
        var confirms = host.Confirms.Count;
        await project.RestoreFileAsync(v1, v1[..7], "hier", "src/app.cs");
        Assert.False(project.LastAction!.Succeeded);
        Assert.Contains("pas encore commités", project.LastAction.Message);
        Assert.Equal(confirms, host.Confirms.Count);
        Assert.Equal("travail en cours", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_github_repository_not_cloned_shows_its_files_read_only()
    {
        var bare = await _repo.BareAsync("stock.git");
        await ProjectAsync();
        await _repo.Git("push", "-q", bare, "main");
        var host = new TestHost(realGit: true);
        var stock = new GitHubRepository("stock", "SmartDev-Web", bare[..^4], "", RepositoryVisibility.Private);
        var history = new HistoryViewModel(host, withDetails: true) { ShowAllFiles = true };

        await history.ShowRepositoryAsync("stock", null, null, TestHost.Pro, stock, defaultBranch: "main");
        await TestHost.WaitUntil(() => history.Tree.Count > 0);
        history.SelectedNode = history.Tree.Single(n => n.Label == "README.md");
        await TestHost.WaitUntil(() => history.ContentLines.Count > 0); // contenu téléchargé à la demande

        Assert.Equal("# Projet", history.ContentLines.Single().Text);
        Assert.True(history is { ViewedFileExists: true, CanRestoreFile: false }); // lecture seule : on peut enregistrer, pas restaurer
    }

    [Fact]
    public async Task Search_dims_other_commits_and_jumps_between_results()
    {
        var history = await LargeHistoryAsync(new TestHost(realGit: true), await ProjectAsync());
        var v1 = history.Rows.Single(r => r.Subject == "Version 1");

        history.SearchText = "version";
        Assert.Equal("2 résultats", history.SearchSummary);
        Assert.Equal("Version 2", history.SelectedRow!.Subject);
        Assert.True(history.Rows.Single(r => r.Subject == "Premier commit").IsDimmed);
        history.NextMatchCommand.Execute(null);
        Assert.Same(v1, history.SelectedRow);
        history.NextMatchCommand.Execute(null);
        Assert.Equal("Version 2", history.SelectedRow!.Subject); // on reboucle

        history.SearchText = v1.Commit.Sha[..6]; // un SHA abrégé
        Assert.Same(v1, history.SelectedRow);
        history.SearchText = "main"; // une branche
        Assert.Equal("Version 2", history.SelectedRow!.Subject);
        history.SearchText = "rien de tel";
        Assert.Equal("Aucun résultat.", history.SearchSummary);

        history.ClearSearchCommand.Execute(null);
        Assert.Null(history.SearchSummary);
        Assert.DoesNotContain(history.Rows, r => r.IsDimmed);
    }

    public void Dispose() => _repo.Dispose();
}
