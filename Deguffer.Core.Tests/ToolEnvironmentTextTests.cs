using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>The environment block a tool is started with, in the form <c>CreateProcess</c> reads.</summary>
public sealed class ToolEnvironmentTextTests
{
    [Fact]
    public void EachVariableIsEndedByANullInNameOrderAndTheBlockByASecond()
    {
        var block = ToolEnvironmentText.Encode(
            new Dictionary<string, string> { ["windir"] = @"C:\Windows", ["ARDUINO_DIRECTORIES_DATA"] = @"D:\arduino", ["=C:"] = @"C:\" },
            out var refused);

        Assert.Null(refused);
        Assert.Equal("=C:=C:\\\0ARDUINO_DIRECTORIES_DATA=D:\\arduino\0windir=C:\\Windows\0\0", block);
    }

    [Fact]
    public void AnEmptyEnvironmentIsTwoNulls() =>
        Assert.Equal("\0\0", ToolEnvironmentText.Encode(new Dictionary<string, string>(), out _));

    /// <summary>
    /// A null in a value would end it and start a variable nobody set, and an <c>=</c> in a name would
    /// split it, so the whole environment is refused and the variable named.
    /// </summary>
    [Theory]
    [InlineData("PATH", "C:\\bin\0ARDUINO_DIRECTORIES_DATA=D:\\elsewhere")]
    [InlineData("ARDUINO=DATA", "D:\\arduino")]
    [InlineData("", "D:\\arduino")]
    public void AVariableWindowsCannotHandOnWholeIsRefused(string name, string value)
    {
        var block = ToolEnvironmentText.Encode(new Dictionary<string, string> { ["SystemRoot"] = @"C:\Windows", [name] = value }, out var refused);

        Assert.Null(block);
        Assert.Equal(name, refused);
    }
}
