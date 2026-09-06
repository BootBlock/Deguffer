using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Stands in for <c>SHEmptyRecycleBin</c>, recording the volume handed across.
///
/// <para><b>Injected everywhere, never defaulted.</b> The real emptier would empty the Recycle Bin
/// of whoever runs the suite, so a test that reached it would destroy a developer's own deleted
/// files on the way to a green tick. Every fixture that plans a Recycle Bin passes one of these.</para>
///
/// <para>Recording is the point rather than a convenience, for the reason
/// <see cref="FakeRecycleBin"/> gives: §6.3 is a requirement about the <em>form</em> of the path
/// that crosses into Win32, this boundary requires the display form rather than the extended-length
/// one, and no outcome of an emptying can demonstrate which one crossed.</para>
/// </summary>
public sealed class FakeRecycleBinEmptier : IRecycleBinEmptier
{
    private readonly Func<string, RecycleBinEmptyOutcome> _behaviour;

    /// <summary>
    /// Empties the account directories under the volume's bin, which is what Windows' own effect
    /// looks like from here: the contents go, the directory stays, and every other account's is
    /// left alone.
    /// </summary>
    public FakeRecycleBinEmptier()
        : this(volumeRoot =>
        {
            var bin = Path.Combine(volumeRoot, "$Recycle.Bin");

            if (!Directory.Exists(bin))
            {
                return new RecycleBinEmptyOutcome(Emptied: true);
            }

            foreach (var account in Directory.EnumerateDirectories(bin))
            {
                // Only the account this process runs as, which is what was measured of the real
                // call. A fake that emptied every account would make the §5.6 negative pass by
                // never being exercised.
                if (!Path.GetFileName(account).Equals(FakeUserEnvironment.SecurityIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The whole tree, not the top level: a recycled *directory* keeps its contents, so
                // a bin is only flat in the ordinary case. Through the extended-length form,
                // because that content can be past MAX_PATH and the real shell copes with it.
                foreach (var child in Directory.EnumerateDirectories(LongPath.Extended(account)))
                {
                    Directory.Delete(child, recursive: true);
                }

                foreach (var file in Directory.EnumerateFiles(LongPath.Extended(account)))
                {
                    File.Delete(file);
                }
            }

            return new RecycleBinEmptyOutcome(Emptied: true);
        })
    {
    }

    public FakeRecycleBinEmptier(Func<string, RecycleBinEmptyOutcome> behaviour) => _behaviour = behaviour;

    /// <summary>Every volume root handed across, in order.</summary>
    public List<string> VolumeRoots { get; } = [];

    /// <summary>
    /// An emptier that reaches every account's bin on the volume, not only this one's — the
    /// over-broad rule §5.6's negative exists to catch. It passes every assertion that the target
    /// was emptied.
    /// </summary>
    public static FakeRecycleBinEmptier TakingEveryAccount() => new(volumeRoot =>
    {
        var bin = Path.Combine(volumeRoot, "$Recycle.Bin");

        // Emptied in place, exactly as the correct behaviour empties in place. Modelling the
        // over-reach as a *removal* would be modelling a shape the real call does not have, and a
        // §5.6 negative that only catches the shape Windows never takes catches nothing.
        foreach (var account in Directory.EnumerateDirectories(bin))
        {
            foreach (var child in Directory.EnumerateDirectories(LongPath.Extended(account)))
            {
                Directory.Delete(child, recursive: true);
            }

            foreach (var file in Directory.EnumerateFiles(LongPath.Extended(account)))
            {
                File.Delete(file);
            }
        }

        return new RecycleBinEmptyOutcome(Emptied: true);
    });

    /// <summary>An emptier that refuses, as the shell does for a bin it will not read.</summary>
    public static FakeRecycleBinEmptier Refusing(string message) =>
        new(_ => new RecycleBinEmptyOutcome(Emptied: false, message));

    /// <summary>
    /// An emptier that reports success and removes nothing, which is what a bin already emptied
    /// between the preview and the clean looks like from here.
    /// </summary>
    public static FakeRecycleBinEmptier DoingNothing() =>
        new(_ => new RecycleBinEmptyOutcome(Emptied: true));

    public RecycleBinEmptyOutcome Empty(string volumeRoot)
    {
        VolumeRoots.Add(volumeRoot);
        return _behaviour(volumeRoot);
    }
}
