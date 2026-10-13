using GitSpread.App.ViewModels;
using GitSpread.Tests.Fakes;

namespace GitSpread.App.Tests;

/// <summary>F2 : choisir qui signe les commits d'un projet, et re-signer ce qui n'est pas encore envoyé.</summary>
public sealed class ProjectIdentityTests : IDisposable
{
    private readonly TempRepo _repo = new("identite app");

    private async Task<(TestHost Host, ProjectItemViewModel Project)> LinkedToProAsync()
    {
        await _repo.WithCommitAsync(); // commit signé « test@gitspread.local », jamais envoyé
        await _repo.Git("remote", "add", "origin", "https://github.com/SmartDev-Web/projet.git");
        var host = new TestHost(realGit: true);
        var project = host.Project("projet", TestHost.Pro, "https://github.com/SmartDev-Web/projet.git", _repo.Path);
        await project.LoadIdentityAsync();
        return (host, project);
    }

    [Fact]
    public async Task Offers_every_account_and_flags_an_identity_GitHub_will_not_attribute_to_the_owner()
    {
        var (_, project) = await LinkedToProAsync();

        var labels = project.IdentityChoices.Select(c => c.Label).ToList();
        Assert.Contains("SmartDev-Web · adresse privée GitHub (recommandé)", labels);
        Assert.Contains("SamuelnitraM · adresse privée GitHub (recommandé)", labels);
        Assert.Equal("Personnalisée…", labels[^1]);

        // L'identité locale « Test » ne correspond à aucun compte : elle apparaît comme « Actuelle » et est signalée.
        Assert.Equal(IdentityChoiceKind.Current, project.SelectedIdentityChoice!.Kind);
        Assert.True(project.IdentityMismatch);
        Assert.Contains("ne seront pas attribués à SmartDev-Web", project.IdentityStatus);
    }

    [Fact]
    public async Task Choosing_an_account_signs_future_commits_and_re_signs_the_unpushed_ones()
    {
        var (host, project) = await LinkedToProAsync();
        var noreply = project.IdentityChoices.Single(c => c.Label.StartsWith("SmartDev-Web · adresse privée"));

        project.SelectedIdentityChoice = noreply; // TestHost.Confirm répond oui à « Re-signer ? »
        await TestHost.WaitUntil(() => project.LastAction?.Title == "Re-signer les commits" && !project.IsBusy && project.ForeignUnpushedCount == 0, 20000);

        Assert.Equal("1001+SmartDev-Web@users.noreply.github.com", await RawGit("config", "--local", "user.email"));
        Assert.Equal("1001+SmartDev-Web@users.noreply.github.com", await RawGit("log", "-1", "--format=%ae"));
        Assert.Equal(0, project.ForeignUnpushedCount);
        Assert.True(project.IdentityMatches);
        Assert.Contains("attribués à SmartDev-Web", project.IdentityStatus);
        Assert.Contains("re-signé", project.LastAction!.Message);
    }

    [Fact]
    public async Task A_confirmed_real_email_is_offered_and_recognised()
    {
        await _repo.WithCommitAsync();
        await _repo.Git("config", "user.email", "smart@exemple.fr");
        var host = new TestHost(realGit: true);
        host.Settings.AddKnownEmail("github.com", "SmartDev-Web", "smart@exemple.fr");
        var project = host.Project("projet", TestHost.Pro, "https://github.com/SmartDev-Web/projet.git", _repo.Path);

        await project.LoadIdentityAsync();

        Assert.Contains(project.IdentityChoices, c => c.Label == "SmartDev-Web · smart@exemple.fr");
        Assert.True(project.IdentityMatches);
    }

    [Fact]
    public async Task Machine_identity_removes_the_project_setting()
    {
        var (_, project) = await LinkedToProAsync();
        var machine = project.IdentityChoices.FirstOrDefault(c => c.Kind == IdentityChoiceKind.Machine);
        if (machine is null) return; // poste sans identité globale (ex. CI) : rien à vérifier

        project.SelectedIdentityChoice = machine;
        await TestHost.WaitUntil(() => project.LastAction is not null && !project.IsBusy, 15000);

        var r = await new Core.Processes.ProcessRunner().RunAsync(new Core.Processes.ProcessRequest("git", ["config", "--local", "--get", "user.email"]) { WorkingDirectory = _repo.Path });
        Assert.False(r.Succeeded); // plus de réglage propre au projet
    }

    /// <summary>git sans les « -c user.* » ajoutés par TempRepo (qui masqueraient la vraie configuration).</summary>
    private async Task<string> RawGit(params string[] args) =>
        (await new Core.Processes.ProcessRunner().RunAsync(new Core.Processes.ProcessRequest("git", args) { WorkingDirectory = _repo.Path })).StandardOutput.Trim();

    public void Dispose() => _repo.Dispose();
}

public class RepointIdentityTests
{
    public RepointIdentityTests() => RepositoryNameCheck.Delay = TimeSpan.Zero;

    [Fact]
    public void Moving_to_another_account_proposes_its_identity_by_default()
    {
        var host = new TestHost();
        var vm = new RepointViewModel(host.Project("projet", TestHost.Pro, "https://github.com/SmartDev-Web/projet.git"), host, TestHost.Perso);

        Assert.True(vm.SignWithTarget);
        Assert.Contains("en tant que SamuelnitraM", vm.SignWithTargetLabel);
        Assert.Contains(vm.Preview, l => l.Contains("identité de SamuelnitraM"));

        vm.SelectedAccount = TestHost.Pro; // même compte : l'identité ne change pas par défaut
        Assert.False(vm.SignWithTarget);
        Assert.DoesNotContain(vm.Preview, l => l.Contains("identité de"));
    }
}
