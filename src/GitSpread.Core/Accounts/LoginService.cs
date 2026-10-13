using System.Text;
using System.Text.RegularExpressions;
using GitSpread.Core.GitHub;

namespace GitSpread.Core.Accounts;

public enum GitProtocol { Https, Ssh }

/// <summary>Réponses aux questions de « gh auth login », posées une par une par l'interface.</summary>
public sealed record LoginRequest(string Host, GitProtocol Protocol)
{
    /// <summary>Token collé par l'utilisateur. Null = connexion par le navigateur (code à usage unique).</summary>
    public string? Token { get; init; }

    /// <summary>Demande le scope admin:public_key, nécessaire pour envoyer une clé SSH ensuite.</summary>
    public bool RequestSshKeyScope { get; init; }
}

public sealed record DeviceCode(string Code, string VerificationUrl);

/// <summary>Connexion d'un nouveau compte via gh auth login, en mode non interactif.</summary>
public sealed partial class LoginService(GhCli gh, AccountService accounts)
{
    /// <summary>Durée de vie d'un code de connexion GitHub.</summary>
    public static readonly TimeSpan BrowserLoginTimeout = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<string> BuildArguments(LoginRequest request)
    {
        var args = new List<string>
        {
            "auth", "login",
            "--hostname", request.Host,
            "--git-protocol", request.Protocol == GitProtocol.Ssh ? "ssh" : "https",
            request.Token is null ? "--web" : "--with-token",
            "--skip-ssh-key", // la clé SSH est gérée par GitSpread, avec le bon compte
        };
        if (request.RequestSshKeyScope) args.AddRange(["--scopes", "admin:public_key"]);
        return args;
    }

    /// <summary>
    /// Lance la connexion et renvoie le compte connecté. En mode navigateur, <paramref name="onDeviceCode"/>
    /// reçoit le code à saisir sur GitHub dès que gh l'affiche (sur un thread d'arrière-plan).
    /// </summary>
    public async Task<GitHubAccount> LoginAsync(LoginRequest request, Action<DeviceCode>? onDeviceCode = null,
        CancellationToken cancellationToken = default)
    {
        var before = await accounts.GetAccountsAsync(cancellationToken);
        var args = BuildArguments(request);

        var output = new StringBuilder();
        var codeSent = false;
        var processRequest = gh.Request(args) with
        {
            StandardInput = request.Token is null ? null : request.Token.Trim() + "\n",
            Timeout = BrowserLoginTimeout,
            OutputReceived = chunk =>
            {
                DeviceCode? code;
                lock (output)
                {
                    output.Append(chunk);
                    if (codeSent || (code = ParseDeviceCode(output.ToString())) is null) return;
                    codeSent = true;
                }
                onDeviceCode?.Invoke(code);
            },
        };

        var result = await gh.RunAsync(processRequest, cancellationToken);
        if (!result.Succeeded) throw new GhCommandException(args, result);

        var after = await accounts.GetAccountsAsync(cancellationToken);
        var onHost = after.Where(a => a.Host.Equals(request.Host, StringComparison.OrdinalIgnoreCase)).ToList();
        var login = LoggedInAs().Match(result.StandardError + result.StandardOutput) is { Success: true } m ? m.Groups[1].Value : null;

        return onHost.FirstOrDefault(a => a.Login.Equals(login, StringComparison.OrdinalIgnoreCase))
            ?? onHost.FirstOrDefault(a => !before.Any(b => b.Login == a.Login && b.Host == a.Host))
            ?? onHost.FirstOrDefault(a => a.IsActive)
            ?? throw new InvalidOperationException("La connexion semble avoir réussi mais aucun compte n'a été trouvé ensuite.");
    }

    /// <summary>Configure gh comme gestionnaire d'identifiants git pour cet hôte (gh auth setup-git).</summary>
    public Task SetupGitAsync(string host, CancellationToken cancellationToken = default) =>
        gh.RunCheckedAsync(["auth", "setup-git", "--hostname", host], cancellationToken: cancellationToken);

    public static DeviceCode? ParseDeviceCode(string output)
    {
        var code = Code().Match(output);
        if (!code.Success) return null;
        var url = Url().Match(output);
        return new DeviceCode(code.Groups[1].Value, url.Success ? url.Value : "https://github.com/login/device");
    }

    // « ! One-time code (ABCD-1234) copied to clipboard » ou « ! First copy your one-time code: ABCD-1234 »
    [GeneratedRegex(@"one-time code\W+([A-Z0-9]{4}-[A-Z0-9]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex Code();

    [GeneratedRegex(@"https?://\S+/login/device")]
    private static partial Regex Url();

    [GeneratedRegex(@"Logged in as ([A-Za-z0-9-]+)")]
    private static partial Regex LoggedInAs();
}
