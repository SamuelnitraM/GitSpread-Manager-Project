using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;

namespace GitSpread.App;

public partial class FoldersWindow : Window
{
    private readonly ObservableCollection<string> _folders;

    public FoldersWindow(IEnumerable<string> folders, int depth)
    {
        InitializeComponent();
        _folders = new ObservableCollection<string>(folders);
        _folders.CollectionChanged += (_, _) => UpdateHint();
        FolderList.ItemsSource = _folders;
        DepthBox.ItemsSource = Enumerable.Range(1, 8).ToList();
        DepthBox.SelectedItem = Math.Clamp(depth, 1, 8);
        UpdateHint();
    }

    public IReadOnlyList<string> Folders => _folders;
    public int Depth => DepthBox.SelectedItem as int? ?? 4;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Dossier contenant des projets", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var folder in dialog.FolderNames)
            if (!_folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) _folders.Add(folder);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var folder in FolderList.SelectedItems.Cast<string>().ToList()) _folders.Remove(folder);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void UpdateHint() => EmptyHint.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
