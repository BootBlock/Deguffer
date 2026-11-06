namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One step of opening a volume's file table, in the order an open runs them.
/// <see cref="MftOpenTimings"/> times each one apart, so a slow open can be traced to the handle or
/// to the reads that find where the table is.
/// </summary>
public enum MftOpenStep
{
    /// <summary>Opening the volume's handle, through whatever file-system filters sit above it.</summary>
    VolumeHandle,

    /// <summary>Reading and parsing the boot sector, which says where <c>$MFT</c> starts.</summary>
    BootSector,

    /// <summary>Reading record 0, <c>$MFT</c>'s entry about itself, in whole clusters.</summary>
    FirstRecord,

    /// <summary>Parsing record 0, and reading its <c>$ATTRIBUTE_LIST</c> where that is kept outside it.</summary>
    AttributeList,

    /// <summary>Reading the extension records the list names for pieces of <c>$MFT</c>'s <c>$DATA</c>.</summary>
    ExtensionRecords,

    /// <summary>Reading any further extension records the list names for pieces of <c>$MFT</c>'s <c>$BITMAP</c>.</summary>
    BitmapPlacement,
}
