namespace GitSpread.Core.Ssh;

public sealed record SshHostEntry(string Alias)
{
    public string? HostName { get; init; }
    public string? User { get; init; }
    public string? IdentityFile { get; init; }
}

/// <summary>
/// Lecture minimale de ~/.ssh/config : résout les alias du type « Host github-perso / HostName github.com »
/// très utilisés pour avoir une clé SSH par compte.
/// </summary>
public sealed class SshConfig
{
    public static readonly SshConfig Empty = new([]);

    private readonly Dictionary<string, SshHostEntry> _entries;

    public SshConfig(IEnumerable<SshHostEntry> entries) =>
        _entries = entries.GroupBy(e => e.Alias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<SshHostEntry> Entries => _entries.Values;

    public SshHostEntry? Find(string alias) => _entries.GetValueOrDefault(alias);

    /// <summary>Nom d'hôte réel derrière un alias (ou l'alias lui-même s'il n'est pas déclaré).</summary>
    public string ResolveHost(string alias) => Find(alias)?.HostName ?? alias;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    public static SshConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try { return File.Exists(path) ? Parse(File.ReadAllLines(path)) : Empty; }
        catch (IOException) { return Empty; }
        catch (UnauthorizedAccessException) { return Empty; }
    }

    public static SshConfig Parse(IEnumerable<string> lines)
    {
        var entries = new List<SshHostEntry>();
        List<SshHostEntry> current = [];
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.IndexOfAny([' ', '\t', '=']);
            if (separator < 0) continue;
            var key = line[..separator];
            var value = line[(separator + 1)..].Trim().TrimStart('=').Trim().Trim('"');

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                entries.AddRange(current);
                // Les motifs (*, ?, !) ne désignent pas un alias précis : ignorés.
                current = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(a => a.IndexOfAny(['*', '?', '!']) < 0)
                    .Select(a => new SshHostEntry(a)).ToList();
            }
            else if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                entries.AddRange(current);
                current = [];
            }
            else
            {
                // Comme ssh : la première valeur rencontrée l'emporte.
                current = current.Select(e => key.ToLowerInvariant() switch
                {
                    "hostname" => e with { HostName = e.HostName ?? value },
                    "user" => e with { User = e.User ?? value },
                    "identityfile" => e with { IdentityFile = e.IdentityFile ?? value },
                    _ => e,
                }).ToList();
            }
        }
        entries.AddRange(current);
        return new SshConfig(entries);
    }
}
