using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;
using Xunit.Sdk;

namespace Deguffer.Core.Tests;

/// <summary>
/// The link fixtures refuse a link a test could not follow, and say why, before any test builds on
/// it. Each makes the kind of link it names, and every link the suite makes goes through them.
/// </summary>
public sealed partial class LinkFixtureTests : IDisposable
{
    private const uint SymbolicLinkTag = 0xA000_000C;
    private const uint MountPointTag = 0xA000_0003;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void ADirectoryLinkThatResolvesLeadsToItsTarget(DirectoryLinkKind kind)
    {
        var target = _temp.CreateDirectory("target");
        File.WriteAllBytes(Path.Combine(target, "child.bin"), new byte[16]);
        var link = Path.Combine(_temp.Path, "link");

        DirectoryLink.Create(kind, link, target);

        Assert.Equal(16, new FileInfo(Path.Combine(link, "child.bin")).Length);
    }

    /// <summary>
    /// A theory over the kinds proves nothing unless each row makes the kind it names, and a junction
    /// is told from a symbolic link only by its tag. The attributes are the same for both, which is
    /// why a test of one stood for both for so long.
    /// </summary>
    [Theory]
    [InlineData(DirectoryLinkKind.SymbolicLink, SymbolicLinkTag)]
    [InlineData(DirectoryLinkKind.Junction, MountPointTag)]
    public void EachKindOfDirectoryLinkCarriesItsOwnTag(DirectoryLinkKind kind, uint tag)
    {
        var link = Path.Combine(_temp.Path, "link");

        DirectoryLink.Create(kind, link, _temp.CreateDirectory("target"));

        Assert.Equal(tag, ReparseTag(link));
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint | FileAttributes.Directory));
    }

    /// <summary>
    /// A junction holds two names, as <c>mklink /J</c> writes them: the <c>\??\</c> form Windows
    /// follows, and the plain path a listing shows. Following the link reads only the first, so a
    /// fixture that wrote the second wrongly would still resolve, and this reads both back.
    /// </summary>
    [Fact]
    public void AJunctionHoldsTheNameWindowsFollowsAndTheNameAListingShows()
    {
        var target = _temp.CreateDirectory("target");
        var link = Path.Combine(_temp.Path, "link");

        Junction.ToDirectory(link, target);

        var (substitute, print) = MountPointNames(link);
        Assert.Equal(@"\??\" + target, substitute, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(target, print, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows makes a junction of any empty folder it is handed, so a fixture that reused one would
    /// replace a folder the test had made for something else, and its clean-up would then delete it.
    /// </summary>
    [Fact]
    public void AJunctionIsNeverMadeOverAFolderThatIsAlreadyThere()
    {
        var existing = _temp.CreateDirectory("existing");

        Assert.Throws<IOException>(() => Junction.ToDirectory(existing, _temp.CreateDirectory("target")));

        Assert.True(Directory.Exists(existing));
        Assert.False(File.GetAttributes(existing).HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void AFileLinkThatResolvesLeadsToItsTarget()
    {
        var target = _temp.CreateFile(16, "target.bin");
        var link = Path.Combine(_temp.Path, "link.bin");

        SymbolicLink.ToFile(link, target);

        Assert.Equal(16, File.ReadAllBytes(link).Length);
    }

    /// <summary>
    /// A link to itself is a refusal every machine produces, and the probe raises it the way it
    /// raises an untrusted mount point: as an <see cref="IOException"/> from following the link.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void ALinkTheSystemWillNotFollowFailsQuotingTheSystemsOwnMessage(DirectoryLinkKind kind)
    {
        var link = Path.Combine(_temp.Path, "loop");

        var failure = Assert.Throws<InvalidOperationException>(() => DirectoryLink.Create(kind, link, link));

        var refusal = Assert.IsType<IOException>(failure.InnerException);
        Assert.Contains(refusal.Message, failure.Message, StringComparison.Ordinal);
        Assert.Contains(link, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void ADirectoryLinkToNothingFailsNamingWhereItLeads(DirectoryLinkKind kind)
    {
        var target = Path.Combine(_temp.Path, "missing");

        var failure = Assert.Throws<TrueException>(
            () => DirectoryLink.Create(kind, Path.Combine(_temp.Path, "link"), target));

        Assert.Contains(target, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileLinkToNothingFailsNamingWhereItLeads()
    {
        var target = Path.Combine(_temp.Path, "missing.bin");

        var failure = Assert.Throws<TrueException>(
            () => SymbolicLink.ToFile(Path.Combine(_temp.Path, "link.bin"), target));

        Assert.Contains(target, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fixtures only help where they are used, and a link made directly fails the old way: a bare
    /// assertion against production code that is correct. Each way of making one is matched as code,
    /// not as a word, so a comment can still say <c>mklink</c>. The fixtures' own files are expected to
    /// match, which also proves the sweep still reads the source, and so is this one, for the samples
    /// that prove the pattern.
    ///
    /// <para>Every project that holds tests or their fixtures is swept, since a link made in one of
    /// the App's tests fails the same way.</para>
    /// </summary>
    [Fact]
    public void NoTestCreatesALinkWithoutTheFixtures()
    {
        string[] projects = ["Deguffer.Core.Tests", "Deguffer.App.Tests", "Deguffer.Testing"];

        var creating = projects
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(MarkdownGuide.RepositoryRoot, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => LinkCreation().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(MarkdownGuide.RepositoryRoot, file))
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                Path.Combine("Deguffer.Core.Tests", "LinkFixtureTests.cs"),
                Path.Combine("Deguffer.Testing", "Junction.cs"),
                Path.Combine("Deguffer.Testing", "SymbolicLink.cs"),
            ],
            creating);
    }

    [Theory]
    [InlineData("Directory.CreateSymbolicLink(link, target);", true)]
    [InlineData("new FileInfo(link).CreateAsSymbolicLink (target);", true)]
    [InlineData("private const uint SetReparsePoint = 0x000900A4;", true)]
    [InlineData("private const uint SetReparsePoint = 0x0009_00A4;", true)]
    [InlineData("DeviceIoControl(handle, 0x900a4, buffer, length, 0, 0, out _, 0);", true)]
    [InlineData("Process.Start(\"cmd.exe\", $\"/c mklink /J {link} {target}\");", true)]
    [InlineData("Process.Start(\"cmd.exe\", \"/c mklink /d a b\");", true)]
    [InlineData("Run(\"New-Item -ItemType Junction -Path a -Target b\");", true)]
    [InlineData("Run(\"New-Item -ItemType 'SymbolicLink' -Path a -Target b\");", true)]
    [InlineData("/// <summary>What <c>mklink /J</c> makes.</summary>", false)]
    [InlineData("// a cache somebody had just relocated with mklink", false)]
    [InlineData("Process.Start(\"cmd.exe\", \"/c mklink /H a b\");", false)]
    public void TheSweepMatchesEveryWayOfMakingALinkAndNoMentionOfOne(string source, bool creates)
    {
        Assert.Equal(creates, LinkCreation().IsMatch(source));
    }

    /// <summary>
    /// The .NET calls, <c>FSCTL_SET_REPARSE_POINT</c> by its value in any spelling, and, inside a
    /// string a process would be started with, <c>mklink</c> with a switch that makes a folder link
    /// and PowerShell's <c>New-Item -ItemType</c>.
    /// </summary>
    [GeneratedRegex(
        @"\bCreateSymbolicLink\s*\(|\bCreateAsSymbolicLink\s*\(|\b0x0*9_?00_?A4\b|""[^""\r\n]*\bmklink\s+/[DJ]\b|""[^""\r\n]*-ItemType\W+(Junction|SymbolicLink)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex LinkCreation();

    private static uint ReparseTag(string path)
    {
        using var handle = CreateFile(
            LongPath.Extended(path), ReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);

        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var info, 8))
        {
            throw new IOException($"Could not read the reparse tag of {path}.", Marshal.GetHRForLastWin32Error());
        }

        return info.ReparseTag;
    }

    /// <summary>The two names in a mount point's <c>REPARSE_DATA_BUFFER</c>, read as Windows holds them.</summary>
    private static (string Substitute, string Print) MountPointNames(string path)
    {
        var buffer = new byte[MaximumReparseDataBytes];

        using var handle = CreateFile(
            LongPath.Extended(path), ReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);

        if (handle.IsInvalid || !DeviceIoControl(handle, FsctlGetReparsePoint, 0, 0, buffer, buffer.Length, out _, 0))
        {
            throw new IOException($"Could not read the reparse data of {path}.", Marshal.GetHRForLastWin32Error());
        }

        Assert.Equal(MountPointTag, BinaryPrimitives.ReadUInt32LittleEndian(buffer));

        return (Name(8), Name(12));

        // Each name is an offset and a length into the path buffer, which follows the eight-byte
        // header and the four eight-byte fields.
        string Name(int field) => Encoding.Unicode.GetString(
            buffer,
            16 + BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(field)),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(field + 2)));
    }

    private const uint FsctlGetReparsePoint = 0x0009_00A8;
    private const int MaximumReparseDataBytes = 16 * 1024;
    private const uint ReadAttributes = 0x0080;
    private const uint ShareAll = 0x0007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const int FileAttributeTagInfo = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file, int informationClass, out AttributeTagInfo information, int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        nint inBuffer,
        int inBufferSize,
        byte[] outBuffer,
        int outBufferSize,
        out int bytesReturned,
        nint overlapped);
}
