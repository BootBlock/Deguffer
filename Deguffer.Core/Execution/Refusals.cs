using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>Files Windows would not release for one reason, and the bytes they hold.</summary>
public readonly record struct RefusalTally(int Files, long Bytes)
{
    public static RefusalTally operator +(RefusalTally left, RefusalTally right) =>
        new(left.Files + right.Files, left.Bytes + right.Bytes);
}

/// <summary>
/// What Windows would not release, counted by <see cref="RefusalReason"/>.
///
/// <para><b>Bytes as well as files.</b> A bare count reads as a handful of locked files whatever it
/// stands for, and "252994 item(s) in use were left alone" beside a run that reclaimed gigabytes
/// said nothing about the 5.9 GB that the next preview then offered again. The size is what tells
/// the reader the run fell short, and by how much.</para>
/// </summary>
public readonly record struct Refusals(RefusalTally InUse, RefusalTally Denied)
{
    public static readonly Refusals None = default;

    public bool IsEmpty => InUse.Files == 0 && Denied.Files == 0;

    public long Bytes => InUse.Bytes + Denied.Bytes;

    /// <summary>One file of <paramref name="bytes"/>, refused for <paramref name="reason"/>.</summary>
    public static Refusals One(RefusalReason reason, long bytes) => reason == RefusalReason.InUse
        ? new Refusals(new RefusalTally(1, bytes), default)
        : new Refusals(default, new RefusalTally(1, bytes));

    public static Refusals operator +(Refusals left, Refusals right) =>
        new(left.InUse + right.InUse, left.Denied + right.Denied);
}
