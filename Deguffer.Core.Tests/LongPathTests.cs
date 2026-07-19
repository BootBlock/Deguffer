using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §6.3 — long path support is mandatory, because a MAX_PATH truncation is a silent partial
/// deletion rather than an error.
/// </summary>
public class LongPathTests
{
    [Fact]
    public void PrefixesALocalPathForTheWin32DeviceNamespace() =>
        Assert.Equal(@"\\?\C:\Users\me\.gradle", LongPath.Extended(@"C:\Users\me\.gradle"));

    [Fact]
    public void PrefixesAUncPathWithTheUncForm() =>
        Assert.Equal(@"\\?\UNC\server\share\cache", LongPath.Extended(@"\\server\share\cache"));

    [Fact]
    public void IsIdempotentSoItCanBeAppliedDefensively() =>
        Assert.Equal(@"\\?\C:\x", LongPath.Extended(LongPath.Extended(@"C:\x")));

    [Theory]
    [InlineData(@"\\?\C:\x", @"C:\x")]
    [InlineData(@"\\?\UNC\server\share", @"\\server\share")]
    [InlineData(@"C:\x", @"C:\x")]
    public void RoundTripsBackToADisplayablePath(string extended, string expected) =>
        Assert.Equal(expected, LongPath.Display(extended));

    /// <summary>
    /// A smoke test over the real filesystem. The assertions above carry the actual proof, because
    /// they check the string form directly — this one would hold even with the prefixing removed,
    /// since .NET applies <c>\\?\</c> itself at 260 characters.
    /// </summary>
    [Fact]
    public void HandlesAPathBeyondMaxPathOnARealFilesystem()
    {
        using var temp = new TempDirectory();

        // Nest until comfortably past 260 characters — the case that silently truncates.
        var deep = temp.Path;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        var file = Path.Combine(deep, "payload.bin");
        File.WriteAllBytes(LongPath.Extended(file), new byte[512]);

        Assert.True(deep.Length > 260);
        Assert.True(LongPath.DirectoryExists(deep));
        Assert.True(LongPath.FileExists(file));
    }
}
