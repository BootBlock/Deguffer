using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a tool's declared inputs (<see cref="ToolInputs"/>) held at one moment, compared with a later
/// reading to decide whether the tool would still give the answer it gave.
///
/// <para><b>The whole environment, not a list of names.</b> npm, pnpm and Yarn expand any
/// <c>${VAR}</c> written in their configuration, so the variables an answer depends on cannot be
/// listed without reading those files as the tool does. The block a tool Deguffer starts is given is
/// this process's own, which Windows never changes while it runs, so comparing all of it costs one
/// pass over a few hundred strings and never asks again for nothing.</para>
/// </summary>
internal sealed class ToolFingerprint
{
    private readonly InputStamp[] _stamps;
    private readonly KeyValuePair<string, string>[] _environment;

    public ToolFingerprint(IReadOnlyList<InputStamp> stamps, IReadOnlyDictionary<string, string> environment)
    {
        _stamps = [.. stamps];
        _environment = [.. environment.OrderBy(static variable => variable.Key, StringComparer.OrdinalIgnoreCase)];

        foreach (var stamp in _stamps)
        {
            IsComplete &= stamp.Presence is not PathPresence.Refused;
            Newest = Math.Max(Newest, stamp.Newest);
        }
    }

    /// <summary>False where Windows would not describe an input, which may then hold anything.</summary>
    public bool IsComplete { get; } = true;

    /// <summary>The latest creation or last-write time of any input, as a FILETIME.</summary>
    public long Newest { get; }

    /// <summary>
    /// Whether both readings are complete and every input and variable reads the same in both. An
    /// incomplete reading matches nothing, itself included.
    /// </summary>
    public bool Matches(ToolFingerprint other) =>
        IsComplete
        && other.IsComplete
        && _stamps.AsSpan().SequenceEqual(other._stamps)
        && SameEnvironment(_environment, other._environment);

    private static bool SameEnvironment(KeyValuePair<string, string>[] one, KeyValuePair<string, string>[] other)
    {
        if (one.Length != other.Length)
        {
            return false;
        }

        for (var index = 0; index < one.Length; index++)
        {
            // Windows compares variable names without regard to case, and a tool reads a value as written.
            if (!string.Equals(one[index].Key, other[index].Key, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(one[index].Value, other[index].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
