using System.Windows;
using GitSpread.App.ViewModels;

namespace GitSpread.App;

/// <summary>Ouvre les fenêtres modales par-dessus la fenêtre active.</summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    public bool ShowCommit(CommitViewModel viewModel) =>
        new CommitWindow(viewModel) { Owner = Owner }.ShowDialog() == true;

    public void ShowPublish(PublishViewModel viewModel) =>
        new PublishWindow(viewModel) { Owner = Owner }.ShowDialog();

    public bool ShowIntegration(IntegrationViewModel viewModel) =>
        new IntegrationWindow(viewModel) { Owner = Owner }.ShowDialog() == true;

    public void ShowRepoint(RepointViewModel viewModel) =>
        new RepointWindow(viewModel) { Owner = Owner }.ShowDialog();

    public bool Confirm(string message, string title) => SmallDialogs.Confirm(Owner, title, message);

    public int Choose(string title, string message, IReadOnlyList<string> options) =>
        SmallDialogs.Choose(Owner, title, message, options);

    public string? Prompt(string title, string message, string initialValue, Func<string, string?>? validate = null, bool spacesToDash = false) =>
        SmallDialogs.Prompt(Owner, title, message, initialValue, validate, spacesToDash);
}
