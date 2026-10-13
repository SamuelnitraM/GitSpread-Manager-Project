using System.Text.RegularExpressions;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;

namespace GitSpread.Core.Projects;

/// <summary>Résultat de l'envoi (ou de la récupération) pour un lien.</summary>
public sealed record LinkResult(ProjectLink Link, GitResult Result, string Message)
{
    public bool Succeeded => Result.Succeeded;
}

/// <summary>
/// F3B : un projet lié à plusieurs repos, un remote git par repo. Le lien principal s'appelle « origin » :
/// c'est lui que suivent les branches (Tirer, état « à jour »). Les autres reçoivent les envois cochés.
/// </summary>
public sealed partial class ProjectLinks(GitService git, GitCredentialResolver credentials)
{
    public const string PrimaryName = "origin";

    /// <summary>Nom proposé pour un lien secondaire : le login du compte en minuscules (lisible dans un terminal).</summary>
    public static string SuggestName(string login, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = Regex.Replace(login.ToLowerInvariant(), "[^a-z0-9._-]", "-");
        var candidate = name;
        for (var i = 2; used.Contains(candidate) || candidate == PrimaryName; i++) candidate = $"{name}-{i}";
        return candidate;
    }

    /// <summary>Message d'erreur pour un nom de lien, ou null s'il est valide.</summary>
    public static string? ValidateName(string name, IEnumerable<string> taken)
    {
        if (name.Length == 0) return "Indique un nom pour ce lien.";
        if (name.Equals(PrimaryName, StringComparison.OrdinalIgnoreCase)) return "« origin » est réservé au lien principal.";
        if (!NamePattern().IsMatch(name)) return "Lettres sans accent, chiffres, « - », « _ » et « . » uniquement (pas d'espace), en commençant par une lettre ou un chiffre.";
        return taken.Contains(name, StringComparer.OrdinalIgnoreCase) ? $"Un lien s'appelle déjà « {name} »." : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,49}$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Fait d'un lien secondaire le lien principal : l'ancien principal devient secondaire sous <paramref name="oldPrimaryNewName"/>.
    /// La branche courante suit ensuite le nouveau principal s'il la connaît déjà, sinon le prochain envoi l'y créera.
    /// </summary>
    public async Task<IReadOnlyList<GitResult>> MakePrimaryAsync(string directory, string linkName, string oldPrimaryNewName, string? currentBranch,
        CancellationToken ct = default)
    {
        var steps = new List<GitResult> { await git.RenameRemoteAsync(directory, PrimaryName, oldPrimaryNewName, ct) };
        if (!steps[^1].Succeeded) return steps;
        steps.Add(await git.RenameRemoteAsync(directory, linkName, PrimaryName, ct));
        if (!steps[^1].Succeeded)
        {
            steps.Add(await git.RenameRemoteAsync(directory, oldPrimaryNewName, PrimaryName, ct)); // retour à l'état d'origine
            return steps;
        }
        steps.Add(await git.SetPushRemotesAsync(directory, [], ct)); // les noms ont changé : la sélection mémorisée repart du principal

        if (currentBranch is not null)
            steps.Add((await git.CompareWithRemoteAsync(directory, PrimaryName, currentBranch, ct)).Exists
                ? await git.SetUpstreamAsync(directory, currentBranch, $"{PrimaryName}/{currentBranch}", ct)
                : await UnsetUpstreamQuietlyAsync(directory, currentBranch, ct));
        return steps;
    }

    private async Task<GitResult> UnsetUpstreamQuietlyAsync(string directory, string branch, CancellationToken ct)
    {
        var r = await git.UnsetUpstreamAsync(directory, branch, ct);
        return r.Succeeded || r.Output.Contains("has no upstream") ? r with { Process = r.Process with { ExitCode = 0 } } : r;
    }

    /// <summary>
    /// Envoie la branche vers chaque lien coché, avec le compte de chaque lien. Jamais de --force.
    /// Seul le lien principal devient la référence de la branche (suivi) ; un refus sur un lien n'empêche pas les autres.
    /// </summary>
    public async Task<IReadOnlyList<LinkResult>> PushAsync(string directory, string branch, IReadOnlyList<ProjectLink> links, bool setPrimaryUpstream,
        CancellationToken ct = default)
    {
        var results = new List<LinkResult>();
        foreach (var link in links)
        {
            var auth = await credentials.EnvironmentForAsync(link.Account, link.Url, ct);
            var r = await git.PushAsync(directory, link.Name, branch, setUpstream: link.IsPrimary && setPrimaryUpstream, auth, ct);
            results.Add(new LinkResult(link, r, PushMessage(link, branch, r)));
        }
        return results;
    }

    /// <summary>Récupère les nouveautés de chaque lien, chacun avec son compte.</summary>
    public async Task<IReadOnlyList<LinkResult>> FetchAsync(string directory, IReadOnlyList<ProjectLink> links, CancellationToken ct = default)
    {
        var results = new List<LinkResult>();
        foreach (var link in links)
        {
            var auth = await credentials.EnvironmentForAsync(link.Account, link.Url, ct);
            var r = await git.FetchAsync(directory, link.Name, auth, ct);
            results.Add(new LinkResult(link, r, r.Succeeded ? $"{Describe(link)} : à jour." : $"{Describe(link)} : {r.Explanation ?? FirstLine(r.Output)}"));
        }
        return results;
    }

    public static string Describe(ProjectLink link) => $"{link.Name} ({link.RepositoryLabel})";

    internal static string PushMessage(ProjectLink link, string branch, GitResult r)
    {
        if (r.Succeeded)
            return r.Output.Contains("Everything up-to-date") ? $"{Describe(link)} : déjà à jour." : $"{Describe(link)} : branche {branch} envoyée.";
        var rejected = r.Output.Contains("rejected") || r.Output.Contains("fetch first") || r.Output.Contains("non-fast-forward");
        if (rejected && !link.IsPrimary)
            return $"{Describe(link)} : refusé. Ce repo contient des commits que ton projet n'a pas (quelqu'un y a envoyé directement). "
                   + "Rien n'a été envoyé ni écrasé sur ce lien. GitSpread ne récupère que depuis le lien principal : "
                   + "pour intégrer ces commits, fais de ce lien le principal puis utilise « Tirer ».";
        return $"{Describe(link)} : {r.Explanation ?? FirstLine(r.Output)}";
    }

    private static string FirstLine(string output) => output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "échec sans message.";
}
