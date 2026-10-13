using CommunityToolkit.Mvvm.ComponentModel;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.Projects;

namespace GitSpread.App.ViewModels;

/// <summary>Entrée du menu « Envoyer avec le compte ».</summary>
public sealed record PushAccountChoice(GitHubAccount Account, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Projet dont le repo est partagé entre plusieurs comptes connectés (F3A).</summary>
public sealed partial class ProjectItemViewModel
{
    /// <summary>Compte de l'onglet où ce projet est affiché (null : « Non liés / autres »).</summary>
    public GitHubAccount? ViewingAccount { get; init; }

    /// <summary>Affiché dans l'onglet d'un compte collaborateur du repo (et non propriétaire).</summary>
    public bool IsViewedAsCollaborator => ViewingAccount is not null && Placement.IsCollaborator(ViewingAccount);

    /// <summary>Repo d'un compte connecté auquel d'autres comptes connectés ont accès.</summary>
    public bool IsShared => Placement.Collaborators.Count > 0;

    public string SharingBadge => IsViewedAsCollaborator ? "Collaborateur" : "Partagé";
    public string SharingLabel => IsViewedAsCollaborator
        ? $"Repo de {Remote?.Owner} : {ViewingAccount!.Login} y a accès en collaborateur."
        : $"Partagé avec {string.Join(", ", Placement.Collaborators.Select(c => c.Login))} (collaborateur{(Placement.Collaborators.Count > 1 ? "s" : "")}).";

    // ------------------------------------------------------------ « Envoyer avec le compte »

    public IReadOnlyList<PushAccountChoice> PushAccountChoices { get; } = BuildPushChoices(placement);

    /// <summary>Choix proposé seulement en HTTPS (en SSH, ce sont les clés qui décident) et si plusieurs comptes ont accès.</summary>
    public bool HasPushAccountChoice => Remote?.Protocol == RemoteProtocol.Https && PushAccountChoices.Count > 1;

    [ObservableProperty] public partial PushAccountChoice? SelectedPushAccount { get; set; } = InitialPushChoice(placement);

    public const string PushAccountNote =
        "Le compte qui envoie n'est pas forcément celui qui signe : la signature des commits se règle dans « Commits signés par ».";

    partial void OnSelectedPushAccountChanged(PushAccountChoice? value)
    {
        if (value is null || value.Account.Is(Placement.NetworkAccount)) return;
        _ = ApplyPushAccountAsync(value);
    }

    private async Task ApplyPushAccountAsync(PushAccountChoice choice)
    {
        const string title = "Envoyer avec le compte";
        var account = choice.Account;
        // Le premier compte (le propriétaire s'il est connecté) est le choix par défaut : rien à mémoriser.
        var login = account.Is(Placement.AccessAccounts[0]) ? null : account.Login;
        await RunAsync(title, async () => ActionLog.From(title, [await Git.SetPushAccountAsync(Path, login)],
            $"Récupérer, tirer et pousser se feront désormais avec {account.Login}"
            + (Placement.IsCollaborator(account) ? " (collaborateur)." : " (propriétaire).")
            + " Les commits restent signés par l'identité choisie dans « Commits signés par »."));
        await host.RescanAsync(); // le projet est reconstruit avec son nouveau compte d'envoi
    }

    private static List<PushAccountChoice> BuildPushChoices(ProjectPlacement placement) =>
        placement.AccessAccounts
            .Select(a => new PushAccountChoice(a, a.Is(placement.Account) ? $"{a.Login} (propriétaire)" : $"{a.Login} (collaborateur)"))
            .ToList();

    private static PushAccountChoice? InitialPushChoice(ProjectPlacement placement) =>
        BuildPushChoices(placement).FirstOrDefault(c => c.Account.Is(placement.NetworkAccount));
}
