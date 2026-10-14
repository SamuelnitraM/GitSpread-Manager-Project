using GitSpread.Core.Git;
using GitSpread.Core.Processes;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Git;

/// <summary>F6a : lecture de l'historique et mise en page du graphe des branches.</summary>
public sealed class HistoryTests : IDisposable
{
    private readonly TempRepo _repo = new("historique");
    private readonly GitService _git = new(new ProcessRunner());

    private static HistoryCommit C(string sha, params string[] parents) =>
        new(sha, parents, "Sam", "sam@x", DateTimeOffset.Now, $"commit {sha}", []);

    private static HistoryCommit Merge(string sha, string branch, params string[] parents) => C(sha, parents) with { Subject = $"Merge branch '{branch}'" };

    [Theory]
    [InlineData("main", BranchKind.Default)]
    [InlineData("feature/login", BranchKind.Classic)]
    [InlineData("version-2", BranchKind.Classic)]
    [InlineData("fix/paiement", BranchKind.Maintenance)]
    [InlineData("hotfix-2.1", BranchKind.Maintenance)]
    [InlineData("debugs", BranchKind.Maintenance)]
    [InlineData("Bugfix_typo", BranchKind.Maintenance)]
    [InlineData("prefix-tool", BranchKind.Classic)] // « fix » ailleurs que dans le premier mot ne compte pas
    public void Branch_kind_comes_from_the_first_word_of_its_name(string name, BranchKind kind) =>
        Assert.Equal(kind, BranchKinds.Of(name, "main"));

    [Fact]
    public void A_branch_merged_back_gets_its_own_lane_and_colour()
    {
        // m3 = fusion de feature (f1) dans main (m2) ; f1 part de m1.
        //   m3
        //   |\
        //   m2 f1
        //   |/
        //   m1
        var commits = new[] { Merge("m3", "feature", "m2", "f1"), C("f1", "m1"), C("m2", "m1"), C("m1") };

        var layout = HistoryGraph.Layout(commits, [("main", "m3")], "main");

        Assert.Equal("main", layout.CommitBranch["m2"]);
        Assert.Equal("feature", layout.CommitBranch["f1"]); // retrouvée grâce au message de fusion
        Assert.Equal([0, 1, 0, 0], layout.Rows.Select(r => r.NodeLane));
        Assert.Equal(new BranchColor(BranchKind.Default, 0), layout.Rows[0].NodeColor);
        Assert.Equal(BranchKind.Classic, layout.Rows[1].NodeColor.Kind);
        Assert.Contains(new GraphSegment(0, 1, layout.Rows[1].NodeColor), layout.Rows[0].Bottom); // départ du trait de la branche fusionnée
        Assert.Contains(layout.Rows[3].Top, s => s.FromLane == 1 && s.ToLane == 0); // la branche rejoint son point de départ
        Assert.Equal(2, layout.MaxLanes);
    }

    [Fact]
    public void Each_branch_of_the_same_family_has_a_different_shade()
    {
        var commits = new[] { C("a", "r"), C("b", "r"), C("c", "r"), C("r") };

        var layout = HistoryGraph.Layout(commits, [("main", "r"), ("feature/a", "a"), ("fix/b", "b"), ("feature/c", "c")], "main");

        Assert.Equal(new BranchColor(BranchKind.Classic, 0), layout.BranchColors["feature/a"]);
        Assert.Equal(new BranchColor(BranchKind.Classic, 1), layout.BranchColors["feature/c"]);
        Assert.Equal(new BranchColor(BranchKind.Maintenance, 0), layout.BranchColors["fix/b"]);
        // Chaque branche garde son couloir jusqu'à son point de départ, où les trois traits se rejoignent.
        Assert.Equal([0, 1, 2, 0], layout.Rows.Select(r => r.NodeLane));
        Assert.Equal(3, layout.Rows[3].Top.Count(s => s.ToLane == 0));
        Assert.Contains(new GraphSegment(2, 0, layout.BranchColors["feature/c"]), layout.Rows[3].Top);
    }

    [Theory]
    [InlineData("Merge branch 'feature/x'", "feature/x")]
    [InlineData("Merge branch 'fix' into main", "fix")]
    [InlineData("Merge remote-tracking branch 'origin/develop'", "develop")]
    [InlineData("Merge pull request #12 from SamuelnitraM/hotfix-1", "hotfix-1")]
    [InlineData("Ajoute le login", null)]
    public void The_merged_branch_is_read_from_the_merge_message(string subject, string? branch) =>
        Assert.Equal(branch, BranchKinds.MergedBranch(subject));

    [Fact]
    public async Task Reads_the_real_history_branches_and_commit_details()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("switch", "-q", "-c", "fix/typo");
        await _repo.Git("mv", "README.md", "LISEZMOI.md");
        _repo.Write("corrige.txt", "Corrigé\n");
        await _repo.Git("add", "corrige.txt");
        await _repo.Git("commit", "-q", "-m", "Corrige la typo");
        await _repo.Git("switch", "-q", "main");
        await _repo.CommitFileAsync("app.txt", "ligne 1\nligne 2\n", "Ajoute app");
        await _repo.Git("merge", "-q", "--no-ff", "fix/typo", "-m", "Merge branch 'fix/typo'");

        var history = await _git.ReadHistoryAsync(_repo.Path);
        Assert.Equal(4, history.Count);
        Assert.True(history[0] is { IsMerge: true, IsHead: true });
        Assert.Contains("main", history[0].Refs);
        Assert.Contains("fix/typo", history.Single(c => c.Subject == "Corrige la typo").Refs);

        var branches = await _git.GetBranchOverviewAsync(_repo.Path, "main");
        Assert.True(branches[0] is { Name: "main", IsDefault: true, NotInDefault: null });
        Assert.True(branches[1] is { Name: "fix/typo", NotInDefault: 0, Subject: "Corrige la typo" }); // déjà intégrée

        var fix = history.Single(c => c.Subject == "Corrige la typo");
        var details = await _git.GetCommitDetailsAsync(_repo.Path, fix.Sha);
        Assert.Equal(["fix/typo", "main"], details.Branches.Order());
        Assert.Equal(2, details.Files.Count);
        var renamed = details.Files.Single(f => f.StatusLetter == "R");
        Assert.Equal(("LISEZMOI.md", "README.md"), (renamed.Path, renamed.OldPath));
        var added = details.Files.Single(f => f.StatusLetter == "A");
        var diff = await _git.GetFileDiffAsync(_repo.Path, fix, added);
        Assert.Contains(diff, l => l is { Kind: DiffLineKind.Added, Text: "Corrigé" });

        var root = history[^1];
        var first = await _git.GetCommitDetailsAsync(_repo.Path, root.Sha);
        Assert.Equal("README.md", Assert.Single(first.Files).Path);
        Assert.Equal([new DiffLine(DiffLineKind.Hunk, "@@ -0,0 +1 @@"), new DiffLine(DiffLineKind.Added, "# Projet")],
            await _git.GetFileDiffAsync(_repo.Path, root, first.Files[0]));
    }

    [Fact]
    public async Task The_remote_default_pointer_is_not_listed_as_a_branch()
    {
        var bare = await _repo.BareAsync("serveur.git");
        await _repo.WithCommitAsync();
        await _repo.Git("push", "-q", bare, "main");
        var clone = System.IO.Path.Combine(_repo.Root, "clone");
        await _repo.GitIn(_repo.Root, "clone", "-q", bare, clone); // crée origin/HEAD

        var head = (await _git.ReadHistoryAsync(clone)).Single();
        var details = await _git.GetCommitDetailsAsync(clone, head.Sha);

        Assert.Equal(["main", "origin/main"], details.Branches.Order());
        Assert.Equal(["HEAD", "main", "origin/main"], head.Refs.Order());
    }

    [Fact]
    public async Task An_empty_repository_has_no_history()
    {
        await _repo.Git("init", "-q", "-b", "main");
        Assert.Empty(await _git.ReadHistoryAsync(_repo.Path));
    }

    public void Dispose() => _repo.Dispose();
}
