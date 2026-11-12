using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether a Godot cache folder holds only what the engine writes, entry by entry. One entry in any
/// other shape is somebody else's, and the whole folder is then left alone (§5.2).
/// </summary>
public sealed class GodotCacheContentsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AnImportCacheOfProductsAndTheirRecordsIsRecognised()
    {
        var cache = _temp.CreateDirectory("imported");
        GodotProjectFixture.WriteImportedAsset(cache);
        GodotProjectFixture.WriteImportedAsset(cache, importedBase: "hero-model.glb-0123456789abcdef0123456789abcdef");

        // A second product of one source, and the temporary file the engine writes through.
        File.WriteAllBytes(Path.Combine(cache, $"{GodotProjectFixture.ImportedBase}.s3tc.ctex"), [1]);
        File.WriteAllBytes(Path.Combine(cache, $"{GodotProjectFixture.ImportedBase}.ctex0.tmp"), [1]);

        Assert.Equal(GodotCacheVerdict.Recognised, GodotCacheContents.ImportCache(cache, allowIgnoreFile: false));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("wall.png-0123456789ABCDEF0123456789ABCDEF.ctex")]
    [InlineData("icon.svg-218a8f2b.ctex")]
    [InlineData(".gdignore")]
    public void AnImportCacheHoldingAFileTheImporterDoesNotWriteIsNot(string stranger)
    {
        var cache = _temp.CreateDirectory("imported");
        GodotProjectFixture.WriteImportedAsset(cache);
        File.WriteAllBytes(Path.Combine(cache, stranger), [1]);

        Assert.Equal(GodotCacheVerdict.Unrecognised, GodotCacheContents.ImportCache(cache, allowIgnoreFile: false));
    }

    /// <summary>The importer writes the record once an import finishes, so a product without one is not its output.</summary>
    [Fact]
    public void AnImportedProductWithoutItsRecordIsNotRecognised()
    {
        var cache = _temp.CreateDirectory("imported");
        GodotProjectFixture.WriteImportedAsset(cache);
        File.Delete(Path.Combine(cache, $"{GodotProjectFixture.ImportedBase}.md5"));

        Assert.Equal(GodotCacheVerdict.Unrecognised, GodotCacheContents.ImportCache(cache, allowIgnoreFile: false));
    }

    [Fact]
    public void AnImportCacheHoldingAFolderIsNotRecognised()
    {
        var cache = _temp.CreateDirectory("imported");
        GodotProjectFixture.WriteImportedAsset(cache);
        Directory.CreateDirectory(Path.Combine(cache, "backup"));

        Assert.Equal(GodotCacheVerdict.Unrecognised, GodotCacheContents.ImportCache(cache, allowIgnoreFile: false));
    }

    /// <summary>Godot 3's editor wrote a <c>.gdignore</c> into its import folder, which was its whole data folder.</summary>
    [Fact]
    public void AGodot3ImportFolderMayHoldTheEditorsIgnoreFile()
    {
        var cache = GodotProjectFixture.CreateGodot3Project(_temp.CreateDirectory("Old"));

        Assert.Equal(GodotCacheVerdict.Recognised, GodotCacheContents.ImportCache(cache, allowIgnoreFile: true));
        Assert.Equal(GodotCacheVerdict.Unrecognised, GodotCacheContents.ImportCache(cache, allowIgnoreFile: false));
    }

    [Theory]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.vulkan.cache")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.d3d12.cache")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.cache")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.vulkan.cache0.tmp")]
    public void AShaderCacheOfVariantsIsRecognised(string variant)
    {
        var cache = _temp.CreateDirectory("shader_cache");
        GodotProjectFixture.WriteShaderVariant(cache, variant);

        Assert.Equal(GodotCacheVerdict.Recognised, GodotCacheContents.ShaderCache(cache));
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("bbbb.vulkan.cache")]
    [InlineData("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC.vulkan.cache")]
    public void AShaderCacheHoldingAFileTheRendererDoesNotWriteIsNot(string stranger)
    {
        var cache = _temp.CreateDirectory("shader_cache");
        GodotProjectFixture.WriteShaderVariant(cache, $"{new string('b', 40)}.vulkan.cache");
        GodotProjectFixture.WriteShaderVariant(cache, stranger);

        Assert.Equal(GodotCacheVerdict.Unrecognised, GodotCacheContents.ShaderCache(cache));
    }

    [Fact]
    public void AShaderCacheWhoseGroupIsNotAHashIsNotRecognised()
    {
        var cache = _temp.CreateDirectory("shader_cache");
        File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(cache, "CanvasShaderRD", "group")).FullName, $"{new string('b', 40)}.cache"), [1]);

        Assert.Equal(GodotCacheVerdict.Unrecognised, GodotCacheContents.ShaderCache(cache));
    }

    [Fact]
    public void AShaderCacheHoldingAFileAboveTheShadersIsNotRecognised()
    {
        var cache = _temp.CreateDirectory("shader_cache");
        GodotProjectFixture.WriteShaderVariant(cache, $"{new string('b', 40)}.cache");
        File.WriteAllBytes(Path.Combine(cache, "notes.txt"), [1]);

        Assert.Equal(GodotCacheVerdict.Unrecognised, GodotCacheContents.ShaderCache(cache));
    }
}
