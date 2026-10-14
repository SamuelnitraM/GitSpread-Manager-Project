using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.Settings;

namespace GitSpread.App.ViewModels;

public enum SettingsPage { Appearance, Git, Projects, Accounts, Advanced }

public sealed record ThemeOption(string Value, string Label)
{
    /// <summary>Nom annoncé par les lecteurs d'écran.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// Fenêtre des paramètres (F6d). Chaque réglage s'applique tout de suite ; si les dossiers ou les projets ont changé,
/// une nouvelle analyse est lancée à la fermeture.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _rescanNeeded;
    private bool _reloadNeeded;

    public SettingsViewModel(MainViewModel main, SettingsPage page = SettingsPage.Appearance)
    {
        _main = main;
        Page = page;
        RootFolders = new(Settings.RootFolders);
        ExtraProjects = new(Settings.ExtraProjects);
        HiddenProjects = new(Settings.HiddenProjects);
        _maintenanceTermsText = string.Join(", ", Settings.MaintenanceTerms);
        _exclusionsText = string.Join(", ", Settings.ExcludedFolders);
        RebuildAccounts();
    }

    private AppSettings Settings => _main.Settings;
    private AppServices Services => _main.Services;

    /// <summary>Applique le thème à l'interface (fourni par la fenêtre : les ViewModels ne connaissent pas WPF).</summary>
    public Action<string>? ApplyTheme { get; init; }

    [ObservableProperty] public partial SettingsPage Page { get; set; }

    // ------------------------------------------------------------ apparence

    public IReadOnlyList<ThemeOption> Themes { get; } =
        [new("System", "Comme Windows"), new("Light", "Clair"), new("Dark", "Sombre")];

    public ThemeOption SelectedTheme
    {
        get => Themes.FirstOrDefault(t => t.Value == Settings.Theme) ?? Themes[0];
        set
        {
            if (value is null || value.Value == Settings.Theme) return;
            Settings.Theme = value.Value;
            _main.SaveSettings();
            ApplyTheme?.Invoke(value.Value);
            OnPropertyChanged();
        }
    }

    /// <summary>Vue principale : historique en grand (arborescence) plutôt que la liste des projets.</summary>
    public bool StartWithHistory
    {
        get => Settings.MainView == MainViews.History;
        set
        {
            if (value == StartWithHistory) return;
            Settings.MainView = value ? MainViews.History : MainViews.List;
            _main.ApplyPreferences(mainViewChanged: true);
            OnPropertyChanged();
            OnPropertyChanged(nameof(StartWithList));
        }
    }

    public bool StartWithList { get => !StartWithHistory; set => StartWithHistory = !value; }

    public bool ShowHistoryPreview
    {
        get => Settings.ShowHistoryPreview;
        set => Update(Settings.ShowHistoryPreview, value, v => Settings.ShowHistoryPreview = v);
    }

    public bool FullDates
    {
        get => Settings.FullDates;
        set
        {
            if (Update(Settings.FullDates, value, v => Settings.FullDates = v)) _reloadNeeded = true;
            OnPropertyChanged(nameof(DateExample));
        }
    }

    public string DateExample => "Exemple : " + DisplayPreferences.Format(DateTimeOffset.Now.AddDays(-2).AddHours(-3), Settings.FullDates, DateTimeOffset.Now);

    /// <summary>Termes de maintenance, séparés par des virgules (appliqués en quittant le champ).</summary>
    public string MaintenanceTermsText
    {
        get => _maintenanceTermsText;
        set
        {
            if (!SetProperty(ref _maintenanceTermsText, value)) return;
            Settings.MaintenanceTerms = SplitList(value);
            _main.ApplyPreferences();
            _reloadNeeded = true;
        }
    }
    private string _maintenanceTermsText;

    [RelayCommand]
    private void ResetMaintenanceTerms() => MaintenanceTermsText = string.Join(", ", BranchKinds.DefaultMaintenanceTerms);

    // ------------------------------------------------------------ git et GitHub

    public bool ShowCommands
    {
        get => Settings.ShowCommands;
        set => Update(Settings.ShowCommands, value, v => Settings.ShowCommands = v);
    }

    public bool NewRepositoriesPrivate
    {
        get => Settings.NewRepositoriesPrivate;
        set
        {
            Update(Settings.NewRepositoriesPrivate, value, v => Settings.NewRepositoriesPrivate = v);
            OnPropertyChanged(nameof(NewRepositoriesPublic));
        }
    }

    public bool NewRepositoriesPublic { get => !NewRepositoriesPrivate; set => NewRepositoriesPrivate = !value; }

    public bool PreferRealEmail
    {
        get => Settings.PreferRealEmail;
        set
        {
            Update(Settings.PreferRealEmail, value, v => Settings.PreferRealEmail = v);
            OnPropertyChanged(nameof(PreferNoReplyEmail));
        }
    }

    public bool PreferNoReplyEmail { get => !PreferRealEmail; set => PreferRealEmail = !value; }

    /// <summary>Dossier proposé pour cloner ; vide : le premier dossier de projets.</summary>
    public string CloneFolder
    {
        get => Settings.CloneFolder ?? "";
        set
        {
            var folder = value.Trim();
            Update(Settings.CloneFolder ?? "", folder, v => Settings.CloneFolder = v.Length == 0 ? null : v);
            OnPropertyChanged(nameof(CloneFolderHint));
        }
    }

    public string CloneFolderHint => Settings.CloneFolder is { Length: > 0 } folder
        ? Directory.Exists(folder) ? "Les repos clonés seront proposés dans ce dossier." : "Ce dossier n'existe pas : le premier dossier de projets sera proposé."
        : Settings.RootFolders.FirstOrDefault(Directory.Exists) is { } root ? $"Vide : le premier dossier de projets ({root})." : "Vide : le premier dossier de projets.";

    public IReadOnlyList<int> PageSizes { get; } = [100, 300, 500, 1000];

    public int HistoryPageSize
    {
        get => Settings.HistoryPageSize;
        set => Update(Settings.HistoryPageSize, value, v => Settings.HistoryPageSize = v);
    }

    // ------------------------------------------------------------ projets

    public ObservableCollection<string> RootFolders { get; }
    public ObservableCollection<string> ExtraProjects { get; }
    public ObservableCollection<string> HiddenProjects { get; }
    public bool HasNoRootFolders => RootFolders.Count == 0;
    public bool HasNoExtraProjects => ExtraProjects.Count == 0;
    public bool HasNoHiddenProjects => HiddenProjects.Count == 0;

    public void AddRootFolders(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
            if (!RootFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) RootFolders.Add(folder);
        SaveFolders();
    }

    [RelayCommand]
    private void RemoveRootFolder(string folder)
    {
        RootFolders.Remove(folder);
        SaveFolders();
    }

    private void SaveFolders()
    {
        Settings.RootFolders = RootFolders.ToList();
        _main.SaveSettings();
        _rescanNeeded = true;
        OnPropertyChanged(nameof(HasNoRootFolders));
        OnPropertyChanged(nameof(CloneFolderHint));
    }

    public IReadOnlyList<int> Depths { get; } = Enumerable.Range(1, 8).ToList();

    public int ScanDepth
    {
        get => Settings.ScanDepth;
        set { if (Update(Settings.ScanDepth, value, v => Settings.ScanDepth = v)) _rescanNeeded = true; }
    }

    /// <summary>Noms de dossiers ignorés pendant l'analyse, séparés par des virgules.</summary>
    public string ExclusionsText
    {
        get => _exclusionsText;
        set
        {
            if (!SetProperty(ref _exclusionsText, value)) return;
            Settings.ExcludedFolders = SplitList(value);
            _main.SaveSettings();
            _rescanNeeded = true;
        }
    }
    private string _exclusionsText;

    [RelayCommand]
    private void ResetExclusions() => ExclusionsText = string.Join(", ", Core.Projects.ScanOptions.DefaultExclusions);

    [RelayCommand]
    private void RemoveExtraProject(string path)
    {
        ExtraProjects.Remove(path);
        Settings.ExtraProjects.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        _main.SaveSettings();
        _rescanNeeded = true;
        OnPropertyChanged(nameof(HasNoExtraProjects));
    }

    /// <summary>Réaffiche un projet masqué dans la liste.</summary>
    [RelayCommand]
    private void ShowProject(string path)
    {
        HiddenProjects.Remove(path);
        Settings.SetHidden(path, false);
        _main.SaveSettings();
        _main.RedistributeProjects();
        OnPropertyChanged(nameof(HasNoHiddenProjects));
    }

    // ------------------------------------------------------------ comptes et identités

    public ObservableCollection<AccountSettingsRow> Accounts { get; } = [];
    public bool HasNoAccounts => Accounts.Count == 0;

    /// <summary>Reconstruit la liste des comptes (après une connexion, une déconnexion ou un changement de compte actif).</summary>
    public void RebuildAccounts()
    {
        Accounts.Clear();
        foreach (var account in _main.Accounts) Accounts.Add(new AccountSettingsRow(account, this, _main));
        OnPropertyChanged(nameof(HasNoAccounts));
        foreach (var row in Accounts) _ = row.LoadAsync();
    }

    [ObservableProperty] public partial string GlobalName { get; set; } = "";
    [ObservableProperty] public partial string GlobalEmail { get; set; } = "";
    [ObservableProperty] public partial string? GlobalIdentityStatus { get; set; }
    [ObservableProperty] public partial bool GlobalIdentityFailed { get; set; }

    [RelayCommand]
    private async Task SaveGlobalIdentityAsync()
    {
        var (name, email) = (GlobalName.Trim(), GlobalEmail.Trim());
        if (name.Length == 0 || !email.Contains('@'))
        {
            (GlobalIdentityStatus, GlobalIdentityFailed) = ("Indique un nom et une adresse e-mail valide.", true);
            return;
        }
        var results = await Services.Git.SetGlobalIdentityAsync(name, email);
        (GlobalIdentityStatus, GlobalIdentityFailed) = results.FirstOrDefault(r => !r.Succeeded) is { } failed
            ? ($"Échec : {failed.Explanation ?? failed.Output}", true)
            : ("Identité du poste enregistrée. Les projets qui ont leur propre identité ne changent pas.", false);
    }

    // ------------------------------------------------------------ avancé

    public string LogDirectory => _main.LogDirectory;
    public string SettingsPath => Services.SettingsStore.Path;
    public string CachePath => Services.Cache.Root;

    [ObservableProperty] public partial string GitVersion { get; set; } = "…";
    [ObservableProperty] public partial string GhVersion { get; set; } = "…";
    [ObservableProperty] public partial string CacheSizeLabel { get; set; } = "…";
    [ObservableProperty] public partial string? AdvancedStatus { get; set; }

    public bool RememberSections
    {
        get => Settings.RememberSections;
        set
        {
            if (value == Settings.RememberSections) return;
            // L'état actuel est gardé pour la session, ou devient l'état mémorisé.
            var (from, to) = value ? (Settings.SessionSections, Settings.PanelSections) : (Settings.PanelSections, Settings.SessionSections);
            to.Clear();
            foreach (var (k, v) in from) to[k] = v;
            from.Clear();
            Settings.RememberSections = value;
            _main.SaveSettings();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ResetSections()
    {
        Settings.PanelSections.Clear();
        Settings.SessionSections.Clear();
        _main.SaveSettings();
        PanelSections.RefreshAll();
        AdvancedStatus = "Toutes les parties ont repris leur état par défaut.";
    }

    [RelayCommand]
    private void ClearCache()
    {
        try
        {
            Services.Cache.Clear();
            AdvancedStatus = "Copies légères supprimées : elles seront refaites à la prochaine ouverture d'un historique.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AdvancedStatus = $"Une copie est en cours d'utilisation, réessaie dans un instant ({e.Message}).";
        }
        UpdateCacheSize();
    }

    [RelayCommand]
    private void ResetPreferences()
    {
        if (!_main.Dialogs.Confirm(
                "Remettre toutes les préférences par défaut ?\n\n"
                + "Apparence, réglages git, profondeur de recherche, dossiers ignorés et parties repliées reviennent à leur état d'origine.\n"
                + "Tes dossiers de projets, les projets ajoutés, masqués ou rangés, l'ordre des onglets et les e-mails connus sont conservés.",
                "Réinitialiser les paramètres"))
            return;
        Settings.ResetPreferences();
        _maintenanceTermsText = string.Join(", ", Settings.MaintenanceTerms);
        _exclusionsText = string.Join(", ", Settings.ExcludedFolders);
        _main.ApplyPreferences(mainViewChanged: true);
        ApplyTheme?.Invoke(Settings.Theme);
        PanelSections.RefreshAll();
        _rescanNeeded = _reloadNeeded = true;
        OnPropertyChanged(string.Empty);
        AdvancedStatus = "Préférences remises par défaut.";
    }

    [RelayCommand] private void OpenLogs() { Directory.CreateDirectory(LogDirectory); Shell.Open(LogDirectory); }
    [RelayCommand] private void OpenSettingsFolder() => Shell.ShowInExplorer(SettingsPath);

    // ------------------------------------------------------------ chargement et fermeture

    /// <summary>Lit ce qui demande git ou gh : versions, identité du poste, place du cache.</summary>
    public async Task LoadAsync()
    {
        UpdateCacheSize();
        var git = Services.Git.GetVersionAsync();
        var gh = Services.GitHub.GetCliVersionAsync();
        try
        {
            var identity = await Services.Git.GetGlobalIdentityAsync();
            (GlobalName, GlobalEmail) = (identity.Name ?? "", identity.Email ?? "");
            if (identity.Email is null) (GlobalIdentityStatus, GlobalIdentityFailed) = ("Aucune identité globale : les projets sans identité propre ne pourront pas commiter.", true);
        }
        catch (Exception e) { (GlobalIdentityStatus, GlobalIdentityFailed) = ($"Identité du poste illisible : {e.Message}", true); }
        GitVersion = await git ?? "introuvable";
        GhVersion = await gh ?? "introuvable";
    }

    private void UpdateCacheSize()
    {
        var bytes = Services.Cache.SizeInBytes();
        CacheSizeLabel = bytes switch
        {
            0 => "vide",
            < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} Ko",
            _ => $"{bytes / (1024.0 * 1024):0.#} Mo",
        };
    }

    /// <summary>À la fermeture : nouvelle analyse si les dossiers ou les projets ont changé, sinon relecture si l'affichage a changé.</summary>
    public async Task CloseAsync()
    {
        if (_rescanNeeded) await _main.UpdateRootFoldersAsync(Settings.RootFolders, Settings.ScanDepth);
        else if (_reloadNeeded) await _main.RefreshAsync();
    }

    // ------------------------------------------------------------ outils

    private bool Update<T>(T current, T value, Action<T> set, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        set(value);
        _main.ApplyPreferences();
        OnPropertyChanged(property);
        return true;
    }

    internal static List<string> SplitList(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Un compte dans les paramètres : état, autorisations, adresses connues et actions.</summary>
public sealed partial class AccountSettingsRow(GitHubAccount account, SettingsViewModel settings, MainViewModel main) : ObservableObject
{
    public GitHubAccount Account => account;
    public string Login => account.Login;
    public string HostLabel => account.Host;
    public bool IsActive => account.IsActive;
    public bool CanMakeActive => !account.IsActive && account.IsAuthenticated;
    public string StateLabel => !account.IsAuthenticated ? $"connexion à refaire : {account.Error}"
        : account.IsActive ? "compte actif de gh" : "connecté";
    public string ScopesLabel => account.ScopeSet.Count == 0 ? "aucune autorisation" : "Autorisations : " + string.Join(", ", account.ScopeSet.Order());

    [ObservableProperty] public partial string NoReplyEmail { get; set; } = "…";
    [ObservableProperty] public partial string? PublicEmail { get; set; }
    public ObservableCollection<string> KnownEmails { get; } = [];
    public bool HasPublicEmail => PublicEmail is not null;
    public bool HasNoKnownEmails => KnownEmails.Count == 0;
    partial void OnPublicEmailChanged(string? value) => OnPropertyChanged(nameof(HasPublicEmail));

    public async Task LoadAsync()
    {
        ReloadKnownEmails();
        var user = account.IsAuthenticated ? await main.GetAccountUserAsync(account) : null;
        NoReplyEmail = user?.NoReplyEmail ?? "indisponible (GitHub injoignable ou compte déconnecté)";
        PublicEmail = user?.Email is { Length: > 0 } e ? e : null;
    }

    private void ReloadKnownEmails()
    {
        KnownEmails.Clear();
        if (main.Settings.AccountEmails.TryGetValue(AppSettings.AccountKey(account.Host, account.Login), out var emails))
            foreach (var e in emails) KnownEmails.Add(e);
        OnPropertyChanged(nameof(HasNoKnownEmails));
    }

    [RelayCommand]
    private void AddEmail()
    {
        var email = main.Dialogs.Prompt("Ajouter une adresse",
            $"Adresse e-mail qui appartient au compte {account.Login} (vérifiée dans les paramètres de ton compte GitHub).\n"
            + "GitSpread la reconnaîtra pour ce compte et pourra la proposer pour signer tes commits.",
            "", v => v.Trim() is { } t && t.Contains('@') && !t.Contains(' ') ? null : "Adresse e-mail invalide.");
        if (email is null) return;
        main.Settings.AddKnownEmail(account.Host, account.Login, email.Trim());
        main.SaveSettings();
        ReloadKnownEmails();
    }

    [RelayCommand]
    private void RemoveEmail(string email)
    {
        main.Settings.RemoveKnownEmail(account.Host, account.Login, email);
        main.SaveSettings();
        ReloadKnownEmails();
    }

    [RelayCommand]
    private async Task MakeActiveAsync()
    {
        if (main.AccountTabs.FirstOrDefault(t => t.Account.Is(account)) is not { } tab) return;
        await main.SwitchToAsync(tab);
        settings.RebuildAccounts();
    }

    [RelayCommand]
    private async Task ScopesAsync()
    {
        if (await main.OpenScopesAsync(account)) settings.RebuildAccounts();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await main.LogoutAsync(account);
        settings.RebuildAccounts();
    }

    [RelayCommand] private void OpenProfile() => Shell.Open($"https://{account.Host}/{account.Login}");
}
