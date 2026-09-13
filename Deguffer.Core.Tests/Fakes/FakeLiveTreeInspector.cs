using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Declares which directories are "in use", so a provider's veto is provable with no editor, no
/// build and no toolchain installed.
///
/// The real inspector is exercised against real live trees in <c>LiveTreeInspectorTests</c> —
/// nothing about the Restart Manager or the process table can be established with a fake. What this
/// proves is the other half: that a provider given a live directory refuses to target it, and that
/// it says so.
/// </summary>
public sealed class FakeLiveTreeInspector : ILiveTreeInspector
{
    private readonly HashSet<string> _live;
    private readonly bool _complete;
    private readonly List<RunningProgram> _programs = [];

    public FakeLiveTreeInspector(bool complete, params string[] live)
    {
        _live = new HashSet<string>(live, StringComparer.OrdinalIgnoreCase);
        _complete = complete;
    }

    public FakeLiveTreeInspector(params string[] live) : this(complete: true, live) { }

    public static FakeLiveTreeInspector NothingLive => new();

    /// <summary>Nothing found, and nothing established either — the "could not tell" case.</summary>
    public static FakeLiveTreeInspector CannotTell => new(complete: false);

    public int InvalidateCount { get; private set; }

    /// <summary>What the provider asked about, so a test can assert the project folder was passed.</summary>
    public IReadOnlyList<LiveTreeQuery> Asked { get; private set; } = [];

    /// <summary>
    /// Pretend a program is running from <paramref name="executable"/>, working in
    /// <paramref name="workingDirectory"/>, or both.
    ///
    /// <para>Declared as a program rather than as a live directory, and answered on both sides by
    /// the real inspector's containment rule: <see cref="FindOccupiedDirectories"/> reports where it
    /// is, and <see cref="FindLive"/> counts it against a candidate whose directory holds the
    /// executable or whose project holds the working directory. A provider that finds candidates
    /// from the first and confirms them with the second is then tested on the pair, which is the
    /// only thing that tells a program in a project's build directory from one merely below the
    /// project.</para>
    /// </summary>
    public FakeLiveTreeInspector WithProgram(string name, string? executable = null, string? workingDirectory = null)
    {
        _programs.Add(new RunningProgram(name, executable, workingDirectory));
        return this;
    }

    public LiveTreeFindings FindLive(IReadOnlyList<LiveTreeQuery> candidates, CancellationToken ct = default)
    {
        Asked = candidates;

        return new LiveTreeFindings(
            [.. candidates
                .Select(c => new LiveTree(c.Directory, HoldersOf(c)))
                .Where(tree => tree.Holders.Count > 0)],
            _complete);
    }

    public LiveTreeFindings FindOccupiedDirectories(CancellationToken ct = default) =>
        new(
            [.. _programs
                .SelectMany(program => new (string? Directory, string Holder)[]
                {
                    (program.Executable is { } executable ? Path.GetDirectoryName(executable) : null,
                        $"{program.Name} is running from inside it"),
                    (program.WorkingDirectory, $"{program.Name} is working in it"),
                })
                .Where(place => place.Directory is not null)
                .GroupBy(place => place.Directory!, StringComparer.OrdinalIgnoreCase)
                .Select(group => new LiveTree(group.Key, [.. group.Select(place => place.Holder)]))],
            _complete);

    private List<string> HoldersOf(LiveTreeQuery candidate)
    {
        var holders = new List<string>();

        if (_live.Contains(candidate.Directory))
        {
            holders.Add("a test says something is using it");
        }

        foreach (var program in _programs)
        {
            if (program.Executable is { } executable && LongPath.Contains(candidate.Directory, executable))
            {
                holders.Add($"{program.Name} is running from inside it");
            }

            if (program.WorkingDirectory is { } working && LongPath.Contains(candidate.Project, working))
            {
                holders.Add($"{program.Name} is working in {Path.GetFileName(candidate.Project)}");
            }
        }

        return holders;
    }

    /// <summary>
    /// The declared live directories that are an <em>immediate</em> child of one of
    /// <paramref name="directories"/>.
    ///
    /// <para>Immediate, and never the root itself, because that is the contract
    /// <see cref="ILiveTreeInspector.FindLiveChildren"/> keeps: it names the child a plan can spare,
    /// and it answers nothing for a program sitting in the folder itself. A fake that reported more
    /// than the real one ever can would let a provider pass a test against behaviour it will never
    /// see.</para>
    /// </summary>
    public LiveTreeFindings FindLiveChildren(
        IReadOnlyList<string> directories,
        CancellationToken ct = default) =>
        new(
            [.. _live
                .Where(child => directories.Any(root => IsImmediateChild(root, child)))
                .Select(child => new LiveTree(child, ["a test says something is using it"]))],
            _complete);

    private static bool IsImmediateChild(string root, string child) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(child)) is { } parent
        && parent.Equals(
            Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);

    public void Invalidate() => InvalidateCount++;

    private sealed record RunningProgram(string Name, string? Executable, string? WorkingDirectory);
}
