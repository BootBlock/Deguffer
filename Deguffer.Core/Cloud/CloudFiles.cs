using System.Runtime.InteropServices;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;
using Windows.Storage.Provider;
using static Deguffer.Core.Cloud.CloudFilesNative;

namespace Deguffer.Core.Cloud;

/// <summary>
/// The Cloud Files API on this machine. Stateless, so one instance serves every caller (G5).
///
/// <para>Every path reaches Windows through <see cref="LongPath.Extended"/> (§6.3). These are raw
/// calls, so nothing adds the prefix on the way down, and a sync root holds whatever folder depth its
/// owner's cloud does.</para>
/// </summary>
public sealed class CloudFiles : ICloudFiles
{
    public static readonly CloudFiles Default = new();

    private CloudFiles()
    {
    }

    /// <summary>
    /// Through <see cref="StorageProviderSyncRootManager"/>, which is the documented list. The registry
    /// keys under <c>SyncRootManager</c> hold the same thing in a layout nobody documents.
    ///
    /// <para>A root whose folder Windows will not hand back is left out: there is nowhere to look. Windows
    /// itself leaves out a root registered under <c>AppData\Local</c>, as a scratch root there showed, and
    /// every sync app Deguffer recognises keeps its root elsewhere.</para>
    /// </summary>
    public IReadOnlyList<SyncRoot> SyncRoots()
    {
        IReadOnlyList<StorageProviderSyncRootInfo> roots;

        try
        {
            roots = StorageProviderSyncRootManager.GetCurrentSyncRoots();
        }
        catch (COMException)
        {
            // The manager answers through a COM server that a stripped-down or policy-locked
            // Windows may not run. No list means no root Deguffer could act on, which is the
            // truthful reading of a machine that cannot say.
            return [];
        }

        var found = new List<SyncRoot>(roots.Count);

        foreach (var root in roots)
        {
            if (root.Path?.Path is { Length: > 0 } path)
            {
                found.Add(new SyncRoot(root.Id, path, root.DisplayNameResource ?? string.Empty));
            }
        }

        return found;
    }

    public unsafe SyncProviderState ProviderState(string syncRoot)
    {
        var buffer = stackalloc byte[ProviderInfoBufferSize];

        var result = CfGetSyncRootInfoByPath(
            LongPath.Extended(syncRoot), SyncRootInfoProvider, buffer, ProviderInfoBufferSize, out _);

        if (result != Success)
        {
            return SyncProviderState.Unknown;
        }

        return *(uint*)buffer is ProviderDisconnected or ProviderTerminated or ProviderError
            ? SyncProviderState.NotRunning
            : SyncProviderState.Running;
    }

    /// <summary>
    /// <para><b>Placeholders are exposed for the listing and for nothing else.</b> Windows disguises a
    /// placeholder as an ordinary file to every process but the sync app connected to its root, so
    /// the listing Deguffer is given carries neither the reparse attribute nor the tag, and every
    /// file in a OneDrive folder read as one that is no placeholder at all. The mode is the calling
    /// thread's, set around each call that lists and put back straight after, so no other file
    /// operation in the process sees a placeholder it was not written to expect.</para>
    /// </summary>
    public IEnumerable<CloudEntry> List(string directory, CancellationToken ct)
    {
        var display = LongPath.Display(directory);
        var handle = FindFirst(Path.Join(LongPath.Extended(directory), "*"), out var data);

        if (handle == InvalidHandle)
        {
            yield break;
        }

        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();

                if (Classify(display, ref data) is { } entry)
                {
                    yield return entry;
                }
            }
            while (FindNext(handle, out data));
        }
        finally
        {
            FindClose(handle);
        }
    }

    /// <summary>The first entry of a listing, made with placeholders exposed to this thread.</summary>
    private static nint FindFirst(string pattern, out FindData data)
    {
        var previous = RtlSetThreadPlaceholderCompatibilityMode(ExposePlaceholders);

        try
        {
            return FindFirstFileEx(pattern, FindExInfoBasic, out data, FindExSearchNameMatch, searchFilter: 0, FindFirstExLargeFetch);
        }
        finally
        {
            Restore(previous);
        }
    }

    /// <summary>The next entry of a listing, on the same terms as <see cref="FindFirst"/>.</summary>
    private static bool FindNext(nint handle, out FindData data)
    {
        var previous = RtlSetThreadPlaceholderCompatibilityMode(ExposePlaceholders);

        try
        {
            return FindNextFile(handle, out data);
        }
        finally
        {
            Restore(previous);
        }
    }

    /// <summary>The thread's own mode back, where setting it answered one.</summary>
    private static void Restore(sbyte previous)
    {
        if (previous >= 0)
        {
            RtlSetThreadPlaceholderCompatibilityMode(previous);
        }
    }

    public PlaceholderReading Read(string path)
    {
        var presence = LongPath.ProbeEntry(path);

        if (presence is not PathPresence.Present)
        {
            return new PlaceholderReading(presence, null);
        }

        using var handle = OpenForState(LongPath.Extended(path));

        if (handle.IsInvalid)
        {
            return Unopened();
        }

        return Describe(handle) switch
        {
            (Success, var placeholder) => new PlaceholderReading(PathPresence.Present, placeholder),
            (NotACloudFile, _) => new PlaceholderReading(PathPresence.Present, null),
            _ => PlaceholderReading.Refused,
        };
    }

    public ReleaseAnswer Release(string path, Func<Placeholder, bool> stillEligible)
    {
        using var handle = OpenForState(LongPath.Extended(path));

        if (handle.IsInvalid)
        {
            return new ReleaseAnswer(
                Unopened().Presence is PathPresence.Absent ? ReleaseResult.Gone : ReleaseResult.Refused);
        }

        var (result, placeholder) = Describe(handle);

        if (result == NotACloudFile || (result == Success && !stillEligible(placeholder!)))
        {
            return new ReleaseAnswer(ReleaseResult.NoLongerEligible);
        }

        if (result != Success)
        {
            return new ReleaseAnswer(ReleaseResult.Refused);
        }

        return CfSetPinState(handle, PinStateUnpinned, SetPinFlagNone, overlapped: 0) == Success
            ? new ReleaseAnswer(ReleaseResult.Requested, placeholder!.OnDiskBytes)
            : new ReleaseAnswer(ReleaseResult.Refused);
    }

    /// <summary>
    /// A listed entry, or null for the directory's own <c>.</c> and <c>..</c>, and for a name that reads
    /// as empty, which would name the directory itself and send a walk round it for ever.
    ///
    /// <para>Classified by <c>CfGetPlaceholderStateFromAttributeTag</c>, which reads only the two numbers
    /// the listing already holds, so a folder of a hundred thousand placeholders costs no handle each.
    /// </para>
    /// </summary>
    private static unsafe CloudEntry? Classify(string directory, ref FindData data)
    {
        string name;

        fixed (char* chars = data.FileName)
        {
            name = new string(chars);
        }

        if (name is "" or "." or "..")
        {
            return null;
        }

        var isReparse = (data.FileAttributes & FileAttributeReparsePoint) != 0;
        var state = isReparse
            ? CfGetPlaceholderStateFromAttributeTag(data.FileAttributes, data.Reserved0)
            : 0;
        var isPlaceholder = state != PlaceholderStateInvalid && (state & PlaceholderStatePlaceholder) != 0;

        return new CloudEntry(
            Path.Join(directory, name),
            IsDirectory: (data.FileAttributes & FileAttributeDirectory) != 0,
            isPlaceholder,
            IsOtherLink: isReparse && !isPlaceholder,
            MinimumAge.NewestFileTimeOf(data.CreationTime, data.LastWriteTime));
    }

    /// <summary>The placeholder behind an open handle, or the HRESULT that says why there is none.</summary>
    private static unsafe (int Result, Placeholder? Placeholder) Describe(SafeFileHandle handle)
    {
        var buffer = stackalloc byte[PlaceholderInfoBufferSize];

        var result = CfGetPlaceholderInfo(
            handle, PlaceholderInfoStandard, buffer, PlaceholderInfoBufferSize, out _);

        if (result != Success)
        {
            return (result, null);
        }

        if (!TryBasicInfo(handle, out var basic))
        {
            return (Marshal.GetHRForLastWin32Error(), null);
        }

        var info = *(PlaceholderStandardInfo*)buffer;

        return (Success, new Placeholder(
            info.OnDiskDataSize,
            info.ModifiedDataSize,
            InSync: info.InSyncState == 1,
            Enum.IsDefined((PinState)info.PinState) ? (PinState)info.PinState : PinState.Pinned,
            MinimumAge.NewestFileTimeOf(basic.CreationTime, basic.LastWriteTime)));
    }

    /// <summary>Why a handle would not open, in the probe's own three answers.</summary>
    private static PlaceholderReading Unopened() =>
        Marshal.GetLastPInvokeError() is FileAttributeRead.FileNotFound or FileAttributeRead.PathNotFound
            ? PlaceholderReading.Absent
            : PlaceholderReading.Refused;
}
