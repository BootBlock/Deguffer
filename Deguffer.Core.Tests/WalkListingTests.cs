using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The walk's larger listing buffer, and what happens where a server will not take it.
///
/// <para>A refusal here leaves every total above the folder short, so a listing that failed only
/// because of its buffer must not become one. No local volume rejects a buffer, so a fake listing
/// stands in for the server that does: it fails part-way at any size above the smallest.</para>
/// </summary>
public sealed class WalkListingTests
{
    private const string Folder = @"\\?\Z:\share\folder";
    private const int Large = 256 * 1024;

    [Fact]
    public void ListsAFolderAgainWithTheSmallestBufferWhenTheLargerOneIsRejected()
    {
        var server = new FakeServer();
        var listing = new WalkListing(new ListingBuffer(Large), server.List);

        var contents = listing.Read(Folder);

        Assert.False(contents.WasRefused);
        Assert.Equal(["a.bin", "b.bin", "c.bin"], contents.Entries.Select(e => e.Name));
        Assert.Equal([Large, WalkTuning.MinimumListingBuffer], server.BuffersAskedFor);
    }

    /// <summary>
    /// Once the smaller buffer has listed a folder the larger one could not, the rest of the walk asks
    /// for the smaller one first. A share answers every folder alike, and paying a failed call for
    /// each of its folders would make the walk slower than it was before the buffer grew.
    /// </summary>
    [Fact]
    public void AsksForTheSmallestBufferFromThenOn()
    {
        var server = new FakeServer();
        var buffer = new ListingBuffer(Large);

        new WalkListing(buffer, server.List).Read(Folder);
        server.BuffersAskedFor.Clear();

        new WalkListing(buffer, server.List).Read(Folder + @"\next");

        Assert.Equal([WalkTuning.MinimumListingBuffer], server.BuffersAskedFor);
    }

    /// <summary>
    /// A folder the account may not read is the refusal a volume holds hundreds of, and no buffer
    /// changes it, so it is reported at once rather than listed twice.
    /// </summary>
    [Fact]
    public void ReportsAFolderItMayNotReadWithoutListingItAgain()
    {
        var server = new FakeServer { Answer = new ListingEnd(PathPresence.Refused, Denied: true) };

        var contents = new WalkListing(new ListingBuffer(Large), server.List).Read(Folder);

        Assert.True(contents.WasRefused);
        Assert.Equal([Large], server.BuffersAskedFor);
    }

    /// <summary>
    /// A folder that fails at both sizes was not refused because of its buffer. It is reported as
    /// refused, and the rest of the walk keeps the larger buffer.
    /// </summary>
    [Fact]
    public void KeepsTheLargerBufferWhenTheSmallestFailsToo()
    {
        var server = new FakeServer { FailsAtEverySize = true };
        var buffer = new ListingBuffer(Large);

        var contents = new WalkListing(buffer, server.List).Read(Folder);

        Assert.True(contents.WasRefused);
        Assert.Equal([Large, WalkTuning.MinimumListingBuffer], server.BuffersAskedFor);
        Assert.Equal(Large, buffer.Options.BufferSize);
    }

    [Fact]
    public void DoesNotRetryWhenItAlreadyAskedForTheSmallestBuffer()
    {
        var server = new FakeServer { FailsAtEverySize = true };

        new WalkListing(new ListingBuffer(WalkTuning.MinimumListingBuffer), server.List).Read(Folder);

        Assert.Equal([WalkTuning.MinimumListingBuffer], server.BuffersAskedFor);
    }

    /// <summary>
    /// Links and files carrying a reparse point are handed back apart from the ordinary entries, which
    /// keep the order they were listed in.
    /// </summary>
    [Fact]
    public void SetsLinksAndMarkedFilesApartFromTheOrdinaryEntries()
    {
        var server = new FakeServer
        {
            Entries =
            [
                Entry("first.bin", FileAttributes.Archive),
                Entry("link", FileAttributes.Directory | FileAttributes.ReparsePoint),
                Entry("folder", FileAttributes.Directory),
                Entry("placeholder.bin", FileAttributes.Archive | FileAttributes.ReparsePoint),
                Entry("last.bin", FileAttributes.Archive),
            ],
        };

        var contents = new WalkListing(new ListingBuffer(WalkTuning.MinimumListingBuffer), server.List).Read(Folder);

        Assert.Equal(["first.bin", "folder", "last.bin"], contents.Entries.Select(e => e.Name));
        Assert.Equal(["link"], contents.Links.Select(e => e.Name));
        Assert.Equal(["placeholder.bin"], contents.ReparseFiles.Select(e => e.Name));
    }

    private static WalkEntry Entry(string name, FileAttributes attributes) =>
        new(Folder, name, attributes, 0, DateTime.UnixEpoch, DateTime.UnixEpoch);

    /// <summary>
    /// A server that rejects any buffer above the smallest, after it has already returned one entry,
    /// as a listing that fails part-way does.
    /// </summary>
    private sealed class FakeServer
    {
        public List<WalkEntry> Entries { get; init; } =
        [
            Entry("a.bin", FileAttributes.Archive),
            Entry("b.bin", FileAttributes.Archive),
            Entry("c.bin", FileAttributes.Archive),
        ];

        /// <summary>How a rejected listing ends. ERROR_INVALID_PARAMETER by default, which is no refusal of access.</summary>
        public ListingEnd Answer { get; init; } = new(PathPresence.Refused, Denied: false);

        public bool FailsAtEverySize { get; init; }

        public List<int> BuffersAskedFor { get; } = [];

        public ListingEnd List(string directory, EnumerationOptions options, List<WalkEntry> into)
        {
            BuffersAskedFor.Add(options.BufferSize);

            if (FailsAtEverySize || options.BufferSize > WalkTuning.MinimumListingBuffer || Answer.Denied)
            {
                into.Add(Entries[0]);
                return Answer;
            }

            into.AddRange(Entries);
            return new ListingEnd(PathPresence.Present, Denied: false);
        }
    }
}
