using System.Text.RegularExpressions;
using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;

namespace GitSpread.App.ViewModels;

/// <summary>Donner accès à un repo à un autre compte (F3A) : depuis le panneau d'un repo ou en déposant un projet sur un onglet.</summary>
public static partial class Collaboration
{
    public const string WriteAccessNote =
        "Sur un repo personnel, un collaborateur a toujours l'accès « écriture » : il peut envoyer des commits et créer des branches, "
        + "mais pas renommer, supprimer ni gérer le repo. Les autres niveaux n'existent que dans les organisations.";

    /// <summary>Invite <paramref name="login"/> sur le repo. Un compte connecté à GitSpread accepte l'invitation aussitôt, avec son propre token.</summary>
    public static async Task<ActionLog> AddAsync(IProjectHost host, GitHubAccount owner, string fullName, string login)
    {
        const string title = "Ajouter un collaborateur";
        var connected = host.Accounts.FirstOrDefault(a => a.IsAuthenticated && a.Host.Equals(owner.Host, StringComparison.OrdinalIgnoreCase)
                                                          && a.Login.Equals(login, StringComparison.OrdinalIgnoreCase));
        var outcome = await host.Services.GitHub.AddCollaboratorAsync(owner, fullName, login, connected);
        var commands = $"gh api --method PUT repos/{fullName}/collaborators/{login}   (avec le compte {owner.Login})"
                       + (outcome == CollaboratorOutcome.Joined ? $"\ngh api --method PATCH user/repository_invitations/<id>   (avec le compte {login})" : "");
        var message = outcome switch
        {
            CollaboratorOutcome.AlreadyCollaborator => $"{login} est déjà collaborateur de {fullName} : rien n'a changé.",
            CollaboratorOutcome.Joined => $"{login} est maintenant collaborateur de {fullName} (invitation acceptée automatiquement avec son compte). "
                                          + $"Les projets locaux liés apparaissent aussi dans l'onglet {login}.",
            _ => $"Invitation envoyée à {login}. Cette personne doit l'accepter sur GitHub (lien reçu par e-mail) ; l'invitation expire au bout de 7 jours.",
        };
        return new ActionLog(title, true, message, commands, "");
    }

    /// <summary>Règles GitHub des noms d'utilisateur : lettres, chiffres et tirets isolés, 39 caractères au plus.</summary>
    public static string? ValidateLogin(string login) =>
        login.Length == 0 ? "Indique un nom d'utilisateur GitHub."
        : LoginPattern().IsMatch(login) ? null
        : "Nom d'utilisateur GitHub invalide : lettres sans accent, chiffres et tirets (pas au début ni à la fin), 39 caractères au plus.";

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")]
    private static partial Regex LoginPattern();
}
