using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a row records about itself. The whole of the rule is which steps get an entry, and the
/// dangerous case is a step the user was never given the control to answer for: recording the
/// app's own untick as the user's carries it into the run where the step can finally be acted on.
/// </summary>
public class RememberedSelectionTests
{
    private const string Caches = @"C:\Users\testuser\.gradle\caches";
    private const string Daemon = @"C:\Users\testuser\.gradle\daemon";
    private const string Native = @"C:\Users\testuser\.gradle\native";

    [Fact]
    public void RecordsEveryStepTheUserCouldChoose()
    {
        var remembered = RememberedSelection.Of(
            isSelected: true,
            [(Caches, true, true), (Daemon, false, true)]);

        Assert.True(remembered.IsSelected);
        Assert.True(remembered.Steps[Caches]);
        Assert.False(remembered.Steps[Daemon]);
    }

    /// <summary>
    /// A recognised child that is empty today is a nought-byte step, so its checkbox is disabled and
    /// its value is Deguffer's rather than the user's. Recorded as a plain <c>false</c> it would be
    /// indistinguishable from a real untick, and once the tool wrote into that directory the user's
    /// ticked row would quietly leave it behind with nothing on screen to say so.
    /// </summary>
    [Fact]
    public void LeavesOutAStepTheUserCouldNotChoose()
    {
        var remembered = RememberedSelection.Of(
            isSelected: true,
            [(Caches, true, true), (Native, false, false)]);

        Assert.DoesNotContain(Native, remembered.Steps.Keys);
    }

    /// <summary>
    /// Omitted means unanswered, and unanswered follows the row. This is the half that makes the
    /// omission worth anything, so it is asserted here rather than left to the reader.
    /// </summary>
    [Fact]
    public void AnOmittedStepThenStartsFromTheRow()
    {
        var memory = new SelectionMemory(new Dictionary<string, RememberedSelection>
        {
            ["gradle"] = RememberedSelection.Of(
                isSelected: true,
                [(Caches, true, true), (Native, false, false)]),
        });

        var row = memory.RowStartsSelected("gradle", SafetyTier.RegenerableCache, byDefault: true);

        Assert.True(row);
        Assert.True(memory.StepStartsSelected("gradle", SafetyTier.RegenerableCache, Native, row));
    }

    [Fact]
    public void RecordsNoStepsForARowThatHasNone()
    {
        Assert.Empty(RememberedSelection.Of(isSelected: false, []).Steps);
    }
}
