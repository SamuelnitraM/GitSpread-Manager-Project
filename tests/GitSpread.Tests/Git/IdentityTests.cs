using GitSpread.Core.Git;
using GitSpread.Core.Processes;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Git;

/// <summary>F2 : identité propre au projet et re-signature des commits pas encore poussés.</summary>
public sealed class IdentityTests : IDisposable
{
    private readonly TempRepo _repo = new("identite");
    private readonly GitService _git = new(new ProcessRunner());
    private static readonly GitIdentity Perso = new("SamuelnitraM", "42+SamuelnitraM@users.noreply.github.com");
    private string P => _repo.Path;

    private Task<string> Authors(string range = "HEAD") => _repo.Git("log", "--format=%ae", range);

    [Fact]
    public async Task Distinguishes_the_project_identity_from_the_machine_identity()
    {
        await _repo.WithCommitAsync(); // identité locale « Test »

        var settings = await _git.GetIdentitySettingsAsync(P);
        Assert.Equal(new GitIdentity("Test", "test@gitspread.local"), settings.Local);
        Assert.True(settings.HasLocal);

        Assert.All(await _git.UnsetLocalIdentityAsync(P), r => Assert.True(r.Succeeded, r.Output));
        Assert.All(await _git.UnsetLocalIdentityAsync(P), r => Assert.True(r.Succeeded, r.Output)); // déjà retirée : pas une erreur

        settings = await _git.GetIdentitySettingsAsync(P);
        Assert.Null(settings.Local);
        Assert.Equal(settings.Global?.Email, settings.Effective.Email); // retour à l'identité du poste
    }

    [Fact]
    public async Task Only_commits_absent_from_every_remote_branch_are_unpushed()
    {
        var bare = await _repo.BareAsync();
        await _repo.WithCommitAsync("publié");
        await _git.AddRemoteAsync(P, "origin", bare);
        await _repo.Git("push", "-q", "-u", "origin", "main");
        await _repo.CommitFileAsync("a.txt", "a", "local 1");
        await _repo.Git("switch", "-q", "-c", "nouvelle"); // jamais poussée, mais partie de main déjà publié
        await _repo.CommitFileAsync("b.txt", "b", "local 2");

        var unpushed = await _git.ListUnpushedCommitsAsync(P);

        Assert.Equal(["local 1", "local 2"], unpushed.Select(c => c.Subject)); // du plus ancien au plus récent
        Assert.All(unpushed, c => Assert.Equal("test@gitspread.local", c.AuthorEmail));
    }

    [Fact]
    public async Task Rewrites_unpushed_authors_keeping_dates_content_and_work_in_progress()
    {
        var bare = await _repo.BareAsync();
        await _repo.WithCommitAsync("publié");
        await _git.AddRemoteAsync(P, "origin", bare);
        await _repo.Git("push", "-q", "-u", "origin", "main");
        var published = await _repo.Git("rev-parse", "HEAD");
        await _repo.CommitFileAsync("a.txt", "a", "local 1");
        await _repo.CommitFileAsync("b.txt", "b", "local 2");
        var dates = await _repo.Git("log", "-2", "--format=%aI");
        _repo.Write("README.md", "modification en cours");
        _repo.Write("brouillon.txt", "nouveau fichier en cours");

        await _git.SetLocalIdentityAsync(P, Perso.Name!, Perso.Email!);
        var results = await _git.RewriteUnpushedAuthorAsync(P, Perso);

        Assert.All(results, r => Assert.True(r.Succeeded, r.Output));
        Assert.Equal([Perso.Email, Perso.Email, "test@gitspread.local"], (await Authors()).Split('\n'));
        Assert.Equal(dates, await _repo.Git("log", "-2", "--format=%aI"));       // dates d'origine conservées
        Assert.Equal(published, await _repo.Git("rev-parse", "HEAD~2"));      // le commit publié n'a pas bougé
        Assert.Equal("b", File.ReadAllText(Path.Combine(P, "b.txt")));
        Assert.Equal("modification en cours", File.ReadAllText(Path.Combine(P, "README.md")));
        Assert.True(File.Exists(Path.Combine(P, "brouillon.txt")));
        Assert.Empty((await _git.ListUnpushedCommitsAsync(P)).Where(c => c.AuthorEmail != Perso.Email));
    }

    [Fact]
    public async Task A_never_pushed_repository_is_rewritten_from_its_first_commit()
    {
        await _repo.WithCommitAsync("premier");
        await _repo.CommitFileAsync("a.txt", "a", "second");

        var results = await _git.RewriteUnpushedAuthorAsync(P, Perso);

        Assert.All(results, r => Assert.True(r.Succeeded, r.Output));
        Assert.Equal([Perso.Email, Perso.Email], (await Authors()).Split('\n'));
    }

    [Fact]
    public async Task Merge_commits_are_kept()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("switch", "-q", "-c", "annexe");
        await _repo.CommitFileAsync("annexe.txt", "x", "annexe");
        await _repo.Git("switch", "-q", "main");
        await _repo.CommitFileAsync("main.txt", "y", "main");
        await _repo.Git("merge", "-q", "--no-ff", "-m", "fusion", "annexe");

        Assert.All(await _git.RewriteUnpushedAuthorAsync(P, Perso), r => Assert.True(r.Succeeded, r.Output));

        Assert.Equal("2", (await _repo.Git("log", "-1", "--format=%p")).Split(' ').Length.ToString()); // toujours une fusion
        Assert.All((await Authors()).Split('\n'), e => Assert.Equal(Perso.Email, e));
    }

    [Fact]
    public async Task Refuses_when_published_commits_would_be_rewritten()
    {
        // main locale fusionne une branche distante : la plage à réécrire contiendrait des commits déjà publiés.
        var bare = await _repo.BareAsync();
        await _repo.WithCommitAsync();
        await _git.AddRemoteAsync(P, "origin", bare);
        await _repo.Git("push", "-q", "-u", "origin", "main");
        await _repo.CommitFileAsync("local.txt", "l", "local non poussé");
        var other = _repo.Sub("autre poste");
        await _repo.GitIn(_repo.Root, "clone", "-q", bare, other);
        await _repo.GitIn(other, "switch", "-q", "-c", "collegue");
        await _repo.CommitFileAsync("c.txt", "c", "publié par le collègue", other);
        await _repo.GitIn(other, "push", "-q", "-u", "origin", "collegue");
        await _repo.Git("fetch", "-q", "origin");
        await _repo.Git("merge", "-q", "--no-ff", "-m", "fusion", "origin/collegue");
        var before = await _repo.Git("rev-parse", "HEAD");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _git.RewriteUnpushedAuthorAsync(P, Perso));
        Assert.Equal(before, await _repo.Git("rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Nothing_happens_when_everything_is_pushed()
    {
        var bare = await _repo.BareAsync();
        await _repo.WithCommitAsync();
        await _git.AddRemoteAsync(P, "origin", bare);
        await _repo.Git("push", "-q", "-u", "origin", "main");

        Assert.Empty(await _git.RewriteUnpushedAuthorAsync(P, Perso));
    }

    public void Dispose() => _repo.Dispose();
}
