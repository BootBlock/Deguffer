namespace Deguffer.App.Diagnostics;

/// <summary>Why Explore showed a tree, as <see cref="ShellEvents.ShowStart"/> states it.</summary>
internal enum ShowCause
{
    /// <summary>A partial tree a scan still under way handed over.</summary>
    Snapshot,

    /// <summary>A step to another place in the tree on screen: a folder opened or left, Back or Forward.</summary>
    Step,

    /// <summary>The tree a scan finished with.</summary>
    Finished,

    /// <summary>What is left of the tree on screen after a removal from it.</summary>
    Removal,
}
