using Deguffer.Core.Safety;

namespace Deguffer.Core.InstalledApps;

/// <summary>
/// What the machine says about a path an entry names, in every view Windows could mean by it
/// (§7.3).
///
/// <para>An entry names a path as its installer saw it, and this process may see another. A 32-bit
/// Deguffer probes <c>System32</c> through the redirection to <c>SysWOW64</c>, and a 64-bit Deguffer
/// expands a 32-bit entry's <c>%ProgramFiles%</c> to the 64-bit folder. Absence in one view proves
/// nothing, so a path counts as absent only where every view of it is absent.</para>
/// </summary>
public sealed class InstalledPaths(IPathProbe probe, ISystemDirectories system)
{
    /// <summary>The three names of the Windows system folder: native, 32-bit, and native as a 32-bit process reaches it.</summary>
    private readonly IReadOnlyList<string> _systemFolders = Named(
        ["System32", "SysWOW64", "Sysnative"],
        name => Path.Combine(system.WindowsDirectory, name));

    /// <summary>The two program folders. The 32-bit one is empty on a 32-bit Windows and is left out.</summary>
    private readonly IReadOnlyList<string> _programFolders = Named([system.ProgramFiles, system.ProgramFilesX86], folder => folder);

    /// <summary>Whether a file is at <paramref name="path"/> in any view.</summary>
    public PathPresence ProbeFile(string path) => InAnyView(path, probe.ProbeFile);

    /// <summary>Whether a file or a directory is at <paramref name="path"/> in any view.</summary>
    public PathPresence ProbeEntry(string path) => InAnyView(path, probe.ProbeEntry);

    /// <summary>
    /// Why an absent <paramref name="path"/> proves nothing, or null where its absence is proof.
    ///
    /// <para>Windows answers "absent" for every path on a drive that is not connected, and for every
    /// path through a link to one: <c>C:\Games</c> a junction to an unplugged <c>E:\</c> reads as
    /// absent while <c>C:\</c> answers. So each view's drive or share must answer, and no folder on
    /// the way down may be a link or refuse to say what it is.</para>
    /// </summary>
    public string? WhyAbsenceProvesNothing(string path)
    {
        foreach (var view in ViewsOf(path))
        {
            var root = Path.GetPathRoot(view);

            if (string.IsNullOrEmpty(root) || probe.ProbeDirectory(root, out _) is not PathPresence.Present)
            {
                return $"{view} is on {root}, which is not connected or will not answer, so Deguffer cannot tell whether the program is gone.";
            }

            if (DerivedPath.FirstObstacleBetween(root, view, probe) is { } obstacle)
            {
                return obstacle.IsLink
                    ? $"{obstacle.Path} is a link, and where it leads may not be connected, so Deguffer cannot tell whether the program is gone."
                    : $"Windows would not say what {obstacle.Path} is, so Deguffer cannot tell whether the program is gone.";
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="path"/> itself first, then the same path under each other folder that one
    /// view of the machine names differently from another.
    /// </summary>
    public IReadOnlyList<string> ViewsOf(string path)
    {
        var views = new List<string> { path };

        AddViews(views, path, _systemFolders);
        AddViews(views, path, _programFolders);

        return views;
    }

    private static IReadOnlyList<string> Named(IEnumerable<string> folders, Func<string, string> path) =>
        [.. folders.Where(folder => folder.Length > 0).Select(folder => Path.TrimEndingDirectorySeparator(path(folder)))];

    private static void AddViews(List<string> views, string path, IReadOnlyList<string> folders)
    {
        if (folders.FirstOrDefault(folder => IsUnder(path, folder)) is not { } under)
        {
            return;
        }

        var rest = path[under.Length..];

        foreach (var folder in folders)
        {
            var view = folder + rest;

            if (!views.Contains(view, StringComparer.OrdinalIgnoreCase))
            {
                views.Add(view);
            }
        }
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && (path.Length == folder.Length || path[folder.Length] is '\\' or '/');

    /// <summary>Present where any view is, then refused where any view is, and absent only where every view is.</summary>
    private PathPresence InAnyView(string path, Func<string, PathPresence> ask)
    {
        var answer = PathPresence.Absent;

        foreach (var view in ViewsOf(path))
        {
            switch (ask(view))
            {
                case PathPresence.Present:
                    return PathPresence.Present;

                case PathPresence.Refused:
                    answer = PathPresence.Refused;
                    break;
            }
        }

        return answer;
    }
}
