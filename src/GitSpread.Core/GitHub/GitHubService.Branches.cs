using System.Text.Json;
using GitSpread.Core.Accounts;

namespace GitSpread.Core.GitHub;

/// <summary>Une branche d'un repo GitHub, avec son dernier commit et sa position par rapport à la branche principale.</summary>
/// <param name="NotInDefault">Commits de la branche absents de la branche principale.</param>
/// <param name="DefaultAhead">Commits de la branche principale absents de cette branche.</param>
public sealed record GitHubBranch(string Name, string Sha, DateTimeOffset? Date, string Subject, string? Author, int NotInDefault, int DefaultAhead);

/// <summary>F6c : branches d'un repo GitHub (lecture en une requête, suppression).</summary>
public sealed partial class GitHubService
{
    public const int MaxBranches = 100;

    private const string BranchesQuery = """
        query($owner: String!, $name: String!, $base: String!) {
          repository(owner: $owner, name: $name) {
            refs(refPrefix: "refs/heads/", first: 100, orderBy: {field: TAG_COMMIT_DATE, direction: DESC}) {
              nodes {
                name
                target { ... on Commit { oid committedDate messageHeadline author { name } } }
                compare(headRef: $base) { aheadBy behindBy }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Branches du repo (100 au plus, les plus récentes d'abord), comparées à la branche principale en une seule requête.
    /// Dans la comparaison GitHub, la branche est la base : « behindBy » compte ses commits absents de la principale.
    /// </summary>
    public async Task<IReadOnlyList<GitHubBranch>> ListBranchesAsync(GitHubAccount account, string fullName, string defaultBranch, CancellationToken ct = default)
    {
        var (owner, name) = (fullName.Split('/')[0], fullName.Split('/')[1]);
        var json = await gh.RunCheckedAsync(["api", "graphql", "-f", $"owner={owner}", "-f", $"name={name}", "-f", $"base={defaultBranch}", "-f", $"query={BranchesQuery}"],
            await EnvironmentForAsync(account, ct), ct);
        return ParseBranches(json);
    }

    internal static IReadOnlyList<GitHubBranch> ParseBranches(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var nodes = doc.RootElement.GetProperty("data").GetProperty("repository").GetProperty("refs").GetProperty("nodes");
        return nodes.EnumerateArray().Select(n =>
        {
            var target = n.GetProperty("target");
            string? Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var compare = n.TryGetProperty("compare", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            int Count(string p) => compare.ValueKind == JsonValueKind.Object && compare.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            return new GitHubBranch(
                n.GetProperty("name").GetString()!,
                Str(target, "oid") ?? "",
                Str(target, "committedDate") is { } d && DateTimeOffset.TryParse(d, out var date) ? date : null,
                Str(target, "messageHeadline") ?? "",
                target.ValueKind == JsonValueKind.Object && target.TryGetProperty("author", out var a) ? Str(a, "name") : null,
                NotInDefault: Count("behindBy"),
                DefaultAhead: Count("aheadBy"));
        }).ToList();
    }

    /// <summary>Supprime une branche sur GitHub (refusé par GitHub pour la branche par défaut ou une branche protégée).</summary>
    public async Task DeleteBranchAsync(GitHubAccount account, string fullName, string branch, CancellationToken ct = default) =>
        await gh.RunCheckedAsync(["api", "--method", "DELETE", $"repos/{fullName}/git/refs/heads/{branch}"], await EnvironmentForAsync(account, ct), ct);
}
