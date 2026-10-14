using GitSpread.Core.Git;
using GitSpread.Core.Processes;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Git;

/// <summary>F6e : tous les fichiers du projet à un commit, leur contenu, et la restauration d'une ancienne version.</summary>
public sealed class CommitFilesTests : IDisposable
{
    private readonly TempRepo _repo = new("fichiers");
    private readonly GitService _git = new(new ProcessRunner());
    private string P => _repo.Path;

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Lists_every_file_of_a_commit_with_spaces_and_accents()
    {
        await _repo.WithCommitAsync();
        await _repo.CommitFileAsync("src/Données/éléments 1.txt", "a", "un");
        var first = await _repo.Git("rev-parse", "HEAD");
        await _repo.CommitFileAsync("src/autre.cs", "b", "deux");

        var files = await _git.ListFilesAsync(P, first);
        Assert.Equal(["README.md", "src/Données/éléments 1.txt"], files.Select(f => f.Path));
        Assert.Equal("éléments 1.txt", files[1].Name);
        Assert.Equal(3, (await _git.ListFilesAsync(P, "HEAD")).Count); // le fichier ajouté après n'existait pas encore
    }

    [Fact]
    public void Parses_submodules_and_links()
    {
        var entries = GitService.ParseTree("100644 blob a1\tREADME.md\0160000 commit b2\tlib/outil\0120000 blob c3\tlien\0");
        Assert.Equal([new TreeEntry("README.md"), new TreeEntry("lib/outil", IsSubmodule: true), new TreeEntry("lien", IsLink: true)], entries);
    }

    [Fact]
    public async Task Reads_text_as_it_was_and_recognises_images_and_binaries()
    {
        await _repo.WithCommitAsync();
        await _repo.CommitFileAsync("notes.txt", "version 1\r\nligne 2\r\n", "v1");
        var v1 = await _repo.Git("rev-parse", "HEAD");
        await _repo.CommitFileAsync("notes.txt", "version 2\n", "v2");
        File.WriteAllBytes(Path.Combine(P, "logo.png"), Png);
        File.WriteAllBytes(Path.Combine(P, "outil.bin"), [1, 0, 2, 0, 3]);
        await _repo.Git("add", "--all");
        await _repo.Git("commit", "-q", "-m", "binaires");

        var text = await _git.ReadFileAsync(P, v1, "notes.txt");
        Assert.Equal(FileContentKind.Text, text.Kind);
        Assert.Equal(["version 1", "ligne 2"], text.Lines);

        var image = await _git.ReadFileAsync(P, "HEAD", "logo.png");
        Assert.Equal(FileContentKind.Image, image.Kind);
        Assert.Equal(Png, File.ReadAllBytes(image.ImagePath!)); // octets intacts
        Assert.Equal(FileContentKind.Binary, (await _git.ReadFileAsync(P, "HEAD", "outil.bin")).Kind);
        await Assert.ThrowsAsync<GitCommandException>(() => _git.ReadFileAsync(P, v1, "logo.png")); // n'existait pas encore
    }

    [Fact]
    public async Task Long_files_are_truncated()
    {
        await _repo.WithCommitAsync();
        await _repo.CommitFileAsync("long.txt", string.Join('\n', Enumerable.Range(1, GitService.MaxFileLines + 10)), "long");

        var content = await _git.ReadFileAsync(P, "HEAD", "long.txt");
        Assert.True(content.Truncated);
        Assert.Equal(GitService.MaxFileLines, content.Lines.Count);
    }

    [Fact]
    public async Task Saves_and_restores_an_old_version_without_touching_the_history()
    {
        await _repo.WithCommitAsync();
        await _repo.CommitFileAsync("dossier/notes.txt", "ancienne", "v1");
        var v1 = await _repo.Git("rev-parse", "HEAD");
        await _repo.CommitFileAsync("dossier/notes.txt", "nouvelle", "v2");
        var head = await _repo.Git("rev-parse", "HEAD");

        var copy = Path.Combine(_repo.Sub("copies"), "notes v1.txt");
        await _git.SaveFileAsync(P, v1, "dossier/notes.txt", copy);
        Assert.Equal("ancienne", File.ReadAllText(copy));
        Assert.False(await _git.HasUncommittedChangesAsync(P, "dossier/notes.txt"));

        var restore = await _git.RestoreFileAsync(P, v1, "dossier/notes.txt");
        Assert.True(restore.Succeeded, restore.Output);
        Assert.Equal($"git restore --source={v1} --worktree -- dossier/notes.txt", restore.Command);
        Assert.Equal("ancienne", File.ReadAllText(Path.Combine(P, "dossier", "notes.txt")));
        Assert.True(await _git.HasUncommittedChangesAsync(P, "dossier/notes.txt")); // un changement normal, à commiter
        Assert.Equal(head, await _repo.Git("rev-parse", "HEAD"));
        Assert.Empty(await _repo.Git("diff", "--cached", "--name-only")); // rien n'est indexé
    }

    [Fact]
    public async Task Saving_a_missing_file_fails_without_leaving_an_empty_copy()
    {
        await _repo.WithCommitAsync();
        var copy = Path.Combine(_repo.Sub("copies"), "absent.txt");
        await Assert.ThrowsAsync<GitCommandException>(() => _git.SaveFileAsync(P, "HEAD", "absent.txt", copy));
        Assert.False(File.Exists(copy));
    }

    public void Dispose() => _repo.Dispose();
}
