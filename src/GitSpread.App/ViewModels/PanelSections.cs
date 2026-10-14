using CommunityToolkit.Mvvm.ComponentModel;

namespace GitSpread.App.ViewModels;

/// <summary>
/// Sections repliables des panneaux (F6c), ouvertes ou repliées au choix : l'état est mémorisé et vaut pour tous
/// les projets et tous les repos. La zone sensible d'un repo GitHub et la sortie brute des commandes sont repliées par défaut.
/// </summary>
public sealed class PanelSections(IProjectHost host) : ObservableObject
{
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

    // Parties de sections qui peuvent devenir longues
    public bool LinkRows { get => Get(); set => Set(value); }
    public bool OtherBranches { get => Get(); set => Set(value); }
    public bool ActionCommands { get => Get(); set => Set(value); }
    public bool ActionOutput { get => Get(defaultOpen: false); set => Set(value); }

    private bool Get(bool defaultOpen = true, [System.Runtime.CompilerServices.CallerMemberName] string key = "") =>
        host.Settings.PanelSections.TryGetValue(key, out var open) ? open : defaultOpen;

    private void Set(bool open, [System.Runtime.CompilerServices.CallerMemberName] string key = "")
    {
        if (host.Settings.PanelSections.TryGetValue(key, out var current) && current == open) return;
        host.Settings.PanelSections[key] = open;
        host.SaveSettings();
        OnPropertyChanged(key);
    }
}
