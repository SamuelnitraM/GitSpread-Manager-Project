using GitSpread.Core.Git;
using GitSpread.Core.Processes;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Git;

/// <summary>Branches, changement de branche, mise de côté et intégration, contre de vrais dépôts.</summary>
public sealed class BranchTests : IDisposable
{
    private readonly TempRepo _repo = new("branches");
    private readonly GitService _git = new(new ProcessRunner());
    private readonly BranchIntegrator _integrator;
    private static readonly IReadOnlyDictionary<string, string> NoAuth = GitCredentialEnvironment.NonInteractive();

    public BranchTests() => _integrator = new BranchIntegrator(_git);

    private string P => _repo.Path;
    private Task<string> Head(string branch) => _repo.Git("rev-parse", branch);
    private async Task<string> Current() => (await _git.GetStatusAsync(P)).Branch!;

    /// <summary>main avec un commit, puis une branche « feature » avec un commit de plus.</summary>
    private async Task FeatureAheadOfMain()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("switch", "-q", "-c", "feature");
        await _repo.CommitFileAsync("feature.txt", "f", "travail sur feature");
    }

    // ------------------------------------------------------------ liste et changement de branche

    [Fact]
    public async Task Lists_local_remote_and_remote_only_branches()
    {
        var bare = await _repo.BareAsync();
        await FeatureAheadOfMain();
        await _git.AddRemoteAsync(P, "origin", bare);
        await _repo.Git("push", "-q", "-u", "origin", "main", "feature");
        await _repo.Git("push", "-q", "origin", "feature:distante");
        await _repo.Git("fetch", "-q", "origin");
        await _repo.CommitFileAsync("plus.txt", "p", "en avance");

        var branches = await _git.ListBranchesAsync(P);

        var feature = branches.Single(b => b.Name == "feature");
        Assert.True(feature is { IsCurrent: true, IsLocal: true, Upstream: "origin/feature", Ahead: 1, Behind: 0, RemoteRef: "origin/feature" });
        Assert.True(branches.Single(b => b.Name == "distante") is { IsRemoteOnly: true, Ref: "origin/distante" });
        Assert.Equal("feature", branches[0].Name); // la branche courante en premier
        Assert.DoesNotContain(branches, b => b.Name == "HEAD");
    }

    [Fact]
    public async Task Default_branch_comes_from_the_remote_head_or_falls_back_to_main()
    {
        await FeatureAheadOfMain();
        Assert.Equal("main", await _git.GetDefaultBranchAsync(P));

        var bare = await _repo.BareAsync();
        await _repo.Git("push", "-q", bare, "main", "feature");
        var clone = Path.Combine(_repo.Root, "clone");
        await _repo.GitIn(_repo.Root, "clone", "-q", "-b", "feature", bare, clone);
        Assert.Equal("main", await _git.GetDefaultBranchAsync(clone)); // origin/HEAD → main
    }

    [Fact]
    public async Task Switching_to_a_remote_only_branch_creates_a_tracking_branch()
    {
        var bare = await _repo.BareAsync();
        await FeatureAheadOfMain();
        await _git.AddRemoteAsync(P, "origin", bare);
        await _repo.Git("push", "-q", "origin", "feature:collegue");
        await _repo.Git("fetch", "-q", "origin");

        var remoteOnly = (await _git.ListBranchesAsync(P)).Single(b => b.Name == "collegue");
        var result = await _git.SwitchAsync(P, remoteOnly);

        Assert.True(result.Succeeded, result.Output);
        Assert.Equal("collegue", await Current());
        Assert.Equal("origin/collegue", (await _git.GetStatusAsync(P)).Upstream);
    }

    [Fact]
    public async Task Conflicting_changes_block_the_switch_and_can_be_put_aside_then_restored()
    {
        await FeatureAheadOfMain();
        await _repo.Git("switch", "-q", "main");
        await _repo.CommitFileAsync("feature.txt", "version main", "même fichier sur main");
        await _repo.Git("switch", "-q", "feature");
        _repo.Write("feature.txt", "modif en cours non commitée");

        var blocked = await _git.SwitchAsync(P, "main");
        Assert.False(blocked.Succeeded);
        Assert.Contains("Mets-les de côté", blocked.Explanation);

        Assert.True((await _git.StashAsync(P, "feature")).Succeeded);
        Assert.True((await _git.SwitchAsync(P, "main")).Succeeded);
        await _git.SwitchAsync(P, "feature");

        var stash = await _git.FindStashForBranchAsync(P, "feature");
        Assert.NotNull(stash);
        Assert.True((await _git.PopStashAsync(P, stash)).Succeeded);
        Assert.Equal("modif en cours non commitée", File.ReadAllText(Path.Combine(P, "feature.txt")));
        Assert.Null(await _git.FindStashForBranchAsync(P, "feature"));
    }

    [Fact]
    public async Task Creates_branches_and_explains_invalid_names()
    {
        await _repo.WithCommitAsync();

        Assert.True((await _git.CreateBranchAsync(P, "correctif-stock")).Succeeded);
        Assert.Equal("correctif-stock", await Current());
        Assert.Equal("Une branche porte déjà ce nom.", (await _git.CreateBranchAsync(P, "main")).Explanation);
        Assert.Contains("Nom de branche invalide", (await _git.CreateBranchAsync(P, "nom avec espaces")).Explanation);
    }

    // ------------------------------------------------------------ intégration

    [Fact]
    public async Task Fast_forwards_main_without_leaving_the_feature_branch()
    {
        await FeatureAheadOfMain();

        var analysis = await _integrator.AnalyzeAsync(P, "feature", "main");
        Assert.Equal(IntegrationKind.FastForward, analysis.Kind);
        Assert.False(analysis.NeedsCheckout);
        Assert.Equal(1, analysis.SourceOnly);

        _repo.Write("en cours.txt", "pas commité"); // ne gêne pas : le dossier n'est pas touché
        var result = await _integrator.IntegrateAsync(P, analysis with { WorkingTreeDirty = true }, new IntegrationOptions());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(await Head("feature"), await Head("main"));
        Assert.Equal("feature", await Current());
        Assert.True(File.Exists(Path.Combine(P, "en cours.txt")));
        Assert.Contains(result.Steps, s => s.Command == "git fetch . feature:main");
    }

    [Fact]
    public async Task Fast_forwards_the_current_branch_with_a_merge()
    {
        await FeatureAheadOfMain();
        await _repo.Git("switch", "-q", "main");

        var analysis = await _integrator.AnalyzeAsync(P, "feature", "main");
        var result = await _integrator.IntegrateAsync(P, analysis, new IntegrationOptions());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(await Head("feature"), await Head("main"));
        Assert.Contains(result.Steps, s => s.Command == "git merge --ff-only feature");
    }

    [Fact]
    public async Task Diverged_branches_get_a_merge_commit_and_the_user_comes_back_to_their_branch()
    {
        await FeatureAheadOfMain();
        await _repo.Git("switch", "-q", "main");
        await _repo.CommitFileAsync("main.txt", "m", "travail sur main");
        await _repo.Git("switch", "-q", "feature");

        var analysis = await _integrator.AnalyzeAsync(P, "feature", "main");
        Assert.Equal(IntegrationKind.Merge, analysis.Kind);
        Assert.True(analysis.NeedsCheckout);

        var result = await _integrator.IntegrateAsync(P, analysis, new IntegrationOptions());

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("feature", await Current());
        Assert.Equal("Intègre feature dans main", await _repo.Git("log", "-1", "--format=%s", "main"));
        Assert.True(await _git.IsAncestorAsync(P, "feature", "main"));
    }

    [Fact]
    public async Task Updating_a_feature_branch_with_main_merges_in_place()
    {
        await FeatureAheadOfMain();
        await _repo.Git("switch", "-q", "main");
        await _repo.CommitFileAsync("main.txt", "m", "nouveauté sur main");
        await _repo.Git("switch", "-q", "feature");

        var analysis = await _integrator.AnalyzeAsync(P, "main", "feature");
        Assert.Equal(IntegrationKind.Merge, analysis.Kind);
        Assert.False(analysis.NeedsCheckout);

        var result = await _integrator.IntegrateAsync(P, analysis, new IntegrationOptions());

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(Path.Combine(P, "main.txt")));
        Assert.Equal("feature", await Current());
    }

    [Fact]
    public async Task Conflicts_are_announced_before_anything_is_touched()
    {
        await FeatureAheadOfMain();
        await _repo.Git("switch", "-q", "main");
        await _repo.CommitFileAsync("feature.txt", "autre contenu", "même fichier");
        await _repo.Git("switch", "-q", "feature");
        var mainBefore = await Head("main");

        var analysis = await _integrator.AnalyzeAsync(P, "feature", "main");
        Assert.Equal(IntegrationKind.Conflicts, analysis.Kind);
        Assert.Equal(["feature.txt"], analysis.ConflictingFiles);

        var result = await _integrator.IntegrateAsync(P, analysis, new IntegrationOptions());

        Assert.False(result.Succeeded);
        Assert.Equal(mainBefore, await Head("main"));
        Assert.Equal("feature", await Current());
        Assert.Equal(RepositoryOperation.None, await _git.GetOperationAsync(P));
    }

    [Fact]
    public async Task Conflicts_can_be_left_to_resolve_then_aborted_or_concluded()
    {
        await FeatureAheadOfMain();
        await _repo.Git("switch", "-q", "main");
        await _repo.CommitFileAsync("feature.txt", "autre contenu", "même fichier");
        await _repo.Git("switch", "-q", "feature");

        var analysis = await _integrator.AnalyzeAsync(P, "feature", "main");
        var result = await _integrator.IntegrateAsync(P, analysis, new IntegrationOptions { StartDespiteConflicts = true });

        Assert.True(result.ConflictsToResolve);
        Assert.Equal("main", await Current());
        Assert.Equal(RepositoryOperation.Merge, await _git.GetOperationAsync(P));
        Assert.Equal(IntegrationBlocker.OperationInProgress, (await _integrator.AnalyzeAsync(P, "feature", "main")).Blocker);

        // Abandon : tout redevient comme avant
        Assert.True((await _git.AbortOperationAsync(P, RepositoryOperation.Merge)).Succeeded);
        Assert.Equal(RepositoryOperation.None, await _git.GetOperationAsync(P));

        // Nouvelle tentative, résolue à la main puis terminée
        await _integrator.IntegrateAsync(P, await _integrator.AnalyzeAsync(P, "feature", "main"), new IntegrationOptions { StartDespiteConflicts = true });
        _repo.Write("feature.txt", "version résolue");
        var conclude = await _git.ConcludeMergeAsync(P);
        Assert.All(conclude, r => Assert.True(r.Succeeded, r.Output));
        Assert.Equal(RepositoryOperation.None, await _git.GetOperationAsync(P));
        Assert.True(await _git.IsAncestorAsync(P, "feature", "main"));
    }

    [Fact]
    public async Task Nothing_to_do_when_main_already_contains_the_branch()
    {
        await FeatureAheadOfMain();
        await _repo.Git("switch", "-q", "main");
        await _repo.Git("merge", "-q", "feature");

        var analysis = await _integrator.AnalyzeAsync(P, "feature", "main");

        Assert.Equal(IntegrationKind.NothingToDo, analysis.Kind);
        Assert.False(analysis.CanProceed);
        Assert.Contains("Rien à faire", analysis.Summary);
    }

    [Fact]
    public async Task Uncommitted_changes_are_put_aside_during_a_merge_then_restored()
    {
        await FeatureAheadOfMain();
        await _repo.Git("switch", "-q", "main");
        await _repo.CommitFileAsync("main.txt", "m", "travail sur main");
        await _repo.Git("switch", "-q", "feature");
        _repo.Write("brouillon.txt", "pas encore commité");

        var analysis = await _integrator.AnalyzeAsync(P, "feature", "main");
        Assert.Equal(IntegrationBlocker.DirtyWorkingTree, analysis.Blocker);

        Assert.False((await _integrator.IntegrateAsync(P, analysis, new IntegrationOptions { StashChanges = false })).Succeeded);

        var result = await _integrator.IntegrateAsync(P, analysis, new IntegrationOptions { StashChanges = true });
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("pas encore commité", File.ReadAllText(Path.Combine(P, "brouillon.txt")));
        Assert.Equal("feature", await Current());
        Assert.Null(await _git.FindStashForBranchAsync(P, "feature"));
    }

    [Fact]
    public async Task A_failed_merge_brings_the_user_back_to_their_branch_with_their_changes()
    {
        await _repo.WithCommitAsync(); // main
        await _repo.Git("switch", "-q", "--orphan", "etrangere"); // historique sans rapport : git refuse la fusion
        await _repo.CommitFileAsync("autre.txt", "a", "Autre histoire");
        await _repo.Git("switch", "-q", "-c", "travail", "main");
        await _repo.CommitFileAsync("t.txt", "t", "travail");
        _repo.Write("brouillon.txt", "pas encore commité");

        var refused = await Assert.ThrowsAsync<GitCommandException>(() => _integrator.AnalyzeAsync(P, "etrangere", "main"));
        Assert.Contains("aucun commit en commun", refused.Message); // l'assistant l'affiche comme un blocage

        // Même échec au moment de fusionner (après être passé sur main) : retour sur la branche de départ.
        await _repo.Git("branch", "proche", "main");
        var analysis = await _integrator.AnalyzeAsync(P, "proche", "main");
        var result = await _integrator.IntegrateAsync(P, analysis with { Source = "etrangere", SourceRef = "etrangere", Kind = IntegrationKind.Merge, WorkingTreeDirty = true },
            new IntegrationOptions { StashChanges = true });

        Assert.False(result.Succeeded);
        Assert.Equal("travail", await Current()); // revenu sur sa branche…
        Assert.Equal("pas encore commité", File.ReadAllText(Path.Combine(P, "brouillon.txt"))); // …avec ses modifications
        Assert.Null(await _git.FindStashForBranchAsync(P, "travail"));
    }

    [Fact]
    public async Task Main_behind_github_is_caught_up_first_then_pushed()
    {
        var bare = await _repo.BareAsync();
        await _repo.WithCommitAsync();
        await _git.AddRemoteAsync(P, "origin", bare);
        await _git.PushAsync(P, "origin", "main", true, NoAuth);

        // Un collègue fait avancer main sur GitHub
        var other = Path.Combine(_repo.Root, "collegue");
        await _repo.GitIn(_repo.Root, "clone", "-q", bare, other);
        await _repo.CommitFileAsync("collegue.txt", "c", "commit du collègue", other);
        await _repo.GitIn(other, "push", "-q");

        await _repo.Git("switch", "-q", "-c", "feature");
        await _repo.CommitFileAsync("feature.txt", "f", "feature");
        await _git.FetchAsync(P, "origin", NoAuth);

        var analysis = await _integrator.AnalyzeAsync(P, "feature", "main");
        Assert.Equal(1, analysis.TargetBehindRemote);
        Assert.Equal(IntegrationKind.Merge, analysis.Kind); // origin/main et feature ont divergé

        var result = await _integrator.IntegrateAsync(P, analysis, new IntegrationOptions { PushTarget = true, DeleteSource = true },
            () => Task.FromResult(NoAuth));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(await Head("main"), await _repo.GitIn(bare, "rev-parse", "main"));
        Assert.Equal("c", await _repo.Git("show", "main:collegue.txt")); // main contient le travail du collègue…
        Assert.Equal("f", await _repo.Git("show", "main:feature.txt"));  // …et celui de feature
        Assert.Equal("feature", await Current());                         // et on est revenu sur feature
    }

    [Fact]
    public async Task Main_diverged_from_github_must_be_synchronised_first()
    {
        var bare = await _repo.BareAsync();
        await _repo.WithCommitAsync();
        await _git.AddRemoteAsync(P, "origin", bare);
        await _git.PushAsync(P, "origin", "main", true, NoAuth);
        var other = Path.Combine(_repo.Root, "collegue");
        await _repo.GitIn(_repo.Root, "clone", "-q", bare, other);
        await _repo.CommitFileAsync("collegue.txt", "c", "distant", other);
        await _repo.GitIn(other, "push", "-q");
        await _repo.CommitFileAsync("local.txt", "l", "local sur main");
        await _repo.Git("switch", "-q", "-c", "feature");
        await _repo.CommitFileAsync("feature.txt", "f", "feature");
        await _git.FetchAsync(P, "origin", NoAuth);

        var analysis = await _integrator.AnalyzeAsync(P, "feature", "main");

        Assert.Equal(IntegrationBlocker.TargetDivergedFromRemote, analysis.Blocker);
        Assert.False((await _integrator.IntegrateAsync(P, analysis, new IntegrationOptions())).Succeeded);
    }

    [Fact]
    public async Task A_branch_that_only_exists_on_github_can_be_integrated()
    {
        var bare = await _repo.BareAsync();
        await FeatureAheadOfMain();
        await _git.AddRemoteAsync(P, "origin", bare);
        await _repo.Git("push", "-q", "origin", "main", "feature:depuis-github");
        await _repo.Git("switch", "-q", "main");
        await _repo.Git("branch", "-q", "-D", "feature");
        await _repo.Git("fetch", "-q", "origin");

        var analysis = await _integrator.AnalyzeAsync(P, "depuis-github", "main");
        Assert.Equal("origin/depuis-github", analysis.SourceRef);

        var result = await _integrator.IntegrateAsync(P, analysis, new IntegrationOptions { DeleteSource = true });
        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(Path.Combine(P, "feature.txt")));
        Assert.DoesNotContain(result.Steps, s => s.Command.StartsWith("git branch -d")); // jamais sur une branche distante
    }

    [Fact]
    public void Preview_describes_every_step()
    {
        var analysis = new IntegrationAnalysis("feature", "main")
        {
            SourceRef = "feature", CurrentBranch = "feature", Kind = IntegrationKind.Merge,
            WorkingTreeDirty = true, TargetBehindRemote = 2, TargetUpstream = "origin/main",
        };

        var preview = BranchIntegrator.Preview(analysis, new IntegrationOptions { PushTarget = true });

        Assert.Equal("git stash push --include-untracked   (mise de côté de tes modifications)", preview[0]);
        Assert.Equal("git fetch . origin/main:main", preview[1]);
        Assert.Contains("git switch main", preview);
        Assert.Contains(preview, l => l.StartsWith("git merge --no-ff"));
        Assert.Contains("git push origin main", preview);
        Assert.Equal("git stash pop   (restauration de tes modifications)", preview[^1]);
    }

    public void Dispose() => _repo.Dispose();
}

public class BranchParserTests
{
    [Fact]
    public void Parses_tracking_information_and_gone_upstreams()
    {
        const string s = "\x1f";
        var output = string.Join('\n',
            $"refs/heads/main{s}main{s}origin/main{s}ahead 2, behind 3{s}*{s}2026-10-01T10:00:00+02:00",
            $"refs/heads/ancienne{s}ancienne{s}origin/ancienne{s}gone{s} {s}2026-09-01T10:00:00+02:00",
            $"refs/remotes/origin/HEAD{s}origin{s}{s}{s} {s}2026-10-01T10:00:00+02:00",
            $"refs/remotes/origin/main{s}origin/main{s}{s}{s} {s}2026-10-01T10:00:00+02:00",
            $"refs/remotes/upstream/main{s}upstream/main{s}{s}{s} {s}2026-10-01T10:00:00+02:00",
            $"refs/remotes/origin/collegue/correctif{s}origin/collegue/correctif{s}{s}{s} {s}2026-10-02T10:00:00+02:00");

        var branches = GitService.ParseBranches(output);

        Assert.True(branches[0] is { Name: "main", IsCurrent: true, Ahead: 2, Behind: 3, RemoteRef: "origin/main" });
        Assert.True(branches.Single(b => b.Name == "ancienne").UpstreamGone);
        Assert.True(branches.Single(b => b.Name == "collegue/correctif") is { IsRemoteOnly: true, Ref: "origin/collegue/correctif" });
        Assert.Equal(3, branches.Count);
    }
}
