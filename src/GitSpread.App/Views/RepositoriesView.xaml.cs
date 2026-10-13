using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitSpread.App.ViewModels;

namespace GitSpread.App.Views;

public partial class RepositoriesView : UserControl
{
    public RepositoriesView() => InitializeComponent();

    private void Repository_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is RepositoryItemViewModel repo) repo.OpenCommand.Execute(null);
    }
}
