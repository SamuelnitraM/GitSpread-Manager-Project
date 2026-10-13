using System.ComponentModel;
using System.Windows;
using GitSpread.App.ViewModels;

namespace GitSpread.App;

public partial class LoginWindow : Window
{
    private readonly LoginWizardViewModel _vm;

    public LoginWindow(LoginWizardViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.CloseRequested += (_, _) => Close();
    }

    // Les PasswordBox ne se lient pas : le secret ne transite que par ces deux setters.
    private void Token_Changed(object sender, RoutedEventArgs e) => _vm.Token = TokenBox.Password;
    private void Passphrase_Changed(object sender, RoutedEventArgs e) => _vm.NewKeyPassphrase = PassphraseBox.Password;

    private void CreateToken_Click(object sender, RoutedEventArgs e)
    {
        var host = _vm.Host.Length > 0 ? _vm.Host : "github.com";
        Shell.Open($"https://{host}/settings/tokens/new?description=GitSpread&scopes=repo,read:org,gist,admin:public_key");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Fermer pendant la connexion l'annule (le processus gh est arrêté).
        if (_vm.Step == LoginStep.Running) _vm.CancelCommand.Execute(null);
        base.OnClosing(e);
    }
}
