using System.Runtime.InteropServices;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Duplicates;

/// <summary>Opens a file by its path, in the form it reaches Windows, to hold it as <see cref="FileInformation.OpenHeld"/> does.</summary>
internal delegate SafeFileHandle CopyOpener(string extendedPath, HeldFor use);

/// <summary>
/// A copy held open for a removal (§7.4), once its path is shown to lead straight to the file the
/// search found, unchanged and on this device: the copy a group keeps, held refusing every other
/// program's write, rename and delete, or the copy to remove, held refusing every other program's
/// write.
///
/// <para><b>By its path, held by its description.</b> A file opened by its number can still be
/// renamed and deleted by another program, and cannot be deleted through its handle, so a copy is
/// opened by its path, its own name never followed. The path is first described through an
/// attributes-only handle, which is held as long as the copy is: while it is held Windows refuses to
/// rename any folder above the file, and its final path must be the copy's path, so no link is on the
/// way. Then the handle opened by the path is described too, and must be the same file, with the
/// identity, length, last-modified time and attributes the search found.</para>
/// </summary>
internal sealed class HeldCopy : IDisposable
{
    /// <summary><c>ERROR_SHARING_VIOLATION</c>: another program has the file open in a way the open would conflict with.</summary>
    private const int SharingViolation = 32;

    private readonly HeldFile _described;

    private HeldCopy(DuplicateCandidate copy, HeldFile described, SafeFileHandle content)
    {
        Copy = copy;
        _described = described;
        Content = content;
    }

    public DuplicateCandidate Copy { get; }

    /// <summary>The handle the copy is read, and where it is removed permanently, deleted through.</summary>
    public SafeFileHandle Content { get; }

    /// <summary><paramref name="copy"/> held for <paramref name="use"/>, or which check it failed and why.</summary>
    public static (HeldCopy? Held, RemovalCheck Check, string Why) Hold(
        DuplicateCandidate copy, HeldFor use, FileInformation files, CopyOpener open)
    {
        var described = files.Hold(copy.Path, copy.Route);

        if (ContentReader.Judge(described.Reading, copy, attributes: null) is { } before)
        {
            described.Dispose();
            return (null, Check(before), Sentence(before));
        }

        var description = described.Reading.Description!;

        if (!string.Equals(description.Path, copy.Path, StringComparison.Ordinal) || description.Attributes != copy.Attributes)
        {
            described.Dispose();
            return (null, RemovalCheck.Changed, Sentence(ContentReadResult.Changed));
        }

        var content = open(LongPath.Extended(copy.Path), use);

        if (content.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            content.Dispose();
            described.Dispose();

            return (null, RemovalCheck.Unreadable, error == SharingViolation
                ? "Another program has it open in a way that would let it change, so Deguffer could not hold it while it compared it."
                : $"Windows would not open it to compare it (error {error}).");
        }

        var held = new HeldCopy(copy, described, content);

        if (held.Recheck(files) is { } opened)
        {
            held.Dispose();
            return (null, opened.Check, opened.Why);
        }

        return (held, RemovalCheck.Removed, string.Empty);
    }

    /// <summary>
    /// Which check the file held fails now, described through the handle it is held by, or null
    /// where it is still the copy as the search found it, on this device.
    /// </summary>
    public (RemovalCheck Check, string Why)? Recheck(FileInformation files) =>
        ContentReader.Judge(files.Describe(Content, Copy.Route), Copy, Copy.Attributes) is { } result
            ? (Check(result), Sentence(result))
            : null;

    public void Dispose()
    {
        Content.Dispose();
        _described.Dispose();
    }

    private static RemovalCheck Check(ContentReadResult result) => result switch
    {
        ContentReadResult.Gone => RemovalCheck.Gone,
        ContentReadResult.OnlyInTheCloud => RemovalCheck.OnlyInTheCloud,
        ContentReadResult.Changed => RemovalCheck.Changed,
        _ => RemovalCheck.Unreadable,
    };

    private static string Sentence(ContentReadResult result) => result switch
    {
        ContentReadResult.Gone => "It is no longer there.",
        ContentReadResult.OnlyInTheCloud => "It went online-only since the search, so it is not on this computer to compare.",
        ContentReadResult.Changed =>
            "It is not the file the search found as the search found it: another file or a link is at its path, or its length, "
            + "last-modified time or attributes changed.",
        _ => "Windows would not describe it, so it may still be there.",
    };
}
