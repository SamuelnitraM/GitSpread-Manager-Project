using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitSpread.App.ViewModels;

namespace GitSpread.App.Views;

public partial class HistoryExplorerView : UserControl
{
    public HistoryExplorerView() => InitializeComponent();

    // Une branche choisie dans l'onglet « Branches » : on montre son dernier commit dans le graphe.
    private void Branch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is HistoryExplorerViewModel vm && BranchList.SelectedItem is BranchRowViewModel branch)
            vm.History.SelectBranchCommand.Execute(branch);
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
