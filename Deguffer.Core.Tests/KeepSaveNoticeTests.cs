using Deguffer.Core.Configuration;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the page says when a change to the keep list could not be saved: what still holds, and for
/// how long, in each case.
/// </summary>
public sealed class KeepSaveNoticeTests
{
    [Fact]
    public void AKeepThatCouldNotBeSavedLastsUntilARestart() =>
        Assert.Equal(
            "Kept cache for now, but Deguffer could not save the keep list, so it will be offered again after a restart. "
            + @"Check that %LOCALAPPDATA%\Deguffer is writable.",
            KeepSaveNotice.For(keeping: true, "cache", storedListUnreadable: false));

    [Fact]
    public void AReleaseThatCouldNotBeSavedDidNotHappen() =>
        Assert.Equal(
            "cache is still on your keep list: Deguffer could not save the change.",
            KeepSaveNotice.For(keeping: false, "cache", storedListUnreadable: false));

    /// <summary>
    /// A list that could not be read at launch is never written over, so either change lasts this
    /// session, and the sentence names the file to repair.
    /// </summary>
    [Theory]
    [InlineData(true, "Kept")]
    [InlineData(false, "Stopped keeping")]
    public void AChangeBesideAnUnreadableListLastsThisSession(bool keeping, string verb) =>
        Assert.Equal(
            $"{verb} cache for this session only. Deguffer could not read the keep list saved in "
            + @"%LOCALAPPDATA%\Deguffer\keep.json when it started, so it is not saving over it. "
            + "Repair or remove that file, then restart Deguffer.",
            KeepSaveNotice.For(keeping, "cache", storedListUnreadable: true));
}
