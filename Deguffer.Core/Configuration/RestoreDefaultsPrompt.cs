using Deguffer.Core.Text;

namespace Deguffer.Core.Configuration;

/// <summary>
/// What the reader is asked before every setting on the Settings page goes back to its default.
///
/// <para>A Core type for the reason the other prompts are: several of these settings decide what gets
/// deleted, so the sentence saying which of them change, and what does not, is held to a test rather
/// than living inside a dialog. It names each setting that will change, with the value it holds and the
/// value it goes back to, and says that the lists the reader built are kept.</para>
/// </summary>
/// <param name="Title">The question, with how many settings change.</param>
/// <param name="Consequence">What changes, and what is kept.</param>
/// <param name="Items">One line for each setting that changes.</param>
/// <param name="ConfirmLabel">The affirmative button.</param>
public sealed record RestoreDefaultsPrompt(string Title, string Consequence, IReadOnlyList<string> Items, string ConfirmLabel)
{
    /// <summary>The question for <paramref name="preferences"/>, or null where every setting is already on its default.</summary>
    public static RestoreDefaultsPrompt? For(AppPreferences preferences)
    {
        var changed = ShippedSettings.Changed(preferences);

        if (changed.Count == 0)
        {
            return null;
        }

        return new RestoreDefaultsPrompt(
            $"Restore {Counted.Of(changed.Count, "setting", "settings")} to {Counted.Form(changed.Count, "its default", "their defaults")}?",

            Counted.Form(changed.Count, "This setting goes", "These settings go")
            + " back to the value Deguffer ships with. Your keep list, source folders, emulator folders and "
            + "scan history are not changed.",

            [.. changed.Select(setting => $"{setting.Name}: from {setting.Says(preferences)} to {setting.Shipped}")],
            "Restore defaults");
    }
}
