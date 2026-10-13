using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace GitSpread.App.Controls;

/// <summary>Comportements de saisie attachables à un TextBox.</summary>
public static partial class TextInput
{
    /// <summary>Remplace chaque espace par un tiret pendant la frappe ou le collage, sans déplacer le curseur.</summary>
    public static readonly DependencyProperty SpacesToDashProperty = DependencyProperty.RegisterAttached(
        "SpacesToDash", typeof(bool), typeof(TextInput), new PropertyMetadata(false, OnSpacesToDashChanged));

    public static bool GetSpacesToDash(DependencyObject d) => (bool)d.GetValue(SpacesToDashProperty);
    public static void SetSpacesToDash(DependencyObject d, bool value) => d.SetValue(SpacesToDashProperty, value);

    private static void OnSpacesToDashChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.TextChanged -= ReplaceSpaces;
        if (e.NewValue is true) box.TextChanged += ReplaceSpaces;
    }

    private static void ReplaceSpaces(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (!Whitespace().IsMatch(box.Text)) return;
        var caret = box.CaretIndex;
        box.Text = Whitespace().Replace(box.Text, "-"); // même longueur : le curseur reste à sa place
        box.CaretIndex = caret;
    }

    [GeneratedRegex(@"\s")]
    private static partial Regex Whitespace();
}
