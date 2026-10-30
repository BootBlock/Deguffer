using System.Security.Principal;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sweep that unregisters the sync roots an interrupted run left registered.
///
/// <para>Its risk is unregistering a root it should not, so most of these cases are negatives: a root
/// a run is still using stays, a root another account or another program registered is never
/// recognised, and a registration nothing can date stays. That second one is §5.2's "recognised
/// children only" turned on the suite's own scratch.</para>
///
/// <para>Each sweep here is handed only this test's own registration, never the whole list. A
/// test process running beside this one may have a root it has disconnected, and only the margin
/// keeps that one out of a sweep's reach.</para>
/// </summary>
public sealed class ScratchSyncRootsTests : IDisposable
{
    private static readonly TimeSpan OlderThan = TimeSpan.FromHours(1);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The case a killed host leaves: the registration stays, and the next run's scratch sweep takes
    /// the folder under it. The registration is unregistered however young it is.
    /// </summary>
    [Fact]
    public void UnregistersARootWhoseFolderIsGone()
    {
        var root = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));

        try
        {
            root.Disconnect();
            Directory.Delete(root.Path);

            ScratchSyncRoots.SweepStale(Own(root), DateTime.UtcNow - OlderThan);

            Assert.False(IsRegistered(root), "a registration over a folder that is gone survived the sweep");
        }
        finally
        {
            Release(root);
        }
    }

    /// <summary>
    /// A registration whose folder is older than the margin is an earlier run's, whatever is left
    /// under it. The cutoff moves rather than the folder's age, which is the same comparison.
    /// </summary>
    [Fact]
    public void UnregistersARootOlderThanTheMargin()
    {
        var root = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));

        try
        {
            root.Disconnect();

            ScratchSyncRoots.SweepStale(Own(root), DateTime.UtcNow + OlderThan);

            Assert.False(IsRegistered(root), "a stale registration survived the sweep");
            Assert.True(Directory.Exists(root.Path), "unregistering a root removed its folder");
        }
        finally
        {
            Release(root);
        }
    }

    [Fact]
    public void LeavesARootThisRunIsStillUsing()
    {
        using var root = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));
        root.Disconnect();

        ScratchSyncRoots.SweepStale(Own(root), DateTime.UtcNow - OlderThan);

        Assert.True(IsRegistered(root));
    }

    /// <summary>
    /// A root its sync app still has connected stays even past the margin, and the sweep does not
    /// throw for it. Windows refuses to unregister a connected root, which is a second guard on a
    /// live run's registration besides the margin.
    /// </summary>
    [Fact]
    public void LeavesAConnectedRootWhateverItsAge()
    {
        using var root = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));

        ScratchSyncRoots.SweepStale(Own(root), DateTime.UtcNow + OlderThan);

        Assert.True(IsRegistered(root));
    }

    /// <summary>
    /// A registration another sweep took between the listing and now costs this one nothing. Not
    /// throwing is the assertion: the sweep runs inside a <see cref="Lazy{T}"/>, which would re-throw
    /// one failure from every <see cref="ScratchSyncRoot"/> the run went on to make.
    /// </summary>
    [Fact]
    public void DoesNotThrowForARegistrationThatIsAlreadyGone()
    {
        var never = new ScratchSyncRoots.Registration(ScratchSyncRoots.NewId(), Path.Combine(_temp.Path, "gone"));

        ScratchSyncRoots.SweepStale([never], DateTime.UtcNow - OlderThan);
    }

    /// <summary>
    /// The registry is read in a layout Windows does not document, so this ties it to the documented
    /// call: a root registered through the API is found, under its identifier, over its folder.
    /// </summary>
    [Fact]
    public void FindsWhatAScratchRootRegisters()
    {
        using var root = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));

        var found = Assert.Single(ScratchSyncRoots.Registered(), registration => registration.Id == root.Id);

        Assert.Equal(root.Path, found.Folder, ignoreCase: true);
    }

    /// <summary>Sweeping is what a scratch root's own constructor sets off, so a run clears the last one's leavings.</summary>
    [Fact]
    public void SweepsWhenAScratchRootIsMade()
    {
        using var root = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));

        Assert.True(ScratchSyncRoots.HasSwept);
    }

    [Fact]
    public void RecognisesWhatAScratchRootIsActuallyNamed()
    {
        using var root = new ScratchSyncRoot(_temp.CreateDirectory("Synced"));

        Assert.True(ScratchSyncRoots.IsScratchId(root.Id));
    }

    /// <summary>
    /// Every identifier names this account, asked of the account rather than of the prefix, so a
    /// prefix that dropped the SID would recognise another user's scratch roots as ours.
    /// </summary>
    [Fact]
    public void NamesThisAccountInEveryIdentifier()
    {
        var user = WindowsIdentity.GetCurrent().User!.Value;

        Assert.StartsWith($"{ScratchSyncRoot.ProviderName}!{user}!", ScratchSyncRoots.NewId(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Identifiers the recogniser has to refuse: another account's scratch root, a different case of
    /// the provider name, which the registry would treat as the same key, an identifier the suite
    /// never writes, and a real sync app's root.
    /// </summary>
    [Theory]
    [InlineData("DegufferTests!S-1-5-21-1-2-3-500!0123456789abcdef0123456789abcdef")]
    [InlineData("deguffertests!{user}!0123456789abcdef0123456789abcdef")]
    [InlineData("DegufferTests!{user}!0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("DegufferTests!{user}!notes")]
    [InlineData("DegufferTests!{user}!")]
    [InlineData("OneDrive!{user}!Personal")]
    public void RefusesAnIdentifierNoScratchRootWouldCarry(string template)
    {
        var user = WindowsIdentity.GetCurrent().User!.Value;

        Assert.False(ScratchSyncRoots.IsScratchId(template.Replace("{user}", user, StringComparison.Ordinal)));
    }

    [Fact]
    public void CallsAFolderMadeWithinTheMarginLive()
    {
        var registration = new ScratchSyncRoots.Registration(ScratchSyncRoots.NewId(), _temp.CreateDirectory("Synced"));

        Assert.False(ScratchSyncRoots.IsStale(registration, DateTime.UtcNow - OlderThan));
        Assert.True(ScratchSyncRoots.IsStale(registration, DateTime.UtcNow + OlderThan));
    }

    /// <summary>
    /// A registration over a folder that is not there is stale at once, where a scratch tree that
    /// is not there is not: removing a registration takes no file with it.
    /// </summary>
    [Fact]
    public void CallsARegistrationOverNothingStale()
    {
        var registration = new ScratchSyncRoots.Registration(ScratchSyncRoots.NewId(), Path.Combine(_temp.Path, "gone"));

        Assert.True(ScratchSyncRoots.IsStale(registration, DateTime.UtcNow - OlderThan));
    }

    /// <summary>A registration Windows recorded no folder for has nothing to judge it by, so it stays.</summary>
    [Fact]
    public void WillNotCallARegistrationWithNoFolderStale()
    {
        var registration = new ScratchSyncRoots.Registration(ScratchSyncRoots.NewId(), Folder: null);

        Assert.False(ScratchSyncRoots.IsStale(registration, DateTime.UtcNow + OlderThan));
    }

    /// <summary>
    /// A folder whose age the account may not read is not one the sweep can call gone, so its
    /// registration stays: Windows throws for it rather than answering with the time it gives an
    /// entry that is not there.
    /// </summary>
    [Fact]
    public void WillNotCallARegistrationStaleWhenTheAgeReadIsRefused()
    {
        var folder = _temp.CreateDirectory("Synced");
        using var denied = DeniedDirectory.WithUnreadableAttributes(folder);
        var registration = new ScratchSyncRoots.Registration(ScratchSyncRoots.NewId(), folder);

        Assert.Throws<UnauthorizedAccessException>(() => Directory.GetCreationTimeUtc(folder));
        Assert.False(ScratchSyncRoots.IsStale(registration, DateTime.UtcNow + OlderThan));
    }

    private static bool IsRegistered(ScratchSyncRoot root) =>
        ScratchSyncRoots.Registered().Any(registration => registration.Id == root.Id);

    /// <summary>This test's own registration and no other, so a sweep cannot reach a concurrent run's.</summary>
    private static IReadOnlyList<ScratchSyncRoots.Registration> Own(ScratchSyncRoot root) =>
        [.. ScratchSyncRoots.Registered().Where(registration => registration.Id == root.Id)];

    /// <summary>
    /// Unregister a root the sweep was meant to have taken, if it did not, so a failing test leaves
    /// nothing registered. <see cref="ScratchSyncRoot.Dispose"/> is not used, because it refuses a
    /// root that is already gone.
    /// </summary>
    private static void Release(ScratchSyncRoot root)
    {
        if (IsRegistered(root))
        {
            root.Dispose();
        }
    }
}
