namespace Deguffer.Core.Providers;

/// <summary>
/// What inside one recognised build directory a row offers, for a kind whose directory is not removed
/// whole. See <see cref="BuildDirectoryKind.Parts"/>.
///
/// <para>§5.2 inside a project. A directory a tool writes into can still hold what something else put
/// there: Cargo's <c>target</c> has been measured holding a project's release archives, its screenshots
/// and a toolchain its build scripts downloaded, beside what Cargo wrote. The parts are what the tool is
/// recognised to have written, and everything else is named in <see cref="LeftAlone"/> so that §5.6
/// asserts it is still standing afterwards.</para>
/// </summary>
/// <param name="Offered">The parts this row removes.</param>
/// <param name="LeftAlone">
/// Entries inside the directory this row neither removes nor leaves to another row, each with the
/// sentence saying why. Never an entry another row offers: a path one plan asserts and another plan in
/// the same run removes reads as a §5.6 failure.
/// </param>
/// <param name="Unreadable">
/// Directories inside it that could not be listed, so whatever they hold was neither offered nor
/// named.
/// </param>
public sealed record BuildDirectoryParts(
    IReadOnlyList<BuildDirectoryPart> Offered,
    IReadOnlyList<(string Path, string Reason)> LeftAlone,
    IReadOnlyList<string> Unreadable);

/// <summary>One part of a build directory that a row removes.</summary>
/// <param name="Path">The part, in display form.</param>
/// <param name="What">
/// What it is, completing "… for &lt;project&gt;" in the step's description, such as
/// "Rust build output (release)".
/// </param>
/// <param name="OwnedElsewhere">
/// Entries inside it that another row offers. Where there are any, the part is emptied in place around
/// them rather than removed, and neither taken nor counted here, so one entry is offered on one row.
/// </param>
public sealed record BuildDirectoryPart(string Path, string What, IReadOnlyList<string> OwnedElsewhere);
