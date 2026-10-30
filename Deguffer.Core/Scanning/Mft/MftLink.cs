namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One more name a file has in a directory, beside the one <see cref="MftRecord"/> places it by: a
/// hard link.
///
/// <para>Every figure counts a file once for each of its names, as a directory listing does, so a
/// file is placed under each of these too. That is the walk's reading, and the removal's: a folder's
/// removal takes each name in it and counts each one it takes. A link is not a second copy, so
/// removing one name frees nothing while another remains, and a store whose figure has to say what
/// an eviction frees measures with <see cref="HardLinkAwareScanner"/> instead.</para>
///
/// <para>A name in the DOS namespace is never one of these. It is the 8.3 alias NTFS keeps beside a
/// long name in the same directory, and a listing never shows it as an entry of its own.</para>
/// </summary>
/// <param name="ParentRecordNumber">Record number of the directory holding this name.</param>
/// <param name="Name">The name, exactly as NTFS stored it.</param>
public readonly record struct MftLink(uint ParentRecordNumber, string Name);
