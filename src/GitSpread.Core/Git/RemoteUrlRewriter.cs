using System.Text.RegularExpressions;

namespace GitSpread.Core.Git;

/// <summary>Change le propriétaire et/ou le nom d'un repo dans une URL de remote, en gardant sa forme
/// (HTTPS ou SSH, alias SSH, utilisateur dans l'URL, suffixe .git).</summary>
public static partial class RemoteUrlRewriter
{
    public static string Rewrite(string url, string newOwner, string newName)
    {
        var m = Scheme().Match(url);
        if (!m.Success) m = ScpLike().Match(url);
        if (!m.Success) throw new ArgumentException($"URL de remote non reconnue : {url}", nameof(url));
        var suffix = m.Groups["git"].Success ? ".git" : "";
        return $"{m.Groups["prefix"].Value}{newOwner}/{newName}{suffix}";
    }

    // https://[user@]host[:port]/owner/name[.git][/]
    [GeneratedRegex(@"^(?<prefix>[A-Za-z][A-Za-z0-9+.-]*://[^/]+/)(?<owner>[^/]+)/(?<name>[^/]+?)(?<git>\.git)?/?$")]
    private static partial Regex Scheme();

    // [user@]host:owner/name[.git]
    [GeneratedRegex(@"^(?<prefix>(?:[^@/]+@)?[^/:\\]{2,}:)(?<owner>[^/]+)/(?<name>[^/]+?)(?<git>\.git)?/?$")]
    private static partial Regex ScpLike();
}
