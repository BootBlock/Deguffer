using System.Runtime.InteropServices;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// A volume's read priority reaches its handle, and keeps reaching it once the handle has passed to
/// the source reading the table. An ordinary file opened as a volume is opened carries the same hint,
/// so the handle is tested without administrator rights.
/// </summary>
public sealed class VolumeReadPriorityTests
{
    [Theory]
    [InlineData(ReadPriority.Low, IoPriorityHintLow)]
    [InlineData(ReadPriority.Normal, IoPriorityHintNormal)]
    public void AHandleCarriesThePriorityItIsGiven(ReadPriority priority, int hint)
    {
        var path = Path.Combine(Path.GetTempPath(), $"deguffer-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[4096]);

        try
        {
            var handle = VolumeMftSource.OpenOverlapped(path);
            using var volume = new VolumeHandle(handle);

            // Set to the other value first, so a call that changed nothing cannot pass.
            volume.Prioritise(priority is ReadPriority.Low ? ReadPriority.Normal : ReadPriority.Low);
            volume.Prioritise(priority);

            Assert.Equal(hint, HintOf(handle));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A build that has finished closes its handle, and a caller raising it afterwards is not an error.</summary>
    [Fact]
    public void AClosedHandleIgnoresAPriority()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deguffer-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[4096]);

        try
        {
            var volume = new VolumeHandle(VolumeMftSource.OpenOverlapped(path));
            volume.Dispose();

            volume.Prioritise(ReadPriority.Normal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The priority is raised while the source is reading, after the opened volume has handed its handle
    /// over, so it goes to the handle rather than to whichever object owns it at the time.
    /// </summary>
    [Fact]
    public void AnOpenedVolumePrioritisesTheHandleItHandedToTheSource()
    {
        var raw = new MftFixture()
            .AddDirectory(6, MftRecord.RootRecordNumber, "Users")
            .BuildVolume(bytesPerSector: 4096, bytesPerCluster: 4096);
        var opened = VolumeMftSource.TryOpen(raw, out _)!;

        opened.Prioritise(ReadPriority.Low);
        using var source = opened.TryReadTable(out _)!;
        opened.Prioritise(ReadPriority.Normal);

        Assert.Equal([ReadPriority.Low, ReadPriority.Normal], raw.Priorities);
    }

    private const int IoPriorityHintLow = 1;
    private const int IoPriorityHintNormal = 2;
    private const int FileIoPriorityHintInformation = 43;

    private static int HintOf(SafeFileHandle handle)
    {
        var hint = -1;
        var status = NtQueryInformationFile(handle, out _, ref hint, sizeof(int), FileIoPriorityHintInformation);

        Assert.Equal(0, status);
        return hint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status, ref int information, int length, int informationClass);
}
