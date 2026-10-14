using System.Diagnostics;
using GitSpread.Core.Git;
using GitSpread.Core.Ssh;

namespace GitSpread.Core.Projects;

/// <summary>Dossier contenant un dépôt git.</summary>
public sealed record LocalProject(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));

    /// <summary>Remote utilisé pour rattacher le projet à un compte : origin, sinon le premier remote déclaré.</summary>
    public string? RemoteName { get; init; }
    public RemoteUrl? Remote { get; init; }
    public int RemoteCount { get; init; }

    /// <summary>
    /// Compte choisi pour échanger avec GitHub (« Envoyer avec le compte », F3A), lu dans .git/config
    /// (gitspread.pushAccount). Null : le propriétaire du repo.
    /// </summary>
    public string? PushAccount { get; init; }

    /// <summary>Tous les remotes, dans l'ordre du .git/config (F3B : un projet lié à plusieurs repos).</summary>
    public IReadOnlyList<ProjectRemote> Remotes { get; init; } = [];

    /// <summary>Liens cochés au dernier « Pousser » (gitspread.pushRemotes). Vide : le lien principal seul.</summary>
    public IReadOnlyList<string> PushRemotes { get; init; } = [];

    /// <summary>Remotes connus : <see cref="Remotes"/>, ou à défaut le seul remote principal.</summary>
    public IReadOnlyList<ProjectRemote> AllRemotes =>
        Remotes.Count > 0 ? Remotes : Remote is null ? [] : [new ProjectRemote(RemoteName ?? "origin", Remote)];

    /// <summary>Faux pour un projet ajouté à la main qui n'est pas encore suivi par git.</summary>
    public bool IsRepository { get; init; } = true;

    /// <summary>Vrai si le projet a été ajouté un par un (et peut donc être retiré de la liste).</summary>
    public bool IsExtra { get; init; }
}

public sealed record ProjectRemote(string Name, RemoteUrl Url);

public sealed record ScanOptions(IReadOnlyList<string> Roots)
{
    public int MaxDepth { get; init; } = 4;
    public IReadOnlyCollection<string> ExcludedFolderNames { get; init; } = DefaultExclusions;
    public IReadOnlyCollection<string> ExtraProjects { get; init; } = [];

    public static readonly IReadOnlyCollection<string> DefaultExclusions =
        ["node_modules", "vendor", "bin", "obj", "packages", "dist", "build", "target", "__pycache__", "venv", "AppData"];
}

/// <param name="MissingRoots">Dossiers racine ou projets ajoutés qui n'existent plus.</param>
public sealed record ScanResult(IReadOnlyList<LocalProject> Projects, TimeSpan Duration, IReadOnlyList<string> MissingRoots);

/// <summary>
/// Parcourt les dossiers racine et repère les dépôts git, sans lancer git : le .git/config est lu
/// directement (des centaines de projets en quelques millisecondes).
/// </summary>
public sealed class ProjectScanner
{
    public Task<ScanResult> ScanAsync(ScanOptions options, SshConfig? ssh = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(options, ssh ?? SshConfig.Empty, cancellationToken), cancellationToken);

    public ScanResult Scan(ScanOptions options, SshConfig ssh, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var excluded = new HashSet<string>(options.ExcludedFolderNames, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, LocalProject>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var root in options.Roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) { missing.Add(root); continue; }
            Walk(new DirectoryInfo(root), 0);
        }

        foreach (var extra in options.ExtraProjects.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(extra));
            if (!Directory.Exists(path)) { missing.Add(path); continue; }
            found[path] = (ReadProject(path, ssh) ?? new LocalProject(path) { IsRepository = false }) with { IsExtra = true };
        }

        return new ScanResult(
            found.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            stopwatch.Elapsed, missing);

        void Walk(DirectoryInfo dir, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found.ContainsKey(dir.FullName)) return;

            if (ReadProject(dir.FullName, ssh) is { } project)
            {
                found[dir.FullName] = project;
                return; // pas de recherche à l'intérieur d'un dépôt (sous-modules, dépendances...)
            }
            if (depth >= options.MaxDepth) return;

            IEnumerable<DirectoryInfo> children;
            try { children = dir.EnumerateDirectories().ToList(); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }

            foreach (var child in children)
            {
                if (child.Name.StartsWith('.') || child.Name.StartsWith('$')) continue;
                // Un dossier exclu (build, dist, vendor…) est ignoré, sauf s'il est lui-même un projet git.
                if (excluded.Contains(child.Name) && !Path.Exists(Path.Combine(child.FullName, ".git"))) continue;
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint) || child.Attributes.HasFlag(FileAttributes.System)) continue;
                Walk(child, depth + 1);
            }
        }
    }

    /// <summary>Projet si le dossier contient un dépôt git, null sinon.</summary>
    public static LocalProject? ReadProject(string directory, SshConfig? ssh = null)
    {
        var configPath = FindConfig(directory);
        if (configPath is null) return null;

        var config = ReadConfig(configPath);
        var remotes = config.Remotes.Select(r => new ProjectRemote(r.Name, RemoteUrlParser.Parse(r.Url, ssh))).ToList();
        var primary = remotes.FirstOrDefault(r => r.Name == "origin") ?? remotes.FirstOrDefault();
        return new LocalProject(directory)
        {
            RemoteName = primary?.Name,
            Remote = primary?.Url,
            RemoteCount = remotes.Count,
            Remotes = remotes,
            PushAccount = config.PushAccount,
            PushRemotes = config.PushRemotes,
        };
    }

    // .git est un dossier (cas normal) ou un fichier « gitdir: ... » (worktree, sous-module).
    private static string? FindConfig(string directory)
    {
        var dotGit = Path.Combine(directory, ".git");
        try
        {
            string gitDir;
            if (Directory.Exists(dotGit)) gitDir = dotGit;
            else if (File.Exists(dotGit))
            {
                var line = File.ReadLines(dotGit).FirstOrDefault() ?? "";
                if (!line.StartsWith("gitdir:")) return null;
                gitDir = Path.GetFullPath(Path.Combine(directory, line[7..].Trim()));
                // Un worktree partage la config du dépôt principal (fichier commondir).
                var commonDir = Path.Combine(gitDir, "commondir");
                if (File.Exists(commonDir))
                    gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDir).Trim()));
            }
            else return null;

            var config = Path.Combine(gitDir, "config");
            return File.Exists(config) ? config : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    internal sealed record ConfigSummary(List<(string Name, string Url)> Remotes, string? PushAccount, IReadOnlyList<string> PushRemotes);

    /// <summary>Remotes (nom, URL) et réglages GitSpread du .git/config.</summary>
    internal static ConfigSummary ReadConfig(string configPath)
    {
        var remotes = new List<(string, string)>();
        string? pushAccount = null;
        IReadOnlyList<string> pushRemotes = [];
        string? remote = null;
        var inGitSpread = false;
        IEnumerable<string> lines;
        try { lines = File.ReadAllLines(configPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(remotes, null, []); }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                // [remote "origin"] ou [gitspread] (noms de section et de clé insensibles à la casse)
                remote = line.StartsWith("[remote \"") && line.EndsWith("\"]") ? line[9..^2] : null;
                inGitSpread = line.Equals("[gitspread]", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.Split('=', 2) is [var rawKey, var value])
            {
                var key = rawKey.Trim();
                if (remote is not null && key.Equals("url", StringComparison.OrdinalIgnoreCase) && !remotes.Any(r => r.Item1 == remote))
                    remotes.Add((remote, UnescapeConfigValue(value.Trim())));
                else if (inGitSpread && key.Equals("pushAccount", StringComparison.OrdinalIgnoreCase))
                    pushAccount = UnescapeConfigValue(value.Trim()) is { Length: > 0 } login ? login : null;
                else if (inGitSpread && key.Equals("pushRemotes", StringComparison.OrdinalIgnoreCase))
                    pushRemotes = UnescapeConfigValue(value.Trim()).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }
        }
        return new(remotes, pushAccount, pushRemotes);
    }

    /// <summary>Valeur de .git/config : guillemets retirés et échappements résolus (\\ → \, \" → ", \t, \n).</summary>
    internal static string UnescapeConfigValue(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '"') continue;
            if (c == '\\' && i + 1 < raw.Length)
            {
                var next = raw[++i];
                sb.Append(next switch { 't' => '\t', 'n' => '\n', 'b' => '\b', _ => next });
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
