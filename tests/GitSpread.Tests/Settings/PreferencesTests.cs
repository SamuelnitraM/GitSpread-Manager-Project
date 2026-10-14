using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Projects;
using GitSpread.Core.Settings;
using GitSpread.Tests.Fakes;

namespace GitSpread.Tests.Settings;

/// <summary>F6d : préférences de la fenêtre des paramètres.</summary>
public class PreferencesTests
{
    private static readonly string[] Git = ["--no-optional-locks", "-c", "core.quotepath=false"];

    [Fact]
    public void Preferences_have_beginner_friendly_defaults_and_survive_a_round_trip()
    {
        var defaults = new AppSettings();
        Assert.Equal(MainViews.List, defaults.MainView);
        Assert.True(defaults is { ShowCommands: true, NewRepositoriesPrivate: true, PreferRealEmail: false, FullDates: false, RememberSections: true });
        Assert.Equal(BranchKinds.DefaultMaintenanceTerms, defaults.MaintenanceTerms);

        var settings = new AppSettings { MainView = MainViews.History, FullDates = true, MaintenanceTerms = ["urgent"], CloneFolder = @"D:\clones" };
        settings.SetHidden(@"C:\projets\vieux", true);
        settings.SessionSections["Links"] = false; // jamais écrit sur le disque
        var path = Path.Combine(Directory.CreateTempSubdirectory("GitSpread prefs ").FullName, "settings.json");
        new SettingsStore(path).Save(settings);
        var loaded = new SettingsStore(path).Load();

        Assert.Equal(MainViews.History, loaded.MainView);
        Assert.True(loaded.FullDates);
        Assert.Equal(["urgent"], loaded.MaintenanceTerms);
        Assert.Equal(@"D:\clones", loaded.CloneFolder);
        Assert.True(loaded.IsHidden(@"C:\PROJETS\vieux"));
        Assert.Empty(loaded.SessionSections);
        Assert.DoesNotContain("SessionSections", File.ReadAllText(path));
    }

    [Fact]
    public void Reset_restores_preferences_but_keeps_projects_folders_and_emails()
    {
        var settings = new AppSettings
        {
            RootFolders = [@"C:\xampp\htdocs"], ExtraProjects = [@"D:\outil"], TabOrder = ["unlinked"],
            Theme = "Dark", MainView = MainViews.History, ShowCommands = false, HistoryPageSize = 1000, ScanDepth = 2,
            ExcludedFolders = ["rien"], MaintenanceTerms = ["urgent"], PreferRealEmail = true, RememberSections = false,
            PanelSections = { ["Links"] = false },
        };
        settings.SetHidden(@"C:\projets\vieux", true);
        settings.AddKnownEmail("github.com", "SamuelnitraM", "email1@exemple.fr");
        settings.SetProjectAccount(@"C:\projets\sans-remote", "github.com/samuelnitram");

        settings.ResetPreferences();

        var defaults = new AppSettings();
        Assert.Equal((defaults.Theme, defaults.MainView, defaults.ShowCommands, defaults.HistoryPageSize, defaults.ScanDepth, defaults.PreferRealEmail, defaults.RememberSections),
            (settings.Theme, settings.MainView, settings.ShowCommands, settings.HistoryPageSize, settings.ScanDepth, settings.PreferRealEmail, settings.RememberSections));
        Assert.Equal(defaults.ExcludedFolders, settings.ExcludedFolders);
        Assert.Equal(defaults.MaintenanceTerms, settings.MaintenanceTerms);
        Assert.Empty(settings.PanelSections);
        Assert.Equal([@"C:\xampp\htdocs"], settings.RootFolders);
        Assert.Equal([@"D:\outil"], settings.ExtraProjects);
        Assert.Equal(["unlinked"], settings.TabOrder);
        Assert.True(settings.IsHidden(@"C:\projets\vieux"));
        Assert.True(settings.IsKnownEmail("github.com", "SamuelnitraM", "email1@exemple.fr"));
        Assert.Equal("github.com/samuelnitram", settings.ProjectAccount(@"C:\projets\sans-remote"));
    }

    [Fact]
    public void Signing_email_follows_the_preference_and_falls_back_to_the_private_address()
    {
        const string noReply = "2002+SamuelnitraM@users.noreply.github.com";
        var settings = new AppSettings();
        Assert.Equal(noReply, settings.SigningEmail("github.com", "SamuelnitraM", noReply, "public@exemple.fr"));

        settings.PreferRealEmail = true;
        Assert.Equal(noReply, settings.SigningEmail("github.com", "SamuelnitraM", noReply, null)); // aucune adresse réelle connue
        Assert.Equal("public@exemple.fr", settings.SigningEmail("github.com", "SamuelnitraM", noReply, "public@exemple.fr"));

        settings.AddKnownEmail("github.com", "SamuelnitraM", "email1@exemple.fr"); // confirmée dans GitSpread : prioritaire
        Assert.Equal("email1@exemple.fr", settings.SigningEmail("github.com", "SamuelnitraM", noReply, "public@exemple.fr"));
        Assert.Equal(["email1@exemple.fr", "public@exemple.fr"], settings.RealEmails("github.com", "SamuelnitraM", "public@exemple.fr"));

        settings.RemoveKnownEmail("github.com", "samuelnitram", "EMAIL1@exemple.fr");
        Assert.False(settings.IsKnownEmail("github.com", "SamuelnitraM", "email1@exemple.fr"));
    }

    [Fact]
    public async Task Reads_and_changes_the_machine_identity_and_the_tool_versions()
    {
        var runner = new FakeProcessRunner()
            .When([.. Git, "config", "--global", "--get", "user.name"], "Samuel Martin\n")
            .When([.. Git, "config", "--global", "--get", "user.email"], "", exitCode: 1)
            .When([.. Git, "config", "--global", "user.name"], "")
            .When([.. Git, "config", "--global", "user.email"], "")
            .When([.. Git, "--version"], "git version 2.47.1.windows.1\n")
            .When(["--version"], "gh version 2.62.0 (2024-11-14)\nhttps://github.com/cli/cli/releases/tag/v2.62.0\n");
        var git = new GitService(runner);
        var gh = new GhCli(runner);

        Assert.Equal(new GitIdentity("Samuel Martin", null), await git.GetGlobalIdentityAsync());
        Assert.All(await git.SetGlobalIdentityAsync("Samuel", "email1@exemple.fr"), r => Assert.True(r.Succeeded));
        Assert.Equal([.. Git, "config", "--global", "user.email", "email1@exemple.fr"], runner.Single([.. Git, "config", "--global", "user.email"]).Arguments);
        Assert.Equal("2.47.1.windows.1", await git.GetVersionAsync());
        Assert.Equal("2.62.0 (2024-11-14)", await new GitHubService(gh, new AccountService(gh)).GetCliVersionAsync());
    }

    [Fact]
    public void Clearing_the_cache_removes_read_only_git_objects()
    {
        var root = Path.Combine(Directory.CreateTempSubdirectory("GitSpread cache ").FullName, "cache");
        var objects = Directory.CreateDirectory(Path.Combine(root, "github.com", "moi", "repo", "objects", "pack")).FullName;
        var pack = Path.Combine(objects, "pack-1.pack");
        File.WriteAllBytes(pack, new byte[2048]);
        File.SetAttributes(pack, FileAttributes.ReadOnly); // comme git
        var cache = new RepositoryCache(new GitService(new FakeProcessRunner()), new GitCredentialResolver(new AccountService(new GhCli(new FakeProcessRunner()))), root);

        Assert.Equal(2048, cache.SizeInBytes());
        cache.Clear();
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, cache.SizeInBytes());
        cache.Clear(); // déjà vide : rien à faire
    }
}
