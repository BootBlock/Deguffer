namespace Deguffer.Testing;

/// <summary>
/// Drive letters the machine running the suite has not given to anything: no volume, no mapped
/// share, no <c>subst</c>. For a test that has <see cref="FakeVolumeInventory.Substituting"/> say a
/// letter stands for a folder it built.
///
/// <para>The fake answers what the letter stands for, but a provider still reads a path through the
/// letter on the real disk. A letter the machine uses would let that reading find whatever the
/// machine keeps there, so the test's answer would depend on the machine running it. A letter
/// nothing holds answers every such reading with "not there", as the fake's own folder does where
/// the test did not build what the path names.</para>
/// </summary>
public static class UnmappedDriveLetter
{
    /// <summary>
    /// The roots, such as <c>Q:\</c>, of <paramref name="count"/> distinct letters the machine is
    /// not using now.
    ///
    /// <para>Taken from <c>Z</c> downwards because Windows gives a disk plugged in during the run the
    /// lowest free letter, so a letter taken from the top is the last to be claimed under the test.
    /// <c>A</c> and <c>B</c> are never taken: Windows keeps them for floppy drives.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The machine has fewer free letters than that.</exception>
    public static IReadOnlyList<string> Take(int count)
    {
        var used = new HashSet<char>(Environment.GetLogicalDrives().Select(root => char.ToUpperInvariant(root[0])));
        var free = Enumerable.Range('C', 'Z' - 'C' + 1)
            .Reverse()
            .Select(letter => (char)letter)
            .Where(letter => !used.Contains(letter))
            .Take(count)
            .Select(letter => $@"{letter}:\")
            .ToList();

        return free.Count == count
            ? free
            : throw new InvalidOperationException(
                $"The test needs {count} drive letter(s) the machine is not using, and it has {free.Count}.");
    }

    /// <summary>The root of one letter the machine is not using now. See <see cref="Take"/>.</summary>
    public static string Any() => Take(1)[0];
}
