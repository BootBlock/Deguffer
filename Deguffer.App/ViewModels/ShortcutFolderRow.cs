using System.Windows.Input;
using Deguffer.Core.Exploring.Links;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One row of the panel of broken shortcuts in the Start menu, on the desktop and in the Startup
/// folders: the link, the folder it is in, and what it points at.
///
/// <para>A value, as <see cref="ExploreFileRow"/> is: a new reading arrives as a new list, and
/// <see cref="Core.Viewing.LiveList"/> replaces only the rows that changed.</para>
/// </summary>
/// <param name="Remove">The panel's removal, handed the row as its parameter, so the button on the row reaches it.</param>
/// <param name="Reveal">The panel's "open containing folder", on the same terms.</param>
public sealed record ShortcutFolderRow(BrokenLink Link, ICommand Remove, ICommand Reveal)
{
    public string Path => Link.Path;

    public string Name => System.IO.Path.GetFileName(Link.Path);

    /// <summary>The folder it is in, in full: the Start menu's folders are named alike on every machine.</summary>
    public string Folder => System.IO.Path.GetDirectoryName(Link.Path) ?? string.Empty;

    /// <summary>What it points at. Every row's target is gone: the panel lists no other kind.</summary>
    public string TargetLine => $"Points at {Link.Target}, which is not there.";

    /// <summary>
    /// What the remove button does, in words, because the link is removed and not what it led to: a
    /// junction removed through its target would take a folder of somebody's files with it.
    /// </summary>
    public string RemoveTip => Link.Kind is LinkKind.Shortcut
        ? "Move this shortcut to the Recycle Bin."
        : "Move this link to the Recycle Bin. Only the link goes: nothing it pointed at is touched.";

    /// <summary>What a screen reader says for the row, which is announced by this rather than by its parts.</summary>
    public string Description => $"{Name}, in {Folder}. {TargetLine}";

    /// <summary>What tells the row apart from the rows already on screen.</summary>
    public string Key => Link.Path;
}
