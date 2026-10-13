using GitSpread.Core.Processes;

namespace GitSpread.Tests.Processes;

/// <summary>Tests sur de vrais processus (git est un prérequis de GitSpread).</summary>
public sealed class ProcessRunnerTests : IDisposable
{
    private readonly ProcessRunner _runner = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("GitSpread tests éà ").FullName;

    [Theory]
    [InlineData("valeur avec espaces")]
    [InlineData("accents éèàç et guillemets \"doubles\" 'simples'")]
    [InlineData("C:\\Mes Projets\\dossier final\\")]
    [InlineData("")]
    public async Task Passes_arguments_verbatim_even_with_spaces_quotes_and_accents(string value)
    {
        var file = Path.Combine(_dir, "config de test");

        var set = await _runner.RunAsync(new ProcessRequest("git", ["config", "--file", file, "spread.value", value]));
        var get = await _runner.RunAsync(new ProcessRequest("git", ["config", "--file", file, "--get", "spread.value"]));

        Assert.True(set.Succeeded, set.StandardError);
        Assert.Equal(value, get.StandardOutput.TrimEnd('\n', '\r'));
    }

    [Fact]
    public async Task Runs_in_the_requested_working_directory_and_reports_exit_codes()
    {
        var result = await _runner.RunAsync(new ProcessRequest("git", ["rev-parse", "--git-dir"]) { WorkingDirectory = _dir });

        Assert.False(result.Succeeded);
        Assert.Contains("not a git repository", result.StandardError);
    }

    [Fact]
    public async Task Missing_executable_raises_ToolNotFoundException()
    {
        var error = await Assert.ThrowsAsync<ToolNotFoundException>(
            () => _runner.RunAsync(new ProcessRequest("gitspread-outil-inexistant", [])));

        Assert.Equal("gitspread-outil-inexistant", error.Tool);
    }

    [Fact]
    public async Task Kills_the_process_when_the_timeout_is_reached()
    {
        var started = DateTime.UtcNow;

        var result = await _runner.RunAsync(new ProcessRequest("ping", ["-n", "30", "127.0.0.1"])
            { Timeout = TimeSpan.FromMilliseconds(500) });

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _runner.RunAsync(new ProcessRequest("ping", ["-n", "30", "127.0.0.1"]), cts.Token));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
