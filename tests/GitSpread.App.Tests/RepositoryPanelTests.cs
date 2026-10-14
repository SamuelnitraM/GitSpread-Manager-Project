using GitSpread.App.ViewModels;
using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;
using GitSpread.Core.Processes;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitSpread.App.Tests;

/// <summary>F4 : panneau d'un repo GitHub et ses actions.</summary>
public sealed class RepositoryPanelTests : IDisposable
{
    private const string Details = """
        {"name":"stock","owner":{"login":"SmartDev-Web"},"description":"Gestion de stock","visibility":"PRIVATE",
         "defaultBranchRef":{"name":"master"},"repositoryTopics":[{"name":"php"}],"homepageUrl":"","isArchived":false,"isFork":false,
         "parent":null,"pushedAt":"2026-10-13T11:02:25Z","createdAt":"2026-10-01T10:00:00Z","url":"https://github.com/SmartDev-Web/stock",
         "sshUrl":"git@github.com:SmartDev-Web/stock.git","diskUsage":2048,"stargazerCount":1,"forkCount":0,"viewerPermission":"ADMIN"}
        """;

    private static readonly GitHubRepository Stock = new("stock", "SmartDev-Web", "https://github.com/SmartDev-Web/stock", "git@github.com:SmartDev-Web/stock.git", RepositoryVisibility.Private);
    private readonly TempRepo _local = new("panneau repo");

    private async Task<(TestHost Host, GitHubRepositoryViewModel Panel)> PanelAsync(GitHubAccount? account = null, bool withLocal = false)
    {
        var host = account is null ? new TestHost(realGit: true) : new TestHost(true, account, TestHost.Perso);
        host.Runner.When(["repo", "view", "SmartDev-Web/stock"], Details).When(["repo", "rename"], "").When(["repo", "delete"], "");
        if (withLocal)
        {
            await _local.WithCommitAsync();
            await _local.Git("remote", "add", "origin", "https://github.com/SmartDev-Web/stock.git");
            host.LocalProjects = [ProjectScanner.ReadProject(_local.Path)!];
        }
        var tab = new AccountTabViewModel(account ?? TestHost.Pro, host, NullLogger.Instance);
        var panel = new GitHubRepositoryViewModel(tab, Stock, host, NullLogger.Instance);
        await panel.LoadAsync();
        return (host, panel);
    }

    [Fact]
    public async Task Shows_details_and_the_local_projects_linked_to_the_repository()
    {
        var (_, panel) = await PanelAsync(withLocal: true);

        Assert.True(panel is { HasDetails: true, CanAdminister: true, IsPrivate: true, VisibilityLabel: "Privé", ToggleVisibilityLabel: "Rendre public…" });
        Assert.Equal(["php"], panel.Topics);
        Assert.Contains("branche principale « master »", panel.FactsLabel);
        Assert.Contains("2 Mo", panel.FactsLabel);
        Assert.Equal("1 projet local lié :", panel.LinkedLabel);
    }

    [Fact]
    public async Task Renaming_updates_github_then_the_linked_local_project()
    {
        var (host, panel) = await PanelAsync(withLocal: true);
        host.PromptAnswers.Enqueue("stock-v2");

        await panel.RenameCommand.ExecuteAsync(null);

        Assert.Equal(["repo", "rename", "stock-v2", "--repo", "SmartDev-Web/stock", "--yes"], host.Runner.Single("repo", "rename").Arguments);
        Assert.Equal("https://github.com/SmartDev-Web/stock-v2.git", await _local.Git("remote", "get-url", "origin"));
        Assert.True(panel.LastAction!.Succeeded, panel.LastAction.Message);
        Assert.Contains("Projets locaux liés mis à jour", panel.LastAction.Message);
    }

    [Fact]
    public async Task An_invalid_new_name_is_refused_by_the_dialog()
    {
        var (host, panel) = await PanelAsync();
        host.PromptAnswers.Enqueue("stock");      // nom actuel
        await panel.RenameCommand.ExecuteAsync(null);
        host.PromptAnswers.Enqueue("stock é");
        await panel.RenameCommand.ExecuteAsync(null);

        Assert.Equal("C'est le nom actuel.", host.PromptErrors[0]);
        Assert.Contains("non autorisé", host.PromptErrors[1]);
        Assert.DoesNotContain(host.Runner.Requests, r => r.Arguments.Take(2).SequenceEqual(["repo", "rename"]));
    }

    [Fact]
    public async Task Deleting_without_the_scope_does_nothing_on_github()
    {
        var (host, panel) = await PanelAsync();
        host.PromptAnswers.Enqueue("SmartDev-Web/stock");

        await panel.DeleteCommand.ExecuteAsync(null); // EnsureScopeAsync : refus (pas de delete_repo)

        Assert.DoesNotContain(host.Runner.Requests, r => r.Arguments.Take(2).SequenceEqual(["repo", "delete"]));
        // Jamais d'arrêt silencieux : le compte rendu dit pourquoi rien n'a été supprimé.
        Assert.False(panel.LastAction!.Succeeded);
        Assert.Contains("Rien n'a été supprimé", panel.LastAction.Message);
    }

    [Fact]
    public async Task Deleting_requires_typing_the_full_name_and_unlinks_local_projects()
    {
        var admin = TestHost.Pro with { Scopes = "repo, delete_repo" };
        var (host, panel) = await PanelAsync(admin, withLocal: true);

        host.PromptAnswers.Enqueue("stock"); // texte incorrect : la fenêtre refuse
        await panel.DeleteCommand.ExecuteAsync(null);
        Assert.Contains("Tape exactement SmartDev-Web/stock.", host.PromptErrors);
        Assert.DoesNotContain(host.Runner.Requests, r => r.Arguments.Take(2).SequenceEqual(["repo", "delete"]));

        host.PromptAnswers.Enqueue("SmartDev-Web/stock"); // TestHost.Confirm : oui pour retirer le lien local
        await panel.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(["repo", "delete", "SmartDev-Web/stock", "--yes"], host.Runner.Single("repo", "delete").Arguments);
        Assert.True(panel.LastAction!.Succeeded, panel.LastAction.Message);
        Assert.StartsWith("SmartDev-Web/stock a été supprimé de GitHub.", panel.Tab.Notice); // reste affiché une fois le panneau fermé
        Assert.Equal("", await _local.Git("remote")); // lien mort retiré…
        Assert.True(File.Exists(Path.Combine(_local.Path, "README.md"))); // …fichiers intacts
    }

    public void Dispose() => _local.Dispose();
}

public class EditRepositoryTests
{
    private static readonly GitHubRepositoryDetails Details = new("SmartDev-Web", "stock", RepositoryVisibility.Private)
    {
        Description = "Ancienne description",
        Topics = ["php", "obsolete"],
    };

    [Fact]
    public async Task Only_changes_are_sent_to_github()
    {
        var host = new TestHost();
        host.Runner.When(["repo", "edit"], "");
        var vm = new EditRepositoryViewModel(TestHost.Pro, Details, host);

        vm.TopicsText = "php, Gestion de stock";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.Saved);
        Assert.Equal(["repo", "edit", "SmartDev-Web/stock", "--add-topic", "gestion-de-stock", "--remove-topic", "obsolete"],
            host.Runner.Single("repo", "edit").Arguments);
    }

    [Fact]
    public async Task Invalid_homepage_and_topics_stay_in_the_form()
    {
        var host = new TestHost();
        var vm = new EditRepositoryViewModel(TestHost.Pro, Details, host);
        var shown = 0;
        vm.ShowErrorsRequested += (_, _) => shown++;

        vm.Homepage = "monsite.fr";
        vm.TopicsText = "écran";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.Saved);
        Assert.Contains("https://", vm.HomepageError);
        Assert.Contains("« écran »", vm.TopicsError);
        Assert.Equal(1, shown);
    }
}

public sealed class CloneTests : IDisposable
{
    private readonly TempRepo _repo = new("clone app");

    [Fact]
    public async Task Clones_signs_with_the_account_and_adds_the_project_outside_scanned_folders()
    {
        var bare = await _repo.BareAsync("stock.git");
        await _repo.WithCommitAsync();
        await _repo.Git("push", "-q", bare, "main");
        var host = new TestHost(realGit: true);
        // URL « GitHub » = dépôt nu local (le ViewModel ajoute .git)
        var repo = new GitHubRepository("stock", "SmartDev-Web", bare[..^4], "", RepositoryVisibility.Private);
        var vm = new CloneViewModel(TestHost.Pro, repo, host) { ParentFolder = _repo.Sub("ailleurs") };

        await vm.CloneCommand.ExecuteAsync(null);

        Assert.Equal(CloneStep.Done, vm.Step);
        Assert.True(File.Exists(Path.Combine(vm.ClonedPath!, "README.md")));
        Assert.Contains(vm.ClonedPath, host.Settings.ExtraProjects);
        var email = await new ProcessRunner().RunAsync(new ProcessRequest("git", ["config", "--local", "user.email"]) { WorkingDirectory = vm.ClonedPath! });
        Assert.Equal("1001+SmartDev-Web@users.noreply.github.com", email.StandardOutput.Trim());
    }

    [Fact]
    public async Task Refuses_a_non_empty_target_folder()
    {
        var host = new TestHost();
        var parent = _repo.Sub("parent");
        Directory.CreateDirectory(Path.Combine(parent, "stock"));
        File.WriteAllText(Path.Combine(parent, "stock", "fichier.txt"), "x");
        var vm = new CloneViewModel(TestHost.Pro, new GitHubRepository("stock", "SmartDev-Web", "https://github.com/SmartDev-Web/stock", "", RepositoryVisibility.Private), host)
            { ParentFolder = parent };

        await vm.CloneCommand.ExecuteAsync(null);

        Assert.Equal(CloneStep.Form, vm.Step);
        Assert.Contains("n'est pas vide", vm.FolderNameError);
    }

    public void Dispose() => _repo.Dispose();
}
