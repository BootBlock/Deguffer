using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// <c>UNICODE_STRING</c>, naming an object to the native calls in this process's own memory. The
/// process table reads its own copy from another process, where the buffer is an address in that
/// process rather than a pointer here.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct UnicodeString
{
    public ushort Length;
    public ushort MaximumLength;
    public char* Buffer;
}

/// <summary><c>OBJECT_ATTRIBUTES</c>: the name a native call opens or asks about, and how it is matched.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ObjectAttributes
{
    public uint Length;
    public nint RootDirectory;
    public UnicodeString* ObjectName;
    public uint Attributes;
    public nint SecurityDescriptor;
    public nint SecurityQualityOfService;
}

/// <summary><c>IO_STATUS_BLOCK</c>: a status or pointer, then the information it returns.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoStatusBlock
{
    public nint Status;
    public nuint Information;
}
