using System.ComponentModel;
using System.Runtime.InteropServices;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>Give an existing file a second name, as <c>mklink /H</c> does, which .NET has no call for.</summary>
public static class HardLink
{
    /// <summary>Make <paramref name="link"/> a second name of <paramref name="existing"/>, creating its folder.</summary>
    /// <returns><paramref name="link"/>.</returns>
    public static string To(string existing, string link)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        if (!CreateHardLink(LongPath.Extended(link), LongPath.Extended(existing), securityAttributes: 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not link {link} to {existing}.");
        }

        return link;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
}
