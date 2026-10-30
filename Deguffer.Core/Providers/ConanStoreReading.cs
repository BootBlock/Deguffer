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
    /// Every entry of <c>t</c> and every folder of <c>b</c>, and how each was judged, which the clean
    /// holds the store to when it runs (<see cref="ConanStoreContents.ReadTempAndBuilds"/>).
    /// </summary>
    public IReadOnlySet<ConanStoreEntry> TempAndBuilds { get; init; } = new HashSet<ConanStoreEntry>();
}

/// <summary>An entry of <c>t</c> or <c>b</c> in Conan's store, and how it was judged.</summary>
internal readonly record struct ConanStoreEntry(string Path, ConanStoreEntryKind Kind);

/// <summary>How an entry of <c>t</c> or <c>b</c> was judged, which decides what <c>-t</c> does with it.</summary>
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
    private readonly HashSet<ConanStoreEntry> _tempAndBuilds = [];
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

    /// <summary>Something the clean would remove that is not shown to be Conan's: protected, and it holds the clean back.</summary>
    public void NotConans(string path)
    {
        _unrecognised.Add(path);
        _kept.Add((path, NotConansReason));
    }

    /// <summary>The same, in <c>t</c> or <c>b</c>, where it is part of what the clean holds the store to.</summary>
    public void NotConans(string path, ConanStoreEntryKind kind)
    {
        NotConans(path);
        Saw(path, kind);
    }

    /// <summary>An entry of <c>t</c>, or a folder of <c>b</c>, and how it was judged.</summary>
    public void Saw(string path, ConanStoreEntryKind kind) => _tempAndBuilds.Add(new ConanStoreEntry(path, kind));

    public void Unreadable(string path)
    {
        _notes.Add(UnreadableRoot.Note(path));
        _unreadable = true;
    }

    public ConanStoreReading Finish() => new(_measured, _kept, _unrecognised, _notes, _unreadable)
    {
        TempAndBuilds = _tempAndBuilds,
    };
}
