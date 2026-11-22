namespace Deguffer.Core.Health.Smartmontools;

/// <summary>
/// The strings a drive database is searched by. smartctl searches by the model and firmware in the
/// drive's IDENTIFY data, which only an administrator can read. Without it, Windows' description of the
/// drive stands in: its model, and its product alone where it also gives a vendor, because the product
/// may be what IDENTIFY holds.
/// </summary>
/// <param name="OtherModel">A second spelling of the model, tried where the first is not listed.</param>
internal sealed record DriveIdentity(string Model, string? OtherModel, string Firmware);

/// <summary>What the installed drive database says about one drive, in the words the drive's details give it.</summary>
internal abstract record DatabaseAnswer
{
    internal static readonly DatabaseAnswer NotListed = new Unlisted();

    /// <summary>
    /// An entry searched before any match has a pattern not translated here, or one that ran out of
    /// time. The database is searched in order and its first match wins, so a later match could be
    /// the wrong family.
    /// </summary>
    internal static readonly DatabaseAnswer CannotTell = new Undecided();

    /// <summary>The value of the drive's database detail.</summary>
    internal abstract string Detail { get; }

    /// <summary>The entry's reading of the drive's attributes, where one is used.</summary>
    internal virtual DatabaseFamily? Family(DriveMedium medium) => null;

    /// <summary>An entry names the drive's family, and its options were read.</summary>
    /// <param name="Defaults">The <c>DEFAULT</c> entry's options, which name an attribute the entry's option leaves unnamed.</param>
    internal sealed record Found(string Name, EntryPresets Presets, EntryPresets? Defaults) : DatabaseAnswer
    {
        internal override string Detail => Name;

        internal override DatabaseFamily Family(DriveMedium medium) => new(Name, Presets, Defaults, medium);
    }

    /// <summary>An entry names the drive, and its options could not be read, so none is used.</summary>
    internal sealed record EntryUnreadable(string Name) : DatabaseAnswer
    {
        internal override string Detail => $"The entry for {Name} could not be read, so it is not used";
    }

    /// <summary>A database file could not be read, so none of the database is used.</summary>
    /// <param name="Line">The line it failed on, or 0 where it could not be read at all.</param>
    internal sealed record FileUnreadable(string File, int Line) : DatabaseAnswer
    {
        internal override string Detail => Line > 0
            ? $"{File} could not be read at line {Line}, so none of it is used"
            : $"{File} could not be read, so none of it is used";
    }

    private sealed record Unlisted : DatabaseAnswer
    {
        internal override string Detail => "No entry for this drive";
    }

    private sealed record Undecided : DatabaseAnswer
    {
        internal override string Detail => "An entry searched before this drive's could not be read, so no entry is used";
    }
}
