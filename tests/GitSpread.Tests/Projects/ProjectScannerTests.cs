using GitSpread.Core.Accounts;
using GitSpread.Core.Projects;
using GitSpread.Core.Ssh;

namespace GitSpread.Tests.Projects;

public sealed class ProjectScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GitSpread scan éà ").FullName;

    private string Repo(string relativePath, params (string Name, string Url)[] remotes)
    {
        var dir = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        var config = "[core]\n\trepositoryformatversion = 0\n"
            + string.Concat(remotes.Select(r => $"[remote \"{r.Name}\"]\n\turl = {r.Url}\n\tfetch = +refs/heads/*:refs/remotes/{r.Name}/*\n"))
            + "[branch \"main\"]\n\tremote = origin\n";
        File.WriteAllText(Path.Combine(dir, ".git", "config"), config);
        return dir;
    }

    private ScanResult Scan(int depth = 4) => new ProjectScanner().Scan(new ScanOptions([_root]) { MaxDepth = depth }, SshConfig.Empty);

    [Fact]
    public void Finds_repositories_and_reads_their_origin()
    {
        Repo("SmartStock", ("origin", "https://github.com/SamuelnitraM/SmartStock.git"));
        Repo(@"clients\Projet avec espaces", ("origin", "git@github.com:SmartDev-Web/site.git"));
        Directory.CreateDirectory(Path.Combine(_root, "pas-un-depot"));

        var projects = Scan().Projects;

        Assert.Equal(["Projet avec espaces", "SmartStock"], projects.Select(p => p.Name));
        Assert.Equal("SmartDev-Web/site", projects[0].Remote!.FullName);
        Assert.Equal("origin", projects[1].RemoteName);
    }

    [Fact]
    public void Falls_back_to_the_first_remote_when_there_is_no_origin()
    {
        Repo("fork", ("upstream", "https://github.com/autre/projet.git"), ("perso", "https://github.com/moi/projet.git"));

        var project = Assert.Single(Scan().Projects);

        Assert.Equal("upstream", project.RemoteName);
        Assert.Equal(2, project.RemoteCount);
    }

    [Fact]
    public void Project_without_remote_is_still_listed()
    {
        Repo("local-only");

        var project = Assert.Single(Scan().Projects);

        Assert.Null(project.Remote);
    }

    [Fact]
    public void Skips_dependencies_hidden_folders_and_nested_repositories()
    {
        Repo("app", ("origin", "https://github.com/moi/app.git"));
        Repo(@"app\sous-module", ("origin", "https://github.com/moi/lib.git"));
        Repo(@"site\node_modules\paquet", ("origin", "https://github.com/x/paquet.git"));
        Repo(@"site\vendor\lib", ("origin", "https://github.com/x/lib.git"));
        Repo(@".cache\depot", ("origin", "https://github.com/x/cache.git"));

        Assert.Equal(["app"], Scan().Projects.Select(p => p.Name));
    }

    [Fact]
    public void Respects_the_maximum_depth()
    {
        Repo(@"a\b\c\profond", ("origin", "https://github.com/moi/profond.git")); // 4 niveaux sous la racine

        Assert.Empty(Scan(depth: 3).Projects);
        Assert.Single(Scan(depth: 4).Projects);
    }

    [Fact]
    public void Follows_worktree_gitdir_files_to_the_main_config()
    {
        var main = Repo("principal", ("origin", "https://github.com/moi/principal.git"));
        var worktreeGitDir = Path.Combine(main, ".git", "worktrees", "wt");
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "wt")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}");

        var wt = Scan().Projects.Single(p => p.Name == "wt");

        Assert.Equal("moi/principal", wt.Remote!.FullName);
    }

    [Fact]
    public void Reports_missing_root_folders()
    {
        var missing = Path.Combine(_root, "n-existe-pas");

        var result = new ProjectScanner().Scan(new ScanOptions([missing]), SshConfig.Empty);

        Assert.Equal([missing], result.MissingRoots);
    }

    [Fact]
    public void Includes_projects_added_by_hand_even_without_git()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "ailleurs", "Projet sans git")).FullName;
        var repo = Repo(Path.Combine("ailleurs", "avec git"), ("origin", "https://github.com/moi/x.git"));
        var missing = Path.Combine(_root, "disparu");

        var result = new ProjectScanner().Scan(new ScanOptions([]) { ExtraProjects = [elsewhere, repo + "\\", missing] }, SshConfig.Empty);

        var plain = result.Projects.Single(p => p.Name == "Projet sans git");
        Assert.False(plain.IsRepository);
        Assert.True(plain.IsExtra);
        var git = result.Projects.Single(p => p.Name == "avec git");
        Assert.True(git.IsRepository);
        Assert.Equal("moi/x", git.Remote!.FullName);
        Assert.Equal([missing], result.MissingRoots);
    }

    [Fact]
    public void A_project_both_scanned_and_added_by_hand_appears_once()
    {
        var repo = Repo("doublon", ("origin", "https://github.com/moi/x.git"));

        var result = new ProjectScanner().Scan(new ScanOptions([_root]) { ExtraProjects = [repo] }, SshConfig.Empty);

        Assert.True(Assert.Single(result.Projects).IsExtra);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public class ProjectClassifierTests
{
    private static readonly GitHubAccount Perso = new("SamuelnitraM", "github.com", false, true);
    private static readonly GitHubAccount Pro = new("SmartDev-Web", "github.com", true, true);

    private static LocalProject Project(string? url) => new(@"C:\p")
    {
        Remote = url is null ? null : GitSpread.Core.Git.RemoteUrlParser.Parse(url),
    };

    [Theory]
    [InlineData("https://github.com/SamuelnitraM/a.git", "SamuelnitraM")]
    [InlineData("git@github.com:smartdev-web/b.git", "SmartDev-Web")] // insensible à la casse
    public void Places_projects_in_the_owner_account(string url, string expected) =>
        Assert.Equal(expected, ProjectClassifier.Place(Project(url), [Perso, Pro]).Account?.Login);

    [Fact]
    public void Folders_without_git_are_flagged() =>
        Assert.Equal(UnlinkedReason.NotARepository,
            ProjectClassifier.Place(new LocalProject(@"C:\p") { IsRepository = false }, [Perso, Pro]).Reason);

    [Theory]
    [InlineData(null, UnlinkedReason.NoRemote)]
    [InlineData("https://gitlab.com/moi/a.git", UnlinkedReason.OtherHost)]
    [InlineData(@"C:\depots\a.git", UnlinkedReason.OtherHost)]
    [InlineData("https://github.com/une-organisation/a.git", UnlinkedReason.OtherOwner)]
    public void Everything_else_goes_to_the_unlinked_tab(string? url, UnlinkedReason reason)
    {
        var placement = ProjectClassifier.Place(Project(url), [Perso, Pro]);

        Assert.Null(placement.Account);
        Assert.Equal(reason, placement.Reason);
    }
}

public class GitConfigValueTests
{
    [Theory]
    [InlineData(@"C:\\Users\\sam\\serveur.git", @"C:\Users\sam\serveur.git")]
    [InlineData(@"""C:\\Mes projets\\a.git""", @"C:\Mes projets\a.git")]
    [InlineData("https://github.com/moi/x.git", "https://github.com/moi/x.git")]
    public void Unescapes_values_like_git(string raw, string expected) =>
        Assert.Equal(expected, ProjectScanner.UnescapeConfigValue(raw));

    [Fact]
    public async Task Reads_the_same_url_as_git_for_a_local_path_remote()
    {
        using var repo = new Fakes.TempRepo("config");
        await repo.WithCommitAsync();
        var target = Path.Combine(repo.Root, "serveur avec espace.git");
        await repo.Git("remote", "add", "origin", target);

        Assert.Equal(target, ProjectScanner.ReadProject(repo.Path)!.Remote!.Url);
    }
}
