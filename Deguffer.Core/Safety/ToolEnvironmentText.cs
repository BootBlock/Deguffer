using System.Text;

namespace Deguffer.Core.Safety;

/// <summary>
/// A set of variables in the form <c>CreateProcess</c> reads as a new process's environment with
/// <c>CREATE_UNICODE_ENVIRONMENT</c>: each <c>name=value</c> ended by a null, sorted by name without
/// regard to case, and the whole ended by a second null.
///
/// <para><b>Refused rather than repaired.</b> A null inside a value would end that variable early and
/// start another, so a value holding <c>\0ARDUINO_DIRECTORIES_DATA=...</c> would hand the tool a
/// setting nobody set. An <c>=</c> in a name after its first character splits it the same way. Either
/// is refused whole: dropping or trimming the variable would start the tool with an environment that
/// is not the one asked for.</para>
/// </summary>
internal static class ToolEnvironmentText
{
    /// <summary>The block, or null where a variable cannot be written into one.</summary>
    /// <param name="refused">The name of the first variable that cannot be written, or null.</param>
    public static string? Encode(IReadOnlyDictionary<string, string> variables, out string? refused)
    {
        var builder = new StringBuilder();

        foreach (var (name, value) in variables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0 || name.Contains('\0') || name.IndexOf('=', 1) >= 0 || value.Contains('\0'))
            {
                refused = name;
                return null;
            }

            builder.Append(name).Append('=').Append(value).Append('\0');
        }

        // An empty environment is still ended by two nulls, which the loop above did not write.
        if (builder.Length == 0)
        {
            builder.Append('\0');
        }

        refused = null;
        return builder.Append('\0').ToString();
    }
}
