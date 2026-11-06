namespace Deguffer.Core.Configuration;

/// <summary>
/// Where the Storage rows' remembered ticks are kept: <see cref="SelectionStore"/>, and a test's store
/// whose read can be held back, which is how the shell proves it opens without waiting for this file.
/// </summary>
public interface ISelectionStore
{
    /// <summary>The remembered rows, or none where they cannot be read.</summary>
    IReadOnlyDictionary<string, RememberedSelection> Load();

    /// <summary>Persist <paramref name="selections"/>. Returns whether it was written.</summary>
    bool Save(IReadOnlyDictionary<string, RememberedSelection> selections);
}
