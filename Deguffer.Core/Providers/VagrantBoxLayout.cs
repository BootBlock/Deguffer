using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where Vagrant keeps an installed box version on disk, read the way <c>BoxCollection</c> reads it
/// (<c>lib/vagrant/box_collection.rb</c>, hashicorp/vagrant at <c>dc55920a</c>).
///
/// <para><c>boxes\&lt;name&gt;\&lt;version&gt;\&lt;architecture&gt;\&lt;provider&gt;</c> since
/// Vagrant learned architectures, and <c>boxes\&lt;name&gt;\&lt;version&gt;\&lt;provider&gt;</c> for a
/// box added before that. Either is a box only where it holds <c>metadata.json</c>, which is the one
/// test <c>BoxCollection#all</c> applies.</para>
/// </summary>
internal static class VagrantBoxLayout
{
    /// <summary>The file whose presence makes a provider folder a box, for Vagrant and here.</summary>
    private const string MetadataFile = "metadata.json";

    /// <summary>
    /// The folder <c>BoxCollection#dir_name</c> gives a box's logical name, or null where the name
    /// would not be one folder inside <paramref name="boxes"/>.
    ///
    /// <para>A colon becomes <c>-VAGRANTCOLON-</c> on Windows and a slash <c>-VAGRANTSLASH-</c>, in that
    /// order. The name comes from a subprocess's output, so a name that would leave
    /// <paramref name="boxes"/> or name it is refused rather than combined: Vagrant itself reads names
    /// from the folders there, so no name it printed can do either.</para>
    /// </summary>
    public static string? BoxDirectory(string boxes, string name)
    {
        var folder = name.Replace(":", "-VAGRANTCOLON-", StringComparison.Ordinal)
            .Replace("/", "-VAGRANTSLASH-", StringComparison.Ordinal);

        return IsOneFolder(folder) ? Path.Combine(boxes, folder) : null;
    }

    /// <summary>
    /// The provider folders a once-named version is installed in, as the remove action would find
    /// them, or none where Windows would not list its version folder.
    ///
    /// <para><b>An architecture folder wins over a pre-architecture one.</b> <c>BoxCollection#all</c>
    /// skips the old layout's folder where the same version and provider is installed for the
    /// machine's own architecture, so the removal takes that one and leaves the other. A version with
    /// two architectures for one provider is listed twice, and is the caller's to recognise; it is
    /// returned here with both, so a caller that compares counts can see it.</para>
    ///
    /// <para><paramref name="present"/> chooses which <c>metadata.json</c> answers count. A figure
    /// counts only a folder whose record is there; a list of survivors also takes one Windows would not
    /// describe, because a refusal is no evidence that it is not a box.</para>
    /// </summary>
    public static IReadOnlyList<string> ProviderDirectories(
        string boxes,
        VagrantBoxVersion box,
        Func<PathPresence, bool> present)
    {
        if (VersionDirectory(boxes, box) is not { } version || !IsOneFolder(box.Provider))
        {
            return [];
        }

        var listing = ChildDirectories.Under(version);

        if (listing.Unreadable)
        {
            return [];
        }

        var byArchitecture = listing.Directories
            .Select(architecture => Path.Combine(LongPath.Display(architecture.FullName), box.Provider))
            .Where(folder => IsBox(folder, present))
            .ToList();

        if (byArchitecture.Count > 0)
        {
            return byArchitecture;
        }

        var unarchitected = Path.Combine(version, box.Provider);

        return IsBox(unarchitected, present) ? [unarchitected] : [];
    }

    /// <summary>
    /// The folder of one version of a box, or null where a part of it would not be one folder.
    /// </summary>
    public static string? VersionDirectory(string boxes, VagrantBoxVersion box) =>
        BoxDirectory(boxes, box.Name) is { } folder && IsOneFolder(box.Version)
            ? Path.Combine(folder, box.Version)
            : null;

    /// <summary>
    /// Every version folder of the box <paramref name="name"/> except <paramref name="removed"/>:
    /// the newest, a version folder <c>BoxCollection#all</c> declines as malformed, and anything else
    /// prune never names. Each must outlive the run (§5.6).
    /// </summary>
    public static IReadOnlyList<string> OtherVersionDirectories(
        string boxes,
        string name,
        IReadOnlySet<string> removed)
    {
        if (BoxDirectory(boxes, name) is not { } folder)
        {
            return [];
        }

        return [.. Children(folder).Where(version => !removed.Contains(Path.GetFileName(version)))];
    }

    /// <summary>
    /// What sits beside <paramref name="removing"/> inside the version folder of <paramref name="box"/>
    /// and is not removed with it: another provider's folder, another architecture's, and the older
    /// layout's folder that the same version installed for this machine's architecture hides from
    /// <c>BoxCollection#all</c>. Each must outlive the run (§5.6).
    /// </summary>
    /// <param name="removing">Every provider folder the prune removes, this version's and the rest.</param>
    public static IReadOnlyList<string> Beside(string boxes, VagrantBoxVersion box, IReadOnlyCollection<string> removing)
    {
        if (VersionDirectory(boxes, box) is not { } version)
        {
            return [];
        }

        return [.. Children(version).SelectMany(child => Holds(child) ? Children(child).Where(Kept) : Kept(child) ? [child] : [])];

        bool Holds(string folder) => removing.Any(removed => LongPath.Contains(folder, removed) && !Same(folder, removed));

        bool Kept(string folder) => !removing.Any(removed => Same(folder, removed));

        static bool Same(string left, string right) => string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar), right.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Children(string folder)
    {
        var listing = ChildDirectories.Under(folder);

        return listing.Directories.Concat(listing.Links).Select(child => LongPath.Display(child.FullName));
    }

    private static bool IsBox(string folder, Func<PathPresence, bool> present) =>
        present(LongPath.ProbeFile(Path.Combine(folder, MetadataFile)));

    private static bool IsOneFolder(string name) =>
        name.Length > 0
        && name is not ("." or "..")
        && name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, Path.VolumeSeparatorChar]) < 0;
}
