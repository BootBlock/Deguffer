using System.IO.Enumeration;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Safety;

/// <summary>
/// One directory's immediate entries, with the reason the listing ended reported as a
/// <see cref="PathPresence"/> rather than thrown.
///
/// <para><b>Why not an exception.</b> §5.3 makes a refused or vanished directory ordinary, and a
/// walk of a whole volume meets hundreds of them. Every one thrown is a first-chance exception: a
/// stack unwind that costs far more than the listing it ends, and a line in a debugger's output
/// that buries the one exception that matters. .NET's enumerator asks <see cref="ContinueOnError"/>
/// before it throws, both when the directory is opened and when a read fails part-way, so answering
/// there is the whole mechanism.</para>
///
/// <para><b>The three answers are the ones every listing here gives</b>, read from the error as
/// <see cref="ListingEnd.Failed"/> reads it. <see cref="PathPresence.Present"/> means the directory
/// was listed to the end.</para>
///
/// <para><b>Entries yielded before a failure are real</b>, and <see cref="Outcome"/> is only settled
/// once <see cref="FileSystemEnumerator{T}.MoveNext"/> has returned false. Whether a partial listing
/// is kept is the caller's decision: a walk totalling bytes keeps it, and a classification that
/// must not describe a folder nobody fully read discards it.</para>
///
/// <para>Hidden and system entries are included, and a refusal is never skipped silently, since a
/// skipped entry is a partial view. <paramref name="directory"/> is extended (§6.3), so every entry
/// carries the prefix too.</para>
/// </summary>
internal sealed class DirectoryListing<T> : FileSystemEnumerator<T>
{
    private readonly FileSystemEnumerable<T>.FindTransform _transform;
    private readonly FileSystemEnumerable<T>.FindPredicate? _include;

    // Can be written from the base constructor, which opens the directory before this constructor's
    // body runs.
    private int _error;

    public DirectoryListing(
        string directory,
        FileSystemEnumerable<T>.FindTransform transform,
        FileSystemEnumerable<T>.FindPredicate? include = null)
        : base(LongPath.Extended(directory), DirectoryListing.EveryEntry)
    {
        _transform = transform;
        _include = include;
    }

    /// <summary>How the listing ended. Read it once enumeration has finished.</summary>
    public PathPresence Outcome => _error == 0 ? PathPresence.Present : ListingEnd.Failed(_error, whileReading: false).Presence;

    /// <summary>
    /// Whether the listing was refused because the account may not read the directory, as against
    /// any other refusal. Read it once enumeration has finished.
    /// </summary>
    public bool WasDenied => _error != 0 && ListingEnd.Failed(_error, whileReading: false).Denied;

    protected override T TransformEntry(ref System.IO.Enumeration.FileSystemEntry entry) => _transform(ref entry);

    protected override bool ShouldIncludeEntry(ref System.IO.Enumeration.FileSystemEntry entry) => _include?.Invoke(ref entry) ?? true;

    /// <summary>Record the error and end the listing, which is what the base does after it.</summary>
    protected override bool ContinueOnError(int error)
    {
        _error = error;
        return true;
    }
}

/// <summary>The listings every caller here asks for.</summary>
internal static class DirectoryListing
{
    /// <summary>
    /// Every entry, hidden and system ones included, with the listing buffer left to the runtime. The
    /// safety listings all use this one.
    /// </summary>
    public static readonly EnumerationOptions EveryEntry = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
    };

    /// <summary>Every entry as a <see cref="FileSystemInfo"/>, which carries what the listing read.</summary>
    public static DirectoryListing<FileSystemInfo> Of(string directory) =>
        new(directory, static (ref System.IO.Enumeration.FileSystemEntry entry) => entry.ToFileSystemInfo());
}
