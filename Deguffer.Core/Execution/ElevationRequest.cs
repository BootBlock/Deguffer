using Deguffer.Core.Duplicates;

namespace Deguffer.Core.Execution;

/// <summary>
/// What a replacement, elevated instance is asked to do when it starts.
///
/// <para>§6.3 deliberately does not elevate at startup, so an elevated instance is always a
/// replacement for one the user was already using. Everything that instance knew goes with the
/// process it stood down, so whatever has to survive travels on the command line — and this is both
/// halves of that: what the standing-down process writes, and what the replacement reads back.
/// Keeping the two in one type is what stops them drifting apart, because a switch written in one
/// place and read in another fails silently when only one of them is changed.</para>
///
/// <para><see cref="ElevationOffer"/> decides whether to offer the relaunch at all. This says what
/// the relaunch carries.</para>
///
/// <para>In Core rather than in the shell for the usual reason: none of it needs a window, and a
/// decision belongs in Core (G1).</para>
/// </summary>
public abstract record ElevationRequest
{
    /// <summary>
    /// Open on the Storage page and preview again. Stateless, so one instance serves every caller
    /// (G5).
    /// </summary>
    public static ElevationRequest Preview { get; } = new PreviewRequest();

    /// <summary>Open on the Installed apps page and read the entries again. Stateless, as <see cref="Preview"/> is.</summary>
    public static ElevationRequest InstalledApps { get; } = new InstalledAppsRequest();

    /// <summary>The arguments that ask a replacement instance to do this.</summary>
    public abstract IReadOnlyList<string> ToArguments();

    /// <summary>
    /// What <paramref name="arguments"/> asks for, or null where it asks for nothing — which is
    /// every ordinary launch, since a user starting Deguffer passes none of these.
    ///
    /// <para>Pass the arguments only. <see cref="Environment.GetCommandLineArgs"/> puts the
    /// executable's own path at index 0, and a launch is not a request because of where it was
    /// started from.</para>
    /// </summary>
    public static ElevationRequest? From(IEnumerable<string> arguments)
    {
        var explore = false;
        var preview = false;
        var installedApps = false;
        var duplicates = false;
        List<SearchLocation> locations = [];
        string? drive = null;
        string? folder = null;

        foreach (var argument in arguments)
        {
            if (argument.Equals(ExploreSwitch, StringComparison.OrdinalIgnoreCase))
            {
                explore = true;
            }
            else if (argument.Equals(PreviewSwitch, StringComparison.OrdinalIgnoreCase))
            {
                preview = true;
            }
            else if (argument.Equals(InstalledAppsSwitch, StringComparison.OrdinalIgnoreCase))
            {
                installedApps = true;
            }
            else if (argument.Equals(DuplicatesSwitch, StringComparison.OrdinalIgnoreCase))
            {
                duplicates = true;
            }
            else if (argument.StartsWith(SearchPrefix, StringComparison.OrdinalIgnoreCase))
            {
                AddLocation(ValueOf(argument, SearchPrefix), LocationRole.Search);
            }
            else if (argument.StartsWith(ReferencePrefix, StringComparison.OrdinalIgnoreCase))
            {
                AddLocation(ValueOf(argument, ReferencePrefix), LocationRole.Reference);
            }
            else if (argument.StartsWith(DrivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                drive = ValueOf(argument, DrivePrefix);
            }
            else if (argument.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                folder = ValueOf(argument, FolderPrefix);
            }
        }

        // Explore wins where both are somehow present: it is the more specific request, and nothing
        // writes the two together. The alternative silently starts a whole-machine preview the user
        // did not ask for. Duplicates and Installed apps come before a preview for the same reason.
        return explore ? new ExploreRequest(drive, folder)
            : duplicates ? new DuplicatesRequest(locations)
            : installedApps ? InstalledApps
            : preview ? Preview
            : null;

        void AddLocation(string? path, LocationRole role)
        {
            if (path is not null)
            {
                locations.Add(new SearchLocation(path, role));
            }
        }
    }

    /// <summary>
    /// The Explore page's own marker, carried even where neither path below it is. Without it a
    /// request naming no drive and no folder would decode as no request at all, and the elevated
    /// window would open somewhere the user did not leave it.
    /// </summary>
    private protected const string ExploreSwitch = "--explore";

    private protected const string PreviewSwitch = "--rescan";

    private protected const string InstalledAppsSwitch = "--installed-apps";

    private protected const string DuplicatesSwitch = "--duplicates";

    private protected const string SearchPrefix = "--duplicates-search=";

    private protected const string ReferencePrefix = "--duplicates-reference=";

    private protected const string DrivePrefix = "--explore-drive=";

    private protected const string FolderPrefix = "--explore-folder=";

    /// <summary>
    /// What follows the prefix, or null where nothing does. A value that is empty, or nothing but
    /// spaces, is a missing one rather than a path.
    ///
    /// <para>Taking one literally points the scan at a path that cannot exist, and
    /// <see cref="Deguffer.Core.Exploring.ExploreScanner"/> rejects such a path by throwing.
    /// Measured, on a launch that scans on its own: nothing awaits that exception, it reaches the
    /// shell's last-resort handler, and the process ends — the handler deliberately does not mark
    /// a fault handled. Reading the value as absent instead leaves the page waiting, which is what
    /// it does for every other request it cannot honour.</para>
    /// </summary>
    private static string? ValueOf(string argument, string prefix) =>
        argument[prefix.Length..] is var value && !string.IsNullOrWhiteSpace(value) ? value : null;
}

/// <summary>Open on the Installed apps page. See <see cref="ElevationRequest.InstalledApps"/>.</summary>
public sealed record InstalledAppsRequest : ElevationRequest
{
    public override IReadOnlyList<string> ToArguments() => [InstalledAppsSwitch];
}

/// <summary>
/// Open on the Duplicates page with the locations the previous instance was given, in their roles and
/// their order, and search them, so the elevated search reads each volume's file table rather than
/// walking (§5.5). The criteria and filters are preferences, so they are already where the new
/// instance reads them.
///
/// <para>Each location is its own argument, its role in its switch, so a path holding a comma, a
/// quote or a space reaches the replacement whole. A location given in both roles travels twice, as
/// it was given, and the search makes it a reference.</para>
/// </summary>
public sealed record DuplicatesRequest : ElevationRequest
{
    public DuplicatesRequest(IReadOnlyList<SearchLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);

        Locations = [.. locations];
    }

    public IReadOnlyList<SearchLocation> Locations { get; }

    public override IReadOnlyList<string> ToArguments() =>
    [
        DuplicatesSwitch,
        .. Locations.Select(location => (location.Role == LocationRole.Reference ? ReferencePrefix : SearchPrefix) + location.Path),
    ];

    public bool Equals(DuplicatesRequest? other) => other is not null && Locations.SequenceEqual(other.Locations);

    public override int GetHashCode() => Locations.Count;
}

/// <summary>Open on the Storage page and preview again. See <see cref="ElevationRequest.Preview"/>.</summary>
public sealed record PreviewRequest : ElevationRequest
{
    public override IReadOnlyList<string> ToArguments() => [PreviewSwitch];
}

/// <summary>
/// Open on the Explore page, pointed where the previous instance was pointed, and scan.
///
/// <para>Both halves of the choice travel, not just the one the scan used. The drive box and the
/// folder beside it state one choice between them, and restoring only the folder would leave the
/// box naming a volume the user never selected.</para>
/// </summary>
/// <param name="Drive">The drive the picker was showing, or null where it was showing none.</param>
/// <param name="Folder">The folder the scan was scoped to, or null for the whole drive.</param>
public sealed record ExploreRequest(string? Drive, string? Folder) : ElevationRequest
{
    public override IReadOnlyList<string> ToArguments()
    {
        var arguments = new List<string>(3) { ExploreSwitch };

        if (Drive is not null)
        {
            arguments.Add(DrivePrefix + Drive);
        }

        if (Folder is not null)
        {
            arguments.Add(FolderPrefix + Folder);
        }

        return arguments;
    }
}
