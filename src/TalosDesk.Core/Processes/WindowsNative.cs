using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TalosDesk.Core.Processes;

internal static class WindowsNative
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNewConsole = 0x00000010;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const short SwHide = 0;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;

    public static (SafeJobHandle Job, SafeProcessHandle Process, int ProcessId, StreamReader Stdout, StreamReader Stderr) StartPowerShell(string command, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("TalosDesk process management is supported on Windows only.");
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        }

        var shellPath = FindPowerShellPath();
        var stdoutRead = CreatePipe(out var stdoutWrite);
        var stderrRead = CreatePipe(out var stderrWrite);
        using var stdin = CreateInheritableNullInput();
        var job = CreateJob();

        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = StartfUseShowWindow | StartfUseStdHandles,
            ShowWindow = SwHide,
            StdInput = stdin.DangerousGetHandle(),
            StdOutput = stdoutWrite.DangerousGetHandle(),
            StdError = stderrWrite.DangerousGetHandle()
        };
        var commandLine = new StringBuilder($"\"{shellPath}\" -NoLogo -NoProfile -NonInteractive -Command {QuoteArgument(command)}");

        if (!CreateProcess(shellPath, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles: true,
                CreateSuspended | CreateNewConsole, IntPtr.Zero, workingDirectory,
                ref startup, out var processInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start PowerShell 7.");
        }

        using var thread = new SafeKernelHandle(processInfo.ThreadHandle);
        var unassignedProcess = new SafeProcessHandle(processInfo.ProcessHandle, ownsHandle: true);
        try
        {
            if (!AssignProcessToJobObject(job, unassignedProcess))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the command to its process job.");
            }

            if (ResumeThread(thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the PowerShell process.");
            }
        }
        catch
        {
            TerminateJob(job, 1);
            throw;
        }
        finally
        {
            stdoutWrite.Dispose();
            stderrWrite.Dispose();
        }

        try
        {
            var stdout = new StreamReader(new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var stderr = new StreamReader(new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return (job, unassignedProcess, checked((int)processInfo.ProcessId), stdout, stderr);
        }
        catch
        {
            TerminateJob(job, 1);
            unassignedProcess.Dispose();
            job.Dispose();
            stdoutRead.Dispose();
            stderrRead.Dispose();
            throw;
        }
    }

    public static async Task<bool> WaitForHandleAsync(SafeJobHandle handle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var milliseconds = timeout == Timeout.InfiniteTimeSpan ? Infinite : checked((uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1));
        var waitTask = Task.Run(() => WaitForSingleObject(handle, milliseconds), cancellationToken);
        var result = await waitTask.ConfigureAwait(false);
        if (result == WaitObject0) return true;
        if (result == 0x00000102) return false;
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Waiting for the command process group failed.");
    }

    public static async Task<bool> WaitForProcessAsync(SafeProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var milliseconds = timeout == Timeout.InfiniteTimeSpan ? Infinite : checked((uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1));
        var result = await Task.Run(() => WaitForSingleObjectProcess(handle, milliseconds), cancellationToken).ConfigureAwait(false);
        if (result == WaitObject0) return true;
        if (result == 0x00000102) return false;
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Waiting for the command process failed.");
    }

    public static void TerminateJob(SafeJobHandle job) => TerminateJob(job, 1);

    public static int GetExitCode(SafeProcessHandle process)
    {
        if (!GetExitCodeProcess(process, out var exitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the command exit code.");
        }

        return unchecked((int)exitCode);
    }

    public static uint GetActiveProcessCount(SafeJobHandle job)
    {
        if (!QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(), out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect the command process group.");
        }

        return accounting.ActiveProcesses;
    }

    public static IReadOnlyList<int> GetActiveProcessIds(SafeJobHandle job)
    {
        const int capacity = 128;
        var buffer = Marshal.AllocHGlobal(8 + IntPtr.Size * capacity);
        try
        {
            if (!QueryInformationJobObjectBuffer(job, 3, buffer, (uint)(8 + IntPtr.Size * capacity), out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not list the command process group.");
            }

            var count = Marshal.ReadInt32(buffer, 4);
            var result = new List<int>(count);
            for (var index = 0; index < count; index++)
            {
                var id = Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64();
                if (id <= int.MaxValue) result.Add((int)id);
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static async Task<bool> WaitForCommandGroupExitAsync(SafeJobHandle job, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var activeProcessIds = GetActiveProcessIds(job);
            if (!activeProcessIds.Any(IsAliveNonConsoleProcess))
            {
                if (GetActiveProcessCount(job) != 0)
                {
                    // Windows keeps the hidden console host alive after the last command process exits.
                    // This job belongs only to this command, so close the console host with its job.
                    TerminateJob(job, 0);
                }

                while (GetActiveProcessCount(job) != 0)
                {
                    if (timeout != Timeout.InfiniteTimeSpan && timer.Elapsed >= timeout) return false;
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }

            if (timeout != Timeout.InfiniteTimeSpan && timer.Elapsed >= timeout) return false;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsAliveNonConsoleProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && !process.ProcessName.Equals("conhost", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool CloseHandle(IntPtr handle) => CloseHandleNative(handle);

    public static IntPtr GetConsoleWindow() => GetConsoleWindowNative();
    public static bool FreeConsole() => FreeConsoleNative();
    public static bool AttachConsole(uint processId) => AttachConsoleNative(processId);
    public static bool SetConsoleCtrlHandler(IntPtr handler, bool add) => SetConsoleCtrlHandlerNative(handler, add);
    public static bool GenerateConsoleCtrlEvent(uint eventType, uint processGroupId) => GenerateConsoleCtrlEventNative(eventType, processGroupId);

    private static SafeJobHandle CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a process job.");
        }

        var limits = new JobObjectExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Could not configure process cleanup.");
        }

        return job;
    }

    private static void TerminateJob(SafeJobHandle job, uint exitCode)
    {
        if (!TerminateJobObject(job, exitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not force stop the command process group.");
        }
    }

    private static SafeFileHandle CreatePipe(out SafeFileHandle writeHandle)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        if (!CreatePipeNative(out var read, out var write, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a command output pipe.");
        }

        if (!SetHandleInformation(read, HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(read);
            CloseHandle(write);
            throw new Win32Exception(error, "Could not secure a command output pipe.");
        }

        writeHandle = new SafeFileHandle(write, ownsHandle: true);
        return new SafeFileHandle(read, ownsHandle: true);
    }

    private static SafeFileHandle CreateInheritableNullInput()
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        var input = CreateFile("NUL", GenericRead, shareMode: 3, ref security, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (input.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            input.Dispose();
            throw new Win32Exception(error, "Could not configure non-interactive command input.");
        }

        return input;
    }

    private static string FindPowerShellPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = Path.IsPathRooted(entry) ? entry : Path.GetFullPath(entry, Environment.CurrentDirectory);
            var candidate = Path.Combine(directory, "pwsh.exe");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("PowerShell 7 (pwsh.exe) was not found on the application PATH.");
    }

    private static string QuoteArgument(string argument)
    {
        var result = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref JobObjectExtendedLimitInformation information, uint informationLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeJobHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WaitForSingleObject")] private static extern uint WaitForSingleObjectProcess(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeJobHandle job, int informationClass, out JobObjectBasicAccountingInformation information, uint informationLength, out uint returnLength);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "QueryInformationJobObject")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObjectBuffer(SafeJobHandle job, int informationClass, IntPtr information, uint informationLength, out uint returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeKernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreatePipe")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipeNative(out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, ref SecurityAttributes securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandleNative(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetConsoleWindow")] private static extern IntPtr GetConsoleWindowNative();
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "FreeConsole")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeConsoleNative();
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "AttachConsole")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsoleNative(uint processId);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetConsoleCtrlHandler")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleCtrlHandlerNative(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GenerateConsoleCtrlEvent")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GenerateConsoleCtrlEventNative(uint eventType, uint processGroupId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr ProcessHandle; public IntPtr ThreadHandle; public uint ProcessId; public uint ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicAccountingInformation { public long TotalUserTime; public long TotalKernelTime; public long ThisPeriodTotalUserTime; public long ThisPeriodTotalKernelTime; public uint TotalPageFaultCount; public uint TotalProcesses; public uint ActiveProcesses; public uint TotalTerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicLimitInformation { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformation { public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed; }

    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandleNative(handle);
    }
}
