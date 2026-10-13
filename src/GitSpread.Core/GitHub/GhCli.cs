using GitSpread.Core.Processes;
using GitSpread.Core.Security;

namespace GitSpread.Core.GitHub;

/// <summary>Enveloppe minimale de la GitHub CLI (gh).</summary>
public sealed class GhCli(IProcessRunner runner)
{
    public const string Executable = "gh";

    /// <summary>Requête gh avec l'environnement par défaut (sans prompt ni couleurs), à compléter avec <c>with</c>.</summary>
    public ProcessRequest Request(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment = null) =>
        new(Executable, args) { Environment = WithDefaults(environment) };

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
        runner.RunAsync(request, cancellationToken);

    public Task<ProcessResult> RunAsync(IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync(Request(args, environment), cancellationToken);

    /// <summary>Comme <see cref="RunAsync(IReadOnlyList{string}, IReadOnlyDictionary{string, string}?, CancellationToken)"/>,
    /// mais lève <see cref="GhCommandException"/> si la commande échoue.</summary>
    public async Task<string> RunCheckedAsync(IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(args, environment, cancellationToken);
        return result.Succeeded ? result.StandardOutput : throw new GhCommandException(args, result);
    }

    // Jamais de prompt ni de couleurs : l'app lit la sortie, personne n'est derrière un terminal.
    private static Dictionary<string, string> WithDefaults(IReadOnlyDictionary<string, string>? environment)
    {
        var env = new Dictionary<string, string> { ["GH_PROMPT_DISABLED"] = "1", ["NO_COLOR"] = "1", ["GH_NO_UPDATE_NOTIFIER"] = "1" };
        if (environment is not null)
            foreach (var (k, v) in environment) env[k] = v;
        return env;
    }
}

public sealed class GhCommandException(IReadOnlyList<string> args, ProcessResult result)
    : Exception(SecretRedactor.Redact(
        result.TimedOut
            ? $"gh {string.Join(' ', args)} n'a pas répondu à temps."
            : $"gh {string.Join(' ', args)} a échoué (code {result.ExitCode}) : {UsefulLine(result)}"))
{
    public ProcessResult Result { get; } = result;

    private static string UsefulLine(ProcessResult r)
    {
        var lines = (r.StandardError + "\n" + r.StandardOutput).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault() ?? "(aucun message)";
    }
}
