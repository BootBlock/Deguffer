using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The locations a chosen scan measures. What it promises is that the scan measures exactly the
/// ticked ones, in the order a full scan would, and that the preferences holding it compare equal
/// when nothing was changed, so a tick that changes nothing writes nothing.
/// </summary>
public sealed class ProviderChoiceTests
{
    private static readonly FakeCleanupProvider Npm = new("npm");
    private static readonly FakeCleanupProvider Temp = new("temp");
    private static readonly FakeCleanupProvider Steam = new("steam");

    private static readonly IReadOnlyList<ICleanupProvider> Planned = [Temp, Npm, Steam];

    [Fact]
    public void NothingIsChosenAtFirst()
    {
        Assert.Empty(ProviderChoice.None.Of(Planned));
    }

    [Fact]
    public void ScansTheChosenOnesInThePlannersOrder()
    {
        var choice = ProviderChoice.None.With([Steam, Temp], chosen: true);

        Assert.Equal([Temp, Steam], choice.Of(Planned));
    }

    [Fact]
    public void TakingOneOutLeavesTheRest()
    {
        var choice = ProviderChoice.None.With([Steam, Temp, Npm], chosen: true).With([Npm], chosen: false);

        Assert.Equal([Temp, Steam], choice.Of(Planned));
        Assert.False(choice.Contains(Npm));
    }

    /// <summary>
    /// A stored id no provider has any more matches nothing, rather than failing the scan or being
    /// dropped: a location that comes back in a later version is still chosen.
    /// </summary>
    [Fact]
    public void AnIdNoProviderHasMatchesNothingAndIsKept()
    {
        var choice = new ProviderChoice { Ids = ["retired", "temp"] };

        Assert.Equal([Temp], choice.Of(Planned));
        Assert.Contains("retired", choice.Ids);
    }

    /// <summary>
    /// <see cref="Deguffer.App.Shell.PreferenceService"/> writes only when the preferences differ, so
    /// a choice that compared by reference would rewrite the file on every tick, and one that compared
    /// by order would see a change in the same set built another way.
    /// </summary>
    [Fact]
    public void TwoChoicesOfTheSameLocationsAreEqualHoweverTheyWereBuilt()
    {
        var one = ProviderChoice.None.With([Npm, Temp], chosen: true);
        var other = new ProviderChoice { Ids = ["temp", "npm", "temp"] };

        Assert.Equal(one, other);
        Assert.Equal(AppPreferences.Default with { ChosenToScan = one }, AppPreferences.Default with { ChosenToScan = other });
        Assert.NotEqual(one, one.With([Steam], chosen: true));
    }

    /// <summary>
    /// The serialiser's nullable checks reach the list, not what is in it, so a hand-edited null or
    /// empty entry arrives here. It must not become an id a provider could be compared against.
    /// </summary>
    [Fact]
    public void DropsAnEmptyOrMissingId()
    {
        var choice = new ProviderChoice { Ids = ["temp", "", null!] };

        Assert.Equal(["temp"], choice.Ids);
    }
}
