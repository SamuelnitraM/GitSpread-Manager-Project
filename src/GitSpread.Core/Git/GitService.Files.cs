namespace GitSpread.Core.Git;

/// <summary>Un fichier du projet à un commit donné (F6e).</summary>
/// <param name="IsSubmodule">Sous-module : un autre dépôt, sans contenu dans celui-ci.</param>
public sealed record TreeEntry(string Path, bool IsSubmodule = false, bool IsLink = false)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

public enum FileContentKind { Text, Image, Binary, TooLarge }

/// <summary>Contenu d'un fichier à un commit : lignes de texte, image (copiée dans un fichier temporaire) ou binaire.</summary>
public sealed record FileContent(FileContentKind Kind, long Size, IReadOnlyList<string> Lines, bool Truncated = false, string? ImagePath = null);

public sealed partial class GitService
{
    public const int MaxFileLines = 5000;
    public const long MaxPreviewBytes = 2 * 1024 * 1024;

    /// <summary>Extensions affichées en aperçu d'image.</summary>
    public static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico" };

    /// <summary>Tous les fichiers du projet tels qu'ils étaient à ce commit (aucun contenu n'est lu : rapide, même sur une copie légère).</summary>
    public async Task<IReadOnlyList<TreeEntry>> ListFilesAsync(string repositoryPath, string sha, CancellationToken ct = default) =>
        ParseTree(await ReadAsync(repositoryPath, ["ls-tree", "-r", "-z", "--full-tree", sha], ct));

    /// <summary>« 100644 blob abc\tchemin\0160000 commit def\tsous-module\0 » → fichiers.</summary>
    internal static IReadOnlyList<TreeEntry> ParseTree(string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('\t', 2))
            .Where(p => p.Length == 2)
            .Select(p => new TreeEntry(p[1], IsSubmodule: p[0].StartsWith("160000"), IsLink: p[0].StartsWith("120000")))
            .ToList();

    /// <summary>Contenu d'un fichier à ce commit, tel qu'il serait sur le disque (fins de ligne du poste).</summary>
    /// <param name="auth">Copie légère (F6c) : compte avec lequel télécharger le contenu, absent jusque-là.</param>
    public async Task<FileContent> ReadFileAsync(string repositoryPath, string sha, string path,
        IReadOnlyDictionary<string, string>? auth = null, CancellationToken ct = default)
    {
        var size = long.Parse((await ReadAsync(repositoryPath, ["cat-file", "-s", $"{sha}:{path}"], auth, ct)).Trim());
        if (ImageExtensions.Contains(System.IO.Path.GetExtension(path)) && size <= MaxPreviewBytes * 5)
        {
            var directory = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GitSpread", "apercus")).FullName;
            var image = System.IO.Path.Combine(directory, $"{Guid.NewGuid():N}{System.IO.Path.GetExtension(path)}");
            await SaveFileAsync(repositoryPath, sha, path, image, auth, ct);
            return new FileContent(FileContentKind.Image, size, [], ImagePath: image);
        }
        if (size > MaxPreviewBytes) return new FileContent(FileContentKind.TooLarge, size, []);

        var text = await ReadAsync(repositoryPath, ["cat-file", "--filters", $"{sha}:{path}"], auth, ct);
        if (text.Contains('\0')) return new FileContent(FileContentKind.Binary, size, []);
        var lines = text.Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length; // dernier saut de ligne
        var truncated = count > MaxFileLines;
        return new FileContent(FileContentKind.Text, size,
            lines.Take(Math.Min(count, MaxFileLines)).Select(l => l.TrimEnd('\r')).ToList(), truncated);
    }

    /// <summary>Écrit la version de ce commit d'un fichier à l'emplacement choisi (binaire compris). Le projet n'est pas touché.</summary>
    public async Task SaveFileAsync(string repositoryPath, string sha, string path, string destination,
        IReadOnlyDictionary<string, string>? auth = null, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(new Processes.ProcessRequest(Executable,
            ["--no-optional-locks", "-c", "core.quotepath=false", "cat-file", "--filters", $"{sha}:{path}"])
        {
            WorkingDirectory = repositoryPath,
            Environment = auth ?? GitCredentialEnvironment.NonInteractive(),
            Timeout = NetworkTimeout,
            StandardOutputPath = destination,
        }, ct);
        if (!result.Succeeded)
        {
            try { File.Delete(destination); } catch (IOException) { }
            throw new GitCommandException($"Fichier illisible à ce commit : {result.StandardError.Trim().Split('\n')[0]}", result);
        }
    }

    /// <summary>Vrai si ce fichier a des changements pas encore commités (ou n'est pas suivi par git).</summary>
    public async Task<bool> HasUncommittedChangesAsync(string repositoryPath, string path, CancellationToken ct = default) =>
        (await ReadAsync(repositoryPath, ["status", "--porcelain", "--", path], ct)).Trim().Length > 0;

    /// <summary>Remet dans le projet la version de ce commit d'un fichier. Seul le fichier sur le disque change :
    /// la restauration apparaît ensuite comme un changement normal, à commiter.</summary>
    public Task<GitResult> RestoreFileAsync(string repositoryPath, string sha, string path, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["restore", $"--source={sha}", "--worktree", "--", path], null, ct);
}
