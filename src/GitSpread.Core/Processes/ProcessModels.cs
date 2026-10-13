namespace GitSpread.Core.Processes;

/// <summary>Description complète d'un lancement de processus (facile à inspecter dans les tests).</summary>
public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public string? StandardInput { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Appelé (sur un thread d'arrière-plan) à chaque morceau de sortie, stdout ou stderr, au fil de l'eau.</summary>
    public Action<string>? OutputReceived { get; init; }

    /// <summary>Ligne de commande lisible, pour les logs et la prévisualisation (à masquer avant affichage).</summary>
    public string CommandLine =>
        string.Join(' ', Arguments.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a).Prepend(FileName));
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public interface IProcessRunner
{
    /// <exception cref="ToolNotFoundException">L'exécutable est introuvable dans le PATH.</exception>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

public sealed class ToolNotFoundException(string tool, Exception? inner = null)
    : Exception($"« {tool} » est introuvable. Vérifie qu'il est installé et présent dans le PATH.", inner)
{
    public string Tool { get; } = tool;
}
