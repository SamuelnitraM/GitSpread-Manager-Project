using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

public enum ScopesStep { Form, Running, Done, Failed }

/// <summary>Une autorisation dans la liste : cochée = souhaitée sur le compte.</summary>
public sealed partial class ScopeItemViewModel(ScopeInfo info, bool granted) : ObservableObject
{
    public ScopeInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Label => Info.Label;
    public string Description => Info.IsRequired ? $"{Info.Description} (toujours accordée par gh)" : Info.Description;
    public bool IsGranted { get; } = granted;
    public bool CanChange => !Info.IsRequired;

    [ObservableProperty] public partial bool IsSelected { get; set; } = granted;

    public bool WillBeAdded => IsSelected && !IsGranted;
    public bool WillBeRemoved => !IsSelected && IsGranted;
    partial void OnIsSelectedChanged(bool value) { OnPropertyChanged(nameof(WillBeAdded)); OnPropertyChanged(nameof(WillBeRemoved)); }
}

/// <summary>Ajouter ou retirer des autorisations à un compte (gh auth refresh, validation dans le navigateur).</summary>
public sealed partial class AccountScopesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ILogger _logger;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly string? _activeLogin;
    private CancellationTokenSource? _cts;

    /// <param name="request">Autorisations à pré-cocher (ex. delete_repo quand une suppression l'exige).</param>
    /// <param name="reason">Pourquoi on les demande, affiché en tête de fenêtre.</param>
    public AccountScopesViewModel(GitHubAccount account, AppServices services, IReadOnlyList<GitHubAccount> accounts,
        IReadOnlyCollection<string>? request = null, string? reason = null)
    {
        Account = account;
        _services = services;
        _logger = services.LoggerFactory.CreateLogger<AccountScopesViewModel>();
        _activeLogin = accounts.FirstOrDefault(a => a.IsActive && a.Host == account.Host)?.Login;
        Reason = reason;

        var granted = account.ScopeSet;
        var items = ScopeCatalog.Known.Select(s => new ScopeItemViewModel(s, account.HasScope(s.Name))).ToList();
        // Autorisations accordées hors catalogue : affichées pour pouvoir être retirées.
        items.AddRange(granted.Where(g => ScopeCatalog.Known.All(k => !k.Name.Equals(g, StringComparison.OrdinalIgnoreCase)))
            .Select(g => new ScopeItemViewModel(ScopeCatalog.Describe(g), true)));
        foreach (var item in items)
        {
            if (request?.Contains(item.Name, StringComparer.OrdinalIgnoreCase) == true) item.IsSelected = true;
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ScopeItemViewModel.IsSelected)) Refresh(); };
            Scopes.Add(item);
        }
        Refresh();
    }

    public event EventHandler? CloseRequested;

    public GitHubAccount Account { get; }
    public string Login => Account.Login;
    public string? Reason { get; }
    public bool HasReason => Reason is not null;
    public bool Completed { get; private set; }

    public ObservableCollection<ScopeItemViewModel> Scopes { get; } = [];
    public ObservableCollection<string> Preview { get; } = [];
    public ObservableCollection<string> Progress { get; } = [];

    public IReadOnlyList<string> ToAdd => Scopes.Where(s => s.WillBeAdded).Select(s => s.Name).ToList();
    public IReadOnlyList<string> ToRemove => Scopes.Where(s => s.WillBeRemoved).Select(s => s.Name).ToList();
    public bool HasChanges => ToAdd.Count > 0 || ToRemove.Count > 0;

    public string BrowserWarning =>
        $"Dans le navigateur, sois connecté au compte {Login} avant de valider : GitHub refuse la validation faite avec un autre compte.";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsForm))] public partial ScopesStep Step { get; set; } = ScopesStep.Form;
    [ObservableProperty] public partial string? DeviceCode { get; set; }
    [ObservableProperty] public partial string DeviceUrl { get; set; } = "https://github.com/login/device";
    [ObservableProperty] public partial string? ResultMessage { get; set; }

    public bool IsForm => Step == ScopesStep.Form;
    public bool HasDeviceCode => DeviceCode is not null;
    partial void OnDeviceCodeChanged(string? value) => OnPropertyChanged(nameof(HasDeviceCode));

    private void Refresh()
    {
        Preview.Clear();
        if (HasChanges)
        {
            var mustSwitch = _activeLogin is not null && _activeLogin != Login;
            if (mustSwitch) Preview.Add($"gh auth switch --hostname {Account.Host} --user {Login}   (compte actif le temps de l'opération)");
            Preview.Add("gh " + string.Join(' ', AccountService.BuildScopeArguments(Account.Host, ToAdd, ToRemove)) + "   (validation dans le navigateur)");
            if (mustSwitch) Preview.Add($"gh auth switch --hostname {Account.Host} --user {_activeLogin}   (retour au compte actif)");
        }
        OnPropertyChanged(nameof(HasChanges));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private async Task ApplyAsync()
    {
        Step = ScopesStep.Running;
        Progress.Clear();
        DeviceCode = null;
        _cts = new CancellationTokenSource();
        var (add, remove) = (ToAdd, ToRemove);
        try
        {
            var updated = await _services.Accounts.ChangeScopesAsync(Account, add, remove,
                code => OnUi(() =>
                {
                    DeviceCode = code.Code;
                    DeviceUrl = code.VerificationUrl;
                    Progress.Add("En attente de ta validation sur GitHub…");
                }),
                new Progress<string>(Progress.Add), _cts.Token);

            var missing = add.Where(s => !updated.HasScope(s)).ToList();
            Completed = true;
            ResultMessage = missing.Count == 0
                ? $"Autorisations de {Login} mises à jour." + (add.Count > 0 ? $" Ajoutées : {string.Join(", ", add.Select(s => ScopeCatalog.Describe(s).Label))}." : "")
                  + (remove.Count > 0 ? $" Retirées : {string.Join(", ", remove.Select(s => ScopeCatalog.Describe(s).Label))}." : "")
                : $"GitHub n'a pas accordé : {string.Join(", ", missing)}.";
            Step = missing.Count == 0 ? ScopesStep.Done : ScopesStep.Failed;
        }
        catch (OperationCanceledException)
        {
            ResultMessage = "Opération annulée : les autorisations n'ont pas changé.";
            Step = ScopesStep.Failed;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Changement d'autorisations de {Login} impossible", Login);
            ResultMessage = e.Message.Contains("correct account", StringComparison.OrdinalIgnoreCase)
                ? $"La validation a été faite avec un autre compte que {Login}. Connecte-toi à GitHub avec {Login} dans le navigateur (ou utilise une fenêtre privée), puis réessaie."
                : e.Message;
            Step = ScopesStep.Failed;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cts is not null) _cts.Cancel();
        else CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void BackToForm() => Step = ScopesStep.Form;
    [RelayCommand] private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void CopyCode() { if (DeviceCode is not null) Shell.Copy(DeviceCode); }
    [RelayCommand] private void OpenDeviceUrl() => Shell.Open(DeviceUrl);

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }
}
