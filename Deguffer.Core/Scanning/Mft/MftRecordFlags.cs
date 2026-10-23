namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// What <see cref="MftVolumeTree"/> knows about a record beyond its numbers, one bit each.
///
/// <para>One byte a record rather than a <c>bool[]</c> per flag, because the tree is sized by the
/// volume: five arrays of a byte a slot were five megabytes a million records, and these are one.</para>
/// </summary>
[Flags]
internal enum MftRecordFlags : byte
{
    None = 0,

    /// <summary>See <see cref="MftVolumeTree.IsDirectory"/>.</summary>
    Directory = 1,

    /// <summary>See <see cref="MftVolumeTree.SizeUnknown"/>.</summary>
    SizeUnknown = 2,

    /// <summary>See <see cref="MftVolumeTree.IsReparsePoint"/>.</summary>
    ReparsePoint = 4,

    /// <summary>See <see cref="MftVolumeTree.IsMailStore"/>.</summary>
    MailStore = 8,

    /// <summary>See <see cref="MftVolumeTree.IsPresent"/>.</summary>
    Present = 16,
}
