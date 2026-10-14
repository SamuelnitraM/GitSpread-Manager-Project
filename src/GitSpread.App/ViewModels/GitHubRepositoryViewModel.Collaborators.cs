using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

/// <summary>Une ligne de la section « Collaborateurs » : collaborateur actif ou invitation en attente.</summary>
public sealed record CollaboratorRow(string Login, bool IsPending, long? InvitationId, bool IsConnected)
{
    public string Detail => (IsPending ? "invitation envoyée, pas encore acceptée" : "accès en écriture")
                            + (IsConnected ? " · compte connecté à GitSpread" : "");
    public string ActionLabel => IsPending ? "Annuler l'invitation" : "Retirer…";
    public override string ToString() => Login;
}

/// <summary>Collaborateurs du repo (F3A) : liste, invitation, retrait, et « quitter » côté collaborateur.</summary>
public sealed partial class GitHubRepositoryViewModel
{
    public ObservableCollection<CollaboratorRow> Collaborators { get; } = [];

    [ObservableProperty] public partial string? CollaboratorsError { get; set; }
    public bool HasCollaboratorsError => CollaboratorsError is not null;
    partial void OnCollaboratorsErrorChanged(string? value) => OnPropertyChanged(nameof(HasCollaboratorsError));

    public string OwnerLogin => FullName.Split('/')[0];

    /// <summary>Le compte de l'onglet n'est pas propriétaire : il a accès au repo en collaborateur.</summary>
    public bool IsViewerCollaborator => !Account.Login.Equals(OwnerLogin, StringComparison.OrdinalIgnoreCase);
    public string OwnerLabel => $"Repo de {OwnerLogin} : {Account.Login} y a accès en collaborateur (écriture).";
    public bool CanManageCollaborators => CanAdminister;
    public bool CanLeave => IsViewerCollaborator && CanAct;
    public string CollaboratorsNote => Collaboration.WriteAccessNote;

    public string CollaboratorsLabel => Collaborators.Count(c => !c.IsPending) switch
    {
        0 when Collaborators.Count == 0 => $"Aucun collaborateur : seul {OwnerLogin} peut envoyer sur ce repo.",
        0 => "Aucun collaborateur pour l'instant.",
        1 => "1 collaborateur :",
        var n => $"{n} collaborateurs :",
    };

    private async Task LoadCollaboratorsAsync()
    {
        CollaboratorsError = null;
        if (Details?.Permission is not ("ADMIN" or "MAINTAIN" or "WRITE"))
        {
            Collaborators.Clear();
            RaiseCollaborators();
            return;
        }
        try
        {
            var collaborators = await GitHub.ListCollaboratorsAsync(Account, FullName);
            // Les invitations en attente ne sont visibles que par un administrateur du repo.
            IReadOnlyList<Core.GitHub.GitHubInvitation> pending = Details is { IsAdmin: true } ? await GitHub.ListPendingInvitationsAsync(Account, FullName) : [];
            Collaborators.Clear();
            foreach (var c in collaborators) Collaborators.Add(new CollaboratorRow(c.Login, false, null, IsConnected(c.Login)));
            foreach (var i in pending.Where(i => !i.IsExpired)) Collaborators.Add(new CollaboratorRow(i.Invitee, true, i.Id, IsConnected(i.Invitee)));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Collaborateurs de {Repo} indisponibles", FullName);
            CollaboratorsError = $"Liste des collaborateurs indisponible : {e.Message}";
        }
        RaiseCollaborators();
    }

    [RelayCommand]
    private async Task InviteCollaboratorAsync()
    {
        if (!CanManageCollaborators) return;
        var taken = Collaborators.Select(c => c.Login).Append(OwnerLogin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connected = host.Accounts.Where(a => a.IsAuthenticated && a.Host == Account.Host && !taken.Contains(a.Login)).ToList();
        var options = connected.Select(a => $"{a.Login} (compte connecté : invitation acceptée automatiquement)").Append("Un autre utilisateur GitHub…").ToList();

        var choice = host.Dialogs.Choose("Ajouter un collaborateur",
            $"Qui doit pouvoir envoyer des commits sur {FullName} ? Le repo reste chez {OwnerLogin}.\n\n{Collaboration.WriteAccessNote}", options);
        if (choice < 0) return;

        var login = choice < connected.Count ? connected[choice].Login
            : host.Dialogs.Prompt("Ajouter un collaborateur", "Nom d'utilisateur GitHub de la personne à inviter :", "",
                l => Collaboration.ValidateLogin(l) ?? (taken.Contains(l) ? $"{l} a déjà accès à ce repo." : null));
        if (login is null) return;

        await RunAsync("Ajouter un collaborateur", () => Collaboration.AddAsync(host, Account, FullName, login));
        await LoadCollaboratorsAsync();
        await host.ReloadCollaborationsAsync();
    }

    [RelayCommand]
    private async Task RemoveCollaboratorAsync(CollaboratorRow? row)
    {
        if (row is null || !CanManageCollaborators) return;
        if (row is { IsPending: true, InvitationId: { } id })
        {
            if (!host.Dialogs.Confirm($"Annuler l'invitation de {row.Login} sur {FullName} ?", "Annuler l'invitation")) return;
            await RunAsync("Annuler l'invitation", async () =>
            {
                await GitHub.CancelInvitationAsync(Account, FullName, id);
                return new ActionLog("Annuler l'invitation", true, $"L'invitation de {row.Login} est annulée.",
                    $"gh api --method DELETE repos/{FullName}/invitations/{id}", "");
            });
        }
        else
        {
            if (!host.Dialogs.Confirm(
                    $"Retirer {row.Login} des collaborateurs de {FullName} ?\n\n"
                    + $"{row.Login} ne pourra plus envoyer de commits sur ce repo" + (IsPrivate ? ", ni le voir (il est privé)" : "") + ". "
                    + "Ses copies locales ne sont pas touchées.",
                    "Retirer un collaborateur", destructive: true))
                return;
            await RunAsync("Retirer un collaborateur", async () =>
            {
                await GitHub.RemoveCollaboratorAsync(Account, FullName, row.Login);
                return new ActionLog("Retirer un collaborateur", true, $"{row.Login} n'a plus accès à {FullName}.",
                    $"gh api --method DELETE repos/{FullName}/collaborators/{row.Login}", "");
            });
        }
        await LoadCollaboratorsAsync();
        await host.ReloadCollaborationsAsync();
    }

    /// <summary>Le compte de l'onglet renonce à son accès de collaborateur.</summary>
    [RelayCommand]
    private async Task LeaveAsync()
    {
        if (!CanLeave) return;
        if (!host.Dialogs.Confirm(
                $"Quitter {FullName} avec le compte {Account.Login} ?\n\n"
                + $"{Account.Login} ne pourra plus envoyer de commits sur ce repo" + (IsPrivate ? " ni le voir (il est privé)" : "") + ". "
                + $"Les projets locaux ne sont pas touchés. Seul {OwnerLogin} pourra te redonner accès.",
                "Quitter le repo", destructive: true))
            return;

        var left = await RunAsync("Quitter le repo", async () =>
        {
            await GitHub.RemoveCollaboratorAsync(Account, FullName, Account.Login);
            return new ActionLog("Quitter le repo", true, $"{Account.Login} n'est plus collaborateur de {FullName}.",
                $"gh api --method DELETE repos/{FullName}/collaborators/{Account.Login}   (avec le compte {Account.Login})", "");
        });
        if (left is { Succeeded: true })
        {
            tab.CloseRepositoryPanel();
            tab.Notice = left.Message;
        }
        await host.ReloadCollaborationsAsync();
    }

    private bool IsConnected(string login) =>
        host.Accounts.Any(a => a.IsAuthenticated && a.Host == Account.Host && a.Login.Equals(login, StringComparison.OrdinalIgnoreCase));

    private void RaiseCollaborators()
    {
        OnPropertyChanged(nameof(CollaboratorsLabel));
        OnPropertyChanged(nameof(CanManageCollaborators));
    }
}
