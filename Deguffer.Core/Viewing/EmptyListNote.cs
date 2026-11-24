namespace Deguffer.Core.Viewing;

/// <summary>
/// What a list says in place of its rows when it has none: a blank list reads as a screen that has
/// failed, and a list emptied by the reader's own choices must say so, or a filter left typed in
/// reads as nothing there.
/// </summary>
/// <param name="Remedy">
/// The words of the button that undoes <paramref name="Cause"/>, such as clearing the filter, or null
/// where nothing the reader chose keeps the rows back. One press does what the sentence would
/// otherwise have to tell the reader to go and find.
/// </param>
public sealed record EmptyListNote(EmptyListCause Cause, string Title, string Message, string? Remedy);
