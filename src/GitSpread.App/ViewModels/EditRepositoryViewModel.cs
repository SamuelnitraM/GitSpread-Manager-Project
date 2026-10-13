using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.GitHub;

namespace GitSpread.App.ViewModels;

/// <summary>Description, site web et sujets d'un repo GitHub.</summary>
public sealed partial class EditRepositoryViewModel : FormViewModel
{
    private readonly GitHubAccount _account;
    private readonly GitHubRepositoryDetails _details;
    private readonly IProjectHost _host;

    public EditRepositoryViewModel(GitHubAccount account, GitHubRepositoryDetails details, IProjectHost host)
    {
        _account = account;
        _details = details;
        _host = host;
        Description = details.Description ?? "";
        Homepage = details.Homepage ?? "";
        TopicsText = string.Join(", ", details.Topics);
    }

    public event EventHandler? CloseRequested;

    public string FullName => _details.FullName;
    public bool Saved { get; private set; }
    public IReadOnlyList<string> Command { get; private set; } = [];

    [ObservableProperty] public partial string Description { get; set; }
    [ObservableProperty] public partial string Homepage { get; set; }
    [ObservableProperty] public partial string TopicsText { get; set; }
    [ObservableProperty] public partial string? HomepageError { get; set; }
    [ObservableProperty] public partial string? TopicsError { get; set; }
    [ObservableProperty] public partial string? SaveError { get; set; }
    [ObservableProperty] public partial bool IsSaving { get; set; }

    /// <summary>Sujets tels qu'ils seront envoyés (minuscules, espaces → tirets).</summary>
    public string TopicsPreview => RepositoryTopics.Parse(TopicsText) is { Count: > 0 } t ? "Sujets : " + string.Join(" · ", t) : "Aucun sujet.";

    partial void OnHomepageChanged(string value) { if (HomepageError is not null) Validate(); }
    partial void OnTopicsTextChanged(string value) { OnPropertyChanged(nameof(TopicsPreview)); if (TopicsError is not null || Submitted) Validate(); }

    private bool Validate()
    {
        var url = Homepage.Trim();
        HomepageError = url.Length == 0 || Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
            ? null : "Adresse invalide : commence par https:// (ex. https://monsite.fr).";
        TopicsError = RepositoryTopics.Validate(RepositoryTopics.Parse(TopicsText));
        return HomepageError is null && TopicsError is null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Submitted = true;
        if (!Validate())
        {
            RequestShowErrors();
            return;
        }

        var topics = RepositoryTopics.Parse(TopicsText);
        var add = topics.Except(_details.Topics).ToList();
        var remove = _details.Topics.Except(topics).ToList();
        string? description = Description.Trim() == (_details.Description ?? "") ? null : Description.Trim();
        string? homepage = Homepage.Trim() == (_details.Homepage ?? "") ? null : Homepage.Trim();
        Command = GitHubService.BuildEditArguments(FullName, description, homepage, add, remove).Prepend("gh").ToList();

        IsSaving = true;
        SaveError = null;
        try
        {
            await _host.Services.GitHub.EditRepositoryAsync(_account, FullName, description, homepage, add, remove);
            Saved = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { SaveError = e.Message; }
        finally { IsSaving = false; }
    }

    [RelayCommand] private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
