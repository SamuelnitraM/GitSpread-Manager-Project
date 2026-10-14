using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitSpread.App.ViewModels;
using Microsoft.Win32;

namespace GitSpread.App;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _main;

    public SettingsWindow(SettingsViewModel viewModel, MainViewModel main)
    {
        InitializeComponent();
        DataContext = viewModel;
        _main = main;
        Loaded += async (_, _) => await viewModel.LoadAsync();
        // Un champ en cours de saisie est enregistré même si on ferme sans le quitter.
        Closing += (_, _) => (Keyboard.FocusedElement as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Dossier contenant des projets", Multiselect = true };
        if (dialog.ShowDialog(this) == true) ViewModel.AddRootFolders(dialog.FolderNames);
    }

    private void BrowseClone_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Dossier où cloner les repos" };
        if (ViewModel.CloneFolder is { Length: > 0 } current && System.IO.Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) == true) ViewModel.CloneFolder = dialog.FolderName;
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new LoginWizardViewModel(_main.Services, _main.AccountTabs.Select(t => t.Account).ToList());
        new LoginWindow(wizard) { Owner = this }.ShowDialog();
        if (!wizard.Completed) return;
        await _main.RefreshAsync();
        ViewModel.RebuildAccounts();
    }
}
