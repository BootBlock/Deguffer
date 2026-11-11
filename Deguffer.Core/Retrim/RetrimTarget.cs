namespace Deguffer.Core.Retrim;

/// <summary>
/// One volume a retrim was accepted for, named both ways: by the <c>\\?\Volume{GUID}\</c> name Windows
/// knows it by, which is what the retrim is asked of, and by the mount point the offer showed, which is
/// what its result names.
///
/// <para>The name travels with the mount point because a letter can move to another volume between the
/// offer and the retrim, across a restart as administrator above all, and the volume the user accepted
/// is the one with the name, wherever it is mounted by then.</para>
/// </summary>
/// <param name="Root">The mount point the offer showed, such as <c>D:\</c>.</param>
/// <param name="VolumeName">The volume's <c>\\?\Volume{GUID}\</c> name.</param>
public sealed record RetrimTarget(string Root, string VolumeName);
