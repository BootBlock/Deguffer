using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace Deguffer.App.Shell;

/// <summary>
/// The system folder picker, owned by a window.
///
/// <para>The Windows App SDK picker rather than <c>Windows.Storage.Pickers</c>. The older one does
/// not work in a process running as administrator: it fails with <c>E_FAIL</c>, and Deguffer
/// offers to relaunch itself elevated on two pages, so every folder chosen after that would have
/// ended the process from an <c>async void</c> handler. This one takes its owner as a
/// <see cref="Microsoft.UI.WindowId"/> and supports an elevated caller.</para>
/// </summary>
public static class FolderDialog
{
    /// <summary>
    /// The path of the folder the user chose, or null if they cancelled. The path is what the picker
    /// returned, and may be empty or name nothing on a disk: see
    /// <see cref="Deguffer.Core.Configuration.PickedFolder"/>.
    /// </summary>
    public static async Task<string?> ChooseAsync(Window owner)
    {
        var picker = new FolderPicker(owner.AppWindow.Id);

        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
