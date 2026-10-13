using GitSpread.Core.Git;
using GitSpread.Core.Processes;

namespace GitSpread.Tests.Git;

public class GitStatusParserTests
{
    [Fact]
    public void Parses_branch_tracking_and_changes()
    {
        var status = GitService.ParseStatus("""
            # branch.oid 4f2a1c0
            # branch.head main
            # branch.upstream origin/main
            # branch.ab +2 -3
            1 M. N... 100644 100644 100644 a b src/staged.cs
            1 .M N... 100644 100644 100644 a b src/modified.cs
            1 MM N... 100644 100644 100644 a b src/both.cs
            2 R. N... 100644 100644 100644 a b R100 new.cs	old.cs
            u UU N... 100644 100644 100644 100644 a b c conflict.cs
            ? untracked.txt
            ? autre.txt
            """);

        Assert.Equal("main", status.Branch);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal((2, 3), (status.Ahead, status.Behind));
        Assert.Equal(3, status.Staged);
        Assert.Equal(2, status.Modified);
        Assert.Equal(1, status.Conflicts);
        Assert.Equal(2, status.Untracked);
        Assert.False(status.IsClean);
    }

    [Fact]
    public void Detects_detached_head_and_empty_repositories()
    {
        Assert.True(GitService.ParseStatus("# branch.oid abc\n# branch.head (detached)\n").IsDetached);

        var empty = GitService.ParseStatus("# branch.oid (initial)\n# branch.head main\n");
        Assert.False(empty.HasCommits);
        Assert.True(empty.IsClean);
        Assert.Null(empty.Upstream);
    }
}

/// <summary>Contre un vrai dépôt git temporaire.</summary>
public sealed class GitServiceTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("GitSpread repo éà ").FullName;
    private readonly ProcessRunner _runner = new();
    private readonly GitService _git;

    public GitServiceTests() => _git = new GitService(_runner);

    private async Task Git(params string[] args)
    {
        var r = await _runner.RunAsync(new ProcessRequest("git", ["-c", "user.name=Test", "-c", "user.email=test@gitspread.local", .. args]) { WorkingDirectory = _repo });
        Assert.True(r.Succeeded, r.StandardError);
    }

    [Fact]
    public async Task Reads_status_last_commit_identity_and_remotes()
    {
        await Git("init", "-q", "-b", "main");
        Assert.False((await _git.GetStatusAsync(_repo)).HasCommits);
        Assert.Null(await _git.GetLastCommitAsync(_repo));

        File.WriteAllText(Path.Combine(_repo, "a.txt"), "a");
        await Git("add", ".");
        await Git("commit", "-q", "-m", "Premier commit éà");
        await Git("config", "user.email", "perso@exemple.fr");
        await Git("remote", "add", "origin", "https://github.com/SamuelnitraM/SmartStock.git");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "modifié");
        File.WriteAllText(Path.Combine(_repo, "nouveau fichier.txt"), "b");

        var status = await _git.GetStatusAsync(_repo);
        var commit = await _git.GetLastCommitAsync(_repo);
        var identity = await _git.GetIdentityAsync(_repo);
        var remotes = await _git.GetRemotesAsync(_repo);

        Assert.Equal("main", status.Branch);
        Assert.Equal((1, 1), (status.Modified, status.Untracked));
        Assert.Equal("Premier commit éà", commit!.Subject);
        Assert.Equal("Test", commit.Author);
        Assert.Equal("perso@exemple.fr", identity.Email);
        Assert.Equal([new GitRemote("origin", "https://github.com/SamuelnitraM/SmartStock.git")], remotes);
    }

    [Fact]
    public async Task Explains_when_the_folder_is_not_a_repository()
    {
        await Assert.ThrowsAsync<GitCommandException>(() => _git.GetStatusAsync(_repo));
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException) { }
    }
}

public class GeneratedFolderTests
{
    [Theory]
    [InlineData("node_modules/", true)]
    [InlineData("web/vendor/", true)]
    [InlineData("bin/", true)]
    [InlineData("src/", false)]
    [InlineData("node_modules", false)] // fichier, pas dossier
    public void Flags_dependency_and_build_folders(string path, bool expected) =>
        Assert.Equal(expected, new FileChange(path, ChangeKind.Untracked).IsLikelyGenerated);

    [Fact]
    public void Tracked_changes_are_never_flagged() =>
        Assert.False(new FileChange("vendor/", ChangeKind.Modified).IsLikelyGenerated);
}
