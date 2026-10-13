using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Processes;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.GitHub;

/// <summary>F3A : collaborateurs (commandes gh), rangement des projets partagés et compte d'envoi.</summary>
public class CollaborationTests
{
    private static readonly GitHubAccount Pro = new("SmartDev-Web", "github.com", true, true);
    private static readonly GitHubAccount Perso = new("SamuelnitraM", "github.com", false, true);

    private static (GitHubService Service, FakeProcessRunner Runner) Create()
    {
        var runner = new FakeProcessRunner()
            .When(["auth", "token", "--hostname", "github.com", "--user", Pro.Login], "gho_tokenPro_0123456789")
            .When(["auth", "token", "--hostname", "github.com", "--user", Perso.Login], "gho_tokenPerso_0123456789");
        var gh = new GhCli(runner);
        return (new GitHubService(gh, new AccountService(gh)), runner);
    }

    private static string TokenOf(ProcessRequest request) => request.Environment!["GH_TOKEN"];

    [Fact]
    public async Task A_connected_account_joins_by_accepting_only_the_invitation_just_created()
    {
        var (service, runner) = Create();
        runner.When(["api", "--method", "PUT"], """{"id":337546465,"permissions":"write"}""")
              .When(["api", "--method", "PATCH"], "");

        var outcome = await service.AddCollaboratorAsync(Pro, "SmartDev-Web/stock", Perso.Login, Perso);

        Assert.Equal(CollaboratorOutcome.Joined, outcome);
        var invite = runner.Single("api", "--method", "PUT");
        Assert.Equal(["api", "--method", "PUT", "repos/SmartDev-Web/stock/collaborators/SamuelnitraM"], invite.Arguments);
        Assert.Equal("gho_tokenPro_0123456789", TokenOf(invite)); // le propriétaire invite…
        var accept = runner.Single("api", "--method", "PATCH");
        Assert.Equal(["api", "--method", "PATCH", "user/repository_invitations/337546465"], accept.Arguments);
        Assert.Equal("gho_tokenPerso_0123456789", TokenOf(accept)); // …et le compte invité accepte avec son propre token
    }

    [Fact]
    public async Task An_existing_collaborator_or_an_outside_user_is_not_auto_accepted()
    {
        var (service, runner) = Create();
        runner.When(["api", "--method", "PUT"], ""); // 204 : déjà collaborateur

        Assert.Equal(CollaboratorOutcome.AlreadyCollaborator, await service.AddCollaboratorAsync(Pro, "SmartDev-Web/stock", Perso.Login, Perso));

        runner.When(["api", "--method", "PUT"], """{"id":42}""");
        Assert.Equal(CollaboratorOutcome.Invited, await service.AddCollaboratorAsync(Pro, "SmartDev-Web/stock", "une-amie", null));
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("PATCH"));
    }

    [Fact]
    public async Task Lists_collaborators_without_the_owner_and_pending_invitations()
    {
        var (service, runner) = Create();
        runner.When(["api", "repos/SmartDev-Web/stock/collaborators?affiliation=direct&per_page=100"],
                """[{"login":"SamuelnitraM","role_name":"write"},{"login":"SmartDev-Web","role_name":"admin"}]""")
              .When(["api", "repos/SmartDev-Web/stock/invitations?per_page=100"],
                """
                [{"id":7,"repository":{"full_name":"SmartDev-Web/stock"},"invitee":{"login":"une-amie"},"inviter":{"login":"SmartDev-Web"},
                  "permissions":"write","created_at":"2026-10-13T15:43:44Z","expired":false}]
                """);

        var collaborators = await service.ListCollaboratorsAsync(Pro, "SmartDev-Web/stock");
        var pending = Assert.Single(await service.ListPendingInvitationsAsync(Pro, "SmartDev-Web/stock"));

        Assert.Equal([new GitHubCollaborator("SamuelnitraM", "write")], collaborators);
        Assert.Equal((7L, "une-amie", "SmartDev-Web", false), (pending.Id, pending.Invitee, pending.Inviter, pending.IsExpired));
    }

    [Fact]
    public async Task Lists_collaborations_from_all_pages_excluding_own_repositories()
    {
        var (service, runner) = Create();
        runner.When(["api", "user/repos?affiliation=collaborator&per_page=100"], """
            {"name":"stock","owner":{"login":"SmartDev-Web"},"url":"https://github.com/SmartDev-Web/stock","sshUrl":"git@github.com:SmartDev-Web/stock.git","visibility":"PRIVATE","description":null,"isArchived":false,"isFork":false,"pushedAt":"2026-10-13T15:43:41Z"}
            {"name":"site","owner":{"login":"antoninadert"},"url":"https://github.com/antoninadert/site","sshUrl":"","visibility":"PUBLIC","description":"Site","isArchived":true,"isFork":false,"pushedAt":null}
            {"name":"perso","owner":{"login":"SamuelnitraM"},"url":"https://github.com/SamuelnitraM/perso","sshUrl":"","visibility":"PUBLIC","description":null,"isArchived":false,"isFork":false,"pushedAt":null}
            """);

        var repos = await service.ListCollaborationsAsync(Perso);

        Assert.Equal(["SmartDev-Web/stock", "antoninadert/site"], repos.Select(r => r.FullName));
        Assert.Contains("--paginate", runner.Single("api", "user/repos?affiliation=collaborator&per_page=100").Arguments);
        Assert.True(repos[1] is { IsArchived: true, Visibility: RepositoryVisibility.Public });
    }

    [Fact]
    public async Task Leaving_or_removing_uses_the_given_account()
    {
        var (service, runner) = Create();
        runner.When(["api", "--method", "DELETE"], "");

        await service.RemoveCollaboratorAsync(Perso, "SmartDev-Web/stock", Perso.Login);
        await service.CancelInvitationAsync(Pro, "SmartDev-Web/stock", 7);

        var leave = runner.Single("api", "--method", "DELETE", "repos/SmartDev-Web/stock/collaborators/SamuelnitraM");
        Assert.Equal("gho_tokenPerso_0123456789", TokenOf(leave));
        Assert.Equal("gho_tokenPro_0123456789", TokenOf(runner.Single("api", "--method", "DELETE", "repos/SmartDev-Web/stock/invitations/7")));
    }
}

public class SharedProjectPlacementTests
{
    private static readonly GitHubAccount Pro = new("SmartDev-Web", "github.com", true, true);
    private static readonly GitHubAccount Perso = new("SamuelnitraM", "github.com", false, true);

    private static LocalProject Project(string url, string? pushAccount = null) =>
        new(@"C:\p") { Remote = RemoteUrlParser.Parse(url), PushAccount = pushAccount };

    private static CollaborationIndex PersoCollaboratesOn(string fullName)
    {
        var index = new CollaborationIndex();
        index.Add(Perso, fullName);
        return index;
    }

    [Fact]
    public void A_shared_project_belongs_to_its_owner_and_lists_connected_collaborators()
    {
        var placement = ProjectClassifier.Place(Project("https://github.com/SmartDev-Web/stock.git"), [Pro, Perso], PersoCollaboratesOn("smartdev-web/STOCK"));

        Assert.Equal(Pro, placement.Account);
        Assert.Equal([Perso], placement.Collaborators);
        Assert.True(placement.IsCollaborator(Perso));
        Assert.Equal(Pro, placement.NetworkAccount); // le propriétaire envoie par défaut
    }

    [Fact]
    public void The_chosen_push_account_is_used_while_it_still_has_access()
    {
        var url = "https://github.com/SmartDev-Web/stock.git";
        Assert.Equal(Perso, ProjectClassifier.Place(Project(url, "samuelnitram"), [Pro, Perso], PersoCollaboratesOn("SmartDev-Web/stock")).NetworkAccount);
        // Plus collaborateur : retour au propriétaire, sans rien casser
        Assert.Equal(Pro, ProjectClassifier.Place(Project(url, "SamuelnitraM"), [Pro, Perso]).NetworkAccount);
    }

    [Fact]
    public void A_repository_of_an_unconnected_owner_goes_to_its_connected_collaborator()
    {
        var placement = ProjectClassifier.Place(Project("https://github.com/antoninadert/site.git"), [Pro, Perso], PersoCollaboratesOn("antoninadert/site"));

        Assert.Null(placement.Account);
        Assert.Null(placement.Reason); // n'est plus dans « Non liés »
        Assert.Equal(Perso, placement.NetworkAccount);

        Assert.Equal(UnlinkedReason.OtherOwner, ProjectClassifier.Place(Project("https://github.com/antoninadert/site.git"), [Pro, Perso]).Reason);
    }

    [Fact]
    public void The_owner_is_never_its_own_collaborator()
    {
        var index = new CollaborationIndex();
        index.Add(Pro, "SmartDev-Web/stock");
        Assert.Empty(ProjectClassifier.Place(Project("https://github.com/SmartDev-Web/stock.git"), [Pro, Perso], index).Collaborators);
    }
}

public sealed class PushAccountSettingTests : IDisposable
{
    private readonly TempRepo _repo = new("compte d'envoi");

    [Fact]
    public async Task Is_stored_in_the_project_git_config_and_read_by_the_scanner()
    {
        await _repo.WithCommitAsync();
        var git = new GitService(new ProcessRunner());

        Assert.True((await git.SetPushAccountAsync(_repo.Path, null)).Succeeded); // rien à retirer : pas une erreur
        Assert.Null(ProjectScanner.ReadProject(_repo.Path)!.PushAccount);

        Assert.True((await git.SetPushAccountAsync(_repo.Path, "SamuelnitraM")).Succeeded);
        Assert.Equal("SamuelnitraM", ProjectScanner.ReadProject(_repo.Path)!.PushAccount);

        await git.SetPushAccountAsync(_repo.Path, null);
        Assert.Null(ProjectScanner.ReadProject(_repo.Path)!.PushAccount);
    }

    public void Dispose() => _repo.Dispose();
}
