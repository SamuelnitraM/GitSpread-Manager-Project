using CommunityToolkit.Mvvm.ComponentModel;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;
using GitSpread.Core.Settings;

namespace GitSpread.App.ViewModels;

/// <summary>
/// Préférences d'affichage lues partout dans l'interface (F6d) : les fenêtres y accèdent directement,
/// quelle que soit leur source de données.
/// </summary>
public sealed partial class DisplayPreferences : ObservableObject
{
    public static DisplayPreferences Current { get; } = new();

    /// <summary>Afficher les commandes git que GitSpread va exécuter, puis celles qu'il a exécutées.</summary>
    [ObservableProperty] public partial bool ShowCommands { get; set; } = true;

    /// <summary>Dates complètes au lieu de relatives.</summary>
    [ObservableProperty] public partial bool FullDates { get; set; }

    public void Apply(AppSettings settings)
    {
        ShowCommands = settings.ShowCommands;
        FullDates = settings.FullDates;
    }

    /// <summary>Date affichée selon la préférence : « il y a 2 j » ou « 13/10/2026 14:05 ».</summary>
    public static string Date(DateTimeOffset date) => Format(date, Current.FullDates, DateTimeOffset.Now);

    public static string Format(DateTimeOffset date, bool full, DateTimeOffset now)
    {
        if (full) return date.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        var elapsed = now - date;
        return elapsed switch
        {
            { TotalMinutes: < 1 } => "à l'instant",
            { TotalHours: < 1 } => $"il y a {(int)elapsed.TotalMinutes} min",
            { TotalDays: < 1 } => $"il y a {(int)elapsed.TotalHours} h",
            { TotalDays: < 30 } => $"il y a {(int)elapsed.TotalDays} j",
            { TotalDays: < 365 } => $"il y a {(int)(elapsed.TotalDays / 30)} mois",
            _ => $"il y a {(int)(elapsed.TotalDays / 365)} an{((int)(elapsed.TotalDays / 365) > 1 ? "s" : "")}",
        };
    }
}

/// <summary>Identité proposée pour signer les commits d'un compte, selon la préférence « adresse privée ou réelle ».</summary>
public static class Signing
{
    public static GitIdentity IdentityFor(this IProjectHost host, GitHubAccount account, GitHubUser user) =>
        new(user.Name ?? user.Login, host.Settings.SigningEmail(account.Host, account.Login, user.NoReplyEmail, user.Email));

    /// <summary>« adresse privée GitHub » ou « adresse réelle du compte » (la privée si aucune réelle n'est connue).</summary>
    public static string EmailKind(this IProjectHost host) =>
        host.Settings.PreferRealEmail ? "adresse réelle du compte si elle est connue" : "adresse privée GitHub";
}
