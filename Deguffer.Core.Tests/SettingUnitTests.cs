using Deguffer.Core.Text;

namespace Deguffer.Core.Tests;

/// <summary>A number box and the word after it read as one phrase, in the singular only for one.</summary>
public sealed class SettingUnitTests
{
    [Theory]
    [InlineData(0, "hours")]
    [InlineData(1, "hour")]
    [InlineData(8, "hours")]
    [InlineData(double.NaN, "hours")]
    public void HoursAgreeWithTheBox(double shown, string expected) =>
        Assert.Equal(expected, SettingUnit.Hours(shown));

    [Theory]
    [InlineData(1, "day")]
    [InlineData(7, "days")]
    public void DaysAgreeWithTheBox(double shown, string expected) =>
        Assert.Equal(expected, SettingUnit.Days(shown));

    /// <summary>An empty box shows Auto and the count it stands for, and the unit agrees with that count.</summary>
    [Theory]
    [InlineData(double.NaN, 1, "thread")]
    [InlineData(double.NaN, 4, "threads")]
    [InlineData(1, 4, "thread")]
    [InlineData(2, 1, "threads")]
    public void ThreadsAgreeWithWhatTheBoxShows(double shown, int whenEmpty, string expected) =>
        Assert.Equal(expected, SettingUnit.Threads(shown, whenEmpty));

    [Theory]
    [InlineData(double.NaN, 1, "read")]
    [InlineData(double.NaN, 8, "reads")]
    [InlineData(1, 8, "read")]
    public void ReadsAgreeWithWhatTheBoxShows(double shown, int whenEmpty, string expected) =>
        Assert.Equal(expected, SettingUnit.Reads(shown, whenEmpty));
}
