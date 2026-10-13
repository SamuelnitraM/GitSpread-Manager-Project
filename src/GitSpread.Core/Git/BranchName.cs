using System.Text.RegularExpressions;

namespace GitSpread.Core.Git;

/// <summary>Règles de nommage des branches git (git check-ref-format), expliquées en clair.</summary>
public static partial class BranchName
{
    /// <summary>Remplace chaque espace par un tiret (« correctif stock » → « correctif-stock »).</summary>
    public static string ReplaceSpaces(string name) => Whitespace().Replace(name, "-");

    /// <summary>Message expliquant pourquoi git refuserait ce nom, ou null s'il est valide.</summary>
    public static string? Validate(string name)
    {
        if (name.Length == 0) return "Donne un nom à la branche.";
        if (Whitespace().IsMatch(name)) return "Pas d'espace dans un nom de branche : utilise « - ».";
        var forbidden = Forbidden().Matches(name).Select(m => m.Value).Distinct().ToList();
        if (forbidden.Count > 0)
            return $"Caractère{(forbidden.Count > 1 ? "s" : "")} non autorisé{(forbidden.Count > 1 ? "s" : "")} : {string.Join(" ", forbidden.Select(c => $"« {c} »"))}.";
        if (name.StartsWith('-')) return "Un nom de branche ne peut pas commencer par « - ».";
        if (name.StartsWith('/') || name.EndsWith('/') || name.Contains("//")) return "« / » sert à créer des dossiers (ex. « correctif/stock ») : pas au début, à la fin ni en double.";
        if (name.Contains("..")) return "« .. » n'est pas autorisé dans un nom de branche.";
        if (name.Contains("@{")) return "« @{ » n'est pas autorisé dans un nom de branche.";
        if (name == "@") return "« @ » seul n'est pas un nom de branche valide.";
        if (name.EndsWith('.') || name.EndsWith(".lock")) return "Un nom de branche ne peut pas finir par « . » ou « .lock ».";
        if (name.Split('/').Any(part => part.StartsWith('.'))) return "Aucune partie du nom ne peut commencer par « . ».";
        return null;
    }

    [GeneratedRegex(@"\s")]
    private static partial Regex Whitespace();

    // Caractères refusés par git dans un nom de référence, plus les caractères de contrôle.
    [GeneratedRegex(@"[~^:?*\[\\\x00-\x1F\x7F]")]
    private static partial Regex Forbidden();
}
