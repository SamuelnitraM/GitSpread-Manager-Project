using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Git;
using Microsoft.Extensions.Logging;

namespace GitSpread.App.ViewModels;

/// <summary>Une ligne du contenu d'un fichier, avec son numéro.</summary>
public sealed record FileLine(int Number, string Text);

public enum FileViewMode { Changes, Content }

/// <summary>
/// Un dossier ou un fichier de l'arborescence d'un commit (F6e). Les dossiers ne construisent leurs enfants
/// qu'à leur ouverture : l'arborescence reste rapide même avec des milliers de fichiers.
/// </summary>
public sealed partial class FileTreeNode : ObservableObject
{
    private static readonly FileTreeNode Placeholder = new("…", "", isFolder: false, null, null, null);
    private IReadOnlyList<TreeEntry>? _pending;
    private readonly IReadOnlyDictionary<string, CommitFile>? _changes;

    private FileTreeNode(string label, string path, bool isFolder, TreeEntry? entry, CommitFile? change, IReadOnlyDictionary<string, CommitFile>? changes)
    {
        Label = label;
        Path = path;
        IsFolder = isFolder;
        Entry = entry;
        Change = change;
        _changes = changes;
    }

    public string Label { get; }
    public string Path { get; }
    public bool IsFolder { get; }
    public TreeEntry? Entry { get; }

    /// <summary>Modification apportée à ce fichier par le commit (pastille A, M, R), sinon null.</summary>
    public CommitFile? Change { get; }
    public bool HasChange => Change is not null;
    public string StatusLetter => Change?.StatusLetter ?? "";

    /// <summary>Nombre de fichiers du dossier, et combien ont été modifiés par ce commit.</summary>
    public int FileCount { get; private init; }
    public int ChangedCount { get; private init; }
    public string Detail => IsFolder
        ? $"{FileCount} fichier{(FileCount > 1 ? "s" : "")}" + (ChangedCount > 0 ? $" · {ChangedCount} modifié{(ChangedCount > 1 ? "s" : "")}" : "")
        : Entry?.IsSubmodule == true ? "sous-module" : "";

    public ObservableCollection<FileTreeNode> Children { get; } = [];
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _pending is not { } entries) return;
        _pending = null;
        Children.Clear();
        foreach (var child in Build(entries, Path.Length == 0 ? 0 : Path.Length + 1, _changes!)) Children.Add(child);
    }

    public override string ToString() => Label;

    /// <summary>Premier niveau de l'arborescence : dossiers d'abord, puis fichiers, par ordre alphabétique.</summary>
    public static IReadOnlyList<FileTreeNode> Roots(IReadOnlyList<TreeEntry> entries, IReadOnlyDictionary<string, CommitFile> changes) =>
        Build(entries, 0, changes);

    /// <summary>Recherche : liste à plat des fichiers dont le chemin contient le texte.</summary>
    public static IReadOnlyList<FileTreeNode> Search(IReadOnlyList<TreeEntry> entries, IReadOnlyDictionary<string, CommitFile> changes, string filter, int max) =>
        entries.Where(e => e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(max)
            .Select(e => new FileTreeNode(e.Path, e.Path, isFolder: false, e, changes.GetValueOrDefault(e.Path), null))
            .ToList();

    private static List<FileTreeNode> Build(IReadOnlyList<TreeEntry> entries, int offset, IReadOnlyDictionary<string, CommitFile> changes)
    {
        var folders = new List<FileTreeNode>();
        var files = new List<FileTreeNode>();
        foreach (var group in entries.GroupBy(e => e.Path.IndexOf('/', offset) is var slash and >= 0 ? e.Path[offset..slash] : null))
        {
            if (group.Key is null)
            {
                files.AddRange(group.Select(e => new FileTreeNode(e.Path[offset..], e.Path, isFolder: false, e, changes.GetValueOrDefault(e.Path), null)));
                continue;
            }
            var content = group.ToList();
            var folder = new FileTreeNode(group.Key, content[0].Path[..(offset + group.Key.Length)], isFolder: true, null, null, changes)
            {
                FileCount = content.Count,
                ChangedCount = content.Count(e => changes.ContainsKey(e.Path)),
            };
            folder._pending = content;
            folder.Children.Add(Placeholder); // flèche d'ouverture, remplacée à la première ouverture
            folders.Add(folder);
        }
        return [.. folders.OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase), .. files.OrderBy(f => f.Label, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>F6e : tous les fichiers du projet au commit choisi, leur contenu, et l'enregistrement d'une ancienne version.</summary>
public sealed partial class HistoryViewModel
{
    public const int MaxSearchResults = 500;

    private IReadOnlyList<TreeEntry> _allFiles = [];
    private Dictionary<string, CommitFile> _changes = [];
    private PanelSections? _sections;
    private PanelSections Sections
    {
        get
        {
            if (_sections is not null) return _sections;
            _sections = new PanelSections(host);
            // Paramètres › « Tout remettre dans l'état par défaut » : la bascule suit.
            _sections.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "" or null or nameof(PanelSections.HistoryAllFiles))
                    foreach (var p in new[] { nameof(ShowAllFiles), nameof(ShowChangedFiles), nameof(FilesHeader), nameof(NoChangedFilesShown) })
                        OnPropertyChanged(p);
            };
            return _sections;
        }
    }

    public ObservableCollection<FileTreeNode> Tree { get; } = [];
    public ObservableCollection<FileLine> ContentLines { get; } = [];

    /// <summary>Bascule « Modifiés / Tous les fichiers » (mémorisée comme les parties repliables).</summary>
    public bool ShowAllFiles
    {
        get => Sections.HistoryAllFiles;
        set
        {
            if (value == ShowAllFiles) return;
            Sections.HistoryAllFiles = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowChangedFiles));
            OnPropertyChanged(nameof(FilesHeader));
            OnPropertyChanged(nameof(NoChangedFilesShown));
            if (value && Details is { } d && _allFiles.Count == 0) _ = LoadTreeAsync(d.Commit.Sha, _detailsVersion);
        }
    }

    public bool ShowChangedFiles { get => !ShowAllFiles; set => ShowAllFiles = !value; }

    [ObservableProperty] public partial string TreeFilter { get; set; } = "";
    [ObservableProperty] public partial FileTreeNode? SelectedNode { get; set; }
    [ObservableProperty] public partial bool IsLoadingTree { get; set; }
    [ObservableProperty] public partial string? TreeNote { get; set; }

    public string ChangedTabLabel => $"Modifiés ({Files.Count})";
    public string AllTabLabel => _allFiles.Count == 0 ? "Tous les fichiers" : $"Tous les fichiers ({_allFiles.Count})";
    public bool HasTreeNote => TreeNote is not null;
    partial void OnTreeNoteChanged(string? value) => OnPropertyChanged(nameof(HasTreeNote));
    public bool HasTreeFilter => TreeFilter.Length > 0;
    partial void OnTreeFilterChanged(string value) { OnPropertyChanged(nameof(HasTreeFilter)); FillTree(); }

    // ------------------------------------------------------------ visionneuse : modifications ou contenu

    [ObservableProperty] public partial FileViewMode ViewMode { get; set; }

    /// <summary>Fichier affiché en bas de la colonne (modifications ou contenu).</summary>
    [ObservableProperty] public partial string? ViewedPath { get; set; }
    [ObservableProperty] public partial string? ContentNote { get; set; }
    [ObservableProperty] public partial string? ImagePath { get; set; }
    [ObservableProperty] public partial string? FileStatus { get; set; }

    /// <summary>Le dernier message sur le fichier est un échec (affiché en orange, pas en vert).</summary>
    [ObservableProperty] public partial bool FileStatusFailed { get; set; }

    public bool IsShowingChanges => ViewMode == FileViewMode.Changes;
    public bool ContentNoteShown => HasContentNote && IsShowingContent;
    public bool NoChangedFilesShown => HasNoFiles && ShowChangedFiles;
    public bool IsShowingContent => ViewMode == FileViewMode.Content;
    public bool HasImage => ImagePath is not null;
    public bool HasContentNote => ContentNote is not null;

    /// <summary>Le fichier existe à ce commit (il n'a pas été supprimé par lui) : on peut le lire, l'enregistrer, le restaurer.</summary>
    public bool ViewedFileExists => ViewedPath is not null && !(_changes.GetValueOrDefault(ViewedPath) is { StatusLetter: "D" });
    public bool CanShowWholeFile => IsShowingChanges && ViewedFileExists;
    public bool CanShowChanges => IsShowingContent && ViewedPath is not null && _changes.ContainsKey(ViewedPath);

    /// <summary>Restaurer n'a de sens que pour un projet sur ce poste (pas pour une copie légère d'un repo GitHub).</summary>
    public bool CanRestoreFile => ViewedFileExists && _remote is null && Project is not null;

    partial void OnViewModeChanged(FileViewMode value) => RaiseViewer();
    partial void OnViewedPathChanged(string? value) => RaiseViewer();
    partial void OnImagePathChanged(string? value) => OnPropertyChanged(nameof(HasImage));
    partial void OnContentNoteChanged(string? value) { OnPropertyChanged(nameof(HasContentNote)); OnPropertyChanged(nameof(ContentNoteShown)); }

    private void RaiseViewer()
    {
        foreach (var p in new[] { nameof(IsShowingChanges), nameof(IsShowingContent), nameof(ContentNoteShown), nameof(DiffHeader), nameof(ViewedFileExists),
                     nameof(CanShowWholeFile), nameof(CanShowChanges), nameof(CanRestoreFile) }) OnPropertyChanged(p);
    }

    partial void OnSelectedNodeChanged(FileTreeNode? value)
    {
        if (value is not { IsFolder: false, Path.Length: > 0 } node || node.Entry?.IsSubmodule == true) return;
        var isImage = GitService.ImageExtensions.Contains(Path.GetExtension(node.Path)); // le diff d'une image ne montre rien d'utile
        if (node.Change is { StatusLetter: not "D" } change && !isImage)
        {
            // Fichier modifié par ce commit : ses modifications d'abord, comme dans la liste « Modifiés ».
            if (SelectedFile == change) { ViewedPath = change.Path; ViewMode = FileViewMode.Changes; }
            else SelectedFile = change;
        }
        else _ = ShowContentAsync(node.Path);
    }

    [RelayCommand]
    private Task ShowWholeFile() => ViewedPath is { } path ? ShowContentAsync(path) : Task.CompletedTask;

    [RelayCommand]
    private void ShowChanges()
    {
        if (ViewedPath is null || !_changes.TryGetValue(ViewedPath, out var change)) return;
        if (SelectedFile == change) ViewMode = FileViewMode.Changes;
        else SelectedFile = change;
    }

    private int _contentVersion;

    private async Task ShowContentAsync(string path)
    {
        var version = ++_contentVersion;
        _diffVersion++; // une lecture de modifications en cours ne doit plus rien afficher…
        IsLoadingDiff = false; // …ni laisser tourner son indicateur
        ViewedPath = path;
        ViewMode = FileViewMode.Content;
        ContentLines.Clear();
        ImagePath = null;
        ContentNote = "Lecture du fichier…";
        FileStatus = null;
        if (Details is not { } details || Project is not { } project) return;
        try
        {
            var content = await Git.ReadFileAsync(project.Path, details.Commit.Sha, path, _remote?.Auth);
            if (version != _contentVersion) return;
            ContentNote = content.Kind switch
            {
                FileContentKind.Image => $"Image · {SizeLabel(content.Size)}",
                FileContentKind.Binary => $"Fichier binaire (programme, archive, document…) · {SizeLabel(content.Size)} : pas d'aperçu. « Enregistrer cette version sous… » permet de l'ouvrir avec le bon logiciel.",
                FileContentKind.TooLarge => $"Fichier trop gros pour l'aperçu ({SizeLabel(content.Size)}). « Enregistrer cette version sous… » permet de l'ouvrir ailleurs.",
                _ when content.Truncated => $"Fichier long : seules les {GitService.MaxFileLines} premières lignes sont affichées.",
                _ when content.Lines.Count == 0 => "Fichier vide.",
                _ => null,
            };
            ImagePath = content.ImagePath;
            var number = 0;
            foreach (var line in content.Lines) ContentLines.Add(new FileLine(++number, line));
        }
        catch (Exception e)
        {
            if (version == _contentVersion) ContentNote = $"Fichier illisible à ce commit : {e.Message}";
        }
    }

    internal static string SizeLabel(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} octet{(bytes > 1 ? "s" : "")}",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} Ko",
        _ => $"{bytes / (1024.0 * 1024):0.#} Mo",
    };

    // ------------------------------------------------------------ arborescence

    private async Task LoadTreeAsync(string sha, int detailsVersion)
    {
        if (Project is not { } project) return;
        IsLoadingTree = true;
        TreeNote = null;
        try
        {
            var files = await Git.ListFilesAsync(project.Path, sha);
            if (detailsVersion != _detailsVersion) return;
            _allFiles = files;
            FillTree();
        }
        catch (Exception e)
        {
            if (detailsVersion != _detailsVersion) return;
            Logger.LogWarning(e, "Fichiers du commit {Sha} illisibles", sha);
            TreeNote = $"Liste des fichiers illisible : {e.Message}";
        }
        finally
        {
            if (detailsVersion == _detailsVersion) IsLoadingTree = false;
            OnPropertyChanged(nameof(AllTabLabel));
            OnPropertyChanged(nameof(FilesHeader));
        }
    }

    private void FillTree()
    {
        var filter = TreeFilter.Trim();
        Tree.Clear();
        var nodes = filter.Length == 0
            ? FileTreeNode.Roots(_allFiles, _changes)
            : FileTreeNode.Search(_allFiles, _changes, filter, MaxSearchResults);
        foreach (var n in nodes) Tree.Add(n);
        TreeNote = filter.Length > 0 && nodes.Count == 0 ? "Aucun fichier ne correspond."
            : filter.Length > 0 && nodes.Count == MaxSearchResults ? $"Plus de {MaxSearchResults} fichiers correspondent : précise la recherche."
            : null;
    }

    /// <summary>Nouveau commit choisi : modifications de ce commit, puis arborescence si elle est affichée.</summary>
    private void ResetFiles(CommitDetails? details, int detailsVersion)
    {
        _changes = details?.Files.Where(f => f.StatusLetter != "D").GroupBy(f => f.Path).ToDictionary(g => g.Key, g => g.First())
                   ?? [];
        // Un fichier supprimé n'est plus dans l'arborescence, mais garde sa pastille dans « Modifiés ».
        foreach (var deleted in details?.Files.Where(f => f.StatusLetter == "D") ?? []) _changes.TryAdd(deleted.Path, deleted);
        _allFiles = [];
        Tree.Clear();
        TreeNote = null;
        OnPropertyChanged(nameof(ChangedTabLabel));
        OnPropertyChanged(nameof(AllTabLabel));
        if (details is not null && ShowAllFiles) _ = LoadTreeAsync(details.Commit.Sha, detailsVersion);
    }

    // ------------------------------------------------------------ actions sur le fichier affiché

    [RelayCommand]
    private void CopyPath() { if (ViewedPath is { } path) Shell.Copy(path); }

    /// <summary>Écrit la version de ce commit où l'utilisateur le choisit. Le projet n'est pas touché.</summary>
    [RelayCommand]
    private async Task SaveVersionAsync()
    {
        if (!ViewedFileExists || ViewedPath is not { } path || Details is not { } details || Project is not { } project) return;
        var name = Path.GetFileNameWithoutExtension(path) + $" ({details.Commit.ShortSha})" + Path.GetExtension(path);
        if (host.Dialogs.AskSavePath("Enregistrer cette version du fichier", name) is not { } destination) return;
        try
        {
            await Git.SaveFileAsync(project.Path, details.Commit.Sha, path, destination, _remote?.Auth);
            (FileStatus, FileStatusFailed) = ($"Version du commit {details.Commit.ShortSha} enregistrée : {destination}", false);
        }
        catch (Exception e) { (FileStatus, FileStatusFailed) = ($"Enregistrement impossible : {e.Message}", true); }
    }
}
