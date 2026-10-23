using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>
/// One started tool: its process, and its standard output and error read to their end.
///
/// <para><b>Started by <c>CreateProcess</c> rather than <see cref="System.Diagnostics.Process"/></b>,
/// because only <c>CreateProcess</c> can place a process in its jobs as it is created (see
/// <see cref="ToolJob"/>). The child inherits its three standard handles and nothing else, so two
/// tools started at once cannot hold each other's output open.</para>
/// </summary>
internal sealed class ToolProcess : IDisposable
{
    /// <summary>
    /// How long the output of an ended tool is waited for once its process has exited. Ending the job
    /// ends every process that could hold the output open, so the wait is normally no wait at all.
    /// </summary>
    private static readonly TimeSpan OutputAfterEnd = TimeSpan.FromSeconds(5);

    private readonly SafeProcessHandle _process;
    private readonly AnonymousPipeServerStream _output;
    private readonly AnonymousPipeServerStream _error;
    private readonly Task _processExited;
    private readonly Task<string> _standardOutput;
    private readonly Task<string> _standardError;

    private ToolProcess(SafeProcessHandle process, AnonymousPipeServerStream output, AnonymousPipeServerStream error)
    {
        _process = process;
        _output = output;
        _error = error;
        _processExited = ExitOf(process);

        var encoding = OutputEncoding();
        _standardOutput = ReadLinesAsync(output, encoding);
        _standardError = ReadLinesAsync(error, encoding);
    }

    /// <summary>
    /// Completes when the process has exited and both of its outputs have reached their end, which is
    /// when everything it started that shared them has exited too.
    /// </summary>
    public Task Exited => Task.WhenAll(_processExited, _standardOutput, _standardError);

    /// <summary>Valid once <see cref="Exited"/> has completed.</summary>
    public int ExitCode =>
        ToolProcessNative.GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1;

    /// <summary>What the tool wrote to its standard output, as far as it has been read.</summary>
    public string StandardOutput => _standardOutput.IsCompletedSuccessfully ? _standardOutput.Result : string.Empty;

    /// <summary>What the tool wrote to its standard error, as far as it has been read.</summary>
    public string StandardError => _standardError.IsCompletedSuccessfully ? _standardError.Result : string.Empty;

    /// <summary>
    /// Start <paramref name="applicationName"/> in <paramref name="jobs"/>, in order: a job listed
    /// after another is made a job inside it.
    /// </summary>
    /// <param name="applicationName">The program, by full path, so Windows searches nowhere for it.</param>
    /// <param name="commandLine">The whole command line, the program's own name first.</param>
    /// <exception cref="Win32Exception">Windows would not start it.</exception>
    public static ToolProcess Start(string applicationName, string commandLine, string workingDirectory, IReadOnlyList<ToolJob> jobs)
    {
        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var started = false;

        try
        {
            // The null device, so a tool that stops to ask a question reads the end of its input and
            // goes on or fails, rather than waiting for an answer nothing will give. A device rather
            // than a path, so LongPath has nothing to do here.
            using var input = File.OpenHandle(@"\\.\NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Inheritable);

            var process = Create(applicationName, commandLine, workingDirectory, jobs, input, output.ClientSafePipeHandle, error.ClientSafePipeHandle);

            // The child has its own copies now. Kept here, they would hold the outputs open after the
            // child had closed them, and reading them would never end.
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();

            started = true;
            return new ToolProcess(process, output, error);
        }
        finally
        {
            if (!started)
            {
                output.Dispose();
                error.Dispose();
            }
        }
    }

    /// <summary>
    /// Waits for the process to exit after its job was ended, then gives its output
    /// <see cref="OutputAfterEnd"/> to reach its end.
    /// </summary>
    public async Task EndedAsync()
    {
        await _processExited.ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_standardOutput, _standardError).WaitAsync(OutputAfterEnd).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Something outside the job holds the output open. The tool itself has exited, which is
            // what the caller waits for, and what it wrote by then is all that will be reported.
        }
    }

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        _process.Dispose();
    }

    private static unsafe SafeProcessHandle Create(
        string applicationName,
        string commandLine,
        string workingDirectory,
        IReadOnlyList<ToolJob> jobs,
        SafeFileHandle input,
        SafePipeHandle output,
        SafePipeHandle error)
    {
        var attributeCount = jobs.Count > 0 ? 2 : 1;
        nuint size = 0;

        // The first call only reports the size, and fails saying so.
        ToolProcessNative.InitializeProcThreadAttributeList(0, attributeCount, 0, ref size);

        var attributes = Marshal.AllocHGlobal((nint)size);
        var inherited = Marshal.AllocHGlobal(3 * nint.Size);
        var jobList = Marshal.AllocHGlobal(Math.Max(1, jobs.Count) * nint.Size);
        var initialised = false;

        try
        {
            initialised = ToolProcessNative.InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size);
            ThrowIfFailed(initialised);

            Marshal.WriteIntPtr(inherited, 0 * nint.Size, input.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, 1 * nint.Size, output.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, 2 * nint.Size, error.DangerousGetHandle());

            ThrowIfFailed(ToolProcessNative.UpdateProcThreadAttribute(
                attributes, 0, ToolProcessNative.ProcThreadAttributeHandleList, inherited, (nuint)(3 * nint.Size), 0, 0));

            if (jobs.Count > 0)
            {
                for (var i = 0; i < jobs.Count; i++)
                {
                    Marshal.WriteIntPtr(jobList, i * nint.Size, jobs[i].Handle.DangerousGetHandle());
                }

                ThrowIfFailed(ToolProcessNative.UpdateProcThreadAttribute(
                    attributes, 0, ToolProcessNative.ProcThreadAttributeJobList, jobList, (nuint)(jobs.Count * nint.Size), 0, 0));
            }

            var startup = new ToolProcessNative.StartupInfoEx
            {
                Cb = Marshal.SizeOf<ToolProcessNative.StartupInfoEx>(),
                Flags = ToolProcessNative.StartfUseStdHandles,
                StdInput = input.DangerousGetHandle(),
                StdOutput = output.DangerousGetHandle(),
                StdError = error.DangerousGetHandle(),
                AttributeList = attributes,
            };

            // CreateProcess may write into the command line, so it is given a copy it owns.
            var line = (commandLine + '\0').ToCharArray();

            fixed (char* writable = line)
            {
                ThrowIfFailed(ToolProcessNative.CreateProcess(
                    applicationName,
                    writable,
                    0,
                    0,
                    inheritHandles: true,
                    ToolProcessNative.ExtendedStartupInfoPresent | ToolProcessNative.CreateNoWindow,
                    0,
                    workingDirectory,
                    startup,
                    out var started));

                ToolProcessNative.CloseHandle(started.Thread);
                return new SafeProcessHandle(started.Process, ownsHandle: true);
            }
        }
        finally
        {
            if (initialised)
            {
                ToolProcessNative.DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(jobList);
            Marshal.FreeHGlobal(inherited);
            Marshal.FreeHGlobal(attributes);
            GC.KeepAlive(input);
            GC.KeepAlive(output);
            GC.KeepAlive(error);
            GC.KeepAlive(jobs);
        }
    }

    private static void ThrowIfFailed(bool succeeded)
    {
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static async Task ExitOf(SafeProcessHandle process)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var wait = new ProcessWait(process);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            wait, (_, _) => exited.TrySetResult(), state: null, Timeout.Infinite, executeOnlyOnce: true);

        try
        {
            await exited.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    /// <summary>
    /// The encoding <see cref="System.Diagnostics.Process"/> reads a redirected output in, which every
    /// parser of tool output was written against: the console's output code page, including the OEM
    /// pages only <see cref="CodePagesEncodingProvider"/> carries, or UTF-8 where there is no console,
    /// which is the app's case.
    ///
    /// <para>Never one with a preamble, because <see cref="System.Diagnostics.Process"/> decoded the
    /// bytes as they came, so a byte order mark a tool wrote reached its parser as text.</para>
    /// </summary>
    private static Encoding OutputEncoding()
    {
        var codePage = (int)ToolProcessNative.GetConsoleOutputCP();

        if (codePage is not (0 or 65001))
        {
            try
            {
                return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // A code page .NET does not carry at all, which Process also read as UTF-8.
            }
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    /// <summary>
    /// Line by line, each ended with <see cref="Environment.NewLine"/>, which is how
    /// <see cref="System.Diagnostics.Process"/> handed the output over before, and so what every parser
    /// of it expects. Nothing is taken for a byte order mark, as nothing was then.
    /// </summary>
    private static async Task<string> ReadLinesAsync(Stream stream, Encoding encoding)
    {
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var text = new StringBuilder();

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            text.AppendLine(line);
        }

        return text.ToString();
    }

    /// <summary>The process handle, waited on without being owned.</summary>
    private sealed class ProcessWait : WaitHandle
    {
        public ProcessWait(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
