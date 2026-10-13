using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Processes;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Projects;

/// <summary>Publication de bout en bout : vrai git, gh simulé, « GitHub » remplacé par un dépôt nu local.</summary>
public sealed class ProjectPublisherTests : IDisposable
{
    private static readonly GitHubAccount Account = new("SamuelnitraM", "github.com", IsActive: false, IsAuthenticated: true);
    private readonly TempRepo _repo = new("publish");
    private readonly string _bare;
    private readonly FakeProcessRunner _gh = new();
    private readonly ProjectPublisher _publisher;
    private readonly GitService _git;

    public ProjectPublisherTests()
    {
        _bare = Path.Combine(_repo.Root, "SamuelnitraM", "mon-projet.git");
        _gh.When(["auth", "token"], "gho_tokenOfSamuelnitraM_0123456789")
           .When(["repo", "view"], "", exitCode: 1, stderr: "GraphQL: Could not resolve to a Repository")
           .When(["repo", "create"], _bare + "\n")
           .When(["api", "gitignore/templates/Node"], """{"name":"Node","source":"node_modules/\n"}""");

        var runner = new RoutingRunner(_gh);
        var gh = new GhCli(runner);
        var accounts = new AccountService(gh);
        _git = new GitService(runner);
        _publisher = new ProjectPublisher(_git, new GitHubService(gh, accounts), new GitCredentialResolver(accounts));
    }

    private async Task CreateServerRepo()
    {
        Directory.CreateDirectory(_bare);
        await _repo.GitIn(_bare, "init", "-q", "--bare");
    }

    [Fact]
    public async Task Publishes_a_folder_that_was_not_even_a_git_repository()
    {
        await CreateServerRepo();
        _repo.Write("index.js", "console.log('hello')");
        _repo.Write("node_modules/lib/index.js", "dépendance");
        var steps = new List<string>();

        var url = await _publisher.PublishAsync(new PublishPlan(_repo.Path, Account, "mon-projet", RepositoryVisibility.Private)
        {
            InitializeRepository = true,
            GitignoreTemplate = "Node",
            Identity = new GitIdentity("Samuel", "1+SamuelnitraM@users.noreply.github.com"),
        }, new SyncProgress(steps));

        Assert.Equal(_bare, url);
        Assert.Equal("node_modules/\n", File.ReadAllText(Path.Combine(_repo.Path, ".gitignore")));

        // Le « serveur » a reçu le commit, sans node_modules, signé avec la bonne identité.
        var files = await _repo.GitIn(_bare, "ls-tree", "-r", "--name-only", "main");
        Assert.Equal([".gitignore", "index.js"], files.Split('\n'));
        Assert.Equal("1+SamuelnitraM@users.noreply.github.com", await _repo.GitIn(_bare, "log", "-1", "--format=%ae", "main"));

        var status = await _git.GetStatusAsync(_repo.Path);
        Assert.Equal("origin/main", status.Upstream);
        Assert.True(status.IsClean);

        var create = _gh.Single("repo", "create");
        Assert.Equal(["repo", "create", "SamuelnitraM/mon-projet", "--private"], create.Arguments);
        Assert.Equal("gho_tokenOfSamuelnitraM_0123456789", create.Environment!["GH_TOKEN"]);
        Assert.Contains("Publication terminée.", steps);
    }

    [Fact]
    public async Task Keeps_an_existing_gitignore_and_history()
    {
        await CreateServerRepo();
        await _repo.WithCommitAsync("Historique existant");
        _repo.Write(".gitignore", "perso/\n");
        await _repo.Git("add", ".");
        await _repo.Git("commit", "-q", "-m", "gitignore perso");

        await _publisher.PublishAsync(new PublishPlan(_repo.Path, Account, "mon-projet", RepositoryVisibility.Public) { GitignoreTemplate = "Node" });

        Assert.Equal("perso/\n", File.ReadAllText(Path.Combine(_repo.Path, ".gitignore")));
        Assert.Equal("gitignore perso\nHistorique existant", await _repo.GitIn(_bare, "log", "--format=%s", "main"));
        Assert.Contains("--public", _gh.Single("repo", "create").Arguments);
    }

    [Fact]
    public async Task Refuses_an_existing_repository_name_before_changing_anything_on_GitHub()
    {
        await _repo.WithCommitAsync();
        _gh.When(["repo", "view"], """{"name":"mon-projet"}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _publisher.PublishAsync(new PublishPlan(_repo.Path, Account, "mon-projet", RepositoryVisibility.Private)));

        Assert.Contains("existe déjà", error.Message);
        Assert.DoesNotContain(_gh.Requests, r => r.Arguments.Take(2).SequenceEqual(["repo", "create"]));
    }

    [Fact]
    public async Task Refuses_a_project_that_already_has_an_origin()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("remote", "add", "origin", "https://github.com/x/y.git");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _publisher.PublishAsync(new PublishPlan(_repo.Path, Account, "mon-projet", RepositoryVisibility.Private)));
        Assert.DoesNotContain(_gh.Requests, r => r.Arguments.Take(2).SequenceEqual(["repo", "create"]));
    }

    [Fact]
    public void Preview_lists_every_command_with_the_chosen_account()
    {
        var preview = ProjectPublisher.Preview(new PublishPlan(_repo.Path, Account, "mon-projet", RepositoryVisibility.Private)
            { InitializeRepository = true, GitignoreTemplate = "Node" });

        Assert.Equal("git init -b main", preview[0]);
        Assert.Contains(preview, l => l.StartsWith("gh repo create SamuelnitraM/mon-projet --private"));
        Assert.Contains(preview, l => l.StartsWith("git push --set-upstream origin") && l.Contains("SamuelnitraM"));
    }

    public void Dispose() => _repo.Dispose();

    /// <summary>Progress synchrone (Progress&lt;T&gt; poste sur le pool de threads, trop tard pour les assertions).</summary>
    private sealed class SyncProgress(List<string> target) : IProgress<string>
    {
        public void Report(string value) => target.Add(value);
    }
}

public class PublishingHelpersTests
{
    [Theory]
    [InlineData("Mon Projet (v2)", "Mon-Projet-v2")]
    [InlineData("Quissac-cbd", "Quissac-cbd")]
    [InlineData("Écran d'accueil", "Ecran-d-accueil")]
    [InlineData("ps_smartstock", "ps_smartstock")]
    [InlineData("...", "projet")]
    [InlineData("  --x--  ", "x")]
    public void Turns_folder_names_into_valid_repository_names(string folder, string expected)
    {
        Assert.Equal(expected, RepositoryName.FromFolder(folder));
        Assert.True(RepositoryName.IsValid(expected));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("é")]
    public void Rejects_invalid_names(string name) => Assert.False(RepositoryName.IsValid(name));

    [Fact]
    public void Recognises_the_commit_emails_GitHub_attributes_to_the_account()
    {
        var user = new GitHubUser(42, "SamuelnitraM") { Email = "sam@exemple.fr" };

        Assert.True(user.OwnsEmail("42+SamuelnitraM@users.noreply.github.com"));
        Assert.True(user.OwnsEmail("samuelnitram@users.noreply.github.com"));
        Assert.True(user.OwnsEmail("SAM@exemple.fr"));
        Assert.False(user.OwnsEmail("smart.developpement.web@gmail.com"));
        Assert.False(user.OwnsEmail(null));
    }

    [Theory]
    [InlineData("package.json", "Node")]
    [InlineData("composer.json", "Composer")]
    [InlineData("artisan", "Laravel")]
    [InlineData("App.sln", "VisualStudio")]
    [InlineData("requirements.txt", "Python")]
    [InlineData("module.php", "Composer")]
    [InlineData("notes.txt", null)]
    public void Guesses_the_gitignore_template(string file, string? expected)
    {
        var dir = Directory.CreateTempSubdirectory("GitSpread guess ").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, file), "");
            Assert.Equal(expected, GitignoreGuesser.Guess(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
