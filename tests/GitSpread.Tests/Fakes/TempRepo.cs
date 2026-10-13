using GitSpread.Core.Processes;

namespace GitSpread.Tests.Fakes;

/// <summary>Dossier temporaire (avec espaces et accents) pour jouer de vraies commandes git.</summary>
public sealed class TempRepo : IDisposable
{
    private static readonly ProcessRunner Runner = new();

    public TempRepo(string name = "repo")
    {
        Root = Directory.CreateTempSubdirectory($"GitSpread {name} éà ").FullName;
        Path = System.IO.Path.Combine(Root, "Mon projet");
        Directory.CreateDirectory(Path);
    }

    /// <summary>Dossier parent, pour créer des dépôts voisins (bare, clones).</summary>
    public string Root { get; }
    public string Path { get; }

    public string Sub(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Root, name)).FullName;

    public void Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Lance git dans <paramref name="directory"/> (le projet par défaut) et exige un succès.</summary>
    public async Task<string> GitIn(string? directory, params string[] args)
    {
        var r = await Runner.RunAsync(new ProcessRequest("git",
            ["-c", "user.name=Test", "-c", "user.email=test@gitspread.local", "-c", "init.defaultBranch=main", .. args])
            { WorkingDirectory = directory ?? Path });
        Assert.True(r.Succeeded, $"git {string.Join(' ', args)} : {r.StandardError}");
        return r.StandardOutput.Trim();
    }

    public Task<string> Git(params string[] args) => GitIn(null, args);

    /// <summary>Dépôt initialisé avec une identité locale et un premier commit.</summary>
    public async Task<TempRepo> WithCommitAsync(string message = "Premier commit")
    {
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.name", "Test");
        await Git("config", "user.email", "test@gitspread.local");
        Write("README.md", "# Projet");
        await Git("add", ".");
        await Git("commit", "-q", "-m", message);
        return this;
    }

    /// <summary>Écrit un fichier et le commite dans <paramref name="directory"/> (le projet par défaut).</summary>
    public async Task CommitFileAsync(string file, string content, string message, string? directory = null)
    {
        var dir = directory ?? Path;
        var full = System.IO.Path.Combine(dir, file);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        await GitIn(dir, "add", "--all");
        await GitIn(dir, "commit", "-q", "-m", message);
    }

    /// <summary>Dépôt nu servant de « GitHub » local.</summary>
    public async Task<string> BareAsync(string name = "serveur.git")
    {
        var bare = Sub(name);
        await GitIn(bare, "init", "-q", "--bare", "-b", "main");
        return bare;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>git réel, gh simulé.</summary>
public sealed class RoutingRunner(FakeProcessRunner gh) : IProcessRunner
{
    private readonly ProcessRunner _real = new();

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
        request.FileName == "gh" ? gh.RunAsync(request, cancellationToken) : _real.RunAsync(request, cancellationToken);
}
