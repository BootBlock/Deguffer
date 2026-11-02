using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Where a store's symbols come from, which decides the row it is offered on.</summary>
public enum SymbolStoreFeed
{
    /// <summary>
    /// Every source that fills the store is known, and each is one of Microsoft's public servers, which
    /// keep every symbol they publish.
    /// </summary>
    Microsoft,

    /// <summary>
    /// A source is a server, share or folder that is not Microsoft's, or Deguffer cannot read the list of
    /// sources at all. Either way the store may hold a symbol nothing else keeps any more.
    /// </summary>
    Other,
}

/// <summary>A folder debuggers copy symbols into.</summary>
/// <param name="Path">The store, as the first setting to name it spells it.</param>
/// <param name="Feed">Where its symbols come from.</param>
/// <param name="Readers">The debuggers that write to it, as the process table names them.</param>
/// <param name="NamedBy">What named it, for the plan to say.</param>
public sealed record SymbolStore(string Path, SymbolStoreFeed Feed, IReadOnlyList<string> Readers, IReadOnlyList<string> NamedBy);

/// <summary>Every store this machine's debuggers are set to use, and what was said about the rest.</summary>
/// <param name="Stores">The stores that may be examined, each named once.</param>
/// <param name="Notes">What each row has to say about a setting it did not follow, by the row it belongs on.</param>
public sealed record SymbolStoreFinding(
    IReadOnlyList<SymbolStore> Stores,
    IReadOnlyList<(SymbolStoreFeed Feed, PlanNote Note)> Notes);

/// <summary>
/// Finds every debugger symbol store on this machine, for the two rows that offer them.
///
/// <para><b>Shared by both rows because a store is on exactly one of them.</b> A folder two settings
/// name is one store, and it is fed by what both say, so a folder <c>_NT_SYMBOL_PATH</c> fills from
/// Microsoft and Visual Studio fills from servers Deguffer cannot see is not Microsoft's alone. Each row
/// asking separately could put that folder on both.</para>
///
/// <para><b>Where it looks.</b> Each store in <c>_NT_SYMBOL_PATH</c> and <c>_NT_ALT_SYMBOL_PATH</c>, which
/// every Microsoft debugger reads and which override Visual Studio's own setting. The <c>sym</c> folder
/// of <c>DBGHELP_HOMEDIR</c>, which is the default store wherever that variable is set. WinDbg's own
/// store, <c>%PROGRAMDATA%\Dbg\sym</c>. Each Visual Studio instance's setting, and its default folder in
/// the temporary folder. WinDbg and Visual Studio keep their list of servers where Deguffer cannot read
/// it, so their stores are <see cref="SymbolStoreFeed.Other"/>.</para>
///
/// <para><b>A folder a setting names is checked before it is examined.</b> It must be a full path, not on
/// a network share, where other computers' debuggers may be reading it, and not a folder
/// <see cref="ConfiguredFolder"/> declines. A folder the symbol path also reads as somebody's own store
/// is never a cache.</para>
/// </summary>
public sealed class SymbolStoreDiscovery
{
    private readonly IUserEnvironment _environment;
    private readonly ISystemDirectories _system;
    private readonly IVolumeInventory _volumes;
    private readonly PassMemo<SymbolStoreFinding> _finding = new();

    public SymbolStoreDiscovery(IUserEnvironment environment, ISystemDirectories? system = null, IVolumeInventory? volumes = null)
    {
        _environment = environment;
        _system = system ?? SystemDirectories.Current;
        _volumes = volumes ?? VolumeInventory.Current;
    }

    /// <summary>The finding, kept for a planning pass (G4), because both rows and Explore ask for it.</summary>
    public SymbolStoreFinding Find() => _finding.Get(Look);

    /// <summary>Every answer here is a setting, which the user can change between one scan and the next.</summary>
    public void Invalidate() => _finding.Forget();

    private SymbolStoreFinding Look()
    {
        var named = new List<Named>();
        var notes = new List<(SymbolStoreFeed, PlanNote)>();
        var masters = new List<string>();
        var accountTemp = TempRoots.Resolve(_environment, _system, _volumes).AccountFolders;

        var home = HomeStore(notes);

        foreach (var variable in SymbolStoreLayout.PathVariables)
        {
            var reading = SymbolPath.Read(_environment.GetEnvironmentVariable(variable));

            masters.AddRange(reading.Masters.Select(LongPath.Configured).OfType<string>());

            foreach (var store in reading.Stores)
            {
                var feed = store.Sources.All(SymbolStoreLayout.IsMicrosoftServer) ? SymbolStoreFeed.Microsoft : SymbolStoreFeed.Other;

                if (store.Folder is null && home is null)
                {
                    notes.Add((feed, new PlanNote(
                        PlanNoteSeverity.Information,
                        $"{variable} names the default symbol store, which is in the home folder of whichever debugger reads "
                        + "it. Deguffer looks in WinDbg's own store, and does not guess at the others.")));
                    continue;
                }

                Name(store.Folder ?? home!, feed, SymbolStoreLayout.AllProcessNames, variable);
            }
        }

        if (home is not null)
        {
            // Every debugger makes its default store there, from whatever servers its own settings name.
            Name(home, SymbolStoreFeed.Other, SymbolStoreLayout.AllProcessNames, SymbolStoreLayout.HomeVariable);
        }

        Found(SymbolStoreLayout.WinDbgStore(_system.ProgramData), SymbolStoreLayout.WinDbgProcessNames, "WinDbg");

        var visualStudio = VisualStudioSymbolSettings.Read(_environment.LocalAppData);

        notes.AddRange(visualStudio.Unread.Select(file => (SymbolStoreFeed.Other, new PlanNote(
            PlanNoteSeverity.Warning,
            $"Deguffer could not read Visual Studio's settings at '{file}', so a symbol cache only they name was neither "
            + "cleared nor ruled out."))));

        foreach (var setting in visualStudio.Settings.Where(setting => setting.Folder is not null))
        {
            Name(setting.Folder!, SymbolStoreFeed.Other, SymbolStoreLayout.VisualStudioProcessNames, $"Visual Studio's settings in '{setting.SettingsFile}'");
        }

        Found(SymbolStoreLayout.VisualStudioDefaultStore(_environment.TempPath), SymbolStoreLayout.VisualStudioProcessNames, "Visual Studio");

        return Merge(named, masters, notes);

        void Name(string value, SymbolStoreFeed feed, IReadOnlyList<string> readers, string namedBy)
        {
            if (Vet(value, accountTemp) is { } why)
            {
                notes.Add((feed, new PlanNote(
                    PlanNoteSeverity.Information,
                    $"{namedBy} names '{value}' as a symbol store, and Deguffer is leaving it alone: {why}")));
                return;
            }

            named.Add(new Named(LongPath.Configured(value)!, feed, readers, namedBy));
        }

        // A default store nothing names is looked at only where it is there, so a machine without the
        // debugger shows no row for it.
        void Found(string folder, IReadOnlyList<string> readers, string namedBy)
        {
            if (LongPath.ProbeDirectory(folder) is not PathPresence.Absent)
            {
                named.Add(new Named(folder, SymbolStoreFeed.Other, readers, namedBy));
            }
        }
    }

    /// <summary>The default store <c>DBGHELP_HOMEDIR</c> sets, or null where it is not set or names no folder.</summary>
    private string? HomeStore(List<(SymbolStoreFeed, PlanNote)> notes)
    {
        if (_environment.GetEnvironmentVariable(SymbolStoreLayout.HomeVariable)?.Trim() is not { Length: > 0 } value)
        {
            return null;
        }

        if (LongPath.Configured(value) is not { } home)
        {
            notes.Add((SymbolStoreFeed.Other, new PlanNote(
                PlanNoteSeverity.Information,
                $"{SymbolStoreLayout.HomeVariable} is set to '{value}', which is not a full path, so Deguffer cannot tell "
                + "which folder the debuggers' default store is in.")));
            return null;
        }

        return Path.Combine(home, SymbolStoreLayout.DefaultStoreName);
    }

    /// <summary>Why the folder a setting names must not be examined as a store, or null where it may be.</summary>
    private string? Vet(string value, IReadOnlyList<string> accountTemp)
    {
        if (LongPath.Configured(value) is not { } folder)
        {
            return "it is not a full path, so Deguffer cannot tell which folder it means.";
        }

        if (HostVolume.Unopened(_volumes, folder) is UnopenedPlace.Share)
        {
            return "it is on a network share, where debuggers on other computers may be reading it.";
        }

        return ConfiguredFolder.WhyNotOwned(folder, _environment, _system, _volumes, accountTemp);
    }

    /// <summary>
    /// One store for each folder, at every path it is reachable at, fed by everything that names it. A
    /// folder the symbol path also reads as a store of its own is left alone.
    /// </summary>
    private SymbolStoreFinding Merge(
        List<Named> named,
        List<string> masters,
        List<(SymbolStoreFeed Feed, PlanNote Note)> notes)
    {
        var reachedMasters = masters.Select(master => ReachedFolder.At(master, _volumes)).ToList();
        var stores = new List<(ReachedFolder Reached, List<Named> Names)>();

        foreach (var store in named)
        {
            var reached = ReachedFolder.At(store.Path, _volumes);

            if (stores.FirstOrDefault(known => known.Reached.IsSameAs(reached)) is { Names: { } names })
            {
                names.Add(store);
            }
            else
            {
                stores.Add((reached, [store]));
            }
        }

        List<SymbolStore> found = [];

        foreach (var (reached, names) in stores)
        {
            var feed = names.Any(name => name.Feed == SymbolStoreFeed.Other) ? SymbolStoreFeed.Other : SymbolStoreFeed.Microsoft;
            var namedBy = names.Select(name => name.NamedBy).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (reachedMasters.Any(master => master.IsSameAs(reached)))
            {
                notes.Add((feed, new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{names[0].Path}' alone: the symbol path reads it as a symbol store of its own as well as "
                    + "copying symbols into it, so a symbol there may be the only copy.")));
                continue;
            }

            found.Add(new SymbolStore(
                names[0].Path,
                feed,
                [.. names.SelectMany(name => name.Readers).Distinct(StringComparer.OrdinalIgnoreCase)],
                namedBy));
        }

        return new SymbolStoreFinding(found, notes);
    }

    private sealed record Named(string Path, SymbolStoreFeed Feed, IReadOnlyList<string> Readers, string NamedBy);
}
