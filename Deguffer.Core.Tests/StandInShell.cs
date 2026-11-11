using System.Runtime.InteropServices;
using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>What the stand-in shell tells one item's sink once it has dealt with the item.</summary>
/// <param name="Result">The <c>HRESULT</c> for the item.</param>
/// <param name="Created">The item as it now is in the bin, or null where it is not there.</param>
internal sealed record ShellTold(int Result, IShellItem? Created)
{
    /// <summary>What the shell reported for every item it moved, measured on 2026-11-05.</summary>
    private const int MovedResult = 0x0027_0008;

    /// <summary>Moved to the bin, at <paramref name="binned"/>; where null, at a place the shell would not name.</summary>
    public static ShellTold Moved(string? binned = null) => new(MovedResult, new BinnedShellItem(binned));

    /// <summary>Reported as done with no item in the bin: deleted outright.</summary>
    public static ShellTold DeletedOutright { get; } = new(0, null);

    public static ShellTold Failed(int result) => new(result, null);
}

/// <summary>
/// Stands in for the shell's operation over one batch, as it was measured on 2026-11-05. Each item's
/// sink is asked before the item is dealt with; a sink that answers a failure, as one does on a
/// cancel, is told that failure and the shell reaches no item after it. Otherwise the item is dealt
/// with and its sink is told how.
/// </summary>
internal static class StandInShell
{
    public static RecycleOperation Telling(Func<string, ShellTold> deal) => (paths, sinks) =>
    {
        for (var i = 0; i < paths.Count; i++)
        {
            var asked = sinks[i].PreDeleteItem(flags: 0, item: null!);

            if (asked < 0)
            {
                sinks[i].PostDeleteItem(flags: 0, item: null!, result: asked, created: null);
                return $"Windows would not move this to the Recycle Bin (0x{asked:X8}). It is still where it was.";
            }

            var told = deal(paths[i]);
            sinks[i].PostDeleteItem(flags: 0, item: null!, told.Result, told.Created);
        }

        return null;
    };

    /// <summary>
    /// An operation that records every path it is handed in <paramref name="handed"/> and does
    /// nothing to them, for a test that shows the shell is never asked.
    /// </summary>
    public static RecycleOperation NeverAsked(List<string> handed) => (paths, _) =>
    {
        handed.AddRange(paths);
        return "The shell was asked.";
    };
}

/// <summary>An item in the bin that names its path, or refuses to where it has none, as the shell's can.</summary>
internal sealed class BinnedShellItem(string? path) : IShellItem
{
    public int GetDisplayName(uint kind, out IntPtr name)
    {
        name = path is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(path);
        return path is null ? unchecked((int)0x8000_4005) : 0;
    }

    public int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result) =>
        throw new NotSupportedException();

    public void GetParent(out IShellItem parent) => throw new NotSupportedException();

    public void GetAttributes(uint mask, out uint attributes) => throw new NotSupportedException();

    public void Compare(IShellItem other, uint hint, out int order) => throw new NotSupportedException();
}
