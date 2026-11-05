namespace Deguffer.Core.Safety;

/// <summary>
/// A running program as the in-use check compares it: its executable and its working directory, each
/// followed to every path it is reachable at once per reading of the process table, rather than again
/// for every directory asked about.
/// </summary>
/// <param name="Name">The process name, for telling the user what to close.</param>
/// <param name="Image">The executable, or null where its path could not be read.</param>
/// <param name="Working">The working directory, or null where it could not be read.</param>
/// <param name="Started">When it started, or null where Windows would not say.</param>
internal sealed record ReachedProgram(string Name, ReachedFolder? Image, ReachedFolder? Working, DateTimeOffset? Started);
