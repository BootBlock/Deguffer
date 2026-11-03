using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What reading CapCut's record of which build to start established.</summary>
public enum CapCutRecordState
{
    /// <summary>The record names a build.</summary>
    Named,

    /// <summary>There is no record.</summary>
    Absent,

    /// <summary>Windows would not say whether the record is there.</summary>
    Unreached,

    /// <summary>The record is there and could not be read.</summary>
    Unreadable,

    /// <summary>The record was read and names no build.</summary>
    NamesNothing,
}

/// <summary>
/// <c>Apps\configure.ini</c>, the launcher's record of which build to start: the line
/// <c>last_version=&lt;build folder&gt;</c>.
///
/// <para><b>Only the key is relied on.</b> Two open-source tools that pin CapCut to an old build write
/// this file, and they disagree on the section the key sits in (<c>[Configure]</c> in one,
/// <c>[Version]</c> in the other). Jianying Pro's own file uses a section of its name. So the reading
/// takes the first <c>last_version</c> line in any section and ignores the rest of the file. A file
/// with two that disagree is read as naming nothing, because Deguffer cannot know which one the
/// launcher reads.</para>
/// </summary>
/// <param name="State">What the reading established.</param>
/// <param name="Version">The build folder the record names, where <paramref name="State"/> is <see cref="CapCutRecordState.Named"/>.</param>
public sealed record CapCutLaunchRecord(CapCutRecordState State, string? Version)
{
    public const string FileName = "configure.ini";

    public const string Key = "last_version";

    /// <summary>Far past a file of a few lines, and small enough that a file of another kind by that name is refused.</summary>
    private const int MaximumBytes = 64 * 1024;

    /// <summary>The record in <paramref name="apps"/>.</summary>
    public static CapCutLaunchRecord In(string apps)
    {
        var path = Path.Combine(apps, FileName);

        switch (LongPath.ProbeFile(path))
        {
            case PathPresence.Absent:
                return new(CapCutRecordState.Absent, null);

            case PathPresence.Refused:
                return new(CapCutRecordState.Unreached, null);
        }

        return BoundedFile.Read(path, MaximumBytes) is { } content
            ? Parse(Encoding.UTF8.GetString(content.Span))
            : new(CapCutRecordState.Unreadable, null);
    }

    /// <summary>The build named in the text of a record.</summary>
    public static CapCutLaunchRecord Parse(string text)
    {
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            var equals = line.IndexOf('=');

            if (equals > 0 && line[..equals].Trim().Equals(Key, StringComparison.OrdinalIgnoreCase)
                && line[(equals + 1)..].Trim() is { Length: > 0 } value)
            {
                named.Add(value);
            }
        }

        return named.Count == 1
            ? new(CapCutRecordState.Named, named.Single())
            : new(CapCutRecordState.NamesNothing, null);
    }
}
