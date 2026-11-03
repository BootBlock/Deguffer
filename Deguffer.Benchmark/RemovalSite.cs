using System.Runtime.InteropServices;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Benchmark;

/// <summary>
/// The folder a removal route creates for itself, and the only place it builds or removes anything.
///
/// <para><b>Created, never adopted.</b> A folder that is already there is refused, because whatever is
/// in it is not the benchmark's to delete. The folder is created by a call that fails where anything
/// stands at the name, so nothing can arrive between a look and the creation.</para>
///
/// <para><b>§5.6 for every run.</b> The folder holds <see cref="Beside"/>, a folder with a file in it
/// that no route targets, next to whatever a route removes. After each run the folder and that file
/// must still be there: a removal that reached beside its target would otherwise be timed as a
/// faster one.</para>
/// </summary>
internal sealed class RemovalSite
{
    private const int AlreadyExists = 183;
    private const int PathNotFound = 3;

    private RemovalSite(string root) => Root = root;

    /// <summary>The folder, fully qualified. Never printed, for the reason <see cref="MeasuredPlace"/> gives.</summary>
    public string Root { get; }

    /// <summary>The folder beside every route's target, which no route removes.</summary>
    public string Beside => Path.Join(Root, "beside");

    private string Kept => Path.Join(Beside, "kept");

    /// <summary>
    /// Create the folder at <paramref name="folder"/>, which must not exist and whose parent must, or
    /// return null with what was wrong in <paramref name="error"/>. The error names no path.
    /// </summary>
    public static RemovalSite? Create(string folder, out string? error)
    {
        error = null;

        if (!CreateDirectory(LongPath.Extended(folder), 0))
        {
            error = Marshal.GetLastPInvokeError() switch
            {
                AlreadyExists => "Something already stands at that name. Name a folder that does not exist, so nothing already in it is deleted.",
                PathNotFound => "The folder's parent does not exist. Name a new folder inside one that does.",
                var code => $"The folder could not be created: Windows error {code}.",
            };

            return null;
        }

        var site = new RemovalSite(folder);
        Directory.CreateDirectory(LongPath.Extended(site.Beside));
        File.WriteAllBytes(LongPath.Extended(site.Kept), []);

        return site;
    }

    /// <summary>A name directly inside the folder, for a route's target.</summary>
    public string Place(string name) => Path.Join(Root, name);

    /// <summary>
    /// Throw where the folder or the file beside the target is gone. Thrown rather than counted, because
    /// a removal that took what it was never pointed at is a defect to stop on, not a slow run.
    /// </summary>
    public void ProveBesideStanding()
    {
        if (LongPath.ProbeDirectory(Root) is not PathPresence.Present || LongPath.ProbeFile(Kept) is not PathPresence.Present)
        {
            throw new InvalidOperationException(
                "The removal took something beside its target: the benchmark's folder, or the file kept beside the tree, is gone (§5.6).");
        }
    }

    /// <summary>
    /// Remove the folder and everything in it, through the removal a clean uses. Whether all of it went.
    /// Never cancelled, because a folder left half removed is left for the person who ran this to find.
    /// </summary>
    public bool RemoveAll() =>
        DirectoryRemover.RemoveAsync(Root).GetAwaiter().GetResult().RootRemoved;

    // DllImport rather than LibraryImport, which needs AllowUnsafeBlocks; this project does not set it.
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, nint securityAttributes);
}
