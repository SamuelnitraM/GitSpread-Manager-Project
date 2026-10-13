using System.Windows;
using GitSpread.App.ViewModels;

namespace GitSpread.App;

public partial class CommitWindow : Window
{
    public CommitWindow(CommitViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Title = $"Commit · {vm.ProjectName}";
    }

    private void Commit_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
