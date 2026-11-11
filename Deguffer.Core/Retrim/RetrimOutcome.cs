using Deguffer.Core.Wmi;

namespace Deguffer.Core.Retrim;

/// <summary>What became of a retrim asked for on one volume.</summary>
public enum RetrimOutcome
{
    /// <summary>Windows answered that it retrimmed the volume.</summary>
    Retrimmed,

    /// <summary>Windows answered that the volume does not support a retrim.</summary>
    NotSupported,

    /// <summary>Windows refused for want of administrator rights.</summary>
    NeedsElevation,

    /// <summary>
    /// Windows answered with another of its documented failures, or one it does not document. The
    /// code is its return value.
    /// </summary>
    Failed,

    /// <summary>
    /// Windows' storage management gave no answer at all: it would not connect, list its volumes or
    /// run the method. The code is the error it gave.
    /// </summary>
    Unanswered,

    /// <summary>Windows' storage management does not list the volume, so nothing was asked of it.</summary>
    NotListed,

    /// <summary>
    /// The volume is no longer one a retrim is offered on, or is not mounted, so nothing was asked of
    /// it. A request from a restart as administrator names its volumes on the command line, and each is
    /// judged again before Windows is asked.
    /// </summary>
    NotOffered,
}

/// <summary>Windows' answer to one retrim, as an outcome and the code that decided it.</summary>
/// <param name="Code">
/// Windows' return value for <see cref="RetrimOutcome.Failed"/>, the error for
/// <see cref="RetrimOutcome.Unanswered"/>, the error or the return value that refused
/// <see cref="RetrimOutcome.NeedsElevation"/>, and 0 otherwise.
/// </param>
public readonly record struct RetrimAnswer(RetrimOutcome Outcome, int Code = 0)
{
    /// <summary>Windows' storage management's "Access denied", as a method's return value.</summary>
    private const int StorageAccessDenied = 40001;

    /// <summary>
    /// What <c>MSFT_Volume.Optimize</c> answered, by the return values Microsoft documents for it: 0 is
    /// success and 1 is "Not Supported". Every other value is a failure, reported with its number.
    ///
    /// <para>40001 is not on the method's page. It is what an unelevated process was answered on
    /// Windows 11, and the message Windows' storage management gives for it is "Access denied".</para>
    /// </summary>
    public static RetrimAnswer FromReturnValue(int returnValue) => returnValue switch
    {
        0 => new RetrimAnswer(RetrimOutcome.Retrimmed),
        1 => new RetrimAnswer(RetrimOutcome.NotSupported),
        StorageAccessDenied => new RetrimAnswer(RetrimOutcome.NeedsElevation, returnValue),
        _ => new RetrimAnswer(RetrimOutcome.Failed, returnValue),
    };

    /// <summary>A call that failed before Windows gave a return value, by its error.</summary>
    public static RetrimAnswer FromError(int hr) =>
        new(WbemConnection.IsAccessDenied(hr) ? RetrimOutcome.NeedsElevation : RetrimOutcome.Unanswered, hr);
}
