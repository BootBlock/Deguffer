using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The real <see cref="UserEnvironment"/>, which every other test replaces with a fake.
///
/// <para>These are runtime-assumption guards rather than rule tests, the same job
/// <c>LongPathTests</c> does. A provider's safety rules are proved against
/// <c>FakeUserEnvironment</c> precisely so they need no toolchain installed, but that leaves the
/// class which answers from the actual platform untested by anything — and one of its answers comes
/// from a P/Invoke that fails silently by returning null.</para>
/// </summary>
public sealed class UserEnvironmentTests
{
    /// <summary>
    /// LocalLow has no <see cref="Environment.SpecialFolder"/>, so it is resolved through
    /// <c>SHGetKnownFolderPath</c> from an instance initialiser that runs while
    /// <see cref="UserEnvironment.Current"/> is still being constructed. A static field holding the
    /// folder identifier would not be assigned yet at that moment, the call would fail with an
    /// empty identifier, and the singleton the application uses would report LocalLow as unknown
    /// for the life of the process — which reads exactly like a machine that has no LocalLow.
    ///
    /// <para><see cref="UserEnvironment.Current"/> specifically, not a fresh instance: the
    /// initialisation order this guards against only applies to the static field, so an assertion
    /// on <c>new UserEnvironment()</c> would pass with the defect present.</para>
    /// </summary>
    [Fact]
    public void TheSingletonResolvesLocalLowRatherThanReportingItUnknown()
    {
        var localLow = UserEnvironment.Current.LocalLowAppData;

        Assert.NotNull(localLow);
        Assert.EndsWith(
            Path.Combine("AppData", "LocalLow"),
            localLow,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The three tiers are siblings under one profile, so a LocalLow that resolved to somewhere
    /// else entirely would still satisfy the assertion above.
    /// </summary>
    [Fact]
    public void LocalLowSitsBesideTheOtherTwoApplicationDataTiers()
    {
        var environment = UserEnvironment.Current;

        Assert.Equal(
            Path.GetDirectoryName(environment.LocalAppData),
            Path.GetDirectoryName(environment.LocalLowAppData),
            StringComparer.OrdinalIgnoreCase);
    }
}
