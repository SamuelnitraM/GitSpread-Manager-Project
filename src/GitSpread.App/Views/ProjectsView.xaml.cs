using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitSpread.App.ViewModels;

namespace GitSpread.App.Views;

public partial class ProjectsView : UserControl
{
    private Point _pressPoint;
    private ProjectItemViewModel? _pressed;

    public ProjectsView() => InitializeComponent();

    // Glisser un projet sur l'onglet d'un autre compte = le passer sur ce compte (étape 5).
    private void Projects_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        _pressed = ItemAt(e.OriginalSource as DependencyObject)?.DataContext as ProjectItemViewModel;
    }

    private void Projects_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is not { } project) return;
        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2 && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        _pressed = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(MainWindow.ProjectFormat, project), DragDropEffects.Move);
    }

    private static ListViewItem? ItemAt(DependencyObject? element)
    {
        // Un Run (texte) n'est pas un élément visuel : on remonte alors par l'arbre logique.
        while (element is not null and not ListViewItem)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as ListViewItem;
    }
}
