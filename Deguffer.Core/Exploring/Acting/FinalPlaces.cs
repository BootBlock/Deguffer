using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// Where a path <see cref="ExploreActionPolicy"/> judges really is, once Windows has followed every
/// link on the way to it (§7.1).
///
/// <para><b>The policy's rules are asked of paths, and a link makes two paths one folder.</b> A
/// junction in the profile leading to <c>C:\Windows</c> puts the Windows directory below the
/// profile's text, where the profile's permission answered for it. A tool's folder moved by a
/// junction, <c>%USERPROFILE%\.gradle</c> leading to <c>D:\gradle</c>, is scanned at
/// <c>D:\gradle</c>, where no declaration names it, and §5.2 applies in Explore exactly as it does in
/// Storage. A removal acts on the folder, so each refusing rule is followed to where it leads once,
/// when the policy is built, and each item at each question.</para>
///
/// <para><b>Only what is above the item is followed.</b> Removing a link removes the link and never
/// what it leads to, because <see cref="ExploreRemover"/> removes a link as itself, so the item is
/// named on the far side of its parent and not of itself.</para>
///
/// <para><b>A share, a network drive and a drive that keeps its files in the cloud are never
/// opened</b>, as a duplicate search does not open them: opening is itself a conversation with
/// another machine or the cloud client, a link on a share is followed by the server that holds it,
/// and such a drive holds no link that could lead back to a local disk. What is there is judged by
/// its text, as it was before links were followed at all.</para>
///
/// <para><b>Following a link only adds places.</b> A rule and an item keep every path they were
/// reachable at before, and a refusal that holds at any of them holds, so a link followed can refuse
/// a removal and never allow one. That is why a permitting region is not followed: the signed-in
/// profile moved by a junction into another account's profile would otherwise carry its permission
/// there. Storage refuses outright to delete through a link (<see cref="DerivedPath"/>); Explore
/// judges the far side instead, because scoping Explore to a link is a route it offers on
/// purpose.</para>
/// </summary>
internal sealed class FinalPlaces(IFileSystem fileSystem, IVolumeInventory volumes)
{
    /// <summary>
    /// The folder a rule names at <paramref name="path"/>, at every path it is reachable at, the
    /// folder a link on the way to it leads to included.
    ///
    /// <para>A rule whose folder Windows will not open keeps the paths its name reaches. Refusing
    /// everything because one rule could not be followed would refuse the whole drive for the sake of
    /// a folder that is, most often, simply not there.</para>
    /// </summary>
    /// <param name="path">The rule's path, resolved as <see cref="LongPath.Configured(string?)"/> resolves it.</param>
    public ReachedFolder Rule(string path)
    {
        var folder = ReachedFolder.At(path, volumes);

        return IsOpened(path)
            && fileSystem.FinalPath(LongPath.Extended(path)) is { } final
            && ReachedFolder.Displayed(final) is { } shown
                ? folder.Joined(ReachedFolder.At(shown, volumes))
                : folder;
    }

    /// <summary>
    /// Where removing <paramref name="target"/> acts once the links above it are followed, or why
    /// Deguffer cannot tell.
    ///
    /// <para><b>Fails closed.</b> A folder above the item that is there and that Windows will not
    /// open, or that Windows names only by a device path, hides where the removal would act, so
    /// it refuses. A folder above the item that is not there hides nothing: the item is not there
    /// either, and the removal reports it as gone.</para>
    /// </summary>
    /// <param name="target">The item, in display form, as the policy has already resolved it. Never the top of a volume.</param>
    public FarSide Item(string target)
    {
        var parent = Path.GetDirectoryName(target)!;
        var extended = LongPath.Extended(parent);
        var name = Path.GetFileName(target);

        if (!IsOpened(parent))
        {
            return new FarSide(null, null);
        }

        if (fileSystem.FinalPath(extended) is not { } final)
        {
            return fileSystem.MayExist(extended)
                ? new FarSide(null, ExploreVerdict.Refuse(
                    $"Windows would not open '{parent}', so Deguffer cannot tell whether a link on the way "
                    + $"leads somewhere else, or what removing '{name}' would act on, and leaves it alone."))
                : new FarSide(null, null);
        }

        return ReachedFolder.Displayed(final) is { } shown
            ? new FarSide(Path.Join(shown, name), null)
            : new FarSide(null, ExploreVerdict.Refuse(
                $"'{parent}' leads to a drive Windows names by no letter or folder, so Deguffer cannot tell "
                + $"what removing '{name}' would act on, and leaves it alone."));
    }

    /// <summary>Whether <paramref name="path"/> is on a drive whose links are asked about. See the type's remarks.</summary>
    private bool IsOpened(string path) =>
        !LongPath.IsShare(path)
        && HostVolume.For(volumes, path) is not ({ Kind: DriveType.Network } or { StoresContentRemotely: true });
}

/// <summary>
/// What <see cref="FinalPlaces.Item"/> learned: the path a removal acts on with every link above it
/// followed, or the refusal where Windows would not say.
/// </summary>
/// <param name="Path">
/// The item's path with every link above it followed, in display form. Null where nothing above the
/// item is there, or where <paramref name="Refusal"/> says why it could not be found.
/// </param>
/// <param name="Refusal">Why Deguffer cannot tell where the removal would act, or null where it can.</param>
internal readonly record struct FarSide(string? Path, ExploreVerdict? Refusal);
