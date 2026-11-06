using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// One entry to record, as the thing that found it saw it.
///
/// <para>The walk hands its names over as its listing lent them (<see cref="FileNames.AllLent"/>), so
/// a child it describes holds no string of its own and can be read only while its directory's
/// contents can. <see cref="ExploreTreeBuilder.AddChildren"/> copies every name into the tree's pool
/// before it returns.</para>
/// </summary>
public readonly record struct ExploreChild
{
    private readonly ListedName _name;

    /// <param name="Name">The leaf name, with no path.</param>
    /// <param name="IsDirectory">Whether the entry is a folder.</param>
    /// <param name="IsLink">Whether the entry is a link, whose target holds its own place.</param>
    /// <param name="Size">See <see cref="Size"/>.</param>
    /// <param name="Created">When the entry was made, or unknown where nothing established it.</param>
    /// <param name="LastWritten">See <see cref="LastWritten"/>.</param>
    /// <param name="Length">See <see cref="Length"/>.</param>
    /// <param name="Storage">Why <paramref name="Size"/> is less than the length, where anything says.</param>
    /// <param name="SizeUnknown">See <see cref="SizeUnknown"/>.</param>
    /// <param name="Visibility">Whether the entry is hidden or a system file, as its attributes say.</param>
    public ExploreChild(
        string Name,
        bool IsDirectory,
        bool IsLink,
        long Size,
        ExploreTimestamp Created = default,
        ExploreTimestamp LastWritten = default,
        long? Length = null,
        FileStorage Storage = FileStorage.Plain,
        bool SizeUnknown = false,
        FileVisibility Visibility = FileVisibility.Shown)
        : this(ListedName.Own(Name), IsDirectory, IsLink, Size, Created, LastWritten, Length, Storage, SizeUnknown, Visibility)
    {
    }

    /// <summary>A child named as its listing kept the name, which may be lent.</summary>
    internal ExploreChild(
        ListedName name,
        bool IsDirectory,
        bool IsLink,
        long Size,
        ExploreTimestamp Created = default,
        ExploreTimestamp LastWritten = default,
        long? Length = null,
        FileStorage Storage = FileStorage.Plain,
        bool SizeUnknown = false,
        FileVisibility Visibility = FileVisibility.Shown)
    {
        _name = name;
        this.IsDirectory = IsDirectory;
        this.IsLink = IsLink;
        this.Size = Size;
        this.Created = Created;
        this.LastWritten = LastWritten;
        this.Length = Length;
        this.Storage = Storage;
        this.SizeUnknown = SizeUnknown;
        this.Visibility = Visibility;
    }

    /// <summary>The leaf name, with no path. A name the walk lent is built into a new string on each call.</summary>
    public string Name
    {
        get => _name.ToString();
        init => _name = ListedName.Own(value);
    }

    public bool IsDirectory { get; init; }

    public bool IsLink { get; init; }

    /// <summary>
    /// The bytes a file occupies on the disk, which is what the picture draws. Zero for a directory: a
    /// directory's own bytes are its children's, and they are recorded separately, so anything counted
    /// here would be counted twice.
    /// </summary>
    public long Size { get; init; }

    public ExploreTimestamp Created { get; init; }

    /// <summary>
    /// When the entry itself was last written. A directory's own timestamp is meant here, not its
    /// contents' — <see cref="ExploreTree.ModifiedOf"/> is what combines the two, and it can only do
    /// that if what it is given is the node's own.
    /// </summary>
    public ExploreTimestamp LastWritten { get; init; }

    /// <summary>
    /// The file's length, where it is not <see cref="Size"/>. Null where the two are one figure, which
    /// is every file a listing alone describes.
    /// </summary>
    public long? Length { get; init; }

    public FileStorage Storage { get; init; }

    /// <summary>
    /// Whether the file system would not say what the file occupies, so <see cref="Size"/> is a lower
    /// bound and so is every total above it.
    /// </summary>
    public bool SizeUnknown { get; init; }

    public FileVisibility Visibility { get; init; }

    /// <summary>The name as it was handed over, which is what the builder sorts and pools.</summary>
    internal ListedName KeptName => _name;
}
