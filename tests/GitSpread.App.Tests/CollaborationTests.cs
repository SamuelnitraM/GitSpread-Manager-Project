using GitSpread.App.ViewModels;
using GitSpread.Core.GitHub;
using GitSpread.Core.Processes;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GitSpread.App.Tests;

/// <summary>F3A : un repo, l'autre compte en collaborateur.</summary>
public sealed class CollaborationTests : IDisposable
{
    private static readonly GitHubRepository Stock = new("stock", "SmartDev-Web", "https://github.com/SmartDev-Web/stock", "", RepositoryVisibility.Private);
    private readonly TempRepo _local = new("projet partagé");

    private static TestHost Host(bool realGit = false)
    {
        var host = new TestHost(realGit);
        host.Runner
            .When(["auth", "token", "--hostname", "github.com", "--user", TestHost.Pro.Login], "gho_tokenPro_0123456789")
            .When(["auth", "token", "--hostname", "github.com", "--user", TestHost.Perso.Login], "gho_tokenPerso_0123456789")
            .When(["api", "--method", "PUT"], """{"id":99}""")
            .When(["api", "--method", "PATCH"], "")
            .When(["api", "--method", "DELETE"], "");
        return host;
    }

    private static string Details(string permission) => $$"""
        {"name":"stock","owner":{"login":"SmartDev-Web"},"visibility":"PRIVATE","defaultBranchRef":{"name":"main"},"repositoryTopics":[],
         "isArchived":false,"isFork":false,"url":"https://github.com/SmartDev-Web/stock","viewerPermission":"{{permission}}"}
        """;

    private static string TokenOf(ProcessRequest request) => request.Environment!["GH_TOKEN"];

    [Fact]
    public async Task Dropping_a_linked_project_on_another_account_can_share_the_repository()
    {
        var host = Host();
        host.ChooseAnswer = 0; // « Donner accès à SamuelnitraM (collaborateur) »
        var project = host.Project(account: TestHost.Pro, remote: "https://github.com/SmartDev-Web/stock.git");

        await project.ChangeLinkedRepositoryAsync(TestHost.Perso);

        Assert.Equal("gho_tokenPro_0123456789", TokenOf(host.Runner.Single("api", "--method", "PUT", "repos/SmartDev-Web/stock/collaborators/SamuelnitraM")));
        Assert.Equal("gho_tokenPerso_0123456789", TokenOf(host.Runner.Single("api", "--method", "PATCH", "user/repository_invitations/99")));
        Assert.True(project.LastAction!.Succeeded, project.LastAction.Message);
        Assert.Contains("SamuelnitraM est maintenant collaborateur", project.LastAction.Message);
        Assert.Equal(1, host.CollaborationReloads);
        Assert.Empty(host.Shown); // pas d'assistant de repointage
    }

    [Fact]
    public async Task Dropping_on_an_account_that_already_collaborates_no_longer_offers_to_share()
    {
        var host = Host();
        host.ChooseAnswer = 1; // F3B : 0 = ajouter un repo de SamuelnitraM, 1 = remplacer le lien (plus de « Donner accès »)
        var placement = new ProjectPlacement(new LocalProject(_local.Path) { Remote = Core.Git.RemoteUrlParser.Parse("https://github.com/SmartDev-Web/stock.git"), RemoteName = "origin" },
            TestHost.Pro, null) { Collaborators = [TestHost.Perso] };

        await new ProjectItemViewModel(placement, host).ChangeLinkedRepositoryAsync(TestHost.Perso);

        Assert.IsType<RepointViewModel>(Assert.Single(host.Shown));
    }

    [Fact]
    public async Task The_owner_panel_lists_and_manages_collaborators()
    {
        var host = Host();
        host.Runner.When(["repo", "view", "SmartDev-Web/stock"], Details("ADMIN"))
            .When(["api", "repos/SmartDev-Web/stock/collaborators?affiliation=direct&per_page=100"],
                """[{"login":"SmartDev-Web","role_name":"admin"},{"login":"une-amie","role_name":"write"}]""")
            .When(["api", "repos/SmartDev-Web/stock/invitations?per_page=100"], "[]");
        var panel = new GitHubRepositoryViewModel(new AccountTabViewModel(TestHost.Pro, host, NullLogger.Instance), Stock, host, NullLogger.Instance);
        await panel.LoadAsync();

        Assert.Equal(["une-amie"], panel.Collaborators.Select(c => c.Login));
        Assert.True(panel is { CanManageCollaborators: true, IsViewerCollaborator: false, CollaboratorsLabel: "1 collaborateur :" });

        host.ChooseAnswer = 0; // SamuelnitraM, compte connecté
        await panel.InviteCollaboratorCommand.ExecuteAsync(null);
        Assert.Contains("SamuelnitraM est maintenant collaborateur", panel.LastAction!.Message);

        await panel.RemoveCollaboratorCommand.ExecuteAsync(panel.Collaborators[0]); // TestHost.Confirm : oui
        host.Runner.Single("api", "--method", "DELETE", "repos/SmartDev-Web/stock/collaborators/une-amie");
        Assert.Equal(2, host.CollaborationReloads);
    }

    [Fact]
    public async Task Inviting_someone_outside_gitspread_asks_for_a_valid_login()
    {
        var host = Host();
        host.Runner.When(["repo", "view", "SmartDev-Web/stock"], Details("ADMIN"))
            .When(["api", "repos/SmartDev-Web/stock/collaborators?affiliation=direct&per_page=100"], "[]")
            .When(["api", "repos/SmartDev-Web/stock/invitations?per_page=100"], "[]");
        var panel = new GitHubRepositoryViewModel(new AccountTabViewModel(TestHost.Pro, host, NullLogger.Instance), Stock, host, NullLogger.Instance);
        await panel.LoadAsync();

        host.ChooseAnswer = 1; // « Un autre utilisateur GitHub… » (SamuelnitraM est l'option 0)
        host.PromptAnswers.Enqueue("-pas valide");
        await panel.InviteCollaboratorCommand.ExecuteAsync(null);
        host.PromptAnswers.Enqueue("une-amie");
        await panel.InviteCollaboratorCommand.ExecuteAsync(null);

        Assert.Contains("invalide", host.PromptErrors.Single());
        host.Runner.Single("api", "--method", "PUT", "repos/SmartDev-Web/stock/collaborators/une-amie");
        Assert.DoesNotContain(host.Runner.Requests, r => r.Arguments.Contains("PATCH")); // personne d'extérieur : elle accepte elle-même
        Assert.Contains("Invitation envoyée à une-amie", panel.LastAction!.Message);
    }

    [Fact]
    public async Task A_collaborator_can_leave_the_repository_with_its_own_account()
    {
        var host = Host();
        host.Runner.When(["repo", "view", "SmartDev-Web/stock"], Details("WRITE"))
            .When(["api", "repos/SmartDev-Web/stock/collaborators?affiliation=direct&per_page=100"], """[{"login":"SamuelnitraM","role_name":"write"}]""");
        var tab = new AccountTabViewModel(TestHost.Perso, host, NullLogger.Instance);
        var panel = new GitHubRepositoryViewModel(tab, Stock, host, NullLogger.Instance);
        await panel.LoadAsync();

        Assert.True(panel is { IsViewerCollaborator: true, CanLeave: true, CanManageCollaborators: false });
        Assert.DoesNotContain(host.Runner.Requests, r => r.Arguments.Any(a => a.Contains("/invitations"))); // réservé aux administrateurs

        await panel.LeaveCommand.ExecuteAsync(null);

        Assert.Equal("gho_tokenPerso_0123456789", TokenOf(host.Runner.Single("api", "--method", "DELETE", "repos/SmartDev-Web/stock/collaborators/SamuelnitraM")));
        Assert.Null(tab.RepositoryPanel);
    }

    [Fact]
    public async Task A_shared_project_chooses_which_account_pushes()
    {
        await _local.WithCommitAsync();
        await _local.Git("remote", "add", "origin", "https://github.com/SmartDev-Web/stock.git");
        var host = Host(realGit: true);
        var placement = new ProjectPlacement(ProjectScanner.ReadProject(_local.Path)!, TestHost.Pro, null) { Collaborators = [TestHost.Perso] };
        var owner = new ProjectItemViewModel(placement, host) { ViewingAccount = TestHost.Pro };
        var collaborator = new ProjectItemViewModel(placement, host) { ViewingAccount = TestHost.Perso };

        Assert.True(owner is { IsShared: true, IsViewedAsCollaborator: false, SharingBadge: "Partagé", HasPushAccountChoice: true });
        Assert.True(collaborator is { IsViewedAsCollaborator: true, SharingBadge: "Collaborateur" });
        Assert.Equal(["SmartDev-Web (propriétaire)", "SamuelnitraM (collaborateur)"], owner.PushAccountChoices.Select(c => c.Label));
        Assert.Equal("Échanges avec GitHub en tant que SmartDev-Web.", owner.NetworkAccountLabel);

        owner.SelectedPushAccount = owner.PushAccountChoices[1];
        await TestHost.WaitUntil(() => owner.LastAction is not null);

        Assert.True(owner.LastAction!.Succeeded, owner.LastAction.Message);
        Assert.Equal("SamuelnitraM", await _local.Git("config", "--local", "gitspread.pushAccount"));
        var rescanned = new ProjectItemViewModel(placement with { Project = ProjectScanner.ReadProject(_local.Path)! }, host);
        Assert.Equal("Échanges avec GitHub en tant que SamuelnitraM (collaborateur).", rescanned.NetworkAccountLabel);
    }

    [Fact]
    public async Task The_account_tab_shows_collaborations_and_received_invitations()
    {
        var host = Host();
        host.Runner.When(["repo", "list", "SamuelnitraM"], "[]")
            .When(["api", "user/repos?affiliation=collaborator&per_page=100"],
                """{"name":"stock","owner":{"login":"SmartDev-Web"},"url":"https://github.com/SmartDev-Web/stock","sshUrl":"","visibility":"PRIVATE","isArchived":false,"isFork":false}""")
            .When(["api", "user/repository_invitations?per_page=100"],
                """[{"id":5,"repository":{"full_name":"ClocloDevo/Projet"},"invitee":{"login":"SamuelnitraM"},"inviter":{"login":"ClocloDevo"},"expired":false}]""");
        var tab = new AccountTabViewModel(TestHost.Perso, host, NullLogger.Instance);

        await tab.LoadRepositoriesAsync();

        var repo = Assert.Single(tab.Repositories);
        Assert.True(repo is { IsCollaboration: true, OwnerLabel: "Repo de SmartDev-Web" });
        Assert.Equal(["SmartDev-Web/stock"], tab.Collaborations.Select(r => r.FullName));
        Assert.Equal("1 invitation à collaborer reçue", tab.ReceivedInvitationsLabel);

        await tab.AcceptInvitationCommand.ExecuteAsync(tab.ReceivedInvitations[0]);
        Assert.Equal("gho_tokenPerso_0123456789", TokenOf(host.Runner.Single("api", "--method", "PATCH", "user/repository_invitations/5")));
        Assert.Equal(1, host.CollaborationReloads);
    }

    public void Dispose() => _local.Dispose();
}
