using Deguffer.Core.Safety;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// A path as a hypervisor's record writes it, which may be relative to the folder the record is in.
/// </summary>
internal static class RecordedPath
{
    /// <summary>
    /// <paramref name="value"/> made full against <paramref name="folder"/>, or null where it is empty or
    /// does not name a full path once resolved. A drive-relative value such as <c>C:disk.vmdk</c> is not
    /// one a hypervisor writes and resolves to null, rather than against Deguffer's own directory.
    /// </summary>
    public static string? Resolve(string folder, string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : LongPath.Configured(Path.IsPathRooted(value) ? value : Path.Combine(folder, value));
}
