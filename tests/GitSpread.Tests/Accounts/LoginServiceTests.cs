using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Accounts;

public class LoginServiceTests
{
    private const string OneAccount = """{"hosts":{"github.com":[{"state":"success","active":true,"host":"github.com","login":"SmartDev-Web"}]}}""";
    private const string TwoAccounts = """
        {"hosts":{"github.com":[
          {"state":"success","active":true,"host":"github.com","login":"NewOne"},
          {"state":"success","active":false,"host":"github.com","login":"SmartDev-Web"}]}}
        """;

    /// <summary>gh auth status renvoie un compte avant la connexion, deux après.</summary>
    private sealed class SequencedRunner(string loginStderr, int loginExit = 0) : GitSpread.Core.Processes.IProcessRunner
    {
        private int _statusCalls;
        public List<GitSpread.Core.Processes.ProcessRequest> Requests { get; } = [];

        public Task<GitSpread.Core.Processes.ProcessResult> RunAsync(GitSpread.Core.Processes.ProcessRequest r, CancellationToken ct = default)
        {
            Requests.Add(r);
            if (r.Arguments[1] == "status")
                return Task.FromResult(new GitSpread.Core.Processes.ProcessResult(0, _statusCalls++ == 0 ? OneAccount : TwoAccounts, ""));
            r.OutputReceived?.Invoke(loginStderr);
            return Task.FromResult(new GitSpread.Core.Processes.ProcessResult(loginExit, "", loginStderr));
        }
    }

    private static LoginService Create(GitSpread.Core.Processes.IProcessRunner runner)
    {
        var gh = new GhCli(runner);
        return new LoginService(gh, new AccountService(gh));
    }

    [Fact]
    public void Browser_login_maps_every_answer_to_a_flag()
    {
        var args = LoginService.BuildArguments(new LoginRequest("github.com", GitProtocol.Ssh) { RequestSshKeyScope = true });

        Assert.Equal(["auth", "login", "--hostname", "github.com", "--git-protocol", "ssh", "--web", "--skip-ssh-key",
            "--scopes", "admin:public_key"], args);
    }

    [Fact]
    public void Token_login_reads_the_token_from_stdin_never_from_the_command_line()
    {
        var args = LoginService.BuildArguments(new LoginRequest("github.com", GitProtocol.Https) { Token = "ghp_secret" });

        Assert.Contains("--with-token", args);
        Assert.DoesNotContain(args, a => a.Contains("ghp_secret"));
    }

    [Theory]
    [InlineData("! One-time code (AB12-CD34) copied to clipboard\nOpen this URL to continue in your web browser: https://github.com/login/device\n")]
    [InlineData("! First copy your one-time code: AB12-CD34\nPress Enter to open https://github.com/login/device in your browser...")]
    public void Recognises_the_device_code_in_both_gh_formats(string output)
    {
        var code = LoginService.ParseDeviceCode(output);

        Assert.NotNull(code);
        Assert.Equal("AB12-CD34", code.Code);
        Assert.Equal("https://github.com/login/device", code.VerificationUrl);
    }

    [Fact]
    public async Task Browser_login_reports_the_code_then_returns_the_new_account()
    {
        var runner = new SequencedRunner("! One-time code (AB12-CD34) copied to clipboard\n✓ Authentication complete.\n✓ Logged in as NewOne\n");
        DeviceCode? received = null;

        var account = await Create(runner).LoginAsync(new LoginRequest("github.com", GitProtocol.Https), c => received = c);

        Assert.Equal("AB12-CD34", received?.Code);
        Assert.Equal("NewOne", account.Login);
    }

    [Fact]
    public async Task Token_login_sends_the_token_on_stdin()
    {
        var runner = new SequencedRunner("");

        var account = await Create(runner).LoginAsync(new LoginRequest("github.com", GitProtocol.Https) { Token = "  ghp_pasted \n" });

        var login = runner.Requests.Single(r => r.Arguments[1] == "login");
        Assert.Equal("ghp_pasted\n", login.StandardInput);
        Assert.Equal("NewOne", account.Login); // trouvé par différence avant / après
    }

    [Fact]
    public async Task Failed_login_raises_a_readable_error()
    {
        var runner = new SequencedRunner("error validating token: HTTP 401: Bad credentials", loginExit: 1);

        var error = await Assert.ThrowsAsync<GhCommandException>(() =>
            Create(runner).LoginAsync(new LoginRequest("github.com", GitProtocol.Https) { Token = "bad" }));

        Assert.Contains("Bad credentials", error.Message);
    }

    [Fact]
    public async Task Switch_targets_the_exact_account()
    {
        var runner = new FakeProcessRunner().When(["auth", "switch"], "");

        await new AccountService(new GhCli(runner)).SwitchAsync(new GitHubAccount("SamuelnitraM", "github.com", false, true));

        Assert.Equal(["auth", "switch", "--hostname", "github.com", "--user", "SamuelnitraM"], runner.Requests[0].Arguments);
    }
}
