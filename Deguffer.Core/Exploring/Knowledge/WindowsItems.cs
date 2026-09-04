namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// What lives under <c>%SystemRoot%</c>, and what each of it costs to remove.
///
/// <para>Past G1's file ceiling, and deliberately: this is a table of sentences rather than a type
/// with parts. Its one responsibility is to say what is inside the Windows directory, and splitting
/// it by depth or by first letter would divide the responsibility nowhere and leave a reader looking
/// in two files for one folder.</para>
///
/// <para><b>An entry is here only where Microsoft documents the folder.</b> Several of the names a
/// size picture puts in front of somebody — <c>Media</c>, <c>Web</c>, <c>servicing\LCU</c>,
/// <c>WinSxS\Backup</c> — are described nowhere in Microsoft's own documentation, and are left out
/// rather than described from recollection. Saying nothing is a worse answer than saying something
/// true, and a much better one than saying something plausible.</para>
///
/// <para>The verdicts follow §5.1: where Windows ships a command that reclaims the space properly,
/// the line names it instead of saying "delete this". Several of those commands are one-way — they
/// buy space by giving up the ability to uninstall an update or roll a driver back — and where that
/// is so, the line says so.</para>
/// </summary>
internal static class WindowsItems
{
    public static IReadOnlyList<KnownItem> All { get; } =
    [
        new(
            KnownPlace.WindowsDirectory,
            string.Empty,
            "Windows itself: the operating system's own programs, drivers, settings and servicing "
            + "data. Almost nothing in here belongs to a user or to an application, and almost none "
            + "of it can be removed without breaking something. Its size is largely fixed by which "
            + "version of Windows is installed and how many updates it has taken.",

            "Deguffer removes nothing from inside the Windows directory, and neither should you by "
            + "hand."),

        new(
            KnownPlace.WindowsDirectory,
            "WinSxS",
            "The component store: every file Windows is built from, plus the earlier version of "
            + "each one that an update replaced, so that features can be turned on and updates "
            + "rolled back. Windows Explorer reports it as far larger than it is, because most of "
            + "what is in here is a second name for a file that already exists elsewhere on the "
            + "drive rather than a second copy of it. In Microsoft's own example a store reported "
            + "as 4.98 GB was carrying about 507 MB that deleting it would actually free.",

            "It must never be deleted — a machine without it may not start and cannot be updated — "
            + "and 'Dism.exe /Online /Cleanup-Image /StartComponentCleanup' is the supported way to "
            + "drop the superseded versions."),

        new(
            KnownPlace.WindowsDirectory,
            "Installer",
            "A trimmed copy of the installation package of every program installed by Windows "
            + "Installer, and of every patch since. Repairing, changing, updating or uninstalling "
            + "one of those programs reads its package back out of here. Nothing prunes it, so it "
            + "grows for the life of the installation.",

            "It must never be deleted: the files are unique to this machine, cannot be copied back "
            + "from another, and Microsoft's documented remedy for losing them is to rebuild the "
            + "machine."),

        new(
            KnownPlace.WindowsDirectory,
            "SoftwareDistribution",
            "Windows Update's working folder. It holds update payloads that have been downloaded, "
            + "along with the catalogue and the history the update agent keeps. It is scratch "
            + "space: the record of what is actually installed lives elsewhere.",

            "Clearing it loses only update history and costs a slower first scan, and the documented "
            + "way is to stop the update service first rather than delete the folder under a "
            + "running one."),

        new(
            KnownPlace.WindowsDirectory,
            @"SoftwareDistribution\Download",
            "Where Windows Update puts an update's files between downloading them and installing "
            + "them. A large one usually means updates are waiting rather than that anything has "
            + "been left behind.",

            "Installing the updates that are waiting and restarting is what empties it properly, "
            + "and clearing it by hand needs the update service stopped first."),

        new(
            KnownPlace.WindowsDirectory,
            @"SoftwareDistribution\DataStore",
            "The database Windows Update keeps its catalogue and its history of installed updates "
            + "in. It is a record rather than a payload, which is why 'View update history' can "
            + "show anything at all.",

            "It rebuilds itself on the next scan, and Microsoft's own update-repair procedure "
            + "renames it aside with the update services stopped."),

        new(
            KnownPlace.WindowsDirectory,
            "servicing",
            "The manifests and catalogues describing every update package installed on this "
            + "machine. It is the index to the component store rather than a copy of anything, and "
            + "Windows checks its integrity whenever it repairs itself.",

            "It cannot be deleted, and the component cleanup commands are what shrink what it "
            + "points at."),

        new(
            KnownPlace.WindowsDirectory,
            "Temp",
            "The temporary folder Windows itself and the services running under system accounts "
            + "write scratch data to, separate from the one inside each user's profile. Programs "
            + "are supposed to clear up after themselves here and many do not, so it accumulates "
            + "indefinitely.",

            "Disk Cleanup clears it and leaves anything written in the past week alone, because a "
            + "newer file may belong to an installer that is still running."),

        new(
            KnownPlace.WindowsDirectory,
            "Prefetch",
            "A small trace file per program recording which parts of it were read while it "
            + "started, so Windows can load those parts ahead of the next launch. The files are "
            + "tens to hundreds of kilobytes each, so this folder is very rarely a meaningful part "
            + "of a size picture.",

            "Clearing it recovers almost nothing and costs a slower next launch of each program "
            + "while the traces are rebuilt."),

        new(
            KnownPlace.WindowsDirectory,
            @"Logs\CBS",
            "The log the servicing stack writes to. Everything 'sfc /scannow' and the Windows "
            + "repair commands do is recorded here, along with what the component installer does "
            + "during an update. It is written and never read back by Windows itself.",

            "Nothing depends on it, so deleting it costs only the record of what a past repair or "
            + "update did — but not while a repair or an update is running."),

        new(
            KnownPlace.WindowsDirectory,
            "Panther",
            "Where Windows Setup writes its logs. An installation, and every feature update since, "
            + "leaves its record of what it did and what went wrong here, along with any answer "
            + "file used to automate the install.",

            "These are logs and nothing running reads them, but Microsoft documents no cleanup for "
            + "this folder and an answer file here may be one a rebuild depends on."),

        new(
            KnownPlace.WindowsDirectory,
            "System32",
            "The heart of Windows: its core programs, its libraries and its drivers. On a 64-bit "
            + "Windows this holds the 64-bit files despite the name, and the 32-bit ones sit in "
            + "SysWOW64 beside it, which is the opposite of what both names suggest. It is arranged "
            + "that way so that older software asking for 'System32' keeps working.",

            "Nothing in here can be removed — it is the operating system."),

        new(
            KnownPlace.WindowsDirectory,
            "SysWOW64",
            "The 32-bit half of Windows, kept so that 32-bit software still runs on a 64-bit "
            + "machine. The name is the wrong way round for a reason: 32-bit programs asking for "
            + "'System32' are sent here instead, so they find the libraries built to match them.",

            "Nothing in here can be removed — it is what 32-bit software runs against."),

        new(
            KnownPlace.WindowsDirectory,
            @"System32\DriverStore\FileRepository",
            "Every driver package that has ever been installed on this machine, kept so Windows can "
            + "reinstall a device without asking for the driver again. Nothing removes the older "
            + "version when a driver is updated, so graphics and printer drivers in particular "
            + "accumulate. On a machine with a discrete graphics card this is often the largest "
            + "folder in Windows.",

            "Deleting a package by hand leaves Windows behaving unpredictably, and 'pnputil "
            + "/delete-driver <name>.inf /uninstall' is the supported way to remove one — at the "
            + "cost of being able to roll that driver back."),

        new(
            KnownPlace.WindowsDirectory,
            @"System32\LogFiles",
            "A general log directory. On an ordinary desktop what is in here is usually the error "
            + "log of the Windows HTTP service and the odd system component; on a server it may "
            + "also hold web-server logs.",

            "Deleting an old, rotated log costs the record of what happened, and a log a service "
            + "currently has open cannot be deleted at all."),

        new(
            KnownPlace.WindowsDirectory,
            @"System32\winevt\Logs",
            "The event logs: one file per channel behind everything Event Viewer shows, from the "
            + "Application and System logs to hundreds of per-component ones. Each channel has its "
            + "own size limit and overwrites its oldest entries when it reaches it, so the total is "
            + "bounded rather than open-ended.",

            "The files are held open by the event log service, so 'wevtutil cl <channel>' is the "
            + "way to clear one and 'wevtutil sl <channel> /ms:<bytes>' the way to cap it for "
            + "good."),

        new(
            KnownPlace.WindowsDirectory,
            @"System32\config",
            "The registry: the machine-wide settings for Windows, for every installed program, and "
            + "for the local accounts and the security policy, held as a handful of database files. "
            + "It is read before anything else starts.",

            "It cannot be deleted — losing the system hive leaves a machine that will not start — "
            + "and there is nothing here worth recovering anyway."),

        new(
            KnownPlace.WindowsDirectory,
            "LiveKernelReports",
            "Snapshots of the Windows kernel's memory, taken to diagnose a hang or a component "
            + "failure the machine recovered from without restarting. Each one can be large, and "
            + "Windows already limits how many it keeps and how often it takes one.",

            "They are diagnostic files that nothing reads back, so deleting them recovers their "
            + "space and costs only the ability to investigate a past fault."),

        new(
            KnownPlace.WindowsDirectory,
            "Minidump",
            "A small memory dump per blue screen, kept as a history so a pattern of crashes can be "
            + "investigated. Each one is tens of kilobytes rather than the hundreds of megabytes "
            + "the single large dump beside it takes.",

            "Nothing reads them back, so deleting them costs the crash history and nothing else — "
            + "though the single large MEMORY.DMP beside them holds far more of the space."),

        new(
            KnownPlace.WindowsDirectory,
            "MEMORY.DMP",
            "The full memory dump from the most recent blue screen, overwritten by each new one. "
            + "How large it is depends on what kind of dump Windows is configured to take: a kernel "
            + "dump runs to a few hundred megabytes, and a complete one is the size of the "
            + "machine's memory.",

            "Nothing reads it back once the crash has been looked at, so deleting it recovers its "
            + "space and costs the ability to investigate that crash."),

        new(
            KnownPlace.WindowsDirectory,
            "CSC",
            "The local copy of network files marked to stay available offline, so they can still be "
            + "opened when the share is not reachable and synchronised when it is. Anything changed "
            + "offline lives here and nowhere else until it has been synchronised back.",

            "Deleting it loses any offline change that has not been synchronised yet, which is the "
            + "user's own work rather than a cache, so it must be emptied through the Offline Files "
            + "settings and never by hand."),

        new(
            KnownPlace.WindowsDirectory,
            "assembly",
            "The shared library store for the older .NET Framework, versions 1.0 to 3.5. An "
            + "assembly here is one that several installed programs are meant to share rather than "
            + "each carry their own copy of.",

            "Removing one breaks every program that expected to find it, nothing counts how many "
            + "those are, and uninstalling the program that put it there is the only supported way "
            + "to take it away."),

        new(
            KnownPlace.WindowsDirectory,
            "Microsoft.NET",
            "The .NET Framework itself: the runtime for each installed version, the shared library "
            + "store for version 4 and later, and the machine code Windows compiles ahead of time "
            + "so managed programs start faster. Its size follows which versions are installed "
            + "rather than how much they are used.",

            "The runtime cannot be removed without breaking every .NET Framework program on the "
            + "machine, and the precompiled code is managed one assembly at a time by 'ngen' rather "
            + "than cleared in bulk."),

        new(
            KnownPlace.WindowsDirectory,
            "Fonts",
            "Every font installed for the whole machine. It grows only when somebody installs a "
            + "font, so it is stable and rarely large.",

            "A font is removed through Settings, under Personalisation and then Fonts, rather than "
            + "by deleting the file — and removing one Windows itself draws with changes how the "
            + "whole interface looks."),

        new(
            KnownPlace.WindowsDirectory,
            "INF",
            "The setup instructions for every driver package staged on this machine, and the log "
            + "Windows writes while installing hardware. The instruction files are the other half "
            + "of what is in the driver store, and are small.",

            "They go with their driver package, which 'pnputil /delete-driver <name>.inf "
            + "/uninstall' removes properly, and deleting one by hand leaves a package Windows can "
            + "no longer make sense of."),

        new(
            KnownPlace.WindowsDirectory,
            "SystemApps",
            "The parts of the Windows interface that are built as apps rather than as ordinary "
            + "programs: the Start menu, the search box and the shell surfaces around them. They "
            + "are part of Windows and are installed with it.",

            "They cannot be removed — Windows' own tools refuse — and deleting the files breaks the "
            + "Start menu or search until Windows repairs itself."),

        new(
            KnownPlace.WindowsDirectory,
            "Downloaded Program Files",
            "A cache for the browser add-ons the earliest versions of Internet Explorer downloaded "
            + "to display a page. Nothing on a modern machine puts anything here, so it is normally "
            + "empty or nearly so.",

            "Disk Cleanup has a category for exactly this, and on a modern machine there is almost "
            + "never anything in it to recover."),

        new(
            KnownPlace.WindowsDirectory,
            "debug",
            "Debug logs from a handful of Windows services. The best known is the one the logon "
            + "service writes, which rotates itself at twenty megabytes and is off unless somebody "
            + "turned it on.",

            "These are logs that nothing reads back, so deleting them costs only diagnostic history "
            + "and rarely recovers much."),

        new(
            KnownPlace.WindowsDirectory,
            "security",
            "Where the local security policy is kept, along with the log written when that policy "
            + "is applied. It is not where accounts or passwords live — those are in the registry.",

            "It is small and it underpins the machine's security settings, so there is nothing here "
            + "worth recovering and a real cost to getting it wrong."),
    ];
}
