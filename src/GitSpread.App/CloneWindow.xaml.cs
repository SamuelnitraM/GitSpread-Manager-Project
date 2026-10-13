using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using GitSpread.App.ViewModels;
using Microsoft.Win32;

namespace GitSpread.App;

public partial class CloneWindow : Window
{
    private readonly CloneViewModel _vm;

    public CloneWindow(CloneViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Title = $"Cloner · {vm.FullName}";
        vm.CloseRequested += (_, _) => Close();
        vm.ShowErrorsRequested += (_, _) => Dispatcher.BeginInvoke(() => Controls.Field.FocusFirstError(this), DispatcherPriority.Loaded);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Dossier où créer le projet", InitialDirectory = _vm.ParentFolder };
        if (dialog.ShowDialog(this) == true) _vm.ParentFolder = dialog.FolderName;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.Step == CloneStep.Running) e.Cancel = true; // un clonage interrompu laisserait un dossier incomplet
        base.OnClosing(e);
    }
}
