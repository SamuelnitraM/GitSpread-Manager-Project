using System.Text;
using GitSpread.Core.Git;
using GitSpread.Core.Processes;

namespace GitSpread.Tests.Git;

public class GitCredentialEnvironmentTests
{
    private const string Token = "gho_tokenOfSamuelnitraM_0123456789";

    [Fact]
    public void Disables_existing_helpers_and_injects_a_basic_header_scoped_to_the_host()
    {
        var env = GitCredentialEnvironment.For("SamuelnitraM", Token);

        Assert.Equal("2", env["GIT_CONFIG_COUNT"]);
        Assert.Equal("credential.helper", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("", env["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("http.https://github.com/.extraHeader", env["GIT_CONFIG_KEY_1"]);

        var header = env["GIT_CONFIG_VALUE_1"];
        Assert.StartsWith("Authorization: Basic ", header);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Authorization: Basic ".Length..]));
        Assert.Equal($"SamuelnitraM:{Token}", decoded);
    }

    [Fact]
    public void Never_lets_git_prompt()
    {
        foreach (var env in new[] { GitCredentialEnvironment.For("a", Token), GitCredentialEnvironment.NonInteractive() })
        {
            Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("never", env["GCM_INTERACTIVE"]);
        }
        Assert.Equal("0", GitCredentialEnvironment.NonInteractive()["GIT_CONFIG_COUNT"]);
    }

    [Fact]
    public async Task Real_git_sees_the_injected_config_without_any_file_being_changed()
    {
        var runner = new ProcessRunner();
        var env = GitCredentialEnvironment.For("SamuelnitraM", Token);

        async Task<string> Get(string key) => (await runner.RunAsync(
            new ProcessRequest("git", ["config", "--get", key]) { Environment = env })).StandardOutput.Trim();

        // La dernière valeur de credential.helper est vide : git réinitialise la liste (GCM désactivé).
        Assert.Equal("", await Get("credential.helper"));
        Assert.StartsWith("Authorization: Basic ", await Get("http.https://github.com/.extraheader"));

        var global = await runner.RunAsync(new ProcessRequest("git", ["config", "--global", "--get-all", "http.https://github.com/.extraheader"]));
        Assert.DoesNotContain("Authorization", global.StandardOutput);
    }
}

public class GitCredentialResolverTests
{
    private static readonly GitSpread.Core.Accounts.GitHubAccount Account = new("SamuelnitraM", "github.com", false, true);

    private static GitCredentialResolver Create(Fakes.FakeProcessRunner runner) =>
        new(new GitSpread.Core.Accounts.AccountService(new GitSpread.Core.GitHub.GhCli(runner)));

    [Fact]
    public async Task Https_remote_of_a_known_account_gets_its_token()
    {
        var runner = new Fakes.FakeProcessRunner().When(["auth", "token"], "gho_tokenOfSamuelnitraM_0123456789");

        var env = await Create(runner).EnvironmentForAsync(Account, RemoteUrlParser.Parse("https://github.com/SamuelnitraM/x.git"));

        Assert.Equal("2", env["GIT_CONFIG_COUNT"]);
        Assert.Contains("--user", runner.Requests[0].Arguments);
    }

    [Theory]
    [InlineData("git@github.com:SamuelnitraM/x.git")] // SSH : les clés de l'utilisateur s'appliquent
    [InlineData("https://gitlab.com/SamuelnitraM/x.git")] // autre hôte : on n'envoie surtout pas le token GitHub
    public async Task Other_remotes_never_receive_the_token(string url)
    {
        var runner = new Fakes.FakeProcessRunner();

        var env = await Create(runner).EnvironmentForAsync(Account, RemoteUrlParser.Parse(url));

        Assert.Equal("0", env["GIT_CONFIG_COUNT"]);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Without_account_git_uses_the_user_config_without_prompting()
    {
        var env = await Create(new Fakes.FakeProcessRunner()).EnvironmentForAsync(null, RemoteUrlParser.Parse("https://github.com/org/x.git"));

        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("0", env["GIT_CONFIG_COUNT"]);
    }
}
