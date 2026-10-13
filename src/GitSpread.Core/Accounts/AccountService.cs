using System.Text.Json;
using GitSpread.Core.GitHub;

namespace GitSpread.Core.Accounts;

public sealed record GitHubAccount(string Login, string Host, bool IsActive, bool IsAuthenticated)
{
    public string? Scopes { get; init; }

    /// <summary>Autorisations accordées au token (« repo », « delete_repo »…).</summary>
    public IReadOnlySet<string> ScopeSet => (Scopes ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vrai si l'autorisation est accordée, directement ou via une autorisation plus large
    /// (ex. admin:public_key couvre write:public_key et read:public_key).</summary>
    public bool HasScope(string scope) => ScopeCatalog.IsCovered(scope, ScopeSet);
    public string GitProtocol { get; init; } = "https";

    /// <summary>Message d'erreur de gh quand le token n'est plus valide (IsAuthenticated = false).</summary>
    public string? Error { get; init; }

    /// <summary>Même compte (login et hôte), quel que soit son état (actif, autorisations…).</summary>
    public bool Is(GitHubAccount? other) =>
        other is not null && Login.Equals(other.Login, StringComparison.OrdinalIgnoreCase) && Host.Equals(other.Host, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Détecte les comptes GitHub connectés via gh et fournit leurs tokens à la demande.</summary>
public sealed class AccountService(GhCli gh)
{
    public const string DefaultHost = "github.com";

    /// <summary>Liste les comptes connectés (tous hôtes). Liste vide si aucun compte n'est connecté.</summary>
    public async Task<IReadOnlyList<GitHubAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        // gh renvoie un code non nul si un des comptes est en erreur, mais le JSON reste exploitable.
        var result = await gh.RunAsync(["auth", "status", "--json", "hosts"], cancellationToken: cancellationToken);
        return ParseAuthStatus(result.StandardOutput);
    }

    /// <summary>Token du compte, récupéré dans le trousseau via gh. Jamais stocké par GitSpread.</summary>
    public async Task<string> GetTokenAsync(GitHubAccount account, CancellationToken cancellationToken = default)
    {
        var output = await gh.RunCheckedAsync(
            ["auth", "token", "--hostname", account.Host, "--user", account.Login], cancellationToken: cancellationToken);
        var token = output.Trim();
        return token.Length > 0 ? token : throw new InvalidOperationException($"gh n'a renvoyé aucun token pour {account.Login}.");
    }

    /// <summary>Rend ce compte actif dans gh (gh auth switch). N'a aucun effet sur les actions de GitSpread,
    /// qui choisit toujours lui-même le compte, mais change le compte utilisé par gh dans le terminal.</summary>
    public Task SwitchAsync(GitHubAccount account, CancellationToken cancellationToken = default) =>
        gh.RunCheckedAsync(["auth", "switch", "--hostname", account.Host, "--user", account.Login], cancellationToken: cancellationToken);

    /// <summary>
    /// Ajoute et/ou retire des autorisations au token d'un compte (validation dans le navigateur, comme une connexion).
    /// gh ne sait le faire que pour le compte actif : GitSpread bascule dessus le temps de l'opération, puis
    /// rend la main au compte qui était actif — même en cas d'échec ou d'annulation.
    /// </summary>
    public async Task<GitHubAccount> ChangeScopesAsync(GitHubAccount account, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove,
        Action<DeviceCode>? onDeviceCode = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (add.Count == 0 && remove.Count == 0) return account;

        var before = await GetAccountsAsync(cancellationToken);
        var previous = before.FirstOrDefault(a => a.IsActive && a.Host == account.Host);
        var mustSwitch = previous is not null && previous.Login != account.Login;
        if (mustSwitch)
        {
            progress?.Report($"{account.Login} devient temporairement le compte actif de gh…");
            await SwitchAsync(account, cancellationToken);
        }

        try
        {
            progress?.Report("Demande d'un code de validation à GitHub…");
            var args = BuildScopeArguments(account.Host, add, remove);
            var output = new System.Text.StringBuilder();
            var codeSent = false;
            var request = gh.Request(args) with
            {
                Timeout = LoginService.BrowserLoginTimeout,
                OutputReceived = chunk =>
                {
                    DeviceCode? code;
                    lock (output)
                    {
                        output.Append(chunk);
                        if (codeSent || (code = LoginService.ParseDeviceCode(output.ToString())) is null) return;
                        codeSent = true;
                    }
                    onDeviceCode?.Invoke(code);
                },
            };
            var result = await gh.RunAsync(request, cancellationToken);
            if (!result.Succeeded) throw new GhCommandException(args, result);
        }
        finally
        {
            if (mustSwitch)
            {
                progress?.Report($"{previous!.Login} redevient le compte actif.");
                await SwitchAsync(previous!, CancellationToken.None); // toujours, même après une annulation
            }
        }

        var after = await GetAccountsAsync(cancellationToken);
        return after.FirstOrDefault(a => a.Login == account.Login && a.Host == account.Host) ?? account;
    }

    public static IReadOnlyList<string> BuildScopeArguments(string host, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
    {
        var args = new List<string> { "auth", "refresh", "--hostname", host };
        if (add.Count > 0) args.AddRange(["--scopes", string.Join(',', add)]);
        if (remove.Count > 0) args.AddRange(["--remove-scopes", string.Join(',', remove)]);
        return args;
    }

    /// <summary>Retire le compte de gh (le token est supprimé du trousseau). Les repos et les fichiers ne sont pas touchés.</summary>
    public Task LogoutAsync(GitHubAccount account, CancellationToken cancellationToken = default) =>
        gh.RunCheckedAsync(["auth", "logout", "--hostname", account.Host, "--user", account.Login], cancellationToken: cancellationToken);

    internal static IReadOnlyList<GitHubAccount> ParseAuthStatus(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return []; } // ex. « You are not logged into any GitHub hosts »

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Object)
                return [];

            var accounts = new List<GitHubAccount>();
            foreach (var host in hosts.EnumerateObject())
            foreach (var entry in host.Value.EnumerateArray())
            {
                var state = Str(entry, "state");
                accounts.Add(new GitHubAccount(
                    Login: Str(entry, "login") ?? "?",
                    Host: Str(entry, "host") ?? host.Name,
                    IsActive: entry.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True,
                    IsAuthenticated: state == "success")
                {
                    Scopes = Str(entry, "scopes"),
                    GitProtocol = Str(entry, "gitProtocol") ?? "https",
                    Error = state == "success" ? null : Str(entry, "error") ?? state,
                });
            }
            return accounts;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
