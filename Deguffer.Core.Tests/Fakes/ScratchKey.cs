using Microsoft.Win32;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A registry key that removes itself, so a test that has to read the real registry can write to one
/// first. The registry counterpart of <see cref="TempDirectory"/>, down to the sweep it sets off.
/// </summary>
/// <remarks>
/// Almost nothing in the suite wants this. A rule is proved against <c>FakeUserEnvironment</c>, and
/// the only tests that come here are the ones asserting what the real platform answers, where a fake
/// would be asserting its own reply.
/// </remarks>
internal sealed class ScratchKey : IDisposable
{
    internal ScratchKey()
    {
        // Before this run's first key, so what an interrupted run left behind goes now rather than
        // staying for good.
        ScratchRegistry.SweepOnce();

        Path = $@"{ScratchRegistry.Parent}\{ScratchRegistry.NewName()}";
        Key = Registry.CurrentUser.CreateSubKey(Path);
    }

    /// <summary>The key's path under <c>HKEY_CURRENT_USER</c>, for a test that reads it by name.</summary>
    internal string Path { get; }

    /// <summary>The open key, for a test that writes values into it.</summary>
    internal RegistryKey Key { get; }

    /// <summary>
    /// Close the key and remove it, the subtree included.
    ///
    /// <para>A refused delete is not forgiven here, unlike a scratch tree's, because nothing holds a
    /// registry key open the way a scanner holds a file: a delete that fails would be telling us
    /// something. A key that is already gone is another matter, and is allowed.</para>
    /// </summary>
    public void Dispose()
    {
        Key.Dispose();

        Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
    }
}
