using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;

namespace GitSpread.App.ViewModels;

/// <summary>Entrée du menu déroulant des branches.</summary>
public sealed class BranchItemViewModel(BranchInfo branch)
{
    public BranchInfo Branch { get; } = branch;
    public string Name => Branch.Name;
    public string Label => Branch.IsRemoteOnly ? $"{Branch.Name}   (sur GitHub uniquement)" : Branch.Name;
    public override string ToString() => Label;
}

/// <summary>Branche annexe comparée à la branche principale, avec les actions possibles.</summary>
public sealed class SideBranchViewModel(ProjectItemViewModel project, BranchInfo branch, string mainBranch, int onlyHere, int onlyInMain)
{
    public BranchInfo Branch { get; } = branch;
    public string Name => Branch.Name;
    public bool IsCurrent => Branch.IsCurrent;
    public string Where => Branch.IsRemoteOnly ? "sur GitHub uniquement" : Branch.IsCurrent ? "branche actuelle" : "";
    public bool HasWhere => Where.Length > 0;

    public string Info => (onlyHere, onlyInMain) switch
    {
        (0, 0) => $"Identique à {mainBranch}",
        (> 0, 0) => $"{onlyHere} commit{S(onlyHere)} pas encore dans {mainBranch}",
        (0, > 0) => $"Déjà intégrée · {mainBranch} a {onlyInMain} commit{S(onlyInMain)} de plus",
        _ => $"{onlyHere} commit{S(onlyHere)} pas encore dans {mainBranch} · {mainBranch} a {onlyInMain} commit{S(onlyInMain)} de plus",
    };

    /// <summary>« Intégrer dans main » : la branche contient du travail absent de main.</summary>
    public bool CanIntegrate => onlyHere > 0;
    public string IntegrateLabel => $"Intégrer dans {mainBranch}";

    /// <summary>« Mettre à jour avec main » : main a avancé depuis (branche locale seulement).</summary>
    public bool CanUpdate => onlyInMain > 0 && Branch.IsLocal;
    public string UpdateLabel => $"Mettre à jour avec {mainBranch}";

    public IAsyncRelayCommand IntegrateCommand => new AsyncRelayCommand(() => project.IntegrateAsync(Name, mainBranch));
    public IAsyncRelayCommand UpdateCommand => new AsyncRelayCommand(() => project.IntegrateAsync(mainBranch, Name));

    private static string S(int n) => n > 1 ? "s" : "";
}

public sealed partial class ProjectItemViewModel
{
    private const int MaxSideBranches = 15;
    private bool _syncingBranch;

    public ObservableCollection<BranchItemViewModel> Branches { get; } = [];
    public ObservableCollection<SideBranchViewModel> SideBranches { get; } = [];
    public ObservableCollection<string> ConflictFiles { get; } = [];

    [ObservableProperty] public partial BranchItemViewModel? SelectedBranch { get; set; }
    [ObservableProperty] public partial bool HasMultipleBranches { get; set; }
    [ObservableProperty] public partial bool HasSideBranches { get; set; }
    [ObservableProperty] public partial string? MainBranch { get; set; }
    [ObservableProperty] public partial bool HasPendingStash { get; set; }
    [ObservableProperty] public partial RepositoryOperation Operation { get; set; }
    [ObservableProperty] public partial string OperationLabel { get; set; } = "";

    public bool HasOperation => Operation != RepositoryOperation.None;
    public bool CanConcludeMerge => Operation == RepositoryOperation.Merge && ConflictFiles.Count == 0;
    partial void OnOperationChanged(RepositoryOperation value) { OnPropertyChanged(nameof(HasOperation)); OnPropertyChanged(nameof(CanConcludeMerge)); }

    /// <summary>Changement dans le menu déroulant : on passe sur la branche choisie.</summary>
    partial void OnSelectedBranchChanged(BranchItemViewModel? value)
    {
        if (_syncingBranch || value is null || value.Branch.IsCurrent) return;
        _ = SwitchBranchAsync(value);
    }

    private async Task LoadBranchStateAsync()
    {
        var branches = await Git.ListBranchesAsync(Path);
        _syncingBranch = true;
        try
        {
            Branches.Clear();
            foreach (var b in branches) Branches.Add(new BranchItemViewModel(b));
            SelectedBranch = Branches.FirstOrDefault(b => b.Branch.IsCurrent);
        }
        finally { _syncingBranch = false; }
        HasMultipleBranches = Branches.Count > 1;

        Operation = await Git.GetOperationAsync(Path);
        ConflictFiles.Clear();
        if (HasOperation)
            foreach (var c in (await Git.GetChangesAsync(Path)).Where(c => c.Kind == ChangeKind.Conflicted)) ConflictFiles.Add(c.Path);
        OperationLabel = Operation switch
        {
            RepositoryOperation.Merge when ConflictFiles.Count > 0 => $"Fusion en cours : {ConflictFiles.Count} fichier{(ConflictFiles.Count > 1 ? "s" : "")} en conflit à résoudre",
            RepositoryOperation.Merge => "Fusion en cours : tous les conflits sont résolus, il ne reste qu'à la terminer",
            RepositoryOperation.Rebase => "Un rebase est en cours (lancé hors de GitSpread)",
            RepositoryOperation.CherryPick => "Un cherry-pick est en cours (lancé hors de GitSpread)",
            RepositoryOperation.Revert => "Un revert est en cours (lancé hors de GitSpread)",
            _ => "",
        };
        OnPropertyChanged(nameof(CanConcludeMerge));

        HasPendingStash = _status?.Branch is { } current && await Git.FindStashForBranchAsync(Path, current) is not null;

        // Branches annexes comparées à la branche principale
        SideBranches.Clear();
        MainBranch = await Git.GetDefaultBranchAsync(Path, Project.RemoteName ?? "origin");
        if (MainBranch is { } main && branches.FirstOrDefault(b => b.Name == main) is { } mainInfo)
        {
            foreach (var b in branches.Where(b => b.Name != main).Take(MaxSideBranches))
            {
                try
                {
                    var (onlyHere, onlyInMain) = await Git.CompareAsync(Path, b.Ref, mainInfo.Ref);
                    SideBranches.Add(new SideBranchViewModel(this, b, main, onlyHere, onlyInMain));
                }
                catch (GitCommandException) { /* branche sans historique commun : ignorée */ }
            }
        }
        HasSideBranches = SideBranches.Count > 0;
    }

    private void ResyncSelectedBranch()
    {
        _syncingBranch = true;
        SelectedBranch = Branches.FirstOrDefault(b => b.Branch.IsCurrent);
        _syncingBranch = false;
    }

    // ------------------------------------------------------------ changer de branche

    public async Task SwitchBranchAsync(BranchItemViewModel target)
    {
        var current = _status?.Branch;
        var stash = false;
        if (_status is { IsClean: false } && current is not null)
        {
            var choice = host.Dialogs.Choose("Changer de branche",
                $"Tu as des modifications non commitées sur « {current} ». Que faut-il en faire ?",
                [
                    $"Les laisser sur « {current} » : elles sont mises de côté et te seront proposées à ton retour (recommandé)",
                    $"Les emporter sur « {target.Name} »",
                ]);
            if (choice < 0) { ResyncSelectedBranch(); return; }
            stash = choice == 0;
        }

        await RunAsync("Changer de branche", async () =>
        {
            var results = new List<GitResult>();
            if (stash)
            {
                var s = await Git.StashAsync(Path, current!);
                results.Add(s);
                if (!s.Succeeded) return ActionLog.From("Changer de branche", results, "");
            }

            var sw = await Git.SwitchAsync(Path, target.Branch);
            results.Add(sw);
            if (!sw.Succeeded)
            {
                if (stash && await Git.FindStashForBranchAsync(Path, current!) is { } back) results.Add(await Git.PopStashAsync(Path, back));
                return ActionLog.From("Changer de branche", results, "");
            }

            var message = $"Tu es maintenant sur « {target.Name} »."
                + (target.Branch.IsRemoteOnly ? " Elle a été récupérée depuis GitHub et reste liée à sa version en ligne." : "")
                + (stash ? $" Tes modifications de « {current} » sont mises de côté." : "");

            if (await Git.FindStashForBranchAsync(Path, target.Name) is { } pending
                && host.Dialogs.Confirm($"Des modifications avaient été mises de côté sur « {target.Name} ». Les restaurer maintenant ?", "Modifications mises de côté"))
            {
                var pop = await Git.PopStashAsync(Path, pending);
                results.Add(pop);
                message += pop.Succeeded ? " Tes modifications mises de côté sont revenues." : $" La restauration a échoué : {pop.Explanation ?? pop.Output}";
            }
            return ActionLog.From("Changer de branche", results, message);
        });
    }

    [RelayCommand]
    private async Task NewBranchAsync()
    {
        var name = host.Dialogs.Prompt("Nouvelle branche",
            $"Nom de la nouvelle branche, créée à partir de « {_status?.Branch ?? "la position actuelle"} ». Tes modifications en cours la suivent.",
            "", name => BranchName.Validate(name) ?? (Branches.Any(b => b.Name == name) ? "Une branche porte déjà ce nom." : null), spacesToDash: true);
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync("Nouvelle branche", async () =>
            ActionLog.From("Nouvelle branche", [await Git.CreateBranchAsync(Path, name.Trim())],
                $"Branche « {name.Trim()} » créée : tu es dessus. Elle sera envoyée sur GitHub au premier « Pousser »."));
    }

    [RelayCommand]
    private Task RestoreStashAsync() => RunAsync("Restaurer les modifications", async () =>
    {
        if (_status?.Branch is not { } branch || await Git.FindStashForBranchAsync(Path, branch) is not { } stash)
            return ActionLog.Failure("Restaurer les modifications", "Aucune modification mise de côté pour cette branche.");
        return ActionLog.From("Restaurer les modifications", [await Git.PopStashAsync(Path, stash)], "Tes modifications mises de côté sont revenues.");
    });

    // ------------------------------------------------------------ fusion en cours

    [RelayCommand]
    private async Task AbortOperationAsync()
    {
        if (!host.Dialogs.Confirm("Abandonner l'opération en cours ?\n\nLe projet revient exactement à son état d'avant la fusion. Les fichiers déjà modifiés pour résoudre les conflits seront remis comme avant.",
                "Abandonner", destructive: true))
            return;
        var operation = Operation;
        await RunAsync("Abandonner", async () =>
            ActionLog.From("Abandonner", [await Git.AbortOperationAsync(Path, operation)], "Opération abandonnée : le projet est revenu à son état d'avant."));
    }

    [RelayCommand]
    private Task ConcludeMergeAsync() => RunAsync("Terminer la fusion", async () =>
        ActionLog.From("Terminer la fusion", await Git.ConcludeMergeAsync(Path), "Fusion terminée : un commit de fusion a été créé."));

    // ------------------------------------------------------------ intégration entre branches

    /// <summary>Intègre <paramref name="source"/> dans <paramref name="target"/> après analyse et confirmation.</summary>
    public async Task IntegrateAsync(string source, string target)
    {
        var dialog = new IntegrationViewModel(this, host, source, target);
        await dialog.LoadAsync();
        if (!host.Dialogs.ShowIntegration(dialog) || dialog.Analysis is not { } analysis) return;

        var options = dialog.BuildOptions();
        await RunAsync(dialog.Title, async () =>
        {
            var result = await host.Services.Integrator.IntegrateAsync(Path, analysis, options,
                () => host.Services.Credentials.EnvironmentForAsync(Placement.NetworkAccount, Remote));
            return new ActionLog(dialog.Title, result.Succeeded || result.ConflictsToResolve, result.Message,
                string.Join("\n", result.Steps.Select(s => s.Command)),
                string.Join("\n", result.Steps.Select(s => s.Output).Where(o => o.Length > 0)));
        });
    }

    // ------------------------------------------------------------ étapes 4 et 5 : changer le repo lié / de compte

    [RelayCommand]
    private Task ChangeLinkedRepo() => ChangeLinkedRepositoryAsync(null);

    /// <summary>
    /// Ouvre l'assistant de repointage, éventuellement vers un compte précis (projet déposé sur son onglet).
    /// Un projet sans remote (ou pas encore suivi par git) n'a rien à repointer : on propose de le publier sur ce compte.
    /// Déposé sur l'onglet d'un autre compte, un projet lié peut aussi simplement être partagé avec ce compte (F3A).
    /// </summary>
    public async Task ChangeLinkedRepositoryAsync(GitHubAccount? targetAccount)
    {
        if (!HasRemote)
        {
            await PublishToAsync(targetAccount);
            return;
        }

        if (targetAccount is not null && Placement.Account is { } owner && !owner.Is(targetAccount)
            && owner.Host.Equals(targetAccount.Host, StringComparison.OrdinalIgnoreCase)
            && Remote is { IsGitHub: true, FullName: { } fullName } && !Placement.IsCollaborator(targetAccount))
        {
            var choice = host.Dialogs.Choose($"« {Name} » et {targetAccount.Login}",
                $"Que faire de « {Name} » avec le compte {targetAccount.Login} ?",
                [
                    $"Donner accès à {targetAccount.Login} (collaborateur) : le repo reste chez {owner.Login}, les deux comptes peuvent envoyer",
                    $"Relier le projet à un repo de {targetAccount.Login} (nouveau repo ou transfert)…",
                ]);
            if (choice < 0) return;
            if (choice == 0)
            {
                await RunAsync("Ajouter un collaborateur", () => Collaboration.AddAsync(host, owner, fullName, targetAccount.Login));
                await host.ReloadCollaborationsAsync();
                return;
            }
        }

        var dialog = new RepointViewModel(this, host, targetAccount);
        host.Dialogs.ShowRepoint(dialog);
        if (dialog.Done) await host.RescanAsync(Path);
    }
}
