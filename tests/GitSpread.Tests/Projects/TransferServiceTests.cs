using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Projects;

/// <summary>Repointage d'un projet vers un autre repo : « GitHub » remplacé par des dépôts nus locaux.</summary>
public sealed class TransferServiceTests : IDisposable
{
    private static readonly GitHubAccount Perso = new("SamuelnitraM", "github.com", false, true);
    private static readonly GitHubAccount Pro = new("SmartDev-Web", "github.com", true, true);

    private readonly TempRepo _repo = new("transfer");
    private readonly FakeProcessRunner _gh = new();
    private readonly TransferService _transfer;
    private readonly GitService _git;
    private string _oldServer = "";

    public TransferServiceTests()
    {
        _gh.When(["auth", "token"], "gho_tokenOfTheAccount_0123456789")
           .When(["repo", "view"], "", exitCode: 1)
           .When(["api"], "{}");
        var runner = new RoutingRunner(_gh);
        var gh = new GhCli(runner);
        var accounts = new AccountService(gh);
        _git = new GitService(runner);
        _transfer = new TransferService(_git, new GitHubService(gh, accounts), new GitCredentialResolver(accounts));
    }

    private string P => _repo.Path;

    /// <summary>Projet lié à un premier « GitHub », avec deux branches et un tag.</summary>
    private async Task LinkedProject()
    {
        _oldServer = await _repo.BareAsync("ancien.git");
        await _repo.WithCommitAsync();
        await _repo.Git("tag", "v1.0");
        await _repo.Git("switch", "-q", "-c", "feature");
        await _repo.CommitFileAsync("feature.txt", "f", "feature");
        await _repo.Git("switch", "-q", "main");
        await _git.AddRemoteAsync(P, "origin", _oldServer);
        await _repo.Git("push", "-q", "-u", "origin", "main");
    }

    [Fact]
    public async Task Repoints_to_an_empty_repository_keeping_the_old_link()
    {
        await LinkedProject();
        var target = await _repo.BareAsync("cible.git");

        Assert.True((await _transfer.AnalyzeTargetAsync(P, Pro, target)).IsEmpty);
        var result = await _transfer.RepointAsync(new RepointPlan(P, Pro, "cible") { TargetUrl = target });

        var remotes = await _git.GetRemotesAsync(P);
        Assert.Equal(target, remotes.Single(r => r.Name == "origin").Url);
        Assert.Equal(_oldServer, remotes.Single(r => r.Name == "old-origin").Url);
        Assert.Equal(["feature", "main"], (await _repo.GitIn(target, "branch", "--format=%(refname:short)")).Split('\n'));
        Assert.Equal("v1.0", await _repo.GitIn(target, "tag"));
        Assert.Equal("origin/main", (await _git.GetStatusAsync(P)).Upstream);
        Assert.Contains("old-origin", result.Message);
    }

    [Fact]
    public async Task A_compatible_history_is_extended_without_force()
    {
        await LinkedProject();
        var target = await _repo.BareAsync("cible.git");
        await _repo.Git("push", "-q", target, "main"); // la cible a déjà main, en retard
        await _repo.CommitFileAsync("nouveau.txt", "n", "nouveau commit");

        var analysis = await _transfer.AnalyzeTargetAsync(P, Pro, target);
        Assert.False(analysis.HasDivergingHistory);
        Assert.Equal(["main"], analysis.CompatibleBranches);

        await _transfer.RepointAsync(new RepointPlan(P, Pro, "cible") { TargetUrl = target, Scope = PushScope.CurrentBranch });
        Assert.Equal(await _repo.Git("rev-parse", "main"), await _repo.GitIn(target, "rev-parse", "main"));
    }

    [Fact]
    public async Task A_different_history_is_never_overwritten()
    {
        await LinkedProject();
        var target = await _repo.BareAsync("cible.git");
        var stranger = Path.Combine(_repo.Root, "autre projet");
        await _repo.GitIn(_repo.Root, "clone", "-q", target, stranger);
        await _repo.CommitFileAsync("autre.txt", "a", "historique étranger", stranger);
        await _repo.GitIn(stranger, "push", "-q", "origin", "HEAD:main");
        var theirs = await _repo.GitIn(target, "rev-parse", "main");

        var analysis = await _transfer.AnalyzeTargetAsync(P, Pro, target);
        Assert.Equal(["main"], analysis.DivergingBranches);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _transfer.RepointAsync(new RepointPlan(P, Pro, "cible") { TargetUrl = target }));
        Assert.Contains("historique différent", error.Message);
        Assert.Equal(_oldServer, (await _git.GetRemotesAsync(P)).Single(r => r.Name == "origin").Url); // rien n'a bougé
        Assert.Equal(theirs, await _repo.GitIn(target, "rev-parse", "main"));

        // Option proposée : envoyer sur une nouvelle branche
        var result = await _transfer.RepointAsync(new RepointPlan(P, Pro, "cible") { TargetUrl = target, PushToNewBranch = "import-gitspread" });
        Assert.Equal(theirs, await _repo.GitIn(target, "rev-parse", "main"));
        Assert.Equal(await _repo.Git("rev-parse", "main"), await _repo.GitIn(target, "rev-parse", "import-gitspread"));
        Assert.Contains("import-gitspread", result.Message);
    }

    [Fact]
    public async Task Failure_restores_the_original_link()
    {
        await LinkedProject();
        var unreachable = Path.Combine(_repo.Root, "n'existe pas.git");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _transfer.RepointAsync(new RepointPlan(P, Pro, "cible") { TargetUrl = unreachable }));

        var remotes = await _git.GetRemotesAsync(P);
        Assert.Equal([new GitRemote("origin", _oldServer)], remotes);
    }

    [Fact]
    public async Task Push_failure_after_relinking_rolls_back_including_a_removed_remote()
    {
        await LinkedProject();
        var target = await _repo.BareAsync("cible.git");
        // Un hook qui refuse tout push simule un refus de GitHub après la liaison
        File.WriteAllText(Path.Combine(target, "hooks", "pre-receive"), "#!/bin/sh\necho refus >&2\nexit 1\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _transfer.RepointAsync(new RepointPlan(P, Pro, "cible") { TargetUrl = target, OldRemote = OldRemoteAction.Remove }));

        Assert.Contains("lien d'origine", error.Message);
        Assert.Equal([new GitRemote("origin", _oldServer)], await _git.GetRemotesAsync(P));
    }

    [Fact]
    public async Task Creates_the_repository_with_the_target_account()
    {
        await LinkedProject();
        var created = Path.Combine(_repo.Root, "SmartDev-Web", "nouveau.git");
        Directory.CreateDirectory(created);
        await _repo.GitIn(created, "init", "-q", "--bare");
        _gh.When(["repo", "create"], created + "\n");

        await _transfer.RepointAsync(new RepointPlan(P, Pro, "nouveau") { OldRemote = OldRemoteAction.Remove, Scope = PushScope.CurrentBranch });

        Assert.Equal(["repo", "create", "SmartDev-Web/nouveau", "--private"], _gh.Single("repo", "create").Arguments);
        Assert.Equal([new GitRemote("origin", created)], await _git.GetRemotesAsync(P));
        Assert.Equal("main", await _repo.GitIn(created, "branch", "--format=%(refname:short)"));
    }

    [Fact]
    public async Task Native_transfer_calls_the_api_with_the_owner_token_then_updates_the_link()
    {
        await LinkedProject();

        var result = await _transfer.TransferOwnershipAsync(P, Perso, Pro, "SmartStock");

        var api = _gh.Single("api");
        Assert.Equal(["api", "--method", "POST", "repos/SamuelnitraM/SmartStock/transfer", "-f", "new_owner=SmartDev-Web"], api.Arguments);
        Assert.Equal("https://github.com/SmartDev-Web/SmartStock.git", (await _git.GetRemotesAsync(P)).Single().Url);
        Assert.Contains("accepter", result.Message);
    }

    [Fact]
    public void Preview_lists_the_commands_and_the_account()
    {
        var preview = TransferService.Preview(new RepointPlan(@"C:\p", Pro, "cible"), "https://github.com/SamuelnitraM/x.git", "main");

        Assert.Equal("gh repo create SmartDev-Web/cible --private   (avec le compte SmartDev-Web)", preview[0]);
        Assert.Contains("git remote rename origin old-origin", preview);
        Assert.Contains("git push --set-upstream origin --all", preview);
        Assert.Contains("(envois faits avec le compte SmartDev-Web)", preview);
    }

    public void Dispose() => _repo.Dispose();
}
