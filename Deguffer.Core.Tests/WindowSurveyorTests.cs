using Deguffer.Core.Memory;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.2.1 posts a close only to a top-level window that is unowned, visible and not cloaked, and
/// refuses the whole process where any of its windows is a console's. These hold each of those rules
/// on its own, and hold the difference between a set with nothing in it and a set that could not be
/// read.
/// </summary>
public sealed class WindowSurveyorTests
{
    private const int Picked = 4_200;

    [Fact]
    public void AnUnownedVisibleUncloakedWindowQualifies()
    {
        var survey = Take(new FakeWindow { Handle = 11, ProcessId = Picked, ClassName = "AWindowClass" });

        Assert.Equal(Answer.No, survey.OwnsConsoleWindow);
        Assert.Equal([new ProcessWindow(11, "AWindowClass")], survey.Qualifying);
    }

    /// <summary>A dialog is an owned window, and closing a dialog is not closing the program.</summary>
    [Fact]
    public void AnOwnedWindowDoesNotQualify() =>
        Assert.Empty(Qualifying(new FakeWindow { Handle = 11, ProcessId = Picked, Owned = true }));

    [Fact]
    public void AnInvisibleWindowDoesNotQualify() =>
        Assert.Empty(Qualifying(new FakeWindow { Handle = 11, ProcessId = Picked, Visible = false }));

    /// <summary>
    /// A cloaked window has every trapping of visibility without being on screen, which is how the
    /// shell holds a window on another virtual desktop.
    /// </summary>
    [Fact]
    public void ACloakedWindowDoesNotQualify() =>
        Assert.Empty(Qualifying(new FakeWindow { Handle = 11, ProcessId = Picked, Cloaked = true }));

    [Fact]
    public void AWindowOfAnotherProcessIsNotSurveyed() =>
        Assert.Empty(Qualifying(new FakeWindow { Handle = 11, ProcessId = Picked + 1 }));

    [Fact]
    public void AProcessWithNoWindowThatQualifiesAnswersAnEmptySetRatherThanANull()
    {
        var survey = Take(new FakeWindow { Handle = 11, ProcessId = Picked, Visible = false });

        Assert.NotNull(survey.Qualifying);
        Assert.Empty(survey.Qualifying);
    }

    /// <summary>
    /// The console's window is reported against an attached process, and closing a console ends every
    /// process attached to it, so the whole process is refused rather than the one window.
    /// </summary>
    [Theory]
    [InlineData("ConsoleWindowClass")]
    [InlineData("PseudoConsoleWindow")]
    public void AConsoleClassRefusesTheWholeProcess(string className)
    {
        var survey = Take(
            new FakeWindow { Handle = 11, ProcessId = Picked, ClassName = "AWindowClass" },
            new FakeWindow { Handle = 12, ProcessId = Picked, ClassName = className });

        Assert.Equal(Answer.Yes, survey.OwnsConsoleWindow);
        Assert.Null(survey.Qualifying);
    }

    /// <summary>
    /// A graphical program can own an invisible console window, and whether a console's window is
    /// visible says nothing about which programs share the console.
    /// </summary>
    [Fact]
    public void AnInvisibleOwnedConsoleWindowStillRefusesTheWholeProcess()
    {
        var survey = Take(new FakeWindow
        {
            Handle = 11,
            ProcessId = Picked,
            ClassName = "ConsoleWindowClass",
            Visible = false,
            Owned = true,
        });

        Assert.Equal(Answer.Yes, survey.OwnsConsoleWindow);
    }

    [Fact]
    public void AConsoleWindowOfAnotherProcessRefusesNothing()
    {
        var survey = Take(new FakeWindow { Handle = 11, ProcessId = Picked + 1, ClassName = "ConsoleWindowClass" });

        Assert.Equal(Answer.No, survey.OwnsConsoleWindow);
    }

    [Fact]
    public void AnEnumerationWindowsRefusesLeavesBothFactsUnread()
    {
        var calls = new FakeWindowCalls { Enumerates = false };

        var survey = WindowSurveyor.Take(calls, Picked);

        Assert.Equal(Answer.Unreadable, survey.OwnsConsoleWindow);
        Assert.Null(survey.Qualifying);
    }

    /// <summary>
    /// The class decides both facts, so a window that is still there and will not name its class costs
    /// both. Reading it as "no console" would be a guess about the one thing the whole process is
    /// refused for.
    /// </summary>
    [Fact]
    public void AClassThatWillNotBeReadLeavesBothFactsUnread()
    {
        var survey = Take(new FakeWindow { Handle = 11, ProcessId = Picked, ClassName = null });

        Assert.Equal(Answer.Unreadable, survey.OwnsConsoleWindow);
        Assert.Null(survey.Qualifying);
    }

    /// <summary>
    /// Windows are created and destroyed constantly. One that has gone since the enumeration is not a
    /// fact Windows refused, so it costs nothing.
    /// </summary>
    [Fact]
    public void AWindowThatHasGoneSinceTheEnumerationIsSkipped()
    {
        var survey = Take(
            new FakeWindow { Handle = 11, ProcessId = Picked, ClassName = null, Exists = false },
            new FakeWindow { Handle = 12, ProcessId = Picked, ClassName = "AWindowClass" });

        Assert.Equal(Answer.No, survey.OwnsConsoleWindow);
        Assert.Equal([new ProcessWindow(12, "AWindowClass")], survey.Qualifying);
    }

    /// <summary>
    /// An owner or a cloak that will not be read leaves the set incomplete, but the class was read, so
    /// whether the process owns a console window is still answered.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AQualificationThatWillNotBeReadLeavesTheSetUnread(bool ownerIsTheOneRefused)
    {
        var survey = Take(new FakeWindow
        {
            Handle = 11,
            ProcessId = Picked,
            Owned = ownerIsTheOneRefused ? null : false,
            Cloaked = ownerIsTheOneRefused ? false : null,
        });

        Assert.Equal(Answer.No, survey.OwnsConsoleWindow);
        Assert.Null(survey.Qualifying);
    }

    [Fact]
    public void AWindowThatGoesWhileItIsBeingJudgedIsSkipped()
    {
        var survey = Take(new FakeWindow { Handle = 11, ProcessId = Picked, Cloaked = null, Exists = false });

        Assert.NotNull(survey.Qualifying);
        Assert.Empty(survey.Qualifying);
    }

    /// <summary>
    /// §7.2.1 posts in the order Windows enumerates them, and names the count in the confirmation, so
    /// the set is a sequence rather than a bag.
    /// </summary>
    [Fact]
    public void TheOrderWindowsEnumeratedThemIsKept()
    {
        var survey = Take(
            new FakeWindow { Handle = 12, ProcessId = Picked, ClassName = "TheSecondClass" },
            new FakeWindow { Handle = 11, ProcessId = Picked, ClassName = "TheFirstClass" });

        Assert.Equal(
            [new ProcessWindow(12, "TheSecondClass"), new ProcessWindow(11, "TheFirstClass")],
            survey.Qualifying);
    }

    /// <summary>
    /// A console found anywhere among its windows refuses the whole process, whatever another window
    /// would not say: the refusal is certain, and the unread fact could only refuse as well.
    /// </summary>
    [Fact]
    public void AConsoleClassRefusesEvenWhereAnotherWindowWouldNotBeRead()
    {
        var survey = Take(
            new FakeWindow { Handle = 11, ProcessId = Picked, ClassName = null },
            new FakeWindow { Handle = 12, ProcessId = Picked, ClassName = "ConsoleWindowClass" });

        Assert.Equal(Answer.Yes, survey.OwnsConsoleWindow);
        Assert.Null(survey.Qualifying);
    }

    /// <summary>Two windows failing in different ways cost what each costs, rather than one masking the other.</summary>
    [Fact]
    public void TwoWindowsFailingDifferentlyCostBothFacts()
    {
        var survey = Take(
            new FakeWindow { Handle = 11, ProcessId = Picked, ClassName = null },
            new FakeWindow { Handle = 12, ProcessId = Picked, Cloaked = null });

        Assert.Equal(Answer.Unreadable, survey.OwnsConsoleWindow);
        Assert.Null(survey.Qualifying);
    }

    /// <summary>
    /// A window still on the desktop that will not say whose it is could be the console window that
    /// refuses the whole process, so it is not quietly taken for another process's.
    /// </summary>
    [Fact]
    public void AWindowThatWillNotSayWhoseItIsCostsBothFacts()
    {
        var survey = Take(new FakeWindow { Handle = 11, ProcessId = null });

        Assert.Equal(Answer.Unreadable, survey.OwnsConsoleWindow);
        Assert.Null(survey.Qualifying);
    }

    [Fact]
    public void AWindowThatWentBeforeItWasAskedWhoseItIsIsSkipped()
    {
        var survey = Take(
            new FakeWindow { Handle = 11, ProcessId = null, Exists = false },
            new FakeWindow { Handle = 12, ProcessId = Picked, ClassName = "AWindowClass" });

        Assert.Equal(Answer.No, survey.OwnsConsoleWindow);
        Assert.Equal([new ProcessWindow(12, "AWindowClass")], survey.Qualifying);
    }

    /// <summary>Another process's window is nothing to this survey, whatever it will or will not answer.</summary>
    [Fact]
    public void AWindowOfAnotherProcessThatWillNotNameItsClassCostsNothing()
    {
        var survey = Take(new FakeWindow { Handle = 11, ProcessId = Picked + 1, ClassName = null });

        Assert.Equal(Answer.No, survey.OwnsConsoleWindow);
        Assert.NotNull(survey.Qualifying);
        Assert.Empty(survey.Qualifying);
    }

    private static WindowSurvey Take(params FakeWindow[] windows)
    {
        var calls = new FakeWindowCalls();

        foreach (var window in windows)
        {
            calls.With(window);
        }

        return WindowSurveyor.Take(calls, Picked);
    }

    private static IReadOnlyList<ProcessWindow> Qualifying(params FakeWindow[] windows) =>
        Take(windows).Qualifying ?? throw new InvalidOperationException("The set was not read.");
}
