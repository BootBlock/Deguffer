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
/// <para>In Core rather than in the shell for the usual reason: none of it needs a window, and the
/// shell has no test project (G8).</para>
/// </summary>
public abstract record ElevationRequest
{
    /// <summary>
    /// Open on the Storage page and preview again. Stateless, so one instance serves every caller
    /// (G5).
    /// </summary>
    public static ElevationRequest Preview { get; } = new PreviewRequest();

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
        // did not ask for.
        return explore ? new ExploreRequest(drive, folder)
            : preview ? Preview
            : null;
    }

    /// <summary>
    /// The Explore page's own marker, carried even where neither path below it is. Without it a
    /// request naming no drive and no folder would decode as no request at all, and the elevated
    /// window would open somewhere the user did not leave it.
    /// </summary>
    private protected const string ExploreSwitch = "--explore";

    private protected const string PreviewSwitch = "--rescan";

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
