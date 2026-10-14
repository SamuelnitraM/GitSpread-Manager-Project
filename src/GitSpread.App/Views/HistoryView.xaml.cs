using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitSpread.App.ViewModels;

namespace GitSpread.App.Views;

public partial class HistoryView : UserControl
{
    public static readonly DependencyProperty ShowLegendProperty =
        DependencyProperty.Register(nameof(ShowLegend), typeof(bool), typeof(HistoryView), new PropertyMetadata(false));

    /// <summary>Légende en mots simples sous le graphe (grande fenêtre).</summary>
    public bool ShowLegend { get => (bool)GetValue(ShowLegendProperty); set => SetValue(ShowLegendProperty, value); }

    public HistoryView()
    {
        InitializeComponent();
        // Les étiquettes des branches prennent la couleur du nouveau thème.
        Loaded += (_, _) => ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Commits.Items.Refresh();

    // Commit choisi ailleurs (recherche, onglet « Branches ») : on le fait apparaître dans la liste.
    private void Commits_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Commits.SelectedItem is { } row) Commits.ScrollIntoView(row);
    }

    // Dans l'aperçu, un double-clic ouvre la grande fenêtre sur ce commit.
    private void Commits_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HistoryViewModel { WithDetails: false } vm && vm.SelectedRow is not null) vm.OpenLargeCommand.Execute(null);
    }
}
