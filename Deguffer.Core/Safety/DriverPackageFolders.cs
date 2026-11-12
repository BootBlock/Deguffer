using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <inheritdoc />
/// <remarks>
/// <para><b>Each folder is asked of SetupAPI.</b> <c>SetupGetInfDriverStoreLocation</c> is the
/// documented question, and it answers without administrator rights. The registry's
/// <c>DriverDatabase</c> names the folders too, and was measured to list 33 of 167 packages to an
/// account that is not elevated.</para>
/// </remarks>
public sealed partial class DriverPackageFolders : IDriverPackageFolders
{
    /// <summary>Longer than any folder the store names, which are a few dozen characters under it.</summary>
    private const int LocationCapacity = 1024;

    private DriverPackageFolders()
    {
    }

    public static DriverPackageFolders Default { get; } = new();

    public string? FolderOf(string publishedName)
    {
        var buffer = new char[LocationCapacity];

        return SetupGetInfDriverStoreLocation(publishedName, IntPtr.Zero, IntPtr.Zero, buffer, buffer.Length, out var length)
            && length > 1
            && Path.GetDirectoryName(new string(buffer, 0, length - 1)) is { Length: > 0 } folder
                ? folder
                : null;
    }

    [LibraryImport(
        "setupapi.dll",
        EntryPoint = "SetupGetInfDriverStoreLocationW",
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupGetInfDriverStoreLocation(
        string fileName,
        IntPtr alternatePlatformInfo,
        IntPtr localeName,
        [Out] char[] returnBuffer,
        int returnBufferSize,
        out int requiredSize);
}
