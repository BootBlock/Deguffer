using Deguffer.Core.Startup;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// The <c>StartupApproved</c> value is undocumented, so §7.5 reads it only in the shape that was
/// measured and replaces only what it can account for. These hold both halves against the values
/// Settings and Task Manager were measured writing.
/// </summary>
public sealed class ApprovalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0u, StartupState.Starts)]
    [InlineData(1u, StartupState.Disabled)]
    [InlineData(2u, StartupState.Starts)]
    [InlineData(3u, StartupState.Disabled)]
    public void EveryMeasuredStateReadsByItsLowBitAndMayBeReplaced(uint state, StartupState expected)
    {
        var approval = Approval.Of(Value(state));

        Assert.Equal(expected, approval.State);
        Assert.True(approval.IsReplaceable);
    }

    /// <summary>
    /// The 4 bit was seen once and nothing explained it. Task Manager showed that value by its low
    /// bit, so the page does too, but replacing it would throw away whatever the other bit records.
    /// </summary>
    [Theory]
    [InlineData(6u, StartupState.Starts)]
    [InlineData(7u, StartupState.Disabled)]
    [InlineData(0x10u, StartupState.Starts)]
    public void AStateWithABitNobodyExplainedReadsByItsLowBitAndIsNotReplaced(uint state, StartupState expected)
    {
        var approval = Approval.Of(Value(state));

        Assert.Equal(expected, approval.State);
        Assert.False(approval.IsReplaceable);
    }

    [Fact]
    public void AValueOfAnotherLengthOrTypeSaysNothingAndIsNotReplaced()
    {
        var shorter = Approval.Of(new StoredValue(RegistryValueKind.Binary, new byte[] { 2, 0, 0, 0 }));
        var number = Approval.Of(new StoredValue(RegistryValueKind.DWord, 2));

        Assert.Equal(StartupState.Unknown, shorter.State);
        Assert.False(shorter.IsReplaceable);
        Assert.Equal(StartupState.Unknown, number.State);
        Assert.False(number.IsReplaceable);
    }

    [Fact]
    public void NoValueStartsAndMayBeReplaced()
    {
        Assert.Equal(StartupState.Starts, Approval.None.State);
        Assert.True(Approval.None.IsReplaceable);
    }

    /// <summary>A key nobody could open is not a key with nothing in it, which would read as "starts".</summary>
    [Fact]
    public void AnUnreadableKeySaysNothingAndIsNotReplaced()
    {
        Assert.Equal(StartupState.Unknown, Approval.Unreadable.State);
        Assert.False(Approval.Unreadable.IsReplaceable);
    }

    [Fact]
    public void DisablingWritesTaskManagersStateAndTheTime()
    {
        var written = Approval.DisabledOn(Now);

        Assert.Equal(RegistryValueKind.Binary, written.Kind);
        Assert.Equal([3, 0, 0, 0, .. BitConverter.GetBytes(Now.UtcDateTime.ToFileTimeUtc())], written.Bytes);
        Assert.Equal(StartupState.Disabled, Approval.Of(written).State);
        Assert.Equal(Now.UtcDateTime, Approval.Of(written).DisabledAt);
    }

    [Fact]
    public void EnablingWritesTaskManagersStateAndAZeroTime()
    {
        var written = Approval.Starting();

        Assert.Equal(RegistryValueKind.Binary, written.Kind);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, written.Bytes);
        Assert.Equal(StartupState.Starts, Approval.Of(written).State);
        Assert.Null(Approval.Of(written).DisabledAt);
    }

    private static StoredValue Value(uint state)
    {
        var bytes = new byte[Approval.Length];
        BitConverter.GetBytes(state).CopyTo(bytes, 0);

        return new StoredValue(RegistryValueKind.Binary, bytes);
    }
}
