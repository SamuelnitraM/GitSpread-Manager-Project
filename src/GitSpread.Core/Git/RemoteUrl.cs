using System.Text.RegularExpressions;
using GitSpread.Core.Ssh;

namespace GitSpread.Core.Git;

public enum RemoteProtocol { Https, Ssh, Git, File, Unknown }

/// <summary>URL de remote décomposée : hôte réel, propriétaire et nom du repo.</summary>
public sealed record RemoteUrl(string Url, RemoteProtocol Protocol, string Host)
{
    public string? Owner { get; init; }
    public string? Repository { get; init; }

    /// <summary>Alias SSH utilisé dans l'URL (ex. github-perso), résolu vers <see cref="Host"/> via ~/.ssh/config.</summary>
    public string? SshAlias { get; init; }

    public string? FullName => Owner is null || Repository is null ? null : $"{Owner}/{Repository}";
    public bool IsGitHub => Host == "github.com";
}

/// <summary>Analyse toutes les formes d'URL que git accepte pour un remote GitHub (HTTPS, SSH, alias SSH...).</summary>
public static partial class RemoteUrlParser
{
    public static RemoteUrl Parse(string url, SshConfig? ssh = null)
    {
        url = url.Trim();

        // Chemin local (C:\..., \\serveur\..., /chemin, file://)
        if (WindowsPath().IsMatch(url) || url.StartsWith('/') || url.StartsWith('.') || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return new RemoteUrl(url, RemoteProtocol.File, "");

        var m = SchemeUrl().Match(url);
        if (m.Success)
        {
            var scheme = m.Groups["scheme"].Value.ToLowerInvariant();
            var protocol = scheme switch
            {
                "https" or "http" => RemoteProtocol.Https,
                "ssh" or "git+ssh" or "ssh+git" => RemoteProtocol.Ssh,
                "git" => RemoteProtocol.Git,
                _ => RemoteProtocol.Unknown,
            };
            return Build(url, protocol, m.Groups["host"].Value, m.Groups["path"].Value, ssh);
        }

        m = ScpLike().Match(url);
        if (m.Success)
            return Build(url, RemoteProtocol.Ssh, m.Groups["host"].Value, m.Groups["path"].Value, ssh);

        return new RemoteUrl(url, RemoteProtocol.Unknown, "");
    }

    private static RemoteUrl Build(string url, RemoteProtocol protocol, string rawHost, string path, SshConfig? ssh)
    {
        string? alias = null;
        var host = rawHost;
        if (protocol == RemoteProtocol.Ssh && ssh?.Find(rawHost)?.HostName is { } real && !real.Equals(rawHost, StringComparison.OrdinalIgnoreCase))
        {
            alias = rawHost;
            host = real;
        }
        host = NormalizeHost(host);

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return new RemoteUrl(url, protocol, host) { SshAlias = alias };

        var repo = segments[^1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        return new RemoteUrl(url, protocol, host)
        {
            Owner = string.Join('/', segments[..^1]), // sous-groupes GitLab : owner = tout sauf le dernier segment
            Repository = repo,
            SshAlias = alias,
        };
    }

    private static string NormalizeHost(string host)
    {
        host = host.ToLowerInvariant();
        return host is "www.github.com" or "ssh.github.com" ? "github.com" : host;
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/]|^\\\\")]
    private static partial Regex WindowsPath();

    // https://[user[:pass]@]host[:port]/path, ssh://git@host[:port]/path, git://host/path
    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.-]*)://(?:[^@/]+@)?(?<host>[^/:]+)(?::\d*)?(?<path>/.*)?$")]
    private static partial Regex SchemeUrl();

    // [user@]host:path (syntaxe scp de git), le chemin ne commence pas par « / » dans le cas courant
    [GeneratedRegex(@"^(?:[^@/]+@)?(?<host>[^/:\\]{2,}):(?<path>.+)$")]
    private static partial Regex ScpLike();
}
