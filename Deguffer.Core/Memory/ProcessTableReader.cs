using System.ComponentModel;

namespace Deguffer.Core.Memory;

/// <summary>
/// Reads the kernel's process table in one call.
///
/// <para>One call, because it describes every process at once, including the ones this account may
/// not open, and it touches no page of any of them. It was measured at about eight milliseconds for
/// five hundred processes.</para>
///
/// <para>The buffer is kept between reads (G5). A table of that size is about a megabyte, and a view
/// refreshing every two seconds would otherwise put a megabyte on the large object heap each
/// time.</para>
/// </summary>
internal sealed class ProcessTableReader
{
    private const int InitialLength = 1 << 20;
    private const int Attempts = 8;
    private const int ErrorInsufficientBuffer = 122;

    private byte[] _buffer = SystemInformation.PinnedArray<byte>(InitialLength);

    public ParsedProcessTable Read()
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var address = SystemInformation.AddressOf(_buffer);
            var status = SystemInformation.NtQuerySystemInformation(
                SystemInformation.ProcessInformation, address, _buffer.Length, out var returned);

            if (status == SystemInformation.StatusInfoLengthMismatch)
            {
                // Processes start between the call that reports the size and the one that uses it, so
                // the new buffer leaves room for a quarter more.
                _buffer = SystemInformation.PinnedArray<byte>(Math.Max(returned, _buffer.Length) + (returned / 4));
                continue;
            }

            if (status < 0)
            {
                throw SystemInformation.Failure(status);
            }

            return ProcessRecordParser.Parse(
                _buffer.AsSpan(0, Math.Min(returned, _buffer.Length)),
                (ulong)(nuint)address,
                ProcessRecordLayout.Current);
        }

        throw new Win32Exception(ErrorInsufficientBuffer);
    }
}
