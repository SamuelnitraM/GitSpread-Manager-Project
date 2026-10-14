using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

/// <summary>Une branche du repo GitHub, avec sa couleur (mêmes règles que l'historique) et sa situation en clair.</summary>
public sealed class GitHubBranchRow(GitHubBranch branch, bool isDefault, string? defaultBranch, BranchColor color)
{
    public GitHubBranch Branch { get; } = branch;
    public string Name => Branch.Name;
    public bool IsDefault { get; } = isDefault;
    public BranchColor Color { get; } = color;
    public string LastCommit => Branch.Subject;
    public string DateLabel => Branch.Date is { } d ? RepositoryItemViewModel.Relative(d) : "";
    public string StateLabel => IsDefault ? "branche principale" : (Branch.NotInDefault, Branch.DefaultAhead) switch
    {
        (0, 0) => $"identique à {defaultBranch}",
        (0, var ahead) => $"déjà intégrée dans {defaultBranch} · {defaultBranch} a {ahead} commit{S(ahead)} de plus",
        (var n, 0) => $"{n} commit{S(n)} pas encore dans {defaultBranch}",
        var (n, ahead) => $"{n} commit{S(n)} pas encore dans {defaultBranch} · {defaultBranch} a {ahead} commit{S(ahead)} de plus",
    };
    public bool CanDelete => !IsDefault;
    public override string ToString() => Name;
    private static string S(int n) => n > 1 ? "s" : "";
}

/// <summary>F6c : section « Branches » du panneau d'un repo GitHub (lue sur GitHub, renommer, supprimer).</summary>
public sealed partial class GitHubRepositoryViewModel
{
    public ObservableCollection<GitHubBranchRow> Branches { get; } = [];

    [ObservableProperty] public partial string? BranchesError { get; set; }
    public bool HasBranchesError => BranchesError is not null;
    partial void OnBranchesErrorChanged(string? value) => OnPropertyChanged(nameof(HasBranchesError));

    public string BranchesLabel => Branches.Count switch
    {
        0 => Details?.DefaultBranch is null ? "Aucune branche : le repo est vide." : "",
        1 => "1 branche :",
        GitHubService.MaxBranches => $"Les {GitHubService.MaxBranches} branches les plus récentes :",
        var n => $"{n} branches :",
    };

    /// <summary>Renommer et supprimer des branches demande au moins l'accès en écriture (repo non archivé).</summary>
    public bool CanEditBranches => Details is { Permission: "ADMIN" or "MAINTAIN" or "WRITE", IsArchived: false } && !IsBusy;

    private async Task LoadBranchesAsync()
    {
        BranchesError = null;
        if (Details?.DefaultBranch is not { } main)
        {
            Branches.Clear();
            RaiseBranches();
            return;
        }
        try
        {
            var branches = await GitHub.ListBranchesAsync(Account, FullName, main);
            Branches.Clear();
            var shades = new Dictionary<BranchKind, int>();
            foreach (var b in branches.OrderByDescending(b => b.Name == main))
            {
                var kind = BranchKinds.Of(b.Name, main);
                var shade = shades.GetValueOrDefault(kind);
                shades[kind] = shade + 1;
                Branches.Add(new GitHubBranchRow(b, b.Name == main, main, new BranchColor(kind, shade)));
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Branches de {Repo} indisponibles", FullName);
            BranchesError = $"Branches indisponibles : {e.Message}";
        }
        RaiseBranches();
    }

    private void RaiseBranches()
    {
        OnPropertyChanged(nameof(BranchesLabel));
        OnPropertyChanged(nameof(CanEditBranches));
    }

    /// <summary>Renomme une branche sur GitHub ; les projets locaux liés suivent. La branche principale garde le parcours de F4.</summary>
    [RelayCommand]
    private async Task RenameBranchAsync(GitHubBranchRow? row)
    {
        if (row is null || !CanEditBranches) return;
        if (row.IsDefault)
        {
            await RenameDefaultBranchCommand.ExecuteAsync(null);
            await LoadBranchesAsync();
            return;
        }
        var from = row.Name;
        var to = host.Dialogs.Prompt("Renommer la branche",
            $"Nouveau nom pour « {from} » sur {FullName}. GitHub garde ses pull requests et redirige l'ancien nom ; les projets locaux liés suivent.",
            from, n => BranchName.Validate(n) ?? (n == from ? "C'est le nom actuel." : Branches.Any(b => b.Name == n) ? "Une branche porte déjà ce nom." : null),
            spacesToDash: true);
        if (string.IsNullOrWhiteSpace(to)) return;
        to = to.Trim();

        await RunAsync("Renommer la branche", async () =>
        {
            await GitHub.RenameBranchAsync(Account, FullName, from, to);
            var results = new List<GitResult>();
            foreach (var project in LinkedProjects)
                results.AddRange(await host.Services.Linked.FollowBranchRenameAsync(project, Account, from, to, FullName, isDefault: false));
            return Log("Renommer la branche", $"gh api --method POST repos/{FullName}/branches/{from}/rename -f new_name={to}", results,
                $"La branche « {from} » s'appelle maintenant « {to} » sur GitHub." + LinkedSummary(results));
        });
        await LoadBranchesAsync();
        await host.RescanAsync();
    }

    /// <summary>Supprime une branche sur GitHub (jamais la principale). Les copies locales ne sont pas touchées.</summary>
    [RelayCommand]
    private async Task DeleteBranchAsync(GitHubBranchRow? row)
    {
        if (row is null || !CanEditBranches) return;
        if (row.IsDefault)
        {
            LastAction = ActionLog.Failure("Supprimer la branche", $"« {row.Name} » est la branche principale de {FullName} : elle ne peut pas être supprimée.");
            return;
        }
        var n = row.Branch.NotInDefault;
        var warning = n == 0
            ? $"Tout son travail est déjà dans « {Details?.DefaultBranch} » : rien n'est perdu."
            : $"ATTENTION : {n} commit{(n > 1 ? "s" : "")} de cette branche {(n > 1 ? "ne sont" : "n'est")} pas dans « {Details?.DefaultBranch} ». "
              + "Ils ne resteront que dans les copies locales qui les ont déjà.";
        if (!host.Dialogs.Confirm($"Supprimer la branche « {row.Name} » sur GitHub ?\n\n{warning}\n\nLes projets locaux ne sont pas touchés.",
                "Supprimer la branche", destructive: true))
            return;

        await RunAsync("Supprimer la branche", async () =>
        {
            await GitHub.DeleteBranchAsync(Account, FullName, row.Name);
            // Les projets liés oublient leur copie de la branche distante (leurs branches locales restent).
            var results = new List<GitResult>();
            foreach (var project in LinkedProjects)
                if (Core.Projects.LinkedProjects.RemoteFor(project, Account.Host, FullName) is { } remote
                    && await host.Services.Git.ForgetRemoteBranchAsync(project.Path, remote.Name, row.Name) is { } forgotten)
                    results.Add(forgotten);
            return Log("Supprimer la branche", $"gh api --method DELETE repos/{FullName}/git/refs/heads/{row.Name}", results,
                $"La branche « {row.Name} » est supprimée de GitHub.");
        });
        await LoadBranchesAsync();
    }
}
