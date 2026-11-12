using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A program whose publisher puts a release number or a platform in its executable's name is found by
/// the beginning of that name, and nothing else is: a rule that matched too little lets Deguffer delete
/// what the program is writing, and one that matched too much holds a row back for no reason.
/// </summary>
public sealed class ProcessNameRuleTests
{
    private static readonly ProcessNameRule Tool =
        ProcessNameRule.Named("tool", "tool-gui").OrStartingWith("tool-cli-windows-", "tool-gui-windows-");

    [Theory]
    [InlineData("tool-cli-windows-x64")]
    [InlineData("tool-gui-windows-x64-v1.2.3")]
    [InlineData("TOOL-GUI-WINDOWS-X64_V1.0.0")]
    public void ANameABeginningMatchesIsFoundAsTheMachineReportsIt(string running)
    {
        Assert.Equal([running], Tool.Among(["svchost", running, "explorer"]));
    }

    [Theory]
    [InlineData("toolx")]
    [InlineData("tool-cli")]
    [InlineData("tool-gui2")]
    [InlineData("my-tool-cli-windows-x64")]
    public void ANameThatOnlyResemblesTheProgramsIsNotFound(string running)
    {
        Assert.Empty(Tool.Among([running]));
    }

    /// <summary>Exact names come back as the rule spells them and in its order, as a list of names always has.</summary>
    [Fact]
    public void ExactNamesAreFoundInTheRulesOrderAndSpelling()
    {
        Assert.Equal(["tool", "tool-gui"], Tool.Among(["TOOL-GUI", "Tool"]));
    }

    /// <summary>Several processes of one program are one name, however they are found.</summary>
    [Fact]
    public void EachNameIsFoundOnceHoweverManyProcessesShareIt()
    {
        Assert.Equal(
            ["tool", "tool-gui-windows-x64"],
            Tool.Among(["tool", "tool", "tool-gui-windows-x64", "Tool-Gui-Windows-X64"]));
    }

    [Fact]
    public void ANameListedTwiceIsFoundOnce()
    {
        Assert.Equal(["tool"], ProcessNameRule.Named("tool", "TOOL").Among(["tool"]));
    }

    /// <summary>
    /// The fake answers a rule from the same snapshot as a list of names, so a check before a removal that
    /// asks by rule is held to the clean's reading limit as every other check is.
    /// </summary>
    [Fact]
    public void TheFakeAnswersARuleFromItsSnapshot()
    {
        var inspector = FakeProcessInspector.NothingRunning;
        Assert.Empty(inspector.FindRunning(Tool));

        inspector.WithRunning("tool-gui-windows-x64-v2.0.0");
        Assert.Empty(inspector.FindRunning(Tool));

        inspector.Invalidate();
        Assert.Equal(["tool-gui-windows-x64-v2.0.0"], inspector.FindRunning(Tool));
    }
}
