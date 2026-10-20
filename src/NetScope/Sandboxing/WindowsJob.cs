using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NetScope.Sandboxing;

/// <summary>
/// (Windows only; created only when OperatingSystem.IsWindows().) A Windows Job Object that caps committed memory, forbids child processes and (optionally) kills the
/// assigned process when NetScope closes the handle. The job is configured before a process is assigned.
/// </summary>
internal sealed class WindowsJob : IDisposable
{
    private readonly SafeJobHandle _job;

    private WindowsJob(SafeJobHandle job) => _job = job;

    public static WindowsJob Create(SandboxOptions options)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

        var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        uint flags = 0;
        if (options.MemoryLimitBytes > 0 && !options.Relaxed.Contains(SandboxFeature.Memory))
        {
            flags |= JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            info.ProcessMemoryLimit = (nuint)options.MemoryLimitBytes;
        }
        if (!options.AllowChildProcesses && !options.Relaxed.Contains(SandboxFeature.NoChildProcesses))
        {
            flags |= JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
            info.BasicLimitInformation.ActiveProcessLimit = 1; // only the assigned process itself
        }
        if (options.KillOnParentExit && !options.Relaxed.Contains(SandboxFeature.KillOnParentExit))
            flags |= JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        info.BasicLimitInformation.LimitFlags = flags;

        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                int err = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(err, "SetInformationJobObject failed");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return new WindowsJob(job);
    }

    public void Assign(IntPtr processHandle)
    {
        if (!AssignProcessToJobObject(_job, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
    }

    /// <summary>Terminates every process still in the job.</summary>
    public void Terminate()
    {
        try { TerminateJobObject(_job, 1); } catch (ObjectDisposedException) { }
    }

    public void Dispose() => _job.Dispose(); // closing the handle kills the children when KILL_ON_JOB_CLOSE is set

    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandle
    {
        public SafeJobHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObject(IntPtr security, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
