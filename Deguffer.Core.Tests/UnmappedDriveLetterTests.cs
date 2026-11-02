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

    [Fact]
    public void RefusesToTakeMoreLettersThanTheMachineHasFree() =>
        Assert.Throws<InvalidOperationException>(() => UnmappedDriveLetter.Take(25));
}
