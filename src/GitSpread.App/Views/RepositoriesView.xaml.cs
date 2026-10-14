using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitSpread.App.ViewModels;

namespace GitSpread.App.Views;

public partial class RepositoriesView : UserControl
{
    public RepositoriesView()
    {
        InitializeComponent();
        // Les pastilles de couleur des branches prennent la palette du nouveau thème.
        Loaded += (_, _) => { ThemeManager.Changed -= OnThemeChanged; ThemeManager.Changed += OnThemeChanged; };
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => GitHubBranches.Items.Refresh();

    // Site web du repo : saisi par son propriétaire, on n'ouvre qu'une vraie adresse web (jamais file:, ms-…, etc.).
    private void Homepage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.DataContext is not GitHubRepositoryViewModel { Details.Homepage: { } homepage }) return;
        var address = homepage.Contains("://") ? homepage.Trim() : "https://" + homepage.Trim();
        if (Shell.IsWebAddress(address)) Shell.Open(address);
    }

    private void Repository_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is RepositoryItemViewModel repo) repo.OpenCommand.Execute(null);
    }
}
