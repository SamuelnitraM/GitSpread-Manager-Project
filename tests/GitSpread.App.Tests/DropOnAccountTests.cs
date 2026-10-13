using GitSpread.App.ViewModels;

namespace GitSpread.App.Tests;

/// <summary>Projet déposé sur l'onglet d'un compte.</summary>
public class DropOnAccountTests
{
    [Fact]
    public async Task A_project_without_remote_opens_publishing_on_the_target_account()
    {
        var host = new TestHost();
        var project = host.Project(); // « Non liés » : pas de remote

        await project.ChangeLinkedRepositoryAsync(TestHost.Perso);

        var publish = Assert.IsType<PublishViewModel>(Assert.Single(host.Shown));
        Assert.Equal(TestHost.Perso.Login, publish.SelectedAccount?.Login);
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
