using System.Collections.Frozen;

namespace Deguffer.Core.Providers;

/// <summary>Which of the three directories inside a <c>.vs</c> folder an entry sits in.</summary>
internal enum VisualStudioLevel
{
    /// <summary>The <c>.vs</c> folder itself, shared by every solution beside it.</summary>
    Folder,

    /// <summary>
    /// <c>.vs\&lt;solution&gt;</c>, named from a solution file beside the <c>.vs</c>: without its
    /// extension for a <c>.sln</c>, and with it for a <c>.slnx</c>.
    /// </summary>
    Solution,

    /// <summary><c>.vs\&lt;solution&gt;\v&lt;N&gt;</c>, one for each Visual Studio version that opened it.</summary>
    Version,
}

/// <summary>One cache Visual Studio keeps in a <c>.vs</c> folder and rebuilds by itself.</summary>
/// <param name="Name">The entry's name at its level.</param>
/// <param name="Level">The directory it sits in.</param>
/// <param name="Kind">Whether it is a folder or a file. An entry of the other kind is not this cache.</param>
/// <param name="Description">What it is, as a noun phrase that completes "… for MySolution".</param>
/// <param name="Reason">Why it can go, written for the user.</param>
/// <param name="Evidence">
/// Whether an entry of this name holds what the cache holds. See <see cref="VisualStudioCacheEvidence"/>.
/// </param>
internal sealed record VisualStudioCache(
    string Name,
    VisualStudioLevel Level,
    TargetKind Kind,
    string Description,
    string Reason,
    Func<string, bool> Evidence);

/// <summary>
/// §5.2 for a <c>.vs</c> folder: the caches Visual Studio rebuilds by itself, and nothing else in it.
///
/// <para><b>Most of a <c>.vs</c> folder is not a cache.</b> Measured across 42 of them, the caches were
/// about 165 MB of 569 MB, and Copilot's file snapshots alone were 253 MB, beside Copilot
/// conversations, solution options and window layouts. So the folder is never removed whole. Each cache is named at the one level it was
/// observed at, every other entry is Tier 4 by construction, and the kept entries a reader would ask
/// about are named with the reason they stay. One table in one file answers "what may this row
/// delete?".</para>
///
/// <para>Where the C++ browsing database and <c>ipch</c> are moved to the <em>Fallback Location</em>
/// they are in <c>%TEMP%</c>, and the temporary-folders row reaches them there.</para>
/// </summary>
internal static class VisualStudioCacheLayout
{
    /// <summary>The name Visual Studio gives the folder beside a solution.</summary>
    public const string FolderName = ".vs";

    /// <summary>The oldest and newest version folders observed: Visual Studio 2015 to Visual Studio 2026.</summary>
    private const int OldestVersion = 14;

    private const int NewestVersion = 18;

    public const string UnknownReason = "Not something Visual Studio rebuilds by itself, so it is left alone.";

    public const string NoSolutionReason =
        "No solution beside this .vs folder has this name, so it is left alone.";

    public const string UnknownVersionReason =
        "Kept by a version of Visual Studio Deguffer does not know, so it is left alone.";

    public static IReadOnlyList<VisualStudioCache> Caches { get; } =
    [
        new(
            "ProjectEvaluation",
            VisualStudioLevel.Folder,
            TargetKind.Directory,
            "Project evaluation cache",
            "How MSBuild evaluated the projects, kept so the solution loads faster. Visual Studio "
            + "evaluates them again the next time it opens the solution.",
            VisualStudioCacheEvidence.ProjectEvaluation),
        new(
            "FileContentIndex",
            VisualStudioLevel.Solution,
            TargetKind.Directory,
            "Find in Files index",
            "The index Find in Files searches. Visual Studio builds it again in the background.",
            VisualStudioCacheEvidence.FileContentIndex),
        new(
            "DesignTimeBuild",
            VisualStudioLevel.Solution,
            TargetKind.Directory,
            "Design-time build cache",
            "What the last design-time builds found. Without it, Visual Studio runs those builds "
            + "again when the solution opens.",
            VisualStudioCacheEvidence.DesignTimeBuild),
        new(
            "ipch",
            VisualStudioLevel.Version,
            TargetKind.Directory,
            "C++ precompiled header cache",
            "Headers IntelliSense compiled ahead. It compiles them again as you open files.",
            VisualStudioCacheEvidence.PrecompiledHeaders),
        new(
            "Browse.VC.db",
            VisualStudioLevel.Version,
            TargetKind.File,
            "C++ browsing database",
            "The C++ database that Go To Definition and Class View read. Visual Studio builds it "
            + "again from the source, as its Recreate Database command does.",
            VisualStudioCacheEvidence.Database),
        new(
            "Solution.VC.db",
            VisualStudioLevel.Version,
            TargetKind.File,
            "C++ solution database",
            "The C++ database of the solution's projects and files. Visual Studio builds it again "
            + "from the solution.",
            VisualStudioCacheEvidence.Database),
    ];

    /// <summary>
    /// The entries a reader would ask about, with why each stays. Anything absent stays for
    /// <see cref="UnknownReason"/>, so this list explains decisions and never makes them.
    /// </summary>
    private static readonly FrozenDictionary<(VisualStudioLevel, string), string> Kept =
        new Dictionary<(VisualStudioLevel, string), string>(KeyComparer.Instance)
        {
            [(VisualStudioLevel.Folder, "CopilotSnapshots")] =
                "Copilot's copies of your files from before it changed them. Nothing re-creates them, so they are left alone.",
            [(VisualStudioLevel.Solution, "copilot-chat")] =
                "Your Copilot conversations. Nothing re-creates them, so they are left alone.",
            [(VisualStudioLevel.Solution, "CopilotIndices")] =
                "Copilot's index of your code. Whether Visual Studio rebuilds it by itself is not established, so it is left alone.",
            [(VisualStudioLevel.Solution, "lut")] =
                "Live Unit Testing's record of test runs and coverage. It records runs that already happened, so it is left alone.",
            [(VisualStudioLevel.Solution, "config")] =
                "IIS Express settings for this solution, which you may have edited, so they are left alone.",
            [(VisualStudioLevel.Version, ".suo")] =
                "Your solution options: open documents, breakpoints and other settings. They are left alone.",
            [(VisualStudioLevel.Version, ".wsuo")] =
                "Your solution options: open documents, breakpoints and other settings. They are left alone.",
            [(VisualStudioLevel.Version, "DocumentLayout.json")] =
                "Your window and document layout for this solution. It is left alone.",
            [(VisualStudioLevel.Version, "DocumentLayout.backup.json")] =
                "Your window and document layout for this solution. It is left alone.",
            [(VisualStudioLevel.Version, "TestStore")] =
                "Test Explorer's record of test runs. It is left alone.",
            [(VisualStudioLevel.Version, "Server")] =
                "Probably the language service's own storage. That is not established, so it is left alone.",
        }.ToFrozenDictionary(KeyComparer.Instance);

    /// <summary>The cache an entry of <paramref name="name"/> and <paramref name="kind"/> at <paramref name="level"/> would be, or null.</summary>
    public static VisualStudioCache? CacheNamed(VisualStudioLevel level, string name, TargetKind kind) =>
        Caches.FirstOrDefault(cache =>
            cache.Level == level && cache.Kind == kind && cache.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why an entry no cache claims is left alone.</summary>
    public static string KeptReason(VisualStudioLevel level, string name) =>
        Kept.TryGetValue((level, name), out var reason) ? reason : UnknownReason;

    /// <summary>Why an entry named like a cache, holding something else, is left alone.</summary>
    public static string NotConfirmedReason(VisualStudioCache cache) =>
        $"Named like Visual Studio's {cache.Description.ToLowerInvariant()}, but it holds something "
        + "Deguffer does not recognise, so it is left alone.";

    /// <summary>Whether <paramref name="name"/> is a version folder Visual Studio 2015 to 2026 writes.</summary>
    public static bool IsKnownVersion(string name) =>
        name.Length > 1
        && (name[0] is 'v' or 'V')
        && int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version)
        && version is >= OldestVersion and <= NewestVersion;

    /// <summary>Whether <paramref name="name"/> has the shape of a version folder, known or not.</summary>
    public static bool IsVersionShaped(string name) =>
        name.Length > 1 && (name[0] is 'v' or 'V') && name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>The folder name inside <c>.vs</c> that the solution file <paramref name="solutionFile"/> writes to.</summary>
    public static string SolutionFolderFor(string solutionFile) =>
        Path.GetExtension(solutionFile).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? solutionFile
            : Path.GetFileNameWithoutExtension(solutionFile);

    /// <summary>Levels compare by value and names ignoring case, as Windows names them.</summary>
    private sealed class KeyComparer : IEqualityComparer<(VisualStudioLevel Level, string Name)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((VisualStudioLevel Level, string Name) x, (VisualStudioLevel Level, string Name) y) =>
            x.Level == y.Level && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((VisualStudioLevel Level, string Name) key) =>
            HashCode.Combine(key.Level, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }
}
