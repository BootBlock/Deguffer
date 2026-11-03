using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// What the removal of one directory found before it deleted anything: what stands at the path, and
/// for a directory, everything its walk gathered. See <see cref="DirectoryRemover.Gather"/>.
///
/// <para><b>Apart from the deletion, so a removal that goes whole or not at all is judged on the walk
/// it then acts on.</b> Looked at in a walk of its own first, a tree was listed twice in a row, and a
/// store that arrived between the two listings was stepped over by a removal already judged to have
/// none. The evidence the judgement reads is here, beside what the deletion takes, so nothing is
/// listed between the judgement and the deletion. See <see cref="WholeTreeLook.Of"/>.</para>
/// </summary>
/// <param name="Extended">The directory to remove, in the extended-length form §6.3 requires.</param>
/// <param name="Bounds">What the removal must leave behind, which the walk has already applied.</param>
internal abstract record GatheredRemoval(string Extended, RemovalBounds Bounds)
{
    /// <summary>Outlook data files the removal would step over, in extended form.</summary>
    public virtual IReadOnlyList<string> MailStores => [];

    /// <summary>Folders Windows would not let the removal list, in extended form.</summary>
    public virtual IReadOnlyList<string> Unlisted => [];

    /// <summary>Paths met as folders where something else now stands, in extended form.</summary>
    public virtual IReadOnlyList<string> NotFolders => [];

    /// <summary>Files and links the guard on recently changed files holds back.</summary>
    public virtual int Kept => 0;

    /// <summary>Nothing stands at the path, so nothing inside it can be lost.</summary>
    public sealed record Gone(string Extended, RemovalBounds Bounds) : GatheredRemoval(Extended, Bounds);

    /// <summary>
    /// Windows would not say whether the directory is there. Nothing is removed, and the path is named
    /// as a folder that would not be listed, because nobody can say what it holds.
    /// </summary>
    public sealed record Refused(string Extended, RemovalBounds Bounds) : GatheredRemoval(Extended, Bounds)
    {
        public override IReadOnlyList<string> Unlisted => [Extended];
    }

    /// <summary>A file, or something else that is not a directory, has taken the directory's name.</summary>
    public sealed record NotAFolder(string Extended, RemovalBounds Bounds) : GatheredRemoval(Extended, Bounds)
    {
        public override IReadOnlyList<string> NotFolders => [Extended];
    }

    /// <summary>
    /// The path is a junction or a symbolic link, which is removed as a link and never followed.
    /// </summary>
    /// <param name="IsKept">Whether the guard holds it back, by the link's own timestamps.</param>
    public sealed record Link(string Extended, RemovalBounds Bounds, bool IsKept) : GatheredRemoval(Extended, Bounds)
    {
        public override int Kept => IsKept ? 1 : 0;
    }

    /// <summary>
    /// A directory, and everything its walk gathered, folder by folder. Deleted once, by
    /// <see cref="DirectoryRemover.DeleteAsync"/>: each folder counts down what is left in it as the
    /// deletion takes it.
    /// </summary>
    public sealed record Tree : GatheredRemoval
    {
        public Tree(string extended, RemovalBounds bounds, RemovalFolder root)
            : base(extended, bounds)
        {
            Root = root;

            var mailStores = new List<string>();
            var unlisted = new List<string>();
            var notFolders = new List<string>();

            foreach (var (folder, _) in root.InWalkOrder())
            {
                mailStores.AddRange(folder.MailStores);
                Kept += folder.Kept;

                switch (folder.Refusal)
                {
                    case ListingRefusal.None:
                        break;

                    case ListingRefusal.NotADirectory:
                        notFolders.Add(folder.Path);
                        break;

                    default:
                        unlisted.Add(folder.Path);
                        break;
                }
            }

            (MailStores, Unlisted, NotFolders) = (mailStores, unlisted, notFolders);
        }

        /// <summary>The directory's own folder, from which every folder below it is reached.</summary>
        public RemovalFolder Root { get; }

        public override IReadOnlyList<string> MailStores { get; }

        public override IReadOnlyList<string> Unlisted { get; }

        public override IReadOnlyList<string> NotFolders { get; }

        public override int Kept { get; }
    }
}
