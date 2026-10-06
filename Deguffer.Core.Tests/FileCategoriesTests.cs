using Deguffer.Core.Exploring.Files;

namespace Deguffer.Core.Tests;

/// <summary>What kind of file a name says it is, by the extension and nothing else.</summary>
public sealed class FileCategoriesTests
{
    [Theory]
    [InlineData("holiday.mp4", FileCategory.Video)]
    [InlineData("stream.ts", FileCategory.Video)]
    [InlineData("song.flac", FileCategory.Audio)]
    [InlineData("photo.heic", FileCategory.Images)]
    [InlineData("report.pdf", FileCategory.Documents)]
    [InlineData("backup.7z", FileCategory.Archives)]
    [InlineData("windows.iso", FileCategory.DiskImages)]
    [InlineData("card.img", FileCategory.DiskImages)]
    [InlineData("ubuntu.vhdx", FileCategory.VirtualMachineDisks)]
    [InlineData("guest.vmdk", FileCategory.VirtualMachineDisks)]
    [InlineData("guest.vdi", FileCategory.VirtualMachineDisks)]
    [InlineData("product.msi", FileCategory.Installers)]
    [InlineData("hotfix.msp", FileCategory.Installers)]
    [InlineData("App.pdb", FileCategory.CodeAndBuildOutput)]
    [InlineData("Program.cs", FileCategory.CodeAndBuildOutput)]
    [InlineData("pagefile.sys", FileCategory.Other)]
    public void AKnownExtensionNamesItsCategory(string name, FileCategory expected) =>
        Assert.Equal(expected, FileCategories.Of(name));

    [Theory]
    [InlineData("HOLIDAY.MP4")]
    [InlineData("Holiday.Mp4")]
    [InlineData("holiday.mp4")]
    public void TheCaseOfTheExtensionDoesNotChangeTheCategory(string name) =>
        Assert.Equal(FileCategory.Video, FileCategories.Of(name));

    /// <summary>
    /// Only the last extension counts. A video named as one but saved as a text file is a text file,
    /// and a name ending in a dot has no extension at all.
    /// </summary>
    [Theory]
    [InlineData("README", FileCategory.Other)]
    [InlineData("archive.zip.txt", FileCategory.Documents)]
    [InlineData("holiday.mp4.part", FileCategory.Other)]
    [InlineData("trailing.", FileCategory.Other)]
    [InlineData(".gitignore", FileCategory.Other)]
    public void ANameWithNoKnownExtensionIsOther(string name, FileCategory expected) =>
        Assert.Equal(expected, FileCategories.Of(name));

    /// <summary>
    /// A program and its installer share an extension, so an executable is an installer only where
    /// its name says it is one, and an ordinary program is described as every other file is.
    /// </summary>
    [Theory]
    [InlineData("VSCodeUserSetup-x64.exe", FileCategory.Installers)]
    [InlineData("INSTALL.EXE", FileCategory.Installers)]
    [InlineData("vs_installer.exe", FileCategory.Installers)]
    [InlineData("notepad.exe", FileCategory.Other)]
    [InlineData("setup.dat", FileCategory.Other)]
    [InlineData("setup", FileCategory.Other)]
    public void AnExecutableIsAnInstallerOnlyWhereItsNameSaysSo(string name, FileCategory expected) =>
        Assert.Equal(expected, FileCategories.Of(name));

    [Fact]
    public void EveryCategoryHasALabel() =>
        Assert.All(FileCategories.All, category => Assert.False(string.IsNullOrWhiteSpace(FileCategories.Label(category))));
}
