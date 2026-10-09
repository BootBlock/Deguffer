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

    /// <summary>Every path the bin was asked whether it can take.</summary>
    public List<string> Asked { get; } = [];

    public string? WhyItCannotTake(string path)
    {
        Asked.Add(path);
        return CannotTake(path);
    }

    public RecycleOutcome Recycle(string path)
    {
        Paths.Add(path);

        return CannotTake(path) is { } why
            ? new RecycleOutcome(Removed: false, why)
            : _behaviour(path);
    }
}
