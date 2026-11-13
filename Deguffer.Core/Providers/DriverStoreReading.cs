using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What <see cref="DriverStoreProvider"/> reads of the machine in one planning pass: the driver store's
/// listing, and Windows' cleanup asked what it would clear, started with it so the handler's
/// measurement runs beside <c>pnputil</c>'s listing rather than after it.
///
/// <para><b>Asked before the plan knows it needs the answer.</b> Elevated on Windows 11 24H2, the
/// listing took 1.2 to 2.1 s and the survey 3.5 to 4.1 s, and the two together took as long as the
/// survey alone. Where the listing shows nothing superseded, the survey is stopped, which the handler
/// obeyed within 110 ms. Unelevated, <see cref="DiskCleanupHandlers.Survey"/> answers before it loads
/// the handler, so the early start costs a registry read.</para>
///
/// <para><b>Not asked while the pass's reading shows an update unfinished</b>, which holds every
/// package back in any case: Windows Update installs drivers, and nothing documents what the handler's
/// measurement does beside it. The plan asks the same reading before it reads the answer, and the
/// clean asks the machine itself before it runs.</para>
/// </summary>
/// <param name="handler">The Disk Cleanup handler, as Windows registers it.</param>
/// <param name="volume">The system volume, in the display form the handler answers for.</param>
/// <param name="mayAsk">
/// Whether the pass's servicing reading lets the handler be asked, read once when the listing is first
/// asked for in a pass.
/// </param>
internal sealed class DriverStoreReading(
    IDriverStore store,
    IDiskCleanupHandlers handlers,
    string handler,
    string volume,
    DiskCleanupQuestion question,
    Func<bool> mayAsk)
{
    /// <summary>
    /// Taken once per planning pass (G4): it is a subprocess, a read of every device and a SetupAPI call
    /// per package.
    /// </summary>
    private readonly PassMemo<Task<DriverStoreListing>> _listing = new();

    /// <summary>The survey started with the listing, until exactly one caller takes it.</summary>
    private EarlyDiskCleanupSurvey? _early;

    /// <summary>The pass's listing, starting the survey beside it the first time it is asked for.</summary>
    public Task<DriverStoreListing> ListingAsync(CancellationToken ct) => _listing.Get(() =>
    {
        var survey = mayAsk() ? Survey(ct) : null;

        // A listing that failed is asked again, and the survey started with it is replaced.
        Interlocked.Exchange(ref _early, survey)?.Abandon();

        return store.ListAsync(ct);
    });

    /// <summary>The survey started with the listing, handed to exactly one caller, or null.</summary>
    public EarlyDiskCleanupSurvey? TakeEarlySurvey() => Interlocked.Exchange(ref _early, null);

    /// <summary>Windows' cleanup asked afresh, for a plan whose listing started no survey.</summary>
    public EarlyDiskCleanupSurvey Survey(CancellationToken ct) => new(handlers, handler, volume, question, ct);

    /// <summary>
    /// Starts the listing, and the survey with it, and ends once both have: the listing however it
    /// ended, and the survey once the handler has returned, so whatever a pass started ends with it.
    /// </summary>
    /// <param name="ct">The pass's token, which stops the survey if no plan has taken it by the pass's end.</param>
    public async Task BeginAsync(CancellationToken ct)
    {
        var listing = ListingAsync(ct);
        var survey = Volatile.Read(ref _early);

        try
        {
            await listing.ConfigureAwait(false);
        }
        finally
        {
            if (survey is not null)
            {
                await survey.Returned.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Forgets the pass's listing, and stops a survey no plan took.</summary>
    public void Invalidate()
    {
        _listing.Forget();
        Interlocked.Exchange(ref _early, null)?.Abandon();
    }
}
