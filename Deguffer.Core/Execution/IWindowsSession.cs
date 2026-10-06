namespace Deguffer.Core.Execution;

/// <summary>
/// The Windows session and the machine under it, asked to do what a <see cref="CompletionAction"/>
/// names.
///
/// <para>A seam because the real one locks, logs off, sleeps or turns off the machine running the
/// tests. Every rule about whether and when to call it is in <see cref="WhenCleanComplete"/> and
/// <see cref="CompletionCountdown"/>, which are tested; what is left behind this interface is the
/// call itself.</para>
/// </summary>
public interface IWindowsSession
{
    /// <summary>Whether Windows lets a program put this machine to sleep.</summary>
    bool CanSleep { get; }

    /// <summary>Whether hibernation is switched on and a program may ask for it.</summary>
    bool CanHibernate { get; }

    /// <summary>
    /// Ask Windows to carry out <paramref name="action"/>. Null where Windows accepted, or Windows'
    /// own reason where it refused.
    ///
    /// <para>Closing Deguffer is the window's to do and not the session's, so
    /// <see cref="CompletionAction.ExitDeguffer"/> and <see cref="CompletionAction.Nothing"/> are
    /// refused with an exception: reaching here with either is a fault in the caller.</para>
    /// </summary>
    string? Perform(CompletionAction action);
}
