using GitSpread.App.ViewModels;
using GitSpread.Core.Git;
using GitSpread.Core.Projects;
using GitSpread.Tests.Fakes;

namespace GitSpread.App.Tests;

/// <summary>F3B : un projet lié à plusieurs repos.</summary>
public sealed class LinksTests : IDisposable
{
    private readonly TempRepo _repo = new("plusieurs liens");

    /// <summary>Projet poussé sur « principal », avec un second lien « second » vers un dépôt vide.</summary>
    private async Task<(TestHost Host, ProjectItemViewModel Project, string Main, string Second)> TwoLinksAsync()
    {
        var main = await _repo.BareAsync("principal.git");
        var second = await _repo.BareAsync("second.git");
        await _repo.WithCommitAsync();
        await _repo.Git("remote", "add", "origin", main);
        await _repo.Git("push", "-q", "-u", "origin", "main");
        await _repo.Git("remote", "add", "second", second);
        var host = new TestHost(realGit: true);
        var project = new ProjectItemViewModel(ProjectClassifier.Place(ProjectScanner.ReadProject(_repo.Path)!, host.Accounts), host);
        await project.LoadDetailsAsync();
        return (host, project, main, second);
    }

    [Fact]
    public async Task Pushing_to_several_links_always_asks_first_then_remembers_the_selection()
    {
        var (host, project, main, second) = await TwoLinksAsync();
        Assert.Equal(["origin", "second"], project.LinkRows.Select(l => l.Name));
        Assert.True(project.LinkRows[0] is { IsSelected: true, IsPrimary: true });
        Assert.False(project.LinkRows[1].IsSelected); // le principal seul par défaut
        Assert.Equal("main n'a encore jamais été envoyée ici", project.LinkRows[1].StateLabel);

        await _repo.CommitFileAsync("nouveau.txt", "n", "Nouveau travail");
        project.LinkRows[1].IsSelected = true;
        Assert.Contains("2 repos", project.PushTargetsLabel);

        host.ConfirmAnswer = false;
        await project.PushCommand.ExecuteAsync(null);
        Assert.Contains("Envoyer la branche main vers 2 repos", Assert.Single(host.Confirms));
        Assert.Contains("annulé", project.LastAction!.Message);
        Assert.NotEqual(await _repo.Git("rev-parse", "main"), await _repo.GitIn(main, "rev-parse", "main"));

        host.ConfirmAnswer = true;
        await project.PushCommand.ExecuteAsync(null);
        Assert.True(project.LastAction!.Succeeded, project.LastAction.Message);
        var head = await _repo.Git("rev-parse", "main");
        Assert.Equal(head, await _repo.GitIn(main, "rev-parse", "main"));
        Assert.Equal(head, await _repo.GitIn(second, "rev-parse", "main"));
        Assert.Equal("origin/main", await _repo.Git("rev-parse", "--abbrev-ref", "main@{upstream}")); // le suivi reste sur le principal

        // Sélection mémorisée : un nouvel affichage du projet (après analyse) la retrouve.
        var reloaded = new ProjectItemViewModel(ProjectClassifier.Place(ProjectScanner.ReadProject(_repo.Path)!, host.Accounts), host);
        await reloaded.LoadDetailsAsync();
        Assert.All(reloaded.LinkRows, l => Assert.True(l.IsSelected));
        Assert.All(reloaded.LinkRows, l => Assert.Equal("à jour (au dernier « Récupérer »)", l.StateLabel));
    }

    [Fact]
    public async Task Pushing_to_the_primary_alone_asks_nothing()
    {
        var (host, project, main, _) = await TwoLinksAsync();
        await _repo.CommitFileAsync("nouveau.txt", "n", "Nouveau travail");

        await project.PushCommand.ExecuteAsync(null);

        Assert.Empty(host.Confirms);
        Assert.Equal(await _repo.Git("rev-parse", "main"), await _repo.GitIn(main, "rev-parse", "main"));
    }

    [Fact]
    public async Task A_secondary_link_can_be_renamed_made_primary_and_removed()
    {
        var (host, project, main, second) = await TwoLinksAsync();

        host.PromptAnswers.Enqueue("perso");
        await project.RenameLinkCommand.ExecuteAsync(project.LinkRows[1]);
        Assert.Equal(second, await _repo.Git("remote", "get-url", "perso"));

        var renamed = new ProjectItemViewModel(ProjectClassifier.Place(ProjectScanner.ReadProject(_repo.Path)!, host.Accounts), host);
        await renamed.LoadDetailsAsync();
        await renamed.MakePrimaryCommand.ExecuteAsync(renamed.LinkRows[1]);
        Assert.True(renamed.LastAction!.Succeeded, renamed.LastAction.Message);
        Assert.Equal(second, await _repo.Git("remote", "get-url", "origin"));
        var oldPrimary = ProjectScanner.ReadProject(_repo.Path)!.Remotes.Single(r => r.Name != "origin");
        Assert.Equal(main, oldPrimary.Url.Url);

        var swapped = new ProjectItemViewModel(ProjectClassifier.Place(ProjectScanner.ReadProject(_repo.Path)!, host.Accounts), host);
        await swapped.LoadDetailsAsync();
        await swapped.RemoveLinkCommand.ExecuteAsync(swapped.LinkRows[1]);
        Assert.Equal("origin", await _repo.Git("remote"));
        Assert.True(Directory.Exists(main)); // le repo lui-même n'est pas touché
    }

    [Fact]
    public async Task Dropping_a_linked_project_on_another_account_offers_a_second_repository()
    {
        var host = new TestHost { ChooseAnswer = 1 }; // 0 = collaborateur, 1 = ajouter un repo, 2 = remplacer
        var project = host.Project(account: TestHost.Pro, remote: "https://github.com/SmartDev-Web/stock.git");

        await project.ChangeLinkedRepositoryAsync(TestHost.Perso);

        var dialog = Assert.IsType<RepointViewModel>(Assert.Single(host.Shown));
        Assert.True(dialog is { IsAddLink: true, LinkName: "samuelnitram", ShowOldLinkChoice: false, CanNativeTransfer: false, SignWithTarget: false });
        Assert.Equal(TestHost.Perso.Login, dialog.SelectedAccount?.Login);
        Assert.Contains("Ajouter un lien", dialog.Title);
    }

    [Fact]
    public async Task The_other_linked_account_can_be_credited_as_co_author()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("config", "user.email", "1001+SmartDev-Web@users.noreply.github.com"); // signé par SmartDev-Web
        _repo.Write("nouveau.txt", "n");
        var host = new TestHost(realGit: true);
        var placement = new ProjectPlacement(new LocalProject(_repo.Path)
        {
            RemoteName = "origin",
            Remote = RemoteUrlParser.Parse("https://github.com/SmartDev-Web/stock.git"),
        }, TestHost.Pro, null)
        {
            SecondaryLinks = [new ProjectLink("samuelnitram", RemoteUrlParser.Parse("https://github.com/SamuelnitraM/stock.git"), TestHost.Perso, false)],
        };
        var commit = new CommitViewModel(new ProjectItemViewModel(placement, host), host) { Message = "Ajoute un fichier" };
        await commit.LoadAsync();

        var coAuthor = Assert.Single(commit.CoAuthors);
        Assert.False(coAuthor.IsChecked); // décoché par défaut
        Assert.Equal("Ajoute un fichier", commit.FinalMessage);

        coAuthor.IsChecked = true;
        Assert.Equal("Ajoute un fichier\n\nCo-authored-by: Samuel <2002+SamuelnitraM@users.noreply.github.com>", commit.FinalMessage);
    }

    public void Dispose() => _repo.Dispose();
}
