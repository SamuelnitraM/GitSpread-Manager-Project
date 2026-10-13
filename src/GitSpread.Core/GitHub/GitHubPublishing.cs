using System.Text;
using System.Text.RegularExpressions;

namespace GitSpread.Core.GitHub;

public sealed record GitHubUser(long Id, string Login)
{
    public string? Name { get; init; }
    public string? Email { get; init; }

    /// <summary>Adresse privée fournie par GitHub : les commits sont attribués au compte sans exposer d'e-mail.</summary>
    public string NoReplyEmail => $"{Id}+{Login}@users.noreply.github.com";

    /// <summary>Vrai si cet e-mail de commit sera rattaché à ce compte par GitHub.</summary>
    public bool OwnsEmail(string? email) =>
        email is not null && (
            email.Equals(Email, StringComparison.OrdinalIgnoreCase)
            || email.Equals(NoReplyEmail, StringComparison.OrdinalIgnoreCase)
            || email.Equals($"{Login}@users.noreply.github.com", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Noms de repos GitHub valides à partir d'un nom de dossier.</summary>
public static partial class RepositoryName
{
    public const int MaxLength = 100;

    /// <summary>« Mon Projet (v2) » → « Mon-Projet-v2 ». GitHub n'accepte que lettres, chiffres, « - », « _ » et « . ».</summary>
    public static string FromFolder(string folderName)
    {
        var ascii = new string(folderName.Normalize(NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
        var name = Collapse().Replace(Invalid().Replace(ascii, "-"), "-").Trim('-', '.');
        if (name.Length > MaxLength) name = name[..MaxLength].Trim('-', '.');
        return name.Length == 0 ? "projet" : name;
    }

    public static bool IsValid(string name) =>
        name.Length is > 0 and <= MaxLength && !Invalid().IsMatch(name) && name is not ("." or "..");

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex Invalid();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex Collapse();
}

/// <summary>Devine le modèle .gitignore de GitHub adapté au contenu d'un dossier.</summary>
public static class GitignoreGuesser
{
    public static string? Guess(string directory)
    {
        bool Has(string pattern) => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any();
        try
        {
            if (Has("artisan")) return "Laravel";
            if (Has("*.sln") || Has("*.csproj") || Has("*.vbproj")) return "VisualStudio";
            if (Has("package.json")) return "Node";
            if (Has("composer.json")) return "Composer";
            if (Has("pyproject.toml") || Has("requirements.txt") || Has("setup.py")) return "Python";
            if (Has("Cargo.toml")) return "Rust";
            if (Has("go.mod")) return "Go";
            if (Has("pom.xml") || Has("build.gradle")) return "Java";
            if (Has("*.php")) return "Composer";
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
