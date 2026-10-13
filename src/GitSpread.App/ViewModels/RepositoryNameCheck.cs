using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;

namespace GitSpread.App.ViewModels;

/// <summary>
/// Vérifie pendant la frappe qu'un nom de repo est libre sur le compte choisi : une demi-seconde après
/// la dernière touche, la vérification précédente étant annulée.
/// </summary>
public sealed class RepositoryNameCheck(GitHubService gitHub)
{
    /// <summary>Délai après la dernière frappe (réglable pour les tests).</summary>
    public static TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(500);

    private CancellationTokenSource? _cts;

    /// <summary>Vérification en cours (terminée immédiatement s'il n'y en a pas).</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public void Cancel()
    {
        _cts?.Cancel();
        Pending = Task.CompletedTask;
    }

    /// <param name="immediate">Sans délai (au clic sur « Publier » / « Lancer »).</param>
    /// <param name="setChecking">Affiche « Vérification… » pendant l'appel à GitHub.</param>
    /// <param name="setError">Reçoit le message « existe déjà », ou null si le nom est libre.</param>
    public void Schedule(GitHubAccount account, string name, bool immediate, Action<bool> setChecking, Action<string?> setError)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Pending = RunAsync();

        async Task RunAsync()
        {
            try
            {
                if (!immediate) await Task.Delay(Delay, cts.Token);
                setChecking(true);
                var exists = await gitHub.RepositoryExistsAsync(account, $"{account.Login}/{name}", cts.Token);
                if (!cts.IsCancellationRequested)
                    setError(exists ? $"Le repo {account.Login}/{name} existe déjà sur ce compte : choisis un autre nom." : null);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* hors ligne : la création échouera avec un message clair si le nom est pris */ }
            finally
            {
                if (_cts == cts) setChecking(false);
            }
        }
    }
}
