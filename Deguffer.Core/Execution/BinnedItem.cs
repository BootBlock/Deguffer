using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Deguffer.Core.Execution;

/// <summary>
/// Hears what the shell did to one item it was asked to move to the Recycle Bin: the item is queued
/// with this as its own sink, so every notification here is about it. <c>PostDeleteItem</c> hands
/// back the item as it now is in the bin. Measured on 2026-10-09: the item it names is in the
/// account's <c>$Recycle.Bin</c> folder on the same volume, with the file ID the file had before,
/// because the move is a rename on that volume.
///
/// <para><b>It stops the batch at this item</b> when the removal is cancelled, or when the batch was
/// stopped at an earlier item: <c>PreDeleteItem</c> answers <c>ERROR_CANCELLED</c>. Measured on
/// 2026-11-05: the shell then tells this item it was cancelled, leaves it where it was, and reaches
/// no item after it.</para>
///
/// <para>Every other notification is answered with <c>S_OK</c> and ignored.</para>
/// </summary>
/// <param name="stop">Shared by every sink of the batch: set once the batch is to stop, so the next item's sink stops it.</param>
/// <param name="heard">
/// Asked once the shell has told this item what became of it, before the shell reaches the next
/// item; where it answers true, <paramref name="stop"/> is set.
/// </param>
[ComVisible(true)]
internal sealed class BinnedItem(CancellationToken ct, StrongBox<bool>? stop = null, Func<BinnedItem, bool>? heard = null)
    : IFileOperationProgressSink
{
    /// <summary><c>SIGDN_FILESYSPATH</c>: the item's path in the file system.</summary>
    private const uint FileSystemPath = 0x8005_8000;

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_CANCELLED)</c>.</summary>
    private const int Cancelled = unchecked((int)0x8007_04C7);

    /// <summary>Where the bin put the item, or null where the shell did not say.</summary>
    public string? Path { get; private set; }

    /// <summary>Whether the shell reported moving the item and handed back the item in the bin.</summary>
    public bool Moved { get; private set; }

    /// <summary>
    /// Whether the shell reported deleting an item and handed back no item in the bin, which
    /// Microsoft documents as the item not having been recycled: it was deleted outright.
    /// </summary>
    public bool DeletedOutright { get; private set; }

    /// <summary>Whether this sink stopped the batch at this item because the removal was cancelled.</summary>
    public bool StoppedHere { get; private set; }

    /// <summary>Whether this sink stopped the batch at this item because it was stopped at an earlier one.</summary>
    public bool HeldBack { get; private set; }

    /// <summary>The <c>HRESULT</c> the shell reported failing to move the item with, or null.</summary>
    public int? Failure { get; private set; }

    /// <summary>Why the shell would not queue the item at all, or null where it did.</summary>
    public string? Refusal { get; private set; }

    /// <summary>Records that the shell would not queue the item, for <paramref name="why"/>.</summary>
    public void Refuse(string why) => Refusal = why;

    public int PreDeleteItem(uint flags, IShellItem item)
    {
        if (ct.IsCancellationRequested)
        {
            StoppedHere = true;
            return Cancelled;
        }

        if (stop?.Value == true)
        {
            HeldBack = true;
            return Cancelled;
        }

        return 0;
    }

    public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
    {
        Record(result, created);

        // The shell tells an item this sink stopped that it was cancelled. Nothing was done to it, so
        // the caller is not asked about it.
        if (StoppedHere || HeldBack)
        {
            return 0;
        }

        if (heard?.Invoke(this) == true && stop is not null)
        {
            stop.Value = true;
        }

        return 0;
    }

    private void Record(int result, IShellItem? created)
    {
        if (result < 0)
        {
            Failure = result;
            return;
        }

        if (created is null)
        {
            DeletedOutright = true;
            return;
        }

        Moved = true;

        // An item with no path in the file system leaves the answer unknown, which a caller reads as a
        // move it cannot confirm, never as one it can.
        if (created.GetDisplayName(FileSystemPath, out var name) < 0)
        {
            return;
        }

        try
        {
            Path = Marshal.PtrToStringUni(name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    public int StartOperations() => 0;

    public int FinishOperations(int result) => 0;

    public int PreRenameItem(uint flags, IShellItem item, IntPtr newName) => 0;

    public int PostRenameItem(uint flags, IShellItem item, IntPtr newName, int result, IShellItem? created) => 0;

    public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName) => 0;

    public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName, int result, IShellItem? created) => 0;

    public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName) => 0;

    public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, IntPtr newName, int result, IShellItem? created) => 0;

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
