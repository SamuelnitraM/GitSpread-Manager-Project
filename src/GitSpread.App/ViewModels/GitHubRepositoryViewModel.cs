using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

/// <summary>Panneau d'un repo GitHub (F4) : infos, projets locaux liés, actions de gestion.</summary>
public sealed partial class GitHubRepositoryViewModel(AccountTabViewModel tab, GitHubRepository repository, IProjectHost host, ILogger logger)
    : ObservableObject
{
    private GitHubService GitHub => host.Services.GitHub;
    private GitHubAccount Account => tab.Account;
    internal AccountTabViewModel Tab => tab;

    public string FullName => repository.FullName;
    public string Name => repository.Name;
    public string WebUrl => repository.Url;
    public GitHubRepository Repository => repository;

    /// <summary>Sections repliables du panneau (F6c) : la zone sensible est repliée par défaut.</summary>
    public PanelSections Sections { get; } = new(host);

    [ObservableProperty] public partial GitHubRepositoryDetails? Details { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? LoadError { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string BusyLabel { get; set; } = "";
    [ObservableProperty] public partial ActionLog? LastAction { get; set; }

    public ObservableCollection<string> Topics { get; } = [];
    public ObservableCollection<LocalProject> LinkedProjects { get; } = [];

    public bool HasDetails => Details is not null;
    public bool HasLoadError => LoadError is not null;
    public bool HasLastAction => LastAction is not null;
    public bool HasTopics => Topics.Count > 0;
    public bool HasLinkedProjects => LinkedProjects.Count > 0;
    public bool HasDescription => Details?.Description is not null;
    public bool HasHomepage => Details?.Homepage is not null;
    public bool IsPrivate => Details?.Visibility != RepositoryVisibility.Public;
    public bool IsArchived => Details?.IsArchived == true;
    public bool IsFork => Details?.IsFork == true;

    /// <summary>Renommer, changer la visibilité, archiver, transférer, supprimer : réservé aux administrateurs du repo.</summary>
    public bool CanAdminister => Details?.IsAdmin == true && !IsBusy;
    public bool CanAct => Details is not null && !IsBusy;
    public bool ShowAdminNote => Details is { IsAdmin: false };

    public string VisibilityLabel => Details?.Visibility switch { RepositoryVisibility.Private => "Privé", RepositoryVisibility.Internal => "Interne", _ => "Public" };
    public string ToggleVisibilityLabel => IsPrivate ? "Rendre public…" : "Rendre privé…";
    public string ToggleArchiveLabel => IsArchived ? "Désarchiver" : "Archiver…";
    public string LinkedLabel => LinkedProjects.Count switch
    {
        0 => "Aucun projet local lié à ce repo.",
        1 => "1 projet local lié :",
        var n => $"{n} projets locaux liés :",
    };

    public string FactsLabel => Details is not { } d ? "" : string.Join("  ·  ", new[]
    {
        d.DefaultBranch is { } b ? $"branche principale « {b} »" : "aucun commit",
        d.PushedAt is { } p ? $"dernier envoi {DisplayPreferences.Date(p)}" : null,
        d.CreatedAt is { } c ? $"créé le {c.LocalDateTime:d MMMM yyyy}" : null,
        $"{d.Stars} étoile{(d.Stars > 1 ? "s" : "")}",
        $"{d.Forks} fork{(d.Forks > 1 ? "s" : "")}",
        d.DiskUsageKb > 0 ? FormatSize(d.DiskUsageKb) : null,
        d.Parent is { } parent ? $"fork de {parent}" : null,
    }.OfType<string>());

    partial void OnDetailsChanged(GitHubRepositoryDetails? value) => RaiseAll();
    partial void OnIsBusyChanged(bool value) => RaiseAll();
    partial void OnLoadErrorChanged(string? value) => OnPropertyChanged(nameof(HasLoadError));
    partial void OnLastActionChanged(ActionLog? value) => OnPropertyChanged(nameof(HasLastAction));

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            Details = await GitHub.GetRepositoryDetailsAsync(Account, FullName);
            Topics.Clear();
            foreach (var t in Details.Topics) Topics.Add(t);
            LinkedProjects.Clear();
            foreach (var p in Core.Projects.LinkedProjects.Find(host.LocalProjects, Account.Host, FullName)) LinkedProjects.Add(p);
            RaiseAll();
            await LoadCollaboratorsAsync();
            await LoadBranchesAsync();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Détail de {Repo} indisponible", FullName);
            LoadError = e.Message;
        }
        finally { IsLoading = false; }
    }

    // ------------------------------------------------------------ actions

    [RelayCommand] private void OpenOnGitHub() => Shell.Open(WebUrl);
    [RelayCommand] private void CopyHttpsUrl() => Shell.Copy(WebUrl + ".git");
    [RelayCommand] private void CopySshUrl() => Shell.Copy(Details?.SshUrl ?? repository.SshUrl);

    [RelayCommand]
    private async Task CloneAsync()
    {
        var dialog = new CloneViewModel(Account, repository, host);
        host.Dialogs.ShowClone(dialog);
        if (dialog.ClonedPath is { } path)
        {
            LastAction = new ActionLog("Cloner en local", true, dialog.ResultMessage ?? "", $"git clone {WebUrl}.git", "");
            await host.RescanAsync(path);
        }
    }

    [RelayCommand]
    private async Task EditAsync()
    {
        if (Details is null) return;
        var dialog = new EditRepositoryViewModel(Account, Details, host);
        host.Dialogs.ShowEditRepository(dialog);
        if (dialog.Saved)
        {
            LastAction = new ActionLog("Modifier le repo", true, "Description, site web et sujets mis à jour.", string.Join(' ', dialog.Command), "");
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        var newName = host.Dialogs.Prompt("Renommer le repo",
            $"Nouveau nom pour {FullName}. GitHub redirige l'ancienne adresse, et GitSpread met à jour les projets locaux liés.",
            Name, n => RepositoryName.Validate(n) ?? (n == Name ? "C'est le nom actuel." : null), spacesToDash: true);
        if (newName is null) return;

        var owner = OwnerLogin; // le propriétaire (compte ou organisation), pas forcément le compte de l'onglet
        var log = await RunAsync("Renommer le repo", async () =>
        {
            await GitHub.RenameRepositoryAsync(Account, FullName, newName);
            var results = await FollowLinkedAsync(owner, newName);
            return Log("Renommer le repo", $"gh repo rename {newName} --repo {FullName}", results,
                $"Le repo s'appelle maintenant {owner}/{newName}." + LinkedSummary(results));
        });
        await host.ReloadRepositoriesAsync(Account);
        if (log is { Succeeded: true }) tab.Reselect($"{owner}/{newName}", log);
        await host.RescanAsync();
    }

    [RelayCommand]
    private async Task ToggleVisibilityAsync()
    {
        if (Details is null) return;
        var toPublic = IsPrivate;
        var message = toPublic
            ? $"Rendre {FullName} PUBLIC ?\n\nTout le monde pourra voir le code et TOUT son historique. Vérifie qu'aucun secret (mot de passe, clé d'API, fichier .env…) n'a jamais été commité : même supprimé depuis, il reste lisible dans l'historique."
            : $"Rendre {FullName} PRIVÉ ?\n\nSeuls toi et tes collaborateurs le verrez. GitHub retire les étoiles et les observateurs du repo, et ses forks publics deviennent indépendants.";
        if (!host.Dialogs.Confirm(message, toPublic ? "Rendre public" : "Rendre privé", destructive: true)) return;

        var visibility = toPublic ? RepositoryVisibility.Public : RepositoryVisibility.Private;
        await RunAsync("Visibilité", async () =>
        {
            await GitHub.SetVisibilityAsync(Account, FullName, visibility);
            return new ActionLog("Visibilité", true, toPublic ? "Le repo est maintenant public." : "Le repo est maintenant privé.",
                $"gh repo edit {FullName} --visibility {visibility.ToString().ToLowerInvariant()}", "");
        });
        await LoadAsync();
        await host.ReloadRepositoriesAsync(Account);
    }

    [RelayCommand]
    private async Task ToggleArchiveAsync()
    {
        var archive = !IsArchived;
        if (archive && !host.Dialogs.Confirm(
                $"Archiver {FullName} ?\n\nLe repo passe en lecture seule : plus aucun envoi, issue ou pull request possible. Tu pourras le désarchiver à tout moment.",
                "Archiver"))
            return;
        var archived = await RunAsync(archive ? "Archiver" : "Désarchiver", async () =>
        {
            await GitHub.SetArchivedAsync(Account, FullName, archive);
            return new ActionLog(archive ? "Archiver" : "Désarchiver", true,
                archive ? "Repo archivé : il est en lecture seule." : "Repo désarchivé : les envois sont de nouveau possibles.",
                $"gh repo {(archive ? "archive" : "unarchive")} {FullName} --yes", "");
        });
        // Le repo qu'on vient d'archiver doit rester visible dans la liste.
        if (archive && archived is { Succeeded: true }) tab.ShowArchived = true;
        await LoadAsync();
        await host.ReloadRepositoriesAsync(Account);
    }

    [RelayCommand]
    private async Task RenameDefaultBranchAsync()
    {
        if (Details?.DefaultBranch is not { } current) return;
        var target = host.Dialogs.Prompt("Renommer la branche principale",
            $"Nouveau nom pour « {current} » sur {FullName}. GitHub met à jour la branche par défaut et les pull requests ; GitSpread met à jour les projets locaux liés.",
            current == "master" ? "main" : current,
            n => BranchName.Validate(n) ?? (n == current ? "C'est le nom actuel." : null), spacesToDash: true);
        if (target is null) return;

        await RunAsync("Renommer la branche principale", async () =>
        {
            await GitHub.RenameBranchAsync(Account, FullName, current, target);
            var results = new List<GitResult>();
            foreach (var project in LinkedProjects)
                results.AddRange(await host.Services.Linked.FollowBranchRenameAsync(project, Account, current, target, FullName));
            return Log("Renommer la branche principale", $"gh api --method POST repos/{FullName}/branches/{current}/rename -f new_name={target}", results,
                $"La branche principale s'appelle maintenant « {target} »." + LinkedSummary(results));
        });
        await LoadAsync();
        await host.RescanAsync();
    }

    [RelayCommand]
    private async Task TransferAsync()
    {
        var targets = host.Accounts.Where(a => a.IsAuthenticated && a.Host == Account.Host && a.Login != Account.Login).ToList();
        if (targets.Count == 0)
        {
            LastAction = ActionLog.Failure("Transférer", "Aucun autre compte connecté : ajoute-en un avec « + » pour lui transférer ce repo.");
            return;
        }
        var choice = host.Dialogs.Choose("Transférer le repo", $"À quel compte transférer {FullName} ? Issues, étoiles et historique sont conservés.",
            targets.Select(t => t.Login).ToList());
        if (choice < 0) return;
        var to = targets[choice];
        if (!host.Dialogs.Confirm($"Transférer {FullName} à {to.Login} ?\n\n{to.Login} doit accepter la demande reçue par e-mail (valable 24 h). Les projets locaux liés pointeront vers {to.Login}/{Name}.",
                "Transférer", destructive: true))
            return;

        await RunAsync("Transférer", async () =>
        {
            await GitHub.TransferRepositoryAsync(Account, FullName, to.Login);
            var results = await FollowLinkedAsync(to.Login, Name);
            return Log("Transférer", $"gh api --method POST repos/{FullName}/transfer -f new_owner={to.Login}", results,
                $"Transfert demandé : {to.Login} doit l'accepter depuis l'e-mail de GitHub." + LinkedSummary(results));
        });
        await host.ReloadRepositoriesAsync(Account);
        await host.RescanAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!host.Accounts.Any(a => a.Is(Account)))
        {
            LastAction = ActionLog.Failure("Supprimer le repo", $"Le compte {Account.Login} n'est plus connecté : rien n'a été supprimé.");
            return;
        }
        if (!await host.EnsureScopeAsync(Account, ScopeCatalog.DeleteRepo,
                $"Pour supprimer {FullName}, GitSpread a besoin de l'autorisation « Suppression de repos » sur {Account.Login}."))
        {
            LastAction = ActionLog.Failure("Supprimer le repo",
                $"Rien n'a été supprimé : {Account.Login} n'a pas l'autorisation « Suppression de repos ». Elle s'accorde dans la fenêtre "
                + "qui vient de s'ouvrir (validation dans le navigateur, avec le compte " + Account.Login + ").");
            return;
        }
        if (host.Accounts.FirstOrDefault(a => a.Is(Account)) is not { } account) // autorisations à jour
        {
            LastAction = ActionLog.Failure("Supprimer le repo", $"Le compte {Account.Login} n'est plus connecté : rien n'a été supprimé.");
            return;
        }

        var typed = host.Dialogs.Prompt("Supprimer définitivement",
            $"Cette action est IRRÉVERSIBLE : le repo, ses issues, ses pull requests, ses étoiles et son wiki seront supprimés de GitHub.\n"
            + (LinkedProjects.Count > 0 ? $"Les fichiers des {LinkedProjects.Count} projet(s) local(aux) lié(s) ne sont pas touchés.\n" : "")
            + $"\nPour confirmer, tape le nom complet du repo : {FullName}",
            "", t => t == FullName ? null : $"Tape exactement {FullName}.");
        if (typed is null) return;

        var deleted = await RunAsync("Supprimer le repo", async () =>
        {
            await GitHub.DeleteRepositoryAsync(account, FullName);
            var results = new List<GitResult>();
            if (LinkedProjects.Count > 0 && host.Dialogs.Confirm(
                    $"{FullName} est supprimé.\n\nRetirer aussi le lien vers ce repo des {LinkedProjects.Count} projet(s) local(aux) ? Leurs fichiers et leur historique ne changent pas.",
                    "Liens locaux"))
                foreach (var p in LinkedProjects) results.Add(await host.Services.Linked.UnlinkAsync(p, FullName, host: Account.Host));
            return Log("Supprimer le repo", $"gh repo delete {FullName} --yes", results, $"{FullName} a été supprimé de GitHub." + LinkedSummary(results));
        });
        if (deleted is { Succeeded: true })
        {
            tab.CloseRepositoryPanel();
            tab.Notice = deleted.Message; // le panneau se ferme : le résultat reste affiché en haut de la liste
        }
        await host.ReloadRepositoriesAsync(account);
        await host.RescanAsync();
    }

    // ------------------------------------------------------------ outils

    private async Task<List<GitResult>> FollowLinkedAsync(string newOwner, string newName)
    {
        var results = new List<GitResult>();
        foreach (var project in LinkedProjects) results.Add(await host.Services.Linked.FollowAsync(project, newOwner, newName, FullName, host: Account.Host));
        return results;
    }

    private static string LinkedSummary(IReadOnlyCollection<GitResult> results) => results.Count == 0 ? ""
        : results.All(r => r.Succeeded) ? " Projets locaux liés mis à jour."
        : " Attention : certains projets locaux n'ont pas pu être mis à jour (voir la sortie).";

    private static ActionLog Log(string title, string githubCommand, IReadOnlyList<GitResult> localSteps, string message) =>
        new(title, localSteps.All(r => r.Succeeded), message,
            string.Join("\n", localSteps.Select(r => r.Command).Prepend(githubCommand)),
            string.Join("\n", localSteps.Select(r => r.Output).Where(o => o.Length > 0)));

    /// <summary>Lance une action et renvoie son compte rendu (null si une autre action était déjà en cours :
    /// l'appelant ne doit pas confondre avec le compte rendu précédent).</summary>
    private async Task<ActionLog?> RunAsync(string title, Func<Task<ActionLog>> action)
    {
        if (IsBusy) return null;
        IsBusy = true;
        BusyLabel = $"{title} en cours…";
        try { LastAction = await action(); }
        catch (Exception e)
        {
            logger.LogError(e, "{Action} sur {Repo} impossible", title, FullName);
            LastAction = ActionLog.Failure(title, e.Message);
        }
        finally { IsBusy = false; }
        return LastAction;
    }

    private void RaiseAll()
    {
        foreach (var p in new[] { nameof(HasDetails), nameof(HasDescription), nameof(HasHomepage), nameof(IsPrivate), nameof(IsArchived), nameof(IsFork),
                     nameof(CanAdminister), nameof(CanAct), nameof(ShowAdminNote), nameof(VisibilityLabel), nameof(ToggleVisibilityLabel),
                     nameof(ToggleArchiveLabel), nameof(LinkedLabel), nameof(FactsLabel), nameof(HasTopics), nameof(HasLinkedProjects),
                     nameof(IsViewerCollaborator), nameof(CanLeave), nameof(CanManageCollaborators), nameof(OwnerLabel),
                     nameof(CanEditBranches) })
            OnPropertyChanged(p);
    }

    private static string FormatSize(long kb) => kb switch
    {
        < 1024 => $"{kb} Ko",
        < 1024 * 1024 => $"{kb / 1024.0:0.#} Mo",
        _ => $"{kb / 1024.0 / 1024.0:0.#} Go",
    };
}
