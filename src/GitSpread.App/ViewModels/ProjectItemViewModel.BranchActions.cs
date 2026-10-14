using GitSpread.Core.Git;
using GitSpread.Core.Projects;

namespace GitSpread.App.ViewModels;

/// <summary>F6b : actions sur n'importe quelle branche, depuis l'historique en grand.</summary>
public sealed partial class ProjectItemViewModel
{
    private string PrimaryRemote => Project.RemoteName ?? ProjectLinks.PrimaryName;

    /// <summary>Les actions ont besoin de l'état du projet (modifications en cours, branche actuelle) : lu si ce n'est pas déjà fait.</summary>
    public async Task EnsureDetailsAsync()
    {
        if (!HasDetails) await LoadDetailsAsync();
    }

    /// <summary>Passe sur une branche (récupérée depuis GitHub si elle n'existe pas encore ici).</summary>
    public Task SwitchToBranchAsync(BranchInfo branch) => SwitchBranchAsync(new BranchItemViewModel(branch));

    /// <summary>Crée une branche à partir d'un commit de l'historique, puis passe dessus.</summary>
    public async Task CreateBranchFromAsync(string sha, string shortSha)
    {
        var name = host.Dialogs.Prompt("Nouvelle branche",
            $"Nom de la nouvelle branche, créée à partir du commit {shortSha}. Tu passeras dessus pour y travailler.",
            "", n => BranchName.Validate(n) ?? (Branches.Any(b => b.Name == n) ? "Une branche porte déjà ce nom." : null), spacesToDash: true);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        await RunAsync("Nouvelle branche", async () =>
            ActionLog.From("Nouvelle branche", [await Git.CreateBranchAtAsync(Path, name, sha)], $"Branche « {name} » créée à partir du commit {shortSha}."));
        if (LastAction is not { Succeeded: true } created) return;

        await SwitchBranchAsync(new BranchItemViewModel(new BranchInfo(name) { IsLocal = true }));
        if (LastAction is { Succeeded: true } switched)
            LastAction = switched with { Title = created.Title, Message = created.Message + " " + switched.Message, Commands = created.Commands + "\n" + switched.Commands };
    }

    /// <summary>Envoie une branche (pas forcément l'actuelle) sur GitHub, vers les liens cochés (F3B).</summary>
    public Task PushBranchAsync(BranchInfo branch) => RunNetworkAsync("Pousser", async auth =>
    {
        var firstPush = branch.Upstream is null;
        if (Placement.HasSecondaryLinks && SelectedLinks is { Count: > 0 } targets && targets is not [{ IsPrimary: true }])
            return await PushToLinksAsync(branch.Name, targets, setPrimaryUpstream: firstPush);
        var result = await Git.PushAsync(Path, PrimaryRemote, branch.Name, setUpstream: firstPush, auth);
        return ActionLog.From("Pousser", [result], result.Output.Contains("Everything up-to-date")
            ? $"« {branch.Name} » est déjà à jour sur GitHub."
            : $"Branche « {branch.Name} » envoyée sur GitHub.");
    });

    /// <summary>
    /// Renomme une branche ici et, si elle y est, sur GitHub (qui garde ses pull requests). Les autres projets de ce poste
    /// liés au même repo suivent ; les liens secondaires où la branche existe sont proposés un par un.
    /// </summary>
    public async Task RenameBranchAsync(BranchInfo branch, IReadOnlyCollection<string> existing, string? defaultBranch)
    {
        var from = branch.Name;
        var onGitHub = Remote is { IsGitHub: true, FullName: not null } && Placement.NetworkAccount is not null
                       && await Git.RemoteBranchExistsAsync(Path, PrimaryRemote, from);
        var to = host.Dialogs.Prompt("Renommer la branche",
            $"Nouveau nom pour « {from} »."
            + (onGitHub ? $" Elle est aussi renommée sur GitHub ({Remote!.FullName}) : GitHub garde ses pull requests et redirige l'ancien nom." : " Elle n'existe que sur ce poste."),
            from, n => BranchName.Validate(n) ?? (n == from ? "C'est le nom actuel." : existing.Contains(n) ? "Une branche porte déjà ce nom." : null),
            spacesToDash: true);
        if (string.IsNullOrWhiteSpace(to)) return;
        to = to.Trim();

        // Liens secondaires (F3B) où la branche existe aussi : à cocher un par un.
        var secondary = new List<ProjectLink>();
        foreach (var link in Placement.SecondaryLinks.Where(l => l is { Account: not null, Url: { IsGitHub: true, FullName: not null } }))
            if (await Git.RemoteBranchExistsAsync(Path, link.Name, from)
                && host.Dialogs.Confirm($"« {from} » existe aussi sur {link.RepositoryLabel} (lien « {link.Name} »). La renommer là aussi ?", "Renommer la branche"))
                secondary.Add(link);

        var isDefault = from == defaultBranch;
        await RunAsync("Renommer la branche", async () =>
        {
            var steps = new List<GitResult>();
            var commands = new List<string>();
            if (onGitHub)
            {
                var account = Placement.NetworkAccount!;
                await host.Services.GitHub.RenameBranchAsync(account, Remote!.FullName!, from, to);
                commands.Add($"gh api --method POST repos/{Remote.FullName}/branches/{from}/rename -f new_name={to}   (avec le compte {account.Login})");
                steps.AddRange(await host.Services.Linked.FollowBranchRenameAsync(Project, account, from, to, Remote.FullName, isDefault));
                // Autres copies du même repo sur ce poste
                foreach (var other in LinkedProjects.Find(host.LocalProjects, Remote.Host, Remote.FullName!).Where(p => !p.Path.Equals(Path, StringComparison.OrdinalIgnoreCase)))
                    steps.AddRange(await host.Services.Linked.FollowBranchRenameAsync(other, account, from, to, Remote.FullName, isDefault));
            }
            else steps.Add(await Git.RenameLocalBranchAsync(Path, from, to));

            foreach (var link in secondary)
            {
                await host.Services.GitHub.RenameBranchAsync(link.Account!, link.Url.FullName!, from, to);
                commands.Add($"gh api --method POST repos/{link.Url.FullName}/branches/{from}/rename -f new_name={to}   (avec le compte {link.Account!.Login})");
                steps.Add(await Git.FetchAsync(Path, link.Name, await host.Services.Credentials.EnvironmentForAsync(link.Account, link.Url)));
                if (await Git.ForgetRemoteBranchAsync(Path, link.Name, from) is { } forgotten) steps.Add(forgotten);
            }

            var where = onGitHub || secondary.Count > 0
                ? $" ici et sur GitHub{(secondary.Count > 0 ? $" ({1 + secondary.Count} repos)" : "")}"
                : "";
            var log = ActionLog.From("Renommer la branche", steps, $"La branche « {from} » s'appelle maintenant « {to} »{where}.");
            return log with { Commands = string.Join("\n", commands.Append(log.Commands).Where(c => c.Length > 0)) };
        });
    }

    /// <summary>
    /// Supprime une branche ici et/ou sur GitHub. Refusé pour la branche principale et la branche actuelle ;
    /// avertit quand des commits n'existent nulle part ailleurs. « Aussi sur GitHub » n'est jamais choisi par défaut.
    /// </summary>
    public async Task DeleteBranchAsync(BranchInfo branch, string? defaultBranch)
    {
        const string title = "Supprimer la branche";
        var name = branch.Name;
        if (name == defaultBranch)
        {
            LastAction = ActionLog.Failure(title, $"« {name} » est la branche principale du projet : elle ne peut pas être supprimée.");
            return;
        }
        if (branch.IsCurrent)
        {
            LastAction = ActionLog.Failure(title, $"Tu es sur « {name} » : passe d'abord sur une autre branche (ex. {defaultBranch ?? "main"}) pour pouvoir la supprimer.");
            return;
        }

        var remoteRef = $"{PrimaryRemote}/{name}";
        var onGitHub = await Git.RemoteBranchExistsAsync(Path, PrimaryRemote, name);
        string[] mainRef = defaultBranch is null ? [] : [Branches.FirstOrDefault(b => b.Name == defaultBranch)?.Branch.Ref ?? defaultBranch];

        bool deleteLocal = branch.IsLocal, deleteRemote;
        if (branch.IsLocal && onGitHub)
        {
            var choice = host.Dialogs.Choose(title, $"Où supprimer la branche « {name} » ?",
            [
                "Seulement sur ce poste : elle reste sur GitHub (recommandé)",
                "Sur ce poste et sur GitHub",
            ]);
            if (choice < 0) return;
            deleteRemote = choice == 1;
        }
        else deleteRemote = !branch.IsLocal && onGitHub;

        // Ce qui serait perdu : commits absents de la branche principale (et de GitHub, si on ne supprime qu'ici).
        var reference = branch.IsLocal ? name : remoteRef;
        var lost = deleteRemote
            ? await Git.CountCommitsOnlyInAsync(Path, reference, mainRef, excludeRemotes: false)
            : await Git.CountCommitsOnlyInAsync(Path, reference, mainRef, excludeRemotes: true);
        var where = (deleteLocal, deleteRemote) switch { (true, true) => "sur ce poste et sur GitHub", (true, false) => "sur ce poste", _ => "sur GitHub" };
        var warning = lost == 0
            ? $"Tout son travail est déjà dans « {defaultBranch} »{(deleteRemote ? "" : " ou sur GitHub")} : rien n'est perdu."
            : $"ATTENTION : {lost} commit{(lost > 1 ? "s" : "")} de cette branche {(lost > 1 ? "ne sont" : "n'est")} pas dans « {defaultBranch} »"
              + (deleteRemote ? "" : " ni sur GitHub") + " et " + (lost > 1 ? "seront perdus" : "sera perdu") + ".";
        if (!host.Dialogs.Confirm($"Supprimer la branche « {name} » {where} ?\n\n{warning}", title, destructive: lost > 0 || deleteRemote))
            return;

        await RunAsync(title, async () =>
        {
            var steps = new List<GitResult>();
            if (deleteLocal) steps.Add(await Git.ForceDeleteBranchAsync(Path, name));
            if (deleteRemote && steps.All(s => s.Succeeded))
                steps.Add(await Git.DeleteRemoteBranchAsync(Path, PrimaryRemote, name,
                    await host.Services.Credentials.EnvironmentForAsync(Placement.NetworkAccount, Remote)));
            return ActionLog.From(title, steps, $"La branche « {name} » est supprimée {where}.");
        });
    }

    /// <summary>
    /// Remet dans le projet la version d'un fichier à un commit (F6e). Seul le fichier sur le disque change : la restauration
    /// apparaît comme un changement normal, à commiter. Refusé si le fichier a des changements pas encore commités.
    /// </summary>
    public async Task RestoreFileAsync(string sha, string shortSha, string when, string file)
    {
        if (await Git.HasUncommittedChangesAsync(Path, file))
        {
            LastAction = ActionLog.Failure("Restaurer un fichier",
                $"« {file} » a des changements pas encore commités : commite-les (ou annule-les) d'abord, pour ne rien perdre. "
                + "Tu peux aussi utiliser « Enregistrer cette version sous… » pour récupérer l'ancienne version ailleurs.");
            return;
        }
        if (!host.Dialogs.Confirm(
                $"Remettre « {file} » comme il était au commit {shortSha} ({when}) ?\n\n"
                + "Le fichier du projet est remplacé par cette version. L'historique n'est pas modifié : la restauration "
                + "apparaîtra comme un changement à commiter, que tu pourras aussi annuler.",
                "Restaurer cette version"))
            return;
        await RunAsync("Restaurer un fichier", async () =>
            ActionLog.From("Restaurer un fichier", [await Git.RestoreFileAsync(Path, sha, file)],
                $"« {file} » a retrouvé sa version du commit {shortSha}. Commite-le si elle te convient."));
    }
}
