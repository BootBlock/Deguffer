using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where vcpkg's directories turned out to be on this machine.
/// </summary>
/// <param name="BinaryCache">
/// The binary cache, or null when nothing points at one. Findable without the clone, because it
/// lives in the profile.
/// </param>
/// <param name="Root">
/// The vcpkg clone, or null when this machine gives no way to find it. Null is the interesting
/// answer: three of the four directories worth reclaiming live inside it, so a null here is a
/// sentence the plan has to say out loud rather than a smaller number nobody can account for.
/// </param>
/// <param name="RelocatedDownloads">
/// The downloads directory when <c>VCPKG_DOWNLOADS</c> has moved it out of the clone, and null when
/// it has not. Null therefore means "wherever the clone is", not "there is none".
/// </param>
public sealed record VcpkgLocations(string? BinaryCache, string? Root, string? RelocatedDownloads);

/// <summary>
/// Finds vcpkg. Separate from the provider for the reason <see cref="ChromiumUserDataDiscovery"/>
/// is: one type answers "where is this tool?" and the other answers "what inside it may go", and
/// keeping them apart is what stops the second question from being asked of a directory that failed
/// the first.
///
/// <para>The split earns itself twice over here, because vcpkg is the first tool Deguffer reaches
/// whose main directory is <em>a git clone the user put wherever they liked</em>. There is no
/// profile location to fall back on, so finding it is three probes with three different failure
/// modes, and none of them may be assumed.</para>
/// </summary>
public sealed class VcpkgDiscovery(IUserEnvironment environment)
{
    /// <summary>Moves the binary cache. The first entry in the documented search order.</summary>
    public const string BinaryCacheVariable = "VCPKG_DEFAULT_BINARY_CACHE";

    /// <summary>Names the clone. The only probe that is a direct statement rather than an inference.</summary>
    public const string RootVariable = "VCPKG_ROOT";

    /// <summary>Moves the downloads directory out of the clone.</summary>
    public const string DownloadsVariable = "VCPKG_DOWNLOADS";

    /// <summary>
    /// What <c>vcpkg integrate install</c> writes into the user's vcpkg directory: the absolute path
    /// of the clone it integrated, so MSBuild can find it later. It is the only record of the
    /// clone's location that exists on disk.
    /// </summary>
    public const string IntegrationFile = "vcpkg.path.txt";

    /// <summary>The user's vcpkg directory, which holds the default binary cache and the integration file.</summary>
    public string ProfileDirectory => Path.Combine(environment.LocalAppData, "vcpkg");

    public VcpkgLocations Discover()
    {
        var root = FindRoot();

        return new VcpkgLocations(FindBinaryCache(), root, FindRelocatedDownloads(root));
    }

    /// <summary>
    /// The documented search order for the default binary cache: the environment variable, then
    /// <c>archives</c> under the local profile directory, then under the roaming one.
    ///
    /// <para><c>VCPKG_BINARY_SOURCES</c> can switch the whole mechanism off or point it at a remote
    /// feed, and it is a small expression language rather than a path. It is not parsed here. The
    /// consequence of ignoring it is bounded and safe: a machine using a remote cache has no local
    /// <c>archives</c> directory, so there is nothing to find and nothing is offered.</para>
    /// </summary>
    private string? FindBinaryCache()
    {
        if (FullyQualified(environment.GetEnvironmentVariable(BinaryCacheVariable)) is { } configured)
        {
            return configured;
        }

        foreach (var profile in (string[])[environment.LocalAppData, environment.RoamingAppData])
        {
            var archives = Path.Combine(profile, "vcpkg", "archives");

            if (LongPath.DirectoryExists(archives))
            {
                return archives;
            }
        }

        return null;
    }

    /// <summary>
    /// The clone, by the three routes that exist, in decreasing order of how directly each says so.
    ///
    /// <list type="number">
    /// <item><c>VCPKG_ROOT</c>, which names it outright.</item>
    /// <item>The integration file, which vcpkg itself wrote and which is exact where it exists.</item>
    /// <item>The directory holding <c>vcpkg</c> on <c>PATH</c>, which is the clone for the ordinary
    /// bootstrap because the executable is built into the root. It is the weakest of the three and
    /// the last tried: a shim, or a copy of the executable somewhere else, would name a directory
    /// that is not a clone at all — which costs nothing, because every location is then declared by
    /// name under it and simply will not be there.</item>
    /// </list>
    /// </summary>
    private string? FindRoot()
    {
        if (FullyQualified(environment.GetEnvironmentVariable(RootVariable)) is { } configured)
        {
            return configured;
        }

        if (ReadIntegrationFile() is { } integrated)
        {
            return integrated;
        }

        return environment.FindExecutable("vcpkg") is { } executable
            ? Path.GetDirectoryName(executable)
            : null;
    }

    private string? FindRelocatedDownloads(string? root)
    {
        if (FullyQualified(environment.GetEnvironmentVariable(DownloadsVariable)) is not { } configured)
        {
            return null;
        }

        // Only "relocated" if it actually left the clone. A variable set to the place vcpkg would
        // have used anyway must not produce a second declaration of the same directory, which §5.6
        // would then report as two survivors and the plan as two steps over one path.
        return root is not null
            && configured.Equals(Path.Combine(root, "downloads"), StringComparison.OrdinalIgnoreCase)
                ? null
                : configured;
    }

    private string? ReadIntegrationFile()
    {
        var file = Path.Combine(ProfileDirectory, IntegrationFile);

        if (!LongPath.FileExists(file))
        {
            return null;
        }

        try
        {
            return FullyQualified(File.ReadAllText(LongPath.Extended(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file vcpkg wrote is unreadable. That is one probe failing, not an error: the next
            // one is tried, and a clone nobody can locate is a sentence the plan already knows how
            // to say.
            return null;
        }
    }

    /// <summary>
    /// A configured value only counts when it is a full path. Every one of these is resolved by
    /// vcpkg against a working directory Deguffer is not, so a relative value has no correct
    /// interpretation here — and reaching into a directory nobody pointed at is exactly the guess
    /// §5.2 forbids.
    /// </summary>
    private static string? FullyQualified(string? value)
    {
        var trimmed = value?.Trim();

        return !string.IsNullOrEmpty(trimmed) && Path.IsPathFullyQualified(trimmed) ? trimmed : null;
    }
}
