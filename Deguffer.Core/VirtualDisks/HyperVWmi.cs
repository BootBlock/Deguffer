using System.Runtime.InteropServices;
using Deguffer.Core.Wmi;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// The machine's <see cref="IHyperVManagement"/>: Hyper-V's <c>root\virtualization\v2</c> namespace,
/// read in process through WMI's own COM interfaces (<see cref="WbemConnection"/>), which is what the
/// Hyper-V module's <c>Get-VM</c>, <c>Get-VMSnapshot</c>, <c>Get-VMHardDiskDrive</c> and <c>Get-VHD</c>
/// are cmdlets over. In process because the module is loaded only by starting Windows PowerShell, which
/// costs a second or more, and every planning pass, clean and Explore policy build reads this.
///
/// <para>Only reads. The one method called, <c>GetVirtualHardDiskSettingData</c>, describes a disk file and
/// changes nothing, and is called by its class and name so no other method can be reached from here.</para>
/// </summary>
public sealed class HyperVWmi : IHyperVManagement
{
    /// <summary>The one instance the app runs with (G5). It holds nothing between sessions.</summary>
    public static readonly HyperVWmi Machine = new();

    private const string Namespace = @"root\virtualization\v2";

    private HyperVWmi()
    {
    }

    public IHyperVSession? Open(out int hr) =>
        WbemConnection.Connect(Namespace, out hr) is { } services ? new Session(services) : null;

    private sealed class Session(IWbemServices services) : IHyperVSession
    {
        private const string ImageManagementClass = "Msvm_ImageManagementService";

        private const string Method = "GetVirtualHardDiskSettingData";

        /// <summary>The service's own object path and the method's input signature, asked once a session.</summary>
        private (string Path, IWbemClassObject Signature)? _imageManagement;

        public IReadOnlyList<HyperVSystemRow>? Systems(out int hr, CancellationToken ct) =>
            WbemQuery.Rows(
                services,
                "SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem",
                system => WbemQuery.Property(system, "Name") is string name
                    ? new HyperVSystemRow(
                        name,
                        WbemQuery.Property(system, "ElementName") as string,
                        WbemQuery.Integer(WbemQuery.Property(system, "EnabledState")))
                    : null,
                out hr,
                ct);

        /// <summary>
        /// Every settings object, then the storage each associates, asked by the settings' own relative
        /// path so the association Hyper-V keeps decides it rather than a guess at how its identifiers are
        /// built. A settings object whose storage cannot be listed fails the whole listing, because a
        /// machine named without a disk it has would leave that disk unregistered.
        /// </summary>
        public IReadOnlyList<HyperVSettingsRow>? Settings(out int hr, CancellationToken ct)
        {
            var settings = WbemQuery.Rows(
                services,
                "SELECT * FROM Msvm_VirtualSystemSettingData",
                setting => WbemQuery.Property(setting, "__RELPATH") is string path
                    ? new SettingsObject(
                        path,
                        WbemQuery.Property(setting, "VirtualSystemIdentifier") as string,
                        WbemQuery.Property(setting, "VirtualSystemType") as string)
                    : null,
                out hr,
                ct);

            if (settings is null)
            {
                return null;
            }

            List<HyperVSettingsRow> rows = new(settings.Count);

            foreach (var (path, system, type) in settings)
            {
                var storage = WbemQuery.Rows(
                    services,
                    $"ASSOCIATORS OF {{{path}}} WHERE ResultClass = Msvm_StorageAllocationSettingData",
                    allocation => new HyperVStorageRow(
                        WbemQuery.Property(allocation, "ResourceSubType") as string,
                        First(WbemQuery.Property(allocation, "HostResource"))),
                    out hr,
                    ct);

                if (storage is null)
                {
                    return null;
                }

                rows.Add(new HyperVSettingsRow(system, type, storage));
            }

            return rows;
        }

        public HyperVDiskAnswer DescribeDisk(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (ImageManagement(out var hr, ct) is not { } service)
            {
                return HyperVDiskAnswer.Failed(hr);
            }

            var (servicePath, signature) = service;

            hr = signature.SpawnInstance(0, out var inputs);

            if (hr != WbemConnection.S_OK)
            {
                return HyperVDiskAnswer.Failed(hr);
            }

            IWbemClassObject? outputs = null;

            try
            {
                object boxed = path;
                hr = inputs.Put("Path", 0, ref boxed, 0);

                if (hr != WbemConnection.S_OK)
                {
                    return HyperVDiskAnswer.Failed(hr);
                }

                hr = services.ExecMethod(servicePath, Method, 0, null, inputs, out outputs, IntPtr.Zero);

                if (hr != WbemConnection.S_OK || outputs is null)
                {
                    return HyperVDiskAnswer.Failed(hr == WbemConnection.S_OK ? WbemConnection.Failed : hr);
                }

                return new HyperVDiskAnswer(
                    WbemConnection.S_OK,
                    WbemQuery.Integer(WbemQuery.Property(outputs, "ReturnValue")),
                    WbemQuery.Property(outputs, "SettingData") as string);
            }
            finally
            {
                WbemQuery.Release(outputs);
                Marshal.ReleaseComObject(inputs);
            }
        }

        public void Dispose()
        {
            if (_imageManagement is { Signature: var signature })
            {
                Marshal.ReleaseComObject(signature);
            }

            Marshal.ReleaseComObject(services);
        }

        /// <summary>
        /// The image management service's path and <see cref="Method"/>'s input signature, or null with
        /// WMI's code. Hyper-V has one such service, and its path is read from it rather than written here.
        /// </summary>
        private (string Path, IWbemClassObject Signature)? ImageManagement(out int hr, CancellationToken ct)
        {
            if (_imageManagement is { } known)
            {
                hr = WbemConnection.S_OK;
                return known;
            }

            var paths = WbemQuery.Rows(
                services,
                $"SELECT * FROM {ImageManagementClass}",
                service => WbemQuery.Property(service, "__RELPATH") as string,
                out hr,
                ct);

            if (paths is null)
            {
                return null;
            }

            if (paths.Count == 0)
            {
                hr = WbemConnection.Failed;
                return null;
            }

            hr = services.GetObject(ImageManagementClass, 0, null, out var serviceClass, IntPtr.Zero);

            if (hr != WbemConnection.S_OK)
            {
                return null;
            }

            try
            {
                hr = serviceClass.GetMethod(Method, 0, out var signature, out var outSignature);
                WbemQuery.Release(outSignature);

                if (hr != WbemConnection.S_OK || signature is null)
                {
                    WbemQuery.Release(signature);
                    hr = hr == WbemConnection.S_OK ? WbemConnection.Failed : hr;
                    return null;
                }

                _imageManagement = (paths[0], signature);
                return _imageManagement;
            }
            finally
            {
                Marshal.ReleaseComObject(serviceClass);
            }
        }

        /// <summary>A settings object as listed, before its storage is asked for.</summary>
        private sealed record SettingsObject(string Path, string? System, string? Type);

        /// <summary>The first element of a string array, which WMI hands over as one of strings or of objects.</summary>
        private static string? First(object? value) => value switch
        {
            string[] strings => strings.FirstOrDefault(),
            object[] objects => objects.FirstOrDefault() as string,
            _ => null,
        };
    }
}
