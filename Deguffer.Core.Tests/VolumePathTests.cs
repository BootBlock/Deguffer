using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Splitting a path into the volume and the components below its root — what an MFT lookup needs,
/// since the table knows nothing about drive letters.
/// </summary>
public class VolumePathTests
{
    [Fact]
    public void SplitsALocalPath()
    {
        Assert.True(VolumePath.TryParse(@"C:\Users\testuser\.npm-cache", out var parsed));

        Assert.Equal('C', parsed.DriveLetter);
        Assert.Equal(["Users", "testuser", ".npm-cache"], parsed.Components);
    }

    /// <summary>
    /// §6.3 puts every path in Core through LongPath, so most arrive here already prefixed. Failing
    /// to strip that would send every measurement down the fallback path on an elevated machine —
    /// working, but slowly, and for no visible reason.
    /// </summary>
    [Fact]
    public void StripsTheExtendedLengthPrefix()
    {
        Assert.True(VolumePath.TryParse(@"\\?\C:\Users\testuser", out var parsed));

        Assert.Equal('C', parsed.DriveLetter);
        Assert.Equal(["Users", "testuser"], parsed.Components);
    }

    [Fact]
    public void NormalisesTheDriveLetterSoLookupsMatch()
    {
        Assert.True(VolumePath.TryParse(@"d:\cache", out var parsed));

        Assert.Equal('D', parsed.DriveLetter);
    }

    [Fact]
    public void TreatsTheVolumeRootAsNoComponents()
    {
        Assert.True(VolumePath.TryParse(@"C:\", out var parsed));

        Assert.Empty(parsed.Components);
    }

    /// <summary>A UNC path has no MFT this process can open, so it must take the walk.</summary>
    [Theory]
    [InlineData(@"\\server\share\cache")]
    [InlineData(@"\\?\UNC\server\share")]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesWhatTheFastPathCannotServe(string path) =>
        Assert.False(VolumePath.TryParse(path, out _));

    /// <summary>
    /// §6.3: a path past MAX_PATH must survive the split intact. Truncating here would resolve a
    /// shorter path than the caller asked about and measure the wrong directory.
    /// </summary>
    [Fact]
    public void KeepsEveryComponentOfAPathBeyondMaxPath()
    {
        using var temp = new TempDirectory();

        var deep = temp.Path;
        var added = 0;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('d', 40));
            added++;
        }

        Assert.True(VolumePath.TryParse(LongPath.Extended(deep), out var parsed));

        Assert.True(deep.Length > 260);
        Assert.Equal(added, parsed.Components.Count(c => c == new string('d', 40)));
        Assert.Equal(deep[0], parsed.DriveLetter);
    }
}
