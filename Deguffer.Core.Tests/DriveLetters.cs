namespace Deguffer.Core.Tests;

internal static class DriveLetters
{
    /// <summary>
    /// The drive letter the fakes stand for. A test serves its file table from a fake factory, and
    /// an Explore scan that falls back to the walk walks it with a <see cref="WalkRecorder"/>, so
    /// nothing reads what the machine running the suite has at this letter: it may be mounted, mapped
    /// or a share that does not answer.
    ///
    /// <para>A <see cref="Scanning.DirectoryScanner"/> has no walk to stand in for. The tests that
    /// measure through one at this letter assert the table route, so a scanner that walked instead
    /// fails them on the route.</para>
    /// </summary>
    public const char Faked = 'Q';
}
