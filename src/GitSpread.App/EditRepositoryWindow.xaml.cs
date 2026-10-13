using System.Windows;
using System.Windows.Threading;
using GitSpread.App.ViewModels;

namespace GitSpread.App;

public partial class EditRepositoryWindow : Window
{
    public EditRepositoryWindow(EditRepositoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Title = $"Modifier · {vm.FullName}";
        vm.CloseRequested += (_, _) => Close();
        vm.ShowErrorsRequested += (_, _) => Dispatcher.BeginInvoke(() => Controls.Field.FocusFirstError(this), DispatcherPriority.Loaded);
    }
}
