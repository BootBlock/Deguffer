using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A Windows directory and a machine-wide data directory rooted in a temp tree.
///
/// This is what makes §5.2 against <c>C:\Windows</c> provable at all. The rule that matters — a
/// provider reaching in there never reaches <c>WinSxS</c> or <c>Windows\Installer</c> — has to be
/// demonstrated by actually running a deletion, on a machine where nobody may delete anything in
/// either. So the directory a provider is handed is one the test built.
/// </summary>
public sealed class FakeSystemDirectories : ISystemDirectories
{
    public FakeSystemDirectories(string root)
    {
        WindowsDirectory = Path.Combine(root, "Windows");
        ProgramData = Path.Combine(root, "ProgramData");
        ProgramFiles = Path.Combine(root, "Program Files");
        ProgramFilesX86 = Path.Combine(root, "Program Files (x86)");

        Directory.CreateDirectory(WindowsDirectory);
        Directory.CreateDirectory(ProgramData);
        Directory.CreateDirectory(ProgramFiles);
        Directory.CreateDirectory(ProgramFilesX86);
    }

    public string WindowsDirectory { get; }

    public string ProgramData { get; }

    public string ProgramFiles { get; }

    public string ProgramFilesX86 { get; }
}
