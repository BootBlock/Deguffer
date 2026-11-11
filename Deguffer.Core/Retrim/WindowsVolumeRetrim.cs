using System.Runtime.InteropServices;
using Deguffer.Core.Wmi;

namespace Deguffer.Core.Retrim;

/// <summary>
/// <c>MSFT_Volume.Optimize</c> in Windows' storage management namespace,
/// <c>ROOT\Microsoft\Windows\Storage</c>, the class the storage module's <c>Optimize-Volume -ReTrim</c>
/// is a cmdlet over. Windows' own route, so Windows decides what a retrim means for each kind of storage
/// (§5.1), and answers "Not Supported" where it means nothing.
///
/// <para>The volume is found by its <c>Path</c>, the <c>\\?\Volume{GUID}\</c> name, rather than by its
/// letter, so a volume mounted at a folder is found too, and a letter moved to another volume is never
/// retrimmed in its place.</para>
/// </summary>
public sealed class WindowsVolumeRetrim : IVolumeRetrim
{
    /// <summary>The one instance the app runs with (G5). It holds nothing between calls.</summary>
    public static readonly WindowsVolumeRetrim Machine = new();

    private const string StorageNamespace = @"ROOT\Microsoft\Windows\Storage";

    private const string VolumeClass = "MSFT_Volume";

    private const string Method = "Optimize";

    private WindowsVolumeRetrim()
    {
    }

    public RetrimAnswer Retrim(string volumeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeName);

        if (WbemConnection.Connect(StorageNamespace, out var hr) is not { } services)
        {
            return RetrimAnswer.FromError(hr);
        }

        try
        {
            if (ObjectPathOf(services, volumeName, out hr) is not { } path)
            {
                return hr == WbemConnection.S_OK ? new RetrimAnswer(RetrimOutcome.NotListed) : RetrimAnswer.FromError(hr);
            }

            return Optimize(services, path);
        }
        finally
        {
            Marshal.ReleaseComObject(services);
        }
    }

    /// <summary>
    /// The path WMI names the volume by, or null: with <paramref name="hr"/> at <c>S_OK</c> where no
    /// volume has that name, and at the error where the listing failed.
    /// </summary>
    private static string? ObjectPathOf(IWbemServices services, string volumeName, out int hr)
    {
        hr = services.ExecQuery("WQL", $"SELECT * FROM {VolumeClass}", WbemConnection.SemiSynchronous, null, out var volumes);

        if (hr != WbemConnection.S_OK)
        {
            return null;
        }

        try
        {
            while (true)
            {
                hr = volumes.Next(WbemConnection.Infinite, 1, out var volume, out var returned);

                if (hr < 0)
                {
                    return null;
                }

                if (returned == 0)
                {
                    hr = WbemConnection.S_OK;
                    return null;
                }

                try
                {
                    if (volume.Get("Path", 0, out var named, out _, out _) == WbemConnection.S_OK
                        && named is string name
                        && string.Equals(name, volumeName, StringComparison.OrdinalIgnoreCase)
                        && volume.Get("__PATH", 0, out var objectPath, out _, out _) == WbemConnection.S_OK
                        && objectPath is string found)
                    {
                        hr = WbemConnection.S_OK;
                        return found;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(volume);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(volumes);
        }
    }

    /// <summary>
    /// <c>Optimize</c> on the volume at <paramref name="path"/>, with the inputs
    /// <see cref="RetrimParameters"/> gives, and its return value read back.
    /// </summary>
    private static RetrimAnswer Optimize(IWbemServices services, string path)
    {
        var hr = services.GetObject(VolumeClass, 0, null, out var volumeClass, IntPtr.Zero);

        if (hr != WbemConnection.S_OK)
        {
            return RetrimAnswer.FromError(hr);
        }

        IWbemClassObject? signature = null;
        IWbemClassObject? inputs = null;
        IWbemClassObject? outputs = null;

        try
        {
            hr = volumeClass.GetMethod(Method, 0, out signature, out var outSignature);
            Release(outSignature);

            if (hr != WbemConnection.S_OK || signature is null)
            {
                return RetrimAnswer.FromError(hr);
            }

            hr = signature.SpawnInstance(0, out inputs);

            if (hr != WbemConnection.S_OK)
            {
                return RetrimAnswer.FromError(hr);
            }

            foreach (var (name, value) in RetrimParameters.All)
            {
                object boxed = value;
                hr = inputs.Put(name, 0, ref boxed, 0);

                if (hr != WbemConnection.S_OK)
                {
                    return RetrimAnswer.FromError(hr);
                }
            }

            hr = services.ExecMethod(path, Method, 0, null, inputs, out outputs, IntPtr.Zero);

            if (hr != WbemConnection.S_OK || outputs is null)
            {
                return RetrimAnswer.FromError(hr);
            }

            hr = outputs.Get("ReturnValue", 0, out var returned, out _, out _);

            // WMI hands a uint32 over as a signed four-byte VARIANT.
            int? code = returned switch
            {
                int signed => signed,
                uint unsigned => unchecked((int)unsigned),
                _ => null,
            };

            return hr == WbemConnection.S_OK && code is { } returnValue
                ? RetrimAnswer.FromReturnValue(returnValue)
                : RetrimAnswer.FromError(hr);
        }
        finally
        {
            Release(outputs);
            Release(inputs);
            Release(signature);
            Marshal.ReleaseComObject(volumeClass);
        }
    }

    private static void Release(object? instance)
    {
        if (instance is not null)
        {
            Marshal.ReleaseComObject(instance);
        }
    }
}
