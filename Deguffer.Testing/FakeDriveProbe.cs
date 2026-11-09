using Deguffer.Core.Health;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Testing;

/// <summary>
/// Disks that answer with synthesised bytes, so a reading is asserted for drives the machine running
/// the suite does not have. A question nothing answered fails as a drive that does not support it,
/// with <c>ERROR_INVALID_FUNCTION</c>.
/// </summary>
public sealed class FakeDriveProbe : IDriveProbe
{
    public const int ErrorInvalidFunction = 1;
    public const int ErrorAccessDenied = 5;

    private readonly Lock _gate = new();
    private readonly List<int> _disks = [];
    private readonly Dictionary<(int Disk, DriveQuestion Question), Func<StorageAnswer>> _answers = [];
    private readonly List<(int Disk, DriveQuestion Question)> _asked = [];

    /// <summary>Whether Windows refuses to list the disks.</summary>
    public bool ListFails { get; set; }

    /// <summary>Every question asked, in order.</summary>
    public IReadOnlyList<(int Disk, DriveQuestion Question)> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    public FakeDriveProbe Answer(int disk, DriveQuestion question, byte[] bytes) =>
        Answer(disk, question, () => StorageAnswer.Answered(bytes));

    public FakeDriveProbe Refuse(int disk, DriveQuestion question, int win32Error) =>
        Answer(disk, question, () => StorageAnswer.Failed(win32Error));

    /// <summary><paramref name="answer"/> runs on the reading thread, so it may block to stand for a stalled drive.</summary>
    public FakeDriveProbe Answer(int disk, DriveQuestion question, Func<StorageAnswer> answer)
    {
        lock (_gate)
        {
            if (!_disks.Contains(disk))
            {
                _disks.Add(disk);
                _disks.Sort();
            }

            _answers[(disk, question)] = answer;
        }

        return this;
    }

    public void Remove(int disk)
    {
        lock (_gate)
        {
            _disks.Remove(disk);
        }
    }

    public IReadOnlyList<int>? PresentDisks()
    {
        lock (_gate)
        {
            return ListFails ? null : [.. _disks];
        }
    }

    public IDriveDevice Open(int disk) => new Device(this, disk);

    private StorageAnswer Ask(int disk, DriveQuestion question)
    {
        Func<StorageAnswer>? answer;

        lock (_gate)
        {
            _asked.Add((disk, question));
            _answers.TryGetValue((disk, question), out answer);
        }

        return answer?.Invoke() ?? StorageAnswer.Failed(ErrorInvalidFunction);
    }

    private sealed class Device(FakeDriveProbe probe, int disk) : IDriveDevice
    {
        public StorageAnswer Ask(DriveQuestion question) => probe.Ask(disk, question);

        public void Dispose()
        {
        }
    }
}
