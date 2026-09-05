namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// What every account on the machine shares: the installed programs, and the data those programs
/// keep for the computer rather than for one person.
///
/// <para>The four roots here are ones §7.1 refuses outright, so their entries have an extra
/// job. A reader who has just found ten gigabytes under <c>ProgramData</c> and cannot delete it is
/// owed the reason rather than a refusal, and where a supported route exists the line names it —
/// which for installed software is nearly always the program's own uninstaller.</para>
/// </summary>
internal static class SharedItems
{
    public static IReadOnlyList<KnownItem> All { get; } =
    [
        new(
            KnownPlace.UserProfiles,
            string.Empty,
            "The folder holding one profile per account on this computer: each person's documents, "
            + "settings and downloads, kept apart from everyone else's. On most machines it is the "
            + "largest thing on the drive after Windows itself.",

            "Nothing here can be removed as a whole, and Deguffer acts only inside the profile it "
            + "is signed in to."),

        new(
            KnownPlace.ProgramFiles,
            string.Empty,
            "Where installed software lives. Each program has a folder of its own here, put there "
            + "by its installer and removed by its uninstaller. On a 64-bit Windows this holds the "
            + "64-bit programs, with the 32-bit ones in 'Program Files (x86)' beside it.",

            "Deleting part of a program here leaves it on the machine and broken, so a program is "
            + "removed through Settings, under Apps, which is what runs its uninstaller."),

        new(
            KnownPlace.ProgramFilesX86,
            string.Empty,
            "Where 32-bit software is installed on a 64-bit Windows, kept separate from the 64-bit "
            + "programs so that two builds of the same product can sit side by side. Plenty of "
            + "long-established software is still 32-bit, so this is often the busier of the two.",

            "Deleting part of a program here leaves it on the machine and broken, so a program is "
            + "removed through Settings, under Apps, which is what runs its uninstaller."),

        new(
            KnownPlace.ProgramData,
            string.Empty,
            "Application data shared by every account on this computer, as against the copy each "
            + "person keeps in their own profile. Installers, services and update caches use it, "
            + "and it is hidden by default, so it grows quietly and is often larger than anyone "
            + "expects.",

            "It holds settings and installation data rather than spare copies, so nothing here goes "
            + "as a whole — Deguffer offers the caches it recognises inside it on the Storage page "
            + "instead."),

        new(
            KnownPlace.ProgramData,
            "Package Cache",
            "Copies of the installation packages behind Visual Studio, the Visual C++ "
            + "redistributables, the .NET SDKs and other products installed the same way. They are "
            + "kept so those products can be repaired, changed or uninstalled without downloading "
            + "anything, and they accumulate as each update adds its own. Several gigabytes is "
            + "normal on a machine with Visual Studio.",

            "Deleting it uninstalls nothing but leaves those products unable to repair or change "
            + "themselves offline, so the Visual Studio installer's '--nocache' option is the "
            + "supported way to clear and stop keeping it."),

        new(
            KnownPlace.ProgramData,
            @"Microsoft\VisualStudio\Packages",
            "The Visual Studio installer's own store: a manifest and a downloaded payload for each "
            + "component of each product it has installed, kept so that a change or a repair can "
            + "run with no network. Every update adds its own and nothing removes the old ones, so "
            + "this reaches several gigabytes on a machine with Visual Studio. It is a different "
            + "folder from 'Package Cache' at the top of ProgramData, and a machine normally has "
            + "both. Beside the payloads sit the installer's records of what each product is made "
            + "of.",

            "The payloads download again on demand, so losing them costs an offline repair and "
            + "nothing else, and the installer's own '--nocache' switch is what clears them."),

        new(
            KnownPlace.ProgramData,
            @"Microsoft\Windows\WER",
            "Crash reports waiting to be sent to Microsoft, and the ones already sent. Each report "
            + "is a folder that can hold event logs and a memory dump of the program that failed, "
            + "so a single one can run to tens of megabytes. Windows keeps up to a thousand "
            + "archived reports by default.",

            "Nothing depends on a report once it has been sent, so clearing them recovers their "
            + "space and costs only the record of what has been crashing."),

        new(
            KnownPlace.ProgramData,
            @"Microsoft\Search\Data",
            "The Windows Search index: a database of the names, properties and full contents of "
            + "everything Windows has been told to index. Its size follows how much content is "
            + "indexed rather than how much is on the drive, and Windows offers no setting to cap "
            + "it, so it reaches several gigabytes easily.",

            "The service holds the database open, so the supported answers are narrowing what is "
            + "indexed in Indexing Options and then rebuilding it, which costs a full re-index that "
            + "can take a day."),

        new(
            KnownPlace.ProgramData,
            @"Microsoft\Windows Defender",
            "Microsoft Defender's own working folder: its scanning engine, the threat definitions "
            + "it updates several times a day, and its scan history. Each engine update installs "
            + "beside the last, so more than one version is usually present.",

            "It is protected against tampering and cannot be deleted, which is the point of it — "
            + "and Defender manages its own versions."),

        new(
            KnownPlace.ProgramData,
            @"Microsoft\Windows\AppRepository",
            "The machine's register of which packaged apps are installed and which accounts have "
            + "them. It is a database rather than a store of files, so its size follows how many "
            + "apps have ever been installed.",

            "Damaging it breaks installing, updating and starting every packaged app on the "
            + "machine, and there is no cleanup for it."),

        new(
            KnownPlace.ProgramData,
            @"Microsoft\Windows\DeliveryOptimization",
            "Downloaded Windows updates and Store content, kept so this machine can pass them on to "
            + "other machines on the network instead of everyone downloading the same files. "
            + "Windows already clears it as content ages or space runs short, but the ceiling is "
            + "generous — a fifth of the drive by default.",

            "Disk Cleanup's 'Delivery Optimization Files' clears it properly, and the only cost is "
            + "downloading again anything the machine needs a second time."),

        new(
            KnownPlace.ProgramData,
            "USOShared",
            "Trace files from the service that sequences Windows updates, and from the one that "
            + "shows the notifications about them. They are normally small, but they can build up "
            + "into thousands of tiny files.",

            "Nothing reads them except somebody diagnosing a failed update, and Microsoft documents "
            + "no command that clears them."),

        new(
            KnownPlace.ProgramData,
            "chocolatey",
            "The Chocolatey package manager, and everything it has installed. Despite the name this "
            + "is not a cache: what is under it is installed software and the shims that put it on "
            + "the command path. Chocolatey downloads to the temporary folder instead unless it has "
            + "been told otherwise.",

            "Deleting it uninstalls the tools it manages without their knowing, so 'choco uninstall "
            + "<package>' removes one properly and 'choco cache remove --expired' clears what is "
            + "genuinely cached."),
    ];
}
