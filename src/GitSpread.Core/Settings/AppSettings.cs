using System.Text.Json;
using System.Text.Json.Serialization;
using GitSpread.Core.Git;
using GitSpread.Core.Projects;

namespace GitSpread.Core.Settings;

public sealed class AppSettings
{
    /// <summary>Dossiers parcourus pour trouver les projets.</summary>
    public List<string> RootFolders { get; set; } = [];

    /// <summary>Profondeur maximale de recherche sous chaque dossier racine.</summary>
    public int ScanDepth { get; set; } = 4;

    public List<string> ExcludedFolders { get; set; } = [.. ScanOptions.DefaultExclusions];

    /// <summary>Thème : System, Light ou Dark.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Ordre des onglets choisi par glisser-déposer (clés « hôte/login », et « unlinked »).</summary>
    public List<string> TabOrder { get; set; } = [];

    /// <summary>Projets ajoutés un par un (n'importe où, avec ou sans git).</summary>
    public List<string> ExtraProjects { get; set; } = [];

    /// <summary>E-mails de commit confirmés par l'utilisateur pour chaque compte (clé « hôte/login »),
    /// en plus de ceux que GitHub expose publiquement.</summary>
    public Dictionary<string, List<string>> AccountEmails { get; set; } = [];

    /// <summary>Aperçu de l'historique en bas de page (F6a) : affiché ou replié, et sa hauteur.</summary>
    public bool ShowHistoryPreview { get; set; } = true;
    public double HistoryPreviewHeight { get; set; } = 110;

    /// <summary>Sections repliables des panneaux (F6c) : ouverte (vrai) ou repliée, par clé de section. Absente : état par défaut.</summary>
    public Dictionary<string, bool> PanelSections { get; set; } = [];

    /// <summary>Nombre de commits chargés à chaque fois dans l'historique (« Afficher plus » charge la suite).</summary>
    public int HistoryPageSize { get; set; } = 300;

    /// <summary>Projets sans remote rangés à la main dans un compte (chemin → clé « hôte/login »).</summary>
    public Dictionary<string, string> ProjectAccounts { get; set; } = [];

    // ------------------------------------------------------------ préférences (F6d)

    /// <summary>Vue d'un onglet à l'ouverture : « List » (liste des projets) ou « History » (historique en grand).</summary>
    public string MainView { get; set; } = MainViews.List;

    /// <summary>Dates complètes (« 13/10/2026 14:05 ») au lieu de relatives (« il y a 2 j »).</summary>
    public bool FullDates { get; set; }

    /// <summary>Premiers mots qui font d'une branche une branche de maintenance (couleurs jaune, orange, rouge).</summary>
    public List<string> MaintenanceTerms { get; set; } = [.. BranchKinds.DefaultMaintenanceTerms];

    /// <summary>Afficher les commandes git que GitSpread va exécuter, puis celles qu'il a exécutées.</summary>
    public bool ShowCommands { get; set; } = true;

    /// <summary>Visibilité proposée pour un nouveau repo GitHub.</summary>
    public bool NewRepositoriesPrivate { get; set; } = true;

    /// <summary>Adresse proposée pour signer les commits d'un compte : faux = adresse privée GitHub (noreply),
    /// vrai = adresse réelle connue du compte (la privée reste utilisée si aucune n'est connue).</summary>
    public bool PreferRealEmail { get; set; }

    /// <summary>Dossier proposé pour cloner un repo (vide : le premier dossier de projets).</summary>
    public string? CloneFolder { get; set; }

    /// <summary>Projets cachés de la liste, sans être retirés du disque.</summary>
    public List<string> HiddenProjects { get; set; } = [];

    /// <summary>Mémoriser les parties repliées ou dépliées des panneaux. Sinon, elles reprennent leur état par défaut au lancement.</summary>
    public bool RememberSections { get; set; } = true;

    /// <summary>État des parties pendant cette session quand il n'est pas mémorisé.</summary>
    [JsonIgnore] public Dictionary<string, bool> SessionSections { get; } = [];

    /// <summary>Remet les préférences par défaut. Les dossiers, les projets ajoutés, masqués ou rangés, l'ordre des onglets
    /// et les e-mails connus sont conservés.</summary>
    public void ResetPreferences()
    {
        var defaults = new AppSettings();
        Theme = defaults.Theme;
        MainView = defaults.MainView;
        FullDates = defaults.FullDates;
        MaintenanceTerms = defaults.MaintenanceTerms;
        ShowCommands = defaults.ShowCommands;
        NewRepositoriesPrivate = defaults.NewRepositoriesPrivate;
        PreferRealEmail = defaults.PreferRealEmail;
        CloneFolder = defaults.CloneFolder;
        ShowHistoryPreview = defaults.ShowHistoryPreview;
        HistoryPreviewHeight = defaults.HistoryPreviewHeight;
        HistoryPageSize = defaults.HistoryPageSize;
        ScanDepth = defaults.ScanDepth;
        ExcludedFolders = defaults.ExcludedFolders;
        RememberSections = defaults.RememberSections;
        PanelSections.Clear();
        SessionSections.Clear();
    }

    public bool IsHidden(string path) => HiddenProjects.Contains(path, StringComparer.OrdinalIgnoreCase);

    public void SetHidden(string path, bool hidden)
    {
        HiddenProjects.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (hidden) HiddenProjects.Add(path);
    }

    /// <summary>E-mails réels connus d'un compte : confirmés dans GitSpread, puis l'adresse publique du profil.</summary>
    public IReadOnlyList<string> RealEmails(string host, string login, string? publicEmail)
    {
        var emails = AccountEmails.TryGetValue(AccountKey(host, login), out var known) ? new List<string>(known) : [];
        if (publicEmail is { Length: > 0 } && !emails.Contains(publicEmail, StringComparer.OrdinalIgnoreCase)) emails.Add(publicEmail);
        return emails;
    }

    /// <summary>Adresse proposée pour signer les commits d'un compte, selon la préférence (privée ou réelle).</summary>
    public string SigningEmail(string host, string login, string noReplyEmail, string? publicEmail) =>
        PreferRealEmail && RealEmails(host, login, publicEmail) is [var real, ..] ? real : noReplyEmail;

    public void RemoveKnownEmail(string host, string login, string email)
    {
        if (AccountEmails.TryGetValue(AccountKey(host, login), out var emails))
            emails.RemoveAll(e => e.Equals(email, StringComparison.OrdinalIgnoreCase));
    }

    public static string AccountKey(string host, string login) => $"{host}/{login}".ToLowerInvariant();

    /// <summary>Compte (clé « hôte/login ») dans lequel ce projet a été rangé, ou null.</summary>
    public string? ProjectAccount(string path) =>
        ProjectAccounts.FirstOrDefault(p => p.Key.Equals(path, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Range un projet dans un compte, ou le remet dans « Non liés » (null).</summary>
    public void SetProjectAccount(string path, string? accountKey)
    {
        foreach (var key in ProjectAccounts.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList())
            ProjectAccounts.Remove(key);
        if (accountKey is not null) ProjectAccounts[path] = accountKey;
    }

    public bool IsKnownEmail(string host, string login, string? email) =>
        email is not null && AccountEmails.TryGetValue(AccountKey(host, login), out var emails)
        && emails.Contains(email, StringComparer.OrdinalIgnoreCase);

    public void AddKnownEmail(string host, string login, string email)
    {
        var key = AccountKey(host, login);
        if (!AccountEmails.TryGetValue(key, out var emails)) AccountEmails[key] = emails = [];
        if (!emails.Contains(email, StringComparer.OrdinalIgnoreCase)) emails.Add(email);
    }

    public ScanOptions ToScanOptions() =>
        new(RootFolders) { MaxDepth = ScanDepth, ExcludedFolderNames = ExcludedFolders, ExtraProjects = ExtraProjects };
}

/// <summary>Vues possibles d'un onglet à l'ouverture.</summary>
public static class MainViews
{
    public const string List = "List";
    public const string History = "History";
}

/// <summary>Préférences en JSON (par défaut %AppData%\GitSpread\settings.json). Aucun secret n'y est stocké.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Path { get; } = path;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitSpread", "settings.json");

    public bool Exists => File.Exists(Path);

    /// <summary>Charge les préférences ; un fichier absent ou illisible donne les valeurs par défaut
    /// (le fichier corrompu est conservé à côté, en .bak).</summary>
    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            File.Copy(Path, Path + ".bak", overwrite: true);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, Path, overwrite: true); // écriture atomique
    }

    /// <summary>Dossiers de projets courants qui existent sur cette machine (proposés au premier lancement).</summary>
    public static List<string> SuggestRootFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            @"C:\xampp\htdocs", @"C:\wamp64\www", @"C:\laragon\www",
            System.IO.Path.Combine(home, "source", "repos"),
            System.IO.Path.Combine(home, "Documents", "GitHub"),
            System.IO.Path.Combine(home, "Projects"), System.IO.Path.Combine(home, "Projets"),
            System.IO.Path.Combine(home, "dev"), System.IO.Path.Combine(home, "code"),
        ];
        return candidates.Where(Directory.Exists).ToList();
    }
}
