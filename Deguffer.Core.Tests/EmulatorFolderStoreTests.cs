using Deguffer.Core.Configuration;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

public sealed class EmulatorFolderStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public EmulatorFolderStoreTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// No file means no folder, and reading that throws nothing: every emulator provider reads the
    /// store on every scan, and most users never declare a folder.
    /// </summary>
    [Fact]
    public void NothingIsDeclaredBeforeAnythingIsSaved()
    {
        IReadOnlyList<string>? folders = null;

        var thrown = ThrownExceptions.During(() => folders = new EmulatorFolderStore(_environment).Load());

        Assert.Empty(folders!);
        Assert.Empty(thrown);
    }

    /// <summary>
    /// An emulator folder is one the user picked, so a name ending in a dot or a space is kept rather
    /// than stored as the folder beside it.
    /// </summary>
    [Fact]
    public void KeepsAPickedFolderWhoseNameEndsInADotOrASpace()
    {
        string[] folders = [Path.Combine(_temp.Path, "rpcs3."), Path.Combine(_temp.Path, "cemu ")];

        Assert.True(new EmulatorFolderStore(_environment).Save(folders, out var stored));

        Assert.Equal(folders, stored);
        Assert.Equal(folders, new EmulatorFolderStore(_environment).Load());
    }

    [Fact]
    public void KeepsFullPathsOnceEachInTheOrderGiven()
    {
        var first = Path.Combine(_temp.Path, "rpcs3");
        var second = Path.Combine(_temp.Path, "cemu");
        var store = new EmulatorFolderStore(_environment);

        Assert.True(store.Save([first, "relative\\folder", " ", second + Path.DirectorySeparatorChar, first.ToUpperInvariant()], out var stored));

        Assert.Equal([first, second], stored);
        Assert.Equal([first, second], new EmulatorFolderStore(_environment).Load());
    }

    [Fact]
    public void ACorruptFileDeclaresNothing()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Deguffer")).FullName;
        File.WriteAllText(Path.Combine(directory, "emulator-folders.json"), "{ not json");

        Assert.Empty(new EmulatorFolderStore(_environment).Load());
    }
}
