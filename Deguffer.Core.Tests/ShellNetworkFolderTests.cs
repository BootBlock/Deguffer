using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Network;

namespace Deguffer.Core.Tests;

/// <summary>
/// How the shell's Network folder is read: the computers among its items, and a listing that fails
/// part-way told as a refusal rather than offered as every computer there is (§5.3).
/// </summary>
public sealed class ShellNetworkFolderTests
{
    private const int Failed = unchecked((int)0x8000_4005);

    [Fact]
    public void OnlyTheComputersAmongTheItemsAreListed()
    {
        var items = new StandInItems(
            [@"\\nas.test", @"::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}\Provider\Media", @"\\printer.test", null],
            end: 1);

        var listed = ShellNetworkFolder.Names(items, CancellationToken.None);

        Assert.Null(listed.Failure);
        Assert.Equal(["nas.test", "printer.test"], listed.Entries);
    }

    [Fact]
    public void AListingThatFailsPartWayIsARefusal()
    {
        var listed = ShellNetworkFolder.Names(new StandInItems([@"\\nas.test"], end: Failed), CancellationToken.None);

        Assert.Equal(NetworkFailure.Refused, listed.Failure);
        Assert.Empty(listed.Entries);
    }

    /// <summary>The folder's items one at a time, each naming the path given, then <paramref name="end"/>.</summary>
    private sealed class StandInItems(IReadOnlyList<string?> paths, int end) : IEnumShellItems
    {
        private int _next;

        public int Next(uint count, out IShellItem item, out uint fetched)
        {
            if (_next < paths.Count)
            {
                item = new BinnedShellItem(paths[_next++]);
                fetched = 1;
                return 0;
            }

            item = null!;
            fetched = 0;
            return end;
        }

        public int Skip(uint count) => throw new NotSupportedException();

        public int Reset() => throw new NotSupportedException();

        public int Clone(out IEnumShellItems copy) => throw new NotSupportedException();
    }
}
