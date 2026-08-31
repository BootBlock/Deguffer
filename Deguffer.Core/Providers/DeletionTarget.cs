namespace Deguffer.Core.Providers;

/// <summary>
/// One directory a provider has decided to delete, before anything has been measured.
///
/// Exists so that measuring a set of targets and turning them into steps stays one piece of code
/// rather than one per provider. The correlation between a target and its size is positional, and
/// four copies of the same indexed loop is four chances to pair a path with the wrong number.
/// </summary>
/// <param name="Path">The directory, in display form — a plan never holds an extended-length path.</param>
/// <param name="Reason">Why it is disposable, written for the user.</param>
/// <param name="LastWritten">
/// §7's age, where the provider can tell. Null for a whole-cache target: a single timestamp
/// spanning everything a tool ever cached is a number with nothing to mean.
/// </param>
public readonly record struct DeletionTarget(string Path, string Reason, DateTime? LastWritten = null);
