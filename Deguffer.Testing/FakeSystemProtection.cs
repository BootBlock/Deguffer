using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.SystemProtection;

namespace Deguffer.Testing;

/// <summary>
/// Stands in for Windows' System Protection: restore points the test chooses, removed from the list when
/// System Restore is asked to remove one, beside shadow copies and storage figures the test chooses.
///
/// <para><b>Injected everywhere, never defaulted.</b> The real one removes the restore points of whoever
/// runs the suite, so every fixture that plans a removal passes one of these.</para>
/// </summary>
public sealed class FakeSystemProtection : ISystemProtection
{
    private const string SystemVolume = @"C:\";

    /// <summary>The restore points System Restore lists now. A removal takes one out.</summary>
    public List<RestorePoint> Points { get; } = [];

    /// <summary>The shadow copies the service lists now.</summary>
    public List<ShadowCopy> Copies { get; } = [];

    /// <summary>What the restore point listing answers, where it does not list.</summary>
    public ListingAnswer PointsAnswer { get; set; } = ListingAnswer.Listed;

    /// <summary>What the shadow copy listing answers, where it does not list.</summary>
    public ListingAnswer CopiesAnswer { get; set; } = ListingAnswer.Listed;

    /// <summary>
    /// What each removal answers, by sequence number. A restore point not named here is removed.
    /// </summary>
    public Dictionary<uint, RemovalAnswer> Removals { get; } = [];

    /// <summary>Run after each restore point is removed, so a test can stand in for what Windows does then.</summary>
    public Action<uint>? OnRemoved { get; set; }

    /// <summary>What the system volume's storage holds now. Each removal frees <see cref="FreedPerRemoval"/>.</summary>
    public ShadowStorage Storage { get; set; } = new(Statement.Stated, UsedBytes: 0, AllocatedBytes: 0, MaximumBytes: 0);

    public long FreedPerRemoval { get; set; }

    /// <summary>Every other volume Windows is asked about, as it answers. None, unless a test adds one.</summary>
    public List<VolumeShadowStorage> OtherVolumes { get; } = [];

    /// <summary>Every sequence number System Restore was asked to remove, in order.</summary>
    public List<uint> Removed { get; } = [];

    /// <summary>How many times the restore points were listed.</summary>
    public int Listings { get; private set; }

    /// <summary>A restore point made <paramref name="daysAgo"/> days ago, numbered <paramref name="sequenceNumber"/>.</summary>
    public RestorePoint Add(uint sequenceNumber, int daysAgo, string description = "Windows Update")
    {
        var point = new RestorePoint(sequenceNumber, DateTime.Now.AddDays(-daysAgo), description);
        Points.Add(point);
        return point;
    }

    /// <summary>A shadow copy of the system volume, from <paramref name="provider"/> with <paramref name="attributes"/>.</summary>
    public ShadowCopy AddCopy(Guid provider, int attributes)
    {
        var copy = new ShadowCopy(Guid.NewGuid(), provider, attributes, @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", DateTime.Now);
        Copies.Add(copy);
        return copy;
    }

    public RestorePointListing ListRestorePoints()
    {
        Listings++;

        return PointsAnswer switch
        {
            ListingAnswer.Listed => RestorePointListing.Of([.. Points]),
            ListingAnswer.NeedsElevation => RestorePointListing.Refused,
            _ => RestorePointListing.Failed("System Restore did not list its restore points (error 0x80004005)."),
        };
    }

    public RemovalAnswer RemoveRestorePoint(uint sequenceNumber)
    {
        Removed.Add(sequenceNumber);
        var answer = Removals.GetValueOrDefault(sequenceNumber, RemovalAnswer.Removed);

        if (answer is RemovalAnswer.Removed)
        {
            Points.RemoveAll(p => p.SequenceNumber == sequenceNumber);
            Storage = Storage with { UsedBytes = Math.Max(0, Storage.UsedBytes - FreedPerRemoval) };
            OnRemoved?.Invoke(sequenceNumber);
        }

        return answer;
    }

    public ShadowCopyListing ListShadowCopies() => CopiesAnswer switch
    {
        ListingAnswer.Listed => ShadowCopyListing.Of([.. Copies]),
        ListingAnswer.NeedsElevation => ShadowCopyListing.Refused,
        _ => ShadowCopyListing.Failed("The Volume Shadow Copy service did not list its shadow copies (error 0x80004005)."),
    };

    public IReadOnlyList<VolumeShadowStorage> ReadStorage() => [new VolumeShadowStorage(SystemVolume, Storage), .. OtherVolumes];
}
