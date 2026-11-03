using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One build folder in an edition's <c>Apps</c> folder.</summary>
/// <param name="Name">The folder's name, which is the build's four-part version.</param>
/// <param name="Path">The folder.</param>
/// <param name="Number">The version, for ordering.</param>
/// <param name="IsLink">Whether the folder is a junction or a symbolic link, which is named and never followed.</param>
public sealed record CapCutBuild(string Name, string Path, Version Number, bool IsLink);

/// <summary>Why every build of an edition is left alone.</summary>
public enum CapCutBuildDoubt
{
    None,

    /// <summary>Windows would not list the <c>Apps</c> folder.</summary>
    AppsUnreadable,

    /// <summary>The launcher's record could not be read, or names no build. See <see cref="CapCutLaunchRecord"/>.</summary>
    RecordNotRead,

    /// <summary>The record names a build whose folder is not there, so the launcher's choice is not known.</summary>
    RecordNamesMissingBuild,

    /// <summary>
    /// A build's number is too large to order, or two build folders name one number
    /// (<c>6.6.0.2610</c> and <c>06.6.0.2610</c>), so which build is newest is not known.
    /// </summary>
    UnorderableBuild,

    /// <summary>
    /// <c>Apps</c>, or the editor's folder above it, is a junction or a symbolic link. What it points
    /// at was never classified, and every survivor §5.6 asserts would be checked through the same
    /// link, so the check would prove nothing about the tree that changed.
    /// </summary>
    LinkedFolder,
}

/// <summary>
/// What one edition's <c>Apps</c> folder holds: every build, the one the launcher starts, and the ones
/// it has superseded.
///
/// <para><b>Two builds are kept, never one.</b> The launcher starts the build its record names, which
/// is what decides "current". The newest build is kept as well, whatever the record says: a record
/// pinned to an old build by a version-lock tool, or an update that finished writing the new build and
/// not yet the record, both leave the newest one as the build the next start may use. Keeping one more
/// build than needed costs space. Removing the wrong one breaks the editor.</para>
///
/// <para><b>A build is recognised by its name alone</b>, four numbers separated by dots, as the
/// version-lock tools and Jianying Pro's own updater test it. Anything else in <c>Apps</c> is the
/// launcher, the uninstaller, the record, a folder an update is still writing (named by a timestamp),
/// or something unknown, and is never a target (§5.2).</para>
/// </summary>
/// <param name="Edition">Which editor.</param>
/// <param name="Apps">Its <c>Apps</c> folder.</param>
/// <param name="Builds">Every build folder, ordered oldest first.</param>
/// <param name="Record">What the launcher's record said.</param>
/// <param name="Doubt">Why every build is left alone, or <see cref="CapCutBuildDoubt.None"/>.</param>
public sealed record CapCutBuilds(
    CapCutEdition Edition,
    string Apps,
    IReadOnlyList<CapCutBuild> Builds,
    CapCutLaunchRecord Record,
    CapCutBuildDoubt Doubt)
{
    private static readonly Regex BuildName = new(@"^\d+\.\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);

    /// <summary>The build the launcher's record names, or null where it names none that is here.</summary>
    public CapCutBuild? Current => Record.State is CapCutRecordState.Named
        ? Builds.FirstOrDefault(build => build.Name.Equals(Record.Version, StringComparison.OrdinalIgnoreCase))
        : null;

    /// <summary>The highest-numbered build.</summary>
    public CapCutBuild? Newest => Builds.Count == 0 ? null : Builds[^1];

    /// <summary>
    /// The builds that may go: every one except <see cref="Current"/> and <see cref="Newest"/>, and none
    /// at all while there is a <see cref="Doubt"/>. Links are included, so the plan can name them.
    /// </summary>
    public IReadOnlyList<CapCutBuild> Superseded => Doubt is not CapCutBuildDoubt.None
        ? []
        : [.. Builds.Where(build => build != Current && build != Newest)];

    /// <summary>The edition's builds, or null where it has no <c>Apps</c> folder.</summary>
    public static CapCutBuilds? Look(IUserEnvironment environment, CapCutEdition edition)
    {
        var apps = CapCutLayout.AppsIn(environment, edition);

        if (LongPath.ProbeDirectory(apps, out var appsIsLink) is PathPresence.Absent)
        {
            return null;
        }

        // A link is declined before anything below it is listed. An answer that could not say is
        // not a link, and the listing that follows reports the folder it could not read.
        LongPath.ProbeDirectory(CapCutLayout.FolderIn(environment, edition), out var folderIsLink);

        if (appsIsLink is true || folderIsLink is true)
        {
            return new CapCutBuilds(edition, apps, [], new CapCutLaunchRecord(CapCutRecordState.Absent, null), CapCutBuildDoubt.LinkedFolder);
        }

        var children = ChildDirectories.Under(apps, static name => BuildName.IsMatch(name));
        var record = CapCutLaunchRecord.In(apps);

        var named = children.Directories.Select(d => (d.Name, IsLink: false))
            .Concat(children.Links.Select(d => (d.Name, IsLink: true)))
            .Select(child => (child.Name, child.IsLink, Number: Version.TryParse(child.Name, out var number) ? number : null))
            .ToList();

        var builds = named
            .Where(child => child.Number is not null)
            .Select(child => new CapCutBuild(child.Name, Path.Combine(apps, child.Name), child.Number!, child.IsLink))
            .OrderBy(build => build.Number)
            .ToList();

        var installation = new CapCutBuilds(edition, apps, builds, record, CapCutBuildDoubt.None);

        var doubt = children.Unreadable ? CapCutBuildDoubt.AppsUnreadable
            : named.Count != builds.Count || builds.DistinctBy(build => build.Number).Count() != builds.Count
                ? CapCutBuildDoubt.UnorderableBuild
            : record.State is not CapCutRecordState.Named ? CapCutBuildDoubt.RecordNotRead
            : installation.Current is null ? CapCutBuildDoubt.RecordNamesMissingBuild
            : CapCutBuildDoubt.None;

        return installation with { Doubt = doubt };
    }
}
