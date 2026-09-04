namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// What NTFS keeps at the top of a volume: the records that describe the filesystem itself, and the
/// optional features that sit a level down in <c>$Extend</c>.
///
/// <para>They are in the catalogue because Deguffer draws them. A directory walk never sees any of
/// this — Windows hides the reserved names from enumeration — but §5.5's file-table route reads the
/// records directly, so the first sixteen appear at the top of a scanned drive with names nobody
/// recognises and, in the master file table's case, a size in the hundreds of megabytes.</para>
///
/// <para><b>None of them is ever a deletion candidate, and the verdicts here say so.</b> Microsoft's
/// position on the whole set is unqualified: altering or deleting one causes permanent damage to the
/// filesystem. Several can be shrunk or reset through a supported command, and where one exists this
/// names it, on §5.1's reasoning — a tool's own eviction beats deleting paths, and that is as true of
/// a deletion the reader performs by hand as of one Deguffer performs for them.</para>
///
/// <para>Sizes are described by their behaviour rather than by a figure. The one published
/// per-metafile breakdown is a single ~1 TB volume, which is evidence of scale and not a prediction
/// about anybody's disk.</para>
/// </summary>
internal static class VolumeRootItems
{
    public static IReadOnlyList<KnownItem> All { get; } =
    [
        Reserved(
            "$MFT",
            "The master file table, which is the index NTFS uses to find everything else on this "
            + "drive. It holds at least one record for every file and folder, and each record "
            + "carries that item's name, dates, permissions and the location of its contents. It "
            + "grows as files are created and does not shrink again when they are deleted, so a "
            + "drive that has churned through millions of small files keeps a large table.",

            "Windows will not let this be deleted, and there is no supported way to shrink it "
            + "either — the space a deleted file's record leaves behind is reclaimed only by "
            + "reformatting the drive."),

        Reserved(
            "$MFTMirr",
            "A backup copy of the first few records of the master file table. The boot sector "
            + "records where both the table and this mirror are, so Windows can still mount the "
            + "drive when the start of the table is damaged. It is a few kilobytes and never grows.",

            "It cannot be deleted, and it is far too small to be worth recovering."),

        Reserved(
            "$LogFile",
            "The journal NTFS writes a metadata change to before making it, so the filesystem can "
            + "be put back into a consistent state after a crash or a power cut. It records "
            + "changes to the filesystem's own bookkeeping rather than the contents of files, so it "
            + "is not a backup of anything. Its size is fixed when the drive is formatted and "
            + "scales with the size of the drive.",

            "It cannot be deleted, and shrinking it with 'chkdsk /l:<size>' buys a few tens of "
            + "megabytes at the cost of how much the filesystem can recover after a crash."),

        Reserved(
            "$Volume",
            "The drive's own identity: its label, its serial number, the version of NTFS it was "
            + "formatted with, and the flag that tells Windows whether to check the disk at the "
            + "next start. All of it fits inside the file's own table record.",

            "It cannot be deleted, and it occupies no space on the drive to recover."),

        Reserved(
            "$AttrDef",
            "The table that tells NTFS what kinds of information a file record may hold, and the "
            + "rules for each kind. It is the schema the filesystem reads its own records through. "
            + "It is written when the drive is formatted and does not change afterwards.",

            "It cannot be deleted, and it is one cluster in size."),

        Reserved(
            "$Bitmap",
            "One bit for every cluster on the drive, saying whether that cluster is in use. This is "
            + "what Windows consults to answer how much free space there is. Its size follows the "
            + "size of the drive and the cluster size it was formatted with, not how full it is, so "
            + "it changes only if the drive is resized.",

            "It cannot be deleted. If the free space Windows reports disagrees badly with what is "
            + "on the drive, 'chkdsk /f' is the documented repair."),

        Reserved(
            "$Boot",
            "The first sectors of the drive: the boot sector and the code that starts an operating "
            + "system from it. The boot sector is also where the location of the master file table "
            + "is written, which is how Windows finds anything at all here. A duplicate is kept at "
            + "the end of the drive.",

            "It cannot be deleted, it is a few kilobytes, and a drive without it will not mount."),

        Reserved(
            "$BadClus",
            "The list of clusters that hold physically bad sectors, so NTFS never puts a file in "
            + "one again. On a healthy drive it occupies nothing, and it grows only as failing "
            + "sectors are found. Some tools report its length as the size of the whole drive; the "
            + "space it actually occupies is what matters.",

            "It cannot be deleted, and clearing the list would hand known-bad sectors back to be "
            + "written to rather than recover any space."),

        Reserved(
            "$Secure",
            "One shared copy of every distinct set of permissions used on this drive. Files point "
            + "at an entry here instead of each carrying its own copy, which is why thousands of "
            + "files sharing one permission set cost almost nothing. Entries are not removed when "
            + "the last file using them goes, so it accumulates slowly.",

            "It cannot be deleted, and 'chkdsk /sdcleanup' collects the entries nothing uses any "
            + "more, which is usually worth a few megabytes."),

        Reserved(
            "$UpCase",
            "A table mapping every character to its upper-case form, which is how NTFS compares "
            + "and sorts filenames without regard to case in the same way for the life of the "
            + "drive. It is exactly 128 KB, one entry per character, and never changes.",

            "It cannot be deleted, and it is 128 KB."),

        Reserved(
            "$Extend",
            "A folder holding NTFS's optional features rather than a file: disk quotas, object "
            + "identifiers, the index of links and junctions, the change journal, and the "
            + "transaction machinery. The folder itself occupies nothing. What is inside it can be "
            + "large, and is where nearly all of a drive's reclaimable filesystem overhead sits.",

            "It cannot be deleted. Each feature inside it is turned off or reset through its own "
            + "command, never by removing a file."),

        Reserved(
            @"$Extend\$ObjId",
            "An index of the identifiers Windows attaches to some files so that shortcuts and "
            + "linked documents can still find them after they are renamed or moved. Only a small "
            + "minority of files are ever given one, so the index stays small.",

            "It cannot be deleted, it is a few tens of kilobytes, and removing an identifier is "
            + "what stops a shortcut finding its target."),

        Reserved(
            @"$Extend\$Quota",
            "Per-account disk usage and limits for this drive. It is only meaningful where disk "
            + "quotas have been switched on, which on an ordinary desktop they have not, and it "
            + "occupies nothing at all when they are off.",

            "It cannot be deleted. Quotas are switched off with 'fsutil quota disable <drive>:', which "
            + "stops the tracking rather than recovering any measurable space."),

        Reserved(
            @"$Extend\$Reparse",
            "An index of every link, junction, drive mount point and cloud placeholder on this "
            + "drive, so NTFS can find them all without walking the whole tree. It grows with how "
            + "many there are rather than with how big anything is, and stays in the low megabytes "
            + "even on a system drive.",

            "It cannot be deleted, and the way to shrink it is to delete the links themselves."),

        Reserved(
            @"$Extend\$UsnJrnl",
            "The change journal: a running log of every file and folder that has changed on this "
            + "drive. Backup software, the search index and file-synchronisation services read it "
            + "to find out what has changed since they last looked, instead of scanning the whole "
            + "drive again. It grows with how busy the drive is, and Windows trims it back towards "
            + "a configured size.",

            "It cannot be deleted as a file, and 'fsutil usn createjournal m=<maxsize> a=<delta> "
            + "<drive>:' caps its growth without the long, disruptive rebuild that deleting the "
            + "journal outright forces on everything reading it."),

        Reserved(
            @"$Extend\$RmMetadata",
            "The folder holding the state of NTFS's transaction machinery, which lets a program "
            + "make several file changes that either all take effect or none of them do. The folder "
            + "itself occupies nothing, but what is under it can reach hundreds of megabytes and is "
            + "the commonest cause of filesystem overhead nobody can account for.",

            "It cannot be deleted, and 'fsutil resource setautoreset true <drive>' followed by a "
            + "restart is the supported way to have Windows clear what is under it."),

        Reserved(
            @"$Extend\$RmMetadata\$Txf",
            "Working storage for NTFS transactions. A file deleted or overwritten inside a "
            + "transaction that has not finished is held here so the transaction can still be "
            + "undone. It holds live state rather than accumulating, so it is normally a few "
            + "kilobytes.",

            "It cannot be deleted, and removing the record of a transaction still in flight is a "
            + "way to corrupt files rather than to recover space."),

        Reserved(
            @"$Extend\$RmMetadata\$TxfLog",
            "The log NTFS's transaction machinery writes to, so a transaction interrupted by a "
            + "crash can be finished or undone at the next start. The folder holds the log's "
            + "containers, which is where its size actually is — typically tens of megabytes.",

            "It cannot be deleted, and 'fsutil resource setlog' is the supported way to change how "
            + "large the log is allowed to become."),

        Reserved(
            @"$Extend\$RmMetadata\$Tops",
            "Where NTFS keeps the previous contents of anything a running transaction has "
            + "overwritten, so the write can be undone. It grows with how much transactional "
            + "writing the machine does and does not obviously shrink back, which makes it the "
            + "largest piece of filesystem overhead measured on a real drive.",

            "It cannot be deleted, and 'fsutil resource setautoreset true <drive>' followed by a "
            + "restart is the supported way to have Windows clear it."),

        Reserved(
            @"$Extend\$Repair",
            "A name NTFS reserves for its own repair work, alongside the online disk checking that "
            + "'chkdsk /scan' performs. Microsoft documents the name and nothing about what it "
            + "holds, so Deguffer will not guess. It has been measured at around ninety megabytes "
            + "on a real drive.",

            "It cannot be deleted, and no supported command trims it."),
    ];

    /// <summary>
    /// One of NTFS's own records. Every one of them ends the same way, so the verdict each entry
    /// writes is about <em>why</em> rather than about whether — and the sentence below is what makes
    /// that a rule instead of twenty separate decisions.
    /// </summary>
    private static KnownItem Reserved(string name, string summary, string removal) =>
        new(
            KnownPlace.VolumeRoot,
            name,
            "Part of NTFS itself, kept at the top of the drive. " + summary,
            removal);
}
