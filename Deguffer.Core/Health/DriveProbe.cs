using System.Runtime.InteropServices;
using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Scanning.Media;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Health;

/// <inheritdoc />
public sealed class DriveProbe : IDriveProbe
{
    /// <summary>The one instance the app runs with (G5). It holds nothing between calls.</summary>
    public static readonly DriveProbe Machine = new();

    private DriveProbe()
    {
    }

    public IReadOnlyList<int>? PresentDisks()
    {
        if (DeviceInterfaces.Disks() is not { } paths)
        {
            return null;
        }

        var disks = new SortedSet<int>();
        var request = DriveRequests.For(DriveQuestion.DeviceNumber);

        foreach (var path in paths)
        {
            using var device = DeviceCalls.Open(path);

            if (device.IsInvalid)
            {
                continue;
            }

            var answer = DeviceCalls.Query(device, request.ControlCode, request.Input, new byte[request.OutputBytes]);

            if (answer.Bytes is { } bytes && DriveProperties.TryReadDeviceNumber(bytes, out var disk))
            {
                disks.Add(disk);
            }
        }

        return [.. disks];
    }

    public IDriveDevice Open(int disk) => new Device(DeviceCalls.DiskName(disk));

    /// <summary>
    /// Each kind of handle is opened at most once, on first need: a reading sends over a dozen
    /// questions, and most drives never need the command handle at all.
    /// </summary>
    private sealed class Device(string name) : IDriveDevice
    {
        private SafeFileHandle? _query;
        private int _queryError;
        private SafeFileHandle? _command;
        private int _commandError;

        public StorageAnswer Ask(DriveQuestion question)
        {
            var request = DriveRequests.For(question);

            var (handle, error) = request.Access is DriveAccess.Query
                ? (_query ??= Opened(DeviceCalls.Open(name), out _queryError), _queryError)
                : (_command ??= Opened(DeviceCalls.OpenForCommands(name), out _commandError), _commandError);

            return handle.IsInvalid
                ? StorageAnswer.Failed(error)
                : DeviceCalls.Query(handle, request.ControlCode, request.Input, new byte[request.OutputBytes]);
        }

        public void Dispose()
        {
            _query?.Dispose();
            _command?.Dispose();
        }

        private static SafeFileHandle Opened(SafeFileHandle handle, out int error)
        {
            error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
            return handle;
        }
    }
}
