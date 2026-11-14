namespace Deguffer.Core.Viewing;

/// <summary>
/// What a list says in place of its rows when it has none: a blank list reads as a screen that has
/// failed, and a list emptied by the reader's own choices must say so, or a filter left typed in
/// reads as nothing there.
/// </summary>
public sealed record EmptyListNote(EmptyListCause Cause, string Title, string Message);
