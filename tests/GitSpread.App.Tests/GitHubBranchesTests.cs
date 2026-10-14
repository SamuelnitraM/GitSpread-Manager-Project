using GitSpread.App.ViewModels;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitSpread.App.Tests;

/// <summary>F6c : section « Branches » du panneau d'un repo GitHub, et sections repliables.</summary>
public class GitHubBranchesTests
{
    private static readonly GitHubRepository Stock = new("stock", "SmartDev-Web", "https://github.com/SmartDev-Web/stock", "", RepositoryVisibility.Private);

    private const string Branches = """
        {"data":{"repository":{"refs":{"nodes":[
          {"name":"fix/paiement","target":{"oid":"a1","committedDate":"2026-10-14T08:00:00Z","messageHeadline":"Corrige le paiement","author":{"name":"Sam"}},"compare":{"aheadBy":2,"behindBy":1}},
          {"name":"main","target":{"oid":"m1","committedDate":"2026-10-13T08:00:00Z","messageHeadline":"Version 1","author":{"name":"Sam"}},"compare":{"aheadBy":0,"behindBy":0}},
          {"name":"feature/ancienne","target":{"oid":"f1","committedDate":"2026-09-01T08:00:00Z","messageHeadline":"Vieille idée","author":null},"compare":{"aheadBy":5,"behindBy":0}}
        ]}}}}
        """;

    private static async Task<(TestHost Host, GitHubRepositoryViewModel Panel)> PanelAsync(string permission = "ADMIN")
    {
        var host = new TestHost();
        host.Runner.When(["repo", "view"], $$"""
                {"name":"stock","owner":{"login":"SmartDev-Web"},"visibility":"PRIVATE","defaultBranchRef":{"name":"main"},"repositoryTopics":[],
                 "isArchived":false,"isFork":false,"url":"https://github.com/SmartDev-Web/stock","viewerPermission":"{{permission}}"}
                """)
            .When(["api"], "[]")
            .When(["api", "graphql"], Branches)
            .When(["api", "--method", "POST"], "{}")
            .When(["api", "--method", "DELETE"], "");
        var panel = new GitHubRepositoryViewModel(new AccountTabViewModel(TestHost.Pro, host, NullLogger.Instance), Stock, host, NullLogger.Instance);
        await panel.LoadAsync();
        return (host, panel);
    }

    [Fact]
    public async Task Lists_github_branches_with_their_situation_and_colour()
    {
        var (host, panel) = await PanelAsync();

        Assert.Equal(["main", "fix/paiement", "feature/ancienne"], panel.Branches.Select(b => b.Name)); // la principale d'abord
        Assert.Equal("3 branches :", panel.BranchesLabel);
        Assert.True(panel.Branches[0] is { IsDefault: true, StateLabel: "branche principale", CanDelete: false, Color.Kind: BranchKind.Default });
        Assert.Equal("1 commit pas encore dans main · main a 2 commits de plus", panel.Branches[1].StateLabel);
        Assert.Equal(BranchKind.Maintenance, panel.Branches[1].Color.Kind);
        Assert.Equal("déjà intégrée dans main · main a 5 commits de plus", panel.Branches[2].StateLabel);
        Assert.True(panel.CanEditBranches);
        Assert.Contains("base=main", host.Runner.Single("api", "graphql").Arguments);
    }

    [Fact]
    public async Task Renames_and_deletes_a_branch_on_github()
    {
        var (host, panel) = await PanelAsync();

        host.PromptAnswers.Enqueue("fix/paiement-v2");
        await panel.RenameBranchCommand.ExecuteAsync(panel.Branches.Single(b => b.Name == "fix/paiement"));
        Assert.Equal(["api", "--method", "POST", "repos/SmartDev-Web/stock/branches/fix/paiement/rename", "-f", "new_name=fix/paiement-v2"],
            host.Runner.Single("api", "--method", "POST").Arguments);

        await panel.DeleteBranchCommand.ExecuteAsync(panel.Branches.Single(b => b.Name == "fix/paiement"));
        Assert.Contains("ATTENTION : 1 commit de cette branche n'est pas dans « main »", host.Confirms[^1]);
        Assert.Equal(["api", "--method", "DELETE", "repos/SmartDev-Web/stock/git/refs/heads/fix/paiement"],
            host.Runner.Single("api", "--method", "DELETE").Arguments);
        Assert.True(panel.LastAction!.Succeeded, panel.LastAction.Message);

        await panel.DeleteBranchCommand.ExecuteAsync(panel.Branches.Single(b => b.Name == "main"));
        Assert.Contains("branche principale", panel.LastAction!.Message);
    }

    [Fact]
    public async Task Read_only_access_cannot_edit_branches()
    {
        var (_, panel) = await PanelAsync(permission: "READ");
        Assert.False(panel.CanEditBranches);
    }

    [Fact]
    public void Sections_remember_their_state_and_the_sensitive_zone_starts_collapsed()
    {
        var host = new TestHost();
        var sections = new PanelSections(host);

        Assert.True(sections is { RepoDaily: true, RepoBranches: true, Links: true });
        Assert.False(sections.RepoDanger);
        Assert.True(sections is { HistoryCommit: true, HistoryFiles: true, HistoryDiff: true, LinkRows: true, OtherBranches: true, ActionCommands: true });
        Assert.False(sections.ActionOutput); // la sortie brute des commandes est repliée par défaut

        sections.Links = false;
        sections.RepoDanger = true;
        Assert.Equal(new Dictionary<string, bool> { ["Links"] = false, ["RepoDanger"] = true }, host.Settings.PanelSections);
        Assert.True(new PanelSections(host) is { Links: false, RepoDanger: true }); // vaut pour tous les projets et repos
    }
}
