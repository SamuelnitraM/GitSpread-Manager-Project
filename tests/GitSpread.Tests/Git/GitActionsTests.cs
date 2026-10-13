using GitSpread.Core.Git;
using GitSpread.Core.Processes;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Git;

/// <summary>Actions git (init, commit, push, pull...) contre de vrais dépôts, le « serveur » étant un dépôt nu local.</summary>
public sealed class GitActionsTests : IDisposable
{
    private readonly TempRepo _repo = new("actions");
    private readonly GitService _git = new(new ProcessRunner());
    private static readonly IReadOnlyDictionary<string, string> NoAuth = GitCredentialEnvironment.NonInteractive();

    [Fact]
    public async Task Init_creates_a_repository_on_the_requested_branch()
    {
        var result = await _git.InitAsync(_repo.Path);

        Assert.True(result.Succeeded);
        Assert.Equal("git init -b main", result.Command);
        Assert.Equal("main", (await _git.GetStatusAsync(_repo.Path)).Branch);
    }

    [Fact]
    public async Task Lists_changes_with_spaces_accents_renames_and_deletions()
    {
        await _repo.WithCommitAsync();
        _repo.Write("à renommer.txt", "contenu stable pour la détection du renommage\n" + new string('x', 200));
        _repo.Write("supprimé.txt", "x");
        await _repo.Git("add", ".");
        await _repo.Git("commit", "-q", "-m", "fichiers");

        await _repo.Git("mv", "à renommer.txt", "renommé é.txt");
        File.Delete(Path.Combine(_repo.Path, "supprimé.txt"));
        _repo.Write("README.md", "# modifié");
        _repo.Write("nouveau dossier/fichier.txt", "n");

        var changes = await _git.GetChangesAsync(_repo.Path);

        Assert.Contains(changes, c => c is { Kind: ChangeKind.Renamed, Path: "renommé é.txt", OriginalPath: "à renommer.txt" });
        Assert.Contains(changes, c => c is { Kind: ChangeKind.Deleted, Path: "supprimé.txt" });
        Assert.Contains(changes, c => c is { Kind: ChangeKind.Modified, Path: "README.md" });
        Assert.Contains(changes, c => c is { Kind: ChangeKind.Untracked, Path: "nouveau dossier/" });
    }

    [Fact]
    public async Task Commits_everything_by_default()
    {
        await _repo.WithCommitAsync();
        _repo.Write("a.txt", "a");
        _repo.Write("b.txt", "b");

        var results = await _git.CommitAsync(_repo.Path, "Ajout de a et b");

        Assert.All(results, r => Assert.True(r.Succeeded, r.Output));
        Assert.Equal(["git add --all", "git commit -m \"Ajout de a et b\""], results.Select(r => r.Command));
        Assert.True((await _git.GetStatusAsync(_repo.Path)).IsClean);
    }

    [Fact]
    public async Task Commits_only_the_selected_files_even_if_others_are_staged()
    {
        await _repo.WithCommitAsync();
        _repo.Write("choisi.txt", "oui");
        _repo.Write("ignoré.txt", "non");
        await _repo.Git("add", "ignoré.txt"); // déjà indexé, mais non sélectionné

        var results = await _git.CommitAsync(_repo.Path, "Seulement le fichier choisi", ["choisi.txt"]);

        Assert.All(results, r => Assert.True(r.Succeeded, r.Output));
        Assert.Equal("choisi.txt", await _repo.Git("show", "--name-only", "--format=", "HEAD"));
        Assert.Contains(await _git.GetChangesAsync(_repo.Path), c => c.Path == "ignoré.txt");
    }

    [Fact]
    public async Task Selected_files_work_for_the_very_first_commit_too()
    {
        await _git.InitAsync(_repo.Path);
        await _git.SetLocalIdentityAsync(_repo.Path, "Test", "test@gitspread.local");
        _repo.Write("garder.txt", "1");
        _repo.Write("plus tard.txt", "2");

        var results = await _git.CommitAsync(_repo.Path, "Premier", ["garder.txt"]);

        Assert.All(results, r => Assert.True(r.Succeeded, r.Output));
        Assert.Equal("garder.txt", await _repo.Git("show", "--name-only", "--format=", "HEAD"));
    }

    [Fact]
    public async Task Explains_an_empty_commit_and_rejects_an_empty_message()
    {
        await _repo.WithCommitAsync();

        var results = await _git.CommitAsync(_repo.Path, "rien");

        Assert.Equal("Rien à commiter : aucune modification.", results.Last().Explanation);
        await Assert.ThrowsAsync<ArgumentException>(() => _git.CommitAsync(_repo.Path, "  "));
    }

    [Fact]
    public async Task Sets_a_local_identity_without_touching_the_global_one()
    {
        await _repo.WithCommitAsync();

        await _git.SetLocalIdentityAsync(_repo.Path, "Samuel", "42+SamuelnitraM@users.noreply.github.com");

        var identity = await _git.GetIdentityAsync(_repo.Path);
        Assert.Equal(("Samuel", "42+SamuelnitraM@users.noreply.github.com"), (identity.Name, identity.Email));
        Assert.Equal("42+SamuelnitraM@users.noreply.github.com", await _repo.Git("config", "--local", "user.email"));
    }

    [Fact]
    public async Task Push_fetch_and_pull_round_trip_through_a_remote()
    {
        var bare = _repo.Sub("serveur.git");
        await _repo.GitIn(bare, "init", "-q", "--bare");
        await _repo.WithCommitAsync();
        await _git.AddRemoteAsync(_repo.Path, "origin", bare);

        // Premier push : lie la branche au distant
        var push = await _git.PushAsync(_repo.Path, "origin", "main", setUpstream: true, NoAuth);
        Assert.True(push.Succeeded, push.Output);
        Assert.Equal("origin/main", (await _git.GetStatusAsync(_repo.Path)).Upstream);

        // Quelqu'un d'autre pousse un commit
        var other = _repo.Sub("autre poste");
        await _repo.GitIn(_repo.Root, "clone", "-q", bare, other);
        File.WriteAllText(Path.Combine(other, "distant.txt"), "d");
        await _repo.GitIn(other, "add", ".");
        await _repo.GitIn(other, "commit", "-q", "-m", "commit distant");
        await _repo.GitIn(other, "push", "-q");

        Assert.True((await _git.FetchAsync(_repo.Path, "origin", NoAuth)).Succeeded);
        Assert.Equal(1, (await _git.GetStatusAsync(_repo.Path)).Behind);

        var pull = await _git.PullAsync(_repo.Path, NoAuth);
        Assert.True(pull.Succeeded, pull.Output);
        Assert.True(File.Exists(Path.Combine(_repo.Path, "distant.txt")));
        Assert.Equal(0, (await _git.GetStatusAsync(_repo.Path)).Behind);
    }

    [Fact]
    public async Task Diverged_branches_are_never_forced_and_are_explained()
    {
        var bare = _repo.Sub("serveur.git");
        await _repo.GitIn(bare, "init", "-q", "--bare");
        await _repo.WithCommitAsync();
        await _git.AddRemoteAsync(_repo.Path, "origin", bare);
        await _git.PushAsync(_repo.Path, "origin", "main", true, NoAuth);

        var other = _repo.Sub("autre poste");
        await _repo.GitIn(_repo.Root, "clone", "-q", bare, other);
        File.WriteAllText(Path.Combine(other, "distant.txt"), "d");
        await _repo.GitIn(other, "add", ".");
        await _repo.GitIn(other, "commit", "-q", "-m", "distant");
        await _repo.GitIn(other, "push", "-q");

        _repo.Write("local.txt", "l");
        await _git.CommitAsync(_repo.Path, "local");

        var push = await _git.PushAsync(_repo.Path, "origin", "main", false, NoAuth);
        Assert.False(push.Succeeded);
        Assert.Contains("Tirer", push.Explanation);

        await _git.FetchAsync(_repo.Path, "origin", NoAuth);
        var pull = await _git.PullAsync(_repo.Path, NoAuth);
        Assert.False(pull.Succeeded);
        Assert.Contains("ne fusionne pas automatiquement", pull.Explanation);
        Assert.Equal("local", (await _git.GetLastCommitAsync(_repo.Path))!.Subject); // rien n'a été écrasé
    }

    public void Dispose() => _repo.Dispose();
}

public class GitChangesParserTests
{
    [Fact]
    public void Parses_nul_separated_entries()
    {
        var z = string.Join('\0',
            "1 .M N... 100644 100644 100644 aaa bbb dossier avec espace/fichier é.txt",
            "1 A. N... 000000 100644 100644 000 bbb ajouté.txt",
            "1 D. N... 100644 000000 000000 aaa 000 supprimé.txt",
            "2 R. N... 100644 100644 100644 aaa bbb R100 nouveau nom.txt", "ancien nom.txt",
            "2 C. N... 100644 100644 100644 aaa bbb C75 copie.txt", "source.txt",
            "u UU N... 100644 100644 100644 100644 a b c conflit.txt",
            "? non suivi/",
            "");

        var changes = GitService.ParseChanges(z);

        Assert.Equal(
        [
            new FileChange("dossier avec espace/fichier é.txt", ChangeKind.Modified),
            new FileChange("ajouté.txt", ChangeKind.Added),
            new FileChange("supprimé.txt", ChangeKind.Deleted),
            new FileChange("nouveau nom.txt", ChangeKind.Renamed) { OriginalPath = "ancien nom.txt" },
            new FileChange("copie.txt", ChangeKind.Copied) { OriginalPath = "source.txt" },
            new FileChange("conflit.txt", ChangeKind.Conflicted),
            new FileChange("non suivi/", ChangeKind.Untracked),
        ], changes);
        Assert.Equal(["nouveau nom.txt", "ancien nom.txt"], changes[3].Pathspecs);
        Assert.Equal(["copie.txt"], changes[4].Pathspecs);
    }
}

public class GitErrorExplainerTests
{
    [Theory]
    [InlineData("remote: Permission to SamuelnitraM/x.git denied to SmartDev-Web.\nfatal: unable to access", "SmartDev-Web")]
    [InlineData(" ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs", "Tirer")]
    [InlineData("fatal: Not possible to fast-forward, aborting.", "ne fusionne pas")]
    [InlineData("error: Your local changes to the following files would be overwritten by merge:", "Commite-les")]
    [InlineData("Author identity unknown\n*** Please tell me who you are.", "identité")]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/x/y.git/' not found", "introuvable")]
    [InlineData("git@github.com: Permission denied (publickey).", "Clé SSH")]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", "Authentification")]
    [InlineData("fatal: unable to access 'https://github.com/x/y.git/': Could not resolve host: github.com", "connexion internet")]
    [InlineData("error: src refspec main does not match any", "aucun commit")]
    public void Explains_common_errors(string output, string expected) =>
        Assert.Contains(expected, GitErrorExplainer.Explain(output));

    [Fact]
    public void Unknown_errors_are_left_as_is() => Assert.Null(GitErrorExplainer.Explain("fatal: quelque chose d'inédit"));
}
