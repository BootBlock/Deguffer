namespace Deguffer.Core.Providers;

/// <summary>One entry inside a <c>.vs</c> folder, as the listing of the folder holding it described it.</summary>
/// <param name="Name">The entry's own name.</param>
/// <param name="Path">The entry's path as the plan names it, without the long-path prefix.</param>
/// <param name="Attributes">Whether it is a folder, and whether it is a link, are read from these.</param>
/// <param name="LastWriteTimeUtc">When it was last written, which dates a cache without reading it again.</param>
internal readonly record struct VisualStudioEntry(
    string Name,
    string Path,
    FileAttributes Attributes,
    DateTime LastWriteTimeUtc)
{
    public bool IsLink => Attributes.HasFlag(FileAttributes.ReparsePoint);

    public TargetKind Kind => Attributes.HasFlag(FileAttributes.Directory) ? TargetKind.Directory : TargetKind.File;
}

/// <summary>
/// One folder inside a <c>.vs</c> folder as one listing read it, with the listings of the folders
/// inside it that the same reading entered. See <see cref="VisualStudioCacheReader"/> for which those
/// are.
/// </summary>
/// <param name="Path">The folder, without the long-path prefix.</param>
/// <param name="Entries">
/// Every entry directly inside it, or null where it refused to be listed or failed partway, on
/// <see cref="FolderEntries"/>' terms.
/// </param>
/// <param name="Inner">
/// The listings of the folders among <paramref name="Entries"/> the reading entered, by name exactly as
/// listed, so two folders a case-sensitive folder tells apart are never read as one.
/// </param>
internal sealed record VisualStudioListing(
    string Path,
    IReadOnlyList<VisualStudioEntry>? Entries,
    IReadOnlyDictionary<string, VisualStudioListing> Inner)
{
    /// <summary>
    /// The listing of <paramref name="folder"/>, one of <see cref="Entries"/>. The reading enters every
    /// folder the lock-file list and the examination look inside, so a folder it did not enter is
    /// a defect in the reader, and throws rather than reading as empty.
    /// </summary>
    public VisualStudioListing Inside(VisualStudioEntry folder) => Inner[folder.Name];

    /// <summary>Whether this listing found an entry called <paramref name="name"/>.</summary>
    public bool Holds(string name) =>
        Entries?.Any(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? false;
}
