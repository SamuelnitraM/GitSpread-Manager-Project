using GitSpread.Core.Processes;

namespace GitSpread.Core.Git;

public sealed record GitStatus
{
    public string? Branch { get; init; }
    public bool IsDetached { get; init; }
    public bool HasCommits { get; init; } = true;
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public int Staged { get; init; }
    public int Modified { get; init; }
    public int Untracked { get; init; }
    public int Conflicts { get; init; }

    public bool IsClean => Staged + Modified + Untracked + Conflicts == 0;
}

public enum ChangeKind { Modified, Added, Deleted, Renamed, Copied, TypeChanged, Untracked, Conflicted }

/// <summary>Un fichier modifié dans l'arbre de travail ou l'index.</summary>
public sealed record FileChange(string Path, ChangeKind Kind)
{
    /// <summary>Ancien chemin pour un renommage ou une copie.</summary>
    public string? OriginalPath { get; init; }

    /// <summary>Nouveau dossier de dépendances ou de compilation (node_modules/, vendor/, bin/...) : rarement à commiter.</summary>
    public bool IsLikelyGenerated =>
        Kind == ChangeKind.Untracked && Path.EndsWith('/')
        && Projects.ScanOptions.DefaultExclusions.Contains(Path.TrimEnd('/').Split('/')[^1], StringComparer.OrdinalIgnoreCase);

    /// <summary>Chemins à passer à git pour inclure ce changement dans un commit.</summary>
    public IEnumerable<string> Pathspecs => OriginalPath is null || Kind == ChangeKind.Copied ? [Path] : [Path, OriginalPath];
}

public sealed record CommitInfo(string Hash, string ShortHash, string Author, string Email, DateTimeOffset Date, string Subject);

public sealed record GitIdentity(string? Name, string? Email);

public sealed record GitRemote(string Name, string Url);

/// <summary>Résultat d'une commande git lancée à la demande de l'utilisateur, avec la commande telle qu'affichée.</summary>
public sealed record GitResult(string Command, ProcessResult Process)
{
    public bool Succeeded => Process.Succeeded;
    public string Output => $"{Process.StandardOutput}\n{Process.StandardError}".Trim();

    /// <summary>Explication en français de l'erreur, si elle est reconnue.</summary>
    public string? Explanation => Succeeded ? null : GitErrorExplainer.Explain(Output);

    public GitResult EnsureSuccess() => Succeeded ? this : throw new GitCommandException(
        Explanation ?? $"{Command} a échoué : {Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim()}", Process);
}

public sealed class GitCommandException(string message, ProcessResult result) : Exception(message)
{
    public ProcessResult Result { get; } = result;
}

/// <summary>
/// Commandes git sur un dépôt local. Les opérations réseau (fetch, pull, push) reçoivent l'environnement
/// d'authentification du bon compte (voir <see cref="GitCredentialResolver"/>).
/// </summary>
public sealed partial class GitService(IProcessRunner runner)
{
    public const string Executable = "git";
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(5);

    // ------------------------------------------------------------ lecture

    public async Task<GitStatus> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        ParseStatus(await ReadAsync(repositoryPath, ["status", "--porcelain=v2", "--branch"], cancellationToken));

    public async Task<IReadOnlyList<FileChange>> GetChangesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        ParseChanges(await ReadAsync(repositoryPath, ["status", "--porcelain=v2", "-z", "--untracked-files=normal"], cancellationToken));

    public async Task<CommitInfo?> GetLastCommitAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(repositoryPath, ["log", "-1", "--format=%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%s"], null, cancellationToken);
        if (!result.Succeeded) return null; // dépôt sans commit
        var parts = result.StandardOutput.TrimEnd('\r', '\n').Split('\x1f');
        return parts.Length == 6 && DateTimeOffset.TryParse(parts[4], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? new CommitInfo(parts[0], parts[1], parts[2], parts[3], date, parts[5])
            : null;
    }

    /// <summary>Identité utilisée pour les commits de ce dépôt (config locale, sinon globale).</summary>
    public async Task<GitIdentity> GetIdentityAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var name = await RunAsync(repositoryPath, ["config", "--get", "user.name"], null, cancellationToken);
        var email = await RunAsync(repositoryPath, ["config", "--get", "user.email"], null, cancellationToken);
        return new GitIdentity(Value(name), Value(email));

        static string? Value(ProcessResult r) => r.Succeeded && r.StandardOutput.Trim() is { Length: > 0 } v ? v : null;
    }

    public async Task<IReadOnlyList<GitRemote>> GetRemotesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        // « nom<TAB>url (fetch) » : l'URL peut contenir des espaces (chemins locaux), on ne découpe que sur la tabulation.
        var output = await ReadAsync(repositoryPath, ["remote", "-v"], cancellationToken);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Split('\t', 2))
            .Where(p => p.Length == 2 && p[1].EndsWith(" (fetch)"))
            .Select(p => new GitRemote(p[0], p[1][..^" (fetch)".Length]))
            .ToList();
    }

    // ------------------------------------------------------------ actions locales

    public Task<GitResult> InitAsync(string directory, string initialBranch = "main", CancellationToken cancellationToken = default) =>
        ActAsync(directory, ["init", "-b", initialBranch], null, cancellationToken);

    /// <summary>
    /// Indexe puis commite. Sans <paramref name="paths"/>, tous les changements sont inclus ; sinon seuls ces
    /// chemins le sont, même si d'autres fichiers étaient déjà indexés.
    /// </summary>
    public async Task<IReadOnlyList<GitResult>> CommitAsync(string repositoryPath, string message,
        IReadOnlyCollection<string>? paths = null, bool allowEmpty = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Le message de commit est obligatoire.", nameof(message));

        var all = paths is null || paths.Count == 0;
        var add = await ActAsync(repositoryPath, all ? ["add", "--all"] : ["add", "--all", "--", .. paths!.Select(Literal)], null, cancellationToken);
        if (!add.Succeeded) return [add];

        List<string> commit = ["commit", "-m", message.Trim()];
        if (allowEmpty) commit.Add("--allow-empty");
        if (!all) commit.AddRange(["--", .. paths!.Select(Literal)]);
        return [add, await ActAsync(repositoryPath, commit, null, cancellationToken)];
    }

    public Task<GitResult> AddRemoteAsync(string repositoryPath, string name, string url, CancellationToken cancellationToken = default) =>
        ActAsync(repositoryPath, ["remote", "add", name, url], null, cancellationToken);

    /// <summary>Fixe l'identité des commits pour ce seul dépôt (config locale).</summary>
    public async Task<IReadOnlyList<GitResult>> SetLocalIdentityAsync(string repositoryPath, string name, string email,
        CancellationToken cancellationToken = default) =>
    [
        await ActAsync(repositoryPath, ["config", "--local", "user.name", name], null, cancellationToken),
        await ActAsync(repositoryPath, ["config", "--local", "user.email", email], null, cancellationToken),
    ];

    // ------------------------------------------------------------ actions réseau

    public Task<GitResult> FetchAsync(string repositoryPath, string remote, IReadOnlyDictionary<string, string> auth,
        CancellationToken cancellationToken = default) =>
        ActAsync(repositoryPath, ["fetch", "--prune", remote], auth, cancellationToken, NetworkTimeout);

    /// <summary>Récupère et avance la branche uniquement si c'est sans fusion (jamais de commit de merge implicite).</summary>
    public Task<GitResult> PullAsync(string repositoryPath, IReadOnlyDictionary<string, string> auth,
        CancellationToken cancellationToken = default) =>
        ActAsync(repositoryPath, ["pull", "--ff-only"], auth, cancellationToken, NetworkTimeout);

    /// <summary>Pousse la branche courante. Jamais de --force. Lie la branche au distant si ce n'est pas déjà fait.</summary>
    public Task<GitResult> PushAsync(string repositoryPath, string remote, string branch, bool setUpstream,
        IReadOnlyDictionary<string, string> auth, CancellationToken cancellationToken = default) =>
        ActAsync(repositoryPath, setUpstream ? ["push", "--set-upstream", remote, branch] : ["push", remote, branch],
            auth, cancellationToken, NetworkTimeout);

    // ------------------------------------------------------------ analyse

    internal static GitStatus ParseStatus(string porcelainV2)
    {
        var status = new GitStatus();
        foreach (var line in porcelainV2.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# branch.oid "))
                status = status with { HasCommits = line[13..].Trim() != "(initial)" };
            else if (line.StartsWith("# branch.head "))
            {
                var head = line[14..].Trim();
                status = head == "(detached)" ? status with { IsDetached = true } : status with { Branch = head };
            }
            else if (line.StartsWith("# branch.upstream "))
                status = status with { Upstream = line[18..].Trim() };
            else if (line.StartsWith("# branch.ab "))
            {
                var ab = line[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (ab.Length == 2 && int.TryParse(ab[0].TrimStart('+'), out var ahead) && int.TryParse(ab[1].TrimStart('-'), out var behind))
                    status = status with { Ahead = ahead, Behind = behind };
            }
            else if (line.Length > 3 && line[0] is '1' or '2')
            {
                // « 1 XY ... » : X = index (staged), Y = arbre de travail, « . » = inchangé
                status = status with
                {
                    Staged = status.Staged + (line[2] != '.' ? 1 : 0),
                    Modified = status.Modified + (line[3] != '.' ? 1 : 0),
                };
            }
            else if (line.StartsWith("u "))
                status = status with { Conflicts = status.Conflicts + 1 };
            else if (line.StartsWith("? "))
                status = status with { Untracked = status.Untracked + 1 };
        }
        return status;
    }

    /// <summary>Analyse « status --porcelain=v2 -z » (chemins séparés par NUL : espaces et accents sans échappement).</summary>
    internal static IReadOnlyList<FileChange> ParseChanges(string porcelainV2Z)
    {
        var changes = new List<FileChange>();
        var fields = porcelainV2Z.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var entry = fields[i];
            if (entry.Length < 2) continue;
            switch (entry[0])
            {
                case '1': // 1 XY sub mH mI mW hH hI path
                    changes.Add(new FileChange(Field(entry, 8), Kind(entry[2], entry[3])));
                    break;
                case '2': // 2 XY sub mH mI mW hH hI Xscore path, puis chemin d'origine dans le champ suivant
                    var path = Field(entry, 9);
                    var original = i + 1 < fields.Length ? fields[++i] : null;
                    var copied = Field(entry, 8).StartsWith('C'); // score « R100 » ou « C75 »
                    changes.Add(new FileChange(path, copied ? ChangeKind.Copied : ChangeKind.Renamed) { OriginalPath = original });
                    break;
                case 'u': // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    changes.Add(new FileChange(Field(entry, 10), ChangeKind.Conflicted));
                    break;
                case '?':
                    changes.Add(new FileChange(entry[2..], ChangeKind.Untracked));
                    break;
            }
        }
        return changes;

        // Le chemin est le dernier champ et peut contenir des espaces : on saute les n premiers.
        static string Field(string entry, int skip)
        {
            var index = 0;
            for (var n = 0; n < skip; n++) index = entry.IndexOf(' ', index) + 1;
            return entry[index..];
        }

        static ChangeKind Kind(char x, char y) => (y != '.' ? y : x) switch
        {
            'A' => ChangeKind.Added,
            'D' => ChangeKind.Deleted,
            'T' => ChangeKind.TypeChanged,
            _ => x == 'A' ? ChangeKind.Added : ChangeKind.Modified,
        };
    }

    // ------------------------------------------------------------ exécution

    /// <summary>
    /// Options ajoutées à chaque commande git. Pas de « fsmonitor » : un dépôt reçu d'ailleurs ne peut pas lancer de
    /// programme quand GitSpread lit son état.
    /// </summary>
    internal static readonly IReadOnlyList<string> CommonOptions =
        ["--no-optional-locks", "-c", "core.quotepath=false", "-c", "core.fsmonitor=false"];

    /// <summary>
    /// Chemin de fichier pris tel quel, jamais comme un motif : « [id].tsx » ne désigne pas aussi « i.tsx ».
    /// (Pas d'option --literal-pathspecs globale : elle empêche « git stash -u » de ranger les fichiers non suivis.)
    /// </summary>
    internal static string Literal(string path) => ":(literal)" + path;

    private async Task<ProcessResult> RunAsync(string directory, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? auth, CancellationToken ct, TimeSpan? timeout = null)
    {
        // Un clone se fait dans un dossier parent : la config d'un éventuel dépôt englobant ne le concerne pas.
        if (args is not ["clone", ..]) await EnsureTokenStaysWithGitHubAsync(directory, auth, ct);
        return await runner.RunAsync(Request(directory, args, auth, timeout), ct);
    }

    /// <summary>Refuse d'envoyer le jeton si le dépôt détourne lui-même la connexion (voir <see cref="NetworkOverrideAsync"/>).</summary>
    private async Task EnsureTokenStaysWithGitHubAsync(string directory, IReadOnlyDictionary<string, string>? auth, CancellationToken ct)
    {
        if (auth is null || !auth.Values.Any(v => v.StartsWith("Authorization:", StringComparison.Ordinal))) return;
        if (await NetworkOverrideAsync(directory, ct) is { } key)
            throw new GitCommandException(
                $"Ce dépôt modifie lui-même ses réglages de connexion ({key} dans sa configuration) : GitSpread n'y envoie pas ton jeton GitHub. "
                + "Si tu n'as pas fait ce réglage toi-même, ce dossier n'est pas fiable.",
                new ProcessResult(-1, "", $"réglage réseau local : {key}"));
    }

    /// <summary>
    /// Réglage réseau déclaré par la configuration du dépôt lui-même (proxy, certificats, redirection d'adresse, vérification
    /// du certificat désactivée) : il pourrait envoyer le jeton à un autre serveur. Les fichiers inclus par le dépôt comptent
    /// aussi ; les réglages du poste (config globale) non. Null si aucun.
    /// </summary>
    private async Task<string?> NetworkOverrideAsync(string directory, CancellationToken ct)
    {
        if ((await RepositoryConfigAsync(directory,
                @"^http\.(.*\.)?(proxy|proxysslcainfo|sslcainfo|sslcapath|sslbackend|schannelusesslcainfo|sslcert|sslkey|curloptresolve)$|^remote\..*\.proxy$", ct) ?? [])
            .FirstOrDefault() is { Key: { } key }) return key;
        // sslVerify n'est un problème que s'il désactive la vérification : git interprète lui-même la valeur (« 0k », « off », clé seule…).
        // Une valeur illisible (même pour une autre adresse) rend la lecture impossible : on refuse plutôt que de supposer.
        return await RepositoryConfigAsync(directory, @"^http\.(.*\.)?sslverify$", ct, asBoolean: true) is { } verify
            ? verify.FirstOrDefault(c => c.Value == "false").Key
            : "http.sslVerify (valeur illisible)";
    }

    /// <summary>
    /// Réglages déclarés par le dépôt lui-même (.git/config, fichiers qu'il inclut, config de worktree), à l'exclusion de ceux
    /// du poste et de ceux que GitSpread passe en ligne de commande. Lire la configuration ne lance rien.
    /// </summary>
    /// <param name="asBoolean">Valeurs lues comme des booléens par git (« true » ou « false ») ; null si git ne peut pas les lire.</param>
    internal async Task<IReadOnlyList<(string Key, string Value)>?> RepositoryConfigAsync(string directory, string keyPattern, CancellationToken ct,
        bool asBoolean = false)
    {
        // -z : « portée\0clé\nvaleur\0 », sans ambiguïté même si l'adresse d'une clé contient des espaces.
        var r = await runner.RunAsync(Request(directory, asBoolean
            ? ["config", "--show-scope", "-z", "--type=bool", "--get-regexp", keyPattern]
            : ["config", "--show-scope", "-z", "--get-regexp", keyPattern], null), ct);
        if (r.ExitCode == 1) return []; // aucun réglage
        if (!r.Succeeded) return asBoolean ? null : []; // valeur illisible, ou pas un dépôt
        var fields = r.StandardOutput.Split('\0');
        var settings = new List<(string, string)>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            if (fields[i] is not ("local" or "worktree")) continue;
            var entry = fields[i + 1];
            var newline = entry.IndexOf('\n');
            settings.Add(newline < 0 ? (entry, "") : (entry[..newline], entry[(newline + 1)..]));
        }
        return settings;
    }

    /// <summary>Requête git avec les options communes. Un dossier de projet disparu donne une erreur claire.</summary>
    internal ProcessRequest Request(string directory, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? auth, TimeSpan? timeout = null)
    {
        if (!Directory.Exists(directory))
            throw new GitCommandException($"Le dossier « {directory} » n'existe plus (déplacé ou supprimé ?). Clique sur « Actualiser ».",
                new ProcessResult(-1, "", $"dossier introuvable : {directory}"));
        return new ProcessRequest(Executable, [.. CommonOptions, .. args])
        {
            WorkingDirectory = directory,
            Environment = auth ?? GitCredentialEnvironment.NonInteractive(),
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
    }

    private async Task<GitResult> ActAsync(string directory, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? auth, CancellationToken ct, TimeSpan? timeout = null) =>
        new(Display(args), await RunAsync(directory, args, auth, ct, timeout));

    private Task<string> ReadAsync(string repositoryPath, IReadOnlyList<string> args, CancellationToken ct) =>
        ReadAsync(repositoryPath, args, null, ct);

    /// <param name="auth">Pour une copie légère (F6c) : git peut avoir à télécharger le contenu d'un fichier.</param>
    private async Task<string> ReadAsync(string repositoryPath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? auth, CancellationToken ct)
    {
        var result = await RunAsync(repositoryPath, args, auth, ct);
        if (result.Succeeded) return result.StandardOutput;

        var error = result.StandardError.Trim();
        throw new GitCommandException(error.Contains("dubious ownership")
            ? $"git refuse ce dossier car il appartient à un autre utilisateur Windows. Pour l'autoriser : git config --global --add safe.directory \"{repositoryPath.Replace('\\', '/')}\""
            : $"git {string.Join(' ', args)} a échoué : {error.Split('\n')[0]}", result);
    }

    // Commande telle qu'on la taperait : la marque « :(literal) » des chemins est retirée pour rester lisible.
    private static string Display(IEnumerable<string> args) =>
        "git " + string.Join(' ', args.Select(a => a.StartsWith(":(literal)", StringComparison.Ordinal) ? a[10..] : a).Select(a => a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c == '"') ? $"\"{a.Replace("\"", "\\\"")}\"" : a));
}
