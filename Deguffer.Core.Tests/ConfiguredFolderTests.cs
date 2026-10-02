using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The refusal every provider handed a folder by a setting asks, read through the same
/// <see cref="LongPath.Configured"/> call the setting goes through.
/// </summary>
public sealed class ConfiguredFolderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public ConfiguredFolderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "machine"));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A folder holding a temporary folder is refused in every spelling a setting may use.
    ///
    /// <para><c>//?/</c> used to leave <see cref="LongPath.Configured"/> still prefixed, and the
    /// temporary folder it is compared with is in display form, so the two never matched and a
    /// folder other rows empty was offered as a tool's own. The folders are invented and carry no
    /// <c>~</c>, so no 8.3 lookup on the machine running this can rewrite either side.</para>
    /// </summary>
    [Theory]
    [InlineData(@"D:\shared\cache")]
    [InlineData(@"//?/D:/shared/cache")]
    [InlineData(@"\\.\D:\shared\cache")]
    [InlineData(@"\??\D:\shared\cache")]
    public void RefusesAFolderHoldingATemporaryFolderInEverySpelling(string value)
    {
        var configured = LongPath.Configured(value)!;

        Assert.Equal(
            "it holds a temporary folder, where other rows remove things.",
            ConfiguredFolder.WhyNotOwned(configured, _environment, _system, [@"D:\shared\cache\Temp"]));
    }
}
