using Deguffer.Core.Safety;
using Deguffer.Testing;

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
    /// Every spelling Windows accepts for a device-namespace path comes out in the one form the
    /// rest of the code classifies, and <see cref="LongPath.Display"/>, <see cref="LongPath.Configured"/>
    /// and <see cref="LongPath.Extended"/> agree on it.
    ///
    /// <para><see cref="Path.IsPathFullyQualified(string)"/> accepts all of these, so a configured
    /// value can arrive as any of them. Classified as they arrived, <c>\\.\C:\</c> and <c>\??\C:\</c>
    /// became <c>\\?\UNC\.\C:\</c> and <c>\\?\\??\C:\</c>, which name nothing, and <c>//?/C:/</c>
    /// left <see cref="LongPath.Configured"/> still prefixed, so no display-form comparison matched
    /// it.</para>
    ///
    /// <para><c>\\.\C:</c> with no separator is the volume itself rather than its top folder, and a
    /// share whose server is <c>.</c> would read back as a device path, so both keep the prefix. The
    /// volume GUID is invented.</para>
    /// </summary>
    [Theory]
    [InlineData(@"\\.\C:\Windows", @"C:\Windows", @"\\?\C:\Windows")]
    [InlineData(@"//?/C:/Windows", @"C:\Windows", @"\\?\C:\Windows")]
    [InlineData(@"\\?/C:/Windows", @"C:\Windows", @"\\?\C:\Windows")]
    [InlineData(@"//./C:/Windows", @"C:\Windows", @"\\?\C:\Windows")]
    [InlineData(@"\??\C:\Windows", @"C:\Windows", @"\\?\C:\Windows")]
    [InlineData(@"\\.\C:\Windows\..\Temp", @"C:\Temp", @"\\?\C:\Temp")]
    [InlineData(@"\\.\C:\", @"C:\", @"\\?\C:\")]
    [InlineData(@"\\.\UNC\server\share\cache", @"\\server\share\cache", @"\\?\UNC\server\share\cache")]
    [InlineData(@"\??\UNC\server\share\cache", @"\\server\share\cache", @"\\?\UNC\server\share\cache")]
    [InlineData(@"//?/UNC/server/share/cache", @"\\server\share\cache", @"\\?\UNC\server\share\cache")]
    [InlineData(
        @"\\.\Volume{11111111-2222-3333-4444-555555555555}\FileHistory",
        @"\\?\Volume{11111111-2222-3333-4444-555555555555}\FileHistory",
        @"\\?\Volume{11111111-2222-3333-4444-555555555555}\FileHistory")]
    [InlineData(@"\\.\C:", @"\\?\C:", @"\\?\C:")]
    [InlineData(@"\\?\UNC\.\C:\Windows", @"\\?\UNC\.\C:\Windows", @"\\?\UNC\.\C:\Windows")]
    public void ReadsEveryDeviceSpellingAsTheLocationWindowsOpens(
        string spelled, string display, string extended)
    {
        Assert.Equal(display, LongPath.Display(spelled));
        Assert.Equal(extended, LongPath.Extended(spelled));
        Assert.Equal(Path.TrimEndingDirectorySeparator(display), LongPath.Configured(spelled));

        // The three agree, so a path that has been through one can go through another unmoved.
        Assert.Equal(extended, LongPath.Extended(display));
        Assert.Equal(display, LongPath.Display(extended));
    }

    /// <summary>
    /// <see cref="LongPath.Unaliased"/> answers in display form whether or not the path carries an
    /// alias, because the in-use check compares what it returns with display-form folders. The
    /// paths are invented and carry no <c>~</c>, so nothing is asked of the disk.
    /// </summary>
    [Theory]
    [InlineData(@"\\?\D:\build", @"D:\build")]
    [InlineData(@"\\.\D:\build", @"D:\build")]
    [InlineData(@"\??\D:\build", @"D:\build")]
    [InlineData(@"\\?\UNC\server\share\build", @"\\server\share\build")]
    [InlineData(@"D:\build", @"D:\build")]
    public void AnswersInDisplayFormForAPathWithNoAlias(string held, string expected) =>
        Assert.Equal(expected, LongPath.Unaliased(held));

    /// <summary>
    /// <see cref="LongPath.Canonical"/> reads every spelling Windows opens as one folder as that
    /// folder: either separator, a <c>.</c> or <c>..</c>, and the device prefix, alone or together.
    /// The paths are invented and carry no <c>~</c>, so nothing is asked of the disk.
    /// </summary>
    [Theory]
    [InlineData(@"C:/Users/testuser/AppData/Local/Temp/profile-1")]
    [InlineData(@"C:\Users\testuser\AppData\Local\Temp\x\..\profile-1")]
    [InlineData(@"C:\Users\testuser\AppData\Local\Temp\.\profile-1")]
    [InlineData(@"\\?\C:\Users\testuser\AppData\Local\Temp\profile-1")]
    [InlineData(@"\\?\C:\Users\testuser\AppData\Local\Temp\x\..\profile-1")]
    [InlineData(@"//?/C:/Users/testuser/AppData/Local/Temp/profile-1")]
    public void ReadsEverySpellingOfAFolderAsThatFolder(string spelled)
    {
        const string Folder = @"C:\Users\testuser\AppData\Local\Temp\profile-1";

        Assert.Equal(Folder, LongPath.Canonical(spelled));
        Assert.Equal(Folder, LongPath.Unaliased(spelled));
    }

    /// <summary>
    /// A path that is not fully qualified has no canonical form, because finding one would resolve
    /// it against a working directory nobody named.
    /// </summary>
    [Fact]
    public void HasNoCanonicalFormForAPathThatIsNotFullyQualified() =>
        Assert.Null(LongPath.Canonical(@"Temp\profile-1"));

    /// <summary>
    /// An alias is expanded on the part of the path that exists, and what follows it is kept as
    /// spelled. A program started with a log it has not written yet names a path whose last
    /// segment is missing, and <c>GetLongPathName</c> refuses the whole of such a path.
    ///
    /// <para>The expected form is the folder's own, which exists and so was expanded before this
    /// change too. <b>This proves nothing on a volume with 8.3 name creation disabled</b>, where the
    /// fixture falls back to the ordinary path.</para>
    /// </summary>
    [Fact]
    public void ExpandsAnAliasAboveASegmentThatDoesNotExistYet()
    {
        using var temp = new TempDirectory();
        var run = temp.CreateDirectory("Run-Folder-Long");
        var asNamed = ShortPath.Of(run) ?? run;

        Assert.DoesNotContain("~", LongPath.Unaliased(run), StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(LongPath.Unaliased(run), "logs", "out.log"),
            LongPath.Canonical(Path.Combine(asNamed, "logs", "out.log")),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A segment carrying a <c>~</c> that Windows would not describe has no canonical form, because
    /// it may be an alias for anything. <see cref="LongPath.Unaliased"/>, which has no way to say
    /// so, gives back the spelling it was handed in full form.
    /// </summary>
    [Fact]
    public void HasNoCanonicalFormForAnAliasWindowsWouldNotDescribe()
    {
        using var temp = new TempDirectory();
        var hidden = temp.CreateDirectory("Locked", "run~1");
        var named = Path.Combine(temp.Path, "Locked", ".", "run~1", "out.log");

        using var denied = new DeniedDirectory(Path.Combine(temp.Path, "Locked"));

        Assert.Null(LongPath.Canonical(named));
        Assert.Equal(Path.Combine(hidden, "out.log"), LongPath.Unaliased(named));
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
    /// <para>In exactly that condition <see cref="LongPath.ProbeDirectory"/> answers
    /// <see cref="PathPresence.Refused"/>, and that is the whole reachability argument: it is the
    /// same attribute query, so every caller that turns a true here into a sentence about a link
    /// meets the refusal first and reports it as one. The pairing is asserted rather than reasoned
    /// about, because a change to either half is what would let the fail-closed answer out.</para>
    /// </summary>
    [Fact]
    public void FailsClosedOnAPathItCannotReadWhileTheProbeAheadOfItReportsTheRefusal()
    {
        using var temp = new TempDirectory();

        var directory = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(directory);

        Assert.False(LongPath.IsReparsePoint(directory));
        Assert.Equal(PathPresence.Present, LongPath.ProbeDirectory(directory));

        using var denied = DeniedDirectory.WithUnreadableAttributes(directory);

        Assert.True(LongPath.IsReparsePoint(directory));
        Assert.Equal(PathPresence.Refused, LongPath.ProbeDirectory(directory));
    }

    /// <summary>
    /// The three-state probe tells a directory that is not there from one Windows would not
    /// describe, which is the distinction <see cref="LongPath.DirectoryExists"/> cannot draw.
    ///
    /// <para>Before this, eleven providers read the refusal as absence and reported a cache that
    /// was on the disk as a tool that is not installed — and denied the row through
    /// <c>IsPresentAsync</c> on the same evidence, so nothing about it was drawn at all.</para>
    /// </summary>
    [Fact]
    public void TellsADirectoryThatIsNotThereFromOneWindowsWillNotDescribe()
    {
        using var temp = new TempDirectory();

        var directory = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(directory);

        Assert.Equal(PathPresence.Absent, LongPath.ProbeDirectory(Path.Combine(temp.Path, "nothing")));
        Assert.Equal(PathPresence.Present, LongPath.ProbeDirectory(directory));

        using var denied = DeniedDirectory.WithUnreadableAttributes(directory);

        Assert.Equal(PathPresence.Refused, LongPath.ProbeDirectory(directory));
    }

    /// <summary>
    /// The question §5.6 asks of a protected path, which may be either kind. A refusal is kept apart
    /// from absence for a file as well as a directory, because a survivor recorded as absent is one
    /// the check afterwards can never fail.
    /// </summary>
    [Fact]
    public void AnEntryOfEitherKindIsTreatedAlikeAndARefusalIsNotAbsence()
    {
        using var temp = new TempDirectory();

        var directory = temp.CreateDirectory("cache");
        var file = temp.CreateFile(1, "cache", "a.bin");

        Assert.Equal(PathPresence.Present, LongPath.ProbeEntry(directory));
        Assert.Equal(PathPresence.Present, LongPath.ProbeEntry(file));
        Assert.Equal(PathPresence.Absent, LongPath.ProbeEntry(Path.Combine(temp.Path, "nothing")));

        using var denied = DeniedDirectory.WithUnreadableAttributes(directory);

        Assert.Equal(PathPresence.Refused, LongPath.ProbeEntry(directory));
    }

    /// <summary>
    /// The form a presence probe asks in: a refusal reads as "may be there", because a row that
    /// never appears is the one state nothing downstream can correct.
    /// </summary>
    [Fact]
    public void ADirectoryWindowsWillNotDescribeMayExist()
    {
        using var temp = new TempDirectory();

        var directory = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(directory);

        Assert.False(LongPath.DirectoryMayExist(Path.Combine(temp.Path, "nothing")));

        using var denied = DeniedDirectory.WithUnreadableAttributes(directory);

        Assert.True(LongPath.DirectoryMayExist(directory));
        Assert.False(LongPath.DirectoryExists(directory));
    }

    /// <summary>
    /// The overload that answers the link question from the same attribute read says false for a
    /// path Windows would not describe, and says so through the return value instead.
    ///
    /// <para>That is the opposite of <see cref="LongPath.IsReparsePoint"/>, which fails closed, and
    /// the pairing is what keeps a caller from having two answers to choose between. A caller
    /// reading the link answer without settling <see cref="PathPresence.Refused"/> first would take
    /// false as "proceed", so the ordering is asserted rather than left to the doc comment.</para>
    /// </summary>
    [Fact]
    public void AnswersTheLinkQuestionOnlyWhereWindowsDescribedThePath()
    {
        using var temp = new TempDirectory();

        var directory = temp.CreateDirectory("cache");
        var link = Path.Combine(temp.Path, "link");
        SymbolicLink.ToDirectory(link, directory);

        Assert.Equal(PathPresence.Present, LongPath.ProbeDirectory(link, out var linkIsALink));
        Assert.True(linkIsALink);

        Assert.Equal(PathPresence.Present, LongPath.ProbeDirectory(directory, out var plainIsALink));
        Assert.False(plainIsALink);

        // Null rather than false for the two states Windows described nothing in, so a caller that
        // skipped the return value cannot read the answer as "not a link, carry on".
        Assert.Equal(
            PathPresence.Absent,
            LongPath.ProbeDirectory(Path.Combine(temp.Path, "nothing"), out var goneIsALink));
        Assert.Null(goneIsALink);

        using var denied = DeniedDirectory.WithUnreadableAttributes(directory);

        Assert.Equal(PathPresence.Refused, LongPath.ProbeDirectory(directory, out var refusedIsALink));
        Assert.Null(refusedIsALink);

        // And the fail-closed predicate disagrees, which is why the two may not be read the same way.
        Assert.True(LongPath.IsReparsePoint(directory));
    }

    /// <summary>
    /// <see cref="LongPath.DirectoryExists"/> and <see cref="LongPath.FileExists"/> are now the
    /// probe's <see cref="PathPresence.Present"/> arm rather than <c>Directory.Exists</c> and
    /// <c>File.Exists</c>, and around a hundred call sites depend on the answer not moving.
    ///
    /// <para><b>The shapes below are the ones measured to differ, or to be capable of it.</b> A
    /// trailing separator after a <em>file</em> is the one that did: <c>File.Exists</c> refuses it
    /// and <c>GetFileAttributes</c> answers for the file anyway, so without the guard in
    /// <c>Probe</c> this reverses from false to true and a caller that opened what it was told was
    /// there would meet <c>ERROR_DIRECTORY</c>. The same separator after a directory agrees either
    /// way, which is why the shape has to be listed for both kinds and not one.</para>
    ///
    /// <para>A path past <c>MAX_PATH</c> is here because it is the one §6.3 exists for, and a
    /// dangling link because an attribute read describes the link while a resolving check could
    /// reasonably have described the target. Neither turned out to differ; they are asserted so
    /// that a later change to <c>Probe</c> cannot make them.</para>
    /// </summary>
    [Fact]
    public void AnswersExactlyWhatTheFrameworkAnswersForEveryShapeThatCouldDiffer()
    {
        using var temp = new TempDirectory();

        var directory = temp.CreateDirectory("cache");
        var file = temp.CreateFile(1, "cache", "a.bin");
        var deep = temp.CreateFile(1, "cache", new string('d', 120), new string('e', 120), new string('f', 120), "deep.bin");

        var target = temp.CreateDirectory("gone");
        var dangling = Path.Combine(temp.Path, "dangling");
        SymbolicLink.ToDirectory(dangling, target);
        Directory.Delete(target);

        foreach (var path in (string[])
                 [
                     directory,
                     directory + Path.DirectorySeparatorChar,
                     file,
                     file + Path.DirectorySeparatorChar,
                     file + Path.AltDirectorySeparatorChar,
                     deep,
                     Path.GetDirectoryName(deep)!,
                     dangling,
                     temp.Path,
                     temp.Path + Path.DirectorySeparatorChar,
                     Path.Combine(temp.Path, "nothing"),
                     Path.Combine(temp.Path, "nothing") + Path.DirectorySeparatorChar,
                 ])
        {
            Assert.Equal(Directory.Exists(path), LongPath.DirectoryExists(path));
            Assert.Equal(File.Exists(path), LongPath.FileExists(path));
            Assert.Equal(
                Directory.Exists(path) || File.Exists(path),
                LongPath.ProbeEntry(path) is PathPresence.Present);
        }

        // The long path is genuinely long, or the two entries above prove nothing about §6.3.
        Assert.True(deep.Length > 260);
    }

    /// <summary>
    /// A path that is not there is answered without an exception being thrown to say so.
    ///
    /// <para>Nearly every presence question is asked about something absent: a <c>PATH</c> search
    /// tries each directory against each extension, and discovery asks after every place a tool
    /// might keep something. Read through <c>File.GetAttributes</c>, each of those threw and was
    /// caught, and building Explore's removal policy threw more than five thousand times. Both
    /// errors that say "absent" are covered: a missing leaf is <c>ERROR_FILE_NOT_FOUND</c> and a
    /// missing parent is <c>ERROR_PATH_NOT_FOUND</c>.</para>
    ///
    /// <para>Counted on this thread only, because the suite runs other tests in parallel and their
    /// exceptions are raised through the same event.</para>
    /// </summary>
    [Fact]
    public void AnswersAbsenceWithoutThrowing()
    {
        using var temp = new TempDirectory();

        string[] absent = [Path.Combine(temp.Path, "nothing.bin"), Path.Combine(temp.Path, "nothing", "a.bin")];

        var thread = Environment.CurrentManagedThreadId;
        var thrown = new List<Exception>();

        void Record(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread)
            {
                thrown.Add(e.Exception);
            }
        }

        var answers = new List<object?>();

        AppDomain.CurrentDomain.FirstChanceException += Record;

        try
        {
            foreach (var path in absent)
            {
                answers.Add(LongPath.ProbeFile(path));
                answers.Add(LongPath.ProbeDirectory(path));
                answers.Add(LongPath.ProbeEntry(path));
                answers.Add(LongPath.IsReparsePoint(path));
                answers.Add(WindowsFileSystem.Default.MayExist(path));
                answers.Add(WindowsFileSystem.Default.TryGetAttributes(path));
            }
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Record;
        }

        Assert.Empty(thrown);

        // And the answers are still the absent ones, or not throwing would be no achievement.
        object?[] expected = [PathPresence.Absent, PathPresence.Absent, PathPresence.Absent, false, false, null];
        Assert.Equal([.. expected, .. expected], answers);
    }

    /// <summary>
    /// A file whose first attribute read is refused is still described, through the directory's own
    /// index, exactly as the framework describes it.
    ///
    /// <para>The framework asks <c>FindFirstFileExW</c> whenever the first read fails for any reason
    /// but an unreachable path, and the answers here have to stay the framework's: around a hundred
    /// call sites read presence through <see cref="LongPath"/>. Without that second read, a file
    /// pending deletion reads as refused rather than present, and so does <c>pagefile.sys</c>, which
    /// answers the first read with a sharing violation.</para>
    /// </summary>
    [Fact]
    public void DescribesAFileTheFirstAttributeReadRefusesAsTheFrameworkDoes()
    {
        using var temp = new TempDirectory();

        var file = temp.CreateFile(1, "pending.bin");

        using var pending = new PendingDeletion(file);

        // What the framework says, which is what the answers below must match.
        Assert.True(File.Exists(file));

        Assert.Equal(PathPresence.Present, LongPath.ProbeFile(file));
        Assert.True(LongPath.FileExists(file));
        Assert.False(LongPath.IsReparsePoint(file));
        Assert.Equal(File.GetAttributes(file), WindowsFileSystem.Default.TryGetAttributes(file));
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
}
