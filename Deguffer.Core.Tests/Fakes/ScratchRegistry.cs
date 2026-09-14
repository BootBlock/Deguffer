using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// The one registry key every <see cref="ScratchKey"/> is made under, and the sweep that clears what
/// an earlier run could not remove.
///
/// <para>A <see cref="ScratchKey"/> deletes itself, but a host that is killed outright runs no
/// <c>Dispose</c> and no <c>finally</c>, so the key it was using stays. Nothing else on the machine
/// would ever collect it, which makes one interrupted run a permanent addition to the registry. This
/// is the registry half of the bargain <see cref="ScratchRoot"/> keeps for a scratch tree.</para>
///
/// <para>Age comes from the key's own last-write time, read through <c>RegQueryInfoKey</c>, because
/// <see cref="RegistryKey"/> reports no time at all. It is the last write rather than the creation
/// the directory sweep reads, since the platform records no creation time for a key. That direction
/// is the safe one: a write can only make a key look younger, and a key that looks younger stays.
/// </para>
/// </summary>
internal static class ScratchRegistry
{
    /// <summary>
    /// Where scratch keys live. One level under <c>Software</c>, so removing one leaves no empty
    /// parent behind, and under <c>HKEY_CURRENT_USER</c>, which needs no elevation.
    /// </summary>
    internal const string Parent = "Software";

    /// <summary>What every scratch key's name starts with, before this run's own identifier.</summary>
    internal const string NamePrefix = "Deguffer.Tests.";

    private const int ErrorSuccess = 0;

    private static readonly Lazy<bool> Swept = new(() =>
    {
        SweepTheParent();
        return true;
    });

    /// <summary>
    /// Whether this process has swept yet.
    ///
    /// <para>The suite asserts on it, because the sweep runs before any test can watch it and
    /// nothing it leaves behind distinguishes "swept and found nothing" from "never ran".</para>
    /// </summary>
    internal static bool HasSwept => Swept.IsValueCreated;

    /// <summary>Clear earlier runs' leavings, once per test process.</summary>
    internal static void SweepOnce() => _ = Swept.Value;

    /// <summary>A name for one piece of this run's registry scratch.</summary>
    internal static string NewName() => NamePrefix + Scratch.NewIdentifier();

    /// <summary>Whether <paramref name="name"/> is a name <see cref="NewName"/> would write.</summary>
    /// <remarks>
    /// The prefix matched ordinally, before the identifier's own round trip. The registry compares
    /// key names without case, so the parent can hold a <c>deguffer.tests.</c> key somebody else
    /// wrote, and §5.2 turns on recognising only what we made.
    /// </remarks>
    internal static bool IsScratchKey(string name) =>
        name.StartsWith(NamePrefix, StringComparison.Ordinal)
        && Scratch.IsIdentifier(name[NamePrefix.Length..]);

    /// <summary>
    /// Remove every recognised child of <paramref name="parent"/> last written before
    /// <paramref name="cutoff"/>, and nothing else.
    ///
    /// <para>"Recognised" is §5.2's rule turned on the suite's own scratch. <paramref name="parent"/>
    /// itself is never a target, and a child whose name is not one <see cref="NewName"/> writes is
    /// left alone, because <c>HKCU\Software</c> is where everything installed on the machine keeps
    /// its settings. A child that will not go is left for the next run rather than retried.</para>
    /// </summary>
    internal static void SweepStale(RegistryKey parent, DateTime cutoff)
    {
        foreach (var name in Stale(parent, cutoff))
        {
            try
            {
                parent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // A key another run's sweep took between the listing and now, or one this account
                // may not remove. Left for a later run, as a held scratch tree is.
            }
        }
    }

    /// <summary>
    /// Whether the child of <paramref name="parent"/> named <paramref name="name"/> was last written
    /// before <paramref name="cutoff"/>.
    ///
    /// <para>A time we could not read answers no. This is the predicate guarding a recursive delete,
    /// and the only safe reading of "I cannot tell" on such a predicate is the one that stops it.
    /// The key going between the listing and this call is the ordinary way to reach it.</para>
    /// </summary>
    internal static bool IsStale(RegistryKey parent, string name, DateTime cutoff) =>
        LastWriteTimeUtc(parent, name) is { } written && written < cutoff;

    /// <summary>
    /// When the child of <paramref name="parent"/> named <paramref name="name"/> was last written,
    /// or null where there is no answer to be had: the key is gone, or this account may not open it.
    /// </summary>
    internal static DateTime? LastWriteTimeUtc(RegistryKey parent, string name)
    {
        try
        {
            using var key = parent.OpenSubKey(name);

            return key is null ? null : LastWriteTimeUtc(key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// When <paramref name="key"/> was last written, or null if <c>RegQueryInfoKey</c> refused.
    ///
    /// <para>A file time at or below zero is not a time. Zero is 1601, which is older than any
    /// cutoff, so a sweep that took it straight would read "there is nothing here to date" as
    /// "certainly stale" and delete on it. A negative one is what
    /// <see cref="DateTime.FromFileTimeUtc"/> throws on.</para>
    /// </summary>
    internal static DateTime? LastWriteTimeUtc(RegistryKey key)
    {
        var status = RegQueryInfoKey(
            key.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            out var written);

        return status == ErrorSuccess && written > 0 ? DateTime.FromFileTimeUtc(written) : null;
    }

    /// <summary>
    /// The stale scratch keys under <paramref name="parent"/>, all of them listed before the first
    /// one is deleted, so that removing a key cannot disturb the enumeration that found it. The list
    /// holds the parent's children, never a key's contents.
    /// </summary>
    private static IReadOnlyList<string> Stale(RegistryKey parent, DateTime cutoff)
    {
        try
        {
            return [.. parent.GetSubKeyNames()
                .Where(IsScratchKey)
                .Where(name => IsStale(parent, name, cutoff))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// Open the place scratch keys live and sweep it on the shared margin.
    ///
    /// <para>A parent that is not there, or that this account may not open for writing, sweeps
    /// nothing and reports nothing. The sweep runs inside a <see cref="Lazy{T}"/>, which keeps a
    /// faulting factory's exception for the life of the process, so one throw from here would be
    /// re-thrown by every <see cref="ScratchKey"/> the run went on to make.</para>
    /// </summary>
    private static void SweepTheParent()
    {
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(Parent, writable: true);

            if (parent is not null)
            {
                SweepStale(parent, DateTime.UtcNow - Scratch.StaleAfter);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The sweep is never the reason a run fails, and there is nobody here to report to.
        }
    }

    // DllImport rather than LibraryImport, matching the App's native calls: the source generator
    // wants AllowUnsafeBlocks, which this project does not set. Every parameter this caller has no
    // use for is an optional out pointer, and IntPtr.Zero asks the platform not to fill it in.
    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    private static extern int RegQueryInfoKey(
        SafeRegistryHandle key,
        IntPtr keyClass,
        IntPtr keyClassLength,
        IntPtr reserved,
        IntPtr subKeyCount,
        IntPtr subKeyNameLength,
        IntPtr keyClassNameLength,
        IntPtr valueCount,
        IntPtr valueNameLength,
        IntPtr valueLength,
        IntPtr securityDescriptorLength,
        out long lastWriteTime);
}
