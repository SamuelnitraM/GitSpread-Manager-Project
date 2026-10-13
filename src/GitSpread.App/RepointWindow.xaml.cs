using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using GitSpread.App.ViewModels;

namespace GitSpread.App;

public partial class RepointWindow : Window
{
    private readonly RepointViewModel _vm;

    public RepointWindow(RepointViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Title = vm.Title;
        vm.CloseRequested += (_, _) => Close();
        vm.ShowErrorsRequested += (_, _) => Dispatcher.BeginInvoke(() => Controls.Field.FocusFirstError(this), DispatcherPriority.Loaded);
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Pas d'abandon à mi-chemin : le retour arrière doit pouvoir se faire jusqu'au bout.
        if (_vm.Step == RepointStep.Running) e.Cancel = true;
        base.OnClosing(e);
    }
}
