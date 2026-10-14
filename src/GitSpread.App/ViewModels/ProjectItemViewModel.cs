using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.Projects;

namespace GitSpread.App.ViewModels;

/// <summary>Un projet local : résumé pour la liste, état git chargé à la sélection, et actions git en un clic.</summary>
public sealed partial class ProjectItemViewModel(ProjectPlacement placement, IProjectHost host) : ObservableObject
{
    private GitStatus? _status;

    public ProjectPlacement Placement { get; } = placement;
    public LocalProject Project => Placement.Project;
    private GitService Git => host.Services.Git;

    public string Name => Project.Name;
    public string Path => Project.Path;
    public RemoteUrl? Remote => Project.Remote;
    public bool HasRemote => Remote is not null;
    public bool IsRepository => Project.IsRepository;
    public bool IsExtra => Project.IsExtra;

    public string RemoteLabel => Remote switch
    {
        null => IsRepository ? "Aucun remote" : "Pas encore suivi par git",
        { FullName: { } full, IsGitHub: true } => full,
        { FullName: { } full } => $"{Remote.Host}/{full}",
        _ => Remote.Url,
    };

    public string ProtocolLabel => Remote?.Protocol switch
    {
        RemoteProtocol.Https => "HTTPS",
        RemoteProtocol.Ssh => Remote.SshAlias is { } alias ? $"SSH · {alias}" : "SSH",
        RemoteProtocol.Git => "git://",
        RemoteProtocol.File => "local",
        _ => "",
    };

    public bool HasProtocol => ProtocolLabel.Length > 0;

    public string? UnlinkedReasonLabel => Placement.Reason switch
    {
        UnlinkedReason.NotARepository => "Pas encore suivi par git",
        UnlinkedReason.NoRemote => "Projet local, sans remote",
        UnlinkedReason.OtherHost => Remote?.Host is { Length: > 0 } h ? $"Hébergé sur {h}" : "Remote local ou non reconnu",
        UnlinkedReason.OtherOwner => $"Appartient à « {Remote?.Owner} » (organisation ou compte non connecté)",
        _ => null,
    };

    public bool IsUnlinked => Placement.Reason is not null;

    public string? GitHubWebUrl => Remote is { IsGitHub: true, FullName: { } full } ? $"https://{Remote.Host}/{full}" : null;
    public bool CanOpenOnGitHub => GitHubWebUrl is not null;

    public string RemoteDetail => Project.RemoteName is { } name
        ? $"{name} → {Remote!.Url}" + (Project.RemoteCount > 1 ? $"  (+{Project.RemoteCount - 1} autre{(Project.RemoteCount > 2 ? "s" : "")})" : "")
        : "";

    // ------------------------------------------------------------ actions disponibles selon l'état

    public bool CanInit => !IsRepository;
    public bool CanPublish => !HasRemote;
    public bool CanCommit => IsRepository;
    public bool CanSync => IsRepository && HasRemote;
    public bool IsLocalRepository => IsRepository && !HasRemote;

    /// <summary>Compte utilisé pour les échanges avec GitHub, dit en clair.</summary>
    public string NetworkAccountLabel => (Placement.NetworkAccount, Remote?.Protocol) switch
    {
        (_, RemoteProtocol.Ssh) => "Échanges via SSH, avec tes clés SSH.",
        ({ } account, _) when Placement.IsCollaborator(account) => $"Échanges avec GitHub en tant que {account.Login} (collaborateur).",
        ({ } account, _) => $"Échanges avec GitHub en tant que {account.Login}.",
        _ => "Aucun compte connecté ne correspond : git utilisera ta configuration habituelle.",
    };

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string BusyLabel { get; set; } = "";
    [ObservableProperty] public partial ActionLog? LastAction { get; set; }
    public bool HasLastAction => LastAction is not null;
    partial void OnLastActionChanged(ActionLog? value) => OnPropertyChanged(nameof(HasLastAction));

    [ObservableProperty] public partial string PushLabel { get; set; } = "Pousser";
    [ObservableProperty] public partial string PullLabel { get; set; } = "Tirer";

    // ------------------------------------------------------------ détails (chargés à la demande)

    [ObservableProperty] public partial bool IsLoadingDetails { get; set; }
    [ObservableProperty] public partial bool HasDetails { get; set; }
    [ObservableProperty] public partial string? DetailsError { get; set; }
    [ObservableProperty] public partial string BranchLabel { get; set; } = "";
    [ObservableProperty] public partial string TrackingLabel { get; set; } = "";
    [ObservableProperty] public partial string StatusLabel { get; set; } = "";
    [ObservableProperty] public partial bool IsClean { get; set; }
    [ObservableProperty] public partial string LastCommitSubject { get; set; } = "";
    [ObservableProperty] public partial string LastCommitMeta { get; set; } = "";
    [ObservableProperty] public partial string IdentityLabel { get; set; } = "";

    public bool HasDetailsError => DetailsError is not null;

    /// <summary>Sections repliables du panneau (F6c), état mémorisé.</summary>
    public PanelSections Sections { get; } = new(host);

    /// <summary>L'état du projet vient d'être relu (après la sélection ou une action) : l'aperçu de l'historique suit.</summary>
    public event EventHandler? DetailsLoaded;
    partial void OnDetailsErrorChanged(string? value) => OnPropertyChanged(nameof(HasDetailsError));

    public async Task LoadDetailsAsync()
    {
        if (IsLoadingDetails || !IsRepository) return;
        IsLoadingDetails = true;
        DetailsError = null;
        try
        {
            var statusTask = Git.GetStatusAsync(Path);
            var commitTask = Git.GetLastCommitAsync(Path);
            var identityTask = Git.GetIdentityAsync(Path);
            var status = _status = await statusTask;
            var commit = await commitTask;
            var identity = await identityTask;

            BranchLabel = status.IsDetached ? "HEAD détachée (aucune branche)" : status.Branch ?? "?";
            TrackingLabel = status switch
            {
                { HasCommits: false } => "Aucun commit pour l'instant",
                { Upstream: null } => HasRemote ? "Branche jamais poussée sur GitHub" : "Pas encore publiée",
                { Ahead: 0, Behind: 0 } => $"À jour avec {status.Upstream} (au dernier « Récupérer »)",
                _ => string.Join(" · ", new[]
                {
                    status.Ahead > 0 ? $"{status.Ahead} commit{Plural(status.Ahead)} à pousser" : null,
                    status.Behind > 0 ? $"{status.Behind} commit{Plural(status.Behind)} à récupérer" : null,
                }.OfType<string>()) + $" ({status.Upstream})",
            };
            PushLabel = status.Ahead > 0 ? $"Pousser ({status.Ahead})" : "Pousser";
            PullLabel = status.Behind > 0 ? $"Tirer ({status.Behind})" : "Tirer";

            IsClean = status.IsClean;
            StatusLabel = status.IsClean
                ? "Propre : aucune modification"
                : string.Join(" · ", new[]
                {
                    status.Conflicts > 0 ? $"{status.Conflicts} en conflit" : null,
                    status.Staged > 0 ? $"{status.Staged} prêt{Plural(status.Staged)} à commiter" : null,
                    status.Modified > 0 ? $"{status.Modified} modifié{Plural(status.Modified)}" : null,
                    status.Untracked > 0 ? $"{status.Untracked} nouveau{(status.Untracked > 1 ? "x" : "")} fichier{Plural(status.Untracked)}" : null,
                }.OfType<string>());

            LastCommitSubject = commit?.Subject ?? "Aucun commit pour l'instant";
            LastCommitMeta = commit is null ? "" : $"{commit.ShortHash} · {commit.Author} · {RepositoryItemViewModel.Relative(commit.Date)}";

            IdentityLabel = identity switch
            {
                { Name: null, Email: null } => "Non configurée (git refusera de commiter)",
                _ => $"{identity.Name ?? "(sans nom)"} <{identity.Email ?? "sans e-mail"}>",
            };
            await LoadBranchStateAsync();
            await LoadLinksAsync(status.IsDetached ? null : status.Branch);
            HasDetails = true;
            DetailsLoaded?.Invoke(this, EventArgs.Empty);
            _ = LoadIdentitySafeAsync(); // profils GitHub : chargés sans retarder l'affichage du panneau
        }
        catch (Exception e)
        {
            DetailsError = e.Message;
            HasDetails = false;
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    // ------------------------------------------------------------ actions git

    [RelayCommand]
    private async Task InitAsync()
    {
        await RunAsync("Initialiser git", async () =>
            ActionLog.From("Initialiser git", [await Git.InitAsync(Path)], "Le dossier est maintenant suivi par git. Prochaine étape : un premier commit, ou « Publier sur GitHub »."));
        await host.RescanAsync(Path);
    }

    [RelayCommand]
    private Task FetchAsync() => Placement.HasSecondaryLinks
        ? RunAsync("Récupérer", async () =>
        {
            // Plusieurs liens (F3B) : chacun est interrogé avec son propre compte.
            var results = await host.Services.Links.FetchAsync(Path, Placement.Links);
            return new ActionLog("Récupérer", results.All(r => r.Succeeded),
                "Infos à jour pour chaque lien :\n" + string.Join("\n", results.Select(r => r.Message)),
                string.Join("\n", results.Select(r => r.Result.Command)),
                string.Join("\n", results.Select(r => r.Result.Output).Where(o => o.Length > 0)));
        })
        : RunNetworkAsync("Récupérer", async auth =>
        {
            var result = await Git.FetchAsync(Path, Project.RemoteName ?? "origin", auth);
            return ActionLog.From("Récupérer", [result], "Infos à jour : GitSpread sait maintenant ce qui a changé sur GitHub.");
        });

    [RelayCommand]
    private Task PullAsync() => RunNetworkAsync("Tirer", async auth =>
    {
        if (_status is { Upstream: null })
            return ActionLog.Failure("Tirer", "Cette branche n'a encore jamais été poussée : il n'y a rien à récupérer.");
        var result = await Git.PullAsync(Path, auth);
        return ActionLog.From("Tirer", [result], result.Output.Contains("Already up to date")
            ? "Déjà à jour : rien de nouveau sur GitHub."
            : "Projet mis à jour avec les commits de GitHub.");
    });

    [RelayCommand]
    private Task PushAsync() => RunNetworkAsync("Pousser", async auth =>
    {
        _status ??= await Git.GetStatusAsync(Path);
        if (_status is not { Branch: { } branch })
            return ActionLog.Failure("Pousser", "Aucune branche active (HEAD détachée) : impossible de savoir quoi pousser.");
        if (!_status.HasCommits)
            return ActionLog.Failure("Pousser", "Il n'y a encore aucun commit à envoyer. Fais d'abord un commit.");

        // Plusieurs liens (F3B) : envoi vers les liens cochés, chacun avec son compte.
        if (Placement.HasSecondaryLinks)
        {
            var targets = SelectedLinks;
            if (targets.Count == 0) return ActionLog.Failure("Pousser", "Aucun lien coché dans « Envoyer vers » : coche au moins un repo.");
            if (targets is not [{ IsPrimary: true }])
                return await PushToLinksAsync(branch, targets, setPrimaryUpstream: _status.Upstream is null);
            await RememberPushTargetsAsync();
        }

        var result = await Git.PushAsync(Path, Project.RemoteName ?? "origin", branch, setUpstream: _status.Upstream is null, auth);
        return ActionLog.From("Pousser", [result], result.Output.Contains("Everything up-to-date")
            ? "Rien de nouveau à envoyer."
            : $"Branche {branch} envoyée sur GitHub.");
    });

    [RelayCommand]
    private async Task CommitAsync()
    {
        var dialog = new CommitViewModel(this, host);
        await dialog.LoadAsync();
        if (!host.Dialogs.ShowCommit(dialog)) return;

        await RunAsync("Commit", async () =>
        {
            var paths = dialog.AllSelected ? null : dialog.SelectedPathspecs;
            var results = await Git.CommitAsync(Path, dialog.FinalMessage, paths);
            return ActionLog.From("Commit", results, $"Commit « {dialog.Message.Trim().Split('\n')[0]} » créé.");
        });
        if (dialog.PushAfterCommit && LastAction is { Succeeded: true } && CanSync) await PushAsync();
    }

    [RelayCommand]
    private async Task PublishAsync()
    {
        var dialog = new PublishViewModel(this, host); // compte proposé : celui où le projet est rangé
        host.Dialogs.ShowPublish(dialog);
        if (dialog.Published) await host.RescanAsync(Path);
    }

    // ------------------------------------------------------------ projet local rangé dans un compte

    /// <summary>Projet sans remote glissé sur l'onglet d'un compte : rangé là, mais pas encore sur GitHub.</summary>
    public bool IsAssigned => Placement.IsAssigned;

    /// <summary>Dans « Non liés », un projet sans remote peut être glissé sur l'onglet d'un compte.</summary>
    public bool ShowAssignHint => IsUnlinked && !HasRemote;
    public string AssignedLabel => Placement is { IsAssigned: true, Account: { } account }
        ? $"Projet local rangé dans {account.Login} : il n'est pas encore sur GitHub. « Publier sur GitHub » créera son repo sur ce compte."
        : "";

    /// <summary>Range ce projet (sans remote) dans les projets locaux d'un compte, ou le remet dans « Non liés » (null).</summary>
    public Task AssignToAsync(GitHubAccount? account) => host.AssignProjectAsync(Path, account);

    [RelayCommand]
    private Task Unassign() => AssignToAsync(null);

    [RelayCommand]
    private async Task RemoveFromListAsync()
    {
        if (host.Dialogs.Confirm($"Retirer « {Name} » de GitSpread ?\n\nLe dossier et ses fichiers ne sont pas touchés.", "Retirer de la liste"))
            await host.RemoveExtraProjectAsync(Path);
    }

    [RelayCommand] private void OpenFolder() => Shell.ShowInExplorer(Path);
    [RelayCommand] private void CopyPath() => Shell.Copy(Path);
    [RelayCommand] private void OpenOnGitHub() { if (GitHubWebUrl is { } url) Shell.Open(url); }
    [RelayCommand] private Task Reload() => LoadDetailsAsync();

    private Task RunNetworkAsync(string title, Func<IReadOnlyDictionary<string, string>, Task<ActionLog>> action) =>
        RunAsync(title, async () =>
            await action(await host.Services.Credentials.EnvironmentForAsync(Placement.NetworkAccount, Remote)));

    private async Task RunAsync(string title, Func<Task<ActionLog>> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyLabel = $"{title} en cours…";
        try
        {
            LastAction = await action();
        }
        catch (Exception e)
        {
            LastAction = ActionLog.Failure(title, e.Message);
        }
        finally
        {
            IsBusy = false;
            await LoadDetailsAsync();
        }
    }

    /// <summary>Nom annoncé par les lecteurs d'écran et l'automatisation de l'interface.</summary>
    public override string ToString() => Name;

    public bool Matches(string filter) =>
        filter.Length == 0
        || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || RemoteLabel.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Path.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string Plural(int n) => n > 1 ? "s" : "";
}
