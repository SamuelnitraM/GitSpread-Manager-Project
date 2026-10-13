using System.IO;
using GitSpread.App.ViewModels;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;
using GitSpread.Core.Settings;
using GitSpread.Core.Ssh;
using GitSpread.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitSpread.App.Tests;

/// <summary>Fenêtre principale simulée : vrais services branchés sur un faux gh / git.</summary>
public sealed class TestHost : IProjectHost, IDialogService
{
    public static readonly GitHubAccount Pro = new("SmartDev-Web", "github.com", IsActive: true, IsAuthenticated: true);
    public static readonly GitHubAccount Perso = new("SamuelnitraM", "github.com", IsActive: false, IsAuthenticated: true);

    public TestHost(params GitHubAccount[] accounts)
    {
        Runner = new FakeProcessRunner()
            .When(["auth", "token"], "gho_tokenDeTest_0123456789abcdef")
            .When(["repo", "view"], "", exitCode: 1, stderr: "Could not resolve to a Repository")
            .When(["repo", "list"], "[]");
        var gh = new GhCli(Runner);
        var accountService = new AccountService(gh);
        var gitHub = new GitHubService(gh, accountService);
        var git = new GitService(Runner);
        var credentials = new GitCredentialResolver(accountService);
        var dir = Directory.CreateTempSubdirectory("GitSpread app tests ").FullName;
        Services = new AppServices(accountService, gitHub, new LoginService(gh, accountService), git, credentials,
            new ProjectPublisher(git, gitHub, credentials), new BranchIntegrator(git), new TransferService(git, gitHub, credentials),
            new SshKeyService(Runner, Path.Combine(dir, ".ssh")), new ProjectScanner(),
            new SettingsStore(Path.Combine(dir, "settings.json")), NullLoggerFactory.Instance, dir);
        Accounts = accounts.Length > 0 ? accounts : [Pro, Perso];
    }

    public FakeProcessRunner Runner { get; }
    public AppServices Services { get; }
    public IDialogService Dialogs => this;
    public IReadOnlyList<GitHubAccount> Accounts { get; }
    public AppSettings Settings { get; } = new();
    public void SaveSettings() { }
    public Task RescanAsync(string? revealPath = null) => Task.CompletedTask;
    public Task RemoveExtraProjectAsync(string path) => Task.CompletedTask;

    /// <summary>Projet local de test (rien n'est lu sur le disque tant qu'on ne charge pas ses détails).</summary>
    public ProjectItemViewModel Project(string name = "Mon projet", GitHubAccount? account = null, string? remote = null) =>
        new(new ProjectPlacement(
            new LocalProject(Path.Combine(Path.GetTempPath(), name)) { Remote = remote is null ? null : RemoteUrlParser.Parse(remote), RemoteName = remote is null ? null : "origin" },
            account, account is null ? UnlinkedReason.NoRemote : null), this);

    /// <summary>Attend qu'une condition devienne vraie (vérifications GitHub asynchrones).</summary>
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            if (waited > timeoutMs) throw new TimeoutException("Condition jamais remplie.");
            await Task.Delay(20);
        }
    }

    // IDialogService : non utilisé par ces tests
    public bool ShowCommit(CommitViewModel viewModel) => false;
    public void ShowPublish(PublishViewModel viewModel) { }
    public bool ShowIntegration(IntegrationViewModel viewModel) => false;
    public void ShowRepoint(RepointViewModel viewModel) { }
    public bool Confirm(string message, string title) => true;
    public int Choose(string title, string message, IReadOnlyList<string> options) => -1;
    public string? Prompt(string title, string message, string initialValue, Func<string, string?>? validate = null, bool spacesToDash = false) => null;
}
