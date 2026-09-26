using Deguffer.Core.Configuration;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

public sealed class EmulatorFolderStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public EmulatorFolderStoreTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void NothingIsDeclaredBeforeAnythingIsSaved() =>
        Assert.Empty(new EmulatorFolderStore(_environment).Load());

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
