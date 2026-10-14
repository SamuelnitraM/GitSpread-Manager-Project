using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;

namespace GitSpread.App.ViewModels;

public enum IdentityChoiceKind { AccountNoReply, AccountEmail, Machine, Current, Custom }

/// <summary>Entrée du menu « Commits signés par ».</summary>
public sealed record IdentityChoice(IdentityChoiceKind Kind, string Label, GitIdentity? Identity = null, GitHubAccount? Account = null)
{
    public override string ToString() => Label;
}

public sealed partial class ProjectItemViewModel
{
    private bool _syncingIdentity;
    private IdentitySettings? _identity;

    public ObservableCollection<IdentityChoice> IdentityChoices { get; } = [];

    [ObservableProperty] public partial IdentityChoice? SelectedIdentityChoice { get; set; }

    /// <summary>Phrase d'état : à qui les commits seront attribués sur GitHub.</summary>
    [ObservableProperty] public partial string IdentityStatus { get; set; } = "";
    [ObservableProperty] public partial bool IdentityMatches { get; set; }
    [ObservableProperty] public partial bool IdentityMismatch { get; set; }

    /// <summary>Commits pas encore poussés signés par quelqu'un d'autre que l'identité actuelle du projet.</summary>
    [ObservableProperty] public partial int ForeignUnpushedCount { get; set; }
    public bool HasForeignUnpushed => ForeignUnpushedCount > 0;
    public string FixAuthorsLabel => $"Re-signer {ForeignUnpushedCount} commit{(ForeignUnpushedCount > 1 ? "s" : "")} pas encore envoyé{(ForeignUnpushedCount > 1 ? "s" : "")}";
    partial void OnForeignUnpushedCountChanged(int value) { OnPropertyChanged(nameof(HasForeignUnpushed)); OnPropertyChanged(nameof(FixAuthorsLabel)); }

    partial void OnSelectedIdentityChoiceChanged(IdentityChoice? value)
    {
        if (_syncingIdentity || value is null) return;
        _ = ApplyIdentityChoiceAsync(value);
    }

    private int _identityVersion;

    /// <summary>Construit le menu des identités et évalue à qui les commits seront attribués.
    /// Plusieurs chargements peuvent se chevaucher : seul le plus récent met à jour l'affichage.</summary>
    internal async Task LoadIdentityAsync()
    {
        if (!IsRepository) return;
        var version = ++_identityVersion;

        // 1. Collecte (git + profils GitHub en cache)
        var settings = await Git.GetIdentitySettingsAsync(Path);
        var effective = settings.Effective;
        var users = new Dictionary<GitHubAccount, GitHubUser?>();
        foreach (var account in host.Accounts.Where(a => a.IsAuthenticated))
            users[account] = await host.GetAccountUserAsync(account);
        var foreign = effective.Email is null ? 0
            : (await Git.ListUnpushedCommitsAsync(Path)).Count(c => !c.AuthorEmail.Equals(effective.Email, StringComparison.OrdinalIgnoreCase));
        if (version != _identityVersion) return; // un chargement plus récent a pris le relais

        // 2. Menu : une entrée par compte connecté (adresse privée GitHub, puis e-mails réels connus) ; l'adresse choisie
        //    dans les paramètres est marquée « recommandé »
        var choices = new List<IdentityChoice>();
        foreach (var (account, user) in users)
        {
            if (user is null) continue;
            var name = user.Name ?? user.Login;
            var preferred = host.Settings.SigningEmail(account.Host, account.Login, user.NoReplyEmail, user.Email);
            string Mark(string email) => email.Equals(preferred, StringComparison.OrdinalIgnoreCase) ? " (recommandé)" : "";
            choices.Add(new(IdentityChoiceKind.AccountNoReply, $"{account.Login} · adresse privée GitHub{Mark(user.NoReplyEmail)}", new(name, user.NoReplyEmail), account));
            foreach (var email in KnownEmails(account, user))
                choices.Add(new(IdentityChoiceKind.AccountEmail, $"{account.Login} · {email}{Mark(email)}", new(name, email), account));
        }
        if (settings.Global is { Email: not null } global)
            choices.Add(new(IdentityChoiceKind.Machine, $"Identité du poste · {Describe(global)}", global));
        if (settings.HasLocal && !choices.Any(c => c.Kind != IdentityChoiceKind.Machine && SameIdentity(c.Identity, effective)))
            choices.Insert(0, new(IdentityChoiceKind.Current, $"Actuelle · {Describe(effective)}", effective));
        choices.Add(new(IdentityChoiceKind.Custom, "Personnalisée…"));

        _identity = settings;
        _syncingIdentity = true;
        try
        {
            IdentityChoices.Clear();
            foreach (var c in choices) IdentityChoices.Add(c);
            SelectedIdentityChoice = settings.HasLocal
                ? IdentityChoices.FirstOrDefault(c => c.Kind is IdentityChoiceKind.Current)
                  ?? IdentityChoices.FirstOrDefault(c => c.Kind != IdentityChoiceKind.Machine && SameIdentity(c.Identity, effective))
                : IdentityChoices.FirstOrDefault(c => c.Kind == IdentityChoiceKind.Machine);
        }
        finally { _syncingIdentity = false; }

        // 3. À qui GitHub attribuera ces commits ? Tout compte ayant accès au repo convient (propriétaire ou collaborateur, F3A).
        var access = Placement.AccessAccounts
            .Select(a => (Account: a, User: users.FirstOrDefault(u => u.Key.Is(a)).Value))
            .Where(x => x.User is not null).ToList();
        var reference = Placement.NetworkAccount;
        var source = settings.HasLocal ? "réglage de ce projet" : "réglage global du poste";
        if (effective.Email is null)
        {
            IdentityStatus = "Aucune identité : git refusera de commiter. Choisis-en une ci-dessus.";
            (IdentityMatches, IdentityMismatch) = (false, true);
        }
        else if (reference is null || access.Count == 0)
        {
            IdentityStatus = $"Commits signés {Describe(effective)} ({source}).";
            (IdentityMatches, IdentityMismatch) = (false, false);
        }
        else if (access.FirstOrDefault(x => BelongsTo(effective.Email, x.Account, x.User!)) is { Account: { } match })
        {
            IdentityStatus = $"Commits attribués à {match.Login} sur GitHub ({source}).";
            (IdentityMatches, IdentityMismatch) = (true, false);
        }
        else
        {
            var role = reference.Is(Placement.Account) ? "propriétaire du repo" : "compte qui envoie ce projet";
            IdentityStatus = $"Ces commits ne seront pas attribués à {reference.Login}, {role} : {effective.Email} n'est pas une adresse connue de ce compte ({source}).";
            (IdentityMatches, IdentityMismatch) = (false, true);
        }
        ForeignUnpushedCount = foreign;
    }

    private async Task LoadIdentitySafeAsync()
    {
        try { await LoadIdentityAsync(); }
        catch (Exception e) { IdentityStatus = $"Identité illisible : {e.Message}"; }
    }

    private IEnumerable<string> KnownEmails(GitHubAccount account, GitHubUser user)
    {
        var emails = new List<string>();
        if (user.Email is { Length: > 0 } pub) emails.Add(pub);
        if (host.Settings.AccountEmails.TryGetValue(Core.Settings.AppSettings.AccountKey(account.Host, account.Login), out var known))
            emails.AddRange(known);
        return emails.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private bool BelongsTo(string email, GitHubAccount account, GitHubUser user) =>
        user.OwnsEmail(email) || host.Settings.IsKnownEmail(account.Host, account.Login, email);

    private async Task ApplyIdentityChoiceAsync(IdentityChoice choice)
    {
        GitIdentity? identity = choice.Kind switch
        {
            IdentityChoiceKind.Custom => AskCustomIdentity(),
            IdentityChoiceKind.Machine => null,
            _ => choice.Identity,
        };
        if (choice.Kind == IdentityChoiceKind.Custom && identity is null)
        {
            await LoadIdentityAsync(); // annulé : on revient au choix actuel
            return;
        }

        await RunAsync("Identité des commits", async () =>
        {
            if (identity is null)
                return ActionLog.From("Identité des commits", await Git.UnsetLocalIdentityAsync(Path),
                    "Ce projet utilise de nouveau l'identité globale du poste.");
            return ActionLog.From("Identité des commits", await Git.SetLocalIdentityAsync(Path, identity.Name!, identity.Email!),
                $"Les prochains commits de ce projet seront signés {Describe(identity)}.");
        });

        // Des commits pas encore envoyés portent une autre signature : on propose de les corriger.
        await LoadIdentityAsync();
        var n = ForeignUnpushedCount;
        if (n > 0 && host.Dialogs.Confirm(
                (n == 1 ? "1 commit pas encore envoyé est signé" : $"{n} commits pas encore envoyés sont signés") + " par une autre identité.\n\n"
                + $"{(n == 1 ? "Le" : "Les")} re-signer avec {Describe(_identity!.Effective)} ? Contenu et date ne changent pas, et rien de ce qui est déjà sur GitHub n'est modifié.",
                "Re-signer les commits non envoyés"))
            await FixUnpushedAuthorsAsync();
    }

    [RelayCommand]
    private async Task FixUnpushedAuthorsAsync()
    {
        if (_identity?.Effective is not { Name: not null, Email: not null } author) return;
        var count = ForeignUnpushedCount;
        await RunAsync("Re-signer les commits", async () =>
        {
            var results = await Git.RewriteUnpushedAuthorAsync(Path, author);
            return ActionLog.From("Re-signer les commits", results,
                (count == 1 ? "1 commit pas encore envoyé a été re-signé" : $"{count} commits pas encore envoyés ont été re-signés")
                + $" {Describe(author)}. Contenu et dates inchangés.");
        });
        await LoadIdentityAsync();
    }

    private GitIdentity? AskCustomIdentity()
    {
        var name = host.Dialogs.Prompt("Identité personnalisée", "Nom affiché dans l'historique des commits :",
            _identity?.Effective.Name ?? "", n => n.Length == 0 ? "Indique un nom." : null);
        if (name is null) return null;
        var email = host.Dialogs.Prompt("Identité personnalisée",
            "E-mail des commits. Pour que GitHub t'attribue les commits, utilise une adresse de ton compte (ou son adresse privée noreply).",
            _identity?.Effective.Email ?? "", e => EmailPattern().IsMatch(e) ? null : "Adresse e-mail invalide (ex. prenom@exemple.fr).");
        return email is null ? null : new GitIdentity(name, email);
    }

    private static string Describe(GitIdentity i) => $"{i.Name ?? "(sans nom)"} <{i.Email ?? "sans e-mail"}>";

    private static bool SameIdentity(GitIdentity? a, GitIdentity b) =>
        a is not null && string.Equals(a.Email, b.Email, StringComparison.OrdinalIgnoreCase) && a.Name == b.Name;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
