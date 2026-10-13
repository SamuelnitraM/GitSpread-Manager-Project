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

    public TestHost(params GitHubAccount[] accounts) : this(realGit: false, accounts) { }

    /// <param name="realGit">Vrai : git est réellement exécuté (dépôts temporaires), seul gh est simulé.</param>
    public TestHost(bool realGit, params GitHubAccount[] accounts)
    {
        Runner = new FakeProcessRunner()
            .When(["auth", "token"], "gho_tokenDeTest_0123456789abcdef")
            .When(["repo", "view"], "", exitCode: 1, stderr: "Could not resolve to a Repository")
            .When(["repo", "list"], "[]");
        Core.Processes.IProcessRunner runner = realGit ? new RoutingRunner(Runner) : Runner;
        var gh = new GhCli(runner);
        var accountService = new AccountService(gh);
        var gitHub = new GitHubService(gh, accountService);
        var git = new GitService(runner);
        var credentials = new GitCredentialResolver(accountService);
        var dir = Directory.CreateTempSubdirectory("GitSpread app tests ").FullName;
        Services = new AppServices(accountService, gitHub, new LoginService(gh, accountService), git, credentials,
            new ProjectPublisher(git, gitHub, credentials), new BranchIntegrator(git), new TransferService(git, gitHub, credentials),
            new LinkedProjects(git, credentials),
            new SshKeyService(runner, Path.Combine(dir, ".ssh")), new ProjectScanner(),
            new SettingsStore(Path.Combine(dir, "settings.json")), NullLoggerFactory.Instance, dir);
        Accounts = accounts.Length > 0 ? accounts : [Pro, Perso];
    }

    public FakeProcessRunner Runner { get; }
    public AppServices Services { get; }
    public IDialogService Dialogs => this;
    public IReadOnlyList<GitHubAccount> Accounts { get; }
    public AppSettings Settings { get; } = new();
    public void SaveSettings() { }

    /// <summary>Profils GitHub renvoyés par <see cref="GetAccountUserAsync"/> (sans e-mail public par défaut).</summary>
    public Dictionary<string, GitHubUser?> Users { get; } = new()
    {
        [Pro.Login] = new GitHubUser(1001, Pro.Login),
        [Perso.Login] = new GitHubUser(2002, Perso.Login) { Name = "Samuel" },
    };

    public Task<GitHubUser?> GetAccountUserAsync(GitHubAccount account) => Task.FromResult(Users.GetValueOrDefault(account.Login));

    public Task<bool> EnsureScopeAsync(GitHubAccount account, string scope, string reason) => Task.FromResult(account.HasScope(scope));
    public void ShowScopes(AccountScopesViewModel viewModel) { }
    public void ShowClone(CloneViewModel viewModel) { }
    public void ShowEditRepository(EditRepositoryViewModel viewModel) { }
    public IReadOnlyList<LocalProject> LocalProjects { get; set; } = [];
    public Task ReloadRepositoriesAsync(GitHubAccount account) => Task.CompletedTask;
    public int CollaborationReloads { get; private set; }
    public Task ReloadCollaborationsAsync() { CollaborationReloads++; return Task.CompletedTask; }
    public Task RescanAsync(string? revealPath = null) => Task.CompletedTask;
    public Task RemoveExtraProjectAsync(string path) => Task.CompletedTask;

    /// <summary>Projet local de test (rien n'est lu sur le disque tant qu'on ne charge pas ses détails).</summary>
    public ProjectItemViewModel Project(string name = "Mon projet", GitHubAccount? account = null, string? remote = null, string? path = null) =>
        new(new ProjectPlacement(
            new LocalProject(path ?? Path.Combine(Path.GetTempPath(), name)) { Remote = remote is null ? null : RemoteUrlParser.Parse(remote), RemoteName = remote is null ? null : "origin" },
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

    /// <summary>Assistants ouverts (publication, repointage), dans l'ordre.</summary>
    public List<object> Shown { get; } = [];

    public bool ShowCommit(CommitViewModel viewModel) => false;
    public void ShowPublish(PublishViewModel viewModel) => Shown.Add(viewModel);
    public bool ShowIntegration(IntegrationViewModel viewModel) => false;
    public void ShowRepoint(RepointViewModel viewModel) => Shown.Add(viewModel);
    public bool Confirm(string message, string title, bool destructive = false) => true;
    /// <summary>Réponses données aux saisies successives (null = annuler). La validation de la fenêtre est appliquée.</summary>
    public Queue<string?> PromptAnswers { get; } = new();
    public List<string> PromptErrors { get; } = [];
    public int ChooseAnswer { get; set; } = -1;

    public int Choose(string title, string message, IReadOnlyList<string> options) => ChooseAnswer;

    public string? Prompt(string title, string message, string initialValue, Func<string, string?>? validate = null, bool spacesToDash = false)
    {
        if (!PromptAnswers.TryDequeue(out var answer) || answer is null) return null;
        if (validate?.Invoke(answer) is { } error) { PromptErrors.Add(error); return null; } // la fenêtre resterait ouverte
        return answer;
    }
}
