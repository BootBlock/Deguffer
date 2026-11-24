namespace Deguffer.App.ViewModels;

/// <summary>
/// The outcome of the latest change on a settings form, shown on the row that made it and on no other.
///
/// <para>A change that fails moves the notice to its own row, so the page never states one failure in
/// two places. A change that saves clears it, because the folder took a write, and a notice left on
/// another row would tell the reader to check a folder that is writable again.</para>
/// </summary>
public sealed class LatestSave
{
    private SettingSave? _failed;

    /// <summary>Show whether the change made through <paramref name="setting"/> was written.</summary>
    /// <returns><paramref name="saved"/>, so the caller can go on to read back what holds.</returns>
    public bool Report(SettingSave setting, bool saved)
    {
        var failed = saved ? null : setting;

        // Left alone when the same row fails again, so its notice stays open rather than closing and
        // opening, which would play its arrival and scroll to it a second time.
        if (failed != _failed)
        {
            _failed?.Failed = false;
            _failed = failed;
            _failed?.Failed = true;
        }

        return saved;
    }
}
