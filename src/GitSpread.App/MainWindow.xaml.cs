using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GitSpread.App.ViewModels;

namespace GitSpread.App;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand AddAccountCommand = new(nameof(AddAccountCommand), typeof(MainWindow));

    public MainWindow()
    {
        InitializeComponent();
        // Taille de base 1400 × 860, ramenée à l'écran s'il est plus petit.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 20);
        Height = Math.Min(Height, area.Height - 20);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>Deux comptes : bascule directe. Plus de deux : menu pour choisir le compte.</summary>
    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountTabViewModel from } button) return;
        var targets = ViewModel.SwitchTargets(from);

        if (targets.Count == 1)
        {
            await ViewModel.SwitchToAsync(targets[0]);
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Rendre actif :", IsEnabled = false });
        foreach (var target in targets)
        {
            var item = new MenuItem { Header = target.Login };
            item.Click += async (_, _) => await ViewModel.SwitchToAsync(target);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e) => AddAccount();
    private void AddAccount_Executed(object sender, ExecutedRoutedEventArgs e) => AddAccount();

    private async void AddAccount()
    {
        var wizard = new LoginWizardViewModel(ViewModel.Services, ViewModel.AccountTabs.Select(t => t.Account).ToList());
        new LoginWindow(wizard) { Owner = this }.ShowDialog();
        if (wizard.Completed) await ViewModel.RefreshAsync();
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier du projet à ajouter", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await ViewModel.AddProjectsAsync(dialog.FolderNames);
    }

    private async void Folders_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FoldersWindow(ViewModel.Settings.RootFolders, ViewModel.Settings.ScanDepth) { Owner = this };
        if (dialog.ShowDialog() == true)
            await ViewModel.UpdateRootFoldersAsync(dialog.Folders, dialog.Depth);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ViewModel.LogDirectory);
        Shell.Open(ViewModel.LogDirectory);
    }

    // ------------------------------------------------------------ menu d'un compte (F5)

    private void Tab_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (sender is not TabItem { DataContext: AccountTabViewModel tab } item) return;
        var account = tab.Account;

        var menu = new ContextMenu { PlacementTarget = item, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = account.Login, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());

        var makeActive = new MenuItem
        {
            Header = account.IsActive ? "Compte actif de gh" : "Rendre actif dans gh",
            IsEnabled = !account.IsActive && account.IsAuthenticated,
            ToolTip = "Compte utilisé par gh dans ton terminal. GitSpread, lui, choisit toujours le bon compte pour chaque projet.",
        };
        makeActive.Click += async (_, _) => await ViewModel.SwitchToAsync(tab);
        menu.Items.Add(makeActive);

        var scopes = new MenuItem { Header = "Autorisations du compte…", IsEnabled = account.IsAuthenticated };
        scopes.Click += async (_, _) => await ViewModel.OpenScopesAsync(account);
        menu.Items.Add(scopes);

        var profile = new MenuItem { Header = "Voir le profil sur GitHub" };
        profile.Click += (_, _) => Shell.Open($"https://{account.Host}/{account.Login}");
        menu.Items.Add(profile);

        menu.Items.Add(new Separator());
        var logout = new MenuItem { Header = "Déconnecter ce compte…" };
        logout.Click += async (_, _) => await ViewModel.LogoutAsync(account);
        menu.Items.Add(logout);

        menu.IsOpen = true;
    }

    // ------------------------------------------------------------ thème

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var (theme, label) in new[] { (AppTheme.System, "Comme Windows"), (AppTheme.Light, "Clair"), (AppTheme.Dark, "Sombre") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = ThemeManager.Current == theme };
            item.Click += (_, _) =>
            {
                ThemeManager.Apply(theme);
                ViewModel.SetTheme(theme.ToString());
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------ glisser-déposer des onglets (et des projets vers un onglet)

    public const string TabFormat = "GitSpread.Tab";
    public const string ProjectFormat = "GitSpread.Project";

    private Point _pressPoint;
    private TabItem? _pressedTab;

    private void Tab_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        _pressedTab = sender as TabItem;
    }

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedTab != sender || _pressedTab.DataContext is not TabViewModel tab) return;
        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _pressedTab = null;
        tab.IsBeingDragged = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(TabFormat, tab), DragDropEffects.Move); }
        finally
        {
            tab.IsBeingDragged = false;
            foreach (var t in ViewModel.Tabs) t.DropHint = DropHint.None;
        }
    }

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (sender is not TabItem { DataContext: TabViewModel target } item) return;
        foreach (var t in ViewModel.Tabs.Where(t => t != target)) t.DropHint = DropHint.None;

        if (e.Data.GetData(TabFormat) is TabViewModel dragged)
        {
            if (dragged == target) { target.DropHint = DropHint.None; return; }
            target.DropHint = e.GetPosition(item).X < item.ActualWidth / 2 ? DropHint.InsertBefore : DropHint.InsertAfter;
            e.Effects = DragDropEffects.Move;
        }
        else if (e.Data.GetData(ProjectFormat) is ProjectItemViewModel project && CanReceive(target, project))
        {
            target.DropHint = DropHint.Project;
            e.Effects = DragDropEffects.Move;
        }
        else target.DropHint = DropHint.None;
    }

    private void Tab_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is TabItem { DataContext: TabViewModel target }) target.DropHint = DropHint.None;
    }

    private async void Tab_Drop(object sender, DragEventArgs e)
    {
        if (sender is not TabItem { DataContext: TabViewModel target } item) return;
        var hint = target.DropHint;
        target.DropHint = DropHint.None;
        e.Handled = true;

        if (e.Data.GetData(TabFormat) is TabViewModel dragged && dragged != target)
            ViewModel.MoveTab(dragged, target, after: hint == DropHint.InsertAfter || e.GetPosition(item).X >= item.ActualWidth / 2);
        else if (e.Data.GetData(ProjectFormat) is ProjectItemViewModel project && CanReceive(target, project))
        {
            if (target is AccountTabViewModel account) await project.ChangeLinkedRepositoryAsync(account.Account);
            else await project.AssignToAsync(null);
        }
    }

    /// <summary>
    /// Un projet peut être déposé sur l'onglet d'un autre compte connecté : passer le projet sur ce compte (étape 5),
    /// ou, s'il n'a pas encore de remote (même pas suivi par git), le ranger dans les projets locaux de ce compte.
    /// Un projet rangé ainsi peut être redéposé sur « Non liés ».
    /// </summary>
    private static bool CanReceive(TabViewModel target, ProjectItemViewModel project) => target switch
    {
        AccountTabViewModel { Account.IsAuthenticated: true } account => !account.Account.Is(project.Placement.Account),
        UnlinkedTabViewModel => project.IsAssigned,
        _ => false,
    };
}
