using Deguffer.Core.Execution;

namespace Deguffer.Core.Providers;

/// <summary>What <see cref="ConanStoreContents.Read"/> found.</summary>
/// <param name="Measured">Every folder the clean removes, for the step to measure and report against.</param>
/// <param name="Kept">Every folder beside them that must outlive the clean, with why.</param>
/// <param name="Unrecognised">
/// Everything the clean would remove that is not shown to be Conan's, each in <paramref name="Kept"/>
/// as well. One is enough for the clean not to be offered.
/// </param>
/// <param name="Notes">What the user is told about what could not be read or measured.</param>
/// <param name="Unreadable">Whether a folder would not be listed, so the figure leaves something out.</param>
internal sealed record ConanStoreReading(
    IReadOnlyList<string> Measured,
    IReadOnlyList<(string Path, string Reason)> Kept,
    IReadOnlyList<string> Unrecognised,
    IReadOnlyList<PlanNote> Notes,
    bool Unreadable)
{
    /// <summary>
    /// Every entry of <c>t</c>, every folder of <c>b</c>, and every recipe or package folder that is a
    /// link, and how each was judged: where <c>-t</c> removes by shape, and where <c>-s</c> and
    /// <c>-b</c> would reach through a link, rather than by what the database lists. The clean holds
    /// the store to it when it runs, read again by <see cref="ConanStoreContents.Read"/>.
    /// </summary>
    public IReadOnlySet<ConanStoreEntry> HeldTo { get; init; } = new HashSet<ConanStoreEntry>();
}

/// <summary>An entry of Conan's store that the clean is held to, and how it was judged.</summary>
internal readonly record struct ConanStoreEntry(string Path, ConanStoreEntryKind Kind);

/// <summary>How an entry the clean is held to was judged, which decides what the clean does with it.</summary>
internal enum ConanStoreEntryKind
{
    /// <summary>An export Conan started in <c>t</c>, removed with it.</summary>
    Export,

    /// <summary>A folder of <c>b</c> with a finished package, which <c>-t</c> leaves.</summary>
    FinishedBuild,

    /// <summary>A folder of <c>b</c> without one, which <c>-t</c> removes whole.</summary>
    UnfinishedBuild,

    /// <summary>Something the clean would remove that is not shown to be Conan's.</summary>
    NotConans,

    /// <summary>A folder Windows would not list.</summary>
    Unreadable,
}

/// <summary>
/// What <see cref="ConanStoreContents"/> finds as it goes, gathered into a <see cref="ConanStoreReading"/>.
/// </summary>
internal sealed class ConanStoreTally
{
    private const string NotConansReason =
        "Not something Conan made, though its clean would remove it, so the clean is not offered while it is here.";

    private const string LinkNotMeasured =
        "Conan's clean removes a folder here that is a link, so Deguffer did not measure through it.";

    private readonly List<string> _measured = [];
    private readonly List<(string Path, string Reason)> _kept = [];
    private readonly List<string> _unrecognised = [];
    private readonly List<PlanNote> _notes = [];
    private readonly HashSet<ConanStoreEntry> _heldTo = [];
    private bool _unreadable;

    public void Removed(string path, bool isLink)
    {
        if (isLink)
        {
            _notes.Add(new PlanNote(PlanNoteSeverity.Information, $"{LinkNotMeasured} ({path})"));
        }
        else
        {
            _measured.Add(path);
        }
    }

    public void Kept(string path, string reason) => _kept.Add((path, reason));

    /// <summary>
    /// Something the clean would remove, or reach through, that is not shown to be Conan's: protected,
    /// it holds the clean back, and it is part of what the clean holds the store to.
    /// </summary>
    public void NotConans(string path, ConanStoreEntryKind kind)
    {
        _unrecognised.Add(path);
        _kept.Add((path, NotConansReason));
        Saw(path, kind);
    }

    /// <summary>An entry the clean holds the store to, and how it was judged.</summary>
    public void Saw(string path, ConanStoreEntryKind kind) => _heldTo.Add(new ConanStoreEntry(path, kind));

    public void Unreadable(string path)
    {
        _notes.Add(UnreadableRoot.Note(path));
        _unreadable = true;
    }

    public ConanStoreReading Finish() => new(_measured, _kept, _unrecognised, _notes, _unreadable)
    {
        HeldTo = _heldTo,
    };
}
