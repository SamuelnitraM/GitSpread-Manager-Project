using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;
using GitSpread.Core.Settings;

namespace GitSpread.App.ViewModels;

/// <summary>Ce qu'un projet de la liste peut demander à la fenêtre principale.</summary>
public interface IProjectHost
{
    AppServices Services { get; }
    IDialogService Dialogs { get; }
    IReadOnlyList<GitHubAccount> Accounts { get; }

    /// <summary>Tous les projets locaux trouvés (pour retrouver ceux liés à un repo GitHub).</summary>
    IReadOnlyList<LocalProject> LocalProjects { get; }


    Task ReloadRepositoriesAsync(GitHubAccount account);

    /// <summary>Recharge les repos de tous les comptes (collaborations comprises) puis range de nouveau les projets locaux :
    /// un projet partagé apparaît dans l'onglet de chaque compte qui y a accès (F3A).</summary>
    Task ReloadCollaborationsAsync();
    AppSettings Settings { get; }
    void SaveSettings();

    /// <summary>Profil GitHub du compte (id, nom, e-mail public), mis en cache jusqu'à la prochaine actualisation.
    /// Null si GitHub est injoignable.</summary>
    Task<GitHubUser?> GetAccountUserAsync(GitHubAccount account);

    /// <summary>Vérifie qu'un compte a une autorisation ; sinon propose de l'ajouter (validation dans le navigateur).
    /// Vrai si l'autorisation est disponible à la fin.</summary>
    Task<bool> EnsureScopeAsync(GitHubAccount account, string scope, string reason);

    /// <summary>Relance l'analyse (le projet a pu changer d'onglet) puis le resélectionne.</summary>
    Task RescanAsync(string? revealPath = null);

    Task RemoveExtraProjectAsync(string path);

    /// <summary>Range un projet sans remote dans les projets locaux d'un compte, ou le remet dans « Non liés » (null).
    /// Rien n'est créé sur GitHub.</summary>
    Task AssignProjectAsync(string path, GitHubAccount? account);
}

/// <summary>Fenêtres modales, ouvertes par l'interface (les ViewModels ne connaissent pas WPF).</summary>
public interface IDialogService
{
    bool ShowCommit(CommitViewModel viewModel);
    void ShowPublish(PublishViewModel viewModel);
    bool ShowIntegration(IntegrationViewModel viewModel);
    void ShowRepoint(RepointViewModel viewModel);
    void ShowScopes(AccountScopesViewModel viewModel);
    void ShowClone(CloneViewModel viewModel);
    void ShowEditRepository(EditRepositoryViewModel viewModel);
    /// <param name="destructive">Action difficile à défaire : « Non » est le choix par défaut (Entrée).</param>
    bool Confirm(string message, string title, bool destructive = false);

    /// <summary>Choix parmi plusieurs réponses : index choisi, ou -1 si annulé.</summary>
    int Choose(string title, string message, IReadOnlyList<string> options);

    /// <summary>Saisie d'un texte court : null si annulé. <paramref name="validate"/> renvoie le message d'erreur
    /// affiché sous le champ (la fenêtre reste ouverte tant qu'il y en a un).</summary>
    string? Prompt(string title, string message, string initialValue, Func<string, string?>? validate = null, bool spacesToDash = false);
}

/// <summary>Compte rendu de la dernière action lancée sur un projet : commandes, résultat en clair, sortie brute.</summary>
public sealed record ActionLog(string Title, bool Succeeded, string Message, string Commands, string Output)
{
    public bool HasOutput => Output.Length > 0;
    public string Icon => Succeeded ? "" : "";

    public static ActionLog From(string title, IReadOnlyList<GitResult> results, string successMessage)
    {
        var failed = results.FirstOrDefault(r => !r.Succeeded);
        var output = string.Join("\n", results.Select(r => r.Output).Where(o => o.Length > 0));
        var message = failed is null
            ? successMessage
            : failed.Explanation ?? (failed.Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "Échec sans message.");
        return new ActionLog(title, failed is null, message, string.Join("\n", results.Select(r => r.Command)), output);
    }

    public static ActionLog Failure(string title, string message, string commands = "") => new(title, false, message, commands, "");
}
