using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitSpread.App.ViewModels;

/// <summary>
/// F6e : recherche dans l'historique chargé (message, auteur, SHA, branche ou tag). Le graphe reste entier :
/// les commits qui ne correspondent pas sont estompés, et « suivant / précédent » passe d'un résultat à l'autre.
/// </summary>
public sealed partial class HistoryViewModel
{
    private List<HistoryRowViewModel> _matches = [];

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string? SearchSummary { get; set; }

    public bool HasSearch => SearchText.Trim().Length > 0;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearch));
        ApplySearch(selectFirst: true);
    }

    /// <summary>Estompe les commits qui ne correspondent pas et résume les résultats (appelé aussi après chaque chargement).</summary>
    private void ApplySearch(bool selectFirst)
    {
        var text = SearchText.Trim();
        _matches = text.Length == 0 ? [] : Rows.Where(r => Matches(r, text)).ToList();
        foreach (var row in Rows) row.IsDimmed = text.Length > 0 && !_matches.Contains(row);
        SearchSummary = text.Length == 0 ? null
            : _matches.Count == 0 ? (HasMore ? $"Aucun résultat dans les {Rows.Count} commits chargés : « Afficher plus » pour chercher plus loin." : "Aucun résultat.")
            : $"{_matches.Count} résultat{(_matches.Count > 1 ? "s" : "")}" + (HasMore ? $" dans les {Rows.Count} commits chargés" : "");
        if (selectFirst && _matches.Count > 0 && (SelectedRow is null || !_matches.Contains(SelectedRow))) SelectedRow = _matches[0];
    }

    internal static bool Matches(HistoryRowViewModel row, string text) =>
        row.Subject.Contains(text, StringComparison.OrdinalIgnoreCase)
        || row.Author.Contains(text, StringComparison.OrdinalIgnoreCase)
        || row.Commit.AuthorEmail.Contains(text, StringComparison.OrdinalIgnoreCase)
        || (text.Length >= 4 && row.Commit.Sha.StartsWith(text, StringComparison.OrdinalIgnoreCase))
        || row.Refs.Any(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void NextMatch() => MoveMatch(+1);

    [RelayCommand]
    private void PreviousMatch() => MoveMatch(-1);

    private void MoveMatch(int step)
    {
        if (_matches.Count == 0) return;
        var index = SelectedRow is { } row ? _matches.IndexOf(row) : -1;
        index = index < 0 ? (step > 0 ? 0 : _matches.Count - 1) : (index + step + _matches.Count) % _matches.Count;
        SelectedRow = _matches[index];
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";
}
