using System.Buffers.Binary;
using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// The process list a test says Windows would show, and how each process answers when opened.
/// </summary>
internal sealed class FakeProcessTableCalls : IProcessTableCalls
{
    private readonly List<FakeListedProcess> _processes = [];

    /// <summary>
    /// Deguffer's own process as the layout self-check reads it: a 64-bit block holding the working
    /// directory this test process really has, with the trailing separator Windows keeps.
    /// </summary>
    public static FakeListedProcess Own => new()
    {
        Id = Environment.ProcessId,
        Name = "Deguffer",
        CommandLine = "Deguffer.exe",
        Memory = FakeProcessMemory.Native(
            Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory) + Path.DirectorySeparatorChar,
            "Deguffer.exe"),
    };

    public FakeProcessTableCalls With(FakeListedProcess process)
    {
        _processes.Add(process);
        return this;
    }

    public IReadOnlyList<ListedProcess> List() => [.. _processes.Select(p => new ListedProcess(p.Id, p.Name))];

    public IOpenedProcess? Open(int processId, bool withMemory) =>
        _processes.FirstOrDefault(p => p.Id == processId) is { OpenRefused: false } process
            ? new Opened(process, withMemory ? process.Memory : null)
            : null;

    private sealed class Opened(FakeListedProcess process, IProcessMemory? memory) : IOpenedProcess
    {
        public IProcessMemory? Memory => memory;

        public string? ImagePath() => process.ImagePath;

        public string? CommandLine() => process.CommandLine;

        public void Dispose()
        {
        }
    }
}

/// <summary>One process in the list. Its memory is null where Windows would refuse to let it be read.</summary>
internal sealed class FakeListedProcess
{
    public required int Id { get; init; }

    public string Name { get; init; } = "process";

    public bool OpenRefused { get; init; }

    public string? ImagePath { get; init; }

    public string? CommandLine { get; init; }

    public FakeProcessMemory? Memory { get; init; }
}

/// <summary>
/// A process's memory, laid out as Windows lays out its environment blocks: the 64-bit block every
/// process on 64-bit Windows has, and for a WOW64 process the 32-bit block beside it. Only what was
/// written can be read, so a pointer to nowhere fails the way an unmapped address does.
/// </summary>
internal sealed class FakeProcessMemory : IProcessMemory
{
    private const long NativeBlock = 0x1000;
    private const long NativeParameters = 0x2000;
    private const long Wow64Block = 0x3000;
    private const long Wow64Parameters = 0x4000;

    private readonly Dictionary<long, byte> _bytes = [];
    private long _nextText = 0x10000;
    private bool _exitsAtWow64Block;
    private bool _exited;

    public long? EnvironmentBlockAddress { get; set; } = NativeBlock;

    /// <summary>Zero for a 64-bit process, as Windows answers for one. Null where Windows would not say.</summary>
    public long? Wow64EnvironmentBlockAddress { get; set; }

    public static FakeProcessMemory Native(string directory, string commandLine)
    {
        var memory = new FakeProcessMemory { Wow64EnvironmentBlockAddress = 0 };
        memory.WriteNative(directory, commandLine);
        return memory;
    }

    /// <summary>
    /// A WOW64 process whose 32-bit block says it is working in <paramref name="directory"/> while its
    /// 64-bit block says <paramref name="staleDirectory"/>, as a 32-bit <c>cmd.exe</c> was measured doing.
    /// </summary>
    public static FakeProcessMemory Wow64(
        string directory,
        string staleDirectory,
        string commandLine,
        string? wow64CommandLine = null)
    {
        var memory = new FakeProcessMemory { Wow64EnvironmentBlockAddress = Wow64Block };
        memory.WriteNative(staleDirectory, commandLine);
        memory.Pointer32(Wow64Block + 0x10, Wow64Parameters);
        memory.String32(Wow64Parameters + 0x24, directory);
        memory.String32(Wow64Parameters + 0x40, wow64CommandLine ?? commandLine);
        return memory;
    }

    /// <summary>Makes the 32-bit block's pointer to its parameter block point at nothing.</summary>
    public FakeProcessMemory WithoutWow64Parameters()
    {
        Pointer32(Wow64Block + 0x10, 0);
        return this;
    }

    /// <summary>
    /// Makes the process exit as its 32-bit block is first read, after its 64-bit block has read: from
    /// then on nothing in its memory can be read, as for a process whose address space has gone.
    /// </summary>
    public FakeProcessMemory ExitingAtWow64Block()
    {
        _exitsAtWow64Block = true;
        return this;
    }

    public long? EnvironmentBlock() => EnvironmentBlockAddress;

    public long? Wow64EnvironmentBlock() => Wow64EnvironmentBlockAddress;

    public bool TryRead(long address, Span<byte> buffer)
    {
        _exited |= _exitsAtWow64Block && address is >= Wow64Block and < Wow64Parameters + 0x1000;

        if (_exited)
        {
            return false;
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            if (!_bytes.TryGetValue(address + i, out var value))
            {
                return false;
            }

            buffer[i] = value;
        }

        return true;
    }

    private void WriteNative(string directory, string commandLine)
    {
        Pointer64(NativeBlock + 0x20, NativeParameters);
        String64(NativeParameters + 0x38, directory);
        String64(NativeParameters + 0x70, commandLine);
    }

    private void Pointer64(long address, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)value);
        Write(address, bytes);
    }

    private void Pointer32(long address, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
        Write(address, bytes);
    }

    private void String64(long address, string text)
    {
        var buffer = Text(text);
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(text.Length * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)(text.Length * 2 + 2));
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)buffer);
        Write(address, header);
    }

    private void String32(long address, string text)
    {
        var buffer = Text(text);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(text.Length * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)(text.Length * 2 + 2));
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)buffer);
        Write(address, header);
    }

    private long Text(string text)
    {
        var address = _nextText;
        Write(address, Encoding.Unicode.GetBytes(text + '\0'));
        _nextText += 0x1000;
        return address;
    }

    private void Write(long address, ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            _bytes[address + i] = bytes[i];
        }
    }
}
