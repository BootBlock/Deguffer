using System.Text.RegularExpressions;

namespace Deguffer.Core.Providers;

/// <summary>
/// The name every Nintendo Switch emulator gives a game's own folder: its title id, as sixteen
/// hexadecimal digits such as <c>0100f2c0115b6000</c>.
/// </summary>
internal static partial class SwitchTitleId
{
    public static bool IsMatch(ReadOnlySpan<char> name) => Pattern().IsMatch(name);

    [GeneratedRegex("^[0-9A-Fa-f]{16}$")]
    private static partial Regex Pattern();
}
