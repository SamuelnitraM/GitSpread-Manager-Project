namespace GitSpread.Core.Git;

/// <summary>Une branche dans l'explorateur : son dernier commit et sa situation, dite en clair.</summary>
public sealed record BranchOverview(BranchInfo Info, string TipSha, string Subject, DateTimeOffset? Date)
{
    public string Name => Info.Name;
    public bool IsDefault { get; init; }
    /// <summary>Commits de la branche absents de la branche principale (null pour la principale).</summary>
    public int? NotInDefault { get; init; }

    /// <summary>Dernier commit de la version distante (GitHub) d'une branche locale, s'il diffère ou non.</summary>
    public string? RemoteTipSha { get; init; }
}

/// <summary>Fichier modifié par un commit (A ajouté, M modifié, D supprimé, R renommé…).</summary>
public sealed record CommitFile(string Status, string Path, string? OldPath)
{
    public string StatusLetter => Status[..1];
}

public sealed record CommitDetails(HistoryCommit Commit, string Message, string Committer, IReadOnlyList<string> Branches, IReadOnlyList<CommitFile> Files);

public enum DiffLineKind { Header, Hunk, Context, Added, Removed }

public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>Lecture de l'historique pour l'explorateur (F6a). Rien n'est modifié, rien n'est envoyé.</summary>
public sealed partial class GitService
{
    public const int HistoryPageSize = 300;
    public const int MaxDiffLines = 3000;

    /// <summary>
    /// Commits de toutes les branches (locales et distantes) et de HEAD, du plus récent au plus ancien, enfants avant parents.
    /// Les mises de côté (stash) ne sont pas incluses.
    /// </summary>
    public async Task<IReadOnlyList<HistoryCommit>> ReadHistoryAsync(string repositoryPath, int skip = 0, int count = HistoryPageSize, CancellationToken ct = default)
    {
        // HEAD seulement s'il désigne un commit (repo vide, ou copie dont la branche par défaut n'existe pas encore).
        var headExists = (await RunAsync(repositoryPath, ["rev-parse", "-q", "--verify", "HEAD^{commit}"], null, ct)).Succeeded;
        var result = await RunAsync(repositoryPath,
            // Options d'abord : dans un repo vide, « HEAD » n'est pas une révision et git le prendrait pour un chemin.
            ["log", "--date-order", $"--skip={skip}", $"--max-count={count}", "--decorate=full",
             "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%D%x1e", "--branches", "--remotes", .. (headExists ? ["HEAD"] : Array.Empty<string>()), "--"],
            null, ct);
        if (!result.Succeeded)
        {
            if (result.StandardError.Contains("does not have any commits") || result.StandardError.Contains("unknown revision")) return [];
            throw new GitCommandException(result.StandardError.Trim().Split('\n')[0], result); // l'appelant précise « Historique illisible »
        }
        return ParseHistory(result.StandardOutput);
    }

    internal static IReadOnlyList<HistoryCommit> ParseHistory(string output) =>
        output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim('\r', '\n').Split('\x1f'))
            .Where(f => f.Length >= 7 && f[0].Length > 0)
            .Select(f => new HistoryCommit(f[0], f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries), f[2], f[3],
                DateTimeOffset.TryParse(f[4], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : DateTimeOffset.MinValue, f[5], ParseDecorations(f[6])))
            .ToList();

    /// <summary>« HEAD -> refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1 » → HEAD, main, origin/main, tag v1.</summary>
    internal static IReadOnlyList<string> ParseDecorations(string decorations)
    {
        var refs = new List<string>();
        foreach (var raw in decorations.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw;
            if (part.StartsWith("HEAD -> ")) { refs.Add("HEAD"); part = part[8..]; }
            if (part == "HEAD") { refs.Add("HEAD"); continue; }
            if (part.StartsWith("tag: refs/tags/")) refs.Add("tag: " + part[15..]);
            else if (part.StartsWith("refs/heads/")) refs.Add(part[11..]);
            else if (part.StartsWith("refs/remotes/") && !part.EndsWith("/HEAD")) refs.Add(part[13..]);
        }
        return refs;
    }

    /// <summary>
    /// Chaque branche (locale, ou seulement sur le distant) avec son dernier commit et le nombre de commits
    /// qui ne sont pas encore dans la branche principale.
    /// </summary>
    public async Task<IReadOnlyList<BranchOverview>> GetBranchOverviewAsync(string repositoryPath, string? defaultBranch, CancellationToken ct = default)
    {
        var branches = await ListBranchesAsync(repositoryPath, ct);
        var tips = (await ReadAsync(repositoryPath, ["for-each-ref", "--format=%(refname)%1f%(objectname)%1f%(contents:subject)%1f%(committerdate:iso-strict)",
                "refs/heads", "refs/remotes"], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\x1f'))
            .Where(f => f.Length >= 4)
            .ToDictionary(f => f[0].StartsWith("refs/heads/") ? f[0][11..] : f[0][13..], f => (Sha: f[1], Subject: f[2], Date: DateTimeOffset.TryParse(f[3], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : (DateTimeOffset?)null));

        var defaultRef = branches.FirstOrDefault(b => b.Name == defaultBranch)?.Ref;
        var result = new List<BranchOverview>();
        foreach (var b in branches)
        {
            if (!tips.TryGetValue(b.Ref, out var tip)) continue;
            var isDefault = b.Name == defaultBranch;
            int? notInDefault = null;
            if (!isDefault && defaultRef is not null)
                notInDefault = (await CompareAsync(repositoryPath, b.Ref, defaultRef, ct)).OnlyInA;
            result.Add(new BranchOverview(b, tip.Sha, tip.Subject, tip.Date)
            {
                IsDefault = isDefault,
                NotInDefault = notInDefault,
                RemoteTipSha = b.IsLocal && b.RemoteRef is { } remote && tips.TryGetValue(remote, out var r) ? r.Sha : null,
            });
        }
        return result.OrderByDescending(b => b.IsDefault).ThenByDescending(b => b.Date).ToList();
    }

    /// <summary>Détail d'un commit : message complet, branches qui le contiennent, fichiers modifiés (par rapport à son premier parent).</summary>
    /// <param name="auth">Copie légère (F6c) : compte avec lequel télécharger au besoin le contenu des fichiers (détection des renommages).</param>
    public async Task<CommitDetails> GetCommitDetailsAsync(string repositoryPath, string sha, IReadOnlyDictionary<string, string>? auth = null,
        CancellationToken ct = default)
    {
        var head = (await ReadAsync(repositoryPath, ["show", "-s", "--decorate=full", "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%D%x1f%cn%x1f%B", sha], ct))
            .Split('\x1f');
        var commit = ParseHistory(string.Join('\x1f', head.Take(7)) + "\x1e")[0];
        var files = commit.Parents.Count == 0
            ? await ReadAsync(repositoryPath, ["diff-tree", "--no-ext-diff", "--no-textconv", "--root", "--no-commit-id", "-r", "-M", "--name-status", "-z", sha], auth, ct)
            : await ReadAsync(repositoryPath, ["diff", "--no-ext-diff", "--no-textconv", "--name-status", "-M", "-z", commit.Parents[0], sha], auth, ct);
        // Noms complets pour écarter refs/remotes/origin/HEAD (que git abrège en « origin », qui n'est pas une branche).
        var branches = (await ReadAsync(repositoryPath, ["branch", "--all", "--contains", sha, "--format=%(refname)"], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => !r.EndsWith("/HEAD"))
            .Select(r => r.StartsWith("refs/heads/") ? r[11..] : r.StartsWith("refs/remotes/") ? r[13..] : null)
            .OfType<string>()
            .ToList();
        return new CommitDetails(commit, head.Length > 8 ? head[8].Trim() : commit.Subject, head.Length > 7 ? head[7] : commit.Author, branches, ParseNameStatus(files));
    }

    /// <summary>« M\0chemin\0R100\0ancien\0nouveau\0 » (sortie -z) → fichiers modifiés.</summary>
    internal static IReadOnlyList<CommitFile> ParseNameStatus(string output)
    {
        var parts = output.Split('\0');
        var files = new List<CommitFile>();
        for (var i = 0; i < parts.Length; i++)
        {
            var status = parts[i].Trim();
            if (status.Length == 0) continue;
            if ((status[0] is 'R' or 'C') && i + 2 < parts.Length)
            {
                files.Add(new CommitFile(status, parts[i + 2], parts[i + 1]));
                i += 2;
            }
            else if (i + 1 < parts.Length)
            {
                files.Add(new CommitFile(status, parts[i + 1], null));
                i++;
            }
        }
        return files;
    }

    /// <summary>Modifications d'un fichier dans un commit, ligne par ligne (tronquées au-delà de <see cref="MaxDiffLines"/> lignes).</summary>
    public async Task<IReadOnlyList<DiffLine>> GetFileDiffAsync(string repositoryPath, HistoryCommit commit, CommitFile file,
        IReadOnlyDictionary<string, string>? auth = null, CancellationToken ct = default)
    {
        IReadOnlyList<string> paths = file.OldPath is { } old ? [old, file.Path] : [file.Path];
        var output = commit.Parents.Count == 0
            ? await ReadAsync(repositoryPath, ["diff-tree", "--no-ext-diff", "--no-textconv", "--root", "-p", "-M", "--no-commit-id", commit.Sha, "--", .. paths.Select(Literal)], auth, ct)
            : await ReadAsync(repositoryPath, ["diff", "--no-ext-diff", "--no-textconv", "-M", commit.Parents[0], commit.Sha, "--", .. paths.Select(Literal)], auth, ct);
        return ParseDiff(output);
    }

    internal static IReadOnlyList<DiffLine> ParseDiff(string output)
    {
        var lines = new List<DiffLine>();
        var inHunk = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (lines.Count >= MaxDiffLines)
            {
                lines.Add(new DiffLine(DiffLineKind.Header, $"… modifications trop longues : seules les {MaxDiffLines} premières lignes sont affichées."));
                break;
            }
            if (line.StartsWith("@@")) { inHunk = true; lines.Add(new DiffLine(DiffLineKind.Hunk, line)); continue; }
            if (line.StartsWith("diff --git")) inHunk = false;
            if (!inHunk)
            {
                if (line.StartsWith("Binary files")) lines.Add(new DiffLine(DiffLineKind.Header, "Fichier binaire (image, archive…) : pas d'aperçu des modifications."));
                continue; // en-têtes techniques (diff --git, index, ---/+++) : inutiles pour un débutant
            }
            if (line.StartsWith('+')) lines.Add(new DiffLine(DiffLineKind.Added, line[1..]));
            else if (line.StartsWith('-')) lines.Add(new DiffLine(DiffLineKind.Removed, line[1..]));
            else if (line.StartsWith('\\')) continue; // « \ No newline at end of file »
            else if (line.Length > 0 || inHunk) lines.Add(new DiffLine(DiffLineKind.Context, line.Length > 0 ? line[1..] : ""));
        }
        while (lines.Count > 0 && lines[^1] is { Kind: DiffLineKind.Context, Text: "" }) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
