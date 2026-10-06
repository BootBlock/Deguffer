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
            ConfiguredFolder.WhyNotOwned(configured, _environment, _system, new FakeVolumeInventory(), [@"D:\shared\cache\Temp"]));
    }

    /// <summary>
    /// A folder holding a temporary folder is refused however either is named. With <c>S:</c>
    /// substituted for <c>D:\shared</c>, <c>S:\cache</c> holds <c>D:\shared\cache\Temp</c>, and
    /// <c>D:\shared\cache</c> holds <c>S:\cache\Temp</c>. A folder beside it reached the same way is
    /// still a tool's (§5.6).
    /// </summary>
    [Fact]
    public void RefusesAFolderHoldingATemporaryFolderWhereEitherIsNamedThroughASubstitutedLetter()
    {
        var volumes = new FakeVolumeInventory().Substituting(@"S:\", @"D:\shared");
        const string held = "it holds a temporary folder, where other rows remove things.";

        Assert.Equal(held, ConfiguredFolder.WhyNotOwned(@"S:\cache", _environment, _system, volumes, [@"D:\shared\cache\Temp"]));
        Assert.Equal(held, ConfiguredFolder.WhyNotOwned(@"D:\shared\cache", _environment, _system, volumes, [@"S:\cache\Temp"]));
        Assert.Null(ConfiguredFolder.WhyNotOwned(@"S:\gradle", _environment, _system, volumes, [@"D:\shared\cache\Temp"]));
    }

    /// <summary>
    /// A setting naming one of the account's own folders through a letter <c>subst</c> made for the
    /// profile names that folder, and is declined as one. A tool's folder reached the same way is
    /// still the tool's (§5.6).
    /// </summary>
    [Fact]
    public void DeclinesTheAccountsOwnFolderNamedThroughASubstitutedLetter()
    {
        var volumes = new FakeVolumeInventory().Substituting(@"S:\", _environment.UserProfile);
        var fallback = Path.Combine(_environment.UserProfile, ".cargo");

        _environment.WithEnvironmentVariable("CARGO_HOME", @"S:\Downloads");
        var downloads = ConfiguredFolder.FromVariable("CARGO_HOME", fallback, _environment, _system, volumes);

        Assert.Null(downloads.Folder);
        Assert.Equal("it is one of your own folders, where you keep your files.", downloads.Declined);

        _environment.WithEnvironmentVariable("CARGO_HOME", @"S:\.cargo");
        var cargo = ConfiguredFolder.FromVariable("CARGO_HOME", fallback, _environment, _system, volumes);

        Assert.Equal(@"S:\.cargo", cargo.Folder);
        Assert.Null(cargo.Declined);
    }
}
