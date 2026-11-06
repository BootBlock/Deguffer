using System.Text;
using Deguffer.Core.InstalledApps;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Listing the backup folder reads each file once, and again only once its length or last write
/// has changed. A file held open with no sharing cannot be read, so a listing that still shows one
/// proves it was not opened, and one that shows its new content proves it was.
/// </summary>
public sealed class RegistryBackupListingTests : IDisposable
{
    private const string RegExe = @"C:\Windows\System32\reg.exe";

    private static readonly DateTime Written = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly TempDirectory _temp = new();

    private readonly RegistryBackups _backups;

    public RegistryBackupListingTests() =>
        _backups = new RegistryBackups(new FakeProcessRunner(), Folder, RegExe, TimeProvider.System);

    public void Dispose() => _temp.Dispose();

    private string Folder => Path.Combine(_temp.Path, "backups");

    private static string KeyOf(string name) => new UninstallKey(UninstallScope.CurrentUser, name).PhysicalPath;

    /// <summary>Write a backup of <paramref name="name"/>'s entry, last written at <paramref name="written"/>.</summary>
    private string Write(string file, string name, DateTime written)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, file);
        File.WriteAllText(path, $"Windows Registry Editor Version 5.00\r\n\r\n[{KeyOf(name)}]\r\n\"DisplayName\"=\"{name}\"\r\n", Encoding.Unicode);
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    private static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    private IReadOnlyList<RegistryBackupFile> List() => _backups.List(CancellationToken.None);

    [Fact]
    public void AFileListedBeforeIsNotOpenedAgainWhileItIsUnchanged()
    {
        var path = Write("tool.reg", "Tool", Written);
        var first = Assert.Single(List());

        using (Hold(path))
        {
            Assert.Same(first, Assert.Single(List()));
        }

        Assert.Equal(path, first.Path);
        Assert.Equal(KeyOf("Tool"), first.KeyPath);
    }

    [Fact]
    public void AFileRewrittenAtTheSameLengthIsReadAgain()
    {
        var path = Write("tool.reg", "ToolA", Written);
        Assert.Equal(KeyOf("ToolA"), Assert.Single(List()).KeyPath);

        Write("tool.reg", "ToolB", Written.AddMinutes(1));

        Assert.Equal(KeyOf("ToolB"), Assert.Single(List()).KeyPath);
        Assert.Equal(path, Assert.Single(List()).Path);
    }

    [Fact]
    public void AFileRewrittenWithTheSameLastWriteIsReadAgain()
    {
        Write("tool.reg", "Tool", Written);
        Assert.Single(List());

        Write("tool.reg", "Tool Pro Edition", Written);

        Assert.Equal(KeyOf("Tool Pro Edition"), Assert.Single(List()).KeyPath);
    }

    [Fact]
    public void ABackupAddedBetweenListingsIsListedAndOneDeletedIsDropped()
    {
        var gone = Write("a.reg", "A", Written);
        Assert.Single(List());

        File.Delete(gone);
        Write("b.reg", "B", Written.AddMinutes(1));
        Write("c.reg", "C", Written.AddMinutes(2));

        Assert.Equal([KeyOf("C"), KeyOf("B")], List().Select(b => b.KeyPath));
    }

    /// <summary>
    /// A file still held by the export writing it, or by a scanner, is read by the next listing,
    /// rather than remembered as no backup and hidden until it next changes.
    /// </summary>
    [Fact]
    public void AFileThatCouldNotBeReadIsReadAgainByTheNextListing()
    {
        var path = Write("tool.reg", "Tool", Written);

        using (Hold(path))
        {
            Assert.Empty(List());
        }

        Assert.Equal(KeyOf("Tool"), Assert.Single(List()).KeyPath);
    }

    [Fact]
    public void AnAbandonedListingStops()
    {
        Write("tool.reg", "Tool", Written);

        Assert.Throws<OperationCanceledException>(() => _backups.List(new CancellationToken(canceled: true)));
    }
}
