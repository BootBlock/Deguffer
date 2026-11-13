namespace Deguffer.Core.Configuration;

/// <summary>
/// What the page says when a change to the keep list could not be saved.
///
/// <para>Said out loud rather than passed over, because a kept item silently offered again after a
/// restart is the failure the keep list exists to prevent. Each case names what still holds: a keep
/// that lasts this session, one that lasts until a restart, or a release that did not happen.</para>
/// </summary>
public static class KeepSaveNotice
{
    /// <param name="keeping">Whether the change was a keep, rather than a release.</param>
    /// <param name="itemName">The item's name, as its row shows it.</param>
    /// <param name="storedListUnreadable">
    /// Whether the list saved on disk could not be read at launch, so nothing is written over it.
    /// </param>
    public static string For(bool keeping, string itemName, bool storedListUnreadable) =>
        storedListUnreadable
            ? $"{(keeping ? "Kept" : "Stopped keeping")} {itemName} for this session only. Deguffer "
              + "could not read the keep list saved in %LOCALAPPDATA%\\Deguffer\\keep.json when it "
              + "started, so it is not saving over it. Repair or remove that file, then restart Deguffer."
            : keeping
                ? $"Kept {itemName} for now, but Deguffer could not save the keep list, so it will be "
                  + "offered again after a restart. Check that %LOCALAPPDATA%\\Deguffer is writable."
                : $"{itemName} is still on your keep list: Deguffer could not save the change.";
}
