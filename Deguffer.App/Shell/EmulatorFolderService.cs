using Deguffer.Core.Configuration;

namespace Deguffer.App.Shell;

/// <summary>
/// The folders the user has said an emulator is installed in, held for the life of the app and
/// written through to <see cref="EmulatorFolderStore"/> on every change.
///
/// <para>The emulator row reads the same file at the start of each scan, so a folder added here is
/// looked in by the next one.</para>
/// </summary>
public sealed class EmulatorFolderService
{
    private readonly EmulatorFolderStore _store;
    private readonly List<string> _folders;

    public EmulatorFolderService(EmulatorFolderStore store)
    {
        _store = store;
        _folders = [.. store.Load()];
    }

    public IReadOnlyList<string> Current => _folders;

    public bool Add(string folder) =>
        _folders.Contains(folder, StringComparer.OrdinalIgnoreCase) || Write([.. _folders, folder]);

    public bool Remove(string folder) =>
        Write([.. _folders.Where(existing => !existing.Equals(folder, StringComparison.OrdinalIgnoreCase))]);

    private bool Write(List<string> updated)
    {
        // Adopt what the store kept rather than what was asked for, so Settings never lists a folder
        // that is not in the file and will not be looked in.
        if (!_store.Save(updated, out var stored))
        {
            return false;
        }

        _folders.Clear();
        _folders.AddRange(stored);

        return true;
    }
}
