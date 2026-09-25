namespace Deguffer.Core.Providers;

/// <summary>
/// A directory whose immediate entries are matched against <see cref="Markers"/>.
///
/// <para>Two kinds, and the difference is who owns the directory. A temporary folder belongs to
/// nobody, so its unrecognised entries are simply not this row's: asserting eighteen thousand of
/// them survived would prove nothing a narrower rule does not. A tool's own folder inside it — Roslyn's
/// shadow-copy folder, ServiceHub's — is that tool's, so §5.2 applies in full: the folder itself
/// stays, and each entry the markers do not recognise is asserted to survive (§5.6).</para>
/// </summary>
/// <param name="Directory">The directory whose entries are examined.</param>
/// <param name="Markers">What may be recognised in it.</param>
/// <param name="Owner">
/// The tool that owns <paramref name="Directory"/>, or null for a temporary folder. Where it is set,
/// the directory and every entry no marker recognises are survivors.
/// </param>
/// <param name="Below">
/// The folder <paramref name="Directory"/> was reached from by name, or null where it was reached
/// directly. Every directory between the two must be a real one: a junction called <c>Roslyn</c>
/// would otherwise hand back somewhere else's entries under Roslyn's names.
/// </param>
public sealed record TempMarkerPlace(
    string Directory,
    IReadOnlyList<TempMarker> Markers,
    string? Owner = null,
    string? Below = null);
