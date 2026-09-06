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
    /// A volume-GUID path keeps its prefix, and this is a safety test rather than a cosmetic one.
    ///
    /// <para>Windows names a drive that has no letter <c>\\?\Volume{…}\</c>, and a File History
    /// target frequently is one. Stripping the prefix leaves <c>Volume{…}\…</c>, which is not a
    /// fully qualified path — so the next thing to touch it resolves it against Deguffer's own
    /// working directory. That string reaches <c>ProtectedPath</c>, where §5.6's negative then
    /// asserts the survival of a folder under Deguffer's directory rather than the one on the drive:
    /// it measures absent, it is reported as "nothing to preserve", and the check passes over
    /// whatever really happened to the folder it was meant to guard.</para>
    ///
    /// <para>The GUID is invented. Nothing here reaches a disk, which is the point — the defect is
    /// in the string handling, and a real volume would not make it any more visible.</para>
    /// </summary>
    [Theory]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\FileHistory")]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\")]
    public void KeepsThePrefixWhereStrippingItWouldUnrootThePath(string device)
    {
        Assert.Equal(device, LongPath.Display(device));

        // The property that makes it safe, stated rather than implied: whatever comes back can be
        // handed to Extended and Configured again without moving.
        Assert.True(Path.IsPathFullyQualified(LongPath.Display(device)));
        Assert.Equal(device, LongPath.Extended(LongPath.Display(device)));
    }

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

    /// <summary>
    /// <see cref="LongPath.IsReparsePoint"/> fails closed on a path whose attributes cannot be
    /// read, and the reading that matters is that no caller ever sees it do so.
    ///
    /// <para>Establishing the refusal at all took measuring, because the obvious denials do not
    /// produce it. NTFS answers <c>GetFileAttributes</c> out of the parent directory's own index
    /// whenever the caller may list the parent, so denying the target every right including
    /// <c>FILE_READ_ATTRIBUTES</c> leaves the attributes readable; and denying the parent
    /// everything leaves them readable too, because the target still answers for itself. Only both
    /// ends together refuse — which is what <see cref="DeniedDirectory.WithUnreadableAttributes"/>
    /// arranges.</para>
    ///
    /// <para>In exactly that condition <see cref="LongPath.DirectoryExists"/> answers false, and
    /// that is the whole reachability argument: it is the same attribute query, and
    /// <c>Directory.Exists</c> swallows the same failure. Every caller that turns a true here into
    /// a sentence about a link asks this first and takes the absent branch instead. The pairing is
    /// asserted rather than reasoned about, because a change to either half is what would let the
    /// fail-closed answer out.</para>
    /// </summary>
    [Fact]
    public void FailsClosedOnAPathItCannotReadWhileTheExistenceCheckAheadOfItFailsToo()
    {
        using var temp = new TempDirectory();

        var directory = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(directory);

        Assert.False(LongPath.IsReparsePoint(directory));
        Assert.True(LongPath.DirectoryExists(directory));

        using var denied = DeniedDirectory.WithUnreadableAttributes(directory);

        Assert.True(LongPath.IsReparsePoint(directory));
        Assert.False(LongPath.DirectoryExists(directory));
    }

    /// <summary>
    /// The one refusal that leaves the attributes readable, and the reason the §5.3 fixture next to
    /// this one cannot reach <see cref="LongPath.IsReparsePoint"/>'s closed branch.
    ///
    /// A directory the account may not list is the ordinary shape of an access refusal in this
    /// suite, and it says nothing at all about the attribute read: the right to list a directory
    /// and the right to read its own attributes are separate, and only the second is what
    /// <c>GetFileAttributes</c> needs.
    /// </summary>
    [Fact]
    public void ADirectoryTheAccountMayNotListStillAnswersForItsOwnAttributes()
    {
        using var temp = new TempDirectory();

        var directory = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(directory);

        using var denied = new DeniedDirectory(directory);

        Assert.False(LongPath.IsReparsePoint(directory));
        Assert.True(LongPath.DirectoryExists(directory));
    }

    /// <summary>
    /// §5.6 asks this of every protected path, so what it answers for each shape is the whole of
    /// what an emptied-in-place over-reach can be caught by.
    ///
    /// <para>A file, a missing path and an unreadable directory all answer false, and the last of
    /// those is the one worth pinning: a directory Windows will not list must not be reported as
    /// having held something, or a refusal Deguffer meets every day becomes an alarm. That is the
    /// same direction §5.3 takes for a locked file.</para>
    /// </summary>
    [Fact]
    public void HoldsAnythingAnswersOnlyForADirectoryThatCanBeListedAndIsNotEmpty()
    {
        using var temp = new TempDirectory();

        var empty = Directory.CreateDirectory(Path.Combine(temp.Path, "empty")).FullName;
        var full = Directory.CreateDirectory(Path.Combine(temp.Path, "full")).FullName;
        var file = Path.Combine(full, "one.bin");
        File.WriteAllBytes(file, new byte[8]);

        Assert.False(LongPath.HoldsAnything(empty));
        Assert.True(LongPath.HoldsAnything(full));

        // A file is not a directory holding something, and neither is a path that is not there.
        Assert.False(LongPath.HoldsAnything(file));
        Assert.False(LongPath.HoldsAnything(Path.Combine(temp.Path, "never-existed")));

        // A directory holding only another directory still holds something.
        var nested = Directory.CreateDirectory(Path.Combine(temp.Path, "outer", "inner")).FullName;
        Assert.True(LongPath.HoldsAnything(Path.GetDirectoryName(nested)!));
    }

    /// <summary>
    /// The refusal case on its own, because it is the one that decides whether §5.6's new check
    /// cries wolf. An unreadable directory answers the same as an empty one, so it can never be
    /// recorded as having held content and can never be reported as emptied.
    /// </summary>
    [Fact]
    public void AnUnreadableDirectoryHoldsNothingRatherThanRaisingAnAlarm()
    {
        using var temp = new TempDirectory();

        var directory = Directory.CreateDirectory(Path.Combine(temp.Path, "denied")).FullName;
        File.WriteAllBytes(Path.Combine(directory, "inside.bin"), new byte[8]);

        Assert.True(LongPath.HoldsAnything(directory));

        using var denied = new DeniedDirectory(directory);

        Assert.False(LongPath.HoldsAnything(directory));
    }

    /// <summary>
    /// §6.3 for the same helper: a directory whose contents sit past <c>MAX_PATH</c> still holds
    /// something. A crash guard rather than a discriminating test, on the reasoning CLAUDE.md's G8
    /// records — .NET prefixes long paths itself — but a helper §5.6 depends on must at least reach
    /// content that deep.
    /// </summary>
    [Fact]
    public void HoldsAnythingReachesContentPastMaxPath()
    {
        using var temp = new TempDirectory();

        var deep = Path.Combine(temp.Path, "deep");
        while (deep.Length < 280)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));

        var outermost = Path.Combine(temp.Path, "deep");
        Assert.True(outermost.Length < 260);
        Assert.True(LongPath.HoldsAnything(outermost));
    }
}
