namespace NetScope.Audit.Rules;

/// <summary>NS008: P/Invoke to sensitive Windows APIs and calls through raw function pointers.</summary>
public sealed class NS008NativeCalls : IRule
{
    public string Id => "NS008";
    public string Name => "SensitiveNativeCall";
    public string Title => "Sensitive native call";
    public string Description => "P/Invoke declarations of kernel32, advapi32 and ntdll functions that manage memory, processes, threads, tokens or services, and Marshal.GetDelegateForFunctionPointer. Their behavior is outside managed code and worth reviewing.";
    public Severity Level => Severity.Note;

    /// <summary>Functions by DLL; a trailing A/W (ANSI/Unicode) variant matches too.</summary>
    private static readonly Dictionary<string, HashSet<string>> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kernel32"] = new(StringComparer.Ordinal)
        {
            "VirtualAlloc", "VirtualAllocEx", "VirtualProtect", "VirtualProtectEx", "WriteProcessMemory", "ReadProcessMemory",
            "CreateRemoteThread", "CreateThread", "OpenProcess", "LoadLibrary", "LoadLibraryEx", "GetProcAddress",
            "CreateProcess", "WinExec", "SetThreadContext", "QueueUserAPC",
        },
        ["advapi32"] = new(StringComparer.Ordinal)
        {
            "OpenProcessToken", "AdjustTokenPrivileges", "DuplicateTokenEx", "ImpersonateLoggedOnUser", "LogonUser",
            "CreateProcessAsUser", "CreateProcessWithLogon", "CreateService", "ChangeServiceConfig", "RegSetValueEx", "RegCreateKeyEx",
        },
        ["ntdll"] = new(StringComparer.Ordinal)
        {
            "NtAllocateVirtualMemory", "NtProtectVirtualMemory", "NtWriteVirtualMemory", "NtCreateThreadEx", "RtlCreateUserThread",
            "NtQueryInformationProcess", "NtSetInformationThread", "NtUnmapViewOfSection", "NtMapViewOfSection",
        },
    };

    public void Check(RuleContext context)
    {
        if (context.Method.PInvoke is { } pinvoke && Sensitive(pinvoke.Dll, pinvoke.EntryPoint))
            context.Report(this, null, $"P/Invoke declaration of {pinvoke}");

        foreach (var (_, ins, target) in context.Calls())
            if (RuleContext.Is(target, "System.Runtime.InteropServices.Marshal", "GetDelegateForFunctionPointer"))
                context.Report(this, ins, "calls native code through a function pointer (Marshal.GetDelegateForFunctionPointer)");
    }

    internal static bool Sensitive(string dll, string entry)
    {
        var module = Path.GetFileNameWithoutExtension(dll.Trim());
        if (!Functions.TryGetValue(module, out var names)) return false;
        if (names.Contains(entry)) return true;
        return entry.Length > 1 && entry[^1] is 'A' or 'W' && names.Contains(entry[..^1]);
    }
}
