using Deguffer.Core.Exploring.Acting;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="ExploreActionPolicy.RefusedAtAndBelow"/>: the places Explore refuses together with
/// everything in them, which a duplicate search passes over (§7.4). Unlike
/// <see cref="ExploreActionPolicy.MayRemove"/>, it does not refuse a folder for what it holds.
/// </summary>
public sealed class RefusedAtAndBelowTests : IDisposable
{
    private readonly DuplicateTree _tree = new();
    private readonly ExploreActionPolicy _policy;

    public RefusedAtAndBelowTests() => _policy = _tree.Policy();

    public void Dispose() => _tree.Dispose();

    private bool Refused(params string[] segments) => _policy.RefusedAtAndBelow(Path.Combine([_tree.Top, .. segments])) is not null;

    [Theory]
    [InlineData("Windows")]
    [InlineData("Windows", "WinSxS", "x86_component")]
    [InlineData("Program Files")]
    [InlineData("Program Files (x86)", "Vendor")]
    [InlineData("ProgramData", "Vendor")]
    [InlineData("Users", "other")]
    [InlineData("Users", "other", "Documents", "a.txt")]
    [InlineData("$Recycle.Bin")]
    [InlineData("$Recycle.Bin", "S-1-5-21-1", "a.txt")]
    [InlineData("System Volume Information")]
    [InlineData("pagefile.sys")]
    [InlineData("$MFT")]
    [InlineData("Data", "Outlook Files")]
    [InlineData("Data", "Outlook Files", "archive", "a.txt")]
    [InlineData("Users", "profile", "AppData", "Local", "Microsoft", "Outlook")]
    public void APlaceExploreRefusesWithEverythingInItIsRefused(params string[] segments)
    {
        Assert.True(Refused(segments));
    }

    /// <summary>
    /// The Users folder is refused, but the signed-in profile inside it is carved back out, so neither
    /// the Users folder nor the profile is refused with everything in it.
    /// </summary>
    [Theory]
    [InlineData("Users")]
    [InlineData("Users", "profile")]
    [InlineData("Users", "profile", "Documents")]
    [InlineData("Users", "profile", "AppData", "Local")]
    [InlineData("Users", "profile", "AppData", "Local", "Microsoft")]
    [InlineData("Data", "$Recycle.Bin")]
    [InlineData("Data")]
    public void APlaceWithSomethingInItExploreAllowsIsNotRefused(params string[] segments)
    {
        Assert.False(Refused(segments));
    }

    /// <summary>A whole volume is never removed, and what is on it is asked about thing by thing.</summary>
    [Fact]
    public void AWholeVolumeIsNotRefused()
    {
        Assert.Null(_policy.RefusedAtAndBelow(_tree.Top));
    }

    /// <summary>A mail store is refused by its type wherever it is, the signed-in profile included.</summary>
    [Theory]
    [InlineData("Data", "archive.pst")]
    [InlineData("Users", "profile", "Documents", "mail.ost")]
    public void AMailStoreIsRefusedWhereverItIs(params string[] segments)
    {
        Assert.True(Refused(segments));
    }

    /// <summary>
    /// What is refused here is refused by <see cref="ExploreActionPolicy.MayRemove"/>, because the two
    /// ask the same rules: a search that passed over a place Explore would let go of would be hiding
    /// nothing, and the reverse would search a place Explore refuses.
    /// </summary>
    [Theory]
    [InlineData("Windows", "System32")]
    [InlineData("Users", "other")]
    [InlineData("$Recycle.Bin", "S-1-5-21-1")]
    [InlineData("Data", "Outlook Files", "a.txt")]
    [InlineData("Users", "profile", "AppData", "Local", "Microsoft", "Outlook", "a.ost")]
    public void EveryPlaceRefusedHereIsRefusedRemoval(params string[] segments)
    {
        var path = Path.Combine([_tree.Top, .. segments]);

        Assert.Equal(_policy.RefusedAtAndBelow(path)!.Reason, _policy.MayRemove(path).Reason);
    }

    [Fact]
    public void TheWatchAsksAboutTheChildrenOnTheWayToARefusalAndNoOthers()
    {
        var watch = _policy.WatchBelow(_tree.Top);
        var profile = _tree.Environment.UserProfile;

        Assert.True(watch.MayRefuse(_tree.Top, parentIsRoot: true, "Anything"));
        Assert.True(watch.MayRefuse(_tree.Users, parentIsRoot: false, "other"));
        Assert.True(watch.MayRefuse(Path.Combine(profile, "AppData", "Local"), parentIsRoot: false, "Microsoft"));
        Assert.True(watch.MayRefuse(Path.Combine(profile, "Documents"), parentIsRoot: false, "Outlook Files"));
        Assert.False(watch.MayRefuse(Path.Combine(profile, "Documents"), parentIsRoot: false, "Letters"));
        Assert.False(watch.MayRefuse(Path.Combine(_tree.Top, "Data"), parentIsRoot: false, "Photos"));
    }
}
