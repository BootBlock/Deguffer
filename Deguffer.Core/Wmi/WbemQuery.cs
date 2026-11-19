using System.Diagnostics;
using System.Runtime.InteropServices;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Wmi;

/// <summary>
/// A WQL query run semi-synchronously and read one instance at a time, with the cancellation token
/// looked at between waits and the whole wait held to <see cref="ToolRun.QueryLimit"/>, so a provider
/// that never answers neither holds a cancelled scan nor holds a pass for ever.
/// </summary>
internal static class WbemQuery
{
    /// <summary>
    /// How long, in milliseconds, each wait for the next instance lasts before the token is looked at
    /// again, so a cancelled scan stops waiting within this rather than when WMI gives up.
    /// </summary>
    private const int CancellationPoll = 100;

    /// <summary>
    /// What <paramref name="read"/> makes of each instance <paramref name="query"/> gives, in WMI's order,
    /// leaving out each it returns null for. Null where the query failed, with WMI's code in
    /// <paramref name="hr"/>, or <see cref="WbemConnection.Unfinished"/> where it ran past the limit.
    /// Each instance is released once read.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before the query finished.</exception>
    public static List<T>? Rows<T>(
        IWbemServices services, string query, Func<IWbemClassObject, T?> read, out int hr, CancellationToken ct)
        where T : class
    {
        hr = services.ExecQuery("WQL", query, WbemConnection.SemiSynchronous, null, out var instances);

        if (hr != WbemConnection.S_OK)
        {
            return null;
        }

        try
        {
            var waited = Stopwatch.StartNew();
            List<T> rows = [];

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                hr = instances.Next(CancellationPoll, 1, out var instance, out var returned);

                if (hr < 0)
                {
                    return null;
                }

                if (returned == 0)
                {
                    if (hr != WbemConnection.TimedOut)
                    {
                        hr = WbemConnection.S_OK;
                        return rows;
                    }

                    if (waited.Elapsed >= ToolRun.QueryLimit)
                    {
                        hr = WbemConnection.Unfinished;
                        return null;
                    }

                    continue;
                }

                try
                {
                    if (read(instance) is { } row)
                    {
                        rows.Add(row);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(instance);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(instances);
        }
    }

    /// <summary>
    /// The property <paramref name="name"/> of <paramref name="instance"/>, or null where WMI would not
    /// give it or left it unset, which arrives as a null VARIANT.
    /// </summary>
    public static object? Property(IWbemClassObject instance, string name) =>
        instance.Get(name, 0, out var value, out _, out _) == WbemConnection.S_OK && value is not DBNull ? value : null;

    /// <summary>
    /// A numeric property as an integer. WMI hands a <c>uint16</c> or a <c>uint32</c> over as a signed
    /// four-byte VARIANT, and a value that is not a number is not an answer.
    /// </summary>
    public static int? Integer(object? value) => value switch
    {
        int signed => signed,
        uint unsigned => unchecked((int)unsigned),
        ushort small => small,
        short small => small,
        byte tiny => tiny,
        _ => null,
    };
}
