using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What <c>conan cache clean -s -b -t</c> removes from one package store, and what it leaves, read
/// from the store's folders as Conan lays them out.
///
/// <para><b>The layout</b> is <c>conan/internal/cache/cache.py</c> and
/// <c>conan_reference_layout.py</c>, Conan 2.34.0 (commit
/// <c>70b4cf60b5787ebf1a1af16ac45a11491be0d4c3</c>). A recipe, and a package downloaded for it, each
/// has a folder named by the first five characters of its name and the first thirteen hex digits of a
/// SHA-256: a recipe holds <c>e</c>, <c>es</c>, <c>s</c> and <c>d</c>, a package <c>p</c> and
/// <c>d</c>. A package built here stays where it was built, in a folder of <c>b</c>, holding
/// <c>b</c>, <c>p</c> and <c>d</c>. <c>t</c> holds exports not yet given a revision, and
/// <c>cache.sqlite3</c> is the database that lists everything else.</para>
///
/// <para><b>What goes</b> is <c>CacheAPI.clean</c> in <c>conan/api/subapi/cache.py</c>. <c>-t</c>
/// removes <c>t</c>, and every folder of <c>b</c> whose <c>p</c> lacks <c>conanmanifest.txt</c> or
/// <c>conaninfo.txt</c>, which is a build that never finished. <c>-s</c> removes each recipe's
/// <c>s</c> and <c>-b</c> each package's <c>b</c>, wherever the database says the package is.</para>
///
/// <para><b>§5.2: <c>-t</c> goes by shape, so Deguffer goes by name.</b> Conan removes <c>t</c> whole,
/// and every folder of <c>b</c> without a finished package in it whatever the folder is called, in
/// whichever folder <c>core.cache:storage_path</c> names. A store pointed at a folder that already
/// held a <c>b</c> or a <c>t</c> would lose what was in them. So every entry of <c>t</c> and every
/// folder of <c>b</c> must carry the name Conan gives the folders it makes there
/// (<c>create_export_recipe_layout</c> and <c>create_build_pkg_layout</c>), and be a plain folder,
/// because Conan makes no links. Anything else, a linked <c>t</c> or <c>b</c>, or one that will not
/// be listed, is <see cref="ConanStoreReading.Unrecognised"/>: Tier 4, and the reason the clean is not
/// offered. So is a recipe or package folder that is a link: <c>-s</c> and <c>-b</c> remove the
/// <c>s</c> and <c>b</c> inside it by a path through it, and <c>rmdir</c>'s <c>os.path.isdir</c>
/// follows the link (<c>conan/internal/util/files.py</c>), so they would empty a folder in a tree
/// nobody here read.</para>
///
/// <para><b>An estimate, in the direction of too much.</b> The database is not read, so a folder Conan
/// no longer lists is measured though Conan will not touch it. The executor's after-measure says what
/// the run really freed.</para>
/// </summary>
internal static partial class ConanStoreContents
{
    /// <summary><c>PkgCache.builds_folder</c>.</summary>
    public const string BuildsFolder = "b";

    /// <summary><c>PkgCache.temp_folder</c>.</summary>
    public const string TempFolder = "t";

    /// <summary>
    /// The database <c>PkgCache</c> opens in the store, creating it where it is missing, at the start
    /// of every Conan command.
    /// </summary>
    public const string Database = "cache.sqlite3";

    public const string SourceFolder = "s";

    public const string BuildFolder = "b";

    private const string PackageFolder = "p";

    private const string KeptByConan =
        "Kept by Conan's clean, which removes only sources and build folders here. Recipes, package "
        + "binaries, downloads and their metadata stay.";

    private const string Unrecognised =
        "Not something Conan's clean removes, and not recognised as part of Conan's store.";

    public static ConanStoreReading Read(string store, CancellationToken ct)
    {
        var reading = new ConanStoreTally();
        var scan = ChildDirectories.Under(store);

        if (scan.Unreadable)
        {
            return new ConanStoreReading([], [], [], [UnreadableRoot.Note(store)], Unreadable: true)
            {
                HeldTo = new HashSet<ConanStoreEntry> { new(LongPath.Display(store), ConanStoreEntryKind.Unreadable) },
            };
        }

        foreach (var child in scan.Directories.Concat(scan.Links))
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(child.FullName);
            var isLink = child.Attributes.HasFlag(FileAttributes.ReparsePoint);

            if (IsTempOrBuilds(child))
            {
                ReadTempOrBuilds(child, reading, ct);
            }

            // -s and -b go through a linked recipe or package folder to the s or b in it, into a tree
            // nobody here read.
            else if (isLink && ReferenceFolder().IsMatch(child.Name))
            {
                reading.NotConans(path, ConanStoreEntryKind.NotConans);
            }
            else if (isLink)
            {
                reading.Kept(path, CacheLevelWalk.LinkReason);
            }
            else if (ReferenceFolder().IsMatch(child.Name))
            {
                reading.Kept(path, "A recipe or package in Conan's store. Its clean removes only the sources or build folder inside.");
                ReadInside(path, reading);
            }
            else
            {
                reading.Kept(path, Unrecognised);
            }
        }

        return reading.Finish();
    }

    private static bool IsTempOrBuilds(FileSystemInfo child) =>
        child.Name.Equals(TempFolder, StringComparison.OrdinalIgnoreCase)
        || child.Name.Equals(BuildsFolder, StringComparison.OrdinalIgnoreCase);

    private static void ReadTempOrBuilds(FileSystemInfo child, ConanStoreTally reading, CancellationToken ct)
    {
        var path = LongPath.Display(child.FullName);

        // Conan goes through a linked t or b as though it were the folder, into a tree nobody here read.
        if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            reading.NotConans(path, ConanStoreEntryKind.NotConans);
        }
        else if (child.Name.Equals(TempFolder, StringComparison.OrdinalIgnoreCase))
        {
            ReadTemp(path, reading, ct);
        }
        else
        {
            reading.Kept(path, "The folder Conan keeps the packages built on this machine in.");
            ReadLocalBuilds(path, reading, ct);
        }
    }

    /// <summary>
    /// <c>t</c>, which <c>-t</c> removes whole, so it is measured only where everything in it is an
    /// export Conan started.
    /// </summary>
    private static void ReadTemp(string temp, ConanStoreTally reading, CancellationToken ct)
    {
        if (FolderEntries.Of(temp) is not { } entries)
        {
            reading.Unreadable(temp);
            reading.NotConans(temp, ConanStoreEntryKind.Unreadable);
            return;
        }

        var foreign = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (IsConansFolder(entry))
            {
                reading.Saw(path, ConanStoreEntryKind.Export);
            }
            else
            {
                reading.NotConans(path, ConanStoreEntryKind.NotConans);
                foreign++;
            }
        }

        if (foreign == 0)
        {
            reading.Removed(temp, isLink: false);
        }
        else
        {
            reading.Kept(temp, "The folder Conan keeps unfinished exports in, which holds something Conan did not make.");
        }
    }

    /// <summary>
    /// The folders of <c>b</c>, each a package built on this machine. One that finished keeps its
    /// package and loses its build folder, and one that did not goes whole, as <c>-t</c> takes it.
    ///
    /// <para><b>Finished is Conan's test, read as Conan reads it.</b> <c>os.path.exists</c> answers
    /// false where Windows refuses to describe the file, so a manifest Deguffer cannot see is one Conan
    /// cannot see either, and the folder is removed.</para>
    ///
    /// <para>A file in <c>b</c> is not listed: Conan's <c>rmdir</c> passes over anything that is not a
    /// folder.</para>
    /// </summary>
    private static void ReadLocalBuilds(string builds, ConanStoreTally reading, CancellationToken ct)
    {
        var scan = ChildDirectories.Under(builds);

        if (scan.Unreadable)
        {
            reading.Unreadable(builds);
            reading.NotConans(builds, ConanStoreEntryKind.Unreadable);
            return;
        }

        foreach (var build in scan.Directories.Concat(scan.Links))
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(build.FullName);

            if (!IsConansFolder(build))
            {
                reading.NotConans(path, ConanStoreEntryKind.NotConans);
            }
            else if (!Finished(path))
            {
                reading.Saw(path, ConanStoreEntryKind.UnfinishedBuild);
                reading.Removed(path, isLink: false);
            }
            else
            {
                reading.Saw(path, ConanStoreEntryKind.FinishedBuild);
                reading.Kept(path, "A package Conan built on this machine, which may exist nowhere else. Its clean removes only the build folder inside.");
                ReadInside(path, reading);
            }
        }
    }

    private static bool IsConansFolder(FileSystemInfo entry) =>
        entry is DirectoryInfo
        && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
        && ReferenceFolder().IsMatch(entry.Name);

    private static bool Finished(string build) =>
        LongPath.ProbeEntry(Path.Combine(build, PackageFolder, "conanmanifest.txt")) is PathPresence.Present
        && LongPath.ProbeEntry(Path.Combine(build, PackageFolder, "conaninfo.txt")) is PathPresence.Present;

    /// <summary>
    /// One recipe's or package's folder: <c>s</c> and <c>b</c> go, and everything beside them stays.
    /// A recipe has no <c>b</c> and a package no <c>s</c>, so the two names need no telling apart.
    /// </summary>
    private static void ReadInside(string folder, ConanStoreTally reading)
    {
        var scan = ChildDirectories.Under(folder);

        if (scan.Unreadable)
        {
            reading.Unreadable(folder);
            return;
        }

        foreach (var child in scan.Directories.Concat(scan.Links))
        {
            var path = LongPath.Display(child.FullName);
            var isLink = child.Attributes.HasFlag(FileAttributes.ReparsePoint);

            if (child.Name.Equals(SourceFolder, StringComparison.OrdinalIgnoreCase)
                || child.Name.Equals(BuildFolder, StringComparison.OrdinalIgnoreCase))
            {
                reading.Removed(path, isLink);
            }
            else
            {
                reading.Kept(path, isLink ? CacheLevelWalk.LinkReason : KeptByConan);
            }
        }
    }

    /// <summary>
    /// <c>PkgCache._get_path</c>, and the same shape for a folder of <c>t</c> or <c>b</c>: up to five
    /// characters of a reference name, which Conan allows to be letters, digits and <c>_ + . -</c>,
    /// then the first thirteen lower-case hex digits of a SHA-256. A build folder hashes a random UUID
    /// in as well, which changes the digits and not the shape.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_+.-]{0,4}[0-9a-f]{13}$")]
    private static partial Regex ReferenceFolder();
}
