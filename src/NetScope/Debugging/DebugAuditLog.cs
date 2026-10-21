using System.Text;

namespace NetScope.Debugging;

/// <summary>
/// The per-session journal (TZ-NetScope-2 §3.4.5): time, user, target path and SHA-256, arguments, the
/// restrictions that were enabled or relaxed, the target PID and the exit code. Values of the target's
/// variables are never written here, so passwords and personal data cannot leak into the log.
/// </summary>
public sealed class DebugAuditLog : IDisposable
{
    private readonly TextWriter? _writer;
    private readonly string? _path;

    private DebugAuditLog(TextWriter? writer, string? path) { _writer = writer; _path = path; }

    public string? Path => _path;

    public static DebugAuditLog Open(string? explicitPath)
    {
        try
        {
            string path = explicitPath ?? DefaultPath();
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            return new DebugAuditLog(writer, path);
        }
        catch
        {
            // The log is best-effort: a session must not fail because the log cannot be written.
            return new DebugAuditLog(null, null);
        }
    }

    private static string DefaultPath()
    {
        string root;
        if (OperatingSystem.IsWindows())
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetScope", "logs");
        else
            root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "netscope", "logs");
        return System.IO.Path.Combine(root, $"debug-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
    }

    public void Line(string key, string value) => _writer?.WriteLine($"{DateTime.UtcNow:O}  {key}: {value}");

    public void Dispose() => _writer?.Dispose();
}
