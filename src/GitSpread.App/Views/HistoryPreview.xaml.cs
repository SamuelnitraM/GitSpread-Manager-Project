using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using GitSpread.App.ViewModels;

namespace GitSpread.App.Views;

public partial class HistoryPreview : UserControl
{
    public const double MinHeightPreview = 110;

    public HistoryPreview() => InitializeComponent();

    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;

    // Tirer le bord supérieur vers le haut agrandit l'aperçu ; la liste au-dessus garde au moins 220 px.
    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Main is not { } main || Window.GetWindow(this) is not { } window) return;
        var max = Math.Max(MinHeightPreview, window.ActualHeight - 360);
        main.HistoryPreviewHeight = Math.Clamp(main.HistoryPreviewHeight - e.VerticalChange, MinHeightPreview, max);
    }

    private void Resize_DragCompleted(object sender, DragCompletedEventArgs e) => Main?.SaveHistoryPreviewHeight();
}
