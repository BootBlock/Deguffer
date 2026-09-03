namespace Deguffer.Core.Safety;

/// <summary>
/// How old a file has to be before Deguffer will delete it, as the instant it must predate.
///
/// <para>A cache is not always cold. A package manager unpacking a download, a build writing
/// intermediate output, an agent keeping notes in a scratch folder — all of them leave files that
/// are indistinguishable from stale ones by name, by location and by size, and differ only in
/// having been written minutes ago. §5.3 already refuses anything the operating system is holding
/// open, but a process that writes a file, closes it, and will open it again in an hour holds
/// nothing at all. This is the guard for that case, and it is off unless the user asks for it.</para>
///
/// <para><b>It is an instant, not a duration.</b> Fixing the cut-off once, when the plan is made,
/// is what lets the preview and the clean agree: a file that is 7 hours 58 minutes old during the
/// preview must not become deletable by the time the user presses Clean, because the preview
/// promised to leave it. Carrying a <see cref="TimeSpan"/> and re-reading the clock at deletion
/// would break exactly that promise, and would do it silently — the difference only shows on a
/// preview the user left open. It also makes the estimate exact rather than a figure that decays,
/// which is why the scanners can filter on the same value.</para>
///
/// <para><b>The newest of creation and last write.</b> Neither alone is enough. A file copied or
/// extracted into a cache keeps the source's last-write time, which can be years old, while its
/// creation time here is now — deleting it is the mistake this type exists to prevent. Last access
/// is deliberately not consulted: §8's first open question records that NTFS last-access updates
/// are unreliable by default, so a file kept because of one would be kept by accident.</para>
///
/// <para><b>It protects files, and directories only through them.</b> A directory still holding a
/// protected file survives because it is not empty, which is what <see cref="Execution.DirectoryRemover"/>
/// already does for a file something has locked. A directory that is young and <em>empty</em> is
/// removed, and that is a decision rather than an oversight: a directory's own timestamp moves
/// every time an entry is added or removed, so the removal itself would make every folder it had
/// just emptied look new, and the guard would keep the whole tree it was asked to delete.</para>
/// </summary>
public readonly record struct MinimumAge
{
    /// <summary>No guard: every file a plan targets is deletable. The shipped default.</summary>
    public static readonly MinimumAge Off = default;

    /// <summary>
    /// The longest window this will accept. Far beyond anything the settings page offers, and
    /// present for one reason: <see cref="KeepFromFileTime"/> distinguishes "off" from "on" by being
    /// zero, and a window of roughly 424 years would put the cut-off at the start of the NTFS epoch
    /// and produce a guard that silently reads as off.
    /// </summary>
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(3650);

    /// <summary>NTFS counts 100-nanosecond intervals from here, which is what a FILETIME is.</summary>
    private static readonly DateTime FileTimeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private MinimumAge(TimeSpan window, DateTime keepFromUtc)
    {
        Window = window;
        KeepFromUtc = keepFromUtc;
        KeepFromFileTime = ToFileTime(keepFromUtc);
    }

    /// <summary>
    /// What the user asked for, kept beside the instant it produced so that a plan can say
    /// "the last 8 hours" rather than quoting a timestamp back at them. Zero when the guard is off.
    /// </summary>
    public TimeSpan Window { get; }

    /// <summary>
    /// Files written or created at or after this instant are left alone. Null when the guard is off.
    /// </summary>
    public DateTime? KeepFromUtc { get; }

    /// <summary>
    /// The same instant as a FILETIME, so the comparison against a record read straight out of the
    /// master file table is an integer one. Zero when the guard is off.
    ///
    /// The table runs to millions of records and <see cref="Scanning.Mft.MftVolumeIndex"/> tests
    /// every one it sums, so converting each record's timestamps into a <see cref="DateTime"/> to
    /// ask this question would be per-record work for an answer the constructor can compute once
    /// (G4). It is also the safer direction: a garbled timestamp is a long that compares, where
    /// <c>DateTime.FromFileTimeUtc</c> would throw part-way through a scan.
    /// </summary>
    public long KeepFromFileTime { get; }

    public bool IsOn => KeepFromUtc is not null;

    /// <summary>
    /// A guard that keeps anything touched inside <paramref name="window"/> of
    /// <paramref name="nowUtc"/>, or <see cref="Off"/> for a window of zero or less.
    ///
    /// <paramref name="nowUtc"/> is passed in rather than read from the clock so that a test can
    /// state the instant it is reasoning about, and so that one planning pass fixes one cut-off for
    /// every provider in it.
    /// </summary>
    public static MinimumAge Within(TimeSpan window, DateTime nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(window, MaximumWindow);

        return window <= TimeSpan.Zero ? Off : new MinimumAge(window, nowUtc.ToUniversalTime() - window);
    }

    /// <summary>
    /// The same, from the whole hours the settings page stores. Zero means off, which is what an
    /// unset preference deserialises to.
    /// </summary>
    public static MinimumAge WithinHours(int hours, DateTime nowUtc) =>
        hours <= 0 ? Off : Within(TimeSpan.FromHours(Math.Min(hours, MaximumWindow.TotalHours)), nowUtc);

    /// <summary>
    /// Whether a file this recent is left alone, given the newer of its creation and last-write
    /// FILETIMEs. The one implementation of the rule; every other overload reduces to this.
    /// </summary>
    public bool Protects(long newestFileTime) =>
        KeepFromFileTime != 0 && newestFileTime >= KeepFromFileTime;

    /// <summary>The same, from the two timestamps NTFS stores separately.</summary>
    public bool Protects(long createdFileTime, long lastWrittenFileTime) =>
        Protects(Math.Max(createdFileTime, lastWrittenFileTime));

    /// <summary>The same question of a file the walk is holding, which costs no further I/O.</summary>
    public bool Protects(FileSystemInfo entry) => Protects(NewestFileTimeOf(entry));

    /// <summary>
    /// The one number this guard reads, from an entry an enumeration has already materialised.
    ///
    /// Public because the deletion path stores it rather than re-asking: <see cref="IFileSystem"/>
    /// hands a <see cref="FileSystemEntry"/> across, which is a struct sized for trees of hundreds
    /// of thousands of files and cannot carry a <see cref="FileSystemInfo"/>. Encoding it in one
    /// place is what keeps the walk's answer and the removal's answer the same answer.
    /// </summary>
    public static long NewestFileTimeOf(FileSystemInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return Math.Max(ToFileTime(entry.CreationTimeUtc), ToFileTime(entry.LastWriteTimeUtc));
    }

    /// <summary>
    /// The same question of a path, for the one caller that has no entry in hand: a plan deciding
    /// whether to offer a step for a single named file at all.
    ///
    /// A path that cannot be read answers false. That is the direction §5.3 already takes — a
    /// refusal is ordinary, and the removal will refuse it too — and the alternative would let an
    /// unreadable path quietly protect itself from a step the user asked for.
    /// </summary>
    public bool ProtectsFile(string path)
    {
        if (!IsOn)
        {
            return false;
        }

        var file = new FileInfo(LongPath.Extended(path));

        return file.Exists && Protects(file);
    }

    /// <summary>
    /// The window as a phrase, for the sentence a plan puts in front of the user. Whole hours up to
    /// two days, then whole days where the window divides into them — "36 hours" says more than
    /// "1 day" does, and "7 days" says more than "168 hours".
    /// </summary>
    public string Describe()
    {
        var hours = (int)Math.Round(Window.TotalHours);

        return hours switch
        {
            // Unreachable while the guard is on, and answered rather than thrown: a phrase is not
            // worth failing a plan over.
            <= 0 => "no time at all",
            1 => "hour",
            < 48 => $"{hours} hours",
            _ when hours % 24 == 0 => $"{hours / 24} days",
            _ => $"{hours} hours",
        };
    }

    /// <summary>
    /// A FILETIME from a <see cref="DateTime"/>, without <c>DateTime.ToFileTimeUtc</c>'s throw for
    /// anything before 1601. A timestamp NTFS never set arrives as exactly the epoch, and clamping
    /// it to zero puts it in the same place the master file table's own zero lands.
    /// </summary>
    private static long ToFileTime(DateTime value)
    {
        // Normalised before the comparison rather than after it. A DateTime compares by its ticks
        // whatever its kind claims, so testing against the epoch first and converting second would
        // measure one instant and answer about another.
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

        return utc <= FileTimeEpoch ? 0 : (utc - FileTimeEpoch).Ticks;
    }
}
