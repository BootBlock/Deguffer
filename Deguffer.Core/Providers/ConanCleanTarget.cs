using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether <c>conan cache clean -s -b -t</c>, run now, would still clean the store the plan examined
/// and remove only what the plan saw there.
///
/// <para><b>Why the plan's own look is not enough.</b> Conan works out its home and reads
/// <c>global.conf</c> and <c>global_user.conf</c> afresh each time it starts, so a <c>.conanrc</c>,
/// <c>CONAN_HOME</c> or <c>conan config install</c> changed after the preview sends the same
/// arguments to another store, where <c>-t</c> removes <c>t</c> and every unfinished build nobody
/// vetted. In the store the plan did examine, <c>-t</c> goes by shape: a folder dropped into
/// <c>t</c> or <c>b</c> after the preview would go, and so would a build a <c>conan create</c>
/// started, which is unfinished until it ends. A recipe or package folder swapped for a link after
/// the preview would have <c>-s</c> and <c>-b</c> empty the <c>s</c> and <c>b</c> in a tree nobody
/// read, because Conan's <c>rmdir</c> follows it. §5.6 could notice none of these, because what the
/// plan never listed is not something it protects. Conan's cache is not safe for concurrent use
/// besides.</para>
///
/// <para>So the step runs only while no Conan is running, the home works out as it did, the
/// configuration files are byte for byte what the plan read once Conan had answered it, and the
/// store, read again by the plan's own reader, holds to what the plan held it to
/// (<see cref="ConanStoreReading.HeldTo"/>). Every reading is the plan's own, so this asks no
/// subprocess.</para>
/// </summary>
/// <param name="inspector">Asked afresh at the clean, to within <see cref="IUseCheck.ReadingLimit"/>.</param>
/// <param name="names">The process names Conan runs as.</param>
/// <param name="resolveHome">Works out Conan's home afresh, as the plan first did.</param>
/// <param name="planned">The home, the store and the configuration files the plan examined.</param>
/// <param name="heldTo">What the plan found in the store's <c>t</c> and <c>b</c>, and its linked folders.</param>
internal sealed class ConanCleanTarget(
    IProcessInspector inspector,
    IReadOnlyList<string> names,
    Func<ConanHomeAnswer> resolveHome,
    ConanLocation.Found planned,
    IReadOnlySet<ConanStoreEntry> heldTo) : ICommandTargetCheck
{
    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

        if (inspector.FindRunning(names) is { Count: > 0 } running)
        {
            return $"{string.Join(", ", running)} is running now, and Conan's clean removes unfinished builds, "
                + "which a build in progress is";
        }

        if (resolveHome() is not ConanHomeAnswer.Found { Home: var home } || !ConanLocation.SameFolder(home, planned.Home))
        {
            return $"Conan's home no longer works out as {planned.Home}, the one the preview examined, so its "
                + "clean could go to a package store nobody examined";
        }

        if (ConanGlobalConf.Read(planned.Home) is not ConanConfReading.Plain { Files: var files } || !files.SameAs(planned.Configuration))
        {
            return $"Conan's configuration in {planned.Home} has changed since the preview, or could not be read "
                + "again, and Conan reads it when it starts, so its clean could go to a package store nobody examined";
        }

        return ConanStoreContents.Read(planned.Store, ct).HeldTo.SetEquals(heldTo)
            ? null
            : $"what Conan's clean would remove from the temporary and build folders in {planned.Store}, or "
                + "reach through a link to, has changed since the preview, and it removes there by shape rather "
                + "than by name. Scan again to see what it would remove now";
    }
}
