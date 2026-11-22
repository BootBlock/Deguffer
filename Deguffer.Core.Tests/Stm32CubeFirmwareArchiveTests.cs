using System.IO.Compression;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a zip in an STM32Cube repository is found to hold. The entry names are written exactly as given,
/// so a zip made on Windows with <c>\</c> separators, and a top folder whose case differs from the
/// package's, are read as ST's tools would leave them.
/// </summary>
public sealed class Stm32CubeFirmwareArchiveTests : IDisposable
{
    private const string Package = "STM32Cube_FW_F4_V1.27.0";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Zip(params string[] entries)
    {
        var path = Path.Combine(_temp.Path, "stm32cube_fw_f4_v1270.zip");

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var entry in entries)
            {
                zip.CreateEntry(entry);
            }
        }

        return path;
    }

    [Theory]
    [InlineData($"{Package}/", $"{Package}/package.xml", $"{Package}/Drivers/CMSIS/core_cm4.h")]
    [InlineData($"{Package}\\", $"{Package}\\package.xml", $"{Package}\\Drivers\\CMSIS\\core_cm4.h")]
    [InlineData($"{Package}\\package.xml", $"{Package}/Drivers\\CMSIS/core_cm4.h")]
    [InlineData("stm32cube_fw_f4_v1.27.0/package.xml", "STM32CUBE_FW_F4_V1.27.0\\Drivers\\a.c")]
    public void HoldsOnlyThePackageWhateverItsSeparatorsOrCase(params string[] entries) =>
        Assert.Equal(ArchiveContents.OnlyThePackage, Stm32CubeFirmwareArchive.Contents(Zip(entries), Package));

    [Theory]
    [InlineData($"{Package}/package.xml", "Extra/readme.txt")]
    [InlineData($"{Package}\\package.xml", "Extra\\readme.txt")]
    [InlineData($"{Package}/package.xml", "readme.txt")]
    [InlineData($"{Package}/package.xml", "/rooted.txt")]
    [InlineData($"{Package}/package.xml", "\\rooted.txt")]
    [InlineData($"{Package}_extra/package.xml")]
    [InlineData($"{Package}_extra\\package.xml")]
    [InlineData("STM32Cube_FW_F4_V1.27/package.xml")]
    [InlineData($"{Package}")]
    public void HoldsSomethingElseWhereAnyEntryIsOutsideThePackage(params string[] entries) =>
        Assert.Equal(ArchiveContents.SomethingElse, Stm32CubeFirmwareArchive.Contents(Zip(entries), Package));

    [Fact]
    public void AnEmptyZipHoldsSomethingElse() =>
        Assert.Equal(ArchiveContents.SomethingElse, Stm32CubeFirmwareArchive.Contents(Zip(), Package));

    [Fact]
    public void AFileThatIsNotAZipIsUnread()
    {
        var path = Path.Combine(_temp.Path, "stm32cube_fw_f4_v1270.zip");
        File.WriteAllBytes(path, new byte[4096]);

        Assert.Equal(ArchiveContents.Unread, Stm32CubeFirmwareArchive.Contents(path, Package));
    }

    [Fact]
    public void AZipHeldOpenForWritingIsUnread()
    {
        var path = Zip($"{Package}/package.xml");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(ArchiveContents.Unread, Stm32CubeFirmwareArchive.Contents(path, Package));
    }
}
