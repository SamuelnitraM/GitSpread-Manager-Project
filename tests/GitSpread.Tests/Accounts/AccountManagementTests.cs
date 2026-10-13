using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;
using GitSpread.Core.Processes;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Accounts;

/// <summary>F5 : autorisations des comptes et déconnexion.</summary>
public class AccountManagementTests
{
    private const string Status = """
        {"hosts":{"github.com":[
          {"state":"success","active":true,"host":"github.com","login":"SmartDev-Web","scopes":"gist, read:org, repo, workflow"},
          {"state":"success","active":false,"host":"github.com","login":"SamuelnitraM","scopes":"gist, read:org, repo, workflow, delete_repo"}]}}
        """;

    private static readonly GitHubAccount Perso = new("SamuelnitraM", "github.com", false, true) { Scopes = "gist, read:org, repo, workflow" };
    private static readonly GitHubAccount Pro = new("SmartDev-Web", "github.com", true, true) { Scopes = "gist, read:org, repo, workflow" };

    [Theory]
    [InlineData("repo", true)]
    [InlineData("REPO", true)]
    [InlineData("delete_repo", false)]
    [InlineData("public_repo", true)]        // couvert par repo
    [InlineData("read:public_key", false)]
    public void Knows_which_scopes_are_granted(string scope, bool expected) =>
        Assert.Equal(expected, Pro.HasScope(scope));

    [Fact]
    public void Admin_public_key_covers_the_narrower_key_scopes()
    {
        var account = Pro with { Scopes = "repo, admin:public_key" };
        Assert.True(account.HasScope("write:public_key"));
        Assert.True(account.HasScope("read:public_key"));
    }

    [Fact]
    public void Builds_refresh_arguments_for_adding_and_removing()
    {
        Assert.Equal(["auth", "refresh", "--hostname", "github.com", "--scopes", "delete_repo,admin:public_key", "--remove-scopes", "workflow"],
            AccountService.BuildScopeArguments("github.com", ["delete_repo", "admin:public_key"], ["workflow"]));
        Assert.Equal(["auth", "refresh", "--hostname", "github.com", "--scopes", "delete_repo"],
            AccountService.BuildScopeArguments("github.com", ["delete_repo"], []));
    }

    [Fact]
    public async Task An_inactive_account_is_made_active_for_the_refresh_then_the_previous_one_comes_back()
    {
        var runner = new FakeProcessRunner()
            .When(["auth", "status"], Status)
            .When(["auth", "switch"], "")
            .When(["auth", "refresh"], "", stderr: "! One-time code (AB12-CD34) copied to clipboard\nOpen this URL to continue in your web browser: https://github.com/login/device\n");
        DeviceCode? code = null;

        var updated = await new AccountService(new GhCli(runner)).ChangeScopesAsync(Perso, ["delete_repo"], [], c => code = c);

        var sequence = runner.Requests.Select(r => string.Join(' ', r.Arguments.Take(2)) + (r.Arguments.Contains("--user") ? " " + r.Arguments[^1] : "")).ToList();
        Assert.Equal(["auth status", "auth switch SamuelnitraM", "auth refresh", "auth switch SmartDev-Web", "auth status"], sequence);
        Assert.Equal("AB12-CD34", code?.Code);
        Assert.True(updated.HasScope("delete_repo"));
        Assert.Equal(LoginService.BrowserLoginTimeout, runner.Single("auth", "refresh").Timeout);
    }

    [Fact]
    public async Task The_previous_account_comes_back_even_when_the_refresh_fails()
    {
        var runner = new FakeProcessRunner()
            .When(["auth", "status"], Status)
            .When(["auth", "switch"], "")
            .When(["auth", "refresh"], "", exitCode: 1, stderr: "error refreshing credentials for SamuelnitraM, received credentials for SmartDev-Web, did you use the correct account in the browser?");

        var error = await Assert.ThrowsAsync<GhCommandException>(() =>
            new AccountService(new GhCli(runner)).ChangeScopesAsync(Perso, ["delete_repo"], []));

        Assert.Contains("did you use the correct account", error.Message);
        Assert.Equal(["SmartDev-Web"], runner.Requests.Where(r => r.Arguments[1] == "switch").Skip(1).Select(r => r.Arguments[^1]));
    }

    [Fact]
    public async Task The_active_account_is_refreshed_without_switching()
    {
        var runner = new FakeProcessRunner().When(["auth", "status"], Status).When(["auth", "refresh"], "");

        await new AccountService(new GhCli(runner)).ChangeScopesAsync(Pro, ["delete_repo"], []);

        Assert.DoesNotContain(runner.Requests, r => r.Arguments[1] == "switch");
    }

    [Fact]
    public async Task Nothing_to_change_means_no_command()
    {
        var runner = new FakeProcessRunner();
        await new AccountService(new GhCli(runner)).ChangeScopesAsync(Pro, [], []);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Logout_targets_the_exact_account()
    {
        var runner = new FakeProcessRunner().When(["auth", "logout"], "");

        await new AccountService(new GhCli(runner)).LogoutAsync(Perso);

        Assert.Equal(["auth", "logout", "--hostname", "github.com", "--user", "SamuelnitraM"], runner.Requests[0].Arguments);
    }
}
