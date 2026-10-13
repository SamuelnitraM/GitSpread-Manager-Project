using System.ComponentModel;
using System.Windows;
using GitSpread.App.ViewModels;

namespace GitSpread.App;

public partial class ScopesWindow : Window
{
    private readonly AccountScopesViewModel _vm;

    public ScopesWindow(AccountScopesViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Title = $"Autorisations · {vm.Login}";
        vm.CloseRequested += (_, _) => Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Fermer pendant la validation l'annule : gh est arrêté et le compte actif d'origine revient.
        if (_vm.Step == ScopesStep.Running) _vm.CancelCommand.Execute(null);
        base.OnClosing(e);
    }
}
