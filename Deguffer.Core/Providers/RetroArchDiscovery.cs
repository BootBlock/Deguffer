using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Every RetroArch found, and what finding them had to say.</summary>
internal sealed record RetroArchFinding(IReadOnlyList<RetroArchInstall> Installs, RetroArchReading Found);

/// <summary>
/// Finds RetroArch, and the settings each copy reads. Shared by the two RetroArch rows, which differ in
/// tier and in what they take and agree on everything here.
///
/// <para><b>Found beside the program, because that is where RetroArch keeps everything.</b> Its Windows
/// defaults are all relative to the folder <c>retroarch.exe</c> is in, so a path in the profile finds
/// nothing. A copy is looked for in each folder the user declared under Emulator folders, and in the
/// folder Steam's own manifest says it installed RetroArch in. The installer's records are not read:
/// what it writes to the registry was not established, and a guess about where a program is would be a
/// guess about what to delete.</para>
///
/// <para><b>A copy is proven by the program, not by a settings file.</b> The <c>:\</c> every default
/// begins with means the program's folder, so only a folder holding <c>retroarch.exe</c> makes those
/// paths mean anything. A settings file on its own is found only in <c>%APPDATA%</c>, and only its full
/// paths are followed.</para>
///
/// <para><b>Which settings a copy reads, in RetroArch's own order.</b> A <c>retroarch.cfg</c> beside
/// the program wins whenever it exists, whatever is in it. Otherwise <c>%APPDATA%\retroarch.cfg</c>,
/// with no <c>RetroArch</c> folder between, although the folder is widely written about. Where neither
/// exists RetroArch has not yet run from there, and every folder is its default.</para>
/// </summary>
public sealed class RetroArchDiscovery
{
    /// <summary>RetroArch's Steam application id.</summary>
    public const string SteamAppId = "1118310";

    private readonly IUserEnvironment _environment;
    private readonly SteamDiscovery _steam;
    private readonly EmulatorFolderStore _folders;
    private readonly ISystemDirectories _system;
    private readonly List<RetroArchProviderBase> _rows = [];
    private RetroArchFinding? _finding;

    public RetroArchDiscovery(
        IUserEnvironment environment,
        SteamDiscovery? steam = null,
        ISystemDirectories? system = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _environment = environment;
        _steam = steam ?? new SteamDiscovery(environment);
        _folders = new EmulatorFolderStore(environment);
        _system = system ?? SystemDirectories.Current;
    }

    /// <summary>
    /// The rows reading this finding, so each can declare to Explore what all of them offer. See
    /// <see cref="RetroArchProviderBase.DiscoverToolRootsAsync"/>.
    /// </summary>
    internal IReadOnlyList<RetroArchProviderBase> Rows => _rows;

    internal void Enlist(RetroArchProviderBase row) => _rows.Add(row);

    /// <summary>The settings file RetroArch reads where none is beside the program.</summary>
    private string ApplicationDataSettings => Path.Combine(_environment.RoamingAppData, RetroArchInstall.SettingsFileName);

    /// <summary>
    /// Drop the memoised finding, so a RetroArch installed, or a folder declared, while the app was open
    /// is seen.
    /// </summary>
    public void Invalidate()
    {
        _finding = null;
        _steam.Invalidate();
    }

    /// <summary>
    /// Every copy, memoised for a planning pass (G4): both rows, their presence and Explore's refusals
    /// all ask it. A pass that is cancelled throws before it is kept, so the next caller looks again.
    /// </summary>
    internal RetroArchFinding Find(CancellationToken ct) => _finding ??= Search(ct);

    /// <summary>
    /// Why <paramref name="folder"/> must not be treated as RetroArch's, as the end of a sentence, or
    /// null where it may be. A drive's root, or a folder holding the profile or Windows, is never one
    /// program's to answer for, whatever is in it.
    /// </summary>
    internal string? WhyNotOwned(string folder) =>
        ConfiguredFolder.WhyNotOwned(folder, _environment, _system, TempRoots.Resolve(_environment, _system).AccountFolders);

    private RetroArchFinding Search(CancellationToken ct)
    {
        var found = new RetroArchReading();
        var installs = new List<RetroArchInstall>();
        var usedApplicationData = false;

        foreach (var candidate in Candidates(found).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (Program(candidate, found) is not { } program)
            {
                continue;
            }

            var beside = Path.Combine(program, RetroArchInstall.SettingsFileName);

            switch (LongPath.ProbeFile(beside))
            {
                case PathPresence.Present:
                    if (Settings(beside, found) is { } own)
                    {
                        installs.Add(new RetroArchInstall(program, own));
                    }

                    continue;

                case PathPresence.Refused:
                    found.Unreached(beside);
                    continue;
            }

            switch (LongPath.ProbeFile(ApplicationDataSettings))
            {
                case PathPresence.Present:
                    usedApplicationData = true;

                    if (Settings(ApplicationDataSettings, found) is { } shared)
                    {
                        installs.Add(new RetroArchInstall(program, shared));
                    }

                    break;

                case PathPresence.Refused:
                    usedApplicationData = true;
                    found.Unreached(ApplicationDataSettings);
                    break;

                default:
                    installs.Add(new RetroArchInstall(program, null));
                    break;
            }
        }

        // Settings no program found was reading: RetroArch was run from somewhere nobody declared. Its
        // full paths still say where its folders are.
        if (!usedApplicationData && LongPath.ProbeFile(ApplicationDataSettings) is PathPresence.Present
            && Settings(ApplicationDataSettings, found) is { } orphaned)
        {
            installs.Add(new RetroArchInstall(null, orphaned));
        }

        return new RetroArchFinding(installs, found);
    }

    /// <summary>The folders a RetroArch program may be in: each declared folder, and Steam's copy.</summary>
    private IEnumerable<string> Candidates(RetroArchReading found)
    {
        foreach (var folder in _folders.Load())
        {
            yield return folder;
        }

        if (_steam.Libraries is not { } libraries)
        {
            yield break;
        }

        var (folders, unread) = new SteamAppManifests(libraries).InstallFoldersOf(SteamAppId);

        foreach (var manifest in unread)
        {
            found.Unread(manifest, "it could not tell whether Steam installed RetroArch in that library.");
        }

        foreach (var (library, folder) in folders)
        {
            if (LongPath.Configured(folder) is not { } configured)
            {
                continue;
            }

            // Built from the library and fixed names, so a link at any folder between would put the
            // copy somewhere nothing established, with every survivor resolving through the same link.
            if (DerivedPath.FirstObstacleBetween(library, configured) is { } obstacle)
            {
                if (obstacle.IsLink)
                {
                    found.DeclineLink(obstacle.Path);
                }
                else
                {
                    found.Unreached(obstacle.Path);
                }

                continue;
            }

            yield return configured;
        }
    }

    /// <summary>
    /// <paramref name="candidate"/> where it holds a RetroArch program Deguffer may act beside, or null.
    /// A folder Windows would not describe, a link and a folder no program may answer for are each said.
    /// </summary>
    private string? Program(string candidate, RetroArchReading found)
    {
        switch (LongPath.ProbeDirectory(candidate))
        {
            case PathPresence.Absent:
                return null;

            case PathPresence.Refused:
                found.Unreached(candidate);
                return null;
        }

        switch (RetroArchInstall.ProgramIn(candidate))
        {
            case PathPresence.Absent:
                return null;

            case PathPresence.Refused:
                found.Unreached(candidate);
                return null;
        }

        if (LongPath.IsReparsePoint(candidate))
        {
            found.DeclineLink(candidate);
            return null;
        }

        if (WhyNotOwned(candidate) is { } why)
        {
            found.Notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving '{candidate}' alone although RetroArch is in it: {why}"));
            return null;
        }

        return candidate;
    }

    private static RetroArchSettings? Settings(string file, RetroArchReading found)
    {
        if (RetroArchSettings.Read(file) is not { } settings)
        {
            found.Unread(file, "it could not tell which folders RetroArch uses, and looked in none of them.");
            return null;
        }

        foreach (var include in settings.UnreadIncludes)
        {
            found.Unread(include, $"a folder '{file}' sets through it may have been missed.");
        }

        return settings;
    }
}
