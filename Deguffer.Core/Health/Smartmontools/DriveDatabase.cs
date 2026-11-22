using System.Text.RegularExpressions;

namespace Deguffer.Core.Health.Smartmontools;

/// <summary>
/// The entries of a user's installed smartmontools drive database, searched as smartctl searches them:
/// in order, the first entry whose patterns match the whole model and firmware winning. The version
/// entry, the <c>DEFAULT</c> entry, USB bridge entries and any family starting with <c>$</c> name no
/// drive. Each pattern is built the first time a search reaches it, and kept.
/// </summary>
internal sealed class DriveDatabase
{
    private const string VersionMarker = "VERSION:";
    private const string DefaultMarker = "DEFAULT";
    private const string UsbMarker = "USB:";
    private const char IgnoredMarker = '$';

    private readonly DriveDatabaseEntry[] _drives;
    private readonly Lazy<Matcher?>[] _models;
    private readonly Lazy<Matcher?>[] _firmware;
    private readonly EntryPresets? _defaults;

    /// <param name="entries">Every file's entries, in the order smartctl reads the files.</param>
    internal DriveDatabase(IReadOnlyList<DriveDatabaseEntry> entries)
    {
        _drives = [.. entries.Where(NamesDrives)];
        _models = [.. _drives.Select(entry => new Lazy<Matcher?>(() => Matcher.Model(entry.ModelPattern)))];
        _firmware = [.. _drives.Select(entry => new Lazy<Matcher?>(() => Matcher.Firmware(entry.FirmwarePattern)))];
        // The first DEFAULT entry is the one smartctl uses, so one in drivedb-add.h replaces drivedb.h's.
        _defaults = entries.FirstOrDefault(entry => entry.Family == DefaultMarker) is { } defaults
            ? AttributePresets.Read(defaults.Presets, EntryKind.Default)
            : null;
    }

    /// <summary>
    /// What the database says about <paramref name="drive"/>: its model first, then the other spelling
    /// of it where there is one and the model is not listed.
    /// </summary>
    internal DatabaseAnswer Find(DriveIdentity drive)
    {
        var answer = Find(drive.Model, drive.Firmware);

        return answer == DatabaseAnswer.NotListed && drive.OtherModel is { } other ? Find(other, drive.Firmware) : answer;
    }

    /// <summary>What the database says about the drive whose model and firmware are given.</summary>
    internal DatabaseAnswer Find(string model, string firmware)
    {
        for (var i = 0; i < _drives.Length; i++)
        {
            if (_models[i].Value is not { } modelMatcher)
            {
                return DatabaseAnswer.CannotTell;
            }

            if (modelMatcher.Matches(model) is not { } modelMatches)
            {
                return DatabaseAnswer.CannotTell;
            }

            if (!modelMatches)
            {
                continue;
            }

            if (_firmware[i].Value is not { } firmwareMatcher)
            {
                return DatabaseAnswer.CannotTell;
            }

            if (firmwareMatcher.Matches(firmware) is not { } firmwareMatches)
            {
                return DatabaseAnswer.CannotTell;
            }

            if (!firmwareMatches)
            {
                continue;
            }

            var entry = _drives[i];

            return AttributePresets.Read(entry.Presets, EntryKind.Family) is { } presets
                ? new DatabaseAnswer.Found(entry.Family, presets, _defaults)
                : new DatabaseAnswer.EntryUnreadable(entry.Family);
        }

        return DatabaseAnswer.NotListed;
    }

    private static bool NamesDrives(DriveDatabaseEntry entry) =>
        !entry.Family.StartsWith(VersionMarker, StringComparison.Ordinal)
        && entry.Family != DefaultMarker
        && !entry.Family.StartsWith(UsbMarker, StringComparison.Ordinal)
        && !entry.Family.StartsWith(IgnoredMarker);

    /// <summary>An entry's pattern, or null where it uses syntax not translated here.</summary>
    private sealed class Matcher(Regex? pattern)
    {
        private static readonly Matcher Anything = new(null);

        internal static Matcher? Model(string pattern) => PosixPattern.Compile(pattern) is { } compiled ? new Matcher(compiled) : null;

        /// <summary>An empty firmware pattern matches any firmware. An empty model pattern matches only an empty model.</summary>
        internal static Matcher? Firmware(string pattern) => pattern.Length == 0 ? Anything : Model(pattern);

        /// <summary>Whether <paramref name="text"/> matches, or null where the match ran out of time.</summary>
        internal bool? Matches(string text)
        {
            try
            {
                return pattern?.IsMatch(text) ?? true;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }
    }
}
