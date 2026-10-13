using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Accounts;

public class AccountServiceTests
{
    private const string TwoAccounts = """
        {"hosts":{"github.com":[
          {"state":"success","active":true,"host":"github.com","login":"SmartDev-Web","tokenSource":"keyring","scopes":"gist, read:org, repo, workflow","gitProtocol":"https"},
          {"state":"success","active":false,"host":"github.com","login":"SamuelnitraM","tokenSource":"keyring","scopes":"gist, read:org, repo, workflow","gitProtocol":"ssh"}
        ]}}
        """;

    private static (AccountService Service, FakeProcessRunner Runner) Create()
    {
        var runner = new FakeProcessRunner();
        return (new AccountService(new GhCli(runner)), runner);
    }

    [Fact]
    public async Task Detects_every_connected_account_and_the_active_one()
    {
        var (service, runner) = Create();
        runner.When(["auth", "status"], TwoAccounts);

        var accounts = await service.GetAccountsAsync();

        Assert.Equal(["SmartDev-Web", "SamuelnitraM"], accounts.Select(a => a.Login));
        Assert.True(accounts[0].IsActive);
        Assert.False(accounts[1].IsActive);
        Assert.All(accounts, a => Assert.True(a.IsAuthenticated));
        Assert.Equal("ssh", accounts[1].GitProtocol);
        Assert.Equal(["auth", "status", "--json", "hosts"], runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task Keeps_accounts_whose_token_is_invalid_but_flags_them()
    {
        var (service, runner) = Create();
        // gh sort en code 1 dans ce cas, mais le JSON reste exploitable.
        runner.When(["auth", "status"], exitCode: 1, stdout: """
            {"hosts":{"github.com":[{"state":"error","error":"token expired","active":true,"host":"github.com","login":"Old"}]}}
            """);

        var account = Assert.Single(await service.GetAccountsAsync());

        Assert.False(account.IsAuthenticated);
        Assert.Equal("token expired", account.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("You are not logged into any GitHub hosts. To log in, run: gh auth login")]
    [InlineData("""{"hosts":{}}""")]
    public async Task Returns_no_account_when_nobody_is_logged_in(string stdout)
    {
        var (service, runner) = Create();
        runner.When(["auth", "status"], stdout, exitCode: 1);

        Assert.Empty(await service.GetAccountsAsync());
    }

    [Fact]
    public async Task Gets_the_token_of_the_requested_account_not_the_active_one()
    {
        var (service, runner) = Create();
        runner.When(["auth", "token"], "gho_secretSecretSecretSecret1234\n");
        var inactive = new GitHubAccount("SamuelnitraM", "github.com", IsActive: false, IsAuthenticated: true);

        var token = await service.GetTokenAsync(inactive);

        Assert.Equal("gho_secretSecretSecretSecret1234", token);
        Assert.Equal(["auth", "token", "--hostname", "github.com", "--user", "SamuelnitraM"], runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task Token_failure_raises_an_error_without_leaking_secrets()
    {
        var (service, runner) = Create();
        runner.When(["auth", "token"], "", exitCode: 1, stderr: "no token for gho_abcdefghijklmnopqrstuvwxyz0123");
        var account = new GitHubAccount("Ghost", "github.com", false, true);

        var error = await Assert.ThrowsAsync<GhCommandException>(() => service.GetTokenAsync(account));

        Assert.DoesNotContain("gho_abcdef", error.Message);
    }

    [Fact]
    public async Task Gh_is_always_run_without_prompts()
    {
        var (service, runner) = Create();
        runner.When(["auth", "status"], TwoAccounts);

        await service.GetAccountsAsync();

        Assert.Equal("1", runner.Requests[0].Environment!["GH_PROMPT_DISABLED"]);
    }
}
