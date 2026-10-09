using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace Deguffer.App.Shell;

/// <summary>
/// The system dialog that asks where to save a CSV file, owned by a window. The Windows App SDK
/// picker, for the reason <see cref="FolderDialog"/> gives: the older one fails in a process running
/// as administrator.
/// </summary>
public static class CsvFileDialog
{
    /// <summary>The path of the file the user chose, or null if they cancelled.</summary>
    public static async Task<string?> ChooseAsync(Window owner, string suggestedName)
    {
        var picker = new FileSavePicker(owner.AppWindow.Id)
        {
            SuggestedFileName = suggestedName,
            DefaultFileExtension = ".csv",
        };
        picker.FileTypeChoices.Add("CSV file", [".csv"]);

        return (await picker.PickSaveFileAsync())?.Path;
    }
}
