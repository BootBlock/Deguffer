namespace Deguffer.Core.Safety;

/// <summary>
/// Whether a named mutex exists in this logon session.
///
/// <para>A seam because it is how a tool can say "this folder is mine and I am still here" more
/// exactly than a process name can. Roslyn names a mutex after each analyzer shadow-copy session
/// folder and holds it for the life of the session, and it decides for itself which folders are
/// stale by asking whether that mutex still exists. Deguffer asks the same question, so it agrees
/// with Roslyn by construction rather than by guessing from what is running.</para>
/// </summary>
public interface INamedMutexes
{
    /// <summary>
    /// Whether a mutex called <paramref name="name"/> exists in this session's namespace. True where
    /// Windows would not say, because the answer decides whether something is deleted.
    /// </summary>
    bool Exists(string name);
}

/// <summary>The machine's own named mutexes.</summary>
public sealed class NamedMutexes : INamedMutexes
{
    public static readonly NamedMutexes Default = new();

    private NamedMutexes()
    {
    }

    public bool Exists(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            if (Mutex.TryOpenExisting(name, out var mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // It exists, and belongs to somebody this process may not open. Present, not absent.
            return true;
        }
        catch (IOException)
        {
            // A name Windows rejects is no answer at all, and "could not tell" must not read as "gone".
            return true;
        }
    }
}
