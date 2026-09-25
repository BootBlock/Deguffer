using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <summary>
/// The full paths a command line names as arguments, for asking whether a running program was
/// started to use a directory.
///
/// <para><b>Split by Windows' own rule rather than a copy of it.</b> Quoting and backslashes follow
/// rules a program's runtime applies when it reads its arguments, and <c>CommandLineToArgvW</c> is
/// the reference for them. A hand-written splitter that disagreed about one quote would hand back a
/// fragment of a path, which matches nothing, which reads as "nothing is using this".</para>
///
/// <para><b>An argument names a path in one of two shapes</b>, and both were observed on a real
/// test run: Chromium is given <c>--user-data-dir=C:\...</c>, and Firefox is given <c>-profile</c>
/// followed by the path as the next argument. So an argument counts as it stands, or as whatever
/// follows the first <c>=</c> of a switch. The program's own path, which is the first argument, is
/// left out: the image path already answers where a program runs from.</para>
///
/// <para>Only a fully qualified path counts. A relative one resolves against a working directory
/// this cannot see from here, and guessing it would compare a path nobody named.</para>
/// </summary>
internal static partial class CommandLinePaths
{
    public static IReadOnlyList<string> Of(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            // CommandLineToArgvW answers an empty string with the path of this process instead.
            return [];
        }

        var paths = new List<string>();

        foreach (var argument in Split(commandLine).Skip(1))
        {
            var value = SwitchValue(argument) ?? argument;

            if (value.Length > 0 && Path.IsPathFullyQualified(value))
            {
                paths.Add(value);
            }
        }

        return paths;
    }

    /// <summary>What follows the first <c>=</c> of a switch such as <c>--user-data-dir=</c>, or null.</summary>
    private static string? SwitchValue(string argument)
    {
        if (argument.Length < 2 || argument[0] is not ('-' or '/'))
        {
            return null;
        }

        var equals = argument.IndexOf('=', StringComparison.Ordinal);

        return equals > 0 ? argument[(equals + 1)..] : null;
    }

    private static IReadOnlyList<string> Split(string commandLine)
    {
        var argv = CommandLineToArgv(commandLine, out var count);

        if (argv == 0)
        {
            return [];
        }

        try
        {
            var arguments = new string[count];

            for (var i = 0; i < count; i++)
            {
                arguments[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            }

            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgv(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
