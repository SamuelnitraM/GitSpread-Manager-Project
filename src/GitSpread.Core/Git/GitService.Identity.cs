namespace GitSpread.Core.Git;

/// <summary>Commit présent seulement sur ce poste (absent de toutes les branches distantes connues).</summary>
public sealed record UnpushedCommit(string Sha, string AuthorName, string AuthorEmail, string Subject);

/// <summary>Identité du projet : réglage propre au dépôt (local) et réglage du poste (global).</summary>
public sealed record IdentitySettings(GitIdentity? Local, GitIdentity? Global)
{
    /// <summary>Identité réellement utilisée par git : la locale l'emporte sur la globale, champ par champ.</summary>
    public GitIdentity Effective => new(Local?.Name ?? Global?.Name, Local?.Email ?? Global?.Email);
    public bool HasLocal => Local is { Name: not null } or { Email: not null };
}

public sealed partial class GitService
{
    /// <summary>Variable d'environnement lue par la commande de réécriture (évite tout problème de guillemets).</summary>
    private const string AuthorVariable = "GITSPREAD_AUTHOR";

    public async Task<IdentitySettings> GetIdentitySettingsAsync(string repositoryPath, CancellationToken ct = default)
    {
        async Task<string?> Get(string scope, string key)
        {
            var r = await RunAsync(repositoryPath, ["config", scope, "--get", key], null, ct);
            return r.Succeeded && r.StandardOutput.Trim() is { Length: > 0 } v ? v : null;
        }

        var local = new GitIdentity(await Get("--local", "user.name"), await Get("--local", "user.email"));
        var global = new GitIdentity(await Get("--global", "user.name"), await Get("--global", "user.email"));
        return new IdentitySettings(
            local is { Name: null, Email: null } ? null : local,
            global is { Name: null, Email: null } ? null : global);
    }

    /// <summary>
    /// Programmes que git exécuterait pour ce dépôt, déclarés dans sa configuration locale (.git/config) : filtres,
    /// pilotes de fusion ou de comparaison, hooks déplacés. Un dépôt créé ou cloné normalement n'en a pas (sauf outils
    /// comme git-crypt) ; un dossier .git reçu tel quel (archive, clé USB) peut en cacher. La lecture ne lance rien.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListRepositoryProgramsAsync(string repositoryPath, CancellationToken ct = default)
    {
        // Noms seuls (--name-only) : un nom peut contenir des espaces (« diff.Mon Outil.textconv »). Fichiers inclus compris.
        var r = await RunAsync(repositoryPath, ["config", "--show-scope", "--name-only", "--get-regexp",
            @"^(filter|merge|diff)\..*\.(clean|smudge|process|driver|command|textconv)$"
            + @"|^core\.(hookspath|fsmonitor|pager|editor|sshcommand|askpass|gitproxy)$|^credential\..*helper$|^gpg\.(.*\.)?program$"
            + @"|^remote\..*\.(uploadpack|receivepack)$"], null, ct);
        return r.Succeeded
            ? r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r').Split('\t', 2))
                .Where(p => p.Length == 2 && p[0] is "local" or "worktree")
                .Select(p => p[1].Trim()).Distinct().ToList()
            : [];
    }

    /// <summary>Identité globale du poste (git config --global), utilisée par tous les projets sans réglage propre.</summary>
    public async Task<GitIdentity> GetGlobalIdentityAsync(CancellationToken ct = default)
    {
        async Task<string?> Get(string key)
        {
            var r = await RunAsync(Environment.CurrentDirectory, ["config", "--global", "--get", key], null, ct);
            return r.Succeeded && r.StandardOutput.Trim() is { Length: > 0 } v ? v : null;
        }
        return new GitIdentity(await Get("user.name"), await Get("user.email"));
    }

    /// <summary>Change l'identité globale du poste. Les projets qui ont leur propre identité ne sont pas concernés.</summary>
    public async Task<IReadOnlyList<GitResult>> SetGlobalIdentityAsync(string name, string email, CancellationToken ct = default) =>
    [
        await ActAsync(Environment.CurrentDirectory, ["config", "--global", "user.name", name], null, ct),
        await ActAsync(Environment.CurrentDirectory, ["config", "--global", "user.email", email], null, ct),
    ];

    /// <summary>Version de git installée (« 2.47.1.windows.1 »), ou null si git ne répond pas.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await RunAsync(Environment.CurrentDirectory, ["--version"], null, ct);
            return r.Succeeded ? r.StandardOutput.Trim().Replace("git version ", "") : null;
        }
        catch (Processes.ToolNotFoundException) { return null; }
    }

    /// <summary>Retire l'identité propre au projet : git utilisera de nouveau l'identité globale du poste.</summary>
    public async Task<IReadOnlyList<GitResult>> UnsetLocalIdentityAsync(string repositoryPath, CancellationToken ct = default)
    {
        // « --unset » échoue (code 5) si la clé n'existe pas : ce n'est pas une erreur ici.
        var results = new List<GitResult>();
        foreach (var key in new[] { "user.name", "user.email" })
        {
            var r = await ActAsync(repositoryPath, ["config", "--local", "--unset", key], null, ct);
            results.Add(r.Process.ExitCode == 5 ? r with { Process = r.Process with { ExitCode = 0 } } : r);
        }
        return results;
    }

    /// <summary>Commits de la branche courante qui ne sont sur aucune branche distante connue (du plus ancien au plus récent).</summary>
    public async Task<IReadOnlyList<UnpushedCommit>> ListUnpushedCommitsAsync(string repositoryPath, CancellationToken ct = default)
    {
        var r = await RunAsync(repositoryPath, ["log", "--reverse", "--topo-order", "--format=%H%x1f%an%x1f%ae%x1f%s", "HEAD", "--not", "--remotes", "--"], null, ct);
        if (!r.Succeeded) return []; // dépôt sans commit
        return r.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\x1f'))
            .Where(p => p.Length == 4)
            .Select(p => new UnpushedCommit(p[0], p[1], p[2], p[3]))
            .ToList();
    }

    /// <summary>
    /// Re-signe avec <paramref name="author"/> tous les commits pas encore poussés (contenu et dates inchangés).
    /// Refuse si un commit déjà publié devait être réécrit : on ne force jamais l'envoi.
    /// </summary>
    public async Task<IReadOnlyList<GitResult>> RewriteUnpushedAuthorAsync(string repositoryPath, GitIdentity author, CancellationToken ct = default)
    {
        if (author is not { Name: { } name, Email: { } email })
            throw new ArgumentException("Nom et e-mail sont nécessaires pour re-signer des commits.", nameof(author));
        if (await GetOperationAsync(repositoryPath, ct) != RepositoryOperation.None)
            throw new InvalidOperationException("Une opération (fusion, rebase…) est déjà en cours : termine-la ou abandonne-la d'abord.");
        if ((await GetStatusAsync(repositoryPath, ct)).IsDetached)
            throw new InvalidOperationException("Le projet n'est sur aucune branche (HEAD détachée).");

        var unpushed = await ListUnpushedCommitsAsync(repositoryPath, ct);
        if (unpushed.Count == 0) return [];

        // Base de la réécriture : le parent du plus ancien commit non poussé (ou la racine si rien n'a jamais été poussé).
        var oldest = unpushed[0].Sha;
        var parents = (await ReadAsync(repositoryPath, ["rev-list", "--parents", "-n", "1", oldest], ct)).Trim().Split(' ');
        string? onto = parents.Length > 1 ? parents[1] : null;

        // Garde-fou : tout ce que la réécriture toucherait doit être non poussé.
        string[] range = onto is null ? ["HEAD"] : [$"{onto}..HEAD"];
        var touched = (await ReadAsync(repositoryPath, ["rev-list", .. range, "--"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());
        var unpushedShas = unpushed.Select(c => c.Sha).ToHashSet();
        if (touched.Any(sha => !unpushedShas.Contains(sha)))
            throw new InvalidOperationException("L'historique local mélange commits envoyés et non envoyés (fusion d'une branche distante) : GitSpread ne peut pas re-signer sans toucher à des commits déjà publiés.");

        List<string> args =
        [
            "-c", "core.editor=true", "rebase", "--rebase-merges", "--autostash",
            "--exec", $"git commit --amend --no-edit --no-verify --allow-empty --author=\"${AuthorVariable}\"",
        ];
        args.Add(onto ?? "--root");

        var env = new Dictionary<string, string>(GitCredentialEnvironment.NonInteractive()) { [AuthorVariable] = $"{name} <{email}>" };
        var result = new GitResult(
            $"git rebase --rebase-merges --autostash --exec \"git commit --amend --no-edit --author=…\" {onto?[..7] ?? "--root"}",
            await runner.RunAsync(Request(repositoryPath, args, env, TimeSpan.FromMinutes(5)), ct));

        // En cas d'échec, on ne laisse jamais un rebase à moitié fait.
        if (!result.Succeeded && await GetOperationAsync(repositoryPath, ct) == RepositoryOperation.Rebase)
            return [result, await ActAsync(repositoryPath, ["rebase", "--abort"], null, ct)];
        return [result];
    }
}
