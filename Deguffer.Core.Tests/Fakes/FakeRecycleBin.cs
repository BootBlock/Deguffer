using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests.Fakes;

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

    /// <summary>A bin that refuses, as the shell does for a path it will not parse.</summary>
    public static FakeRecycleBin Refusing(string message) =>
        new(_ => new RecycleOutcome(Removed: false, message));

    public RecycleOutcome Recycle(string path)
    {
        Paths.Add(path);
        return _behaviour(path);
    }
}
