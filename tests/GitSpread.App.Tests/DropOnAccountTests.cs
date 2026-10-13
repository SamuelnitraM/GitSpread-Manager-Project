using GitSpread.App.ViewModels;

namespace GitSpread.App.Tests;

/// <summary>Projet déposé sur l'onglet d'un compte.</summary>
public class DropOnAccountTests
{
    [Fact]
    public async Task A_project_without_remote_is_filed_under_the_target_account_without_touching_github()
    {
        var host = new TestHost();
        var project = host.Project(); // « Non liés » : pas de remote

        await project.ChangeLinkedRepositoryAsync(TestHost.Perso);

        Assert.Empty(host.Shown); // ni publication ni repointage
        Assert.Empty(host.Runner.Requests); // aucune commande gh ni git
        Assert.Equal("github.com/samuelnitram", host.Settings.ProjectAccount(project.Path));

        await project.AssignToAsync(null); // redéposé sur « Non liés »
        Assert.Null(host.Settings.ProjectAccount(project.Path));
    }

    [Fact]
    public async Task Publishing_a_filed_project_proposes_its_account()
    {
        var host = new TestHost();
        var placement = new Core.Projects.ProjectPlacement(new Core.Projects.LocalProject(@"C:\projets\site") { IsRepository = false }, TestHost.Perso, null)
            { IsAssigned = true };
        var project = new ProjectItemViewModel(placement, host);

        await project.PublishCommand.ExecuteAsync(null);

        Assert.Equal(TestHost.Perso.Login, Assert.IsType<PublishViewModel>(Assert.Single(host.Shown)).SelectedAccount?.Login);
        Assert.True(project is { IsAssigned: true, IsUnlinked: false, ShowAssignHint: false });
        Assert.Contains("rangé dans SamuelnitraM", project.AssignedLabel);
    }

    [Fact]
    public async Task A_linked_project_opens_repointing_on_the_target_account()
    {
        var host = new TestHost { ChooseAnswer = 1 }; // F3A : « Relier le projet à un repo de SamuelnitraM » (0 = le partager)
        var project = host.Project(account: TestHost.Pro, remote: "https://github.com/SmartDev-Web/stock.git");

        await project.ChangeLinkedRepositoryAsync(TestHost.Perso);

        var repoint = Assert.IsType<RepointViewModel>(Assert.Single(host.Shown));
        Assert.Equal(TestHost.Perso.Login, repoint.SelectedAccount?.Login);
    }
}
