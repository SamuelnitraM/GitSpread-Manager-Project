using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.GitHub;

public class GitHubServiceTests
{
    private const string Repos = """
        [
          {"name":"SmartStock","owner":{"id":"1","login":"SamuelnitraM"},"url":"https://github.com/SamuelnitraM/SmartStock",
           "sshUrl":"git@github.com:SamuelnitraM/SmartStock.git","visibility":"PRIVATE","description":"Gestion de stock",
           "isArchived":false,"isFork":false,"pushedAt":"2026-09-01T10:00:00Z"},
          {"name":"old-fork","owner":{"id":"1","login":"SamuelnitraM"},"url":"https://github.com/SamuelnitraM/old-fork",
           "sshUrl":"git@github.com:SamuelnitraM/old-fork.git","visibility":"PUBLIC","description":"",
           "isArchived":true,"isFork":true,"pushedAt":null}
        ]
        """;

    private static readonly GitHubAccount Inactive = new("SamuelnitraM", "github.com", IsActive: false, IsAuthenticated: true);

    private static (GitHubService Service, FakeProcessRunner Runner) Create()
    {
        var runner = new FakeProcessRunner()
            .When(["auth", "token"], "gho_tokenOfSamuelnitraM_0123456789")
            .When(["repo", "list"], Repos);
        var gh = new GhCli(runner);
        return (new GitHubService(gh, new AccountService(gh)), runner);
    }

    [Fact]
    public async Task Lists_repos_with_the_token_of_the_account_even_when_it_is_not_active()
    {
        var (service, runner) = Create();

        await service.ListRepositoriesAsync(Inactive);

        var list = runner.Single("repo", "list");
        Assert.Equal("SamuelnitraM", list.Arguments[2]);
        Assert.Equal("gho_tokenOfSamuelnitraM_0123456789", list.Environment!["GH_TOKEN"]);
        Assert.Contains("200", list.Arguments);
    }

    [Fact]
    public async Task Maps_every_repo_field()
    {
        var (service, _) = Create();

        var repos = await service.ListRepositoriesAsync(Inactive);

        Assert.Equal(2, repos.Count);
        var stock = repos[0];
        Assert.Equal("SamuelnitraM/SmartStock", stock.FullName);
        Assert.Equal(RepositoryVisibility.Private, stock.Visibility);
        Assert.Equal("git@github.com:SamuelnitraM/SmartStock.git", stock.SshUrl);
        Assert.Equal("Gestion de stock", stock.Description);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), stock.PushedAt);

        var fork = repos[1];
        Assert.True(fork.IsArchived);
        Assert.True(fork.IsFork);
        Assert.Null(fork.PushedAt);
        Assert.Null(fork.Description);
    }

    [Fact]
    public async Task Uses_the_enterprise_token_variable_for_other_hosts()
    {
        var (service, runner) = Create();

        await service.ListRepositoriesAsync(Inactive with { Host = "ghe.example.com" });

        var env = runner.Single("repo", "list").Environment!;
        Assert.False(env.ContainsKey("GH_TOKEN"));
        Assert.Equal("ghe.example.com", env["GH_HOST"]);
        Assert.Equal("gho_tokenOfSamuelnitraM_0123456789", env["GH_ENTERPRISE_TOKEN"]);
    }

    [Fact]
    public async Task Surfaces_gh_errors()
    {
        var runner = new FakeProcessRunner()
            .When(["auth", "token"], "gho_tokenOfSamuelnitraM_0123456789")
            .When(["repo", "list"], "", exitCode: 1, stderr: "HTTP 401: Bad credentials");
        var gh = new GhCli(runner);

        var error = await Assert.ThrowsAsync<GhCommandException>(
            () => new GitHubService(gh, new AccountService(gh)).ListRepositoriesAsync(Inactive));

        Assert.Contains("Bad credentials", error.Message);
    }
}
