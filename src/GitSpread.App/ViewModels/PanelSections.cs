using CommunityToolkit.Mvvm.ComponentModel;

namespace GitSpread.App.ViewModels;

/// <summary>
/// Sections repliables des panneaux (F6c), ouvertes ou repliées au choix : l'état est mémorisé et vaut pour tous
/// les projets et tous les repos. La zone sensible d'un repo GitHub et la sortie brute des commandes sont repliées par défaut.
/// Si la mémorisation est désactivée dans les paramètres (F6d), l'état ne dure que le temps de la session.
/// </summary>
public sealed class PanelSections : ObservableObject
{
    private static readonly List<WeakReference<PanelSections>> Instances = [];
    private readonly IProjectHost _host;

    public PanelSections(IProjectHost host)
    {
        _host = host;
        lock (Instances)
        {
            Instances.RemoveAll(w => !w.TryGetTarget(out _));
            Instances.Add(new WeakReference<PanelSections>(this));
        }
    }

    /// <summary>Après une réinitialisation : tous les panneaux ouverts reprennent l'état par défaut de leurs parties.</summary>
    public static void RefreshAll()
    {
        List<PanelSections> alive;
        lock (Instances) alive = Instances.Select(w => w.TryGetTarget(out var s) ? s : null).OfType<PanelSections>().ToList();
        foreach (var sections in alive) sections.OnPropertyChanged(string.Empty);
    }

    // Panneau d'un projet local
    public bool Links { get => Get(); set => Set(value); }
    public bool Branches { get => Get(); set => Set(value); }
    public bool Identity { get => Get(); set => Set(value); }
    public bool LastCommit { get => Get(); set => Set(value); }

    // Panneau d'un repo GitHub
    public bool RepoDaily { get => Get(); set => Set(value); }
    public bool RepoBranches { get => Get(); set => Set(value); }
    public bool RepoSharing { get => Get(); set => Set(value); }
    public bool RepoDanger { get => Get(defaultOpen: false); set => Set(value); }

    // Historique en grand : colonne du commit choisi
    public bool HistoryCommit { get => Get(); set => Set(value); }
    public bool HistoryBranches { get => Get(); set => Set(value); }
    public bool HistoryFiles { get => Get(); set => Set(value); }
    public bool HistoryDiff { get => Get(); set => Set(value); }

    /// <summary>Fichiers du commit : tous les fichiers du projet (vrai) ou seulement les modifiés (F6e).</summary>
    public bool HistoryAllFiles { get => Get(defaultOpen: false); set => Set(value); }

    // Parties de sections qui peuvent devenir longues
    public bool LinkRows { get => Get(); set => Set(value); }
    public bool OtherBranches { get => Get(); set => Set(value); }
    public bool ActionCommands { get => Get(); set => Set(value); }
    public bool ActionOutput { get => Get(defaultOpen: false); set => Set(value); }

    private Dictionary<string, bool> Store => _host.Settings.RememberSections ? _host.Settings.PanelSections : _host.Settings.SessionSections;

    private bool Get(bool defaultOpen = true, [System.Runtime.CompilerServices.CallerMemberName] string key = "") =>
        Store.TryGetValue(key, out var open) ? open : defaultOpen;

    private void Set(bool open, [System.Runtime.CompilerServices.CallerMemberName] string key = "")
    {
        if (Store.TryGetValue(key, out var current) && current == open) return;
        Store[key] = open;
        if (_host.Settings.RememberSections) _host.SaveSettings();
        OnPropertyChanged(key);
    }
}
