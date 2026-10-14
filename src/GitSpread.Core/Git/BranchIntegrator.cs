namespace GitSpread.Core.Git;

public enum IntegrationKind
{
    /// <summary>La cible contient déjà tout : rien à faire.</summary>
    NothingToDo,
    /// <summary>La cible n'a pas bougé : on l'avance simplement (aucun commit de fusion).</summary>
    FastForward,
    /// <summary>Les deux ont évolué sans toucher aux mêmes lignes : commit de fusion automatique.</summary>
    Merge,
    /// <summary>Les deux ont modifié les mêmes lignes : intervention humaine nécessaire.</summary>
    Conflicts,
}

public enum IntegrationBlocker
{
    None,
    /// <summary>Une fusion / un rebase est déjà en cours dans le dépôt.</summary>
    OperationInProgress,
    /// <summary>HEAD détachée : on ne sait pas sur quelle branche revenir.</summary>
    DetachedHead,
    /// <summary>La cible locale et sa version GitHub ont divergé : il faut d'abord la synchroniser.</summary>
    TargetDivergedFromRemote,
    /// <summary>Il faut changer de branche mais des modifications non commitées en empêchent (et la mise de côté n'est pas autorisée).</summary>
    DirtyWorkingTree,
}

/// <summary>Ce qui se passera si on intègre <see cref="Source"/> dans <see cref="Target"/>.</summary>
public sealed record IntegrationAnalysis(string Source, string Target)
{
    /// <summary>Référence git de la source (ex. origin/feature si elle n'existe que sur GitHub).</summary>
    public required string SourceRef { get; init; }
    public required string CurrentBranch { get; init; }
    public IntegrationKind Kind { get; init; }
    public IntegrationBlocker Blocker { get; init; }

    /// <summary>Commits de la source absents de la cible, et l'inverse.</summary>
    public int SourceOnly { get; init; }
    public int TargetOnly { get; init; }
    public IReadOnlyList<string> ConflictingFiles { get; init; } = [];

    /// <summary>La cible locale est en retard sur sa version GitHub : elle sera d'abord avancée.</summary>
    public int TargetBehindRemote { get; init; }
    public string? TargetUpstream { get; init; }
    public bool WorkingTreeDirty { get; init; }

    public bool TargetIsCurrent => Target == CurrentBranch;

    /// <summary>Il faut se placer sur la cible (fusion, ou avance rapide de la branche courante).</summary>
    public bool NeedsCheckout => Kind is IntegrationKind.Merge or IntegrationKind.Conflicts && !TargetIsCurrent;

    public bool CanProceed => Blocker == IntegrationBlocker.None && Kind != IntegrationKind.NothingToDo;

    /// <summary>Explication en une phrase, sans jargon.</summary>
    public string Summary => (Blocker, Kind) switch
    {
        (IntegrationBlocker.OperationInProgress, _) => "Une fusion est déjà en cours dans ce projet : termine-la ou abandonne-la d'abord.",
        (IntegrationBlocker.DetachedHead, _) => "Le projet n'est sur aucune branche (HEAD détachée) : place-toi d'abord sur une branche.",
        (IntegrationBlocker.TargetDivergedFromRemote, _) => $"{Target} a des commits différents de sa version GitHub ({TargetUpstream}) : synchronise d'abord {Target} (Tirer).",
        (IntegrationBlocker.DirtyWorkingTree, _) => $"Il faut passer sur {Target} pour cette opération, mais tu as des modifications non commitées. Commite-les ou laisse GitSpread les mettre de côté.",
        (_, IntegrationKind.NothingToDo) => $"Rien à faire : {Target} contient déjà tout le travail de {Source}.",
        (_, IntegrationKind.FastForward) => $"{Target} n'a pas changé depuis la création de {Source} : {Target} va simplement avancer de {SourceOnly} commit{Plural(SourceOnly)}, sans commit de fusion.",
        (_, IntegrationKind.Merge) => $"{Source} et {Target} ont chacune évolué ({SourceOnly} et {TargetOnly} commit{Plural(TargetOnly)}) sans toucher aux mêmes lignes : un commit de fusion sera créé automatiquement.",
        (_, IntegrationKind.Conflicts) => $"{Source} et {Target} ont modifié les mêmes lignes dans {ConflictingFiles.Count} fichier{Plural(ConflictingFiles.Count)} : la fusion demandera de choisir quelle version garder.",
        _ => "",
    };

    private static string Plural(int n) => n > 1 ? "s" : "";
}

public sealed record IntegrationOptions
{
    /// <summary>Mettre de côté les modifications non commitées le temps de l'opération, puis les restaurer.</summary>
    public bool StashChanges { get; init; } = true;

    /// <summary>En cas de conflits : lancer quand même la fusion et laisser l'utilisateur les résoudre.</summary>
    public bool StartDespiteConflicts { get; init; }

    /// <summary>Pousser la cible sur GitHub une fois à jour.</summary>
    public bool PushTarget { get; init; }

    /// <summary>Supprimer la branche source locale une fois intégrée (refusé par git si du travail n'est pas fusionné).</summary>
    public bool DeleteSource { get; init; }

    /// <summary>Lien principal du projet, vers lequel la cible est envoyée (pas toujours « origin »).</summary>
    public string Remote { get; init; } = "origin";

    public string? MergeMessage { get; init; }
}

public sealed record IntegrationResult(bool Succeeded, string Message, IReadOnlyList<GitResult> Steps)
{
    /// <summary>Vrai si la fusion a été laissée en cours avec des conflits à résoudre.</summary>
    public bool ConflictsToResolve { get; init; }
}

/// <summary>
/// Intègre une branche dans une autre (ex. une branche annexe dans main, ou main dans une branche annexe)
/// en choisissant le comportement le plus sûr : jamais de --force, jamais de travail perdu.
/// </summary>
public sealed class BranchIntegrator(GitService git)
{
    public async Task<IntegrationAnalysis> AnalyzeAsync(string dir, string source, string target, CancellationToken ct = default)
    {
        var branches = await git.ListBranchesAsync(dir, ct);
        var status = await git.GetStatusAsync(dir, ct);
        var current = branches.FirstOrDefault(b => b.IsCurrent)?.Name ?? "";
        var sourceBranch = branches.FirstOrDefault(b => b.Name == source) ?? throw new ArgumentException($"Branche introuvable : {source}");
        var targetBranch = branches.FirstOrDefault(b => b.Name == target) ?? throw new ArgumentException($"Branche introuvable : {target}");
        var targetRef = targetBranch.Ref;

        var analysis = new IntegrationAnalysis(source, target)
        {
            SourceRef = sourceBranch.Ref,
            CurrentBranch = current,
            WorkingTreeDirty = !status.IsClean,
            TargetUpstream = targetBranch.Upstream,
        };

        if (await git.GetOperationAsync(dir, ct) != RepositoryOperation.None)
            return analysis with { Blocker = IntegrationBlocker.OperationInProgress };
        if (status.IsDetached)
            return analysis with { Blocker = IntegrationBlocker.DetachedHead };

        // La cible est d'abord rattrapée sur sa version GitHub (avance rapide), si elle est en retard.
        if (targetBranch is { IsLocal: true, Upstream: { } upstream, UpstreamGone: false, Behind: > 0 })
        {
            if (targetBranch.Ahead > 0)
                return analysis with { Blocker = IntegrationBlocker.TargetDivergedFromRemote };
            analysis = analysis with { TargetBehindRemote = targetBranch.Behind };
            targetRef = upstream;
        }

        var (sourceOnly, targetOnly) = await git.CompareAsync(dir, sourceBranch.Ref, targetRef, ct);
        analysis = analysis with { SourceOnly = sourceOnly, TargetOnly = targetOnly };

        if (sourceOnly == 0) return analysis with { Kind = IntegrationKind.NothingToDo };
        if (targetOnly == 0) analysis = analysis with { Kind = IntegrationKind.FastForward };
        else
        {
            var conflicts = await git.PredictConflictsAsync(dir, targetRef, sourceBranch.Ref, ct);
            analysis = analysis with { Kind = conflicts.Count > 0 ? IntegrationKind.Conflicts : IntegrationKind.Merge, ConflictingFiles = conflicts };
        }

        return analysis.NeedsCheckout && analysis.WorkingTreeDirty
            ? analysis with { Blocker = IntegrationBlocker.DirtyWorkingTree }
            : analysis;
    }

    /// <summary>Commandes qui seront exécutées, pour les montrer avant de lancer.</summary>
    public static IReadOnlyList<string> Preview(IntegrationAnalysis a, IntegrationOptions o)
    {
        var steps = new List<string>();
        var stash = a.WorkingTreeDirty && (a.NeedsCheckout || a.TargetIsCurrent && a.Kind != IntegrationKind.NothingToDo) && o.StashChanges;
        if (stash) steps.Add($"git stash push --include-untracked   (mise de côté de tes modifications)");
        if (a.TargetBehindRemote > 0)
            steps.Add(a.TargetIsCurrent ? $"git merge --ff-only {a.TargetUpstream}" : $"git fetch . {a.TargetUpstream}:{a.Target}");

        switch (a.Kind)
        {
            case IntegrationKind.FastForward when a.TargetIsCurrent:
                steps.Add($"git merge --ff-only {a.SourceRef}");
                break;
            case IntegrationKind.FastForward:
                steps.Add($"git fetch . {a.SourceRef}:{a.Target}   (avance {a.Target} sans changer de branche)");
                break;
            case IntegrationKind.Merge or IntegrationKind.Conflicts:
                if (a.NeedsCheckout) steps.Add($"git switch {a.Target}");
                steps.Add($"git merge --no-ff -m \"{o.MergeMessage ?? DefaultMessage(a)}\" {a.SourceRef}");
                if (a.Kind == IntegrationKind.Merge && a.NeedsCheckout) steps.Add($"git switch {a.CurrentBranch}   (retour sur ta branche)");
                break;
        }
        if (o.PushTarget && a.Kind != IntegrationKind.Conflicts) steps.Add($"git push {o.Remote} {a.Target}");
        if (o.DeleteSource && a.Kind != IntegrationKind.Conflicts) steps.Add($"git branch -d {a.Source}");
        if (stash && a.Kind != IntegrationKind.Conflicts) steps.Add("git stash pop   (restauration de tes modifications)");
        return steps;
    }

    public static string DefaultMessage(IntegrationAnalysis a) => $"Intègre {a.Source} dans {a.Target}";

    public async Task<IntegrationResult> IntegrateAsync(string dir, IntegrationAnalysis a, IntegrationOptions o,
        Func<Task<IReadOnlyDictionary<string, string>>>? auth = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<GitResult>();
        IntegrationResult Fail(string message) => new(false, message, steps);
        bool Ok(GitResult r) { steps.Add(r); return r.Succeeded; }
        string Explain() => steps.Last().Explanation ?? steps.Last().Output;

        if (a.Blocker is not (IntegrationBlocker.None or IntegrationBlocker.DirtyWorkingTree))
            return Fail(a.Summary);
        if (a.Kind == IntegrationKind.NothingToDo)
            return new(true, a.Summary, steps);
        if (a.Kind == IntegrationKind.Conflicts && !o.StartDespiteConflicts)
            return Fail(a.Summary);

        // 1. Mise de côté si on doit toucher au dossier de travail
        var touchesWorkingTree = a.NeedsCheckout || a.TargetIsCurrent;
        var stashed = false;
        if (a.WorkingTreeDirty && touchesWorkingTree)
        {
            if (!o.StashChanges) return Fail(a.Summary);
            progress?.Report("Mise de côté de tes modifications en cours…");
            var before = (await git.ListStashesAsync(dir, ct)).Count;
            if (!Ok(await git.StashAsync(dir, a.CurrentBranch, ct))) return Fail($"Impossible de mettre tes modifications de côté : {Explain()}");
            // git répond « rien à mettre de côté » sans erreur (ex. modification dans un sous-module) : on ne restaurera alors
            // pas une ancienne mise de côté qui n'est pas la nôtre.
            stashed = (await git.ListStashesAsync(dir, CancellationToken.None)).Count > before;
        }

        var message = o.MergeMessage ?? DefaultMessage(a);
        var conflictsLeft = false;
        var switchBackFailed = "";
        try
        {
            // 2. Rattrapage de la cible sur GitHub
            if (a.TargetBehindRemote > 0)
            {
                progress?.Report($"Mise à jour de {a.Target} avec sa version GitHub…");
                var catchUp = a.TargetIsCurrent
                    ? await git.MergeFastForwardOnlyAsync(dir, a.TargetUpstream!, ct)
                    : await git.FastForwardBranchAsync(dir, a.Target, a.TargetUpstream!, ct);
                if (!Ok(catchUp)) return Fail($"Impossible de mettre {a.Target} à jour : {Explain()}");
            }

            // 3. Intégration
            switch (a.Kind)
            {
                case IntegrationKind.FastForward:
                    progress?.Report($"Avance de {a.Target} jusqu'à {a.Source}…");
                    var ff = a.TargetIsCurrent
                        ? await git.MergeFastForwardOnlyAsync(dir, a.SourceRef, ct)
                        : await git.FastForwardBranchAsync(dir, a.Target, a.SourceRef, ct);
                    if (!Ok(ff)) return Fail($"L'avance rapide a échoué : {Explain()}");
                    break;

                case IntegrationKind.Merge or IntegrationKind.Conflicts:
                    if (a.NeedsCheckout)
                    {
                        progress?.Report($"Passage sur {a.Target}…");
                        if (!Ok(await git.SwitchAsync(dir, a.Target, ct))) return Fail($"Impossible de passer sur {a.Target} : {Explain()}");
                    }
                    progress?.Report($"Fusion de {a.Source} dans {a.Target}…");
                    var merge = await git.MergeAsync(dir, a.SourceRef, message, ct);
                    steps.Add(merge);
                    if (!merge.Succeeded)
                    {
                        if (await git.GetOperationAsync(dir, ct) == RepositoryOperation.Merge)
                        {
                            conflictsLeft = true;
                            return new IntegrationResult(false,
                                $"La fusion est en cours sur {a.Target}, avec des conflits à résoudre dans : {string.Join(", ", a.ConflictingFiles)}. "
                                + "Modifie ces fichiers (choisis la version à garder entre les marqueurs <<<<<<< et >>>>>>>), puis « Terminer la fusion », ou « Abandonner la fusion ».",
                                steps) { ConflictsToResolve = true };
                        }
                        var why = Explain();
                        // Échec sans fusion en cours (ex. historiques sans rapport) : retour sur la branche de départ,
                        // pour que tes modifications mises de côté y reviennent.
                        if (a.NeedsCheckout) Ok(await git.SwitchAsync(dir, a.CurrentBranch, CancellationToken.None));
                        return Fail($"La fusion a échoué : {why}");
                    }
                    if (a.NeedsCheckout)
                    {
                        progress?.Report($"Retour sur {a.CurrentBranch}…");
                        if (!Ok(await git.SwitchAsync(dir, a.CurrentBranch, ct)))
                            switchBackFailed = $" Attention : impossible de revenir sur {a.CurrentBranch} ({Explain()}) : tu es sur {a.Target}"
                                + (stashed ? ", et tes modifications restent mises de côté (« Restaurer les modifications mises de côté » une fois revenu sur " + a.CurrentBranch + ")." : ".");
                    }
                    break;
            }

            // 4. Envoi et nettoyage (facultatifs)
            if (o.PushTarget && auth is not null)
            {
                progress?.Report($"Envoi de {a.Target} sur GitHub…");
                if (!Ok(await git.PushAsync(dir, o.Remote, a.Target, a.TargetUpstream is null, await auth(), ct)))
                    return Fail($"{a.Target} est à jour sur ton poste, mais l'envoi sur GitHub a échoué : {Explain()}");
            }
            if (o.DeleteSource && a.Source != a.CurrentBranch && a.SourceRef == a.Source)
            {
                progress?.Report($"Suppression de la branche locale {a.Source}…");
                Ok(await git.DeleteBranchAsync(dir, a.Source, ct)); // refus éventuel de git sans gravité : signalé dans le détail
            }

            return new IntegrationResult(true, (a.Kind == IntegrationKind.FastForward
                ? $"{a.Target} contient maintenant le travail de {a.Source} (avance rapide)."
                : $"{a.Source} a été fusionnée dans {a.Target}.") + switchBackFailed, steps);
        }
        finally
        {
            // Les modifications mises de côté reviennent, sauf pendant une fusion avec conflits (elles attendront).
            // Jamais interrompu : les modifications mises de côté doivent revenir même si l'opération a été annulée.
            // Et seulement sur leur branche d'origine : si le retour sur cette branche a échoué, elles attendent (« Restaurer… »).
            // Une erreur ici ne remplace jamais le résultat de l'intégration : les modifications restent mises de côté.
            try
            {
                if (stashed && !conflictsLeft
                    && (await git.GetStatusAsync(dir, CancellationToken.None)).Branch == a.CurrentBranch
                    && await git.FindStashForBranchAsync(dir, a.CurrentBranch, CancellationToken.None) is { } stash)
                {
                    progress?.Report("Restauration de tes modifications…");
                    steps.Add(await git.PopStashAsync(dir, stash, CancellationToken.None));
                }
            }
            catch (GitCommandException) { }
        }
    }
}
