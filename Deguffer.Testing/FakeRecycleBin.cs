using Deguffer.Core.Execution;

namespace Deguffer.Testing;

/// <summary>
/// Stands in for the shell's Recycle Bin, recording the path handed across.
///
/// <para>Recording is the point rather than a convenience. §6.3 is a requirement about the
/// <em>form</em> of the path that crosses into Win32, and this boundary requires the opposite form
/// from every other one in Core — the shell namespace refuses the extended-length prefix — so the
/// only way to hold the code to it is to look at what actually crossed.</para>
/// </summary>
public sealed class FakeRecycleBin : IRecycleBin
{
    private readonly Func<string, RecycleOutcome> _behaviour;

    /// <summary>Recycles by removing the item outright, which is what the shell's effect looks like from here.</summary>
    public FakeRecycleBin()
        : this(path =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }

            return new RecycleOutcome(Removed: true);
        })
    {
    }

    public FakeRecycleBin(Func<string, RecycleOutcome> behaviour) => _behaviour = behaviour;

    /// <summary>Every path the bin reached, in the order it reached them, across every batch.</summary>
    public List<string> Paths { get; } = [];

    /// <summary>
    /// A bin that takes the containing folder as well — the over-broad removal §5.6's negative
    /// exists to catch. It passes every assertion that its own target went away.
    /// </summary>
    public static FakeRecycleBin TakingTheParentToo() => new(path =>
    {
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        return new RecycleOutcome(Removed: true);
    });

    /// <summary>
    /// A bin that takes one named neighbour along with the item, leaving the folder standing.
    ///
    /// <para>The narrower over-broad removal, and the one worth testing separately: when the whole
    /// folder goes, the survivor check fails because the folder cannot be listed at all, which is a
    /// different branch from the comparison that finds a missing neighbour.</para>
    /// </summary>
    public static FakeRecycleBin TakingAlso(string neighbour) => new(path =>
    {
        File.Delete(path);
        File.Delete(Path.Combine(Path.GetDirectoryName(path)!, neighbour));

        return new RecycleOutcome(Removed: true);
    });

    /// <summary>
    /// A bin that moves the item into <paramref name="bin"/> under a new name and says where, as the
    /// shell's progress sink does: a move on one volume, which keeps the file's ID.
    /// </summary>
    /// <param name="alongside">Run on the item's path before it is moved, to take or change something else as the bin does.</param>
    public static FakeRecycleBin MovingTo(string bin, Action<string>? alongside = null) => new(path =>
    {
        alongside?.Invoke(path);
        Directory.CreateDirectory(bin);
        var binned = Path.Combine(bin, $"$R{Guid.NewGuid():N}{Path.GetExtension(path)}");
        File.Move(path, binned);

        return new RecycleOutcome(Removed: true) { Binned = binned };
    });

    /// <summary>A bin that refuses, as the shell does for a path it will not parse.</summary>
    public static FakeRecycleBin Refusing(string message) =>
        new(_ => new RecycleOutcome(Removed: false, message));

    /// <summary>
    /// What the bin cannot take, by path, as <see cref="ShellRecycleBin"/> answers from the item and
    /// its drive's bin; nothing where a test does not say. <see cref="Recycle"/> refuses on it, as the
    /// real bin does.
    /// </summary>
    public Func<string, string?> CannotTake { get; set; } = _ => null;

    /// <summary>Every path the bin was asked whether it can take, across every question.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>Every list the bin was asked about, as it was asked.</summary>
    public List<IReadOnlyList<string>> Questions { get; } = [];

    public IReadOnlyList<string?> WhyItCannotTake(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        Questions.Add([.. paths]);
        var answers = new string?[paths.Count];

        for (var i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            Asked.Add(paths[i]);
            answers[i] = CannotTake(paths[i]);
        }

        return answers;
    }

    /// <summary>Every batch handed to <see cref="Recycle"/>, as it was handed.</summary>
    public List<IReadOnlyList<string>> Batches { get; } = [];

    /// <summary>
    /// Takes each item in turn as the shell does: refused where <see cref="CannotTake"/> says, and
    /// otherwise handed to the behaviour. A cancel, or the caller's rule, stops it before the next
    /// item, as the shell's sink does, so that item and every one after it are not reached and are not
    /// recorded in <see cref="Paths"/>.
    /// </summary>
    public IReadOnlyList<RecycleOutcome> Recycle(
        IReadOnlyList<string> paths, CancellationToken ct = default, Func<int, RecycleOutcome, bool>? stopsAfter = null)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(paths.Count, IRecycleBin.LargestBatch, nameof(paths));
        Batches.Add([.. paths]);

        var outcomes = new List<RecycleOutcome>(paths.Count);
        var stopped = false;

        for (var i = 0; i < paths.Count; i++)
        {
            if (ct.IsCancellationRequested || stopped)
            {
                outcomes.Add(ct.IsCancellationRequested ? RecycleOutcome.NotReached : RecycleOutcome.StoppedBefore);
                continue;
            }

            Paths.Add(paths[i]);

            if (CannotTake(paths[i]) is { } why)
            {
                outcomes.Add(new RecycleOutcome(Removed: false, why));
                continue;
            }

            var outcome = _behaviour(paths[i]);
            outcomes.Add(outcome);
            stopped = stopsAfter?.Invoke(i, outcome) == true;
        }

        return outcomes;
    }
}
