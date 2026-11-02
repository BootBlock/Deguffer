using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The letters a substituted-letter test pretends with are ones the machine running it does not
/// use, so a reading of a path through one finds nothing of the machine's.
/// </summary>
public sealed class UnmappedDriveLetterTests
{
    [Fact]
    public void TakesDistinctLettersTheMachineDoesNotUse()
    {
        var used = Environment.GetLogicalDrives();

        var letters = UnmappedDriveLetter.Take(2);

        Assert.Equal(2, letters.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(letters, letter =>
        {
            Assert.Matches(@"^[C-Z]:\\$", letter);
            Assert.DoesNotContain(letter, used, StringComparer.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(letter));
        });
    }

    /// <summary>
    /// Every letter from <c>C</c> to <c>Z</c> the machine has not given to anything can be taken, and
    /// one more cannot, so a letter the machine uses is never counted as free.
    /// </summary>
    [Fact]
    public void TakesEveryFreeLetterAndRefusesOneMore()
    {
        var used = Environment.GetLogicalDrives().Select(root => char.ToUpperInvariant(root[0])).ToHashSet();
        var free = Enumerable.Range('C', 'Z' - 'C' + 1).Count(letter => !used.Contains((char)letter));

        Assert.Equal(free, UnmappedDriveLetter.Take(free).Count);
        Assert.Throws<InvalidOperationException>(() => UnmappedDriveLetter.Take(free + 1));
    }
}
