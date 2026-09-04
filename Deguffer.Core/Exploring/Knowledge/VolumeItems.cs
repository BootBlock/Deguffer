namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// What Windows itself keeps at the top of a volume, as against
/// <see cref="VolumeRootItems"/>, which is what NTFS keeps there.
///
/// <para>Nearly everything here is hidden, which is why a size picture is where somebody meets it
/// for the first time. Several of these are among the largest items on a drive — the paging file,
/// the hibernation file, the restore points, a previous Windows installation — and between them
/// they account for most of the space a reader cannot find in Explorer.</para>
///
/// <para>They are also where the supported route matters most. The hibernation file goes with a
/// single documented command; a previous Windows installation goes through Settings and takes the
/// ability to go back with it; the restore points are a setting rather than a folder. Each entry
/// names the route, because "delete this" is the wrong answer to every one of them.</para>
///
/// <para>Names Microsoft does not document at all — <c>$GetCurrent</c>, <c>$SysReset</c>,
/// <c>DumpStack.log</c>, <c>OneDriveTemp</c> and the rest of the family — are left out. Each is
/// widely written about and none of it is first-party, and Deguffer's silence is the honest answer
/// to a question it cannot source.</para>
///
/// <para>Where Microsoft documents the <em>component</em> and not the folder it leaves behind,
/// <c>$WinREAgent</c> being the one here, the entry says which of the two is documented and stops.
/// That is a narrower thing than an exception to the rule above: the reader is told what the name
/// belongs to and told that nothing else about it is stated anywhere, rather than being handed a
/// purpose somebody inferred.</para>
/// </summary>
internal static class VolumeItems
{
    public static IReadOnlyList<KnownItem> All { get; } =
    [
        new(
            KnownPlace.VolumeRoot,
            "System Volume Information",
            "Where Windows keeps this drive's system restore points, its shadow copies and its "
            + "change-tracking data. It is created on every drive and locked so that even an "
            + "administrator cannot open it, which is why a scan may report nothing for it. Its size "
            + "follows how many restore points and snapshots have built up, and can be many "
            + "gigabytes.",

            "It belongs to Windows and cannot be deleted, so the way to reduce it is System "
            + "Protection's own limit on how much of the drive restore points may use."),

        new(
            KnownPlace.VolumeRoot,
            "$Recycle.Bin",
            "This drive's recycle bin, divided into one folder per account. That division is why it "
            + "is so often far larger than the bin appears to be: what is shown is only the signed-in "
            + "account's own deleted files, and every other account's are in here too.",

            "Emptying the recycle bin clears this account's share of it and nobody else's, so another "
            + "account's deleted files go only when that account empties its own."),

        new(
            KnownPlace.VolumeRoot,
            "pagefile.sys",
            "The paging file: where Windows puts memory that has not been used recently, so the "
            + "space it occupied can be given to something else. It also raises how much memory "
            + "programs may commit in total, and it is where a crash dump is written. Windows sizes "
            + "it from the memory in the machine and grows it under pressure.",

            "Windows holds it open and it cannot be deleted, so its size is changed in the virtual "
            + "memory settings — and making it too small causes programs to fail to start."),

        new(
            KnownPlace.VolumeRoot,
            "swapfile.sys",
            "A second paging file used only by Store apps. When memory runs short Windows writes a "
            + "suspended app's memory out here in one piece and reads it back when the user returns "
            + "to it, which is why it is separate from the ordinary paging file.",

            "Windows holds it open, it cannot be deleted, and Microsoft documents no setting that "
            + "turns it off."),

        new(
            KnownPlace.VolumeRoot,
            "hiberfil.sys",
            "Where the contents of memory are written when the machine hibernates. It also backs "
            + "Fast Startup, which is why it exists on machines whose owners never hibernate "
            + "deliberately. It is a large fraction of the memory installed, so on a machine with a "
            + "lot of memory it is one of the biggest files on the drive.",

            "'powercfg.exe /hibernate off' from an administrator prompt removes it and returns the "
            + "space, at the cost of hibernation and Fast Startup both."),

        new(
            KnownPlace.VolumeRoot,
            "Recovery",
            "The recovery environment: what starts when Windows will not, and what runs 'Reset this "
            + "PC', Startup Repair and BitLocker recovery. It is locked to the system account, so a "
            + "scan without administrator rights reports nothing for it rather than its real size. "
            + "Where the recovery image is kept here rather than on its own partition it is a few "
            + "hundred megabytes.",

            "Deleting it takes away the machine's ability to repair or reset itself, silently, until "
            + "the day that is needed."),

        new(
            KnownPlace.VolumeRoot,
            "Windows.old",
            "The previous Windows installation, moved aside when this one was upgraded or reset. It "
            + "is what 'Go back to the previous version of Windows' restores from, and it is where "
            + "files end up if a migration goes wrong. It is an entire Windows installation, so tens "
            + "of gigabytes is normal, and Windows deletes it by itself ten days after the upgrade.",

            "It is usually the largest thing worth reclaiming on a recently upgraded machine, and "
            + "Settings' 'Previous version of Windows' under Storage removes it properly — after "
            + "which going back is no longer possible."),

        new(
            KnownPlace.VolumeRoot,
            "$WinREAgent",
            "Working files left by the component that services the Windows recovery environment "
            + "during an update. Microsoft documents the component but not this folder, so what is "
            + "inside it and when it is cleared away are not stated anywhere first-party.",

            "Microsoft documents no way to clear it and says nothing about what removing it costs, "
            + "so Deguffer will not guess either."),

        new(
            KnownPlace.VolumeRoot,
            "$Windows.~BT",
            "Windows Setup's working folder for an upgrade, holding the new installation's files "
            + "while the upgrade runs and the logs it wrote if the upgrade had to be rolled back. It "
            + "is several gigabytes while an upgrade is in progress and shrinks to logs afterwards.",

            "It is what an unfinished upgrade rolls back from, so it must be left alone until the "
            + "upgrade has settled, after which Disk Cleanup's system files pass removes it."),

        new(
            KnownPlace.VolumeRoot,
            "Config.Msi",
            "Where Windows Installer keeps the backup of every file it is about to replace, plus "
            + "the script that would put them back, so a failed install can be undone. It is created "
            + "and removed again by each install, so one sitting here outside an installation is "
            + "left over from one that was interrupted.",

            "Deleting it during an installation destroys that installation's only way back, and "
            + "letting the installer finish and then restarting is what clears it properly."),

        new(
            KnownPlace.VolumeRoot,
            "PerfLogs",
            "Where Performance Monitor saves its traces by default. Windows creates the folder when "
            + "it is installed and nothing writes to it unless somebody starts a data collector "
            + "set, so on most machines it is empty.",

            "Anything inside it is a diagnostic trace somebody started deliberately, so deleting it "
            + "loses only that — and on most machines there is nothing in it to recover."),

        new(
            KnownPlace.VolumeRoot,
            "Documents and Settings",
            "Not a folder but a signpost: it points at the Users folder, so that software written "
            + "before Windows Vista still finds profiles where it expects them. It is deliberately "
            + "unreadable, and it holds nothing of its own.",

            "There is nothing here to recover — its whole size is the Users folder counted a second "
            + "time — and deleting it breaks older software permanently."),

        new(
            KnownPlace.VolumeRoot,
            "MSOCache",
            "A complete compressed copy of the installation files for a version of Office installed "
            + "from an installer package. Office keeps it so that repairing, changing or patching "
            + "itself never asks for the original media, and it is several hundred megabytes to a "
            + "few gigabytes.",

            "Deleting it does not break a working Office, but the next repair or feature install "
            + "asks for the original media instead — and Disk Cleanup deliberately will not touch "
            + "it."),

        new(
            KnownPlace.VolumeRoot,
            "inetpub",
            "Historically the folder Internet Information Services keeps websites in. Since the "
            + "April 2025 security updates Windows also creates it, empty, on machines with no web "
            + "server at all: its existence and its permissions are part of the fix for a "
            + "privilege-escalation flaw in Windows Update.",

            "On a machine with no web server there is nothing in it to recover and deleting it "
            + "reopens the flaw, so it should be left where it is."),

        new(
            KnownPlace.VolumeRoot,
            "AMD",
            "Where an AMD driver installer unpacks itself before installing. AMD's own "
            + "documentation says the installer removes it once it has finished, so one still here "
            + "is from an installation that did not complete — unless somebody chose it as the "
            + "place to install to.",

            "AMD states that nothing installed depends on it, so removing it costs only the unpacked "
            + "copy — unless it is where somebody chose to install to, which is what to check first."),

        new(
            KnownPlace.VolumeRoot,
            "bootmgr",
            "The boot manager on a machine that starts the older way, through a BIOS. The firmware "
            + "hands control to this file, and it reads the boot configuration and starts Windows. "
            + "It is a few hundred kilobytes.",

            "There is nothing to recover and a machine without it does not start at all."),

        new(
            KnownPlace.VolumeRoot,
            "Boot",
            "The boot configuration on a machine that starts through a BIOS: the database saying "
            + "which operating systems are installed and how to start each one, along with the "
            + "memory test. It is a few megabytes and does not grow.",

            "There is nothing to recover and a machine without it does not start at all."),
    ];
}
