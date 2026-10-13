using System.Text.Json;
using GitSpread.Core.Accounts;

namespace GitSpread.Core.GitHub;

/// <summary>Collaborateur d'un repo. Role : admin, maintain, write, triage ou read.</summary>
public sealed record GitHubCollaborator(string Login, string Role);

/// <summary>Invitation à collaborer sur un repo, envoyée ou reçue.</summary>
public sealed record GitHubInvitation(long Id, string FullName, string Invitee, string Inviter)
{
    public string Permission { get; init; } = "write";
    public bool IsExpired { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
}

public enum CollaboratorOutcome
{
    /// <summary>Le compte était déjà collaborateur : rien n'a changé.</summary>
    AlreadyCollaborator,
    /// <summary>Invitation envoyée : la personne doit l'accepter (compte non connecté à GitSpread).</summary>
    Invited,
    /// <summary>Invitation envoyée puis acceptée avec le token du compte invité (compte connecté).</summary>
    Joined,
}

/// <summary>
/// Collaborateurs (F3A). Sur un repo personnel, un collaborateur a toujours l'accès « écriture » :
/// GitHub refuse tout autre niveau (les niveaux lecture, maintenance… n'existent que pour les organisations).
/// </summary>
public sealed partial class GitHubService
{
    /// <summary>Collaborateurs directs du repo, propriétaire exclu. Lisible par tout compte ayant au moins l'accès écriture.</summary>
    public async Task<IReadOnlyList<GitHubCollaborator>> ListCollaboratorsAsync(GitHubAccount account, string fullName, CancellationToken ct = default)
    {
        var owner = fullName.Split('/')[0];
        var json = await gh.RunCheckedAsync(["api", $"repos/{fullName}/collaborators?affiliation=direct&per_page=100"], await EnvironmentForAsync(account, ct), ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => new GitHubCollaborator(e.GetProperty("login").GetString()!,
                e.TryGetProperty("role_name", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "write"))
            .Where(c => !c.Login.Equals(owner, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Invitations envoyées et pas encore acceptées (visible par un administrateur du repo).</summary>
    public async Task<IReadOnlyList<GitHubInvitation>> ListPendingInvitationsAsync(GitHubAccount account, string fullName, CancellationToken ct = default) =>
        ParseInvitations(await gh.RunCheckedAsync(["api", $"repos/{fullName}/invitations?per_page=100"], await EnvironmentForAsync(account, ct), ct));

    /// <summary>Invitations reçues par le compte, en attente de réponse.</summary>
    public async Task<IReadOnlyList<GitHubInvitation>> ListReceivedInvitationsAsync(GitHubAccount account, CancellationToken ct = default) =>
        ParseInvitations(await gh.RunCheckedAsync(["api", "user/repository_invitations?per_page=100"], await EnvironmentForAsync(account, ct), ct));

    /// <summary>
    /// Invite un utilisateur GitHub à collaborer (token du propriétaire). Renvoie l'identifiant de l'invitation,
    /// ou null si l'utilisateur est déjà collaborateur (GitHub répond alors sans contenu).
    /// </summary>
    public async Task<long?> InviteCollaboratorAsync(GitHubAccount owner, string fullName, string login, CancellationToken ct = default)
    {
        var json = await gh.RunCheckedAsync(["api", "--method", "PUT", $"repos/{fullName}/collaborators/{login}"], await EnvironmentForAsync(owner, ct), ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetInt64() : null;
    }

    public async Task AcceptInvitationAsync(GitHubAccount invitee, long invitationId, CancellationToken ct = default) =>
        await gh.RunCheckedAsync(["api", "--method", "PATCH", $"user/repository_invitations/{invitationId}"], await EnvironmentForAsync(invitee, ct), ct);

    public async Task DeclineInvitationAsync(GitHubAccount invitee, long invitationId, CancellationToken ct = default) =>
        await gh.RunCheckedAsync(["api", "--method", "DELETE", $"user/repository_invitations/{invitationId}"], await EnvironmentForAsync(invitee, ct), ct);

    /// <summary>Annule une invitation envoyée (token du propriétaire).</summary>
    public async Task CancelInvitationAsync(GitHubAccount owner, string fullName, long invitationId, CancellationToken ct = default) =>
        await gh.RunCheckedAsync(["api", "--method", "DELETE", $"repos/{fullName}/invitations/{invitationId}"], await EnvironmentForAsync(owner, ct), ct);

    /// <summary>Retire un collaborateur. Avec le token du collaborateur lui-même, c'est « quitter le repo ».</summary>
    public async Task RemoveCollaboratorAsync(GitHubAccount account, string fullName, string login, CancellationToken ct = default) =>
        await gh.RunCheckedAsync(["api", "--method", "DELETE", $"repos/{fullName}/collaborators/{login}"], await EnvironmentForAsync(account, ct), ct);

    /// <summary>
    /// Donne accès au repo à un autre compte. Si ce compte est connecté à GitSpread, l'invitation est acceptée
    /// aussitôt avec son propre token. Seule l'invitation qui vient d'être créée est acceptée, jamais une autre.
    /// </summary>
    public async Task<CollaboratorOutcome> AddCollaboratorAsync(GitHubAccount owner, string fullName, string login,
        GitHubAccount? connectedInvitee, CancellationToken ct = default)
    {
        var invitationId = await InviteCollaboratorAsync(owner, fullName, login, ct);
        if (invitationId is null) return CollaboratorOutcome.AlreadyCollaborator;
        if (connectedInvitee is null) return CollaboratorOutcome.Invited;

        await AcceptInvitationAsync(connectedInvitee, invitationId.Value, ct);
        return CollaboratorOutcome.Joined;
    }

    /// <summary>
    /// Repos dont le compte est collaborateur sans en être propriétaire (repos d'autres comptes, y compris non connectés).
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepository>> ListCollaborationsAsync(GitHubAccount account, CancellationToken ct = default)
    {
        // Même forme que « gh repo list --json » : un objet par ligne, toutes pages comprises.
        const string projection = "[.[] | {name, owner: {login: .owner.login}, url: .html_url, sshUrl: .ssh_url, "
            + "visibility: (.visibility // (if .private then \"private\" else \"public\" end) | ascii_upcase), description, "
            + "isArchived: .archived, isFork: .fork, pushedAt: .pushed_at}] | .[]";
        var output = await gh.RunCheckedAsync(
            ["api", "user/repos?affiliation=collaborator&per_page=100", "--paginate", "--jq", projection], await EnvironmentForAsync(account, ct), ct);
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return ParseRepositories($"[{string.Join(',', lines)}]")
            .Where(r => !r.Owner.Equals(account.Login, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    internal static IReadOnlyList<GitHubInvitation> ParseInvitations(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => new GitHubInvitation(
                e.GetProperty("id").GetInt64(),
                e.GetProperty("repository").GetProperty("full_name").GetString()!,
                e.TryGetProperty("invitee", out var i) && i.ValueKind == JsonValueKind.Object ? i.GetProperty("login").GetString()! : "",
                e.TryGetProperty("inviter", out var by) && by.ValueKind == JsonValueKind.Object ? by.GetProperty("login").GetString()! : "")
            {
                Permission = e.TryGetProperty("permissions", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "write",
                IsExpired = e.TryGetProperty("expired", out var x) && x.ValueKind == JsonValueKind.True,
                CreatedAt = e.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(c.GetString(), out var date) ? date : null,
            })
            .ToList();
    }
}
