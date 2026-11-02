using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>
/// <see cref="TemporaryUserWork"/> asked again immediately before a temporary folder is emptied, as
/// well as whatever <paramref name="inner"/> asks.
///
/// <para><b>Why the preview's list is not enough.</b> An editor can start a new unsaved sketch while
/// the preview is on screen, and with no age limit set the clean would take it with the rest of the
/// folder. Each one found now and missing from the step is kept as the step's own are, and reported
/// as somebody's work.</para>
///
/// <para>A folder whose entries could not be listed is held back whole, because no entry can be
/// spared on a listing that names none.</para>
/// </summary>
internal sealed class TemporaryUserWorkCheck(IUseCheck inner) : IUseCheck
{
    public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
    {
        var asked = inner.Ask(step, ct);

        if (step is not ClearDirectoryStep clear)
        {
            return asked;
        }

        var scan = TemporaryUserWork.In(clear.Path);

        if (scan.Unreadable)
        {
            return [.. asked, new InUseNow(clear.Path, "Deguffer could not look for unsaved work in this folder")];
        }

        return
        [
            .. asked,
            .. scan.Entries
                .Where(entry => !clear.UserWork.Contains(entry.Path, StringComparer.OrdinalIgnoreCase))
                .Select(entry => new InUseNow(entry.Path, $"it is {entry.What}", IsUserWork: true)),
        ];
    }
}
