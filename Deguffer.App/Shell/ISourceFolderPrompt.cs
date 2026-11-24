using Deguffer.Core.Configuration;

namespace Deguffer.App.Shell;

/// <summary>
/// Asks the user for a folder to look for build output in, and whether to approve one Deguffer has
/// something to warn about. What is asked, and what an answer means, is decided by
/// <see cref="SourceFolderAddition"/> and Core; this seam only carries the questions to a surface that
/// can ask them.
///
/// <para>It exists so the pages that add a source folder can be reasoned about without the system
/// picker or a dialog, which need a window a test does not have.</para>
/// </summary>
public interface ISourceFolderPrompt
{
    /// <summary>
    /// The folder the user chose, exactly as the picker returned it, or null where they chose none.
    /// The value may name nothing on a disk: see <see cref="PickedFolder"/>.
    /// </summary>
    Task<string?> PickAsync();

    /// <summary>
    /// Whether the user approves <paramref name="approval"/>'s folder once they have read its
    /// <see cref="SourceRootApproval.Consequence"/>. Asked only where
    /// <see cref="SourceRootApproval.NeedsConfirming"/>.
    /// </summary>
    Task<bool> AcceptsAsync(SourceRootApproval approval, CancellationToken ct);
}
