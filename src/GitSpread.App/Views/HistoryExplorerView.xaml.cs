using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitSpread.App.ViewModels;

namespace GitSpread.App.Views;

public partial class HistoryExplorerView : UserControl
{
    public HistoryExplorerView()
    {
        InitializeComponent();
        // Les pastilles de couleur des branches prennent la palette du nouveau thème.
        Loaded += (_, _) => { ThemeManager.Changed -= OnThemeChanged; ThemeManager.Changed += OnThemeChanged; };
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => BranchList.Items.Refresh();

    // Une branche choisie dans l'onglet « Branches » : on montre son dernier commit dans le graphe.
    private void Branch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is HistoryExplorerViewModel vm && BranchList.SelectedItem is BranchRowViewModel branch)
            vm.History.SelectBranchCommand.Execute(branch);
    }

    // Arborescence des fichiers (F6e) : le TreeView ne permet pas de lier l'élément choisi, on le transmet ici.
    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is HistoryExplorerViewModel vm) vm.History.SelectedNode = e.NewValue as FileTreeNode;
    }

    // Recherche : Entrée passe au résultat suivant, Maj+Entrée au précédent, Échap efface.
    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HistoryExplorerViewModel { History: var history }) return;
        if (e.Key == Key.Enter)
        {
            SearchBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); // sans attendre le délai de frappe
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) history.PreviousMatchCommand.Execute(null);
            else history.NextMatchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && history.HasSearch)
        {
            history.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Fichiers modifiés et modifications défilent d'abord eux-mêmes ; arrivés en haut ou en bas,
    /// la molette fait défiler toute la colonne de droite (sinon elle resterait « bloquée » dans la liste).
    /// </summary>
    private void Nested_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject list || FindScrollViewer(list) is not { } inner) return;
        var atTop = inner.VerticalOffset <= 0;
        var atBottom = inner.VerticalOffset >= inner.ScrollableHeight;
        if ((e.Delta > 0 && !atTop) || (e.Delta < 0 && !atBottom)) return; // la liste peut encore défiler
        e.Handled = true;
        DetailsScroll.ScrollToVerticalOffset(DetailsScroll.VerticalOffset - e.Delta);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, i)) is { } found) return found;
        return null;
    }
}
