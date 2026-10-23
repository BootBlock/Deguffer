using Deguffer.Core.Startup;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// What may be changed (§7.5), and which refusal is given where several apply, so the same entry
/// always gives the same answer.
/// </summary>
public sealed class StartupChangePolicyTests
{
    [Fact]
    public void AnEntryThatStartsMayBeDisabledAndADisabledOneEnabled()
    {
        Assert.Equal(StartupChange.Disable, StartupChangePolicy.MayChange(Entry(Approval.None), isElevated: false).Change);
        Assert.Equal(StartupChange.Enable, StartupChangePolicy.MayChange(Entry(Approval.Of(State(1))), isElevated: false).Change);
    }

    /// <summary>
    /// A form nobody explained is refused before the rights it would need are mentioned, because
    /// elevating would not change the answer.
    /// </summary>
    [Fact]
    public void AnUnexplainedFormIsTheReasonGivenBeforeTheRightsItWouldNeed()
    {
        var verdict = StartupChangePolicy.MayChange(Entry(Approval.Of(State(6)), StartupSource.MachineRun), isElevated: false);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("form Deguffer has not seen", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableRecordIsRefused()
    {
        var verdict = StartupChangePolicy.MayChange(Entry(Approval.Unreadable), isElevated: true);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("would not open", verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StartupSource.UserRun, true)]
    [InlineData(StartupSource.UserStartupFolder, true)]
    [InlineData(StartupSource.MachineRun, false)]
    [InlineData(StartupSource.MachineRun32, false)]
    [InlineData(StartupSource.CommonStartupFolder, false)]
    public void OnlyThisUsersEntriesMayBeChangedUnelevated(StartupSource source, bool allowed) =>
        Assert.Equal(allowed, StartupChangePolicy.MayChange(Entry(Approval.None, source), isElevated: false).IsAllowed);

    private static StartupEntry Entry(Approval approval, StartupSource source = StartupSource.UserRun) => new(
        new StartupKey(source, "Tool"), "Tool", "tool.exe", null, ProgramStanding.Unknown, null, approval.State, approval);

    private static StoredValue State(uint state)
    {
        var bytes = new byte[Approval.Length];
        BitConverter.GetBytes(state).CopyTo(bytes, 0);

        return new StoredValue(RegistryValueKind.Binary, bytes);
    }
}
