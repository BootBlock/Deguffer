using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Testing;

/// <summary>A table that names a chain of folders, so it resolves a path a test also builds on the disk.</summary>
public static class MftFolders
{
    /// <summary>A table holding <paramref name="folders"/>, each inside the one before it, below the volume's top.</summary>
    /// <returns>The table, the record of the last folder, and the first record number still free.</returns>
    public static (MftFixture Fixture, uint Parent, uint Next) Through(IEnumerable<string> folders)
    {
        var fixture = new MftFixture();
        uint parent = MftRecord.RootRecordNumber;
        uint next = MftRecord.ReservedRecordCount;

        foreach (var folder in folders)
        {
            fixture.AddDirectory(next, parent, folder);
            parent = next++;
        }

        return (fixture, parent, next);
    }
}
