using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Projects;

/// <summary>F3B : un projet lié à plusieurs repos. « GitHub » est remplacé par des dépôts nus locaux.</summary>
public sealed class ProjectLinksTests : IDisposable
{
    private static readonly GitHubAccount Pro = new("SmartDev-Web", "github.com", true, true);
    private static readonly GitHubAccount Perso = new("SamuelnitraM", "github.com", false, true);

    private readonly TempRepo _repo = new("liens");
    private readonly FakeProcessRunner _gh = new();
    private readonly GitService _git;
    private readonly ProjectLinks _links;
    private readonly TransferService _transfer;
    private string _main = "", _second = "";

    public ProjectLinksTests()
    {
        _gh.When(["auth", "token"], "gho_tokenOfTheAccount_0123456789").When(["repo", "view"], "", exitCode: 1);
        var runner = new RoutingRunner(_gh);
        var gh = new GhCli(runner);
        var accounts = new AccountService(gh);
        var credentials = new GitCredentialResolver(accounts);
        _git = new GitService(runner);
        _links = new ProjectLinks(_git, credentials);
        _transfer = new TransferService(_git, new GitHubService(gh, accounts), credentials);
    }

    private string P => _repo.Path;
    private LocalProject Read() => ProjectScanner.ReadProject(P)!;

    /// <summary>Projet poussé sur un premier repo (origin), avec un second repo vide à côté.</summary>
    private async Task TwoServers()
    {
        _main = await _repo.BareAsync("principal.git");
        _second = await _repo.BareAsync("second.git");
        await _repo.WithCommitAsync();
        await _git.AddRemoteAsync(P, "origin", _main);
        await _repo.Git("push", "-q", "-u", "origin", "main");
    }

    private static ProjectLink Link(string name, string url, bool primary = false) => new(name, RemoteUrlParser.Parse(url), null, primary);

    [Fact]
    public async Task Adding_a_secondary_link_keeps_the_primary_and_its_tracking()
    {
        await TwoServers();

        var result = await _transfer.RepointAsync(new RepointPlan(P, Perso, "second")
            { TargetUrl = _second, RemoteName = "samuelnitram", KeepCurrentLink = true, Scope = PushScope.CurrentBranch });

        Assert.Contains("aussi lié", result.Message);
        var project = Read();
        Assert.Equal(("origin", _main), (project.RemoteName, project.Remote!.Url));
        Assert.Equal(["origin", "samuelnitram"], project.Remotes.Select(r => r.Name));
        Assert.Equal("origin/main", await _repo.Git("rev-parse", "--abbrev-ref", "main@{upstream}")); // le suivi ne change pas
        Assert.Equal(await _repo.Git("rev-parse", "main"), await _repo.GitIn(_second, "rev-parse", "main"));
        Assert.DoesNotContain(TransferService.Preview(new RepointPlan(P, Perso, "x") { KeepCurrentLink = true, RemoteName = "samuelnitram" }, _main, "main"),
            l => l.Contains("--set-upstream") || l.Contains("remote rename") || l.Contains("remote remove"));
    }

    [Fact]
    public async Task Pushes_to_every_selected_link_and_explains_a_secondary_with_extra_commits()
    {
        await TwoServers();
        await _git.AddRemoteAsync(P, "samuelnitram", _second);
        // Quelqu'un a envoyé directement sur le second repo : son historique diffère.
        var other = _repo.Sub("autre poste");
        await _repo.GitIn(other, "init", "-q", "-b", "main");
        await _repo.CommitFileAsync("ailleurs.txt", "x", "Commit fait ailleurs", other);
        await _repo.GitIn(other, "push", "-q", _second, "main");
        await _repo.CommitFileAsync("nouveau.txt", "n", "Nouveau travail");

        var results = await _links.PushAsync(P, "main", [Link("origin", _main, primary: true), Link("samuelnitram", _second)], setPrimaryUpstream: false);

        Assert.True(results[0].Succeeded, results[0].Message);
        Assert.Contains("branche main envoyée", results[0].Message);
        Assert.False(results[1].Succeeded);
        Assert.Contains("contient des commits que ton projet n'a pas", results[1].Message);
        Assert.Contains("Rien n'a été envoyé ni écrasé", results[1].Message);
        Assert.Equal("Commit fait ailleurs", await _repo.GitIn(_second, "log", "-1", "--format=%s", "main")); // intact

        var fetched = await _links.FetchAsync(P, [Link("origin", _main, primary: true), Link("samuelnitram", _second)]);
        Assert.All(fetched, f => Assert.True(f.Succeeded, f.Message));
        Assert.Equal(new RemoteBranchState(true, 2, 1), await _git.CompareWithRemoteAsync(P, "samuelnitram", "main"));
        Assert.Equal(new RemoteBranchState(true, 0, 0), await _git.CompareWithRemoteAsync(P, "origin", "main"));
        Assert.False((await _git.CompareWithRemoteAsync(P, "origin", "inconnue")).Exists);
    }

    [Fact]
    public async Task Making_a_secondary_link_primary_swaps_names_and_tracking()
    {
        await TwoServers();
        await _git.AddRemoteAsync(P, "samuelnitram", _second);
        await _git.SetPushRemotesAsync(P, ["origin", "samuelnitram"]);

        // Le second repo ne connaît pas encore main : la branche ne suit plus rien, le prochain envoi la créera.
        var steps = await _links.MakePrimaryAsync(P, "samuelnitram", "smartdev-web", "main");
        Assert.All(steps, s => Assert.True(s.Succeeded, s.Output));
        var project = Read();
        Assert.Equal(_second, project.Remote!.Url);
        Assert.Equal(_main, project.Remotes.Single(r => r.Name == "smartdev-web").Url.Url);
        Assert.Empty(project.PushRemotes); // la sélection repart du principal
        Assert.Equal("", await new Core.Processes.ProcessRunner().RunAsync(new Core.Processes.ProcessRequest("git", ["config", "branch.main.remote"]) { WorkingDirectory = P })
            .ContinueWith(t => t.Result.StandardOutput.Trim()));

        // Retour : l'ancien principal connaît main, la branche le suit de nouveau.
        steps = await _links.MakePrimaryAsync(P, "smartdev-web", "samuelnitram", "main");
        Assert.All(steps, s => Assert.True(s.Succeeded, s.Output));
        Assert.Equal(_main, Read().Remote!.Url);
        Assert.Equal("origin/main", await _repo.Git("rev-parse", "--abbrev-ref", "main@{upstream}"));
    }

    [Fact]
    public void Link_names_default_to_the_account_login_and_are_validated()
    {
        Assert.Equal("samuelnitram", ProjectLinks.SuggestName("SamuelnitraM", ["origin"]));
        Assert.Equal("samuelnitram-2", ProjectLinks.SuggestName("SamuelnitraM", ["origin", "samuelnitram"]));
        Assert.Null(ProjectLinks.ValidateName("perso", ["origin"]));
        Assert.Contains("réservé", ProjectLinks.ValidateName("origin", []));
        Assert.Contains("pas d'espace", ProjectLinks.ValidateName("mon lien", []));
        Assert.Contains("déjà", ProjectLinks.ValidateName("Perso", ["perso"]));
    }

    [Fact]
    public async Task The_scanner_reads_every_link_and_the_remembered_push_selection()
    {
        await TwoServers();
        await _git.AddRemoteAsync(P, "samuelnitram", "https://github.com/SamuelnitraM/stock.git");
        await _git.SetPushRemotesAsync(P, ["origin", "samuelnitram"]);

        var project = Read();
        Assert.Equal(["origin", "samuelnitram"], project.PushRemotes);
        Assert.Equal(2, project.RemoteCount);

        // Le lien secondaire vers un repo de SamuelnitraM rattache aussi le projet à ce compte.
        var placement = ProjectClassifier.Place(project with { Remotes = [project.Remotes[0] with { Url = RemoteUrlParser.Parse("https://github.com/SmartDev-Web/stock.git") }, project.Remotes[1]],
            Remote = RemoteUrlParser.Parse("https://github.com/SmartDev-Web/stock.git") }, [Pro, Perso]);
        Assert.Equal(Pro, placement.Account);
        Assert.True(placement.HasLinkFor(Perso) && placement.BelongsTo(Perso) && placement.BelongsTo(Pro));
        Assert.Equal(Perso, placement.SecondaryLinks.Single().Account);
        Assert.Equal(["origin", "samuelnitram"], placement.Links.Select(l => l.Name));

        Assert.Single(LinkedProjects.Find([project], "github.com", "samuelnitram/STOCK")); // trouvé aussi par son lien secondaire
    }

    [Fact]
    public void A_project_whose_primary_is_elsewhere_still_belongs_to_the_account_of_a_secondary_link()
    {
        var project = new LocalProject(@"C:\p")
        {
            RemoteName = "origin",
            Remote = RemoteUrlParser.Parse("https://gitlab.com/moi/stock.git"),
            Remotes = [new("origin", RemoteUrlParser.Parse("https://gitlab.com/moi/stock.git")), new("perso", RemoteUrlParser.Parse("https://github.com/SamuelnitraM/stock.git"))],
        };

        var placement = ProjectClassifier.Place(project, [Pro, Perso]);

        Assert.Null(placement.Reason);
        Assert.True(placement.BelongsTo(Perso));
        Assert.False(placement.BelongsTo(Pro));
    }

    public void Dispose() => _repo.Dispose();
}
