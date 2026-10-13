using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.GitHub;

namespace GitSpread.App.ViewModels;

/// <summary>Repo GitHub prêt à afficher (libellés calculés une fois pour toutes).</summary>
public sealed partial class RepositoryItemViewModel(GitHubRepository repository)
{
    public GitHubRepository Repository { get; } = repository;

    public string Name => Repository.Name;
    public string? Description => Repository.Description;
    public bool HasDescription => Repository.Description is not null;
    public string Url => Repository.Url;
    public bool IsPrivate => Repository.Visibility != RepositoryVisibility.Public;
    public bool IsArchived => Repository.IsArchived;
    public bool IsFork => Repository.IsFork;

    public string VisibilityLabel => Repository.Visibility switch
    {
        RepositoryVisibility.Private => "Privé",
        RepositoryVisibility.Internal => "Interne",
        _ => "Public",
    };

    public string PushedLabel => Repository.PushedAt is { } date ? Relative(date) : "jamais";

    public string PushedTooltip => Repository.PushedAt?.LocalDateTime.ToString("dddd d MMMM yyyy 'à' HH:mm") ?? "";

    public bool Matches(string filter) =>
        filter.Length == 0
        || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    [RelayCommand] private void Open() => Shell.Open(Url);
    [RelayCommand] private void CopyHttpsUrl() => Shell.Copy(Url + ".git");
    [RelayCommand] private void CopySshUrl() => Shell.Copy(Repository.SshUrl);

    internal static string Relative(DateTimeOffset date)
    {
        var elapsed = DateTimeOffset.Now - date;
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
