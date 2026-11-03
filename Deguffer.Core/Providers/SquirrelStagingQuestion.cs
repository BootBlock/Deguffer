using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What <see cref="SquirrelStagingProvider"/> asks the live-tree veto about one staging directory.
///
/// <para><b>Three signals, because an update and an install use the folder from different
/// places.</b> A first install runs <c>Update.exe</c> from inside the staging directory the setup
/// program unpacked, so the executable signal sees it. A program working anywhere in the staging
/// folder is the second. An update is the case neither sees: Squirrel's apply step makes a staging
/// directory, fills it and deletes it from the application's own <c>Update.exe</c>, or from the
/// application itself where it calls the update manager in its own process, and both run from the
/// installation folder. Neither necessarily works in the staging folder. So a program running from
/// an installation Deguffer found is asked when it started, and one already running when the
/// directory was last written holds it.</para>
///
/// <para><b>That holds more than it needs to, in the safe direction.</b> An application that has run
/// since before a staging directory was written holds that directory until it exits, whether or not
/// it is updating, because nothing outside the process says which directory it made. A leftover from
/// an update that died while the application was running is offered once the application has been
/// restarted.</para>
///
/// <para>Built again for every reading, as <see cref="LiveTreeQuestion"/> requires: the clean asks
/// again immediately before each removal, and both the programs running and the directory's last
/// write may have changed since the preview.</para>
/// </summary>
internal static class SquirrelStagingQuestion
{
    /// <summary>The question for one reading, naming every installation <paramref name="discovery"/> found.</summary>
    public static LiveTreeQuestion For(SquirrelDiscovery discovery, CancellationToken ct)
    {
        IReadOnlyList<string> installations = [.. discovery.Look(ct).Installations.Select(i => i.Root)];

        return new LiveTreeQuestion(candidate => new LiveTreeQuery(candidate.Path, candidate.Project)
        {
            Writers = installations.Count == 0
                ? null
                : new LiveTreeWriters(installations, LastWritten(candidate.Path)),
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

            var created = info.CreationTimeUtc;
            var written = info.LastWriteTimeUtc;

            return new DateTimeOffset(created > written ? created : written, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A refusal is not an answer. The veto reports the directory unanswered where a writer is
            // running, and holds it back.
            return null;
        }
    }
}
