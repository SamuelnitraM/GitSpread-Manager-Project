namespace GitSpread.Core.Git;

/// <summary>F6b : actions sur les branches depuis l'historique (créer à un commit, supprimer ici ou sur GitHub).</summary>
public sealed partial class GitService
{
    /// <summary>Crée une branche à un commit donné, sans changer de branche.</summary>
    public Task<GitResult> CreateBranchAtAsync(string repositoryPath, string name, string startPoint, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["branch", name, startPoint], null, ct);

    /// <summary>
    /// Supprime une branche locale même si elle n'est pas intégrée. GitSpread vérifie lui-même avant, et prévient
    /// quand des commits n'existent nulle part ailleurs (<see cref="CountCommitsOnlyInAsync"/>).
    /// </summary>
    public Task<GitResult> ForceDeleteBranchAsync(string repositoryPath, string name, CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["branch", "-D", name], null, ct);

    /// <summary>Supprime une branche sur le dépôt distant (GitHub), avec le compte du lien.</summary>
    public Task<GitResult> DeleteRemoteBranchAsync(string repositoryPath, string remote, string branch, IReadOnlyDictionary<string, string> auth,
        CancellationToken ct = default) =>
        ActAsync(repositoryPath, ["push", remote, "--delete", branch], auth, ct, NetworkTimeout);

    /// <summary>
    /// Oublie la copie locale d'une branche distante qui n'existe plus (ex. renommée sur GitHub : juste après, le serveur
    /// peut encore l'annoncer et « fetch --prune » ne la retire pas). Sans effet si elle n'existe pas.
    /// </summary>
    public async Task<GitResult?> ForgetRemoteBranchAsync(string repositoryPath, string remote, string branch, CancellationToken ct = default) =>
        await RemoteBranchExistsAsync(repositoryPath, remote, branch, ct)
            ? await ActAsync(repositoryPath, ["update-ref", "-d", $"refs/remotes/{remote}/{branch}"], null, ct)
            : null;

    /// <summary>
    /// Commits de <paramref name="reference"/> absents de toutes les références <paramref name="elsewhere"/>
    /// (et, si demandé, de toutes les branches connues sur les dépôts distants) : ceux qu'une suppression ferait perdre.
    /// </summary>
    public async Task<int> CountCommitsOnlyInAsync(string repositoryPath, string reference, IEnumerable<string> elsewhere, bool excludeRemotes,
        CancellationToken ct = default)
    {
        List<string> args = ["rev-list", "--count", reference, "--not", .. elsewhere];
        if (excludeRemotes) args.Add("--remotes");
        return int.TryParse((await ReadAsync(repositoryPath, args, ct)).Trim(), out var n) ? n : 0;
    }
}
