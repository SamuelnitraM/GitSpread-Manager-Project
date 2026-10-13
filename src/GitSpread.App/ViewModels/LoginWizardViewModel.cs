using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.Ssh;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

public enum LoginStep { Host, Protocol, GitCredentials, SshKey, Method, Token, Summary, Running, Done, Failed }

public enum SshKeyChoice { Skip, Existing, Generate }

/// <summary>
/// Les questions de « gh auth login », posées une par une. Les réponses sont ensuite traduites en options
/// de gh auth login (mode non interactif), puis GitSpread enchaîne les étapes annexes lui-même.
/// </summary>
public sealed partial class LoginWizardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ILogger _logger;
    private readonly Stack<LoginStep> _history = new();
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private CancellationTokenSource? _cts;

    public LoginWizardViewModel(AppServices services, IReadOnlyList<GitHubAccount> existingAccounts)
    {
        _services = services;
        _logger = services.LoggerFactory.CreateLogger<LoginWizardViewModel>();
        PreviousActive = existingAccounts.FirstOrDefault(a => a.IsActive && a.Host == AccountService.DefaultHost)?.Login;
        SshKeys = services.SshKeys.ListPublicKeys();
        SelectedSshKey = SshKeys.FirstOrDefault();
        SshChoice = SshKeys.Count > 0 ? SshKeyChoice.Existing : SshKeyChoice.Generate;
    }

    /// <summary>Vrai quand un compte a été connecté : la fenêtre principale doit s'actualiser.</summary>
    public bool Completed { get; private set; }

    public event EventHandler? CloseRequested;

    // ------------------------------------------------------------ réponses

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Host))] public partial bool UseGitHubCom { get; set; } = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Host))] public partial string CustomHost { get; set; } = "";
    public string Host => UseGitHubCom ? AccountService.DefaultHost : CustomHost.Trim().TrimEnd('/').Replace("https://", "");

    [ObservableProperty] public partial bool UseSsh { get; set; }
    [ObservableProperty] public partial bool ConfigureGitCredentials { get; set; }

    public IReadOnlyList<SshPublicKey> SshKeys { get; }
    public bool HasSshKeys => SshKeys.Count > 0;
    [ObservableProperty] public partial SshKeyChoice SshChoice { get; set; }
    [ObservableProperty] public partial SshPublicKey? SelectedSshKey { get; set; }
    [ObservableProperty] public partial string SshKeyTitle { get; set; } = "GitSpread";
    public string NewKeyPassphrase { private get; set; } = "";

    [ObservableProperty] public partial bool UseBrowser { get; set; } = true;
    public string Token { private get; set; } = "";

    public string? PreviousActive { get; }
    public bool HasPreviousActive => PreviousActive is not null;
    [ObservableProperty] public partial bool KeepPreviousActive { get; set; } = true;

    private bool UploadsSshKey => UseSsh && SshChoice != SshKeyChoice.Skip;

    // ------------------------------------------------------------ navigation

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGoBack), nameof(IsQuestion), nameof(StepTitle))]
    public partial LoginStep Step { get; set; } = LoginStep.Host;

    [ObservableProperty] public partial string? ValidationMessage { get; set; }

    public bool CanGoBack => _history.Count > 0 && IsQuestion;
    public bool IsQuestion => Step is not (LoginStep.Running or LoginStep.Done or LoginStep.Failed);

    public string StepTitle => Step switch
    {
        LoginStep.Summary => "Récapitulatif",
        LoginStep.Running => "Connexion en cours",
        LoginStep.Done => "Compte connecté",
        LoginStep.Failed => "La connexion a échoué",
        _ => $"Question {_history.Count + 1}",
    };

    public ObservableCollection<string> CommandsPreview { get; } = [];

    [RelayCommand]
    private void Next()
    {
        ValidationMessage = Validate();
        if (ValidationMessage is not null) return;

        var next = Step switch
        {
            LoginStep.Host => LoginStep.Protocol,
            LoginStep.Protocol => UseSsh ? LoginStep.SshKey : LoginStep.GitCredentials,
            LoginStep.GitCredentials or LoginStep.SshKey => LoginStep.Method,
            LoginStep.Method => UseBrowser ? LoginStep.Summary : LoginStep.Token,
            LoginStep.Token => LoginStep.Summary,
            _ => Step,
        };
        if (next == Step) return;
        if (next == LoginStep.Summary) BuildPreview();
        _history.Push(Step);
        Step = next;
    }

    [RelayCommand]
    private void Back()
    {
        ValidationMessage = null;
        if (_history.Count > 0) Step = _history.Pop();
    }

    private string? Validate() => Step switch
    {
        LoginStep.Host when Host.Length == 0 || Host.Contains(' ') => "Indique le nom d'hôte de ton serveur GitHub Enterprise (ex. github.monentreprise.fr).",
        LoginStep.SshKey when SshChoice == SshKeyChoice.Existing && SelectedSshKey is null => "Choisis une clé dans la liste.",
        LoginStep.SshKey when SshChoice != SshKeyChoice.Skip && string.IsNullOrWhiteSpace(SshKeyTitle) => "Donne un titre à la clé (il apparaîtra sur GitHub).",
        LoginStep.Token when string.IsNullOrWhiteSpace(Token) => "Colle ton token d'accès personnel.",
        _ => null,
    };

    private LoginRequest BuildRequest() => new(Host, UseSsh ? GitProtocol.Ssh : GitProtocol.Https)
    {
        Token = UseBrowser ? null : Token,
        RequestSshKeyScope = UploadsSshKey,
    };

    private void BuildPreview()
    {
        CommandsPreview.Clear();
        var login = "gh " + string.Join(' ', LoginService.BuildArguments(BuildRequest()));
        CommandsPreview.Add(UseBrowser ? login : $"{login}   (token transmis en entrée, jamais affiché)");
        if (!UseSsh && ConfigureGitCredentials) CommandsPreview.Add($"gh auth setup-git --hostname {Host}");
        if (UseSsh && SshChoice == SshKeyChoice.Generate)
            CommandsPreview.Add($"ssh-keygen -t ed25519 -f {_services.SshKeys.SshDirectory}\\id_ed25519_<compte> -C <compte>@GitSpread");
        if (UploadsSshKey)
            CommandsPreview.Add($"gh ssh-key add {(SshChoice == SshKeyChoice.Existing ? SelectedSshKey!.PublicKeyPath : "<nouvelle clé>.pub")} --title \"{SshKeyTitle}\"   (avec le nouveau compte)");
        if (HasPreviousActive && KeepPreviousActive)
            CommandsPreview.Add($"gh auth switch --hostname {Host} --user {PreviousActive}");
    }

    partial void OnKeepPreviousActiveChanged(bool value) { if (Step == LoginStep.Summary) BuildPreview(); }

    // ------------------------------------------------------------ exécution

    [ObservableProperty] public partial string? DeviceCode { get; set; }
    [ObservableProperty] public partial string DeviceUrl { get; set; } = "https://github.com/login/device";
    [ObservableProperty] public partial string? ConnectedLogin { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    public bool HasDeviceCode => DeviceCode is not null;
    partial void OnDeviceCodeChanged(string? value) => OnPropertyChanged(nameof(HasDeviceCode));

    public ObservableCollection<string> Progress { get; } = [];

    [RelayCommand]
    private async Task StartAsync()
    {
        _history.Push(Step);
        Step = LoginStep.Running;
        Progress.Clear();
        DeviceCode = null;
        ErrorMessage = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            Report(UseBrowser ? "Demande d'un code de connexion à GitHub…" : "Vérification du token…");
            var account = await _services.Login.LoginAsync(BuildRequest(), code => OnUi(() =>
            {
                DeviceCode = code.Code;
                DeviceUrl = code.VerificationUrl;
                Report("En attente de ta validation sur GitHub…");
            }), ct);
            ConnectedLogin = account.Login;
            Completed = true;
            Report($"Connecté en tant que {account.Login}.");

            if (!UseSsh && ConfigureGitCredentials)
            {
                Report("Configuration de git pour utiliser gh…");
                await _services.Login.SetupGitAsync(Host, ct);
            }

            if (UploadsSshKey)
            {
                var key = SshChoice == SshKeyChoice.Existing ? SelectedSshKey! : await GenerateKeyAsync(account, ct);
                Report($"Envoi de la clé {key.FileName} sur le compte {account.Login}…");
                await _services.GitHub.AddSshKeyAsync(account, key.PublicKeyPath, SshKeyTitle.Trim(), ct);
            }

            if (KeepPreviousActive && PreviousActive is { } previous && previous != account.Login)
            {
                Report($"{previous} redevient le compte actif.");
                var accounts = await _services.Accounts.GetAccountsAsync(ct);
                if (accounts.FirstOrDefault(a => a.Login == previous && a.Host == Host) is { } prev)
                    await _services.Accounts.SwitchAsync(prev, ct);
            }

            Step = LoginStep.Done;
        }
        catch (OperationCanceledException)
        {
            Fail(Completed ? "Opération annulée après la connexion du compte." : "Connexion annulée.");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Connexion d'un compte impossible");
            Fail(e.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task<SshPublicKey> GenerateKeyAsync(GitHubAccount account, CancellationToken ct)
    {
        var fileName = _services.SshKeys.SuggestFileName(account.Login);
        Report($"Génération de la clé SSH {fileName}…");
        return await _services.SshKeys.GenerateAsync(fileName, $"{account.Login}@GitSpread", NewKeyPassphrase, ct);
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        Step = LoginStep.Failed;
    }

    [RelayCommand]
    private void Retry()
    {
        BuildPreview();
        Step = LoginStep.Summary;
        _history.TryPop(out _);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cts is not null) _cts.Cancel();
        else CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void CopyCode() { if (DeviceCode is not null) Shell.Copy(DeviceCode); }
    [RelayCommand] private void OpenDeviceUrl() => Shell.Open(DeviceUrl);

    private void Report(string message) => OnUi(() => Progress.Add(message));

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }
}
