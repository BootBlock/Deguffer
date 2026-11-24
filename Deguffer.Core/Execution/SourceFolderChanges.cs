using Deguffer.Core.Providers;

namespace Deguffer.Core.Execution;

/// <summary>
/// Which findings a newly approved source folder leaves describing a scan that no longer holds, so
/// the re-plan after it measures those and leaves every other row as it stands.
///
/// <para><b>Every row that was waiting for a folder, not only the one the folder was added from.</b>
/// The providers that search approved folders all read the one list the user keeps in Settings, so a
/// folder approved for the build output row is approved for the .NET, Git LFS, Nx and Visual Studio
/// rows too. Re-planning the one row would leave the others asking for a folder the user has just
/// given, beside a figure that stays at nothing until the next scan.</para>
///
/// <para><b>Read from the finding, never from the provider.</b> Approving the folder is what changes
/// the provider's own answer, so by the time this is asked every provider says it is no longer
/// waiting. <see cref="Finding.AwaitingSourceFolders"/> records what the scan found, which is the
/// question here.</para>
/// </summary>
public static class SourceFolderChanges
{
    /// <param name="shown">Every finding on the page, in the order it is shown.</param>
    /// <returns>The providers to plan again, in the order <paramref name="shown"/> gives them.</returns>
    public static IReadOnlyList<ICleanupProvider> Stale(IReadOnlyList<Finding> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);

        return [.. shown.Where(finding => finding.AwaitingSourceFolders).Select(finding => finding.Provider)];
    }
}
