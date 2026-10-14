using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitSpread.Core.Accounts;
using GitSpread.Core.Git;
using GitSpread.Core.GitHub;

namespace GitSpread.App.ViewModels;

public enum CloneStep { Form, Running, Done, Failed }

/// <summary>Cloner un repo GitHub sur le poste, avec le token du bon compte, puis l'ajouter à GitSpread.</summary>
public sealed partial class CloneViewModel : FormViewModel
{
    private readonly GitHubAccount _account;
    private readonly GitHubRepository _repository;
    private readonly IProjectHost _host;

    public CloneViewModel(GitHubAccount account, GitHubRepository repository, IProjectHost host)
    {
        _account = account;
        _repository = repository;
        _host = host;
        ParentFolder = (host.Settings.CloneFolder is { Length: > 0 } preferred && Directory.Exists(preferred) ? preferred : null)
                       ?? host.Settings.RootFolders.FirstOrDefault(Directory.Exists)
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        FolderName = repository.Name;
    }

    public event EventHandler? CloseRequested;

    public string FullName => _repository.FullName;
    public string Url => _repository.Url + ".git";
    public string SignLabel => $"Signer les commits de ce projet en tant que {_account.Login} ({_host.EmailKind()})";
    public string TargetPath => Path.Combine(ParentFolder.Trim(), FolderName.Trim());
    public string CommandPreview => $"git clone {Url} \"{TargetPath}\"   (avec le compte {_account.Login})";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TargetPath), nameof(CommandPreview))] public partial string ParentFolder { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TargetPath), nameof(CommandPreview))] public partial string FolderName { get; set; }
    [ObservableProperty] public partial bool SignWithAccount { get; set; } = true;
    [ObservableProperty] public partial string? ParentFolderError { get; set; }
    [ObservableProperty] public partial string? FolderNameError { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsForm))] public partial CloneStep Step { get; set; } = CloneStep.Form;
    [ObservableProperty] public partial string? ResultMessage { get; set; }

    public bool IsForm => Step == CloneStep.Form;
    public ObservableCollection<string> Progress { get; } = [];

    /// <summary>Dossier du projet cloné (null tant que le clonage n'a pas réussi).</summary>
    public string? ClonedPath { get; private set; }

    partial void OnParentFolderChanged(string value) { if (Submitted) Validate(); }
    partial void OnFolderNameChanged(string value) { if (Submitted || FolderNameError is not null) Validate(); }

    private bool Validate()
    {
        var parent = ParentFolder.Trim();
        ParentFolderError = parent.Length == 0 ? "Choisis le dossier où créer le projet."
            : !Path.IsPathFullyQualified(parent) ? "Indique un chemin complet (ex. C:\\xampp\\htdocs)."
            : null;

        var name = FolderName.Trim();
        FolderNameError = name.Length == 0 ? "Donne un nom au dossier du projet."
            : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "Ce nom contient un caractère interdit dans un nom de dossier (\\ / : * ? \" < > |)."
            : ParentFolderError is null && Directory.Exists(TargetPath) && Directory.EnumerateFileSystemEntries(TargetPath).Any()
                ? $"Le dossier {TargetPath} existe déjà et n'est pas vide : choisis un autre nom."
            : null;
        return ParentFolderError is null && FolderNameError is null;
    }

    [RelayCommand]
    private async Task CloneAsync()
    {
        Submitted = true;
        if (!Validate())
        {
            RequestShowErrors();
            return;
        }

        Step = CloneStep.Running;
        Progress.Clear();
        var target = TargetPath;
        try
        {
            Progress.Add($"Clonage de {FullName} avec le compte {_account.Login}…");
            var auth = await _host.Services.Credentials.EnvironmentForAsync(_account, RemoteUrlParser.Parse(Url));
            var clone = await _host.Services.Git.CloneAsync(ParentFolder.Trim(), Url, FolderName.Trim(), auth);
            if (!clone.Succeeded)
            {
                ResultMessage = clone.Explanation ?? clone.Output;
                Step = CloneStep.Failed;
                return;
            }

            var signed = "";
            if (SignWithAccount && await _host.GetAccountUserAsync(_account) is { } user)
            {
                Progress.Add("Identité des commits du projet…");
                var identity = _host.IdentityFor(_account, user);
                await _host.Services.Git.SetLocalIdentityAsync(target, identity.Name!, identity.Email!);
                signed = $" Ses commits seront signés {identity.Name} <{identity.Email}>.";
            }

            // Cloné depuis l'onglet d'un collaborateur : c'est ce compte qui enverra, même si le propriétaire est connecté (F3A).
            if (!_account.Login.Equals(_repository.Owner, StringComparison.OrdinalIgnoreCase))
            {
                await _host.Services.Git.SetPushAccountAsync(target, _account.Login);
                signed += $" Il est envoyé sur GitHub avec {_account.Login} (collaborateur).";
            }

            // Hors des dossiers analysés (ou trop profond) : on l'ajoute à la main pour qu'il apparaisse.
            if (!IsCoveredByScan(target))
            {
                _host.Settings.ExtraProjects.Add(target);
                _host.SaveSettings();
            }

            ClonedPath = target;
            ResultMessage = $"{FullName} est cloné dans {target}.{signed}";
            Step = CloneStep.Done;
        }
        catch (Exception e)
        {
            ResultMessage = e.Message;
            Step = CloneStep.Failed;
        }
    }

    private bool IsCoveredByScan(string path)
    {
        var full = Path.GetFullPath(path);
        return _host.Settings.RootFolders.Any(root =>
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), full);
            return !relative.StartsWith("..") && !Path.IsPathRooted(relative)
                   && relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length <= _host.Settings.ScanDepth;
        });
    }

    [RelayCommand] private void BackToForm() => Step = CloneStep.Form;
    [RelayCommand] private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
