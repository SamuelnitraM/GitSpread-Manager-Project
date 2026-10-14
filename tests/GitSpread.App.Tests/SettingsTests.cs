using System.ComponentModel;
using GitSpread.App.ViewModels;
using GitSpread.Core.Git;
using GitSpread.Core.Settings;

namespace GitSpread.App.Tests;

/// <summary>F6d : fenêtre des paramètres et préférences appliquées dans l'interface.</summary>
public sealed class SettingsTests : IDisposable
{
    private const string TwoAccounts = """
        {"hosts":{"github.com":[
          {"state":"success","active":true,"host":"github.com","login":"SmartDev-Web","scopes":"gist, read:org, repo, workflow"},
          {"state":"success","active":false,"host":"github.com","login":"SamuelnitraM","scopes":"gist, read:org, repo, workflow"}]}}
        """;

    private readonly TestHost _host = new();
    private readonly MainViewModel _main;

    public SettingsTests()
    {
        _host.Runner.When(["auth", "status"], TwoAccounts).When(["api"], "[]");
        _main = new MainViewModel(_host.Services, _host);
    }

    // Les préférences d'affichage sont partagées par toute l'interface : on les remet par défaut après chaque test.
    public void Dispose() => DisplayPreferences.Current.Apply(new AppSettings());

    private AppSettings Saved => _host.Services.SettingsStore.Load();

    [Fact]
    public void Dates_are_relative_or_complete()
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var date = now.AddDays(-2).AddHours(-3);

        Assert.Equal("il y a 2 j", DisplayPreferences.Format(date, full: false, now));
        Assert.Equal(date.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), DisplayPreferences.Format(date, full: true, now));
        Assert.Equal("à l'instant", DisplayPreferences.Format(now, full: false, now));
    }

    [Fact]
    public void Each_choice_applies_immediately_and_is_saved()
    {
        var vm = new SettingsViewModel(_main);

        vm.ShowCommands = false;
        vm.FullDates = true;
        vm.NewRepositoriesPublic = true;
        vm.PreferRealEmail = true;
        vm.ShowHistoryPreview = false;
        vm.HistoryPageSize = 500;
        vm.MaintenanceTermsText = "urgent, , Fix ; urgent";

        Assert.False(DisplayPreferences.Current.ShowCommands); // commandes masquées dans les assistants et les comptes rendus
        Assert.True(DisplayPreferences.Current.FullDates);
        Assert.False(_main.ShowHistoryPreview);
        Assert.Equal(["urgent", "Fix"], _main.Settings.MaintenanceTerms);
        var saved = Saved;
        Assert.True(saved is { ShowCommands: false, FullDates: true, NewRepositoriesPrivate: false, PreferRealEmail: true, ShowHistoryPreview: false, HistoryPageSize: 500 });
        Assert.StartsWith("Exemple : ", vm.DateExample);
        Assert.DoesNotContain("il y a", vm.DateExample);

        vm.ResetMaintenanceTermsCommand.Execute(null);
        Assert.Equal(BranchKinds.DefaultMaintenanceTerms, _main.Settings.MaintenanceTerms);
    }

    [Fact]
    public async Task Arborescence_as_main_view_opens_each_tab_once_on_the_large_history()
    {
        await _main.RefreshAsync();
        var vm = new SettingsViewModel(_main);

        vm.StartWithHistory = true;
        var tab = _main.SelectedTab!;
        Assert.True(tab.IsExplorerOpen); // l'onglet affiché bascule tout de suite
        Assert.Equal(MainViews.History, Saved.MainView);

        tab.CloseExplorer(); // « Retour aux projets »
        var other = _main.Tabs.First(t => t != tab);
        _main.SelectedTab = other;
        _main.SelectedTab = tab;
        Assert.True(other.IsExplorerOpen);
        Assert.False(tab.IsExplorerOpen); // déjà montré : la liste reste affichée

        vm.StartWithList = true;
        Assert.Equal(MainViews.List, Saved.MainView);
    }

    [Fact]
    public async Task Folder_changes_trigger_a_new_scan_when_the_window_closes()
    {
        var root = Directory.CreateTempSubdirectory("GitSpread racine ").FullName;
        var vm = new SettingsViewModel(_main, SettingsPage.Projects);
        Assert.True(vm.HasNoRootFolders);

        vm.AddRootFolders([root, root.ToUpperInvariant()]); // doublon ignoré
        vm.ScanDepth = 2;
        vm.ExclusionsText = "node_modules, vendor";
        Assert.Equal([root], Saved.RootFolders);
        Assert.Equal(["node_modules", "vendor"], Saved.ExcludedFolders);
        Assert.DoesNotContain("trouvé", _main.Status); // rien n'est analysé avant la fermeture

        await vm.CloseAsync();
        Assert.Contains("trouvé", _main.Status);
        Assert.False(_main.HasNoRootFolders);

        vm.RemoveRootFolderCommand.Execute(root);
        Assert.Empty(Saved.RootFolders);
        Assert.True(vm.HasNoRootFolders);
    }

    [Fact]
    public async Task A_hidden_project_leaves_the_list_and_comes_back_from_the_settings()
    {
        var project = _host.Project("vieux projet");
        await project.HideCommand.ExecuteAsync(null);
        Assert.Contains("Paramètres › Projets", _host.Confirms.Single());
        Assert.True(_host.Settings.IsHidden(project.Path));

        _main.Settings.SetHidden(project.Path, true);
        var vm = new SettingsViewModel(_main, SettingsPage.Projects);
        Assert.Equal([project.Path], vm.HiddenProjects);
        vm.ShowProjectCommand.Execute(project.Path);
        Assert.True(vm.HasNoHiddenProjects);
        Assert.Empty(Saved.HiddenProjects);
    }

    [Fact]
    public void Collapsed_parts_can_be_forgotten_and_reset()
    {
        var vm = new SettingsViewModel(_main, SettingsPage.Advanced);
        var sections = new PanelSections(_main);
        sections.Links = false;
        Assert.Equal(false, Saved.PanelSections["Links"]);

        vm.RememberSections = false; // l'état actuel reste valable pour la session, mais n'est plus mémorisé
        Assert.False(sections.Links);
        Assert.Empty(Saved.PanelSections);
        sections.Branches = false;
        Assert.Empty(Saved.PanelSections);

        vm.RememberSections = true; // l'état de la session devient l'état mémorisé
        Assert.Equal(new Dictionary<string, bool> { ["Links"] = false, ["Branches"] = false }, Saved.PanelSections);

        var changed = new List<string?>();
        ((INotifyPropertyChanged)sections).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.ResetSectionsCommand.Execute(null);
        Assert.True(sections is { Links: true, Branches: true }); // les panneaux ouverts se redéplient
        Assert.Contains(string.Empty, changed);
        Assert.Empty(Saved.PanelSections);
    }

    [Fact]
    public void Reset_asks_first_then_restores_the_defaults()
    {
        var vm = new SettingsViewModel(_main, SettingsPage.Advanced);
        vm.ShowCommands = false;
        vm.StartWithHistory = true;

        _host.ConfirmAnswer = false;
        vm.ResetPreferencesCommand.Execute(null);
        Assert.False(Saved.ShowCommands);

        _host.ConfirmAnswer = true;
        vm.ResetPreferencesCommand.Execute(null);
        Assert.Contains("conservés", _host.Confirms[^1]);
        Assert.True(Saved is { ShowCommands: true, MainView: MainViews.List });
        Assert.True(vm.ShowCommands);
        Assert.True(DisplayPreferences.Current.ShowCommands);
    }

    [Fact]
    public async Task Lists_accounts_with_their_emails_and_edits_the_known_ones()
    {
        await _main.RefreshAsync();
        var vm = new SettingsViewModel(_main, SettingsPage.Accounts);
        var perso = vm.Accounts.Single(a => a.Login == "SamuelnitraM");
        await perso.LoadAsync();

        Assert.True(perso is { IsActive: false, CanMakeActive: true });
        Assert.Contains("repo", perso.ScopesLabel);
        Assert.True(perso.HasNoKnownEmails);

        _host.PromptAnswers.Enqueue("pas une adresse");
        perso.AddEmailCommand.Execute(null);
        Assert.Equal("Adresse e-mail invalide.", _host.PromptErrors.Single());

        _host.PromptAnswers.Enqueue(" email1@exemple.fr ");
        perso.AddEmailCommand.Execute(null);
        Assert.Equal(["email1@exemple.fr"], perso.KnownEmails);
        Assert.True(Saved.IsKnownEmail("github.com", "SamuelnitraM", "email1@exemple.fr"));

        perso.RemoveEmailCommand.Execute("email1@exemple.fr");
        Assert.True(perso.HasNoKnownEmails);
        Assert.False(Saved.IsKnownEmail("github.com", "SamuelnitraM", "email1@exemple.fr"));
    }

    [Fact]
    public async Task Refuses_an_incomplete_machine_identity()
    {
        var vm = new SettingsViewModel(_main, SettingsPage.Accounts) { GlobalName = "Samuel", GlobalEmail = "sans arobase" };
        await vm.SaveGlobalIdentityCommand.ExecuteAsync(null);
        Assert.True(vm.GlobalIdentityFailed);
        Assert.DoesNotContain(_host.Runner.Requests, r => r.Arguments.Contains("--global") && r.Arguments.Contains("user.email"));
    }

    [Fact]
    public void New_repositories_and_clones_follow_the_preferences()
    {
        _host.Settings.NewRepositoriesPrivate = false;
        var clones = Directory.CreateTempSubdirectory("GitSpread clones ").FullName;
        _host.Settings.CloneFolder = clones;

        Assert.False(new PublishViewModel(_host.Project(), _host).IsPrivate);
        var repo = new Core.GitHub.GitHubRepository("stock", "SmartDev-Web", "https://github.com/SmartDev-Web/stock", "", Core.GitHub.RepositoryVisibility.Private);
        var clone = new CloneViewModel(TestHost.Pro, repo, _host);
        Assert.Equal(clones, clone.ParentFolder);
        Assert.Contains("adresse privée GitHub", clone.SignLabel);

        _host.Settings.PreferRealEmail = true;
        Assert.Contains("adresse réelle", new CloneViewModel(TestHost.Pro, repo, _host).SignLabel);
    }

    [Fact]
    public void Signing_identity_uses_the_preferred_address()
    {
        var user = _host.Users[TestHost.Perso.Login]!;
        Assert.Equal(new GitIdentity("Samuel", user.NoReplyEmail), _host.IdentityFor(TestHost.Perso, user));

        _host.Settings.PreferRealEmail = true;
        _host.Settings.AddKnownEmail("github.com", TestHost.Perso.Login, "email1@exemple.fr");
        Assert.Equal(new GitIdentity("Samuel", "email1@exemple.fr"), _host.IdentityFor(TestHost.Perso, user));
    }
}
