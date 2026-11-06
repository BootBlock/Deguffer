namespace Deguffer.Core.Safety;

/// <summary>Who holds <paramref name="files"/> open, asked in one session: <see cref="RestartManager.Query"/>.</summary>
internal delegate FileHolders FileHolderQuery(IReadOnlyList<string> files, CancellationToken ct);

/// <summary>
/// Who holds each candidate's lock files open, asked in one Restart Manager session where the
/// answer allows it.
///
/// <para><b>Each session has a cost of its own.</b> Forty candidates of eight files each took
/// 324-441 ms in a session apiece and 80-117 ms in one session of all 320, so every candidate's
/// files are asked about together first. A set of files no process holds has no subset a process
/// holds, so where that one session names nobody, it is every candidate's answer.</para>
///
/// <para><b>A shared answer that names anyone is never shared.</b> <c>RmGetList</c> says which
/// processes hold some file in the session, not which file, so a holder read from a shared session
/// could name the wrong project in the plan, the failure <see cref="LiveTreeVeto"/> warns about.
/// Each candidate is then asked in a session of its own, so its answer is the one it had before
/// files were shared. A shared session that cannot be asked at all (one path past <c>MAX_PATH</c>
/// refuses the whole registration) falls back the same way, so one unusual path does not make every
/// candidate "could not tell".</para>
///
/// <para><b>Not halved to find the holder.</b> A session with a held file in it costs about
/// 100-250 ms where one without costs under 10, so the cost is the number of sessions a held file
/// is in. Halving puts the held file in a session at every level: one held among forty measured
/// 631-702 ms halved. Asking one at a time after the shared session puts it in two, and measured
/// 532-636 ms against 348-430 ms with no shared session. That one extra session is what a held lock
/// file costs, for the five-fold saving when none is.</para>
///
/// <para>Not split into batches by size: one session registered 20,000 files and answered.</para>
/// </summary>
internal static class LockFileHolders
{
    /// <summary>The holders of each of <paramref name="lockSets"/>, in the same order.</summary>
    /// <param name="lockSets">Each candidate's lock files, none of them empty.</param>
    public static FileHolders[] Ask(IReadOnlyList<IReadOnlyList<string>> lockSets, FileHolderQuery query, CancellationToken ct)
    {
        var answers = new FileHolders[lockSets.Count];

        // One candidate's shared session would be its own session, asked twice where it is held.
        if (lockSets.Count > 1)
        {
            var shared = query([.. lockSets.SelectMany(files => files)], ct);

            if (shared is { Answered: true, Holders.Count: 0 })
            {
                Array.Fill(answers, shared);
                return answers;
            }
        }

        for (var i = 0; i < lockSets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            answers[i] = query(lockSets[i], ct);
        }

        return answers;
    }
}
