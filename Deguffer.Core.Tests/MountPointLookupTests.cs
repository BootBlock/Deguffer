using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where the volume holding a path is mounted, answered from the path's own folders where they prove
/// it and by Windows everywhere else (#512). Each case that is not proved has to reach Windows and
/// come back with its answer unchanged, because the refusal of a volume's top, the standing-folder
/// guard and the in-use check all read it.
/// </summary>
public class MountPointLookupTests
{
    private const string Deep = @"C:\Users\testuser\source\repos\app\src\web\bin\Debug";

    /// <summary>What a fake Windows answers, distinct from every root, so a test sees which one answered.</summary>
    private const string WindowsAnswer = @"C:\Mounted\";

    private const string LocalVolume = @"\Device\HarddiskVolume3";

    [Fact]
    public void AnswersAPlainDeepPathWithItsRootAndNoCallToWindows()
    {
        var disk = new FakeDisk().Folders(Deep);

        Assert.Equal(@"C:\", disk.Lookup.MountPointOf(Deep));
        Assert.Empty(disk.AskedWindows);
    }

    /// <summary>
    /// §6.3: every read is a prefix of the path in <c>\\?\</c> form, from the root down, one for each
    /// folder and the root.
    /// </summary>
    [Fact]
    public void ReadsEveryFolderFromTheRootDownInExtendedForm()
    {
        var disk = new FakeDisk().Folders(Deep);

        disk.Lookup.MountPointOf(Deep);

        Assert.Equal(
            [
                @"\\?\C:\",
                @"\\?\C:\Users",
                @"\\?\C:\Users\testuser",
                @"\\?\C:\Users\testuser\source",
                @"\\?\C:\Users\testuser\source\repos",
                @"\\?\C:\Users\testuser\source\repos\app",
                @"\\?\C:\Users\testuser\source\repos\app\src",
                @"\\?\C:\Users\testuser\source\repos\app\src\web",
                @"\\?\C:\Users\testuser\source\repos\app\src\web\bin",
                @"\\?\C:\Users\testuser\source\repos\app\src\web\bin\Debug",
            ],
            disk.Read);
    }

    /// <summary>A path longer than <c>MAX_PATH</c> is read whole, never cut at 260.</summary>
    [Fact]
    public void ReadsAPathLongerThanMaxPathWhole()
    {
        var deep = Path.Join(@"C:\Users\testuser", string.Join('\\', Enumerable.Repeat(new string('n', 40), 10)));
        var disk = new FakeDisk().Folders(deep);

        Assert.Equal(@"C:\", disk.Lookup.MountPointOf(deep));
        Assert.Equal(@"\\?\" + deep, disk.Read[^1]);
    }

    [Fact]
    public void KeepsTheCaseTheRootWasAskedIn()
    {
        var disk = new FakeDisk().Folders(@"c:\users\testuser");

        Assert.Equal(@"c:\", disk.Lookup.MountPointOf(@"c:\users\testuser"));
    }

    [Fact]
    public void AnswersARootItIsAskedAboutAfterReadingIt()
    {
        var disk = new FakeDisk().Folders(@"C:\");

        Assert.Equal(@"C:\", disk.Lookup.MountPointOf(@"C:\"));
        Assert.Equal([@"\\?\C:\"], disk.Read);
        Assert.Empty(disk.AskedWindows);
    }

    /// <summary>
    /// A file is no reparse point either, so the file the duplicate search asks about is answered by
    /// its folders.
    /// </summary>
    [Fact]
    public void AnswersAPlainFileWithItsRoot()
    {
        var file = Deep + @"\app.dll";
        var disk = new FakeDisk().Folders(Deep).Entry(file, FileAttributes.Archive);

        Assert.Equal(@"C:\", disk.Lookup.MountPointOf(file));
        Assert.Empty(disk.AskedWindows);
    }

    /// <summary>
    /// Nothing below a folder that is not there exists, so nothing below it can be a mount point: what
    /// a run asks about each target it removed.
    /// </summary>
    [Fact]
    public void AnswersAPathWhoseTailIsGoneWithItsRoot()
    {
        var disk = new FakeDisk().Folders(@"C:\Users\testuser\source");

        Assert.Equal(@"C:\", disk.Lookup.MountPointOf(Deep));
        Assert.Empty(disk.AskedWindows);
        Assert.Equal(@"\\?\C:\Users\testuser\source\repos", disk.Read[^1]);
    }

    /// <summary>
    /// A volume mounted at a folder, a junction, a symbolic link and a cloud placeholder are all
    /// reparse points, at any depth, and only Windows can say where the path then leads.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\testuser\source")]
    [InlineData(@"C:\Users\testuser\source\repos\app\src\web\bin")]
    [InlineData(Deep)]
    public void AsksWindowsWhereAnyFolderIsAReparsePoint(string reparse)
    {
        var disk = new FakeDisk().Folders(Deep).Entry(reparse, FileAttributes.Directory | FileAttributes.ReparsePoint);

        Assert.Equal(WindowsAnswer, disk.Lookup.MountPointOf(Deep));
        Assert.Equal([@"\\?\" + Deep], disk.AskedWindows);
        Assert.Equal(@"\\?\" + reparse, disk.Read[^1]);
    }

    /// <summary>A folder gone below a reparse point proves nothing, because the reparse point came first.</summary>
    [Fact]
    public void AsksWindowsWhereTheTailIsGoneBelowAReparsePoint()
    {
        var disk = new FakeDisk()
            .Folders(@"C:\Users\testuser")
            .Entry(@"C:\Users\testuser", FileAttributes.Directory | FileAttributes.ReparsePoint);

        Assert.Equal(WindowsAnswer, disk.Lookup.MountPointOf(Deep));
        Assert.Single(disk.AskedWindows);
    }

    /// <summary>
    /// A level that will not be read may be anything, a mount point included. Access denied, sharing
    /// violation, not ready and an invalid name each go to Windows.
    /// </summary>
    [Theory]
    [InlineData(@"C:\", 21)]
    [InlineData(@"C:\Users", 5)]
    [InlineData(@"C:\Users\testuser\source", 32)]
    [InlineData(Deep, 123)]
    public void AsksWindowsWhereAnyLevelWouldNotBeRead(string refused, int error)
    {
        var disk = new FakeDisk().Folders(Deep).Refuse(refused, error);

        Assert.Equal(WindowsAnswer, disk.Lookup.MountPointOf(Deep));
        Assert.Single(disk.AskedWindows);
    }

    /// <summary>A letter whose volume has gone answers absent at its root, which says nothing about a volume.</summary>
    [Fact]
    public void AsksWindowsWhereTheRootItselfIsAbsent()
    {
        var disk = new FakeDisk();

        Assert.Equal(WindowsAnswer, disk.Lookup.MountPointOf(Deep));
        Assert.Equal([@"\\?\C:\"], disk.Read);
        Assert.Single(disk.AskedWindows);
    }

    /// <summary>
    /// A letter that is not a volume's own: a <c>subst</c>, a mapped share through either redirector,
    /// a letter defined for a folder of a volume, a shadow copy, an optical drive, and a letter not in
    /// use. None is read, because its root is not a volume's top.
    /// </summary>
    [Theory]
    [InlineData(@"\??\C:\Users\testuser")]
    [InlineData(@"\Device\LanmanRedirector\;Z:0000000000012345\server\share")]
    [InlineData(@"\Device\Mup\;LanmanRedirector\;Z:0000000000012345\server\share")]
    [InlineData(@"\Device\HarddiskVolume3\Users\testuser")]
    [InlineData(@"\Device\HarddiskVolumeShadowCopy4")]
    [InlineData(@"\Device\CdRom0")]
    [InlineData(null)]
    public void AsksWindowsWhereTheLetterIsNotALocalVolumesOwn(string? device)
    {
        var disk = new FakeDisk(device).Folders(Deep);

        Assert.Equal(WindowsAnswer, disk.Lookup.MountPointOf(Deep));
        Assert.Single(disk.AskedWindows);
        Assert.Empty(disk.Read);
    }

    [Theory]
    [InlineData(@"\\server\share\cache\app")]
    [InlineData(@"\\?\UNC\server\share\cache\app")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\cache\app")]
    public void AsksWindowsForAShareAndAVolumeNamedByItsGuid(string path)
    {
        var disk = new FakeDisk().Folders(Deep);

        Assert.Equal(WindowsAnswer, disk.Lookup.MountPointOf(path));
        Assert.Single(disk.AskedWindows);
        Assert.Empty(disk.AskedDevices);
        Assert.Empty(disk.Read);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"relative\path")]
    [InlineData(@"C:relative")]
    public void AnswersNullForAPathThatNamesNoVolumeAndAsksNothing(string path)
    {
        var disk = new FakeDisk().Folders(Deep);

        Assert.Null(disk.Lookup.MountPointOf(path));
        Assert.Empty(disk.AskedWindows);
        Assert.Empty(disk.Read);
    }

    /// <summary>
    /// On the real disk: a junction is read as the reparse point it is, through the same attribute
    /// read the app uses, so the path through it goes to Windows, and the answer agrees with Windows
    /// for every path asked.
    /// </summary>
    [Fact]
    public void AgreesWithWindowsOnTheRealDiskAndAsksItThroughAJunction()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateDirectory("target", "inner");
        var link = Path.Join(temp.Path, "link");
        Junction.ToDirectory(link, Path.GetDirectoryName(target)!);

        var asked = new List<string>();
        var lookup = new MountPointLookup(
            FileAttributeRead.Read,
            VolumeCalls.DeviceOf,
            extended =>
            {
                asked.Add(extended);
                return VolumeCalls.VolumePathNameOf(extended);
            });

        string[] paths =
        [
            temp.Path,
            target,
            temp.CreateFile(1, "target", "inner", "file.bin"),
            Path.Join(temp.Path, "gone", "deeper"),
            Path.Join(link, "inner"),
        ];

        foreach (var path in paths)
        {
            Assert.Equal(VolumeCalls.VolumePathNameOf(LongPath.Extended(path)), lookup.MountPointOf(path));
        }

        Assert.Contains(LongPath.Extended(Path.Join(link, "inner")), asked);
    }

    /// <summary>
    /// A disk of folders, files and refusals, keyed by extended path, which records every question
    /// the lookup asks of it.
    /// </summary>
    private sealed class FakeDisk
    {
        private readonly Dictionary<string, (int Error, FileAttributes Attributes)> _entries =
            new(StringComparer.OrdinalIgnoreCase);

        public FakeDisk(string? device = LocalVolume)
        {
            Lookup = new MountPointLookup(
                ReadEntry,
                letter =>
                {
                    AskedDevices.Add(letter);
                    return device;
                },
                extended =>
                {
                    AskedWindows.Add(extended);
                    return WindowsAnswer;
                });
        }

        public MountPointLookup Lookup { get; }

        public List<string> Read { get; } = [];

        public List<string> AskedWindows { get; } = [];

        public List<char> AskedDevices { get; } = [];

        /// <summary>Every folder from the root of <paramref name="path"/> down to it, as plain folders.</summary>
        public FakeDisk Folders(string path)
        {
            for (var folder = path; folder is not null; folder = Path.GetDirectoryName(folder))
            {
                _entries.TryAdd(Key(folder), (0, FileAttributes.Directory));
            }

            return this;
        }

        public FakeDisk Entry(string path, FileAttributes attributes)
        {
            _entries[Key(path)] = (0, attributes);
            return this;
        }

        public FakeDisk Refuse(string path, int error)
        {
            _entries[Key(path)] = (error, default);
            return this;
        }

        private static string Key(string path) =>
            Path.EndsInDirectorySeparator(path) && path.Length > 3 ? Path.TrimEndingDirectorySeparator(path) : path;

        private int ReadEntry(string extendedPath, out FileAttributes attributes)
        {
            Read.Add(extendedPath);

            if (_entries.TryGetValue(LongPath.Display(extendedPath), out var entry))
            {
                attributes = entry.Attributes;
                return entry.Error;
            }

            attributes = default;
            return FileAttributeRead.PathNotFound;
        }
    }
}
