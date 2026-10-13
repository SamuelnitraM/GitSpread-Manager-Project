using GitSpread.App.ViewModels;

namespace GitSpread.App.Tests;

/// <summary>F1 : chaque erreur est portée par son champ, et la vue est sollicitée pour l'afficher.</summary>
public class PublishValidationTests
{
    public PublishValidationTests() => RepositoryNameCheck.Delay = TimeSpan.Zero;

    [Fact]
    public async Task Invalid_characters_are_reported_on_the_name_field_without_asking_github()
    {
        var host = new TestHost();
        var vm = new PublishViewModel(host.Project(), host);
        await TestHost.WaitUntil(() => !vm.IsCheckingName); // vérification du nom proposé à l'ouverture
        var before = host.Runner.Requests.Count;

        vm.RepositoryName = "Écran-accueil";
        await Task.Delay(50);

        Assert.Contains("« É »", vm.RepositoryNameError);
        Assert.Equal(before, host.Runner.Requests.Count); // aucun appel à GitHub pour un nom invalide
    }

    [Fact]
    public async Task A_name_already_taken_on_the_account_turns_the_field_red_while_typing()
    {
        var host = new TestHost();
        host.Runner.When(["repo", "view", "SmartDev-Web/deja-pris"], """{"name":"deja-pris"}""");
        var vm = new PublishViewModel(host.Project(), host);

        vm.RepositoryName = "deja-pris";
        await TestHost.WaitUntil(() => vm.RepositoryNameError is not null);

        Assert.Equal("Le repo SmartDev-Web/deja-pris existe déjà sur ce compte : choisis un autre nom.", vm.RepositoryNameError);
        Assert.False(vm.IsCheckingName);

        vm.RepositoryName = "nom-libre";
        await TestHost.WaitUntil(() => vm.RepositoryNameError is null && !vm.IsCheckingName);
    }

    [Fact]
    public async Task Changing_account_checks_the_name_again()
    {
        var host = new TestHost();
        host.Runner.When(["repo", "view", "SamuelnitraM/mon-projet"], """{"name":"mon-projet"}""");
        var vm = new PublishViewModel(host.Project("mon-projet"), host);
        await TestHost.WaitUntil(() => !vm.IsCheckingName);
        Assert.Null(vm.RepositoryNameError); // libre sur SmartDev-Web

        vm.SelectedAccount = TestHost.Perso;

        await TestHost.WaitUntil(() => vm.RepositoryNameError is not null);
        Assert.Contains("SamuelnitraM/mon-projet existe déjà", vm.RepositoryNameError);
    }

    [Fact]
    public async Task An_empty_name_is_only_flagged_after_trying_to_publish_and_the_view_is_asked_to_show_it()
    {
        var host = new TestHost();
        var vm = new PublishViewModel(host.Project(), host);
        var shown = 0;
        vm.ShowErrorsRequested += (_, _) => shown++;

        vm.RepositoryName = "";
        Assert.Null(vm.RepositoryNameError);

        await vm.PublishCommand.ExecuteAsync(null);

        Assert.Equal("Donne un nom au repo.", vm.RepositoryNameError);
        Assert.Equal(1, shown);
        Assert.Equal(PublishStep.Form, vm.Step);
        Assert.DoesNotContain(host.Runner.Requests, r => r.Arguments.Take(2).SequenceEqual(["repo", "create"]));
    }

    [Fact]
    public async Task Publishing_waits_for_the_name_check_and_refuses_a_taken_name()
    {
        var host = new TestHost();
        host.Runner.When(["repo", "view", "SmartDev-Web/deja-pris"], """{"name":"deja-pris"}""");
        var vm = new PublishViewModel(host.Project(), host);
        RepositoryNameCheck.Delay = TimeSpan.FromSeconds(30); // l'utilisateur clique avant la fin de la pause
        vm.RepositoryName = "deja-pris";

        await vm.PublishCommand.ExecuteAsync(null);

        Assert.Contains("existe déjà", vm.RepositoryNameError);
        Assert.Equal(PublishStep.Form, vm.Step);
    }
}

public class RepointValidationTests
{
    public RepointValidationTests() => RepositoryNameCheck.Delay = TimeSpan.Zero;

    [Fact]
    public async Task Choosing_an_existing_repo_without_selecting_one_flags_the_list()
    {
        var host = new TestHost();
        var vm = new RepointViewModel(host.Project("projet", TestHost.Pro, "https://github.com/SmartDev-Web/projet.git"), host, TestHost.Perso);
        var shown = 0;
        vm.ShowErrorsRequested += (_, _) => shown++;

        vm.CreateNew = false;
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Choisis un repo dans la liste, ou « Créer un nouveau repo ».", vm.RepositoryChoiceError);
        Assert.Null(vm.NewNameError); // le champ du nouveau repo n'est plus concerné
        Assert.Equal(1, shown);
        Assert.Equal(RepointStep.Form, vm.Step);
    }

    [Fact]
    public async Task The_new_repository_name_is_checked_on_the_target_account()
    {
        var host = new TestHost();
        host.Runner.When(["repo", "view", "SamuelnitraM/projet"], """{"name":"projet"}""");

        var vm = new RepointViewModel(host.Project("projet", TestHost.Pro, "https://github.com/SmartDev-Web/projet.git"), host, TestHost.Perso);

        await TestHost.WaitUntil(() => vm.NewNameError is not null);
        Assert.Contains("SamuelnitraM/projet existe déjà", vm.NewNameError);

        vm.NewName = "projet-copie";
        await TestHost.WaitUntil(() => vm.NewNameError is null && !vm.IsCheckingName);
    }
}

public class LoginValidationTests
{
    [Theory]
    [InlineData("", "Indique le nom d'hôte")]
    [InlineData("github entreprise.fr", "pas d'espace")]
    [InlineData("https://github.entreprise.fr/login", "sans chemin")]
    [InlineData("intranet", "il manque le domaine")]
    public void Explains_invalid_hosts(string host, string expected) =>
        Assert.Contains(expected, LoginWizardViewModel.ValidateHost(host));

    [Fact]
    public void A_valid_host_has_no_error() => Assert.Null(LoginWizardViewModel.ValidateHost("github.monentreprise.fr"));

    [Fact]
    public void Next_stays_on_the_question_and_flags_the_host_field()
    {
        var host = new TestHost();
        var vm = new LoginWizardViewModel(host.Services, host.Accounts);
        var shown = 0;
        vm.ShowErrorsRequested += (_, _) => shown++;

        vm.UseGitHubCom = false;
        vm.NextCommand.Execute(null);

        Assert.Equal(LoginStep.Host, vm.Step);
        Assert.NotNull(vm.HostError);
        Assert.Equal(1, shown);

        vm.CustomHost = "github.monentreprise.fr"; // l'erreur disparaît pendant la saisie
        Assert.Null(vm.HostError);
        vm.NextCommand.Execute(null);
        Assert.Equal(LoginStep.Protocol, vm.Step);
    }
}
