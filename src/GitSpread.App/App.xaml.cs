using System.IO;
using System.Windows;
using System.Windows.Threading;
using GitSpread.App.ViewModels;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Processes;
using GitSpread.Core.Projects;
using GitSpread.Core.Settings;
using GitSpread.Core.Ssh;
using Serilog;
using Serilog.Extensions.Logging;
using Microsoft.Extensions.Logging;

namespace GitSpread.App;

public partial class App : Application
{
    public static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitSpread");

    private SerilogLoggerFactory? _loggerFactory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDirectory = Path.Combine(DataDirectory, "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logDirectory, "gitspread-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        _loggerFactory = new SerilogLoggerFactory(Log.Logger);
        Log.Information("GitSpread {Version} démarre", typeof(App).Assembly.GetName().Version);

        DispatcherUnhandledException += OnUnhandledException;

        // Composition manuelle : peu de services, pas besoin d'un conteneur.
        var runner = new ProcessRunner(_loggerFactory.CreateLogger<ProcessRunner>());
        var gh = new GhCli(runner);
        var accounts = new AccountService(gh);
        var gitHub = new GitHubService(gh, accounts);
        var git = new GitService(runner);
        var credentials = new GitCredentialResolver(accounts);
        var services = new AppServices(
            Accounts: accounts,
            GitHub: gitHub,
            Login: new LoginService(gh, accounts),
            Git: git,
            Credentials: credentials,
            Publisher: new ProjectPublisher(git, gitHub, credentials),
            Integrator: new BranchIntegrator(git),
            Transfer: new TransferService(git, gitHub, credentials),
            Linked: new LinkedProjects(git, credentials),
            Links: new ProjectLinks(git, credentials),
            Cache: new RepositoryCache(git, credentials, Path.Combine(DataDirectory, "cache")),
            SshKeys: new SshKeyService(runner),
            Scanner: new ProjectScanner(),
            SettingsStore: new SettingsStore(SettingsStore.DefaultPath),
            LoggerFactory: _loggerFactory,
            LogDirectory: logDirectory);

        var viewModel = new MainViewModel(services, new DialogService());
        viewModel.ApplyFirstRunDefaults();
        ThemeManager.Apply(ThemeManager.Parse(viewModel.Settings.Theme));

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        _ = viewModel.RefreshAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("GitSpread s'arrête");
        Log.CloseAndFlush();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Erreur non gérée");
        MessageBox.Show($"Une erreur inattendue s'est produite :\n\n{e.Exception.Message}\n\nDétails dans les journaux :\n{Path.Combine(DataDirectory, "logs")}",
            "GitSpread", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
