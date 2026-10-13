using System.Text;

namespace GitSpread.Core.Git;

/// <summary>
/// Variables d'environnement qui forcent un processus git à s'authentifier avec un compte précis,
/// sans gh auth switch ni modification de la config de l'utilisateur (stratégie validée à l'étape 0,
/// voir spikes/AuthSpike/RESULTS.md).
/// </summary>
public static class GitCredentialEnvironment
{
    public static IReadOnlyDictionary<string, string> For(string login, string token, string host = "github.com")
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{token}"));
        return WithConfig(
            ("credential.helper", ""), // vide la liste des helpers (GCM) pour ce seul processus
            ($"http.https://{host}/.extraHeader", $"Authorization: Basic {basic}")); // limité à cet hôte
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
