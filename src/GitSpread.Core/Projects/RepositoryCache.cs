using System.Collections.Concurrent;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;

namespace GitSpread.Core.Projects;

/// <summary>Copie légère d'un repo GitHub, prête à être lue comme un projet (historique, branches, modifications).</summary>
/// <param name="Auth">Environnement git du compte : le contenu des fichiers est téléchargé à la demande, avec ce compte.</param>
/// <param name="Warning">Message si la copie n'a pas pu être mise à jour (elle reste lisible).</param>
public sealed record RemoteCopy(string Path, IReadOnlyDictionary<string, string> Auth, string? Warning);

/// <summary>
/// F6c : historique des repos GitHub pas encore clonés. GitSpread garde une copie « légère » de chacun dans son dossier
/// (%LocalAppData%\GitSpread\cache), jamais dans les dossiers de projets : historique et branches seulement, le contenu
/// des fichiers n'est téléchargé que quand on ouvre ses modifications. Aucun token n'est écrit sur le disque.
/// </summary>
public sealed class RepositoryCache(GitService git, GitCredentialResolver credentials, string root)
{
    /// <summary>Une copie lue il y a moins longtemps n'est pas remise à jour (évite un aller-retour par clic).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _fetchedAt = new(StringComparer.OrdinalIgnoreCase);

    public string Root => root;

    public string PathFor(string host, string fullName) =>
        System.IO.Path.Combine(root, host, fullName.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Crée la copie au premier usage, sinon la met à jour (sauf si elle vient de l'être).</summary>
    public async Task<RemoteCopy> EnsureAsync(GitHubAccount account, string host, string fullName, string cloneUrl, bool force = false,
        CancellationToken ct = default)
    {
        var path = PathFor(host, fullName);
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var auth = await credentials.EnvironmentForAsync(account, RemoteUrlParser.Parse(cloneUrl), ct);
            var ready = System.IO.Path.Combine(path, ReadyMarker);
            if (!File.Exists(ready))
            {
                // Pas de copie, ou copie interrompue (clone coupé, réglage non écrit) : on repart de zéro.
                if (Directory.Exists(path)) DeleteDirectory(path);
                var parent = System.IO.Path.GetDirectoryName(path)!;
                var clone = await git.CloneLightAsync(parent, cloneUrl, System.IO.Path.GetFileName(path), auth, ct);
                if (!clone.Succeeded)
                    throw new InvalidOperationException($"Impossible de lire {fullName} sur GitHub : {clone.Explanation ?? FirstLine(clone.Output)}");
                File.WriteAllText(ready, "copie complète\n"); // la copie n'est valide qu'une fois tout terminé
                _fetchedAt[path] = DateTime.UtcNow;
                return new RemoteCopy(path, auth, null);
            }

            if (!force && _fetchedAt.TryGetValue(path, out var at) && DateTime.UtcNow - at < RefreshInterval)
                return new RemoteCopy(path, auth, null);

            var fetch = await git.FetchAsync(path, "origin", auth, ct);
            if (fetch.Succeeded) _fetchedAt[path] = DateTime.UtcNow;
            return new RemoteCopy(path, auth, fetch.Succeeded ? null
                : $"Copie pas à jour (GitHub injoignable : {fetch.Explanation ?? FirstLine(fetch.Output)}). Affichage de la dernière version lue.");
        }
        finally { gate.Release(); }
    }

    /// <summary>Place occupée par les copies (affichée dans les paramètres).</summary>
    public long SizeInBytes() => Directory.Exists(root)
        ? new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
        : 0;

    /// <summary>Supprime toutes les copies : elles seront refaites à la prochaine ouverture d'un historique.</summary>
    /// <exception cref="InvalidOperationException">Une copie est en cours de création ou de mise à jour.</exception>
    public void Clear()
    {
        var held = new List<SemaphoreSlim>();
        try
        {
            foreach (var gate in _locks.Values)
            {
                if (!gate.Wait(0)) throw new InvalidOperationException("Une copie est en cours de lecture sur GitHub : réessaie dans un instant.");
                held.Add(gate);
            }
            _fetchedAt.Clear();
            if (Directory.Exists(root)) DeleteDirectory(root);
        }
        finally { foreach (var gate in held) gate.Release(); }
    }

    /// <summary>Fichier écrit une fois la copie complète (clone et réglage terminés).</summary>
    private const string ReadyMarker = "gitspread-copie-complete";

    private static void DeleteDirectory(string path)
    {
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal; // git crée ses objets en lecture seule
        Directory.Delete(path, recursive: true);
    }

    private static string FirstLine(string output) => output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "échec sans message";
}
