using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A variable naming a file or a folder a tool reads is stamped where the tool opens it: from the
/// folder the tool runs in when it is relative, and nowhere when no path can be made of it.
/// </summary>
public sealed class ToolInputsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ToolInputsTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ARelativeValueIsOpenedFromTheFolderTheToolRunsIn() =>
        Assert.Equal(
            Path.Combine(ToolInputs.WorkingDirectory(_environment), "config", "tool.ini"),
            ToolInputs.Opened(Path.Combine("config", "tool.ini"), _environment));

    [Fact]
    public void AParentStepInARelativeValueIsResolved() =>
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(ToolInputs.WorkingDirectory(_environment))!, "tool.ini"),
            ToolInputs.Opened(Path.Combine("..", "tool.ini"), _environment));

    [Fact]
    public void AFullPathIsOpenedAsItStands()
    {
        var full = Path.Combine(_temp.Path, "elsewhere", "tool.ini");

        Assert.Equal(full, ToolInputs.Opened(full, _environment));
    }

    [Fact]
    public void AnEmptyValueOpensNothing() => Assert.Null(ToolInputs.Opened(string.Empty, _environment));

    [Fact]
    public void AValueHoldingANulOpensNothing() => Assert.Null(ToolInputs.Opened($"con{(char)0}fig", _environment));
}
