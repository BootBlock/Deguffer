namespace Deguffer.Benchmark;

/// <summary>How the links route reads each file's link count and sizes.</summary>
internal enum LinkRead
{
    /// <summary>By name, with no handle, as a scan does where the file system answers that.</summary>
    Name,

    /// <summary>Through a handle opened on each file, as a scan does where the file system does not.</summary>
    Handle,
}
