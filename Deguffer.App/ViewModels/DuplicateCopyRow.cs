using Deguffer.Core.Duplicates;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>One copy in a group: where it is, cut where its path differs from the others', and what it is.</summary>
public sealed class DuplicateCopyRow
{
    public DuplicateCopyRow(DuplicateCandidate copy, PathParts path)
    {
        Copy = copy;
        Same = path.Same;
        Differs = path.Differs;
        SameEnd = path.SameEnd;
        Size = FreeSpace.Format(copy.Length);
        Modified = copy.Modified.ToLocalTime().ToString("d MMM yyyy HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
        Names = copy.HasSeveralNames
            ? $"One file with {copy.NameCount:N0} names, so removing one name frees nothing: {string.Join("; ", copy.Names)}"
            : string.Empty;
        Description = $"{copy.Path}, {Size}, last modified {Modified}"
            + (IsReference ? ", in a reference location" : string.Empty)
            + (copy.HasSeveralNames ? $", one file with {copy.NameCount:N0} names" : string.Empty);
    }

    public DuplicateCandidate Copy { get; }

    /// <summary>The start of the path, which another copy's path starts with too.</summary>
    public string Same { get; }

    /// <summary>What no other copy's path shares at its start or end, which the row picks out.</summary>
    public string Differs { get; }

    /// <summary>The end of the path, which another copy's path ends with too.</summary>
    public string SameEnd { get; }

    public string Size { get; }

    public string Modified { get; }

    public bool IsReference => Copy.Role == LocationRole.Reference;

    public bool HasSeveralNames => Copy.HasSeveralNames;

    /// <summary>Every name of a file with several, or an empty string, because a binding cannot show null.</summary>
    public string Names { get; }

    /// <summary>Everything the row shows, in one sentence, for a screen reader.</summary>
    public string Description { get; }
}
