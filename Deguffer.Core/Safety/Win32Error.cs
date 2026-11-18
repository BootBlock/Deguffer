namespace Deguffer.Core.Safety;

/// <summary>
/// The Win32 error codes Core names, each declared once. A code a native call answers is compared
/// against these rather than against a number, or a constant of its own.
/// </summary>
internal static class Win32Error
{
    /// <summary><c>ERROR_INVALID_FUNCTION</c>: what a file system answers for a call it does not implement.</summary>
    public const int InvalidFunction = 1;

    /// <summary><c>ERROR_FILE_NOT_FOUND</c>.</summary>
    public const int FileNotFound = 2;

    /// <summary><c>ERROR_PATH_NOT_FOUND</c>: a folder on the way to the name is not there.</summary>
    public const int PathNotFound = 3;

    /// <summary><c>ERROR_ACCESS_DENIED</c>.</summary>
    public const int AccessDenied = 5;

    /// <summary><c>ERROR_NOT_SUPPORTED</c>.</summary>
    public const int NotSupported = 50;

    /// <summary><c>ERROR_INVALID_PARAMETER</c>.</summary>
    public const int InvalidParameter = 87;

    /// <summary><c>ERROR_DIR_NOT_EMPTY</c>: a folder held up by what is still inside it.</summary>
    public const int DirectoryNotEmpty = 145;

    /// <summary><c>ERROR_FILENAME_EXCED_RANGE</c>.</summary>
    public const int NameTooLong = 206;

    /// <summary><c>ERROR_DIRECTORY</c>: a folder was asked for, and something else is at the name.</summary>
    public const int NotADirectory = 267;
}
