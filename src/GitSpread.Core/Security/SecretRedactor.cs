using System.Text.RegularExpressions;

namespace GitSpread.Core.Security;

/// <summary>Masque les tokens GitHub et les en-têtes d'authentification avant tout log ou affichage.</summary>
public static partial class SecretRedactor
{
    public const string Mask = "***";

    public static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? text ?? "" : AuthHeader().Replace(Token().Replace(text, Mask), "$1" + Mask);

    // gho_, ghp_, ghu_, ghs_, ghr_ et les fine-grained github_pat_
    [GeneratedRegex(@"\bgh[opusr]_[A-Za-z0-9]{20,}\b|\bgithub_pat_[A-Za-z0-9_]{20,}\b")]
    private static partial Regex Token();

    [GeneratedRegex(@"(Authorization:\s*(?:Basic|Bearer|token)\s+)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthHeader();
}
