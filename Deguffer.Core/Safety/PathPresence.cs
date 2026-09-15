namespace Deguffer.Core.Safety;

/// <summary>
/// What a probe by name established about a path: the three answers Windows gives, where
/// <see cref="Directory.Exists"/> passes on two.
///
/// <para><b>The missing answer is <see cref="Refused"/>, and folding it into
/// <see cref="Absent"/> is how Deguffer came to report a full cache as a tool that is not
/// installed.</b> <see cref="Directory.Exists"/> swallows every failure and answers false, so a
/// provider that probes its root by name cannot tell a location that is not there from one
/// Windows would not describe. <see cref="Providers.ICleanupProvider.IsPresentAsync"/> then denies
/// the row exists at all, and the largest thing on the disk is reported as nothing.</para>
///
/// <para><b>Two conditions produce it, and only one of them is anybody's mistake.</b> An access
/// rule that denies the listing of a directory's parent and the attribute read on the directory
/// itself is the deliberate case — <c>DeniedDirectory.WithUnreadableAttributes</c> builds it, and
/// nothing short of both ends refuses, because NTFS answers <c>GetFileAttributes</c> out of the
/// parent's own index. The ordinary case is a directory symbolic link the account created itself:
/// Windows may decline to follow it, answering <c>ERROR_UNTRUSTED_MOUNT_POINT</c> (448) for
/// everything below it while the link's own attributes read normally. Relocating a cache onto
/// another drive is a thing developers do on purpose, so that is the case the user most wants an
/// answer about.</para>
/// </summary>
public enum PathPresence
{
    /// <summary>
    /// Windows says nothing of the kind asked about is there, and that is a complete answer: an
    /// absent directory holds nothing, so a caller reading it as empty is reading it correctly.
    /// </summary>
    Absent,

    /// <summary>Windows says it is there.</summary>
    Present,

    /// <summary>
    /// Windows would not say. Not an answer at all, and never to be read as <see cref="Absent"/>:
    /// the location may hold the largest tree on the machine, and nothing here establishes
    /// otherwise.
    /// </summary>
    Refused,
}
