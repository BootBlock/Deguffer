namespace Deguffer.Core.Configuration;

/// <summary>
/// The settings as they stand right now, for the Core types that read one.
///
/// <para><b>Read at the moment it is needed rather than taken once.</b> A provider is built when the
/// page is built and lives for the session, while a setting changes whenever the user opens
/// Settings — so a value captured in a constructor would go stale the first time somebody changed
/// their mind, and would go stale silently. Asking through this at plan time is what makes a change
/// take effect from the next preview.</para>
///
/// <para>An interface because <see cref="PreferenceStore"/> is a file, not a value: reading it here
/// would put a disk read inside a planning pass, and the live copy lives in the shell where Core
/// cannot see it. This is the same inversion <c>IUserEnvironment</c> makes, for the same reason —
/// it is what lets a provider's route be chosen in a test with no settings file anywhere.</para>
/// </summary>
public interface ICurrentPreferences
{
    AppPreferences Current { get; }
}

/// <summary>
/// The shipped defaults, for a caller with no settings of its own. The safe answer for anything
/// that reads a preference outside the app: every default is what the app ships with, so a Core
/// component built without the shell behaves as an untouched install would.
/// </summary>
public sealed class DefaultPreferences : ICurrentPreferences
{
    public static DefaultPreferences Instance { get; } = new();

    private DefaultPreferences()
    {
    }

    public AppPreferences Current => AppPreferences.Default;
}
