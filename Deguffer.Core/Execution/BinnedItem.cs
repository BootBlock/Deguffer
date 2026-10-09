using System.Runtime.InteropServices;

namespace Deguffer.Core.Execution;

/// <summary>
/// Hears where the shell put the item it moved to the Recycle Bin: <c>PostDeleteItem</c> hands back
/// the item as it now is in the bin. Measured on 2026-10-09: the item it names is in the account's
/// <c>$Recycle.Bin</c> folder on the same volume, with the file ID the file had before, because the
/// move is a rename on that volume.
///
/// <para>Every other notification is answered with <c>S_OK</c> and ignored. One item is heard,
/// because <see cref="ShellRecycleBin"/> recycles one item per operation.</para>
/// </summary>
[ComVisible(true)]
internal sealed class BinnedItem : IFileOperationProgressSink
{
    /// <summary><c>SIGDN_FILESYSPATH</c>: the item's path in the file system.</summary>
    private const uint FileSystemPath = 0x8005_8000;

    /// <summary>Where the bin put the item, or null where the shell did not say.</summary>
    public string? Path { get; private set; }

    /// <summary>
    /// Whether the shell reported deleting an item and handed back no item in the bin, which
    /// Microsoft documents as the item not having been recycled: it was deleted outright.
    /// </summary>
    public bool DeletedOutright { get; private set; }

    public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
    {
        if (result < 0)
        {
            return 0;
        }

        if (created is null)
        {
            DeletedOutright = true;
            return 0;
        }

        // An item with no path in the file system leaves the answer unknown, which a caller reads as a
        // move it cannot confirm, never as one it can.
        if (created.GetDisplayName(FileSystemPath, out var name) < 0)
        {
            return 0;
        }

        try
        {
            Path = Marshal.PtrToStringUni(name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }

        return 0;
    }

    public int StartOperations() => 0;

    public int FinishOperations(int result) => 0;

    public int PreRenameItem(uint flags, IShellItem item, IntPtr newName) => 0;

    public int PostRenameItem(uint flags, IShellItem item, IntPtr newName, int result, IShellItem? created) => 0;

    public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName) => 0;

    public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName, int result, IShellItem? created) => 0;

    public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName) => 0;

    public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName, int result, IShellItem? created) => 0;

    public int PreDeleteItem(uint flags, IShellItem item) => 0;

    public int PreNewItem(uint flags, IShellItem destination, IntPtr newName) => 0;

    public int PostNewItem(
        uint flags, IShellItem destination, IntPtr newName, IntPtr templateName, uint attributes, int result, IShellItem? created) => 0;

    public int UpdateProgress(uint total, uint done) => 0;

    public int ResetTimer() => 0;

    public int PauseTimer() => 0;

    public int ResumeTimer() => 0;
}

/// <summary>
/// What the shell tells a caller as it works, in its vtable order. Every method is declared, for the
/// reason <see cref="IFileOperation"/>'s are, and returns its <c>HRESULT</c> as a value, because an
/// exception thrown back into the shell would stop the operation for no reason it could report.
/// </summary>
[ComImport]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationProgressSink
{
    [PreserveSig]
    int StartOperations();

    [PreserveSig]
    int FinishOperations(int result);

    [PreserveSig]
    int PreRenameItem(uint flags, IShellItem item, IntPtr newName);

    [PreserveSig]
    int PostRenameItem(uint flags, IShellItem item, IntPtr newName, int result, IShellItem? created);

    [PreserveSig]
    int PreMoveItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName);

    [PreserveSig]
    int PostMoveItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName, int result, IShellItem? created);

    [PreserveSig]
    int PreCopyItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName);

    [PreserveSig]
    int PostCopyItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName, int result, IShellItem? created);

    [PreserveSig]
    int PreDeleteItem(uint flags, IShellItem item);

    /// <param name="created">The item as it now is in the Recycle Bin, or null where it was deleted outright.</param>
    [PreserveSig]
    int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);

    [PreserveSig]
    int PreNewItem(uint flags, IShellItem destination, IntPtr newName);

    [PreserveSig]
    int PostNewItem(uint flags, IShellItem destination, IntPtr newName, IntPtr templateName, uint attributes, int result, IShellItem? created);

    [PreserveSig]
    int UpdateProgress(uint total, uint done);

    [PreserveSig]
    int ResetTimer();

    [PreserveSig]
    int PauseTimer();

    [PreserveSig]
    int ResumeTimer();
}
