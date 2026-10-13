using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Processes;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.GitHub;

/// <summary>F4 : gestion des repos GitHub (commandes gh) et cohérence des projets locaux liés.</summary>
public class RepositoryManagementTests
{
    private static readonly GitHubAccount Pro = new("SmartDev-Web", "github.com", true, true) { Scopes = "gist, read:org, repo, workflow" };

    private static (GitHubService Service, FakeProcessRunner Runner) Create()
    {
        var runner = new FakeProcessRunner().When(["auth", "token"], "gho_tokenDeSmartDevWeb_0123456789");
        var gh = new GhCli(runner);
        return (new GitHubService(gh, new AccountService(gh)), runner);
    }

    [Fact]
    public void Parses_repository_details_with_or_without_topics()
    {
        var details = GitHubService.ParseDetails("""
            {"createdAt":"2026-10-13T10:50:33Z","defaultBranchRef":{"name":"main"},"description":"Gestion de stock","diskUsage":4,
             "forkCount":2,"homepageUrl":"https://exemple.fr","isArchived":true,"isFork":true,"name":"stock","owner":{"login":"SmartDev-Web"},
             "parent":{"name":"origine","owner":{"login":"autre"}},"pushedAt":"2026-10-13T11:02:25Z",
             "repositoryTopics":[{"name":"php"},{"name":"prestashop"}],"sshUrl":"git@github.com:SmartDev-Web/stock.git",
             "stargazerCount":5,"url":"https://github.com/SmartDev-Web/stock","viewerPermission":"ADMIN","visibility":"PRIVATE"}
            """);

        Assert.Equal("SmartDev-Web/stock", details.FullName);
        Assert.Equal(RepositoryVisibility.Private, details.Visibility);
        Assert.Equal(["php", "prestashop"], details.Topics);
        Assert.Equal(("main", "autre/origine"), (details.DefaultBranch, details.Parent));
        Assert.True(details is { IsArchived: true, IsFork: true, IsAdmin: true, Stars: 5, Forks: 2, Homepage: "https://exemple.fr" });

        var empty = GitHubService.ParseDetails("""
            {"name":"vide","owner":{"login":"x"},"repositoryTopics":null,"description":"","homepageUrl":"","parent":null,"visibility":"PUBLIC","defaultBranchRef":null}
            """);
        Assert.Empty(empty.Topics);
        Assert.Null(empty.Description);
        Assert.Null(empty.DefaultBranch);
    }

    [Fact]
    public async Task Every_management_command_uses_the_owner_token_and_never_prompts()
    {
        var (service, runner) = Create();
        runner.When(["repo"], "").When(["api"], "{}");

        await service.RenameRepositoryAsync(Pro, "SmartDev-Web/ancien", "nouveau");
        await service.SetVisibilityAsync(Pro, "SmartDev-Web/x", RepositoryVisibility.Public);
        await service.SetArchivedAsync(Pro, "SmartDev-Web/x", archived: true);
        await service.SetArchivedAsync(Pro, "SmartDev-Web/x", archived: false);
        await service.RenameBranchAsync(Pro, "SmartDev-Web/x", "master", "main");

        var commands = runner.Requests.Where(r => r.Arguments[0] != "auth").Select(r => string.Join(' ', r.Arguments)).ToList();
        Assert.Equal(
        [
            "repo rename nouveau --repo SmartDev-Web/ancien --yes",
            "repo edit SmartDev-Web/x --visibility public --accept-visibility-change-consequences",
            "repo archive SmartDev-Web/x --yes",
            "repo unarchive SmartDev-Web/x --yes",
            "api --method POST repos/SmartDev-Web/x/branches/master/rename -f new_name=main",
        ], commands);
        Assert.All(runner.Requests.Where(r => r.Arguments[0] != "auth"), r => Assert.Equal("gho_tokenDeSmartDevWeb_0123456789", r.Environment!["GH_TOKEN"]));
    }

    [Fact]
    public async Task Deleting_requires_the_delete_repo_scope()
    {
        var (service, runner) = Create();
        runner.When(["repo", "delete"], "");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteRepositoryAsync(Pro, "SmartDev-Web/x"));
        Assert.DoesNotContain(runner.Requests, r => r.Arguments[0] == "repo");

        await service.DeleteRepositoryAsync(Pro with { Scopes = "repo, delete_repo" }, "SmartDev-Web/x");
        Assert.Equal(["repo", "delete", "SmartDev-Web/x", "--yes"], runner.Single("repo", "delete").Arguments);
    }

    [Fact]
    public void Edit_only_sends_what_changed()
    {
        Assert.Equal(["repo", "edit", "o/r", "--description", "", "--add-topic", "php,web", "--remove-topic", "old"],
            GitHubService.BuildEditArguments("o/r", "", null, ["php", "web"], ["old"]));
        Assert.Equal(["repo", "edit", "o/r"], GitHubService.BuildEditArguments("o/r", null, null, [], []));
    }

    [Theory]
    [InlineData("https://github.com/SmartDev-Web/ancien.git", "https://github.com/SamuelnitraM/nouveau.git")]
    [InlineData("https://github.com/SmartDev-Web/ancien", "https://github.com/SamuelnitraM/nouveau")]
    [InlineData("https://SmartDev-Web@github.com/SmartDev-Web/ancien.git", "https://SmartDev-Web@github.com/SamuelnitraM/nouveau.git")]
    [InlineData("git@github.com:SmartDev-Web/ancien.git", "git@github.com:SamuelnitraM/nouveau.git")]
    [InlineData("git@github-perso:SmartDev-Web/ancien.git", "git@github-perso:SamuelnitraM/nouveau.git")]
    [InlineData("ssh://git@github.com/SmartDev-Web/ancien.git", "ssh://git@github.com/SamuelnitraM/nouveau.git")]
    public void Rewrites_remote_urls_keeping_their_form(string url, string expected) =>
        Assert.Equal(expected, RemoteUrlRewriter.Rewrite(url, "SamuelnitraM", "nouveau"));

    [Fact]
    public void Topics_are_normalised_and_checked()
    {
        var topics = RepositoryTopics.Parse("Gestion de stock, PHP ; php\nprestashop");
        Assert.Equal(["gestion-de-stock", "php", "prestashop"], topics);
        Assert.Null(RepositoryTopics.Validate(topics));
        Assert.Contains("« écran »", RepositoryTopics.Validate(RepositoryTopics.Parse("écran")));
        Assert.Contains("trop long", RepositoryTopics.Validate([new string('a', 51)]));
        Assert.Contains("20 sujets maximum", RepositoryTopics.Validate(Enumerable.Range(0, 21).Select(i => $"t{i}").ToList()));
    }

    [Fact]
    public void Finds_the_local_projects_linked_to_a_repository()
    {
        LocalProject P(string path, string url) => new(path) { Remote = RemoteUrlParser.Parse(url), RemoteName = "origin" };
        var projects = new[]
        {
            P(@"C:\a", "https://github.com/SmartDev-Web/Stock.git"),
            P(@"C:\b", "git@github.com:smartdev-web/stock.git"),
            P(@"C:\c", "https://github.com/SmartDev-Web/autre.git"),
            new LocalProject(@"C:\d"),
        };

        Assert.Equal([@"C:\a", @"C:\b"], LinkedProjects.Find(projects, "github.com", "SmartDev-Web/stock").Select(p => p.Path));
    }
}

/// <summary>Clonage et suivi des changements faits sur GitHub, contre de vrais dépôts.</summary>
public sealed class LinkedProjectsTests : IDisposable
{
    private readonly TempRepo _repo = new("liens");
    private readonly GitService _git = new(new ProcessRunner());
    private readonly LinkedProjects _linked;
    private static readonly IReadOnlyDictionary<string, string> NoAuth = GitCredentialEnvironment.NonInteractive();

    public LinkedProjectsTests() =>
        _linked = new LinkedProjects(_git, new GitCredentialResolver(new AccountService(new GhCli(new FakeProcessRunner()))));

    [Fact]
    public async Task Clones_into_the_chosen_folder()
    {
        var bare = await _repo.BareAsync();
        await _repo.WithCommitAsync();
        await _repo.Git("push", "-q", bare, "main");
        var parent = _repo.Sub("mes projets");

        var result = await _git.CloneAsync(parent, bare, "Copie clonée", NoAuth);

        Assert.True(result.Succeeded, result.Output);
        var clone = Path.Combine(parent, "Copie clonée");
        Assert.True(File.Exists(Path.Combine(clone, "README.md")));
        Assert.Equal("origin/main", (await _git.GetStatusAsync(clone)).Upstream);
    }

    [Fact]
    public async Task A_renamed_or_transferred_repository_is_followed_by_the_local_link()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("remote", "add", "origin", "https://github.com/SmartDev-Web/ancien.git");
        var project = ProjectScanner.ReadProject(_repo.Path)!;

        Assert.True((await _linked.FollowAsync(project, "SamuelnitraM", "nouveau")).Succeeded);

        Assert.Equal("https://github.com/SamuelnitraM/nouveau.git", (await _git.GetRemotesAsync(_repo.Path)).Single().Url);
    }

    [Fact]
    public async Task A_deleted_repository_leaves_no_dead_link()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("remote", "add", "origin", "https://github.com/SmartDev-Web/supprime.git");

        Assert.True((await _linked.UnlinkAsync(ProjectScanner.ReadProject(_repo.Path)!)).Succeeded);

        Assert.Empty(await _git.GetRemotesAsync(_repo.Path));
        Assert.True(File.Exists(Path.Combine(_repo.Path, "README.md")));
    }

    [Fact]
    public async Task Follows_a_default_branch_renamed_on_github()
    {
        // « GitHub » : dépôt nu dont la branche master devient main (comme l'API de renommage)
        var bare = await _repo.BareAsync();
        await _repo.Git("init", "-q", "-b", "master");
        await _repo.Git("config", "user.name", "T");
        await _repo.Git("config", "user.email", "t@t");
        _repo.Write("a.txt", "a");
        await _repo.Git("add", ".");
        await _repo.Git("commit", "-q", "-m", "init");
        await _repo.Git("remote", "add", "origin", bare);
        await _repo.Git("push", "-q", "-u", "origin", "master");
        await _repo.GitIn(bare, "branch", "-m", "master", "main");
        await _repo.GitIn(bare, "symbolic-ref", "HEAD", "refs/heads/main");

        var steps = await _linked.FollowBranchRenameAsync(ProjectScanner.ReadProject(_repo.Path)!, null, "master", "main");

        Assert.All(steps, s => Assert.True(s.Succeeded, $"{s.Command} : {s.Output}"));
        var status = await _git.GetStatusAsync(_repo.Path);
        Assert.Equal(("main", "origin/main"), (status.Branch, status.Upstream));
        Assert.Equal("main", await _git.GetDefaultBranchAsync(_repo.Path));
        Assert.DoesNotContain(await _git.ListBranchesAsync(_repo.Path), b => b.Name == "master"); // origin/master élagué par le fetch
    }

    public void Dispose() => _repo.Dispose();
}
