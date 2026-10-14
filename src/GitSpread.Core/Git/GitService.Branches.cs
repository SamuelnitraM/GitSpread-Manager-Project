namespace GitSpread.Core.Git;

/// <summary>Une branche, locale et/ou présente sur le distant.</summary>
public sealed record BranchInfo(string Name)
{
    public bool IsLocal { get; init; }
    public bool IsCurrent { get; init; }

    /// <summary>Branche de suivi distante du même nom (ex. origin/feature), si elle existe.</summary>
    public string? RemoteRef { get; init; }

    /// <summary>Branche distante configurée comme amont de la branche locale.</summary>
    public string? Upstream { get; init; }
    public bool UpstreamGone { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public DateTimeOffset? LastCommitDate { get; init; }

    public bool IsRemoteOnly => !IsLocal;

    /// <summary>Référence à utiliser dans les commandes git (locale si possible).</summary>
    public string Ref => IsLocal ? Name : RemoteRef!;
}

/// <summary>Opération git laissée en cours dans le dépôt (souvent après des conflits).</summary>
public enum RepositoryOperation { None, Merge, Rebase, CherryPick, Revert }

public sealed record StashEntry(string Ref, string Message);

public sealed record RemoteHead(string Branch, string Sha);

public sealed partial class GitService
{
    public const string StashPrefix = "GitSpread · ";

    // ------------------------------------------------------------ branches

    public async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string repositoryPath, CancellationToken ct = default) =>
        ParseBranches(await ReadAsync(repositoryPath,
            ["for-each-ref", "--format=%(refname)%1f%(refname:short)%1f%(upstream:short)%1f%(upstream:track,nobracket)%1f%(HEAD)%1f%(committerdate:iso-strict)",
             "refs/heads", "refs/remotes"], ct));

    /// <summary>Branche principale : celle désignée par le distant (origin/HEAD), sinon main, sinon master.</summary>
    public async Task<string?> GetDefaultBranchAsync(string repositoryPath, string remote = "origin", CancellationToken ct = default)
    {
        var head = await RunAsync(repositoryPath, ["symbolic-ref", "--short", $"refs/remotes/{remote}/HEAD"], null, ct);
        if (head.Succeeded && head.StandardOutput.Trim() is { Length: > 0 } full && full.StartsWith(remote + "/"))
            return full[(remote.Length + 1)..];

        var branches = await ListBranchesAsync(repositoryPath, ct);
        return branches.FirstOrDefault(b => b.Name == "main")?.Name
            ?? branches.FirstOrDefault(b => b.Name == "master")?.Name;
    }

    /// <summary>Nombre de commits présents seulement dans <paramref name="a"/>, et seulement dans <paramref name="b"/>.</summary>
    public async Task<(int OnlyInA, int OnlyInB)> CompareAsync(string repositoryPath, string a, string b, CancellationToken ct = default)
    {
        var parts = (await ReadAsync(repositoryPath, ["rev-list", "--left-right", "--count", $"{a}...{b}", "--"], ct))
            .Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    public async Task<bool> IsAncestorAsync(string repositoryPath, string ancestor, string descendant, CancellationToken ct = default) =>
        (await RunAsync(repositoryPath, ["merge-base", "--is-ancestor", ancestor, descendant], null, ct)).ExitCode == 0;

    public async Task<bool> HasCommitAsync(string repositoryPath, string sha, CancellationToken ct = default) =>
        (await RunAsync(repositoryPath, ["cat-file", "-e", $"{sha}^{{commit}}"], null, ct)).ExitCode == 0;

    /// <summary>Simule la fusion de <paramref name="source"/> dans <paramref name="target"/> sans toucher au dossier :
    /// renvoie les fichiers qui seraient en conflit (liste vide = fusion propre).</summary>
    public async Task<IReadOnlyList<string>> PredictConflictsAsync(string repositoryPath, string target, string source, CancellationToken ct = default)
    {
        var r = await RunAsync(repositoryPath, ["merge-tree", "--write-tree", "--name-only", "--no-messages", target, source], null, ct);
        if (r.ExitCode == 0) return [];
        if (r.ExitCode != 1)
            throw new GitCommandException(r.StandardError.Contains("unrelated histories")
                ? "Ces deux branches n'ont aucun commit en commun (historiques sans rapport) : GitSpread ne les fusionne pas."
                : $"Impossible de simuler la fusion : {r.StandardError.Trim()}", r);
        return r.StandardOutput.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
    }

    public Task<GitResult> SwitchAsync(string repositoryPath, BranchInfo branch, CancellationToken ct = default) =>
        ActAsync(repositoryPath, branch.IsLocal ? ["switch", branch.Name] : ["switch", "-c", branch.Name, "--track", branch.RemoteRef!], null, ct);

    public Task<GitResult> SwitchAsync(string repositoryPath, string localBranch, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["switch", localBranch], null, ct);

    public Task<GitResult> CreateBranchAsync(string repositoryPath, string name, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["switch", "-c", name], null, ct);

    /// <summary>Avance <paramref name="target"/> jusqu'à <paramref name="source"/> sans changer de branche
    /// (refusé par git si ce n'est pas une avance rapide : rien ne peut être perdu).</summary>
    public Task<GitResult> FastForwardBranchAsync(string repositoryPath, string target, string source, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["fetch", ".", $"{source}:{target}"], null, ct);

    public Task<GitResult> MergeFastForwardOnlyAsync(string repositoryPath, string source, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["merge", "--ff-only", source], null, ct);

    public Task<GitResult> MergeAsync(string repositoryPath, string source, string message, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["merge", "--no-ff", "-m", message, source], null, ct);

    public Task<GitResult> AbortMergeAsync(string repositoryPath, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["merge", "--abort"], null, ct);

    /// <summary>Termine une fusion dont les conflits ont été résolus (message de fusion proposé par git).</summary>
    public async Task<IReadOnlyList<GitResult>> ConcludeMergeAsync(string repositoryPath, CancellationToken ct = default)
    {
        var add = await ActAsync(repositoryPath, ["add", "--all"], null, ct);
        return add.Succeeded ? [add, await ActAsync(repositoryPath, ["commit", "--no-edit"], null, ct)] : [add];
    }

    /// <summary>Supprime une branche locale déjà intégrée (git refuse si elle contient du travail non fusionné).</summary>
    public Task<GitResult> DeleteBranchAsync(string repositoryPath, string name, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["branch", "-d", name], null, ct);

    public async Task<RepositoryOperation> GetOperationAsync(string repositoryPath, CancellationToken ct = default)
    {
        async Task<bool> Exists(string gitPath)
        {
            var r = await RunAsync(repositoryPath, ["rev-parse", "--git-path", gitPath], null, ct);
            if (!r.Succeeded) return false;
            var p = Path.Combine(repositoryPath, r.StandardOutput.Trim());
            return File.Exists(p) || Directory.Exists(p);
        }

        if (await Exists("MERGE_HEAD")) return RepositoryOperation.Merge;
        if (await Exists("rebase-merge") || await Exists("rebase-apply")) return RepositoryOperation.Rebase;
        if (await Exists("CHERRY_PICK_HEAD")) return RepositoryOperation.CherryPick;
        if (await Exists("REVERT_HEAD")) return RepositoryOperation.Revert;
        return RepositoryOperation.None;
    }

    public Task<GitResult> AbortOperationAsync(string repositoryPath, RepositoryOperation operation, CancellationToken ct = default) =>
        ActAsync(repositoryPath, operation switch
        {
            RepositoryOperation.Rebase => ["rebase", "--abort"],
            RepositoryOperation.CherryPick => ["cherry-pick", "--abort"],
            RepositoryOperation.Revert => ["revert", "--abort"],
            _ => ["merge", "--abort"],
        }, null, ct);

    // ------------------------------------------------------------ modifications mises de côté (stash)

    /// <summary>Met de côté les modifications (y compris les nouveaux fichiers), étiquetées avec la branche.</summary>
    public Task<GitResult> StashAsync(string repositoryPath, string branch, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["stash", "push", "--include-untracked", "-m", StashPrefix + branch], null, ct);

    public async Task<IReadOnlyList<StashEntry>> ListStashesAsync(string repositoryPath, CancellationToken ct = default)
    {
        var r = await RunAsync(repositoryPath, ["stash", "list", "--format=%gd%x1f%s"], null, ct);
        if (!r.Succeeded) return [];
        return r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\x1f'))
            .Where(p => p.Length == 2)
            .Select(p => new StashEntry(p[0], p[1]))
            .ToList();
    }

    /// <summary>Dernière mise de côté faite par GitSpread pour cette branche.</summary>
    public async Task<StashEntry?> FindStashForBranchAsync(string repositoryPath, string branch, CancellationToken ct = default) =>
        (await ListStashesAsync(repositoryPath, ct)).FirstOrDefault(s => s.Message.EndsWith(StashPrefix + branch, StringComparison.Ordinal));

    /// <summary>Réapplique puis supprime la mise de côté (conservée par git en cas de conflit).</summary>
    public Task<GitResult> PopStashAsync(string repositoryPath, StashEntry stash, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["stash", "pop", stash.Ref], null, ct);

    // ------------------------------------------------------------ remotes

    public Task<GitResult> RenameRemoteAsync(string repositoryPath, string from, string to, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["remote", "rename", from, to], null, ct);

    public Task<GitResult> RemoveRemoteAsync(string repositoryPath, string name, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["remote", "remove", name], null, ct);

    public Task<GitResult> SetRemoteUrlAsync(string repositoryPath, string name, string url, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["remote", "set-url", name, url], null, ct);

    /// <summary>Branches présentes sur un dépôt distant, sans rien télécharger.</summary>
    public async Task<IReadOnlyList<RemoteHead>> ListRemoteHeadsAsync(string repositoryPath, string url,
        IReadOnlyDictionary<string, string> auth, CancellationToken ct = default)
    {
        var r = (await ActAsync(repositoryPath, ["ls-remote", "--heads", url], auth, ct, TimeSpan.FromMinutes(2))).EnsureSuccess();
        return r.Process.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split('\t'))
            .Where(p => p.Length == 2 && p[1].StartsWith("refs/heads/"))
            .Select(p => new RemoteHead(p[1]["refs/heads/".Length..], p[0]))
            .ToList();
    }

    /// <summary>Pousse toutes les branches locales (en les liant au distant, sauf pour un lien secondaire) puis les tags. Jamais de --force.</summary>
    public async Task<IReadOnlyList<GitResult>> PushAllAsync(string repositoryPath, string remote,
        IReadOnlyDictionary<string, string> auth, CancellationToken ct = default, bool setUpstream = true)
    {
        var branches = await ActAsync(repositoryPath, setUpstream ? ["push", "--set-upstream", remote, "--all"] : ["push", remote, "--all"], auth, ct, NetworkTimeout);
        return branches.Succeeded ? [branches, await ActAsync(repositoryPath, ["push", remote, "--tags"], auth, ct, NetworkTimeout)] : [branches];
    }

    /// <summary>Pousse une branche locale vers une branche distante éventuellement différente.</summary>
    public Task<GitResult> PushToAsync(string repositoryPath, string remote, string localBranch, string remoteBranch,
        IReadOnlyDictionary<string, string> auth, CancellationToken ct = default, bool setUpstream = true) =>
        ActAsync(repositoryPath, setUpstream ? ["push", "--set-upstream", remote, $"{localBranch}:{remoteBranch}"] : ["push", remote, $"{localBranch}:{remoteBranch}"],
            auth, ct, NetworkTimeout);

    // ------------------------------------------------------------ analyse

    internal static IReadOnlyList<BranchInfo> ParseBranches(string output)
    {
        var locals = new List<BranchInfo>();
        var remotes = new List<(string Remote, string Name, string Short, DateTimeOffset? Date)>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\x1f');
            if (f.Length < 6) continue;
            DateTimeOffset? date = DateTimeOffset.TryParse(f[5], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

            if (f[0].StartsWith("refs/heads/"))
            {
                var (ahead, behind, gone) = ParseTrack(f[3]);
                locals.Add(new BranchInfo(f[0]["refs/heads/".Length..])
                {
                    IsLocal = true,
                    IsCurrent = f[4] == "*",
                    Upstream = f[2].Length > 0 ? f[2] : null,
                    UpstreamGone = gone,
                    Ahead = ahead,
                    Behind = behind,
                    LastCommitDate = date,
                });
            }
            else if (f[0].StartsWith("refs/remotes/"))
            {
                var rest = f[0]["refs/remotes/".Length..];
                var slash = rest.IndexOf('/');
                if (slash < 0 || rest.EndsWith("/HEAD")) continue;
                remotes.Add((rest[..slash], rest[(slash + 1)..], rest, date));
            }
        }

        var result = locals.Select(l => l with
        {
            RemoteRef = remotes.Where(r => r.Name == l.Name).OrderBy(r => r.Remote != "origin").Select(r => r.Short).FirstOrDefault(),
        }).ToList();

        // Branches qui n'existent que sur le distant (poussées depuis un autre poste, par exemple)
        foreach (var group in remotes.Where(r => locals.All(l => l.Name != r.Name)).GroupBy(r => r.Name))
        {
            var r = group.OrderBy(x => x.Remote != "origin").First();
            result.Add(new BranchInfo(r.Name) { RemoteRef = r.Short, LastCommitDate = r.Date });
        }

        return result.OrderByDescending(b => b.IsCurrent).ThenBy(b => b.IsRemoteOnly).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (int Ahead, int Behind, bool Gone) ParseTrack(string track)
    {
        if (track == "gone") return (0, 0, true);
        int ahead = 0, behind = 0;
        foreach (var part in track.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(' ');
            if (kv.Length != 2 || !int.TryParse(kv[1], out var n)) continue;
            if (kv[0] == "ahead") ahead = n;
            else if (kv[0] == "behind") behind = n;
        }
        return (ahead, behind, false);
    }
}
