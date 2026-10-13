using System.Windows;
using Microsoft.Win32;

namespace GitSpread.App;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Applique le thème : contrôles WPF « Fluent » (ThemeMode) + palette GitSpread (Themes/Light.xaml ou Dark.xaml).
/// En mode Système, suit le réglage clair / sombre de Windows en direct.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri LightPalette = new("Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkPalette = new("Themes/Dark.xaml", UriKind.Relative);
    private static bool _listening;

    public static AppTheme Current { get; private set; } = AppTheme.System;

    public static event EventHandler? Changed;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var dark = theme == AppTheme.Dark || theme == AppTheme.System && !WindowsUsesLightTheme();

        var app = Application.Current;
#pragma warning disable WPF0001 // ThemeMode (Fluent) est encore marqué « évaluation » dans WPF .NET 10
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var palettes = app.Resources.MergedDictionaries;
        var existing = palettes.FirstOrDefault(d => d.Source == LightPalette || d.Source == DarkPalette);
        var wanted = dark ? DarkPalette : LightPalette;
        if (existing?.Source != wanted)
        {
            if (existing is not null) palettes.Remove(existing);
            palettes.Add(new ResourceDictionary { Source = wanted });
        }

        if (theme == AppTheme.System && !_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static AppTheme Parse(string? value) => Enum.TryParse<AppTheme>(value, ignoreCase: true, out var t) ? t : AppTheme.System;

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Current == AppTheme.System && e.Category == UserPreferenceCategory.General)
            Application.Current.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
    }

    private static bool WindowsUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}
