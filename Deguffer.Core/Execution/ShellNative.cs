using System.Runtime.InteropServices;

namespace Deguffer.Core.Execution;

/// <summary>
/// The shell interfaces <see cref="ShellRecycleBin"/> calls, declared no further than it needs.
///
/// <para>Every method of <c>IFileOperation</c> is declared even though four are used, because a COM
/// interface is a vtable: the runtime calls the slot at the declared position, so an omitted method
/// does not fail to compile — it silently shifts every method after it onto the wrong slot. That is
/// the failure this file exists to make impossible, so the unused members are declared in order with
/// their arguments left as opaque pointers, which is enough to occupy the slot correctly and is the
/// honest shape for a parameter nothing here constructs.</para>
///
/// <para>Declared without <c>PreserveSig</c>, so a failing HRESULT arrives as a
/// <see cref="COMException"/> rather than as a number a caller can forget to read. The one caller
/// catches it.</para>
/// </summary>
internal static class ShellNative
{
    /// <summary>
    /// The shell item for a path. It does not accept an extended-length path — see
    /// <see cref="IRecycleBin"/> for what that means for §6.3 here.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    internal static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}

/// <summary>One item in the shell namespace. Declared to <c>Compare</c> and no further.</summary>
[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);

    void GetParent(out IShellItem parent);

    void GetDisplayName(uint kind, out IntPtr name);

    void GetAttributes(uint mask, out uint attributes);

    void Compare(IShellItem other, uint hint, out int order);
}

/// <summary>
/// The shell's file-operation engine. <c>SetOperationFlags</c>, <c>DeleteItem</c>,
/// <c>PerformOperations</c> and <c>GetAnyOperationsAborted</c> are called; the rest hold their
/// vtable slots.
/// </summary>
[ComImport]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    void Advise(IntPtr sink, out uint cookie);

    void Unadvise(uint cookie);

    void SetOperationFlags(uint flags);

    void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);

    void SetProgressDialog(IntPtr dialog);

    void SetProperties(IntPtr properties);

    void SetOwnerWindow(IntPtr owner);

    void ApplyPropertiesToItem(IShellItem item);

    void ApplyPropertiesToItems(IntPtr items);

    void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);

    void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string newName);

    void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);

    void MoveItems(IntPtr items, IShellItem destination);

    void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string copyName, IntPtr sink);

    void CopyItems(IntPtr items, IShellItem destination);

    void DeleteItem(IShellItem item, IntPtr sink);

    void DeleteItems(IntPtr items);

    void NewItem(
        IShellItem destination,
        uint fileAttributes,
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string templateName,
        IntPtr sink);

    void PerformOperations();

    void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}
