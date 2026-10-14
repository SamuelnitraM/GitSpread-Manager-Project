using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;

namespace GitSpread.App.ViewModels;

/// <summary>
/// Vérifie qui signe les commits d'un projet et propose l'identité du compte GitHub
/// (le risque n°1 du multi-comptes : commiter avec la mauvaise identité).
/// </summary>
public sealed partial class IdentityViewModel(string path, IProjectHost host) : ObservableObject
{
    private AppServices Services => host.Services;
    private (GitHubAccount Account, string Email)? _unrecognised;
    private int _version;

    /// <summary>L'utilisateur peut confirmer qu'une adresse non publique appartient bien au compte.</summary>
    public bool CanConfirmEmail => _unrecognised is not null;

    [ObservableProperty] public partial string Label { get; set; } = "…";
    [ObservableProperty] public partial string? Warning { get; set; }
    [ObservableProperty] public partial GitIdentity? Suggested { get; set; }

    public bool HasWarning => Warning is not null;
    public bool CanUseSuggested => Suggested is not null;
    public string SuggestedLabel => Suggested is { } s ? $"{s.Name} <{s.Email}>" : "";

    partial void OnWarningChanged(string? value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnSuggestedChanged(GitIdentity? value) { OnPropertyChanged(nameof(CanUseSuggested)); OnPropertyChanged(nameof(SuggestedLabel)); }

    /// <param name="isRepository">Faux : le dossier n'a pas encore de .git, seule la config globale compte.</param>
    public async Task LoadAsync(GitHubAccount? account, bool isRepository = true)
    {
        // Changer vite de compte lance plusieurs lectures : seule la dernière compte (sinon on proposerait de signer
        // avec le compte précédent, exactement l'erreur que GitSpread doit éviter).
        var version = ++_version;
        var identity = await Services.Git.GetIdentityAsync(isRepository ? path : Environment.CurrentDirectory);
        if (version != _version) return;
        Label = identity is { Name: null, Email: null }
            ? "Aucune identité configurée"
            : $"{identity.Name ?? "(sans nom)"} <{identity.Email ?? "sans e-mail"}>";

        GitHubUser? user = null;
        if (account is { IsAuthenticated: true })
        {
            user = await host.GetAccountUserAsync(account); // null hors ligne : avertissement générique
            if (version != _version) return;
        }
        Suggested = user is null || account is null ? null : host.IdentityFor(account, user);

        var known = account is not null && host.Settings.IsKnownEmail(account.Host, account.Login, identity.Email);
        _unrecognised = null;
        Warning = (identity, user) switch
        {
            ({ Email: null }, _) => "git ne sait pas qui signe les commits : le commit échouera sans identité.",
            (_, { } u) when !u.OwnsEmail(identity.Email) && !known =>
                $"L'adresse {identity.Email} n'est pas reconnue comme une adresse du compte {u.Login}. "
                + "Sur GitHub, ces commits risquent d'être attribués à quelqu'un d'autre. Si c'est bien une adresse de ce compte, clique sur « C'est bien mon adresse ».",
            _ => null,
        };
        if (Warning is not null && identity.Email is { } email && user is not null && account is not null)
            _unrecognised = (account, email);
        OnPropertyChanged(nameof(CanConfirmEmail));
    }

    /// <summary>« C'est bien mon adresse » : mémorisée pour ce compte, l'avertissement ne revient plus.</summary>
    [RelayCommand]
    private void ConfirmEmail()
    {
        if (_unrecognised is not { } u) return;
        host.Settings.AddKnownEmail(u.Account.Host, u.Account.Login, u.Email);
        host.SaveSettings();
        _unrecognised = null;
        Warning = null;
        OnPropertyChanged(nameof(CanConfirmEmail));
    }

    /// <summary>Applique l'identité proposée à ce dépôt seulement (git config --local).</summary>
    public async Task<IReadOnlyList<GitResult>> ApplySuggestedAsync()
    {
        if (Suggested is not { Name: { } name, Email: { } email }) return [];
        var results = await Services.Git.SetLocalIdentityAsync(path, name, email);
        if (results.All(r => r.Succeeded))
        {
            Label = $"{name} <{email}>";
            Warning = null;
            _unrecognised = null;
            OnPropertyChanged(nameof(CanConfirmEmail));
        }
        return results;
    }
}
