using GitSpread.App.ViewModels;
using GitSpread.Core.Accounts;

namespace GitSpread.App.Tests;

/// <summary>F5 : fenêtre « Autorisations du compte ».</summary>
public class AccountScopesTests
{
    private const string StatusAfter = """
        {"hosts":{"github.com":[
          {"state":"success","active":true,"host":"github.com","login":"SmartDev-Web","scopes":"gist, read:org, repo, workflow"},
          {"state":"success","active":false,"host":"github.com","login":"SamuelnitraM","scopes":"gist, read:org, repo, workflow, delete_repo"}]}}
        """;

    private static readonly GitHubAccount Perso = TestHost.Perso with { Scopes = "gist, read:org, repo, workflow" };

    [Fact]
    public void Lists_granted_scopes_and_locks_the_ones_gh_always_needs()
    {
        var host = new TestHost();
        var vm = new AccountScopesViewModel(Perso, host.Services, [TestHost.Pro, Perso]);

        Assert.True(vm.Scopes.Single(s => s.Name == "repo") is { IsSelected: true, CanChange: false });
        Assert.True(vm.Scopes.Single(s => s.Name == "workflow") is { IsGranted: true, CanChange: true });
        Assert.True(vm.Scopes.Single(s => s.Name == ScopeCatalog.DeleteRepo) is { IsGranted: false, IsSelected: false });
        Assert.False(vm.HasChanges);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void A_requested_scope_is_pre_checked_and_the_preview_switches_accounts_around_the_refresh()
    {
        var host = new TestHost();
        var vm = new AccountScopesViewModel(Perso, host.Services, [TestHost.Pro, Perso], [ScopeCatalog.DeleteRepo], "Pour supprimer un repo…");

        Assert.Equal([ScopeCatalog.DeleteRepo], vm.ToAdd);
        Assert.True(vm.HasReason);
        Assert.Equal(
        [
            "gh auth switch --hostname github.com --user SamuelnitraM   (compte actif le temps de l'opération)",
            "gh auth refresh --hostname github.com --scopes delete_repo   (validation dans le navigateur)",
            "gh auth switch --hostname github.com --user SmartDev-Web   (retour au compte actif)",
        ], vm.Preview);

        vm.Scopes.Single(s => s.Name == "workflow").IsSelected = false;
        Assert.Equal(["workflow"], vm.ToRemove);
        Assert.Contains(vm.Preview, l => l.Contains("--remove-scopes workflow"));
    }

    [Fact]
    public async Task Applying_shows_the_device_code_then_reports_the_new_scopes()
    {
        var host = new TestHost();
        host.Runner.When(["auth", "status"], StatusAfter)
            .When(["auth", "switch"], "")
            .When(["auth", "refresh"], "", stderr: "! One-time code (WX12-YZ34) copied to clipboard\nOpen this URL to continue in your web browser: https://github.com/login/device\n");
        var vm = new AccountScopesViewModel(Perso, host.Services, [TestHost.Pro, Perso], [ScopeCatalog.DeleteRepo]);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal("WX12-YZ34", vm.DeviceCode);
        Assert.Equal(ScopesStep.Done, vm.Step);
        Assert.True(vm.Completed);
        Assert.Contains("Suppression de repos", vm.ResultMessage);
    }

    [Fact]
    public async Task Validating_with_the_wrong_browser_account_is_explained()
    {
        var host = new TestHost();
        host.Runner.When(["auth", "status"], StatusAfter)
            .When(["auth", "switch"], "")
            .When(["auth", "refresh"], "", exitCode: 1,
                stderr: "error refreshing credentials for SamuelnitraM, received credentials for SmartDev-Web, did you use the correct account in the browser?");
        var vm = new AccountScopesViewModel(Perso, host.Services, [TestHost.Pro, Perso], [ScopeCatalog.DeleteRepo]);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(ScopesStep.Failed, vm.Step);
        Assert.False(vm.Completed);
        Assert.Contains("fenêtre privée", vm.ResultMessage);
    }
}
