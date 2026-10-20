namespace NetScope.Diagnostics;

public enum Verbosity { Quiet, Normal, Verbose }

/// <summary>
/// All diagnostics go to stderr so that stdout stays clean for piping.
/// Counts failures to compute the process exit code.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static int _failures;
    private static TextWriter _err = Console.Error;

    public static Verbosity Level { get; set; } = Verbosity.Normal;

    /// <summary>Number of files or methods that were processed with errors (or skipped).</summary>
    public static int Failures => Volatile.Read(ref _failures);

    public static void SetWriter(TextWriter writer) => _err = writer;

    public static void Reset() => Interlocked.Exchange(ref _failures, 0);

    /// <summary>An error that makes part of the output incomplete (affects exit code).</summary>
    public static void Error(string? location, string message)
    {
        Interlocked.Increment(ref _failures);
        Write("error", location, message);
    }

    /// <summary>A file was skipped: counts as a partial failure but is reported as a warning.</summary>
    public static void Skipped(string location, string message)
    {
        Interlocked.Increment(ref _failures);
        if (Level >= Verbosity.Normal) Write("warning", location, message);
    }

    public static void Warning(string location, string message)
    {
        if (Level >= Verbosity.Normal) Write("warning", location, message);
    }

    public static void Info(string message)
    {
        if (Level >= Verbosity.Verbose) Write("info", null, message);
    }

    /// <summary>Formats "file: method: IL_xxxx" for diagnostics.</summary>
    public static string At(string file, string? method = null, int? ilOffset = null)
    {
        var s = file;
        if (method != null) s += ": " + method;
        if (ilOffset != null) s += $": IL_{ilOffset.Value:X4}";
        return s;
    }

    private static void Write(string level, string? location, string message)
    {
        var line = location == null ? $"netscope: {level}: {message}" : $"netscope: {level}: {location}: {message}";
        lock (Sync)
        {
            _err.WriteLine(line);
            _err.Flush();
        }
    }
}
