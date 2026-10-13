using System.ComponentModel;
using System.Windows;
using GitSpread.App.ViewModels;

namespace GitSpread.App;

public partial class PublishWindow : Window
{
    private readonly PublishViewModel _vm;

    public PublishWindow(PublishViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Title = $"Publier sur GitHub · {vm.ProjectName}";
        vm.CloseRequested += (_, _) => Close();
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Ne pas abandonner une publication à mi-chemin (repo créé mais pas encore poussé).
        if (_vm.Step == PublishStep.Running) e.Cancel = true;
        base.OnClosing(e);
    }
}
