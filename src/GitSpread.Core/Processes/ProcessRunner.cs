using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GitSpread.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitSpread.Core.Processes;

/// <summary>
/// Lance un exécutable sans shell. Les arguments passent par <see cref="ProcessStartInfo.ArgumentList"/>
/// (espaces, accents et guillemets gérés par .NET), l'environnement est surchargeable par appel.
/// </summary>
public sealed class ProcessRunner(ILogger<ProcessRunner>? logger = null) : IProcessRunner
{
    private readonly ILogger _logger = logger ?? NullLogger<ProcessRunner>.Instance;

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in request.Arguments) psi.ArgumentList.Add(arg);
        if (request.WorkingDirectory is not null) psi.WorkingDirectory = request.WorkingDirectory;
        if (request.Environment is not null)
            foreach (var (key, value) in request.Environment) psi.Environment[key] = value;

        var stopwatch = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new ToolNotFoundException(request.FileName);
        }
        catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3) // fichier ou chemin introuvable
        {
            throw new ToolNotFoundException(request.FileName, e);
        }

        using (process)
        {
            var stdout = PumpAsync(process.StandardOutput, request.OutputReceived);
            var stderr = PumpAsync(process.StandardError, request.OutputReceived);

            if (request.StandardInput is not null)
                await process.StandardInput.WriteAsync(request.StandardInput);
            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* déjà terminé */ }
                cancellationToken.ThrowIfCancellationRequested();
                timedOut = true;
            }

            var result = new ProcessResult(timedOut ? -1 : process.ExitCode, await stdout, await stderr, timedOut);
            _logger.LogInformation("{Command} → code {ExitCode} en {Elapsed} ms{TimedOut}",
                SecretRedactor.Redact(request.CommandLine), result.ExitCode, stopwatch.ElapsedMilliseconds,
                timedOut ? " (délai dépassé)" : "");
            if (!result.Succeeded && result.StandardError.Length > 0)
                _logger.LogWarning("stderr : {StdErr}", SecretRedactor.Redact(result.StandardError.Trim()));
            return result;
        }
    }

    private static async Task<string> PumpAsync(StreamReader reader, Action<string>? onChunk)
    {
        var all = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
        {
            var chunk = new string(buffer, 0, read);
            all.Append(chunk);
            onChunk?.Invoke(chunk);
        }
        return all.ToString();
    }
}
