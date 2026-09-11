namespace Deguffer.Core.Safety;

/// <summary>
/// Why Windows would not let a removal take a file.
///
/// <para><b>Two reasons, because they ask different things of the reader.</b> A file another
/// program has open is released when that program closes it, so the answer is to close something.
/// A file Windows refuses for any other reason stays refused however long the user waits: an access
/// rule, security software guarding a folder, or an executable something is running from. Reporting
/// both as "in use" sent the reader looking for a program that was not there — observed on a
/// workstation where security software guarded 5.9 GB of browser profiles in <c>%TEMP%</c>, and
/// every clean reported a quarter of a million files "in use" and then offered them again.</para>
/// </summary>
public enum RefusalReason
{
    /// <summary>Another program has the file open in a way that stops it being deleted.</summary>
    InUse,

    /// <summary>
    /// Windows refused for any reason other than the file being open. Worded on screen as Windows
    /// not letting Deguffer remove it, never as a permission, because this is also what a running
    /// executable and a guarding filter driver produce.
    /// </summary>
    Denied,
}

public static class RefusalReasons
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// The reason behind a deletion that threw. .NET carries the Win32 error in the HResult of an
    /// <see cref="IOException"/>, and raises <see cref="UnauthorizedAccessException"/> for an access
    /// refusal, so the type and the code between them are the whole of the answer.
    /// </summary>
    public static RefusalReason Of(Exception refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return refusal is IOException io && IsInUse(io.HResult & 0xFFFF)
            ? RefusalReason.InUse
            : RefusalReason.Denied;
    }

    /// <summary>
    /// The reason behind an open for deletion that failed with <paramref name="win32Error"/>, or
    /// null where the error says nothing is there to refuse.
    /// </summary>
    public static RefusalReason? OfWin32Error(int win32Error) => win32Error switch
    {
        // ERROR_FILE_NOT_FOUND and ERROR_PATH_NOT_FOUND. A file that went is not one Windows kept.
        2 or 3 => null,
        _ when IsInUse(win32Error) => RefusalReason.InUse,
        _ => RefusalReason.Denied,
    };

    private static bool IsInUse(int win32Error) =>
        win32Error is ErrorSharingViolation or ErrorLockViolation;
}
