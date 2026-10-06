using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which temporary folders <see cref="TempRoots"/> offers when a setting names one through another
/// path to it: a letter <c>subst</c> made for a folder, here <c>S:</c>. Its three comparisons (a
/// repeat, a nested pair and the machine's own folder) are asked of the folders, so each is as true
/// of <c>S:\temp</c> as of the path it stands for. A different folder reached the same way is still
/// offered (§5.6).
/// </summary>
public sealed class TempRootsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeVolumeInventory _volumes = new();

    public TempRootsTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "machine"));
    }

    public void Dispose() => _temp.Dispose();

    private string DefaultTemp => Path.Combine(_environment.LocalAppData, "Temp");

    private TempRootSet Resolve() => TempRoots.Resolve(_environment, _system, _volumes);

    /// <summary>
    /// Two settings naming one folder through different letters are one folder, offered once. Offered
    /// twice, its estimate doubled and two steps emptied it.
    /// </summary>
    [Fact]
    public void OffersAFolderNamedThroughASubstitutedLetterOnce()
    {
        _volumes.Substituting(@"S:\", _temp.Path);
        _environment.WithEnvironmentVariable("TMP", @"S:\temp").WithEnvironmentVariable("TEMP", @"S:\Tmp");

        var roots = Resolve();

        Assert.Equal([_environment.TempPath, @"S:\Tmp", DefaultTemp], roots.AccountFolders);
        Assert.Empty(roots.Refused);
    }

    /// <summary>
    /// A setting naming the folder that holds the one this process uses, through a letter, is the outer
    /// of a nested pair (§5.3). Kept, its step emptied the inner folder and removed it. A folder
    /// beside it reached the same way is still offered.
    /// </summary>
    [Fact]
    public void KeepsTheInnerFolderOfAPairNestedThroughASubstitutedLetter()
    {
        _volumes.Substituting(@"S:\", _temp.Path);
        _environment
            .WithTempPath(Path.Combine(_temp.Path, "temp", "2"))
            .WithEnvironmentVariable("TMP", @"S:\temp")
            .WithEnvironmentVariable("TEMP", @"S:\Tmp");

        var roots = Resolve();

        Assert.Equal([_environment.TempPath, @"S:\Tmp", DefaultTemp], roots.AccountFolders);
        var (path, reason) = Assert.Single(roots.Refused);
        Assert.Equal(@"S:\temp", path);
        Assert.Contains("sits inside it", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same, read in the other order: the folder this process uses is the outer one, and the inner
    /// is named through the letter.
    /// </summary>
    [Fact]
    public void KeepsTheInnerFolderNamedThroughASubstitutedLetterWhenTheOuterIsNamedFirst()
    {
        _volumes.Substituting(@"S:\", _temp.Path);
        _environment.WithEnvironmentVariable("TMP", @"S:\temp\2");

        var roots = Resolve();

        Assert.Equal([@"S:\temp\2", DefaultTemp], roots.AccountFolders);
        Assert.Equal(_environment.TempPath, Assert.Single(roots.Refused).Path);
    }

    /// <summary>
    /// A letter standing for the Windows directory reaches its <c>Temp</c>, which is declared whatever
    /// the settings say. Named through the letter, it is not declared a second time without the
    /// administrator rights it needs, and a folder inside it is left to it. A folder beside it reached
    /// the same way is not refused for being inside it.
    /// </summary>
    [Fact]
    public void LeavesTheMachinesFolderNamedThroughASubstitutedLetterToItsOwnStep()
    {
        _volumes.Substituting(@"S:\", _system.WindowsDirectory);
        _environment
            .WithEnvironmentVariable("TMP", @"S:\Temp\1")
            .WithEnvironmentVariable("TEMP", @"S:\Temp");

        var roots = Resolve();

        Assert.Equal([_environment.TempPath, DefaultTemp], roots.AccountFolders);
        var (path, reason) = Assert.Single(roots.Refused);
        Assert.Equal(@"S:\Temp\1", path);
        Assert.Contains("temporary folder Windows itself uses", reason, StringComparison.Ordinal);

        _environment.WithEnvironmentVariable("TEMP", @"S:\Tmp");

        Assert.Contains(@"S:\Tmp", Resolve().AccountFolders);
    }
}
