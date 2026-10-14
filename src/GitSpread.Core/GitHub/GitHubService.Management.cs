using System.Text.Json;
using GitSpread.Core.Accounts;

namespace GitSpread.Core.GitHub;

/// <summary>Détail d'un repo GitHub, pour le panneau « Repos sur GitHub ».</summary>
public sealed record GitHubRepositoryDetails(string Owner, string Name, RepositoryVisibility Visibility)
{
    public string FullName => $"{Owner}/{Name}";
    public string Url { get; init; } = "";
    public string SshUrl { get; init; } = "";
    public string? Description { get; init; }
    public string? Homepage { get; init; }
    public IReadOnlyList<string> Topics { get; init; } = [];
    public string? DefaultBranch { get; init; }
    public bool IsArchived { get; init; }
    public bool IsFork { get; init; }
    public string? Parent { get; init; }
    public DateTimeOffset? PushedAt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public int Stars { get; init; }
    public int Forks { get; init; }
    public long DiskUsageKb { get; init; }

    /// <summary>ADMIN, MAINTAIN, WRITE, TRIAGE ou READ : ce que le compte a le droit de faire.</summary>
    public string? Permission { get; init; }
    public bool IsAdmin => Permission == "ADMIN";
}

/// <summary>Gestion des repos (F4) : toujours avec le token du compte concerné.</summary>
public sealed partial class GitHubService
{
    private const string DetailFields =
        "name,owner,description,visibility,defaultBranchRef,repositoryTopics,homepageUrl,isArchived,isFork,parent,pushedAt,createdAt,url,sshUrl,diskUsage,stargazerCount,forkCount,viewerPermission";

    public async Task<GitHubRepositoryDetails> GetRepositoryDetailsAsync(GitHubAccount account, string fullName, CancellationToken ct = default)
    {
        var env = await EnvironmentForAsync(account, ct);
        return ParseDetails(await gh.RunCheckedAsync(["repo", "view", fullName, "--json", DetailFields], env, ct));
    }

    /// <summary>Renomme le repo (GitHub redirige l'ancienne adresse, mais les liens locaux doivent suivre).</summary>
    public async Task RenameRepositoryAsync(GitHubAccount account, string fullName, string newName, CancellationToken ct = default) =>
        await gh.RunCheckedAsync(["repo", "rename", newName, "--repo", fullName, "--yes"], await EnvironmentForAsync(account, ct), ct);

    /// <summary>Description, site web et sujets. Null = inchangé ; chaîne vide = effacé.</summary>
    public async Task EditRepositoryAsync(GitHubAccount account, string fullName, string? description, string? homepage,
        IReadOnlyCollection<string> addTopics, IReadOnlyCollection<string> removeTopics, CancellationToken ct = default)
    {
        var args = BuildEditArguments(fullName, description, homepage, addTopics, removeTopics);
        if (args.Count > 3) await gh.RunCheckedAsync(args, await EnvironmentForAsync(account, ct), ct);
    }

    public static List<string> BuildEditArguments(string fullName, string? description, string? homepage,
        IReadOnlyCollection<string> addTopics, IReadOnlyCollection<string> removeTopics)
    {
        List<string> args = ["repo", "edit", fullName];
        if (description is not null) args.AddRange(["--description", description]);
        if (homepage is not null) args.AddRange(["--homepage", homepage]);
        if (addTopics.Count > 0) args.AddRange(["--add-topic", string.Join(',', addTopics)]);
        if (removeTopics.Count > 0) args.AddRange(["--remove-topic", string.Join(',', removeTopics)]);
        return args;
    }

    public async Task SetVisibilityAsync(GitHubAccount account, string fullName, RepositoryVisibility visibility, CancellationToken ct = default) =>
        await gh.RunCheckedAsync(["repo", "edit", fullName, "--visibility", visibility.ToString().ToLowerInvariant(), "--accept-visibility-change-consequences"],
            await EnvironmentForAsync(account, ct), ct);

    public async Task SetArchivedAsync(GitHubAccount account, string fullName, bool archived, CancellationToken ct = default) =>
        await gh.RunCheckedAsync(["repo", archived ? "archive" : "unarchive", fullName, "--yes"], await EnvironmentForAsync(account, ct), ct);

    /// <summary>Suppression définitive. Nécessite l'autorisation delete_repo sur le compte.</summary>
    public async Task DeleteRepositoryAsync(GitHubAccount account, string fullName, CancellationToken ct = default)
    {
        if (!account.HasScope(ScopeCatalog.DeleteRepo))
            throw new InvalidOperationException($"Le compte {account.Login} n'a pas l'autorisation « Suppression de repos » (delete_repo).");
        await gh.RunCheckedAsync(["repo", "delete", fullName, "--yes"], await EnvironmentForAsync(account, ct), ct);
    }

    /// <summary>Renomme une branche sur GitHub (ex. master → main) : la branche par défaut et les pull requests suivent.</summary>
    public async Task RenameBranchAsync(GitHubAccount account, string fullName, string from, string to, CancellationToken ct = default) =>
        await gh.RunCheckedAsync(["api", "--method", "POST", $"repos/{RepoPath(fullName)}/branches/{BranchPath(from)}/rename", "-f", $"new_name={to}"],
            await EnvironmentForAsync(account, ct), ct);

    internal static GitHubRepositoryDetails ParseDetails(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        DateTimeOffset? Date(string name) => Str(name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        int Int(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        var topics = e.TryGetProperty("repositoryTopics", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.TryGetProperty("name", out var n) ? n.GetString() : null)
                .OfType<string>().ToList()
            : [];
        string? parent = e.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.Object
            ? $"{(p.TryGetProperty("owner", out var po) ? po.GetProperty("login").GetString() : "?")}/{p.GetProperty("name").GetString()}"
            : null;

        return new GitHubRepositoryDetails(
            e.GetProperty("owner").GetProperty("login").GetString()!,
            e.GetProperty("name").GetString()!,
            Str("visibility")?.ToUpperInvariant() switch { "PRIVATE" => RepositoryVisibility.Private, "INTERNAL" => RepositoryVisibility.Internal, _ => RepositoryVisibility.Public })
        {
            Url = Str("url") ?? "",
            SshUrl = Str("sshUrl") ?? "",
            Description = Str("description"),
            Homepage = Str("homepageUrl"),
            Topics = topics,
            DefaultBranch = e.TryGetProperty("defaultBranchRef", out var b) && b.ValueKind == JsonValueKind.Object ? b.GetProperty("name").GetString() : null,
            IsArchived = e.TryGetProperty("isArchived", out var a) && a.ValueKind == JsonValueKind.True,
            IsFork = e.TryGetProperty("isFork", out var f) && f.ValueKind == JsonValueKind.True,
            Parent = parent,
            PushedAt = Date("pushedAt"),
            CreatedAt = Date("createdAt"),
            Stars = Int("stargazerCount"),
            Forks = Int("forkCount"),
            DiskUsageKb = e.TryGetProperty("diskUsage", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetInt64() : 0,
            Permission = Str("viewerPermission"),
        };
    }
}

/// <summary>Règles GitHub pour les sujets (topics) d'un repo.</summary>
public static partial class RepositoryTopics
{
    public const int MaxCount = 20;
    public const int MaxLength = 50;

    /// <summary>« Gestion de stock, PHP » → « gestion-de-stock », « php » (minuscules, espaces → tirets).</summary>
    public static IReadOnlyList<string> Parse(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(t => Spaces().Replace(t.ToLowerInvariant(), "-"))
            .Distinct()
            .ToList();

    public static string? Validate(IReadOnlyList<string> topics)
    {
        if (topics.Count > MaxCount) return $"{MaxCount} sujets maximum ({topics.Count} actuellement).";
        var bad = topics.FirstOrDefault(t => t.Length > MaxLength || !Valid().IsMatch(t));
        return bad is null ? null
            : bad.Length > MaxLength ? $"« {bad} » est trop long ({MaxLength} caractères maximum)."
            : $"« {bad} » : un sujet ne contient que des lettres minuscules sans accent, des chiffres et des tirets, et commence par une lettre ou un chiffre.";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex Spaces();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial System.Text.RegularExpressions.Regex Valid();
}
