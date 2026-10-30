using System.Windows.Input;

namespace Deguffer.App.Shell;

/// <summary>Running a view-model's command from the shell, where a key, a click or a page's arrival asks for it.</summary>
internal static class Commands
{
    /// <summary>
    /// Run <paramref name="command"/> if it will run, and say whether it did.
    ///
    /// <para>Reporting that back is what lets a key go on to the control underneath when the command
    /// does not apply: swallowing Enter while nothing is selected would leave a list unable to
    /// activate a row, and swallowing Delete would make the key look broken rather than
    /// inapplicable.</para>
    /// </summary>
    public static bool TryExecute(this ICommand command, object? parameter = null)
    {
        if (!command.CanExecute(parameter))
        {
            return false;
        }

        command.Execute(parameter);
        return true;
    }
}
