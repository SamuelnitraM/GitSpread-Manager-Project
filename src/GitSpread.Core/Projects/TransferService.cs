using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;

namespace GitSpread.Core.Projects;

public enum OldRemoteAction
{
    /// <summary>L'ancien lien est conservé sous le nom « old-origin » (rien n'est perdu).</summary>
    KeepAsOldOrigin,
    /// <summary>L'ancien lien est supprimé du projet (le repo sur GitHub, lui, n'est pas touché).</summary>
    Remove,
}

public enum PushScope { CurrentBranch, AllBranchesAndTags }

/// <summary>État du repo cible avant de repointer un projet vers lui.</summary>
public sealed record TargetAnalysis(bool IsEmpty)
{
    /// <summary>Branches de la cible absentes ou en avance par rapport au projet local : les pousser écraserait du travail.</summary>
    public IReadOnlyList<string> DivergingBranches { get; init; } = [];

    /// <summary>Branches de la cible déjà contenues dans le projet local (le push sera une simple avance).</summary>
    public IReadOnlyList<string> CompatibleBranches { get; init; } = [];

    public bool HasDivergingHistory => DivergingBranches.Count > 0;

    public string Summary => IsEmpty
        ? "Le repo cible est vide : tout l'historique du projet y sera envoyé."
        : HasDivergingHistory
            ? $"Le repo cible contient déjà un historique différent ({string.Join(", ", DivergingBranches)}). Pour ne rien écraser, GitSpread refuse d'y pousser directement : tu peux envoyer ton travail sur une nouvelle branche."
            : "Le repo cible contient un historique compatible : ton projet le prolonge, l'envoi se fera sans rien écraser.";
}

/// <summary>Repointer un projet local vers un autre repo GitHub (même compte ou autre compte).</summary>
public sealed record RepointPlan(string Directory, GitHubAccount TargetAccount, string TargetRepository)
{
    /// <summary>URL de clonage du repo existant (null quand il faut le créer).</summary>
    public string? TargetUrl { get; init; }
    public bool CreateRepository => TargetUrl is null;
    public RepositoryVisibility Visibility { get; init; } = RepositoryVisibility.Private;
    public string? Description { get; init; }

    public OldRemoteAction OldRemote { get; init; } = OldRemoteAction.KeepAsOldOrigin;
    public PushScope Scope { get; init; } = PushScope.AllBranchesAndTags;

    /// <summary>Historique différent sur la cible : nom de la nouvelle branche distante où envoyer la branche courante.</summary>
    public string? PushToNewBranch { get; init; }

    public string RemoteName { get; init; } = "origin";

    /// <summary>F3B : ajouter ce repo comme lien secondaire (remote <see cref="RemoteName"/>) sans toucher au lien actuel.</summary>
    public bool KeepCurrentLink { get; init; }

    public string FullName => $"{TargetAccount.Login}/{TargetRepository}";
}

public sealed record TransferResult(string Url, IReadOnlyList<GitResult> Steps, string Message);

/// <summary>
/// Orchestration du changement de repo lié : validation, plan de commandes, exécution, retour arrière en cas d'échec.
/// Jamais de --force : un historique différent sur la cible n'est jamais écrasé.
/// </summary>
public sealed class TransferService(GitService git, GitHubService gitHub, GitCredentialResolver credentials)
{
    public const string OldRemoteName = "old-origin";

    /// <summary>Compare la cible avec le projet local sans rien télécharger (ls-remote + commits connus localement).</summary>
    public async Task<TargetAnalysis> AnalyzeTargetAsync(string directory, GitHubAccount account, string url, CancellationToken ct = default)
    {
        var auth = await credentials.EnvironmentForAsync(account, RemoteUrlParser.Parse(url), ct);
        var heads = await git.ListRemoteHeadsAsync(directory, url, auth, ct);
        if (heads.Count == 0) return new TargetAnalysis(IsEmpty: true);

        var locals = (await git.ListBranchesAsync(directory, ct)).Where(b => b.IsLocal).Select(b => b.Name).ToHashSet();
        var compatible = new List<string>();
        var diverging = new List<string>();
        foreach (var head in heads)
        {
            // Compatible si le commit distant est connu ici ET contenu dans la branche locale du même nom.
            var ok = locals.Contains(head.Branch)
                && await git.HasCommitAsync(directory, head.Sha, ct)
                && await git.IsAncestorAsync(directory, head.Sha, head.Branch, ct);
            (ok ? compatible : diverging).Add(head.Branch);
        }
        return new TargetAnalysis(false) { CompatibleBranches = compatible, DivergingBranches = diverging };
    }

    public static IReadOnlyList<string> Preview(RepointPlan plan, string? currentRemoteUrl, string? currentBranch)
    {
        var steps = new List<string>();
        var url = plan.TargetUrl ?? $"https://{plan.TargetAccount.Host}/{plan.FullName}.git";
        if (plan.CreateRepository)
            steps.Add($"gh repo create {plan.FullName} --{(plan.Visibility == RepositoryVisibility.Public ? "public" : "private")}   (avec le compte {plan.TargetAccount.Login})");
        if (currentRemoteUrl is not null && !plan.KeepCurrentLink)
            steps.Add(plan.OldRemote == OldRemoteAction.KeepAsOldOrigin
                ? $"git remote rename {plan.RemoteName} {OldRemoteName}"
                : $"git remote remove {plan.RemoteName}");
        steps.Add($"git remote add {plan.RemoteName} {url}");
        var upstream = plan.KeepCurrentLink ? "" : "--set-upstream "; // un lien secondaire ne devient pas la référence des branches
        if (plan.PushToNewBranch is { } newBranch)
            steps.Add($"git push {upstream}{plan.RemoteName} {currentBranch}:{newBranch}");
        else if (plan.Scope == PushScope.AllBranchesAndTags)
            steps.AddRange([$"git push {upstream}{plan.RemoteName} --all", $"git push {plan.RemoteName} --tags"]);
        else
            steps.Add($"git push {upstream}{plan.RemoteName} {currentBranch}");
        steps.Add($"(envois faits avec le compte {plan.TargetAccount.Login})");
        return steps;
    }

    public async Task<TransferResult> RepointAsync(RepointPlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dir = plan.Directory;
        var steps = new List<GitResult>();
        var status = await git.GetStatusAsync(dir, ct);
        if (!status.HasCommits) throw new InvalidOperationException("Le projet n'a encore aucun commit : il n'y a rien à envoyer.");
        if (status.Branch is not { } branch && (plan.Scope == PushScope.CurrentBranch || plan.PushToNewBranch is not null))
            throw new InvalidOperationException("Le projet n'est sur aucune branche (HEAD détachée).");
        branch = status.Branch ?? "";

        // 1. Repo cible
        string url;
        if (plan.CreateRepository)
        {
            if (await gitHub.RepositoryExistsAsync(plan.TargetAccount, plan.FullName, ct))
                throw new InvalidOperationException($"Le repo {plan.FullName} existe déjà : choisis-le dans la liste des repos existants.");
            progress?.Report($"Création du repo {plan.FullName}…");
            url = await gitHub.CreateRepositoryAsync(plan.TargetAccount, plan.TargetRepository, plan.Visibility, plan.Description, ct);
        }
        else
        {
            url = plan.TargetUrl!;
            progress?.Report("Vérification du repo cible…");
            var target = await AnalyzeTargetAsync(dir, plan.TargetAccount, url, ct);
            if (target.HasDivergingHistory && plan.PushToNewBranch is null)
                throw new InvalidOperationException(target.Summary);
        }

        // 2. Ancien lien
        var remotes = await git.GetRemotesAsync(dir, ct);
        if (plan.KeepCurrentLink && remotes.Any(r => r.Name == plan.RemoteName))
            throw new InvalidOperationException($"Le projet a déjà un lien nommé « {plan.RemoteName} » : choisis un autre nom.");
        var old = plan.KeepCurrentLink ? null : remotes.FirstOrDefault(r => r.Name == plan.RemoteName);
        var keptAs = OldRemoteName;
        for (var i = 2; remotes.Any(r => r.Name == keptAs); i++) keptAs = $"{OldRemoteName}-{i}";
        if (old is not null)
        {
            progress?.Report(plan.OldRemote == OldRemoteAction.KeepAsOldOrigin ? $"L'ancien lien devient « {keptAs} »…" : "Suppression de l'ancien lien…");
            var r = plan.OldRemote == OldRemoteAction.KeepAsOldOrigin
                ? await git.RenameRemoteAsync(dir, plan.RemoteName, keptAs, ct)
                : await git.RemoveRemoteAsync(dir, plan.RemoteName, ct);
            steps.Add(r.EnsureSuccess());
        }

        try
        {
            // 3. Nouveau lien + envoi avec le compte cible
            progress?.Report($"Liaison au repo {plan.FullName}…");
            steps.Add((await git.AddRemoteAsync(dir, plan.RemoteName, url, ct)).EnsureSuccess());

            progress?.Report($"Envoi avec le compte {plan.TargetAccount.Login}…");
            var auth = await credentials.EnvironmentForAsync(plan.TargetAccount, RemoteUrlParser.Parse(url), ct);
            var upstream = !plan.KeepCurrentLink;
            IReadOnlyList<GitResult> push = plan.PushToNewBranch is { } newBranch
                ? [await git.PushToAsync(dir, plan.RemoteName, branch, newBranch, auth, ct, upstream)]
                : plan.Scope == PushScope.AllBranchesAndTags
                    ? await git.PushAllAsync(dir, plan.RemoteName, auth, ct, upstream)
                    : [await git.PushAsync(dir, plan.RemoteName, branch, setUpstream: upstream, auth, ct)];
            steps.AddRange(push);
            foreach (var p in push) p.EnsureSuccess();
        }
        catch (Exception e)
        {
            // Retour arrière : le projet retrouve son lien d'origine.
            progress?.Report("Échec : retour à la configuration d'origine…");
            await git.RemoveRemoteAsync(dir, plan.RemoteName, ct);
            if (old is not null)
            {
                if (plan.OldRemote == OldRemoteAction.KeepAsOldOrigin) await git.RenameRemoteAsync(dir, keptAs, plan.RemoteName, ct);
                else await git.AddRemoteAsync(dir, plan.RemoteName, old.Url, ct);
            }
            var created = plan.CreateRepository ? $" Le repo {plan.FullName} a été créé sur GitHub et reste vide : tu peux le réutiliser ou le supprimer depuis GitHub." : "";
            throw new InvalidOperationException($"{e.Message} Le projet a retrouvé {(plan.KeepCurrentLink ? "ses liens d'origine" : "son lien d'origine")}.{created}", e);
        }

        if (plan.KeepCurrentLink)
            return new TransferResult(url, steps, plan.PushToNewBranch is { } b
                ? $"Le projet est maintenant aussi lié à {plan.FullName} (lien « {plan.RemoteName} »). Ta branche {branch} y a été envoyée sur la nouvelle branche {b}. Le lien principal ne change pas."
                : $"Le projet est maintenant aussi lié à {plan.FullName} (lien « {plan.RemoteName} »). Le lien principal ne change pas.");

        var message = plan.PushToNewBranch is { } nb
            ? $"Le projet est maintenant lié à {plan.FullName}. Ta branche {branch} a été envoyée sur la nouvelle branche {nb}."
            : $"Le projet est maintenant lié à {plan.FullName}" + (old is not null && plan.OldRemote == OldRemoteAction.KeepAsOldOrigin ? $" (l'ancien lien est conservé sous « {keptAs} »)." : ".");
        return new TransferResult(url, steps, message);
    }

    /// <summary>
    /// Transfert natif GitHub : le repo change de propriétaire (issues, étoiles et historique conservés).
    /// Entre deux comptes personnels, le compte destinataire doit accepter la demande (e-mail de GitHub).
    /// </summary>
    public async Task<TransferResult> TransferOwnershipAsync(string directory, GitHubAccount from, GitHubAccount to,
        string repository, string remoteName = "origin", IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report($"Demande de transfert de {from.Login}/{repository} vers {to.Login}…");
        await gitHub.TransferRepositoryAsync(from, $"{from.Login}/{repository}", to.Login, ct);

        var newUrl = $"https://{to.Host}/{to.Login}/{repository}.git";
        progress?.Report("Mise à jour du lien du projet…");
        var set = (await git.SetRemoteUrlAsync(directory, remoteName, newUrl, ct)).EnsureSuccess();
        return new TransferResult(newUrl, [set],
            $"Transfert demandé. {to.Login} doit l'accepter depuis l'e-mail envoyé par GitHub (valable 24 h) ; "
            + "en attendant, les envois vers le nouveau lien échoueront. Le projet pointe déjà vers sa future adresse.");
    }
}
