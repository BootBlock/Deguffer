using Deguffer.Core.Memory;
using Deguffer.Core.Startup;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// The badge beside an entry's name (§7.5) names its state in the pair the change button uses, which
/// is the pair Task Manager and Settings use, so the page never calls one state by two words.
/// </summary>
public sealed class StartupMarksTests
{
    [Fact]
    public void AnEntryWindowsStartsIsEnabledTheWordTheEnableButtonLeavesItAs()
    {
        var marks = Marks(Approval.None);

        Assert.Equal("Enabled", marks.State);
        Assert.Equal(StartupWording.For(StartupChange.Enable) + "d", marks.State);
    }

    [Fact]
    public void ADisabledEntryIsDisabledTheWordTheDisableButtonLeavesItAs()
    {
        var marks = Marks(Approval.Of(DisabledValue()));

        Assert.Equal("Disabled", marks.State);
        Assert.Equal(StartupWording.For(StartupChange.Disable) + "d", marks.State);
    }

    [Fact]
    public void AnEntryWhoseStateCouldNotBeReadIsUnknown() =>
        Assert.Equal("Unknown", Marks(Approval.Unreadable).State);

    private static StartupMarks Marks(Approval approval) => StartupMarks.For(
        new StartupEntry(new StartupKey(StartupSource.UserRun, "Tool"), "Tool", "tool.exe", null, ProgramStanding.Unknown, null, approval.State, approval),
        running: null,
        ProcessFigures.Checked,
        isElevated: false);

    // The low bit of the state says the entry is disabled (§7.5).
    private static StoredValue DisabledValue()
    {
        var bytes = new byte[Approval.Length];
        BitConverter.GetBytes(3u).CopyTo(bytes, 0);

        return new StoredValue(RegistryValueKind.Binary, bytes);
    }
}
