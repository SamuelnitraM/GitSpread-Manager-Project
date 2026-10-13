using CommunityToolkit.Mvvm.ComponentModel;

namespace GitSpread.App.ViewModels;

/// <summary>Base des formulaires : la vue amène l'utilisateur sur la première erreur quand on le lui demande.</summary>
public abstract class FormViewModel : ObservableObject
{
    /// <summary>Validation refusée : la fenêtre fait défiler jusqu'au premier champ en erreur.</summary>
    public event EventHandler? ShowErrorsRequested;

    /// <summary>Vrai après une première tentative de validation : les champs vides passent alors en erreur.</summary>
    protected bool Submitted { get; set; }

    protected void RequestShowErrors() => ShowErrorsRequested?.Invoke(this, EventArgs.Empty);
}
