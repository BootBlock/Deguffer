using Deguffer.Core.Startup;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which <c>StartupApproved</c> key records each source's entries, as Task Manager and Settings were
/// measured using them (§7.5). A source paired with the wrong key would read another entry's state and
/// write a value that disables nothing, or disables an entry of the same name in another view.
/// </summary>
public sealed class StartupSourceTests
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    [Theory]
    [InlineData(StartupSource.UserRun, @"HKEY_CURRENT_USER\" + Approved + @"\Run")]
    [InlineData(StartupSource.MachineRun, @"HKEY_LOCAL_MACHINE\" + Approved + @"\Run")]
    [InlineData(StartupSource.MachineRun32, @"HKEY_LOCAL_MACHINE\" + Approved + @"\Run32")]
    [InlineData(StartupSource.UserStartupFolder, @"HKEY_CURRENT_USER\" + Approved + @"\StartupFolder")]
    [InlineData(StartupSource.CommonStartupFolder, @"HKEY_LOCAL_MACHINE\" + Approved + @"\StartupFolder")]
    public void EachSourceIsRecordedWhereWindowsRecordsIt(StartupSource source, string key) =>
        Assert.Equal(key, source.ApprovalLocation());

    [Fact]
    public void APackagedTaskHasNoApprovalKey() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => StartupSource.PackagedTask.ApprovalLocation());
}
