using System.Runtime.InteropServices;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The link-aware measurement, proved against real hard links rather than inferred. G8 makes that
/// distinction the point: a hard link costs nothing to create unelevated, so the exact fixture the
/// claim rests on — one file whose blocks a "project" outside the tree also holds — is built here
/// and observed, on every run, at whatever privilege the test host has.
/// </summary>
public sealed class HardLinkAwareScannerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static async Task<ScanSize> MeasureAsync(string path)
    {
        var result = await HardLinkAwareScanner.Default.MeasureAsync(path);
        return result.Size;
    }

    /// <summary>The store→node_modules relationship, reduced to one linked file.</summary>
    private string LinkOut(string storeFile, params string[] linkSegments)
    {
        var link = Path.Combine([_temp.Path, .. linkSegments]);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        Assert.True(
            CreateHardLink(link, storeFile, securityAttributes: 0),
            $"CreateHardLink failed with error {Marshal.GetLastWin32Error()}.");

        return link;
    }

    [Fact]
    public async Task CountsAFileWhoseOnlyLinkIsInTheTree()
    {
        var store = _temp.CreateDirectory("store");
        _temp.CreateFile(4096, "store", "files", "ab", "sole.bin");

        var size = await MeasureAsync(store);

        Assert.Equal(4096, size.Logical);

        // Allocated is the volume's real, cluster-rounded figure here rather than a copy of
        // the length, so it is asserted as a relationship: a 64 KB-cluster volume would make
        // an exact expectation a claim about the disk the test happens to run on.
        Assert.True(size.Allocated >= size.Logical);
    }

    /// <summary>
    /// The number this scanner exists for. Summing lengths would report the shared file's bytes as
    /// reclaimable, and the disk would give back only the sole file's — the §5.4 over-report the
    /// pnpm and conda survey entries both describe.
    /// </summary>
    [Fact]
    public async Task ExcludesAFileHardLinkedFromOutsideTheTree()
    {
        var store = _temp.CreateDirectory("store");
        _temp.CreateFile(4096, "store", "sole.bin");
        var shared = _temp.CreateFile(65536, "store", "shared.bin");
        LinkOut(shared, "project", "node_modules", "linked.bin");

        var size = await MeasureAsync(store);

        Assert.Equal(4096, size.Logical);
    }

    /// <summary>
    /// Documents the deliberate cheap reading: a file linked twice inside the tree is excluded
    /// too, though deleting the whole tree would free it. Under-reporting the rare case is the
    /// direction §5.4 allows; over-reporting any case is not.
    /// </summary>
    [Fact]
    public async Task AFileLinkedTwiceWithinTheTreeIsAlsoExcluded()
    {
        var store = _temp.CreateDirectory("store");
        var original = _temp.CreateFile(8192, "store", "a", "content.bin");
        LinkOut(original, "store", "b", "content.bin");

        var size = await MeasureAsync(store);

        Assert.Equal(0, size.Logical);
    }

    /// <summary>
    /// A sole-link sum is a prediction — installs and removals move link counts under it — so the
    /// result must never present itself as precise, however exactly its bytes were read.
    /// </summary>
    [Fact]
    public async Task EveryMeasurementIsMarkedApproximate()
    {
        var store = _temp.CreateDirectory("store");
        _temp.CreateFile(4096, "store", "sole.bin");

        Assert.True((await MeasureAsync(store)).IsApproximate);
        Assert.True((await MeasureAsync(Path.Combine(_temp.Path, "absent"))).IsApproximate);
    }

    /// <summary>
    /// §6.3: a MAX_PATH truncation is a silent under-count here — the deep file would simply not
    /// be reached, and nothing downstream could tell.
    ///
    /// <para><b>What this establishes, and what it cannot.</b> It proves the walk reaches past
    /// MAX_PATH on the machine it runs on. It does not prove this scanner applied the prefix
    /// itself, for the reason <see cref="Safety.IFileSystem"/> already records: .NET prepends
    /// <c>\\?\</c> to any path of 260 characters or more before calling Win32, and a host that
    /// is long-path aware — which every .NET 10 test host is — carries a bare path through as
    /// well. Deleting the prefixing from this scanner was tried, and this test stayed green.
    /// The exposure is real but is not observable from an outcome: the per-file query here goes
    /// through <c>CreateFileW</c> directly, which does no normalising of its own, so a machine
    /// without long-path support would skip the deep file and under-count. Making that assertable
    /// means asserting on the <em>form</em> of each path handed to Win32, which is the seam
    /// <see cref="Safety.IFileSystem"/> exists to provide for deletion and which scanning does not
    /// have.</para>
    /// </summary>
    [Fact]
    public async Task MeasuresAStoreDeeperThanMaxPath()
    {
        var deep = _temp.CreateDirectory("store");
        while (deep.Length <= 260)
        {
            deep = Path.Combine(deep, new string('p', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "payload.bin")), new byte[4096]);

        var size = await MeasureAsync(Path.Combine(_temp.Path, "store"));

        Assert.Equal(4096, size.Logical);
    }

    /// <summary>
    /// A junction's target keeps its own links and was never classified as this tree's, so the
    /// walk must not follow it — the same rule every other scanner applies.
    /// </summary>
    [Fact]
    public async Task DoesNotFollowAJunctionInsideTheTree()
    {
        var store = _temp.CreateDirectory("store");
        _temp.CreateFile(4096, "store", "sole.bin");
        _temp.CreateFile(65536, "outside", "big.bin");
        Directory.CreateSymbolicLink(Path.Combine(store, "linked"), Path.Combine(_temp.Path, "outside"));

        var size = await MeasureAsync(store);

        Assert.Equal(4096, size.Logical);
    }

    [Fact]
    public async Task AnAbsentPathMeasuresZero()
    {
        var size = await MeasureAsync(Path.Combine(_temp.Path, "never-created"));

        Assert.Equal(0, size.Logical);
        Assert.Equal(0, size.Allocated);
    }

    /// <summary>
    /// A single named file is a legitimate subject, mirroring the fallback walk — and the same
    /// link rule applies to it: multi-linked, its bytes are not this path's to promise.
    /// </summary>
    [Fact]
    public async Task ASingleFileIsMeasuredByTheSameLinkRule()
    {
        var sole = _temp.CreateFile(4096, "loose", "sole.bin");
        var shared = _temp.CreateFile(65536, "loose", "shared.bin");
        LinkOut(shared, "elsewhere", "link.bin");

        Assert.Equal(4096, (await MeasureAsync(sole)).Logical);
        Assert.Equal(0, (await MeasureAsync(shared)).Logical);
    }

    /// <summary>
    /// Elevating changes nothing about this number — one route serves both privilege levels — so
    /// the result must never carry a fallback reason that would make the UI offer elevation.
    /// </summary>
    [Fact]
    public async Task NeverOffersElevationBecauseTheAnswerWouldNotChange()
    {
        var store = _temp.CreateDirectory("store");
        _temp.CreateFile(4096, "store", "sole.bin");

        var result = await HardLinkAwareScanner.Default.MeasureAsync(store);

        Assert.Equal(FallbackReason.None, result.Fallback);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
}
