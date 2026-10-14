using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Git;
using GitSpread.Core.Projects;

namespace GitSpread.App.ViewModels;

/// <summary>Une ligne du bloc « Liens » : un repo lié au projet (remote git), son état et la case « envoyer vers ».</summary>
public sealed partial class LinkRowViewModel(ProjectLink link) : ObservableObject
{
    public ProjectLink Link { get; } = link;
    public string Name => Link.Name;
    public string RepositoryLabel => Link.RepositoryLabel;
    public bool IsPrimary => Link.IsPrimary;
    public bool IsSecondary => !Link.IsPrimary;
    public string RoleLabel => IsPrimary ? "principal" : "secondaire";
    public string AccountLabel => Link.Account is { } a ? $"avec le compte {a.Login}" : "aucun compte connecté : configuration git habituelle";

    /// <summary>Coché : « Pousser » envoie aussi vers ce lien.</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string StateLabel { get; set; } = "";
    /// <summary>Le repo contient des commits que le projet n'a pas : un envoi y serait refusé.</summary>
    [ObservableProperty] public partial bool HasExtraCommits { get; set; }

    public override string ToString() => Name;
}

/// <summary>F3B : un projet lié à plusieurs repos (un remote par repo, « origin » = principal).</summary>
public sealed partial class ProjectItemViewModel
{
    public ObservableCollection<LinkRowViewModel> LinkRows { get; } = [];

    public bool HasSecondaryLinks => Placement.HasSecondaryLinks;

    /// <summary>Le projet est affiché dans l'onglet d'un compte uniquement grâce à un lien secondaire.</summary>
    public bool IsViewedThroughLink => ViewingAccount is not null && Placement.HasLinkFor(ViewingAccount)
                                       && !ViewingAccount.Is(Placement.Account) && !Placement.IsCollaborator(ViewingAccount);

    public string LinksBadge => IsViewedThroughLink ? "Lien secondaire"
        : $"+{Placement.SecondaryLinks.Count} lien{(Placement.SecondaryLinks.Count > 1 ? "s" : "")}";
    public string LinksLabel => "Aussi lié à " + string.Join(", ", Placement.SecondaryLinks.Select(l => $"{l.RepositoryLabel} (« {l.Name} »)"))
                                + (Placement.PrimaryLink is { } p ? $". Lien principal : {p.RepositoryLabel}." : ".");

    public IReadOnlyList<LinkRowViewModel> SelectedLinks => LinkRows.Where(l => l.IsSelected).ToList();
    public string PushTargetsLabel => SelectedLinks.Count switch
    {
        0 => "Aucun lien coché : « Pousser » n'enverra rien.",
        1 when SelectedLinks[0].IsPrimary => "« Pousser » envoie vers le lien principal.",
        1 => $"« Pousser » envoie seulement vers « {SelectedLinks[0].Name} ».",
        var n => $"« Pousser » envoie vers {n} repos (une confirmation est demandée).",
    };

    /// <summary>Lignes du bloc « Liens » et leur état, d'après le dernier « Récupérer ». La sélection en cours est conservée.</summary>
    private async Task LoadLinksAsync(string? branch)
    {
        var previous = LinkRows.ToDictionary(l => l.Name, l => l.IsSelected);
        var remembered = Project.PushRemotes.Where(n => Placement.Links.Any(l => l.Name == n)).ToHashSet();
        var rows = new List<LinkRowViewModel>();
        foreach (var link in Placement.Links)
        {
            var row = new LinkRowViewModel(link)
            {
                IsSelected = previous.TryGetValue(link.Name, out var selected) ? selected
                    : remembered.Count > 0 ? remembered.Contains(link.Name) : link.IsPrimary,
            };
            if (branch is not null && Placement.HasSecondaryLinks)
            {
                var state = await Git.CompareWithRemoteAsync(Path, link.Name, branch);
                row.HasExtraCommits = state.Behind > 0 && !link.IsPrimary;
                row.StateLabel = state switch
                {
                    { Exists: false } => $"{branch} n'a encore jamais été envoyée ici",
                    { Ahead: 0, Behind: 0 } => "à jour (au dernier « Récupérer »)",
                    _ => string.Join(" · ", new[]
                    {
                        state.Ahead > 0 ? $"{state.Ahead} commit{Plural(state.Ahead)} à envoyer" : null,
                        state.Behind > 0 ? $"{state.Behind} commit{Plural(state.Behind)} que ton projet n'a pas" : null,
                    }.OfType<string>()),
                };
            }
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LinkRowViewModel.IsSelected)) OnPropertyChanged(nameof(PushTargetsLabel)); };
            rows.Add(row);
        }
        LinkRows.Clear();
        foreach (var r in rows) LinkRows.Add(r);
        OnPropertyChanged(nameof(PushTargetsLabel));
    }

    /// <summary>Envoi vers plusieurs liens (ou un seul lien secondaire) : confirmation dès que plusieurs repos sont cochés.</summary>
    /// <param name="alwaysConfirm">Vrai quand un envoi vers le principal a déjà eu lieu (ex. intégration) : ce sera plusieurs repos au total.</param>
    private async Task<ActionLog> PushToLinksAsync(string branch, IReadOnlyList<LinkRowViewModel> targets, bool setPrimaryUpstream, bool alwaysConfirm = false)
    {
        const string title = "Pousser";
        if ((targets.Count > 1 || alwaysConfirm) && !host.Dialogs.Confirm(
                $"Envoyer la branche {branch} vers {(targets.Count > 1 ? $"{targets.Count} repos" : "ce repo")}"
                + (alwaysConfirm ? ", en plus du lien principal" : "") + " ?\n\n"
                + string.Join("\n", targets.Select(t => $"• {t.RepositoryLabel} (« {t.Name} », {t.AccountLabel})")),
                "Envoyer vers plusieurs repos"))
            return ActionLog.Failure(title, "Envoi annulé : rien n'a été envoyé.");

        var results = await host.Services.Links.PushAsync(Path, branch, targets.Select(t => t.Link).ToList(), setPrimaryUpstream);
        await RememberPushTargetsAsync();
        return new ActionLog(title, results.All(r => r.Succeeded), string.Join("\n", results.Select(r => r.Message)),
            string.Join("\n", results.Select(r => $"{r.Result.Command}   ({(r.Link.Account is { } a ? $"avec le compte {a.Login}" : "configuration git habituelle")})")),
            string.Join("\n", results.Select(r => r.Result.Output).Where(o => o.Length > 0)));
    }

    /// <summary>La sélection « envoyer vers » est mémorisée par projet (le principal seul : rien à mémoriser).</summary>
    private async Task RememberPushTargetsAsync()
    {
        var selected = SelectedLinks;
        await Git.SetPushRemotesAsync(Path, selected is [{ IsPrimary: true }] ? [] : selected.Select(l => l.Name).ToList());
    }

    [RelayCommand]
    private async Task AddLinkAsync() => await OpenAddLinkAsync(null);

    /// <summary>Assistant « Ajouter un lien » : un repo (nouveau ou existant) d'un compte, en plus du lien actuel.</summary>
    public async Task OpenAddLinkAsync(Core.Accounts.GitHubAccount? account)
    {
        await EnsureDetailsAsync();
        var dialog = new RepointViewModel(this, host, account, addLink: true);
        host.Dialogs.ShowRepoint(dialog);
        if (!dialog.Done) return;
        if (dialog.SelectedAccount is { } target) await host.ReloadRepositoriesAsync(target); // repo peut-être créé sur ce compte
        await host.RescanAsync(Path);
    }

    [RelayCommand]
    private async Task MakePrimaryAsync(LinkRowViewModel? row)
    {
        if (row is not { IsSecondary: true } || Placement.PrimaryLink is not { } primary) return;
        var taken = Project.AllRemotes.Select(r => r.Name).Where(n => n != primary.Name);
        var oldName = ProjectLinks.SuggestName(primary.Url.Owner ?? "ancien-principal", taken);
        if (!host.Dialogs.Confirm(
                $"Faire de {row.RepositoryLabel} le lien principal ?\n\n"
                + "C'est depuis le lien principal que « Tirer » récupère les nouveautés, et c'est lui que suit la branche.\n"
                + $"L'actuel principal ({primary.RepositoryLabel}) reste lié, sous le nom « {oldName} ».",
                "Lien principal"))
            return;

        await RunAsync("Lien principal", async () => ActionLog.From("Lien principal",
            await host.Services.Links.MakePrimaryAsync(Path, row.Name, oldName, _status?.Branch),
            $"{row.RepositoryLabel} est maintenant le lien principal (« origin ») ; {primary.RepositoryLabel} devient « {oldName} »."));
        await host.RescanAsync(Path);
    }

    [RelayCommand]
    private async Task RenameLinkAsync(LinkRowViewModel? row)
    {
        if (row is not { IsSecondary: true }) return;
        var taken = Project.AllRemotes.Select(r => r.Name).Where(n => n != row.Name).ToList();
        var name = host.Dialogs.Prompt("Renommer le lien", $"Nouveau nom pour le lien vers {row.RepositoryLabel} :", row.Name,
            n => ProjectLinks.ValidateName(n, taken) ?? (n == row.Name ? "C'est le nom actuel." : null));
        if (name is null) return;

        var selected = SelectedLinks.Select(l => l.Name == row.Name ? name : l.Name).ToList();
        await RunAsync("Renommer le lien", async () =>
        {
            var steps = new List<GitResult> { await Git.RenameRemoteAsync(Path, row.Name, name) };
            if (steps[0].Succeeded && Project.PushRemotes.Count > 0) steps.Add(await Git.SetPushRemotesAsync(Path, selected));
            return ActionLog.From("Renommer le lien", steps, $"Le lien vers {row.RepositoryLabel} s'appelle maintenant « {name} ».");
        });
        await host.RescanAsync(Path);
    }

    [RelayCommand]
    private async Task RemoveLinkAsync(LinkRowViewModel? row)
    {
        if (row is not { IsSecondary: true }) return;
        if (!host.Dialogs.Confirm(
                $"Retirer le lien « {row.Name} » vers {row.RepositoryLabel} ?\n\nLe repo sur GitHub n'est pas touché : seul ce projet ne lui enverra plus rien. Tu pourras le relier de nouveau avec « Ajouter un lien ».",
                "Retirer un lien"))
            return;

        var remaining = SelectedLinks.Where(l => l.Name != row.Name).Select(l => l.Name).ToList();
        await RunAsync("Retirer un lien", async () =>
        {
            var steps = new List<GitResult> { await Git.RemoveRemoteAsync(Path, row.Name) };
            if (steps[0].Succeeded && Project.PushRemotes.Count > 0)
                steps.Add(await Git.SetPushRemotesAsync(Path, remaining is [ProjectLinks.PrimaryName] ? [] : remaining));
            return ActionLog.From("Retirer un lien", steps, $"Le lien vers {row.RepositoryLabel} est retiré (le repo sur GitHub n'a pas changé).");
        });
        await host.RescanAsync(Path);
    }
}
