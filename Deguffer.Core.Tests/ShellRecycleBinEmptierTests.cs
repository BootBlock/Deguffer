using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The real emptier, exercised only where it refuses.
///
/// <para><b>Nothing here reaches <c>SHEmptyRecycleBin</c>, and nothing here may.</b> The call has no
/// dry run and no scope narrower than a volume, so a test that made it would destroy the deleted
/// files of whoever ran the suite. Everything the call itself does is proved through
/// <c>FakeRecycleBinEmptier</c> instead, and what is left here is the guard that runs before it —
/// which is the half a fake cannot stand in for, because the fake is what the guard exists to
/// distinguish this type from.</para>
/// </summary>
public class ShellRecycleBinEmptierTests
{
    /// <summary>
    /// The scope of the call over a volume root is what was measured; its scope over anything else
    /// is not stated anywhere, by Microsoft or by us. So a path that is not a root is refused rather
    /// than tried, and the refusal is readable.
    ///
    /// <para>The case that makes it worth having is a derivation that has gone wrong — a bin laid
    /// out somewhere the two-levels-up rule does not describe. Without this, that would reach the
    /// shell as a request nobody could say the reach of, and the answer would come back as success.</para>
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\testuser")]
    [InlineData(@"C:\Users\testuser\$Recycle.Bin")]
    [InlineData(@"C:\Users\testuser\$Recycle.Bin\S-1-5-21-1111111111-2222222222-3333333333-1001")]
    [InlineData(@"C:")]
    [InlineData(@"..")]
    public void RefusesAnythingThatIsNotADriveRoot(string path)
    {
        var outcome = ShellRecycleBinEmptier.Default.Empty(path);

        Assert.False(outcome.Emptied);
        Assert.Contains("not the root of a drive", outcome.Message);
        Assert.Contains(path, outcome.Message);
    }

    [Fact]
    public void RefusesAnEmptyPathOutright()
    {
        Assert.Throws<ArgumentException>(() => ShellRecycleBinEmptier.Default.Empty("   "));
        Assert.Throws<ArgumentNullException>(() => ShellRecycleBinEmptier.Default.Empty(null!));
    }
}
