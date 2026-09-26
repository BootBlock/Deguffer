using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// <c>retroarch.cfg</c> is read by RetroArch's own grammar, and every folder the RetroArch rows look in
/// is a value in it, so a reading that disagrees with RetroArch's looks in the wrong folder. Each case
/// here is a rule of <c>config_file.c</c> an INI reader would get wrong.
/// </summary>
public sealed class RetroArchSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Write(string name, params string[] lines)
    {
        var path = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private RetroArchSettings Read(params string[] lines) =>
        RetroArchSettings.Read(Write("retroarch.cfg", lines))!;

    [Fact]
    public void ReadsAQuotedValueWholeAndAnUnquotedOneToItsFirstSpace()
    {
        var settings = Read(
            "thumbnails_directory = \"D:\\Retro Art\\thumbnails\"",
            "video_shader_dir = D:\\Retro Art\\shaders");

        Assert.Equal(@"D:\Retro Art\thumbnails", settings["thumbnails_directory"]);
        Assert.Equal(@"D:\Retro", settings["video_shader_dir"]);
    }

    [Fact]
    public void AKeyWithNoSpaceBeforeItsEqualsSignIsNotAnEntry()
    {
        var settings = Read("thumbnails_directory=\"D:\\art\"");

        Assert.Null(settings["thumbnails_directory"]);
    }

    [Fact]
    public void TheFirstEntryForAKeyWins()
    {
        var settings = Read(
            "thumbnails_directory = \"D:\\first\"",
            "thumbnails_directory = \"D:\\second\"");

        Assert.Equal(@"D:\first", settings["thumbnails_directory"]);
    }

    [Fact]
    public void KeysAreCaseSensitive()
    {
        var settings = Read("Thumbnails_Directory = \"D:\\art\"");

        Assert.Null(settings["thumbnails_directory"]);
    }

    [Fact]
    public void AHashCutsTheLineUnlessItIsInsideTheQuotes()
    {
        var settings = Read(
            "# thumbnails_directory = \"D:\\commented\"",
            "video_shader_dir = \"D:\\sets#1\"",
            "content_database_path = D:\\db#comment");

        Assert.Null(settings["thumbnails_directory"]);
        Assert.Equal(@"D:\sets#1", settings["video_shader_dir"]);
        Assert.Equal(@"D:\db", settings["content_database_path"]);
    }

    [Fact]
    public void ACarriageReturnEndsAnUnquotedValue()
    {
        var settings = Read("content_database_path = D:\\db\r", "video_shader_dir = \"D:\\shaders\"\r");

        Assert.Equal(@"D:\db", settings["content_database_path"]);
        Assert.Equal(@"D:\shaders", settings["video_shader_dir"]);
    }

    /// <summary>
    /// An included file's entries stand where its line stands: a key above the line beats it, and it
    /// beats a key below. A relative include is relative to the file that includes it.
    /// </summary>
    [Fact]
    public void AnIncludeStandsWhereItsLineStands()
    {
        Write(Path.Combine("more", "paths.cfg"),
            "thumbnails_directory = \"D:\\included\"",
            "video_shader_dir = \"D:\\included\"");

        var settings = Read(
            "thumbnails_directory = \"D:\\above\"",
            "#include \"more\\paths.cfg\"",
            "video_shader_dir = \"D:\\below\"");

        Assert.Equal(@"D:\above", settings["thumbnails_directory"]);
        Assert.Equal(@"D:\included", settings["video_shader_dir"]);
        Assert.Empty(settings.UnreadIncludes);
    }

    [Fact]
    public void AMissingIncludeIsSkipped()
    {
        var settings = Read("#include \"missing.cfg\"", "video_shader_dir = \"D:\\shaders\"");

        Assert.Equal(@"D:\shaders", settings["video_shader_dir"]);
        Assert.Empty(settings.UnreadIncludes);
    }

    /// <summary>RetroArch loads sixteen nested levels below the file itself, and no more.</summary>
    [Fact]
    public void IncludesNestNoDeeperThanRetroArchAllows()
    {
        for (var level = 1; level <= 17; level++)
        {
            Write($"level{level}.cfg", $"#include \"level{level + 1}.cfg\"", $"key{level} = \"{level}\"");
        }

        var settings = Read("#include \"level1.cfg\"");

        Assert.Equal("16", settings["key16"]);
        Assert.Null(settings["key17"]);
    }

    [Fact]
    public void AnIncludeThatCannotBeReadIsReported()
    {
        var locked = Write("locked.cfg", "video_shader_dir = \"D:\\hidden\"");
        using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var settings = Read("#include \"locked.cfg\"");

        Assert.Equal([locked], settings.UnreadIncludes);
    }

    [Fact]
    public void AFileLargerThanRetroArchWritesIsNotRead()
    {
        var path = Path.Combine(_temp.Path, "retroarch.cfg");
        File.WriteAllBytes(path, new byte[(1024 * 1024) + 1]);

        Assert.Null(RetroArchSettings.Read(path));
    }
}
