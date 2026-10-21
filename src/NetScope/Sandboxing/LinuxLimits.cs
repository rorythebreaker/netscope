using System.Runtime.InteropServices;

namespace NetScope.Sandboxing;

/// <summary>
/// (Linux only; called only when OperatingSystem.IsLinux().) Self-applied Linux restrictions for the current process, installed before any untrusted code runs:
/// address-space limit (RLIMIT_AS), death with the parent (PR_SET_PDEATHSIG), no new privileges, and a
/// seccomp filter that denies process creation (with RLIMIT_NPROC as a fallback).
/// </summary>
internal static class LinuxLimits
{
    /// <summary>
    /// Applied after the runtime started, so the address space it already reserved (GC regions, mapped
    /// images) is kept and only growth beyond it is limited. The managed heap itself is capped separately
    /// by DOTNET_GCHeapHardLimit in the child's environment.
    /// </summary>
    public static void ApplyMemoryLimit(long bytes)
    {
        if (bytes <= 0) return;
        if (!SetRlimit(RLIMIT_AS, CurrentAddressSpace() + (ulong)bytes))
            throw new SandboxException(SandboxFeature.Memory, "setrlimit(RLIMIT_AS) failed: errno " + Marshal.GetLastWin32Error());
    }

    public static void DieWithParent()
    {
        if (prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0) != 0)
            throw new SandboxException(SandboxFeature.KillOnParentExit, "prctl(PR_SET_PDEATHSIG) failed: errno " + Marshal.GetLastWin32Error());
    }

    public static void DropNewPrivileges()
    {
        // Best-effort: required for seccomp without privileges, harmless if it is unsupported.
        _ = prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0);
    }

    /// <summary>Blocks fork/clone/exec with seccomp; falls back to RLIMIT_NPROC when seccomp is unavailable.</summary>
    public static void ForbidChildProcesses()
    {
        if (TryInstallSeccomp()) return;
        // Fallback: cap the number of processes for this (real) user to the current count.
        if (!SetRlimit(RLIMIT_NPROC, CurrentProcessCount()))
            throw new SandboxException(SandboxFeature.NoChildProcesses, "neither seccomp nor RLIMIT_NPROC could forbid child processes");
    }

    private static bool TryInstallSeccomp()
    {
        if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0) return false;
        var filter = BuildFilter();
        if (filter == null) return false;
        var handle = GCHandle.Alloc(filter, GCHandleType.Pinned);
        try
        {
            var prog = new SockFprog { Length = (ushort)filter.Length, Filter = handle.AddrOfPinnedObject() };
            var progHandle = GCHandle.Alloc(prog, GCHandleType.Pinned);
            try
            {
                var p = (long)progHandle.AddrOfPinnedObject();
                // TSYNC applies the filter to every thread of the runtime, not only the calling one.
                long nr = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 277 : 317;
                if (syscall(nr, SECCOMP_SET_MODE_FILTER, SECCOMP_FILTER_FLAG_TSYNC, p) == 0) return true;
                return prctl(PR_SET_SECCOMP, SECCOMP_MODE_FILTER, p, 0, 0) == 0;
            }
            finally { progHandle.Free(); }
        }
        finally { handle.Free(); }
    }

    /// <summary>
    /// BPF: verify the architecture, then deny process creation while still allowing threads:
    /// fork/vfork/execve/execveat return EPERM, clone returns EPERM unless it creates a thread (CLONE_THREAD),
    /// and clone3 returns ENOSYS so that glibc falls back to clone (its flags are not visible to seccomp).
    /// A denied attempt surfaces as an exception in the child instead of killing it.
    /// Unknown architectures disable the filter.
    /// </summary>
    private static SockFilter[]? BuildFilter()
    {
        uint arch;
        int[] denied;
        int clone, clone3 = 435;
        switch (RuntimeInformation.ProcessArchitecture)
        {
            case Architecture.X64:
                arch = AUDIT_ARCH_X86_64;
                denied = new[] { 57, 58, 59, 322 }; // fork, vfork, execve, execveat
                clone = 56;
                break;
            case Architecture.Arm64:
                arch = AUDIT_ARCH_AARCH64;
                denied = new[] { 221, 281 }; // execve, execveat (no fork/vfork on arm64)
                clone = 220;
                break;
            default:
                return null;
        }

        var f = new List<SockFilter>
        {
            // A = seccomp_data.arch; a foreign ABI would reinterpret syscall numbers, so it is killed.
            new(BPF_LD | BPF_W | BPF_ABS, 0, 0, SeccompDataArchOffset),
            new(BPF_JMP | BPF_JEQ | BPF_K, 1, 0, arch),
            new(BPF_RET | BPF_K, 0, 0, SECCOMP_RET_KILL_PROCESS),
            // A = seccomp_data.nr
            new(BPF_LD | BPF_W | BPF_ABS, 0, 0, SeccompDataNrOffset),
        };
        foreach (var nr in denied)
        {
            f.Add(new(BPF_JMP | BPF_JEQ | BPF_K, 0, 1, (uint)nr));
            f.Add(new(BPF_RET | BPF_K, 0, 0, SECCOMP_RET_ERRNO | EPERM));
        }
        f.Add(new(BPF_JMP | BPF_JEQ | BPF_K, 0, 1, (uint)clone3));
        f.Add(new(BPF_RET | BPF_K, 0, 0, SECCOMP_RET_ERRNO | ENOSYS));
        // clone: allow only with CLONE_THREAD in the flags (low word of args[0]).
        f.Add(new(BPF_JMP | BPF_JEQ | BPF_K, 0, 3, (uint)clone));
        f.Add(new(BPF_LD | BPF_W | BPF_ABS, 0, 0, SeccompDataArg0Offset));
        f.Add(new(BPF_JMP | BPF_JSET | BPF_K, 1, 0, CLONE_THREAD));
        f.Add(new(BPF_RET | BPF_K, 0, 0, SECCOMP_RET_ERRNO | EPERM));
        f.Add(new(BPF_RET | BPF_K, 0, 0, SECCOMP_RET_ALLOW));
        return f.ToArray();
    }

    /// <summary>VmSize of the current process in bytes (first field of /proc/self/statm, in pages).</summary>
    private static ulong CurrentAddressSpace()
    {
        try
        {
            var pages = File.ReadAllText("/proc/self/statm").Split(' ')[0];
            return ulong.Parse(pages, System.Globalization.CultureInfo.InvariantCulture) * (ulong)Environment.SystemPageSize;
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static ulong CurrentProcessCount()
    {
        try { return (ulong)Directory.GetDirectories("/proc").Count(d => int.TryParse(Path.GetFileName(d), out _)); }
        catch { return 64; }
    }

    private static bool SetRlimit(int resource, ulong value)
    {
        var limit = new RLimit { Current = value, Maximum = value };
        return setrlimit(resource, ref limit) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit { public ulong Current; public ulong Maximum; }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SockFilter
    {
        public SockFilter(ushort code, byte jt, byte jf, uint k) { Code = code; Jt = jt; Jf = jf; K = k; }
        public readonly ushort Code;
        public readonly byte Jt;
        public readonly byte Jf;
        public readonly uint K;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockFprog { public ushort Length; public IntPtr Filter; }

    private const int RLIMIT_AS = 9;
    private const int RLIMIT_NPROC = 6;
    private const int PR_SET_PDEATHSIG = 1;
    private const int PR_SET_NO_NEW_PRIVS = 38;
    private const int PR_SET_SECCOMP = 22;
    private const int SECCOMP_MODE_FILTER = 2;
    private const int SIGKILL = 9;

    private const ushort BPF_LD = 0x00, BPF_W = 0x00, BPF_ABS = 0x20, BPF_JMP = 0x05, BPF_JEQ = 0x10, BPF_JSET = 0x40, BPF_K = 0x00, BPF_RET = 0x06;
    private const uint SECCOMP_RET_KILL_PROCESS = 0x80000000;
    private const uint SECCOMP_RET_ERRNO = 0x00050000;
    private const uint SECCOMP_RET_ALLOW = 0x7fff0000;
    private const uint EPERM = 1;
    private const uint ENOSYS = 38;
    private const uint CLONE_THREAD = 0x00010000;
    private const uint AUDIT_ARCH_X86_64 = 0xC000003E;
    private const uint AUDIT_ARCH_AARCH64 = 0xC00000B7;
    // offsetof(struct seccomp_data, nr) == 0, offsetof(..., arch) == 4
    private const uint SeccompDataNrOffset = 0;
    private const uint SeccompDataArchOffset = 4;
    // offsetof(struct seccomp_data, args[0]); the low 32 bits come first on little-endian x64 and arm64.
    private const uint SeccompDataArg0Offset = 16;

    [DllImport("libc", SetLastError = true)]
    private static extern int setrlimit(int resource, ref RLimit limit);

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, long arg2, long arg3, long arg4, long arg5);

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, long arg1, long arg2, long arg3);

    private const long SECCOMP_SET_MODE_FILTER = 1;
    private const long SECCOMP_FILTER_FLAG_TSYNC = 1;
}
