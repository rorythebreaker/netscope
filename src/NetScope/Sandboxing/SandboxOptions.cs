namespace NetScope.Sandboxing;

/// <summary>
/// Restrictions applied to an untrusted child process before any of its code runs (TZ 2.3 and 7.3).
/// The same options drive the <c>__jit-helper</c> and, later, the debugged process.
/// </summary>
public sealed class SandboxOptions
{
    /// <summary>Address-space / committed-memory limit in bytes. 0 disables the limit.</summary>
    public long MemoryLimitBytes { get; init; } = 1L << 30; // 1 GiB, --run-memory

    /// <summary>When false (default), the child may not create further processes.</summary>
    public bool AllowChildProcesses { get; init; }

    /// <summary>Kill the child when NetScope (the parent) exits or closes the job handle.</summary>
    public bool KillOnParentExit { get; init; } = true;

    /// <summary>A fresh working directory created for the child and deleted afterwards.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment variables passed to the child in addition to PATH and DOTNET_*.</summary>
    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>Restrictions the caller allowed NetScope to skip if the OS cannot enforce them (by name, see <see cref="SandboxFeature"/>).</summary>
    public IReadOnlySet<string> Relaxed { get; init; } = new HashSet<string>();
}

/// <summary>Names of the individually relaxable restrictions (used by <c>--relax-sandbox</c> and the session log).</summary>
public static class SandboxFeature
{
    public const string Memory = "memory";
    public const string NoChildProcesses = "no-child-processes";
    public const string KillOnParentExit = "kill-on-parent-exit";
    public const string LowPrivilege = "low-privilege";

    public static readonly IReadOnlyList<string> All = new[] { Memory, NoChildProcesses, KillOnParentExit, LowPrivilege };
}

/// <summary>Thrown when a restriction could not be enforced and the caller did not relax it.</summary>
public sealed class SandboxException : Exception
{
    public SandboxException(string feature, string message) : base(message) => Feature = feature;

    /// <summary>The <see cref="SandboxFeature"/> that could not be applied.</summary>
    public string Feature { get; }
}
