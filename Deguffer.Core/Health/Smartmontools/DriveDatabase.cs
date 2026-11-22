using System.Text.RegularExpressions;

namespace Deguffer.Core.Health.Smartmontools;

/// <summary>What a drive database says about one drive.</summary>
internal enum DatabaseVerdict
{
    /// <summary>An entry names the drive's family, and its options were read.</summary>
    Found,

    /// <summary>No entry names the drive.</summary>
    NotListed,

    /// <summary>An entry names the drive, and its options could not be read, so none is used.</summary>
    EntryUnreadable,

    /// <summary>
    /// An entry searched before any match has a pattern not translated here, or one that ran out of
    /// time. The database is searched in order and its first match wins, so a later match could be the
    /// wrong family.
    /// </summary>
    CannotTell,
}

/// <summary>The database's answer for one drive.</summary>
/// <param name="Family">The family the matching entry names, where one matched.</param>
/// <param name="Presets">Its attribute options, where they were read.</param>
/// <param name="Defaults">The <c>DEFAULT</c> entry's options, which name an attribute an entry's option leaves unnamed.</param>
internal sealed record DatabaseAnswer(DatabaseVerdict Verdict, string? Family, EntryPresets? Presets, EntryPresets? Defaults)
{
    internal static readonly DatabaseAnswer NotListed = new(DatabaseVerdict.NotListed, null, null, null);

    internal static readonly DatabaseAnswer CannotTell = new(DatabaseVerdict.CannotTell, null, null, null);
}

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
            ? AttributePresets.Read(defaults.Presets, isDefault: true)
            : null;
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

            return AttributePresets.Read(entry.Presets, isDefault: false) is { } presets
                ? new DatabaseAnswer(DatabaseVerdict.Found, entry.Family, presets, _defaults)
                : new DatabaseAnswer(DatabaseVerdict.EntryUnreadable, entry.Family, null, null);
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
