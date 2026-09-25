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
    private bool _complete;
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

    /// <summary>
    /// Answer "could not tell" from now on, so a test can have the process table become unreadable
    /// between the preview and the clean.
    /// </summary>
    public FakeLiveTreeInspector CannotTellFromNow()
    {
        _complete = false;
        return this;
    }

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
    ///
    /// <para><paramref name="arguments"/> are the full paths it was started with, which
    /// <see cref="FindLiveChildren"/> alone answers, as the real inspector does: the child of a
    /// scratch folder an argument names, or names a path inside.</para>
    /// </summary>
    public FakeLiveTreeInspector WithProgram(
        string name,
        string? executable = null,
        string? workingDirectory = null,
        IReadOnlyList<string>? arguments = null)
    {
        _programs.Add(new RunningProgram(name, executable, workingDirectory, arguments ?? []));
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
    /// The immediate children of <paramref name="directories"/> that are declared live, or that hold
    /// a place a program added with <see cref="WithProgram"/> runs from, works in or was started
    /// with, which is how the real inspector builds this answer.
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
            [
                .. _live
                    .Where(child => directories.Any(root => IsImmediateChild(root, child)))
                    .Select(child => new LiveTree(child, ["a test says something is using it"])),
                .. FindOccupiedDirectories(ct).Live
                    .SelectMany(place => directories
                        .Select(root => ChildHolding(root, place.Directory))
                        .OfType<string>()
                        .Select(child => new LiveTree(child, place.Holders))),
                .. _programs
                    .SelectMany(program => program.Arguments
                        .SelectMany(argument => directories.Select(root => ChildHolding(root, argument)))
                        .OfType<string>()
                        .Select(child => new LiveTree(child, [$"{program.Name} was started with it"]))),
            ],
            _complete);

    /// <summary>The immediate child of <paramref name="root"/> holding <paramref name="place"/>, or null where it is not below.</summary>
    private static string? ChildHolding(string root, string place)
    {
        var parent = Path.TrimEndingDirectorySeparator(root);

        if (place.Length <= parent.Length + 1 || !LongPath.Contains(parent, place))
        {
            return null;
        }

        var below = place[(parent.Length + 1)..];
        var separator = below.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        return Path.Combine(parent, separator < 0 ? below : below[..separator]);
    }

    private static bool IsImmediateChild(string root, string child) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(child)) is { } parent
        && parent.Equals(
            Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);

    public void Invalidate() => InvalidateCount++;

    private sealed record RunningProgram(
        string Name,
        string? Executable,
        string? WorkingDirectory,
        IReadOnlyList<string> Arguments);
}
