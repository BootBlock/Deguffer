using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What <see cref="SquirrelStagingProvider"/> asks the live-tree veto about one staging directory.
///
/// <para><b>An update and an install use the folder from different places, so three signals are
/// asked.</b> A first install runs <c>Update.exe</c> from inside the staging directory the setup
/// program unpacked, so the executable signal sees it. A program working anywhere in the staging
/// folder is the second. An update is the case neither sees: Squirrel's apply step makes a staging
/// directory, fills it and deletes it from the application's own <c>Update.exe</c>, or from the
/// application itself where it calls the update manager in its own process, and both run from the
/// application's folder. Neither necessarily works in the staging folder. So a program running from
/// a folder that may write the staging folder is asked when it started, and one already running
/// when the directory was last written holds it.</para>
///
/// <para><b>The folders that may write it are wide on purpose.</b> Every folder in
/// <c>%LOCALAPPDATA%</c> that holds the updater, whether or not Deguffer could read its builds, and
/// the staging folder itself, where an install running from one staging directory unpacks into
/// another. Where <c>%LOCALAPPDATA%</c> would not be listed, every program running from anywhere in
/// it is asked instead, because the folders cannot be told apart.</para>
///
/// <para><b>That holds more than it needs to, in the safe direction.</b> An application that has run
/// since before a staging directory was written holds that directory until it exits, whether or not
/// it is updating, because nothing outside the process says which directory it made. A leftover from
/// an update that died while the application was running is offered once the application has been
/// restarted.</para>
///
/// <para><b>A staging folder on a share has no time to compare.</b> The share's own clock writes its
/// timestamps, and this machine's writes a program's start, so a server a few seconds behind would
/// read a directory an updater has just made as older than the updater. Its last write is reported
/// unknown, and the directory is held back wherever a writer is running.</para>
///
/// <para>Built again for every reading, as <see cref="LiveTreeQuestion"/> requires: the clean asks
/// again immediately before each removal, and both the programs running and the directory's last
/// write may have changed since the preview.</para>
/// </summary>
internal static class SquirrelStagingQuestion
{
    /// <summary>The question for one reading of the machine, about directories in <paramref name="stagingRoot"/>.</summary>
    public static LiveTreeQuestion For(
        SquirrelDiscovery discovery,
        IUserEnvironment environment,
        IVolumeInventory volumes,
        string stagingRoot,
        CancellationToken ct)
    {
        var sweep = discovery.Look(ct);

        IReadOnlyList<string> writers =
        [
            stagingRoot,
            .. sweep.ApplicationDataUnreadable ? [environment.LocalAppData] : sweep.UpdaterFolders,
        ];

        var comparable = HostVolume.Unopened(volumes, stagingRoot) is not UnopenedPlace.Share;

        return new LiveTreeQuestion(candidate => new LiveTreeQuery(candidate.Path, candidate.Project)
        {
            Writers = new LiveTreeWriters(writers, comparable ? LastWritten(candidate.Path) : null),
        });
    }

    /// <summary>
    /// The later of the directory's creation and its own last write, or null where Windows would not
    /// say. See <see cref="LiveTreeWriters.LastWritten"/> for why both.
    /// </summary>
    private static DateTimeOffset? LastWritten(string directory)
    {
        var info = new DirectoryInfo(LongPath.Extended(directory));

        try
        {
            // A missing directory answers with the start of the Windows epoch rather than failing,
            // and 1601 would read as written before every program started.
            if (!info.Exists)
            {
                return null;
            }

            return new DateTimeOffset(DateTime.FromFileTimeUtc(MinimumAge.NewestFileTimeOf(info)), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A refusal is not an answer. The veto reports the directory unanswered where a writer is
            // running, and holds it back.
            return null;
        }
    }
}
