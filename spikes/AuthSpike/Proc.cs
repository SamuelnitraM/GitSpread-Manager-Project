using System.Diagnostics;
using System.Text;

namespace GitSpread.AuthSpike;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Lance un exécutable sans shell : les arguments passent par ArgumentList
/// (aucun problème d'espaces ou d'accents), l'environnement est surchargeable par appel.
/// </summary>
public static class Proc
{
    public static async Task<ProcResult> Run(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? stdin = null,
        TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Impossible de lancer {fileName}");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();

        if (stdin is not null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        var timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { p.Kill(entireProcessTree: true); } catch { /* déjà terminé */ }
        }

        return new ProcResult(timedOut ? -1 : p.ExitCode, await outTask, await errTask, timedOut);
    }
}
