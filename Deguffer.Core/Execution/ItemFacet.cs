namespace Deguffer.Core.Execution;

/// <summary>
/// One thing a provider can say about an item beside its path, for a reader choosing between items:
/// the project a build folder belongs to, the version a superseded build was. The shell shows each
/// label as a column.
///
/// <para><b>A label and a value rather than a property per fact.</b> The shell holds no knowledge of
/// what any cache is (G2), and a typed property each for a project, a version and a browser would
/// teach it three providers' vocabularies. A pair is enough to lay out and to search, and that is all
/// the shell does with it.</para>
///
/// <para><b>Shown and searched, never matched on.</b> A choice about an item is remembered by
/// <see cref="CleanupStep.SelectionKey"/>, and a protection by <see cref="ItemIdentity"/>. A facet is
/// prose for the reader, so rewording one must change nothing about what is ticked or kept.</para>
/// </summary>
/// <param name="Label">
/// What the column is headed, such as "Project". Columns are matched on this text, so a provider uses
/// the same label for every item it offers.
/// </param>
/// <param name="Value">What this item has in that column, such as the project's folder name.</param>
public sealed record ItemFacet(string Label, string Value);
