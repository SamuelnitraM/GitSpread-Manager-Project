using GitSpread.App.ViewModels;
using GitSpread.Core.Git;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;

namespace GitSpread.App.Tests;

/// <summary>
/// F6b : actions sur les branches depuis l'historique. Le « GitHub » est un dépôt nu local, atteint par une vraie
/// adresse github.com grâce à url.insteadOf : git travaille pour de vrai, seules les commandes gh sont simulées.
/// </summary>
public sealed class BranchActionsTests : IDisposable
{
    private const string GitHubUrl = "https://github.com/SmartDev-Web/stock.git";
    private readonly TempRepo _repo = new("actions branches");
    private string _server = "";

    /// <summary>main et feature/envoyee sur « GitHub », feature/locale seulement ici (avec un commit unique).</summary>
    private async Task<(TestHost Host, ProjectItemViewModel Project)> ProjectAsync()
    {
        _server = await _repo.BareAsync("serveur.git");
        await _repo.WithCommitAsync();
        await _repo.Git("config", $"url.{_server.Replace('\\', '/')}.insteadOf", GitHubUrl);
        await _repo.Git("remote", "add", "origin", GitHubUrl);
        await _repo.Git("push", "-q", "-u", "origin", "main");
        await _repo.Git("switch", "-q", "-c", "feature/envoyee");
        await _repo.CommitFileAsync("envoye.txt", "e", "Travail envoyé");
        await _repo.Git("push", "-q", "-u", "origin", "feature/envoyee");
        await _repo.Git("switch", "-q", "-c", "feature/locale", "main");
        await _repo.CommitFileAsync("local.txt", "l", "Travail seulement ici");
        await _repo.Git("switch", "-q", "main");

        var host = new TestHost(realGit: true);
        host.Runner.When(["api", "--method", "POST"], "{}");
        var placement = ProjectClassifier.Place(ProjectScanner.ReadProject(_repo.Path)!, host.Accounts);
        var project = new ProjectItemViewModel(placement, host);
        await project.EnsureDetailsAsync();
        return (host, project);
    }

    private static BranchInfo Branch(ProjectItemViewModel p, string name) => p.Branches.Single(b => b.Name == name).Branch;
    private Task<string> Branches(string? dir = null) => _repo.GitIn(dir, "branch", "--format=%(refname:short)");

    [Fact]
    public async Task Renaming_a_local_only_branch_does_not_touch_github()
    {
        var (host, project) = await ProjectAsync();
        host.PromptAnswers.Enqueue("feature/renommee");

        await project.RenameBranchAsync(Branch(project, "feature/locale"), ["main", "feature/envoyee", "feature/locale"], "main");

        Assert.True(project.LastAction!.Succeeded, project.LastAction.Message);
        Assert.Contains("feature/renommee", (await Branches()).Split('\n'));
        Assert.DoesNotContain(host.Runner.Requests, r => r.Arguments.Contains("POST"));
    }

    [Fact]
    public async Task Renaming_a_branch_on_github_renames_it_there_and_keeps_the_link()
    {
        var (host, project) = await ProjectAsync();
        // Ce que fera GitHub… avec un serveur qui annonce encore l'ancien nom juste après le renommage (vu en réel).
        await _repo.GitIn(_server, "branch", "feature/nouveau-nom", "feature/envoyee");
        host.PromptAnswers.Enqueue("feature/nouveau-nom");

        await project.RenameBranchAsync(Branch(project, "feature/envoyee"), ["main", "feature/envoyee", "feature/locale"], "main");

        Assert.True(project.LastAction!.Succeeded, project.LastAction.Message);
        Assert.Contains("ici et sur GitHub", project.LastAction.Message);
        Assert.Equal(["api", "--method", "POST", "repos/SmartDev-Web/stock/branches/feature/envoyee/rename", "-f", "new_name=feature/nouveau-nom"],
            host.Runner.Single("api", "--method", "POST").Arguments);
        Assert.Equal("origin/feature/nouveau-nom", await _repo.Git("rev-parse", "--abbrev-ref", "feature/nouveau-nom@{upstream}"));
        Assert.DoesNotContain("remote set-head", project.LastAction.Commands); // ce n'est pas la branche principale
        Assert.DoesNotContain("origin/feature/envoyee", (await _repo.Git("branch", "-r", "--format=%(refname:short)")).Split('\n')); // ancien nom oublié
    }

    [Fact]
    public async Task The_main_and_current_branches_cannot_be_deleted()
    {
        var (host, project) = await ProjectAsync();

        await project.DeleteBranchAsync(Branch(project, "main"), "main");
        Assert.Contains("branche principale", project.LastAction!.Message);

        await _repo.Git("switch", "-q", "feature/locale");
        await project.LoadDetailsAsync();
        await project.DeleteBranchAsync(Branch(project, "feature/locale"), "main");
        Assert.Contains("passe d'abord sur une autre branche", project.LastAction!.Message);
        Assert.Empty(host.Confirms);
    }

    [Fact]
    public async Task Deleting_warns_about_work_that_would_be_lost()
    {
        var (host, project) = await ProjectAsync();

        await project.DeleteBranchAsync(Branch(project, "feature/locale"), "main");

        Assert.Contains("ATTENTION : 1 commit de cette branche n'est pas dans « main » ni sur GitHub et sera perdu", Assert.Single(host.Confirms));
        Assert.DoesNotContain("feature/locale", (await Branches()).Split('\n'));
    }

    [Fact]
    public async Task Deleting_on_github_is_never_the_default_choice()
    {
        var (host, project) = await ProjectAsync();

        host.ChooseAnswer = 0; // « Seulement sur ce poste (recommandé) »
        await project.DeleteBranchAsync(Branch(project, "feature/envoyee"), "main");
        Assert.Contains("rien n'est perdu", host.Confirms[^1]); // son travail est encore sur GitHub
        Assert.DoesNotContain("feature/envoyee", (await Branches()).Split('\n'));
        Assert.Contains("feature/envoyee", (await Branches(_server)).Split('\n'));

        await project.LoadDetailsAsync();
        await project.DeleteBranchAsync(Branch(project, "feature/envoyee"), "main"); // ne reste que sur GitHub
        Assert.Contains("sur GitHub ?", host.Confirms[^1]);
        Assert.Contains("ATTENTION : 1 commit", host.Confirms[^1]);
        Assert.True(project.LastAction!.Succeeded, project.LastAction.Message);
        Assert.DoesNotContain("feature/envoyee", (await Branches(_server)).Split('\n'));
    }

    [Fact]
    public async Task Creates_a_branch_from_an_older_commit_and_pushes_a_branch_that_is_not_the_current_one()
    {
        var (host, project) = await ProjectAsync();
        var first = await _repo.Git("rev-list", "--max-parents=0", "HEAD");
        host.PromptAnswers.Enqueue("repartir-de-zero");

        await project.CreateBranchFromAsync(first, first[..7]);
        Assert.Contains($"Branche « repartir-de-zero » créée à partir du commit {first[..7]}", project.LastAction!.Message);
        Assert.Contains("Tu es maintenant sur « repartir-de-zero »", project.LastAction.Message);
        Assert.Equal(first, await _repo.Git("rev-parse", "HEAD"));

        await project.PushBranchAsync(Branch(project, "feature/locale"));
        Assert.True(project.LastAction!.Succeeded, project.LastAction.Message);
        Assert.Equal(await _repo.Git("rev-parse", "feature/locale"), await _repo.GitIn(_server, "rev-parse", "feature/locale"));
        Assert.Equal("origin/feature/locale", await _repo.Git("rev-parse", "--abbrev-ref", "feature/locale@{upstream}"));
    }

    [Fact]
    public async Task The_large_history_runs_branch_actions_and_shows_the_report()
    {
        var (host, project) = await ProjectAsync();
        var tab = new UnlinkedTabViewModel { History = new HistoryViewModel(host) };
        tab.SetProjects([project]);
        tab.SelectedProject = project;
        await TestHost.WaitUntil(() => tab.History.Rows.Count > 0);
        tab.History.OpenLargeCommand.Execute(null);
        var explorer = tab.Explorer!;
        await TestHost.WaitUntil(() => explorer.History.Branches.Count == 3);
        Assert.True(explorer.CanAct);

        var locale = explorer.History.Branches.Single(b => b.Name == "feature/locale");
        Assert.True(locale is { CanSwitch: true, CanIntegrate: true, IntegrateLabel: "Intégrer dans main", CanPush: true, PushLabel: "Envoyer sur GitHub", CanDelete: true });
        Assert.False(explorer.History.Branches.Single(b => b.Name == "main").CanDelete);

        await explorer.SwitchBranchCommand.ExecuteAsync(locale);
        Assert.Contains("Tu es maintenant sur « feature/locale »", explorer.LastAction!.Message);
        await TestHost.WaitUntil(() => explorer.History.Branches.Any(b => b is { Name: "feature/locale", IsCurrent: true }));
    }

    public void Dispose() => _repo.Dispose();
}
