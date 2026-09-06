using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The real <see cref="UserEnvironment"/>, which every other test replaces with a fake.
///
/// <para>This is a runtime-assumption guard rather than a rule test, the same job
/// <c>LongPathTests</c> does. A provider's safety rules are proved against
/// <c>FakeUserEnvironment</c> precisely so they need no toolchain installed, but that leaves the
/// class which answers from the actual platform untested by anything — and one of its answers comes
/// from a P/Invoke that fails silently by returning null.</para>
///
/// <para><b>Nothing here asserts where a folder is.</b> A known folder can be relocated, and this
/// tier and <c>%LOCALAPPDATA%</c> are redirected through separate registry values, so an assertion
/// on the shape of a path would go red on a machine that is configured legitimately and running
/// correct code. What is asserted is only what a defect would change.</para>
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
    ///
    /// <para>Null is a legitimate answer from the platform where no user profile is loaded, and the
    /// interface says so. It is not a legitimate answer to a test run, which is a signed-in
    /// interactive session by construction.</para>
    /// </summary>
    [Fact]
    public void TheSingletonResolvesLocalLowRatherThanReportingItUnknown()
    {
        var environment = UserEnvironment.Current;

        Assert.NotNull(environment.LocalLowAppData);

        // A wrong folder identifier does not fail — it answers with a different real folder. So the
        // discriminating assertion is that the answer is none of the places already had, which
        // holds wherever the profile is and wherever any of its tiers has been redirected to.
        Assert.NotEqual(environment.LocalAppData, environment.LocalLowAppData, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(environment.RoamingAppData, environment.LocalLowAppData, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(environment.UserProfile, environment.LocalLowAppData, StringComparer.OrdinalIgnoreCase);
    }
}
