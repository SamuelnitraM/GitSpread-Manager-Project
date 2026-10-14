using System.Text;

namespace GitSpread.Core.Git;

/// <summary>
/// Variables d'environnement qui forcent un processus git à s'authentifier avec un compte précis,
/// sans gh auth switch ni modification de la config de l'utilisateur (stratégie validée à l'étape 0,
/// voir spikes/AuthSpike/RESULTS.md).
/// </summary>
public static class GitCredentialEnvironment
{
    /// <param name="url">Adresse exacte du dépôt : la vérification du certificat y est aussi imposée. git retient le réglage
    /// le plus précis ; une config locale visant le chemin du dépôt (http.https://github.com/moi/.sslVerify=false) l'emporterait
    /// sinon sur le réglage de l'hôte.</param>
    public static IReadOnlyDictionary<string, string> For(string login, string token, string host = "github.com", string? url = null)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{token}"));
        List<(string, string)> config =
        [
            ("credential.helper", ""), // vide la liste des helpers (GCM) pour ce seul processus
            ($"http.https://{host}/.extraHeader", $"Authorization: Basic {basic}"), // limité à cet hôte
            // Jamais d'envoi du jeton sans vérification du certificat, même si la config du dépôt la désactive.
            ("http.sslVerify", "true"),
            ($"http.https://{host}/.sslVerify", "true"),
            // Pas de descente dans les sous-modules : leur propre configuration (.git/modules/…) n'est pas contrôlée et
            // pourrait envoyer le jeton par un proxy. GitSpread ne gère pas les sous-modules.
            ("submodule.recurse", "false"),
            ("fetch.recurseSubmodules", "false"),
            ("push.recurseSubmodules", "no"),
            // Pas de « http.curloptResolve= » ici : vide, il casse le choix du moteur SSL de git pour Windows (« Unsupported SSL
            // backend »). Une redirection d'adresse déclarée par le dépôt est refusée avant l'envoi (GitService.NetworkOverrideAsync).
        ];
        if (url is not null) config.Add(($"http.{url}.sslVerify", "true")); // ex. https://moi@github.com:443/o/r.git
        return WithConfig([.. config]);
    }

    /// <summary>Environnement non interactif : échec propre plutôt qu'une fenêtre ou un prompt bloquant.</summary>
    public static IReadOnlyDictionary<string, string> NonInteractive() => WithConfig();

    private static Dictionary<string, string> WithConfig(params (string Key, string Value)[] config)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_CONFIG_COUNT"] = config.Length.ToString(),
        };
        for (var i = 0; i < config.Length; i++)
        {
            env[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
            env[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
        }
        return env;
    }
}
