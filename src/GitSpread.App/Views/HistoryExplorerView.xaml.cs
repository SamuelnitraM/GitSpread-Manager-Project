using System.Windows.Controls;
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
}
