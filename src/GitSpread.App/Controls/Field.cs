using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GitSpread.App.Controls;

/// <summary>
/// Champ de formulaire : libellé, contrôle de saisie, aide, et erreur affichée en rouge sous le champ.
/// <see cref="FocusFirstError"/> amène la vue sur la première erreur et y place le curseur.
/// </summary>
public sealed class Field : ContentControl
{
    static Field() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Field), new FrameworkPropertyMetadata(typeof(Field)));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(Field), new PropertyMetadata(null));

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(Field), new PropertyMetadata(null, OnMessageChanged));

    public static readonly DependencyProperty ErrorProperty =
        DependencyProperty.Register(nameof(Error), typeof(string), typeof(Field), new PropertyMetadata(null, OnMessageChanged));

    public static readonly DependencyProperty IsCheckingProperty =
        DependencyProperty.Register(nameof(IsChecking), typeof(bool), typeof(Field), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey HasErrorKey =
        DependencyProperty.RegisterReadOnly(nameof(HasError), typeof(bool), typeof(Field), new PropertyMetadata(false));

    public static readonly DependencyProperty HasErrorProperty = HasErrorKey.DependencyProperty;

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? Hint { get => (string?)GetValue(HintProperty); set => SetValue(HintProperty, value); }
    public string? Error { get => (string?)GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }

    /// <summary>Vérification en cours (ex. le nom est-il déjà pris sur GitHub ?).</summary>
    public bool IsChecking { get => (bool)GetValue(IsCheckingProperty); set => SetValue(IsCheckingProperty, value); }

    public bool HasError => (bool)GetValue(HasErrorProperty);

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        UpdateAutomation();
    }

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (Field)d;
        field.SetValue(HasErrorKey, !string.IsNullOrEmpty(field.Error));
        field.UpdateAutomation();
    }

    // Les lecteurs d'écran annoncent l'erreur (ou l'aide) avec le champ.
    private void UpdateAutomation()
    {
        if (Content is not DependencyObject input) return;
        AutomationProperties.SetHelpText(input, Error ?? Hint ?? "");
        if (Label is { Length: > 0 } label && string.IsNullOrEmpty(AutomationProperties.GetName(input)))
            AutomationProperties.SetName(input, label);
    }

    /// <summary>Fait défiler jusqu'au premier champ en erreur visible et y met le focus. Faux s'il n'y en a pas.</summary>
    public static bool FocusFirstError(DependencyObject root)
    {
        if (FindFirst(root) is not { } field) return false;
        field.BringIntoView();
        if (FirstFocusable(field) is { } input)
        {
            input.Focus();
            Keyboard.Focus(input);
            if (input is TextBox box) box.SelectAll();
        }
        return true;
    }

    private static Field? FindFirst(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { IsVisible: false }) continue;
            if (child is Field { HasError: true } field) return field;
            if (FindFirst(child) is { } found) return found;
        }
        return null;
    }

    private static IInputElement? FirstFocusable(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Control { Focusable: true, IsEnabled: true, IsVisible: true } control and (TextBox or PasswordBox or ComboBox or ListBox))
                return control;
            if (FirstFocusable(child) is { } found) return found;
        }
        return null;
    }
}
