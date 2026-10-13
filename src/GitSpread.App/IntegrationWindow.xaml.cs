using System.Windows;
using GitSpread.App.ViewModels;

namespace GitSpread.App;

public partial class IntegrationWindow : Window
{
    public IntegrationWindow(IntegrationViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Title = vm.Title;
    }

    private void Run_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
