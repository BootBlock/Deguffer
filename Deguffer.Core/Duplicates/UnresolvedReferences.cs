namespace Deguffer.Core.Duplicates;

/// <summary>
/// The places named by reference locations that could not be resolved, which a search passes over
/// with everything in them (§7.4: a reference copy is never marked and never removed).
///
/// <para><b>Passed over, because the role is what went unresolved.</b> A file takes the role of the
/// innermost location holding it, and a location that could not be resolved holds nothing, so a
/// reference folder inside a searched drive would hand its files the drive's role, and a reference
/// copy would be offered for removal. Searching the place with the safer role instead is not
/// possible either: what Windows would not open, or would not name, cannot be told to be the folder
/// the walk reached. An unresolved location that was to be searched changes nothing, because the
/// location holding it gives its files the role they would have had.</para>
///
/// <para><b>Matched by the path as given, ignoring case.</b> The place is the location's path once a
/// substituted drive is followed, in display form, and a path the walk reaches matches it whatever
/// the case of either, which can only pass over more. A link on the way to the place can still hide
/// it from a match of text, which is why no rule marks a copy while a reference location went
/// unsearched (<c>docs/todo/duplicates.md</c>, phase 4).</para>
/// </summary>
internal sealed class UnresolvedReferences
{
    private const string Reason =
        "This was chosen as a reference, and Deguffer could not search it as one, so nothing in it is "
        + "searched, lest a reference copy be offered for removal.";

    private readonly HashSet<string> _places;

    /// <param name="places">Each place in display form, without a trailing separator unless it is a drive's top.</param>
    public UnresolvedReferences(IEnumerable<string> places) =>
        _places = new HashSet<string>(places, StringComparer.OrdinalIgnoreCase);

    /// <summary>Why <paramref name="path"/> is passed over with everything in it, or null where no unresolved reference names it.</summary>
    /// <param name="path">A path the search reached, in display form.</param>
    public string? WhyPassedOver(string path) => _places.Contains(path) ? Reason : null;
}
