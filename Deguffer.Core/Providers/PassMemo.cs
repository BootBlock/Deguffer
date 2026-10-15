namespace Deguffer.Core.Providers;

/// <summary>
/// A value worked out at most once until it is forgotten, however many threads ask for it at once:
/// the first asks, and the rest wait for its answer.
///
/// <para><b>Why not <c>_value ??= Work()</c>.</b> A planning pass plans several providers at once,
/// and a provider's cache is read from threads other than its own: the temporary-folder row asks its
/// tenants what they claim, and <see cref="RowDeclarations"/> reads every row's declaration from the
/// thread of whichever row asked first. Two threads that both found the field empty would both do the
/// work, a walk of the disk or a run of a tool, and could each act on a different answer within the
/// one pass, so two rows would disagree about what the disk held. A field of a struct type is worse:
/// its write is not one step, so a reader could see half of each answer.</para>
///
/// <para><b>The work runs under the lock.</b> A caller that arrives while it runs waits for it rather
/// than repeating it, which is the point. The work must not wait for a thread that is waiting here,
/// which is <see cref="RowDeclarations"/>' rule that no declaration asks it anything. A
/// <see cref="Task"/> is remembered like any value: work that returns one holds the lock only until it
/// first waits, and every caller awaits the one task.</para>
///
/// <para><b>What is not remembered.</b> A null answer, so it is worked out again on the next ask, as
/// the field it replaces did. And a task that failed or was stopped, which is no answer about the
/// machine: a tool that timed out once is asked again rather than reported as broken for the rest of
/// the pass.</para>
/// </summary>
/// <typeparam name="T">The answer.</typeparam>
internal sealed class PassMemo<T>
{
    private readonly Lock _gate = new();

    // Boxed, so a struct answer is published by one reference write rather than field by field.
    private Box? _remembered;

    /// <summary>The remembered answer, or <paramref name="work"/>'s, which is then remembered.</summary>
    public T Get(Func<T> work)
    {
        if (Volatile.Read(ref _remembered) is { } remembered && Holds(remembered.Value))
        {
            return remembered.Value;
        }

        lock (_gate)
        {
            if (_remembered is { } raced && Holds(raced.Value))
            {
                return raced.Value;
            }

            var answer = work();
            Volatile.Write(ref _remembered, answer is null ? null : new Box(answer));
            return answer;
        }
    }

    /// <summary>Drop the answer, so the next ask works it out again.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _remembered = null;
        }
    }

    private sealed record Box(T Value);

    private static bool Holds(T answer) => answer is not Task { IsFaulted: true } and not Task { IsCanceled: true };
}
