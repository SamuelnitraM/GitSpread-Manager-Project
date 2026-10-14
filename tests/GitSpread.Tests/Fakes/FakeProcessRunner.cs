using GitSpread.Core.Processes;

namespace GitSpread.Tests.Fakes;

/// <summary>Lanceur de processus simulé : répond selon le début des arguments et mémorise les appels.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(string[] Prefix, ProcessResult Result)> _responses = [];

    public List<ProcessRequest> Requests { get; } = [];

    public FakeProcessRunner When(string[] argsPrefix, string stdout, int exitCode = 0, string stderr = "")
    {
        _responses.Insert(0, (argsPrefix, new ProcessResult(exitCode, stdout, stderr))); // la plus récente l'emporte
        return this;
    }

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        // La réponse la plus spécifique (préfixe le plus long) gagne.
        var match = _responses
            .Where(r => request.Arguments.Take(r.Prefix.Length).SequenceEqual(r.Prefix))
            .OrderByDescending(r => r.Prefix.Length)
            .Select(r => r.Result)
            .FirstOrDefault();
        if (match is null) throw new InvalidOperationException($"Aucune réponse simulée pour : {request.CommandLine}");
        if (request.StandardOutputPath is { } file)
        {
            File.WriteAllText(file, match.StandardOutput);
            return Task.FromResult(match with { StandardOutput = "" });
        }
        if (match.StandardError.Length > 0) request.OutputReceived?.Invoke(match.StandardError);
        if (match.StandardOutput.Length > 0) request.OutputReceived?.Invoke(match.StandardOutput);
        return Task.FromResult(match);
    }

    public ProcessRequest Single(params string[] argsPrefix) =>
        Requests.Single(r => r.Arguments.Take(argsPrefix.Length).SequenceEqual(argsPrefix));
}
