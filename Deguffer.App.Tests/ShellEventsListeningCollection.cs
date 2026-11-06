namespace Deguffer.App.Tests;

/// <summary>
/// Whether a trace is recording is one flag for the whole process, and a recorder in a test running
/// beside these would raise it. These tests ask what happens while it is down, so they run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ShellEventsListeningCollection
{
    public const string Name = "Whether a trace of the shell is recording";
}
