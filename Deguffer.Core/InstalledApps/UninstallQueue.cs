namespace Deguffer.Core.InstalledApps;

/// <summary>An uninstaller about to start, and where it stands in the queue.</summary>
/// <param name="Number">Its place, counting from 1.</param>
public sealed record UninstallStep(PreparedUninstall Item, int Number, int Count);

/// <summary>What a queue of uninstalls came to.</summary>
/// <param name="Finished">Each uninstall that was tried, in order, with what the list says after it.</param>
/// <param name="Abandoned">The program whose uninstaller was still being watched when the user cancelled.</param>
/// <param name="NotRun">The programs whose uninstallers were never started.</param>
public sealed record UninstallQueueReport(
    IReadOnlyList<UninstallReport> Finished,
    InstalledEntry? Abandoned,
    IReadOnlyList<InstalledEntry> NotRun)
{
    /// <summary>Whether every uninstaller ran and was watched until it exited.</summary>
    public bool IsComplete => Abandoned is null && NotRun.Count == 0 && Finished.All(r => r.After is not AfterUninstall.NotRun);

    public string Summary => string.Join(Environment.NewLine, Lines());

    private IEnumerable<string> Lines()
    {
        foreach (var report in Finished)
        {
            yield return report.Summary;
        }

        if (Abandoned is { } abandoned)
        {
            yield return $"Deguffer stopped watching the uninstaller of '{abandoned.Name}'. It may still be running.";
        }

        if (NotRun is [var only])
        {
            yield return $"The uninstaller of '{only.Name}' was not started.";
        }
        else if (NotRun.Count > 1)
        {
            yield return $"The uninstallers of {NotRun.Count} programs were not started: {string.Join(", ", NotRun.Select(e => $"'{e.Name}'"))}.";
        }
    }
}

/// <summary>
/// Runs the uninstallers the user confirmed together, one at a time (§7.3), and asks before starting
/// each one after the first. Many uninstallers copy themselves elsewhere and exit at once, so an exit
/// does not mean the uninstaller has finished, and two that overlap can each fail. Only the user can
/// see that the last one has finished, so the user decides when the next one starts.
/// </summary>
public sealed class UninstallQueue(ProgramUninstaller uninstaller, Func<IInstalledAppsConfirmation> confirmation)
{
    /// <summary>
    /// Run <paramref name="confirmed"/> in order. Cancelling stops the wait for the uninstaller
    /// running, never the uninstaller, and starts no more.
    /// </summary>
    /// <remarks>
    /// Awaited on the caller's context, because the question between two uninstallers is asked on
    /// it. Each uninstall runs off that context, because it reads the registry before and after.
    /// </remarks>
    public async Task<UninstallQueueReport> RunAsync(
        IReadOnlyList<PreparedUninstall> confirmed,
        IProgress<UninstallStep> progress,
        CancellationToken ct)
    {
        var finished = new List<UninstallReport>();

        for (var i = 0; i < confirmed.Count; i++)
        {
            var item = confirmed[i];

            if (ct.IsCancellationRequested || (i > 0 && !await AskNextAsync(finished[^1], item, confirmed.Count - i - 1, ct)))
            {
                return new UninstallQueueReport(finished, null, [.. confirmed.Skip(i).Select(p => p.Entry)]);
            }

            progress.Report(new UninstallStep(item, i + 1, confirmed.Count));

            try
            {
                // Not handed the token: Task.Run would then throw for a cancel that came before the
                // uninstaller started, and report as abandoned an uninstaller that never ran.
                finished.Add(await Task.Run(() => uninstaller.UninstallAsync(item, ct)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new UninstallQueueReport(finished, item.Entry, [.. confirmed.Skip(i + 1).Select(p => p.Entry)]);
            }
        }

        return new UninstallQueueReport(finished, null, []);
    }

    private async Task<bool> AskNextAsync(UninstallReport previous, PreparedUninstall next, int after, CancellationToken ct)
    {
        try
        {
            return await confirmation().AskAsync(InstalledAppsPrompt.ForNextUninstall(previous, next, after), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled before the question opened: the same answer as Cancel inside it.
            return false;
        }
    }
}
