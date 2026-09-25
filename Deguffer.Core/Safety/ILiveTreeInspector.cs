namespace Deguffer.Core.Safety;

/// <summary>One directory a plan is considering, and where to look for signs that it is in use.</summary>
/// <param name="Directory">The directory a plan would remove.</param>
/// <param name="Project">
/// The project or solution folder <paramref name="Directory"/> belongs to.
///
/// Asked for separately because the strongest signal sits beside the build directory rather than
/// inside it: a build, a shell and an open editor all work in the <em>project</em>, and a live
/// Visual Studio's working directory is the solution folder rather than the <c>.vs</c> below it.
/// </param>
/// <param name="LockFileNames">
/// Paths, relative to <paramref name="Directory"/>, of files the owning tool holds open for as long
/// as it is using it — Unity's <c>UnityLockfile</c>, Visual Studio's <c>.suo</c>. A full path is
/// taken as it stands, for a file that sits elsewhere in the project: the Unreal editor's log is in
/// <c>Saved\Logs</c>, beside the build directory rather than inside it.
///
/// Declared by the provider and never guessed, for the same reason
/// <see cref="DisposableChildSet"/>'s children are: a name list a reader can audit is worth more
/// than a rule inferred from what happens to be on disk. A provider that knows of no such file
/// passes none, and the directory is then judged by the process table alone.
/// </param>
public sealed record LiveTreeQuery(string Directory, string Project, IReadOnlyList<string> LockFileNames)
{
    public LiveTreeQuery(string directory, string project) : this(directory, project, []) { }
}

/// <summary>A directory something is using right now.</summary>
/// <param name="Directory">The directory asked about, as it was asked.</param>
/// <param name="Holders">
/// What makes it live, in a form that goes straight into a plan note. Process names rather than
/// identifiers: the user has to recognise the thing they need to close.
/// </param>
public sealed record LiveTree(string Directory, IReadOnlyList<string> Holders);

/// <param name="Live">Every candidate found to be in use.</param>
/// <param name="Complete">
/// False when one of the mechanisms could not run at all — the Restart Manager refused the query, or
/// the process table could not be read. Absence from <see cref="Live"/> is then not evidence of
/// dormancy.
///
/// The distinction is the whole point. "Nothing is using this" and "we could not tell" lead to
/// opposite decisions on a directory whose deletion breaks the work someone is doing right now, and
/// a seam that folded them together would report the second as the first — which is the direction
/// §5.2 calls dangerous, arriving through a different door.
///
/// <b>It reports a mechanism failing, not the standing limits of an unelevated run.</b> A process
/// belonging to another account, or one running elevated, cannot be opened at all from an ordinary
/// Deguffer, and one that opens without read access to its memory yields no working directory.
/// Neither is a per-run failure — both are true of every unelevated run on every machine — so
/// flagging them here would put a warning on every plan and teach the user to ignore the one that
/// matters. They are stated as a limit of the answer instead, on the interface below.
/// </param>
public sealed record LiveTreeFindings(IReadOnlyList<LiveTree> Live, bool Complete)
{
    public static readonly LiveTreeFindings Nothing = new([], Complete: true);

    /// <summary>Whether <paramref name="directory"/> was found to be in use.</summary>
    public bool IsLive(string directory) =>
        Live.Any(l => l.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// §5.3 generalised from <c>%TEMP%</c> to a directory anywhere: <em>is anything using this right
/// now?</em>
///
/// <para><see cref="IProcessInspector"/> answers different questions — whether a process of a given
/// name exists anywhere on the machine, or one with a given id — and neither fits a source tree. A
/// tree records no process id, and the name is the wrong question twice over. It is too broad, because one Unity editor open on some other project would veto every Unity
/// project on the disk. And it is too narrow, because the process actually holding a directory open
/// is often not the one whose name a reader would think of: a live Visual Studio solution's
/// <c>.vs</c> index is held open by <c>DevHub.exe</c>, a service host, while <c>devenv</c> holds
/// nothing in it at all. That was observed rather than assumed — see
/// <c>docs/todo/unreached-locations.md</c> §2.</para>
///
/// <para>This is a veto on a target, not a warning beside one. A cache a tool is still writing to
/// costs a slower next use; a build directory removed under a live editor or a running build breaks
/// the thing the user is working on at that moment, and nothing re-downloads the afternoon.</para>
///
/// <para><b>It can miss, and it must never fire wrongly.</b> Every signal here is positive evidence
/// that something is using the directory, so a directory reported live is one. The reverse does not
/// follow, and it misses in two known ways. A compiler holding a file deep inside a tree that is
/// neither its own executable nor its working directory is invisible to all three signals, and
/// nothing unelevated answers that at directory granularity. And a program running as another
/// account or as administrator cannot be inspected from an ordinary Deguffer at all, so an elevated
/// build is not seen. Neither gap closes without elevation, and §6.3 makes unelevated the ordinary
/// run — so §7's age column carries the rest of the decision, and the user-facing guide says plainly
/// that the check is not exhaustive.</para>
/// </summary>
public interface ILiveTreeInspector
{
    /// <summary>
    /// Which of <paramref name="candidates"/> are in use. Batched because the process table is read
    /// once for the whole set — asking per directory would walk it once per project.
    /// </summary>
    LiveTreeFindings FindLive(IReadOnlyList<LiveTreeQuery> candidates, CancellationToken ct = default);

    /// <summary>
    /// Every directory a running program is in: the folder its executable was started from, and
    /// its working directory. <see cref="LiveTree.Holders"/> names each program in one.
    ///
    /// <para><b>The same evidence as <see cref="FindLive"/>, asked from the other end, and the
    /// reason is that the other end is a disk walk.</b> Explore refuses a build directory a plan
    /// holds back as in use (§7.1), and that method can only judge a directory somebody has named.
    /// Naming a source tree's build directories is discovery — a walk of every approved source root,
    /// each time Explore rebuilds its policy, with every path refused until it finished. The process
    /// table already holds the few places a live build directory can be reached from, and this is
    /// one pass over it.</para>
    ///
    /// <para><b>A place, not a verdict.</b> A program in a project's own <c>tools</c> folder is
    /// using neither the project nor its build output, so a caller that wants the plan's answer
    /// asks <see cref="FindLive"/> about what it finds from here. Declared lock files are no part of
    /// this answer, because a file held open is not a place a program is.</para>
    ///
    /// <para><see cref="LiveTreeFindings.Complete"/> is false where working directories could not be
    /// read, and the list then holds executables' folders alone. The same limits apply as to
    /// <see cref="FindLive"/>: every entry is positive evidence, and an elevated program or one
    /// belonging to another account cannot be inspected at all.</para>
    /// </summary>
    LiveTreeFindings FindOccupiedDirectories(CancellationToken ct = default);

    /// <summary>
    /// Which immediate children of <paramref name="directories"/> something is running from or
    /// working in, asked without naming the children.
    ///
    /// <para><b>The same evidence as <see cref="FindLive"/>, asked from the other end, and the
    /// reason is arithmetic.</b> That method tests each candidate against every process, which is
    /// what a set of projects wants: there are tens of them and the caller knows their names. A
    /// scratch folder is the opposite shape — the user's <c>%TEMP%</c> held 18,056 immediate
    /// entries on the machine this was written against — so naming them would mean enumerating the
    /// folder to build the list and then walking the process table once per entry, to learn a fact
    /// the process table already holds outright. Asked this way it is one pass over the processes,
    /// whatever the folder holds.</para>
    ///
    /// <para><see cref="LiveTree.Directory"/> is the child rather than the process's own path,
    /// because the child is the unit a caller can act on: <c>%TEMP%\pip-build-abc123</c> is what
    /// gets spared, not the executable four levels inside it. A process sitting in one of
    /// <paramref name="directories"/> itself has no such child and is not reported — there is
    /// nothing below it to spare, and the folder itself is never a candidate for removal.</para>
    ///
    /// <para>The same limits apply as to <see cref="FindLive"/>: every signal is positive evidence,
    /// and an elevated program or one belonging to another account cannot be inspected at all.</para>
    /// </summary>
    LiveTreeFindings FindLiveChildren(IReadOnlyList<string> directories, CancellationToken ct = default);

    /// <summary>
    /// Discard any cached snapshot, so every provider in one planning pass sees the same machine.
    /// The same contract as <see cref="IProcessInspector.Invalidate"/>.
    /// </summary>
    void Invalidate();
}
