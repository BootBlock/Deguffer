using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §6.3 — long path support is mandatory, because a MAX_PATH truncation is a silent partial
/// deletion rather than an error.
/// </summary>
public class LongPathTests
{
    /// <summary>
    /// <see cref="LongPath.Contains"/> is what two providers refuse a configured root with, so the
    /// answer it gives for a volume root is not academic: a caller asking whether something sits
    /// under a whole volume must not be told no.
    /// </summary>
    [Theory]
    [InlineData(@"C:\", @"C:\Users", true)]
    [InlineData(@"C:\", @"C:\", true)]
    [InlineData(@"C:\Users\me", @"C:\Users\me\.m2", true)]
    [InlineData(@"C:\Users\me\", @"C:\Users\me\.m2", true)]
    [InlineData(@"C:\Users\me", @"C:\Users\me", true)]
    [InlineData(@"C:\Users\me", @"C:\USERS\ME\.m2", true)]
    [InlineData(@"\\server\share", @"\\server\share\a", true)]
    [InlineData(@"C:\a\bc", @"C:\a\bcd", false)]
    [InlineData(@"C:\Users\me\.m2", @"C:\Users\me", false)]
    public void ContainsAnswersForTheRootsAProviderIsActuallyHanded(
        string ancestor, string candidate, bool expected) =>
        Assert.Equal(expected, LongPath.Contains(ancestor, candidate));

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
    /// The assumption every other long-path test in this suite rests on, made falsifiable.
    ///
    /// <para>.NET prepends <c>\\?\</c> itself to any path of 260 characters or more before it calls
    /// Win32. That is why building a deep tree and asserting an operation succeeded proves nothing
    /// about this codebase: such a test passes identically with <see cref="LongPath.Extended"/>
    /// deleted outright. Measured, rather than assumed — stripping every
    /// <c>LongPath.Extended</c> call from all sixteen seams in Core, one seam at a time, left the
    /// whole suite green for twelve of them; the four that go red are the ones that check the
    /// <em>form</em> of a path rather than the outcome of an operation.</para>
    ///
    /// <para>The registry is not what makes that true. <c>LongPathsEnabled</c> is set on the machine
    /// this was measured on, and <c>RtlAreLongPathsEnabled</c> still reports 0 inside an ordinary
    /// .NET apphost, because the process manifest must opt in as well and a test host has no such
    /// manifest. In that very process a raw <c>CreateDirectoryW</c> on a 377-character path failed
    /// with <c>ERROR_PATH_NOT_FOUND</c> while <c>Directory.CreateDirectory</c> on the same path
    /// succeeded. So an outcome-based long-path test is unfalsifiable on every machine, not merely
    /// on one with the registry value set.</para>
    ///
    /// <para>This test is the one place that assumption is checked. It goes red on a runtime that
    /// stops prefixing for us — which is precisely the moment <see cref="LongPath"/> starts earning
    /// its keep, and the moment the outcome-based tests elsewhere would begin to mean something.</para>
    /// </summary>
    [Fact]
    public void TheRuntimeStillReachesPastMaxPathWithoutOurPrefix()
    {
        using var temp = new TempDirectory();

        var deep = temp.Path;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Assert.True(deep.Length > 260);

        // No LongPath.Extended anywhere below. If any of this throws, the runtime has stopped
        // prefixing on our behalf and the suite's long-path coverage needs re-reading.
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "payload.bin"), new byte[512]);

        Assert.True(Directory.Exists(deep));
        Assert.Single(new DirectoryInfo(deep).EnumerateFiles());
    }

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
