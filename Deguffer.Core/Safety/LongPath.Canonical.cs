using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// A path put in the one form the in-use check compares: the spellings Windows opens as one place,
/// read as that place.
/// </summary>
public static partial class LongPath
{
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>
    /// A path in the one form every comparison in the in-use check relies on, or in display form
    /// where it cannot be put in that form. Null in, null out. <see cref="Canonical"/> says what the
    /// form is, and why each part of it is there.
    ///
    /// <para><b>This is a safety seam, not tidiness.</b> Whether a directory is in use is decided by
    /// asking whether a path a program holds sits inside it, and that test is a string comparison. A
    /// process working in <c>C:\Users\LONGPR~1\AppData\Local\Temp\build-123</c> is inside
    /// <c>C:\Users\LongProfileName\AppData\Local\Temp</c> and compares as though it were not, so the
    /// veto silently misses it — in the direction that deletes a directory somebody is working
    /// in.</para>
    ///
    /// <para><b>Observed rather than anticipated, and <c>%TEMP%</c> is where it bites.</b> Windows
    /// sets the per-user <c>TEMP</c> variable to the short form on a profile whose folder name
    /// exceeds eight characters, so a program that resolves its scratch folder from the environment
    /// — which is most of them — reports short paths. Both sides of a comparison are put in this
    /// form, here or through <see cref="Canonical"/>, so a short form on either side cannot make one
    /// folder look like two.</para>
    ///
    /// <para>For a caller with no better answer than the path itself. A caller that can report a
    /// path as unread asks <see cref="Canonical"/> instead, and does.</para>
    /// </summary>
    [return: NotNullIfNotNull(nameof(path))]
    public static string? Unaliased(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Canonicalised(path, Resolved, out _);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path Windows will accept, which is a misread rather than a directory. Handing it
            // back unchanged leaves it matching nothing, exactly as it did before.
            return path;
        }
    }

    /// <summary>
    /// A fully qualified path named the way the filesystem names it: in display form, with either
    /// separator read as the one Windows uses, <c>.</c> and <c>..</c> resolved, and every 8.3 alias
    /// expanded to the name it stands for. Null where that cannot be established.
    ///
    /// <para><b>Each of those is a spelling a program may be handed, and each compared as a
    /// different place.</b> The in-use check asks whether a path a program holds starts with a
    /// folder's, so <c>C:/Temp/profile-1</c>, <c>C:\Temp\x\..\profile-1</c> and
    /// <c>\\?\C:\Temp\profile-1</c> each read as outside <c>C:\Temp</c>, and the veto passed a
    /// profile somebody was using.</para>
    ///
    /// <para><b>An alias is expanded on the longest part of the path that exists</b>, not on the
    /// whole of it. <c>GetLongPathName</c> refuses a path any segment of which is missing, and a
    /// program is routinely started with a file it has not written yet, such as a log. Asked of the
    /// whole path, <c>C:\Users\LONGPR~1\...\run-1\out.log</c> kept its alias until the log appeared.
    /// What lies below the part that exists cannot be an alias, because an alias names something on
    /// the disk, so it is kept as it was spelled. The walk up covers every root, a volume's GUID name
    /// included, and finds a part Windows refused.</para>
    ///
    /// <para><b>Null is "cannot say", never a guess.</b> A path that is not fully qualified would
    /// have to be resolved against a working directory nobody named, and a part that carries a
    /// <c>~</c> but that Windows would not describe may be an alias for anything. Compared as it
    /// arrived, either would read as "nothing is using this", so the caller reports the path as
    /// unread instead.</para>
    ///
    /// <para>The disk is asked only about a path that carries a <c>~</c>, which every 8.3 segment
    /// does. Callers apply this to every process on the machine (G4), and casing alone changes no
    /// comparison here — every one of them is ordinal-ignore-case.</para>
    /// </summary>
    public static string? Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var form = Canonicalised(path, Resolved, out var whole);

            return whole ? form : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path Windows will accept, so there is no place it names to compare.
            return null;
        }
    }

    /// <summary>
    /// <see cref="Canonical"/> of the folder a program opens when it hands <paramref name="path"/> to
    /// Win32, where that is another folder than the one the path spells, or null where it is not or
    /// cannot be said.
    ///
    /// <para><b>For a path another program spelled</b>, such as an argument it was started with.
    /// Win32 drops trailing dots and spaces from the last name and one trailing dot from every other
    /// as it opens a path, so a browser started with <c>--user-data-dir=C:\Temp\pw.</c> works in
    /// <c>C:\Temp\pw</c>. Compared only as spelled, that profile read as unused, in the direction that
    /// deletes it. A path Windows itself reports, such as a working directory, already names the
    /// folder as it is, so <see cref="Canonical"/> alone answers for it.</para>
    /// </summary>
    public static string? CanonicalAsOpened(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var opened = Canonicalised(path, Path.GetFullPath, out var whole);

            return whole && !opened.Equals(Canonicalised(path, Resolved, out _), StringComparison.Ordinal) ? opened : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path Windows will accept, so it opens nothing.
            return null;
        }
    }

    /// <summary>
    /// Whether any of <paramref name="candidates"/> may be <paramref name="ancestor"/> or sit inside
    /// it: <see cref="Contains"/> asked of both sides in <see cref="Canonical"/> form, and true for
    /// a candidate where either side has none.
    ///
    /// <para><b>For a guard whose "no" lets something be deleted.</b> A path a program or Windows
    /// recorded may be spelled with an 8.3 alias, and compared as spelled it reads as outside the
    /// folder it names, in the direction that deletes. A side that cannot be put in canonical form
    /// may be an alias for the very folder asked about, so it cannot be ruled out.</para>
    ///
    /// <para>Asked of every candidate at once because the ancestor is put in canonical form once:
    /// a folder such as <c>$Windows.~BT</c> carries a <c>~</c>, so each of its forms asks the
    /// disk.</para>
    /// </summary>
    public static bool MayContainAny(string ancestor, IEnumerable<string> candidates) =>
        Canonical(ancestor) is { } folder
            ? candidates.Any(candidate => Canonical(candidate) is not { } path || Contains(folder, path))
            : candidates.Any();

    /// <param name="whole">
    /// Whether the answer is the canonical form, rather than the best spelling available of a path
    /// that has none.
    /// </param>
    /// <param name="resolve">
    /// How the path's names are read: as spelled (<see cref="Resolved"/>), or as Win32 opens them
    /// (<see cref="Path.GetFullPath(string)"/>).
    /// </param>
    private static string Canonicalised(string path, Func<string, string> resolve, out bool whole)
    {
        var shown = Display(path);

        if (!Path.IsPathFullyQualified(shown))
        {
            whole = false;
            return shown;
        }

        // Safe only because the path is fully qualified, so nothing resolves against Deguffer's own
        // working directory. A path that keeps its device prefix, such as a volume GUID's, comes
        // back unresolved, which is right: Windows resolves nothing in it either. Names are read
        // as the caller asks: Canonical keeps them as spelled, because a folder named "build." holds
        // what a program is using in it, and compared as "build" it would hold nothing.
        var full = resolve(shown);
        var existing = full;
        var below = "";

        whole = true;

        while (existing.Contains('~', StringComparison.Ordinal))
        {
            if (LongName(existing, out var error) is { } expanded)
            {
                return Path.Join(expanded, below);
            }

            if (!NamesNothing(error))
            {
                whole = false;
                return full;
            }

            if (Path.GetDirectoryName(existing) is not { } parent)
            {
                // Not even the root is there, so nothing in the path is an alias for anything.
                return full;
            }

            below = Path.Join(Path.GetFileName(existing), below);
            existing = parent;
        }

        return full;
    }

    /// <summary>
    /// Whether <paramref name="win32Error"/> says that no such entry is on the disk, so that nothing
    /// at or below the path can be an alias.
    ///
    /// <para><b>Narrower than <see cref="SaysNothingIsThere"/> on purpose.</b> That set answers "is
    /// anything here to look at", where a share that will not answer and a drive with no media are
    /// complete answers. Here they are not: a segment on a share that refused may be an alias for
    /// the very folder being asked about, and reading the refusal as absence would hand the alias
    /// back as canonical. Anything outside this set therefore reads as "cannot say".</para>
    /// </summary>
    private static bool NamesNothing(int win32Error) => win32Error is
        2 or      // ERROR_FILE_NOT_FOUND
        3 or      // ERROR_PATH_NOT_FOUND
        123 or    // ERROR_INVALID_NAME: a segment no entry can be named
        161;      // ERROR_BAD_PATHNAME

    /// <summary>
    /// What <c>GetLongPathName</c> answers for <paramref name="path"/>, in display form, or null and
    /// the Win32 error it gave.
    /// </summary>
    private static string? LongName(string path, out int error)
    {
        var extended = Extended(path);
        var length = GetLongPathName(extended, null, 0);

        if (length == 0)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        var buffer = new char[length];
        var written = GetLongPathName(extended, buffer, length);

        // The first call sizes the buffer including the terminator, so a successful second call
        // writes strictly fewer characters than that. A larger answer is a path that changed between
        // the two calls, which describes nothing, so it is read as a refusal.
        if (written > 0 && written < length)
        {
            error = 0;
            return Display(new string(buffer, 0, (int)written));
        }

        error = written == 0 ? Marshal.GetLastPInvokeError() : ErrorInsufficientBuffer;
        return null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathName(string path, [Out] char[]? buffer, uint length);
}
