using System.Text.RegularExpressions;

namespace GitSpread.Core.Git;

/// <summary>Traduit les erreurs git les plus courantes en explications compréhensibles, avec la marche à suivre.</summary>
public static partial class GitErrorExplainer
{
    public static string? Explain(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        if (DeniedTo().Match(output) is { Success: true } denied)
            return $"GitHub refuse l'accès au compte {denied.Groups[1].Value} : ce compte n'a pas le droit d'écrire dans ce dépôt.";
        if (Has(output, "rejected") && (Has(output, "non-fast-forward") || Has(output, "fetch first")))
            return "GitHub contient des commits que tu n'as pas encore. Clique d'abord sur « Tirer », puis pousse à nouveau.";
        if (Has(output, "Not possible to fast-forward") || Has(output, "diverging branches") || Has(output, "Need to specify how to reconcile"))
            return "Ta branche et celle de GitHub ont chacune des commits différents. GitSpread ne fusionne pas automatiquement pour ne rien abîmer : il faudra fusionner ou rebaser.";
        if (Has(output, "would be overwritten by checkout") || Has(output, "would be overwritten by switch"))
            return "Tes modifications en cours entrent en conflit avec la branche choisie. Mets-les de côté (ou commite-les) avant de changer de branche.";
        if (Has(output, "a branch named") && Has(output, "already exists"))
            return "Une branche porte déjà ce nom.";
        if (Has(output, "is not a valid branch name") || Has(output, "not a valid branch name"))
            return "Nom de branche invalide : évite les espaces et les caractères spéciaux (ex. « correctif-stock »).";
        if (Has(output, "is not fully merged"))
            return "Cette branche contient des commits qui ne sont intégrés nulle part ailleurs : GitSpread ne la supprime pas.";
        if (Has(output, "refusing to fetch into branch") || Has(output, "refusing to fetch into current branch"))
            return "Impossible d'avancer la branche sur laquelle tu te trouves de cette façon.";
        if (Has(output, "would be overwritten by merge") || Has(output, "Please commit your changes or stash them"))
            return "Des modifications locales seraient écrasées. Commite-les d'abord, puis réessaie.";
        if (Has(output, "CONFLICT"))
            return "Des conflits sont apparus : les mêmes lignes ont été modifiées des deux côtés. Il faut les résoudre avant de continuer.";
        if (Has(output, "nothing to commit") || Has(output, "nothing added to commit"))
            return "Rien à commiter : aucune modification.";
        if (Has(output, "Please tell me who you are") || Has(output, "unable to auto-detect email"))
            return "git ne sait pas qui signe les commits. Définis l'identité des commits pour ce projet.";
        if (Has(output, "has no upstream branch") || Has(output, "no tracking information"))
            return "Cette branche n'est pas encore liée à une branche sur GitHub. Pousse-la une première fois.";
        if (Has(output, "Repository not found") || Has(output, "repository '") && Has(output, "not found"))
            return "Dépôt introuvable sur GitHub : il a été supprimé ou renommé, ou il est privé et ce compte n'y a pas accès.";
        if (Has(output, "Permission denied (publickey)"))
            return "Clé SSH refusée : aucune clé SSH acceptée par GitHub n'est configurée pour ce compte sur cet ordinateur.";
        if (Has(output, "Host key verification failed"))
            return "L'empreinte du serveur SSH n'est pas encore connue. Connecte-toi une fois avec « ssh -T git@github.com » pour l'accepter.";
        if (Has(output, "Authentication failed") || Has(output, "could not read Username") || Has(output, "terminal prompts disabled"))
            return "Authentification impossible : aucun compte connecté dans GitSpread ne correspond à ce dépôt.";
        if (Has(output, "Could not resolve host") || Has(output, "Failed to connect") || Has(output, "unable to access"))
            return "Impossible de joindre le serveur : vérifie ta connexion internet.";
        if (Has(output, "does not have any commits yet") || Has(output, "src refspec") && Has(output, "does not match any"))
            return "Il n'y a encore aucun commit à envoyer. Fais un premier commit.";
        if (Has(output, "remote origin already exists"))
            return "Ce projet est déjà relié à un dépôt (remote « origin »).";
        if (Has(output, "dubious ownership"))
            return "git refuse ce dossier car il appartient à un autre utilisateur Windows (protection « safe.directory »).";
        return null;
    }

    private static bool Has(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"denied to ([A-Za-z0-9-]+)")]
    private static partial Regex DeniedTo();
}
