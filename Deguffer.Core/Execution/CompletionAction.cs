namespace Deguffer.Core.Execution;

/// <summary>
/// What Deguffer does once a clean on the Storage page has finished, so a long clean can be left to
/// run and the machine left in the state the user wanted.
///
/// <para>The values are ordinal, from the least disruptive to the most, and the Storage page lists
/// the ones this machine offers in this order. They are stored by name, as every other preference
/// is, so the order is a presentation decision and not a compatibility one. See
/// <see cref="WhenCleanComplete"/> for which are offered and when one is carried out.</para>
/// </summary>
public enum CompletionAction
{
    /// <summary>Leave everything as it is. The shipped choice.</summary>
    Nothing = 0,

    /// <summary>Close Deguffer, as the window's own close button does.</summary>
    ExitDeguffer = 1,

    /// <summary>Lock the Windows session.</summary>
    Lock = 2,

    /// <summary>End the Windows session. Windows asks every program to close first.</summary>
    LogOff = 3,

    /// <summary>Put the machine to sleep. Offered only where the machine can.</summary>
    Sleep = 4,

    /// <summary>Hibernate the machine. Offered only where hibernation is switched on.</summary>
    Hibernate = 5,

    /// <summary>Restart the machine. Windows asks every program to close first.</summary>
    Restart = 6,

    /// <summary>Shut the machine down. Windows asks every program to close first.</summary>
    ShutDown = 7,
}
