using System.Diagnostics;
using System.Windows;

namespace GitSpread.App;

/// <summary>Ouvertures déléguées à Windows (navigateur, explorateur) et presse-papiers.</summary>
public static class Shell
{
    public static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    public static void ShowInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } })?.Dispose();

    public static void Copy(string text)
    {
        // Le presse-papiers peut être verrouillé un court instant par une autre application.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(50); }
        }
    }
}
