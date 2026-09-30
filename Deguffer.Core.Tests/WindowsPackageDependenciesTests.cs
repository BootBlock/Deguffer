using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the real reader makes of WiX dependency registrations, against scratch keys laid out as the
/// Burn engine writes them. A fake would be asserting its own reply.
/// </summary>
public sealed class WindowsPackageDependenciesTests : IDisposable
{
    private const string Bundle = "{0a1b2c3d-0000-0000-0000-000000000001}";

    private readonly ScratchKey _machine = new();

    private readonly ScratchKey _user = new();

    public void Dispose()
    {
        _machine.Dispose();
        _user.Dispose();
    }

    private PackageDependencies Read(params Func<RegistryKey?>[] roots) =>
        new WindowsPackageDependencies(roots.Length > 0 ? roots : [Open(_machine), Open(_user)]).Read();

    private static Func<RegistryKey?> Open(ScratchKey scratch) => () => Registry.CurrentUser.OpenSubKey(scratch.Path);

    private static void Provider(ScratchKey root, string name, object? code, params string[] dependents)
    {
        using var provider = root.Key.CreateSubKey(name);

        if (code is not null)
        {
            provider.SetValue(null, code);
        }

        foreach (var dependent in dependents)
        {
            provider.CreateSubKey($@"Dependents\{dependent}").Dispose();
        }
    }

    [Fact]
    public void EachProviderIsListedUnderEveryDependentItNames()
    {
        Provider(_machine, "Tool.Core", "{11111111-1111-1111-1111-111111111111}", Bundle, "{0a1b2c3d-0000-0000-0000-000000000002}");
        Provider(_user, "Tool.User", "{22222222-2222-2222-2222-222222222222}", Bundle);

        var read = Read();

        Assert.Equal(PathPresence.Present, read.Presence);
        Assert.Equal(
            [new PackageProvider("Tool.Core", "{11111111-1111-1111-1111-111111111111}"), new PackageProvider("Tool.User", "{22222222-2222-2222-2222-222222222222}")],
            read.ProvidersOf(Bundle.ToUpperInvariant()));
        Assert.Single(read.ProvidersOf("{0a1b2c3d-0000-0000-0000-000000000002}"));
    }

    /// <summary>An executable package's provider has no default value, and a value of another type says no code.</summary>
    [Fact]
    public void AProviderWithNoTextDefaultNamesNoCode()
    {
        Provider(_machine, "Tool.Exe", null, Bundle);
        Provider(_machine, "Tool.Odd", 5, Bundle);

        var providers = Read().ProvidersOf(Bundle);

        Assert.Equal(["Tool.Exe", "Tool.Odd"], providers.Select(p => p.Key).Order());
        Assert.All(providers, provider => Assert.Null(provider.Code));
    }

    [Fact]
    public void AProviderWithNoDependentsListsNothing()
    {
        Provider(_machine, "Tool.Alone", "{11111111-1111-1111-1111-111111111111}");

        Assert.Empty(Read().ByDependent);
    }

    /// <summary>No per-user packages is an ordinary machine, not a refusal.</summary>
    [Fact]
    public void AMissingRootIsReadAsNoRegistrations()
    {
        var read = Read(Open(_machine), () => null);

        Assert.Equal((PathPresence.Present, 0), (read.Presence, read.ByDependent.Count));
    }

    [Fact]
    public void ARootWindowsRefusesIsReported()
    {
        Provider(_machine, "Tool.Core", "{11111111-1111-1111-1111-111111111111}", Bundle);

        var read = Read(Open(_machine), () => throw new UnauthorizedAccessException());

        Assert.Equal(PathPresence.Refused, read.Presence);
        Assert.Single(read.ProvidersOf(Bundle));
    }
}
