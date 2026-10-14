using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GitSpread.App;

/// <summary>Visible quand la valeur (enum) vaut le paramètre ; « A|B » accepte plusieurs valeurs.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string names && names.Split('|').Contains(value.ToString())
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Lie un RadioButton à une valeur d'enum : coché quand la valeur vaut le paramètre.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

/// <summary>Choisit le modèle d'onglet selon qu'il s'agit d'un compte ou de l'onglet « Non liés ».</summary>
public sealed class TabTemplateSelector : System.Windows.Controls.DataTemplateSelector
{
    public DataTemplate? Account { get; set; }
    public DataTemplate? Other { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is ViewModels.AccountTabViewModel ? Account : Other;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Masqué quand la valeur est null (ou une chaîne vide).</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Couleur d'une branche (graphe, étiquettes) selon le thème courant.</summary>
public sealed class BranchColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Core.Git.BranchColor color ? Controls.BranchBrushes.For(color) : System.Windows.Media.Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
