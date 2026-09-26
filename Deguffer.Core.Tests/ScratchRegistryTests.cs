using Deguffer.Testing;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sweep that clears registry scratch an interrupted run left behind.
///
/// <para>Its whole risk is taking something it should not, so most of these cases are negatives: the
/// parent survives, a key this run is still using survives, and a child the suite did not write
/// survives however old it is. That last one is §5.2's "recognised children only" turned on the
/// suite's own scratch, and <c>HKCU\Software</c> is exactly the kind of shared place the rule exists
/// for.</para>
///
/// <para>Nothing here can age a key: the registry stamps the write time itself and offers no way to
/// set it. The cutoff moves instead, which is the same comparison read from the other side. A cutoff
/// an hour ahead asks what the sweep would do to a key an hour old, and one an hour behind asks what
/// it does to the key this test just made.</para>
/// </summary>
public sealed class ScratchRegistryTests : IDisposable
{
    /// <summary>The margin the real sweep runs on, which these tests apply to a key of a known age.</summary>
    private static readonly TimeSpan OlderThan = TimeSpan.FromHours(1);

    /// <summary>
    /// How far apart the registry's clock and <see cref="DateTime.UtcNow"/> may read for the same
    /// moment. Both come from the system clock, so the slack only has to cover its update interval.
    /// </summary>
    private static readonly TimeSpan Granularity = TimeSpan.FromSeconds(5);

    /// <summary>
    /// This test's own scratch key, and the parent every sweep here is pointed at.
    ///
    /// <para>The real sweep is never pointed at <c>HKCU\Software</c> by a test. Doing so would put
    /// the live key of a test process running beside this one in reach, and the margin is the only
    /// thing keeping it out.</para>
    /// </summary>
    private readonly ScratchKey _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void RemovesAKeyAnEarlierRunLeft()
    {
        var stale = Child();

        ScratchRegistry.SweepStale(_scratch.Key, DateTime.UtcNow + OlderThan);

        Assert.False(Exists(stale));
    }

    [Fact]
    public void LeavesAKeyThisRunIsStillUsing()
    {
        var live = Child();

        ScratchRegistry.SweepStale(_scratch.Key, DateTime.UtcNow - OlderThan);

        Assert.True(Exists(live));
    }

    /// <summary>
    /// A child the suite did not write survives, whatever its age. Everything installed on the
    /// machine keeps its settings under <c>HKCU\Software</c>, and a name this class cannot account
    /// for is not the suite's to remove.
    /// </summary>
    [Fact]
    public void LeavesAChildItDoesNotRecognise()
    {
        var theirs = Child("notes");

        Assert.False(ScratchRegistry.IsScratchKey("notes"));

        ScratchRegistry.SweepStale(_scratch.Key, DateTime.UtcNow + OlderThan);

        Assert.True(Exists(theirs));
    }

    /// <summary>
    /// The parent goes nowhere, and the cutoff is not what saves it. The parent swept here carries a
    /// name the recogniser accepts and a write time before the cutoff, so it meets both tests a
    /// child has to meet, and the only thing between it and the delete is that a parent is never a
    /// candidate.
    /// </summary>
    [Fact]
    public void NeverTargetsTheParentItself()
    {
        var child = Child();

        Assert.True(ScratchRegistry.IsScratchKey(Name(_scratch.Path)));

        ScratchRegistry.SweepStale(_scratch.Key, DateTime.UtcNow + OlderThan);

        // The child as well as the parent: without it a sweep that did nothing at all would pass.
        Assert.False(Exists(child));

        using var parent = Registry.CurrentUser.OpenSubKey(_scratch.Path);
        Assert.NotNull(parent);
    }

    /// <summary>
    /// The age read is the key's own, and neither a constant nor the clock.
    ///
    /// <para>No sweep test can say so. Moving the cutoff is the only side of the comparison a test
    /// can move, and a reader that ignored the key and answered "now" would sit on the right side of
    /// both cutoffs these tests use. It would collect nothing, ever, and every other case here would
    /// still pass.</para>
    ///
    /// <para>The window refuses the constant: 1601, which is what a reader that dropped the file
    /// time would answer, is an age outside it. Reading the same untouched key twice, either side of
    /// a tick of the clock, refuses the live one: the key's own time does not move between the two
    /// reads, and <see cref="DateTime.UtcNow"/> does.</para>
    /// </summary>
    [Fact]
    public void ReadsTheWriteTimeTheRegistryKeeps()
    {
        var before = DateTime.UtcNow;
        var child = Child();
        var after = DateTime.UtcNow;

        var written = ScratchRegistry.LastWriteTimeUtc(_scratch.Key, child);

        Assert.NotNull(written);
        Assert.InRange(written.Value, before - Granularity, after + Granularity);

        WaitForTheClockToMove();

        Assert.Equal(written, ScratchRegistry.LastWriteTimeUtc(_scratch.Key, child));
    }

    /// <summary>
    /// A key that is not there has no age, and a sweep must not read that as "certainly stale". The
    /// key going between the listing and the age read is the ordinary way to reach it.
    /// </summary>
    [Fact]
    public void WillNotCallAKeyStaleWhenThereIsNoTimeToRead()
    {
        var absent = ScratchRegistry.NewName();

        Assert.Null(ScratchRegistry.LastWriteTimeUtc(_scratch.Key, absent));
        Assert.False(ScratchRegistry.IsStale(_scratch.Key, absent, DateTime.UtcNow + OlderThan));
    }

    /// <summary>Sweeping is what a scratch key's own constructor sets off, so a run clears the last one's leavings.</summary>
    /// <remarks>
    /// The trigger is <see cref="_scratch"/>, built before this method by xUnit. There is nothing to
    /// arrange: take the call out of <see cref="ScratchKey"/>'s constructor and nothing in the
    /// process sets this at all.
    /// </remarks>
    [Fact]
    public void SweepsWhenAScratchKeyIsMade() => Assert.True(ScratchRegistry.HasSwept);

    /// <summary>
    /// The parent the sweep is pointed at is the parent scratch keys are actually made under, asked
    /// of the two sides rather than of a literal, so they cannot drift apart.
    /// </summary>
    [Fact]
    public void MakesEveryScratchKeyUnderTheParent() =>
        Assert.Equal(ScratchRegistry.Parent, Above(_scratch.Path));

    /// <summary>
    /// The recogniser accepts what <see cref="ScratchKey"/> writes, asked of the writer itself
    /// rather than of a literal, so the two cannot drift apart.
    /// </summary>
    [Fact]
    public void RecognisesWhatAScratchKeyIsActuallyNamed() =>
        Assert.True(ScratchRegistry.IsScratchKey(Name(_scratch.Path)));

    /// <summary>
    /// Names the recogniser has to refuse. The registry compares key names without case, so the
    /// prefix is the half a case-insensitive reading would let through, and the identifier is the
    /// other half.
    /// </summary>
    [Theory]
    [InlineData("deguffer.tests.0123456789abcdef0123456789abcdef")]
    [InlineData("Deguffer.Tests.0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("Deguffer.Tests.notes")]
    [InlineData("Deguffer.Tests.")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("notes")]
    [InlineData("")]
    public void RefusesANameNoScratchKeyWouldCarry(string name) =>
        Assert.False(ScratchRegistry.IsScratchKey(name));

    /// <summary>
    /// Wait until <see cref="DateTime.UtcNow"/> reports a different moment.
    ///
    /// <para>It answers the same moment for a tick at a time, so two reads with nothing between them
    /// can land on one value, and a reader that answered "now" twice would look like a reader that
    /// answered the key's own unchanged time. This waits out the tick, which is under 16ms.</para>
    /// </summary>
    private static void WaitForTheClockToMove()
    {
        var mark = DateTime.UtcNow;

        SpinWait.SpinUntil(() => DateTime.UtcNow != mark);
    }

    /// <summary>The last segment of a registry path, which is the name the recogniser is asked about.</summary>
    private static string Name(string path) => path[(path.LastIndexOf('\\') + 1)..];

    /// <summary>Everything above that last segment, which is the key a sweep would be pointed at.</summary>
    private static string Above(string path) => path[..path.LastIndexOf('\\')];

    /// <summary>A child of this test's own scratch key, named the way <see cref="ScratchKey"/> names one.</summary>
    private string Child(string? name = null)
    {
        var child = name ?? ScratchRegistry.NewName();

        using var key = _scratch.Key.CreateSubKey(child);

        // A value and a key below it, so a sweep that only removed empty keys would not pass.
        key.SetValue("Content", 1, RegistryValueKind.DWord);
        key.CreateSubKey("Below").Dispose();

        return child;
    }

    /// <summary>Whether the child named <paramref name="name"/> is still there.</summary>
    private bool Exists(string name)
    {
        using var key = _scratch.Key.OpenSubKey(name);

        return key is not null;
    }
}
