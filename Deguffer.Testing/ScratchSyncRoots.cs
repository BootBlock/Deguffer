using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Deguffer.Core.Safety;
using Microsoft.Win32;
using Windows.Storage.Provider;

namespace Deguffer.Testing;

/// <summary>
/// The sync roots <see cref="ScratchSyncRoot"/> registers, and the sweep that unregisters what an
/// earlier run could not.
///
/// <para>A <see cref="ScratchSyncRoot"/> unregisters itself, but a host that is killed outright runs
/// no <c>Dispose</c>, so its registration stays, and the scratch sweeps remove only the folder under
/// it. Windows goes on listing a root whose folder is gone, and Explorer may go on showing it. This
/// is the sync root half of the bargain <see cref="ScratchRoot"/> keeps for a scratch tree.</para>
///
/// <para>The registrations are read from the registry, because the documented list leaves out a
/// root under <c>AppData\Local</c>, and that is where nearly every scratch root is. Only the reading
/// relies on that layout. Removal goes through
/// <see cref="StorageProviderSyncRootManager.Unregister"/>, which leaves the folder alone.</para>
///
/// <para>Age comes from the folder's creation time. Each <see cref="ScratchSyncRoot"/> registers
/// over a folder made for it moments before, so the folder is as old as the registration.</para>
/// </summary>
internal static class ScratchSyncRoots
{
    /// <summary>Where Windows keeps each sync root's registration, one key per identifier.</summary>
    private const string RegistrationsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    private static readonly string User = WindowsIdentity.GetCurrent().User!.Value;

    private static readonly Lazy<bool> Swept = new(() =>
    {
        SweepStale(Registered(), DateTime.UtcNow - Scratch.StaleAfter);
        return true;
    });

    /// <summary>
    /// What every scratch root's identifier starts with: the suite's provider name and this user's
    /// SID. Another user's roots are not this account's to unregister, so they are never recognised.
    /// </summary>
    internal static string IdPrefix { get; } = $"{ScratchSyncRoot.ProviderName}!{User}!";

    /// <summary>
    /// Whether this process has swept yet.
    ///
    /// <para>The suite asserts on it, because the sweep runs before any test can watch it and
    /// nothing it leaves behind distinguishes "swept and found nothing" from "never ran".</para>
    /// </summary>
    internal static bool HasSwept => Swept.IsValueCreated;

    /// <summary>Clear earlier runs' leavings, once per test process.</summary>
    internal static void SweepOnce() => _ = Swept.Value;

    /// <summary>An identifier for one of this run's sync roots.</summary>
    internal static string NewId() => IdPrefix + Scratch.NewIdentifier();

    /// <summary>Whether <paramref name="id"/> is an identifier <see cref="NewId"/> would write.</summary>
    /// <remarks>
    /// The prefix matched ordinally, before the identifier's own round trip. The registry compares
    /// key names without case, so it can hold a <c>deguffertests!</c> root somebody else registered,
    /// and §5.2 turns on recognising only what we made.
    /// </remarks>
    internal static bool IsScratchId(string id) =>
        id.StartsWith(IdPrefix, StringComparison.Ordinal)
        && Scratch.IsIdentifier(id[IdPrefix.Length..]);

    /// <summary>
    /// Every registered sync root whose identifier is a scratch one, with the folder Windows recorded
    /// for this user, or null where it recorded none.
    ///
    /// <para>A list that cannot be read is empty. The sweep runs inside a <see cref="Lazy{T}"/>,
    /// which keeps a faulting factory's exception for the life of the process, so one throw from here
    /// would be re-thrown by every <see cref="ScratchSyncRoot"/> the run went on to make.</para>
    /// </summary>
    internal static IReadOnlyList<Registration> Registered()
    {
        try
        {
            using var registrations = Registry.LocalMachine.OpenSubKey(RegistrationsKey);

            if (registrations is null)
            {
                return [];
            }

            return [.. registrations.GetSubKeyNames()
                .Where(IsScratchId)
                .Select(id => new Registration(id, FolderOf(registrations, id)))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// Unregister each of <paramref name="registrations"/> that is stale by <paramref name="cutoff"/>,
    /// and nothing else. A registration Windows will not let go of is left for a later run.
    /// </summary>
    internal static void SweepStale(IEnumerable<Registration> registrations, DateTime cutoff)
    {
        foreach (var registration in registrations.Where(registration => IsStale(registration, cutoff)))
        {
            try
            {
                StorageProviderSyncRootManager.Unregister(registration.Id);
            }
            catch (COMException)
            {
                // Windows refuses to unregister a root that is still connected, which is a live run's,
                // and answers one another sweep took between the listing and now the same way.
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="registration"/> is an earlier run's: its folder is gone, or was made
    /// before <paramref name="cutoff"/>.
    ///
    /// <para>A folder that is gone answers yes, where <see cref="ScratchRoot.IsStale"/> answers no.
    /// That predicate guards a recursive delete. This one guards removing a registration, which
    /// takes no file with it, and a registration over nothing is the very thing being collected.
    /// A live run's folder is never gone, because each <see cref="ScratchSyncRoot"/> makes its
    /// folder before it registers and unregisters before the folder is removed.</para>
    ///
    /// <para>A registration with no folder recorded, or a folder whose age the account may not read,
    /// answers no: there is nothing to judge it by.</para>
    /// </summary>
    internal static bool IsStale(Registration registration, DateTime cutoff)
    {
        if (registration.Folder is null)
        {
            return false;
        }

        try
        {
            var created = Directory.GetCreationTimeUtc(LongPath.Extended(registration.Folder));

            return created == Scratch.NotFound || created < cutoff;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The folder Windows recorded for this user's root <paramref name="id"/>, if any.</summary>
    private static string? FolderOf(RegistryKey registrations, string id)
    {
        using var users = registrations.OpenSubKey($@"{id}\UserSyncRoots");

        return users?.GetValue(User) as string;
    }

    /// <summary>One registered scratch root, and the folder it was registered over.</summary>
    internal readonly record struct Registration(string Id, string? Folder);
}
