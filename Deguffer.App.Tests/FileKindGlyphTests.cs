using Deguffer.App.ViewModels;

namespace Deguffer.App.Tests;

/// <summary>A group's glyph, by its file's extension alone, whatever its case and wherever the file is.</summary>
public sealed class FileKindGlyphTests
{
    [Theory]
    [InlineData("IMG_0001.JPG", "photo.jpg")]
    [InlineData(@"C:\Users\testuser\Videos\clip.MP4", "film.mkv")]
    [InlineData("song.flac", "other.mp3")]
    [InlineData("report.pdf", "notes.docx")]
    [InlineData("backup.7z", "archive.zip")]
    [InlineData("setup.exe", "library.dll")]
    public void FilesOfOneKindShareAGlyph(string name, string sameKind) =>
        Assert.Equal(FileKindGlyph.Of(sameKind), FileKindGlyph.Of(name));

    [Fact]
    public void EachKindHasItsOwnGlyph()
    {
        string[] kinds = ["a.jpg", "a.mp4", "a.mp3", "a.pdf", "a.zip", "a.cs", "a.exe", "a.ttf", "a.unknown"];

        Assert.Equal(kinds.Length, kinds.Select(FileKindGlyph.Of).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("README")]
    [InlineData("archive.")]
    [InlineData(".gitignore")]
    [InlineData("data.bin")]
    public void AFileOfNoKnownKindHasTheOtherGlyph(string name) =>
        Assert.Equal(FileKindGlyph.Other, FileKindGlyph.Of(name));
}
