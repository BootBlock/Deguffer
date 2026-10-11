using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;

namespace Deguffer.Core.Exploring.History;

/// <summary>
/// The map moving from the earlier scan a comparison is against to the tree on screen now (#285): one
/// tree at each step from the earlier scan (step 0) to now (<see cref="Steps"/>), with every size part
/// way between the two (<see cref="At"/>).
///
/// <para><b>Rebuilt from what the earlier scan recorded</b>, which is its largest folders and every
/// folder above them (<see cref="ScanSummary"/>). Below a recorded folder, the rest of what it held
/// then, its files and its folders too small to record, is one block, which says so
/// (<see cref="GrowthText.Unrecorded"/>). A folder recorded then and not on the disk now starts at its
/// earlier size and shrinks away, with what was recorded under it. Whatever is on the disk now and was
/// not recorded grows from nothing, so a block and the files that replaced it trade places rather than
/// one turning into the other: nothing says which of them were there then.</para>
///
/// <para><b>Between the two ends nothing was measured.</b> Each size moves in a straight line from one
/// scan to the other, so a step part way is an estimate, which the page says. Each step is laid out
/// as any treemap is, largest first, so a folder that grows past its neighbour moves ahead of it, and
/// the map shows that as the shapes travelling. Siblings of one size keep their name order at every
/// step (#309), so only a real change of size moves a shape.</para>
///
/// <para>The nodes of the tree now keep their numbers at every step, and what only the earlier scan
/// had is numbered after them, so a node is the same thing at every step and in the tree now (see
/// <see cref="GrowthReplayFrame.CounterpartsIn"/>). That is what lets the map move from one step to
/// the next.</para>
///
/// <para><b>A picture only</b> (§7.1). Nothing in a step answers the pointer, so nothing in it can be
/// picked, opened or removed: see <see cref="Rendering.MapReading.Answers"/>.</para>
/// </summary>
public sealed class GrowthReplay
{
    /// <summary>
    /// How many steps there are from the earlier scan to now. Twelve moves of a quarter of a second
    /// each play the change in about three seconds, and each move is short enough to follow.
    /// </summary>
    public const int Steps = 12;

    /// <summary>What the earlier scan recorded at or below each node of the tree now that it recorded.</summary>
    private readonly Dictionary<int, long> _before;

    /// <summary>
    /// The shapes only the earlier scan had, numbered from <see cref="TodayCount"/>: the folder above
    /// each, its name, or null for the block of what was not recorded, and what it held then.
    /// </summary>
    private readonly int[] _extraParents;

    private readonly string?[] _extraNames;

    private readonly long[] _extraBytes;

    /// <summary>The shapes only the earlier scan had, under each node that holds any.</summary>
    private readonly Dictionary<int, int[]> _extrasUnder;

    /// <summary>Each step once made, so the map is handed the same tree for the same step (G5).</summary>
    private readonly GrowthReplayFrame?[] _frames = new GrowthReplayFrame?[Steps + 1];

    private GrowthReplay(
        ScanGrowth growth,
        ExploreTree today,
        Dictionary<int, long> before,
        List<(int Parent, string? Name, long Bytes)> extras)
    {
        Growth = growth;
        Today = today;
        _before = before;
        _extraParents = [.. extras.Select(extra => extra.Parent)];
        _extraNames = [.. extras.Select(extra => extra.Name)];
        _extraBytes = [.. extras.Select(extra => extra.Bytes)];
        _extrasUnder = Enumerable.Range(0, extras.Count)
            .GroupBy(extra => extras[extra].Parent)
            .ToDictionary(under => under.Key, under => under.Select(extra => TodayCount + extra).ToArray());
    }

    /// <summary>The comparison replayed.</summary>
    public ScanGrowth Growth { get; }

    /// <summary>
    /// The tree on screen now: the scan the comparison is of, or what is left of it after a removal
    /// from the page (<see cref="ScanGrowth.Describes"/>).
    /// </summary>
    public ExploreTree Today { get; }

    /// <summary>How many nodes the tree now numbers. Every node from here on is a shape only the earlier scan had.</summary>
    public int TodayCount => Today.NodeCount;

    /// <summary>
    /// The replay of <paramref name="growth"/> ending at <paramref name="today"/>, or null where the
    /// comparison is not of that tree.
    /// </summary>
    public static GrowthReplay? Of(ScanGrowth? growth, ExploreTree? today)
    {
        if (growth is null || today is null || !growth.Describes(today))
        {
            return null;
        }

        var folders = growth.Earlier.Folders;
        var placed = growth.Placed;
        var before = new Dictionary<int, long>(folders.Count);
        var extras = new List<(int Parent, string? Name, long Bytes)>();

        // Where each recorded folder is drawn, and how much of what it held its recorded folders
        // account for. Parents come first in a summary, so a folder's parent is always placed first.
        var drawnAs = new int[folders.Count];
        var recordedBelow = new long[folders.Count];

        for (var i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];

            if (i > 0)
            {
                recordedBelow[folder.Parent] += folder.Bytes;
            }

            // A folder taken out from the page since the scan is gone from the disk too.
            if (placed[i] >= 0 && today.Holds(placed[i]))
            {
                drawnAs[i] = placed[i];
                before[placed[i]] = folder.Bytes;
                continue;
            }

            drawnAs[i] = today.NodeCount + extras.Count;
            extras.Add((drawnAs[folder.Parent], folder.Name, folder.Bytes));
        }

        for (var i = 0; i < folders.Count; i++)
        {
            if (folders[i].Bytes - recordedBelow[i] is > 0 and var unrecorded)
            {
                extras.Add((drawnAs[i], null, unrecorded));
            }
        }

        return new GrowthReplay(growth, today, before, extras);
    }

    /// <summary>
    /// Whether the earlier scan recorded <paramref name="node"/> of the tree now. A folder it did not
    /// record has no earlier picture of its own: it was inside its parent's block, or not there.
    /// </summary>
    public bool Recorded(int node) => _before.ContainsKey(node);

    /// <summary>
    /// The tree at <paramref name="step"/>, from 0, the earlier scan, to <see cref="Steps"/>, which is
    /// the tree now with the earlier scan's shapes shrunk to nothing.
    /// </summary>
    public GrowthReplayFrame At(int step)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(step);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(step, Steps);

        return _frames[step] ??= new GrowthReplayFrame(this, step);
    }

    /// <summary>
    /// The volume to draw beside a step, where <paramref name="now"/> is drawn beside the tree now:
    /// its capacity and free space part way from the earlier scan's to now's, as the folders are.
    ///
    /// <para>What Windows states about the space no folder accounts for is left out before now. It
    /// was read with this scan and not recorded with the earlier one, so drawn beside the earlier
    /// scan it would state a size nothing measured then. That space is drawn in the block of what is
    /// not accounted for instead.</para>
    /// </summary>
    public VolumeSpace VolumeAt(int step, VolumeSpace now)
    {
        if (now == VolumeSpace.None || step == Steps)
        {
            return now;
        }

        var earlier = Growth.Earlier;

        return new VolumeSpace(
            Between(earlier.TotalBytes, now.TotalBytes, step),
            Between(earlier.FreeBytes, now.FreeBytes, step),
            CountedSystemVolumeInformation: now.CountedSystemVolumeInformation);
    }

    /// <summary>
    /// How much <paramref name="node"/> grew by, for its colour, which stays the same at every step:
    /// the change it ends with, as the map coloured by growth paints the tree now
    /// (<see cref="ScanGrowth.BytesAt"/>). A folder only the earlier scan had shrank by all of it, and
    /// null for the block of what was not recorded, which nothing compares.
    /// </summary>
    internal long? ChangeOf(int node) =>
        node < TodayCount ? Growth.BytesAt(node)
        : _extraNames[node - TodayCount] is null ? null
        : -_extraBytes[node - TodayCount];

    /// <summary>
    /// The step a play of the replay moves to after <paramref name="step"/>: the next one where the
    /// shapes travel, and straight to now where the reader has animation effects off, which shows the
    /// two ends and nothing estimated between them (#273).
    /// </summary>
    public static int Next(int step, Motion shift) => shift.Travels ? Math.Min(step + 1, Steps) : Steps;

    /// <summary>
    /// How long a play of the replay stays on a step: the move to it and a moment for the drawing to
    /// land where the shapes travel, and long enough to take in the earlier scan where they do not.
    /// </summary>
    public static TimeSpan Pace(Motion shift) =>
        shift.Travels ? shift.Duration + TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(1.5);

    /// <summary>What the earlier scan recorded at or below <paramref name="node"/>, or zero where it recorded nothing of it.</summary>
    internal long Before(int node) =>
        node < TodayCount ? _before.GetValueOrDefault(node) : _extraBytes[node - TodayCount];

    /// <summary>What <paramref name="node"/> holds now: nothing for a shape only the earlier scan had.</summary>
    internal long After(int node) => node < TodayCount ? Today.SizeOf(node) : 0;

    /// <summary>The shapes only the earlier scan had directly under <paramref name="node"/>.</summary>
    internal ReadOnlySpan<int> ExtrasUnder(int node) =>
        _extrasUnder.TryGetValue(node, out var extras) ? extras : [];

    internal int ParentOf(int node) => node < TodayCount ? Today.ParentOf(node) : _extraParents[node - TodayCount];

    internal bool IsContainer(int node) =>
        node < TodayCount ? ((Layout.ISizedTree)Today).IsContainer(node) : _extraNames[node - TodayCount] is not null;

    /// <summary>A node's name, or null for a block of what was not recorded.</summary>
    internal string? NameOf(int node) => node < TodayCount ? Today.NameOf(node) : _extraNames[node - TodayCount];

    /// <summary><paramref name="before"/> moved <paramref name="step"/> steps of <see cref="Steps"/> towards <paramref name="after"/>.</summary>
    internal static long Between(long before, long after, int step) =>
        before + (long)Math.Round((double)(after - before) * step / Steps);
}
