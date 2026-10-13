using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace GitSpread.App;

/// <summary>Petites fenêtres génériques (choix multiple, saisie), construites en code pour rester homogènes.</summary>
internal static class SmallDialogs
{
    public static int Choose(Window? owner, string title, string message, IReadOnlyList<string> options)
    {
        var result = -1;
        var window = Create(owner, title);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Style = (Style)Application.Current.Resources[i == 0 ? "PrimaryButton" : "SecondaryButton"],
                Content = new TextBlock { Text = options[i], TextWrapping = TextWrapping.Wrap },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(14, 8, 14, 8),
            };
            AutomationProperties.SetName(button, options[i]);
            button.Click += (_, _) => { result = index; window.Close(); };
            panel.Children.Add(button);
        }
        var cancel = new Button { Style = (Style)Application.Current.Resources["SecondaryButton"], Content = "Annuler", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => window.Close();
        panel.Children.Add(cancel);
        window.Content = panel;
        window.ShowDialog();
        return result;
    }

    public static string? Prompt(Window? owner, string title, string message, string initial,
        Func<string, string?>? validate = null, bool spacesToDash = false)
    {
        string? result = null;
        var submitted = false;
        var window = Create(owner, title);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var input = new TextBox { Text = initial, Padding = new Thickness(6, 4, 6, 4) };
        AutomationProperties.SetName(input, title);
        Controls.TextInput.SetSpacesToDash(input, spacesToDash);
        var field = new Controls.Field { Content = input };
        // L'erreur s'affiche dès que l'on tape quelque chose (ou après une première validation pour un champ vide).
        input.TextChanged += (_, _) => field.Error = validate is null || (input.Text.Length == 0 && !submitted) ? null : validate(input.Text.Trim());
        panel.Children.Add(field);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Style = (Style)Application.Current.Resources["SecondaryButton"], Content = "Annuler", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Style = (Style)Application.Current.Resources["PrimaryButton"], Content = "Valider", IsDefault = true };
        cancel.Click += (_, _) => window.Close();
        ok.Click += (_, _) =>
        {
            submitted = true;
            field.Error = validate?.Invoke(input.Text.Trim());
            if (field.Error is not null)
            {
                Controls.Field.FocusFirstError(window);
                return;
            }
            result = input.Text.Trim();
            window.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); Keyboard.Focus(input); };
        window.ShowDialog();
        return result;
    }

    private static Window Create(Window? owner, string title)
    {
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
        };
        window.SetResourceReference(Control.BackgroundProperty, "Bg");
        window.SetResourceReference(Control.ForegroundProperty, "Text");
        return window;
    }
}
