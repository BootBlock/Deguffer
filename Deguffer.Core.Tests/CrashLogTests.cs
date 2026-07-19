using Deguffer.Core.Diagnostics;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The crash log exists because the XAML runtime kills the process before anything else can
/// record why (<c>0xc000027b</c>). It therefore runs at the worst possible moment — mid-teardown,
/// on an arbitrary thread, possibly twice — so the cases that matter are the ones where writing
/// itself goes wrong. It must never throw: an exception raised from here replaces a diagnosable
/// fault with an undiagnosable one.
/// </summary>
public class CrashLogTests
{
    [Fact]
    public void RecordsTheTypeMessageAndStackOfTheException()
    {
        using var temp = new TempDirectory();
        var log = new CrashLog(new FakeUserEnvironment(temp.Path));

        Assert.True(log.Record("Application.UnhandledException", Thrown("binding evaluation failed")));

        var written = File.ReadAllText(log.FilePath);
        Assert.Contains("Application.UnhandledException", written);
        Assert.Contains(nameof(InvalidOperationException), written);
        Assert.Contains("binding evaluation failed", written);
        Assert.Contains(nameof(Thrown), written);
    }

    /// <summary>
    /// The bug class this was built for nests the real cause inside a framework wrapper, so an
    /// entry that stops at the outer exception records nothing useful.
    /// </summary>
    [Fact]
    public void RecordsTheInnerExceptionThatCarriesTheRealCause()
    {
        using var temp = new TempDirectory();
        var log = new CrashLog(new FakeUserEnvironment(temp.Path));

        log.Record(
            "TaskScheduler.UnobservedTaskException",
            new TaskCanceledException("a callback faulted", Thrown("null view-model state")));

        Assert.Contains("null view-model state", File.ReadAllText(log.FilePath));
    }

    /// <summary>A fault on the second run must not erase the evidence from the first.</summary>
    [Fact]
    public void AppendsRatherThanReplacingEarlierEntries()
    {
        using var temp = new TempDirectory();
        var log = new CrashLog(new FakeUserEnvironment(temp.Path));

        log.Record("first", Thrown("earlier fault"));
        log.Record("second", Thrown("later fault"));

        var written = File.ReadAllText(log.FilePath);
        Assert.Contains("earlier fault", written);
        Assert.Contains("later fault", written);
    }

    /// <summary>
    /// A crash loop writes an entry per launch. Deguffer's whole purpose is reclaiming disk space,
    /// so its own diagnostics must be bounded rather than growing without limit.
    /// </summary>
    [Fact]
    public void RestartsTheFileOnceItGrowsPastItsCap()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var log = new CrashLog(environment);

        Directory.CreateDirectory(Path.Combine(environment.LocalAppData, "Deguffer"));
        File.WriteAllText(log.FilePath, new string('x', 256 * 1024) + "ancient fault");

        log.Record("latest", Thrown("newest fault"));

        var written = File.ReadAllText(log.FilePath);
        Assert.DoesNotContain("ancient fault", written);
        Assert.Contains("newest fault", written);
        Assert.True(written.Length < 256 * 1024);
    }

    /// <summary>
    /// A directory sitting where the log file belongs — the file cannot be opened at all. The
    /// handler has to survive it, because it is running inside the crash it is trying to record.
    /// </summary>
    [Fact]
    public void ReportsFailureRatherThanThrowingWhenTheFileCannotBeWritten()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var log = new CrashLog(environment);

        Directory.CreateDirectory(LongPath.Extended(log.FilePath));

        Assert.False(log.Record("Application.UnhandledException", Thrown("secondary failure")));
    }

    /// <summary>
    /// §6.3 — a profile deep enough to push the log past MAX_PATH. A truncated write here would
    /// mean the one artefact left behind by a fault is the one that silently did not appear.
    ///
    /// A smoke test, on the same terms as <see cref="LongPathTests"/>: it holds even with the
    /// prefixing removed, because .NET applies <c>\\?\</c> itself to a rooted path at 260
    /// characters. Core prefixes explicitly anyway so the behaviour does not depend on that.
    /// </summary>
    [Fact]
    public void WritesToAProfileBeyondMaxPath()
    {
        using var temp = new TempDirectory();

        var deep = temp.Path;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        var log = new CrashLog(new FakeUserEnvironment(deep));

        Assert.True(log.FilePath.Length > 260);
        Assert.True(log.Record("Application.UnhandledException", Thrown("deep profile fault")));
        Assert.Contains("deep profile fault", File.ReadAllText(LongPath.Extended(log.FilePath)));
    }

    /// <summary>
    /// WinUI can hand a handler event arguments whose exception failed to marshal, and a
    /// non-CLS-compliant throw reaches <c>AppDomain.UnhandledException</c> as a non-Exception. A
    /// guard clause here would throw from inside the crash it is recording; an entry with no
    /// detail still separates "the handler never ran" from "there was nothing to write".
    /// </summary>
    [Fact]
    public void StillRecordsAnEntryWhenTheHandlerSuppliesNoException()
    {
        using var temp = new TempDirectory();
        var log = new CrashLog(new FakeUserEnvironment(temp.Path));

        Assert.True(log.Record("AppDomain.UnhandledException", exception: null));
        Assert.Contains("AppDomain.UnhandledException", File.ReadAllText(log.FilePath));
    }

    /// <summary>
    /// A profile path the platform reported as empty — <c>LongPath</c> rejects it before any I/O
    /// is attempted, which is the one failure that arrives as an ArgumentException rather than an
    /// IOException. It must still not escape.
    /// </summary>
    [Fact]
    public void ReportsFailureRatherThanThrowingWhenTheProfilePathIsUnusable()
    {
        var log = new CrashLog(new UnusableProfileEnvironment());

        Assert.False(log.Record("Application.UnhandledException", Thrown("fault on a broken profile")));
    }

    /// <summary>A platform that reports no local profile at all, as it can in a damaged session.</summary>
    private sealed class UnusableProfileEnvironment : IUserEnvironment
    {
        public string UserProfile => string.Empty;

        public string LocalAppData => string.Empty;

        public string RoamingAppData => string.Empty;

        public string TempPath => string.Empty;

        public string? FindExecutable(string command) => null;

        public string? GetEnvironmentVariable(string name) => null;

        public void Invalidate()
        {
        }
    }

    /// <summary>Thrown and caught, so the entry has a real stack rather than a null one.</summary>
    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }
}
