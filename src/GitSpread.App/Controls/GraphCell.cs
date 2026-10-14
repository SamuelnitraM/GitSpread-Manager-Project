using System.Windows;
using System.Windows.Media;
using GitSpread.Core.Git;

namespace GitSpread.App.Controls;

/// <summary>Couleurs des branches, lues dans la palette du thème (Themes/Light.xaml, Themes/Dark.xaml).</summary>
public static class BranchBrushes
{
    public const int Shades = 6;

    public static Brush For(BranchColor color) =>
        (Application.Current?.TryFindResource(Key(color)) as Brush) ?? Brushes.Gray;

    public static string Key(BranchColor color) => color switch
    {
        { Kind: BranchKind.Default } => "BranchMain",
        { Shade: < 0 } => "BranchNeutral",
        { Kind: BranchKind.Maintenance } => $"BranchMaint{color.Shade % Shades}",
        _ => $"BranchClassic{color.Shade % Shades}",
    };
}

/// <summary>
/// Une ligne du graphe des branches : les traits qui passent, ceux qui rejoignent le commit ou en partent,
/// et le point du commit (rond plein ; anneau pour une fusion ; point cerclé pour la position actuelle).
/// La branche principale est dessinée plus épaisse.
/// </summary>
public sealed class GraphCell : FrameworkElement
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(GraphRow), typeof(GraphCell),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsMergeProperty = DependencyProperty.Register(nameof(IsMerge), typeof(bool), typeof(GraphCell),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsHeadProperty = DependencyProperty.Register(nameof(IsHead), typeof(bool), typeof(GraphCell),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public GraphRow? Row { get => (GraphRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }
    public bool IsMerge { get => (bool)GetValue(IsMergeProperty); set => SetValue(IsMergeProperty, value); }
    public bool IsHead { get => (bool)GetValue(IsHeadProperty); set => SetValue(IsHeadProperty, value); }

    public GraphCell()
    {
        // Le thème peut changer pendant que la ligne est affichée : on la redessine avec la nouvelle palette.
        Loaded += (_, _) => ThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    private static double X(int lane) => lane * ViewModels.HistoryViewModel.LaneWidth + ViewModels.HistoryViewModel.LaneWidth / 2;

    protected override void OnRender(DrawingContext dc)
    {
        if (Row is not { } row) return;
        var h = ActualHeight;
        var mid = h / 2;
        foreach (var s in row.Top) Line(dc, s, 0, mid);
        foreach (var s in row.Bottom) Line(dc, s, mid, h);

        var main = row.NodeColor.Kind == BranchKind.Default;
        var brush = BranchBrushes.For(row.NodeColor);
        var center = new Point(X(row.NodeLane), mid);
        var r = main ? 5.5 : 4.5;
        var background = (Application.Current?.TryFindResource("Surface") as Brush) ?? Brushes.White;
        if (IsHead)
        {
            dc.DrawEllipse(background, new Pen(brush, 2), center, r + 3, r + 3); // position actuelle
            dc.DrawEllipse(brush, null, center, r - 1, r - 1);
        }
        else if (IsMerge) dc.DrawEllipse(background, new Pen(brush, 2.5), center, r, r); // fusion : anneau
        else dc.DrawEllipse(brush, null, center, r, r);
    }

    private static void Line(DrawingContext dc, GraphSegment s, double y1, double y2)
    {
        var pen = new Pen(BranchBrushes.For(s.Color), s.Color.Kind == BranchKind.Default ? 3 : 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double x1 = X(s.FromLane), x2 = X(s.ToLane);
        if (s.FromLane == s.ToLane)
        {
            dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
            return;
        }
        // Changement de couloir : une courbe douce plutôt qu'une diagonale.
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var ym = (y1 + y2) / 2;
            g.BeginFigure(new Point(x1, y1), false, false);
            g.BezierTo(new Point(x1, ym), new Point(x2, ym), new Point(x2, y2), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
