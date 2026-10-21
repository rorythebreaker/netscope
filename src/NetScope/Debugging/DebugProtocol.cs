using System.Text;
using System.Text.Json;

namespace NetScope.Debugging;

/// <summary>One stack frame reported by the debug host. <see cref="Il"/> is null when the IL offset is
/// unavailable or does not validate (see the Stage-3 note in <see cref="DebugHost"/>).</summary>
public sealed record DebugFrame(int Index, string Method, int Token, string Module, int? Il);

/// <summary>Why the target stopped.</summary>
public enum StopReason { Entry, Breakpoint }

/// <summary>
/// The line-delimited JSON protocol spoken between NetScope and the <c>__debug-host</c> process
/// (TZ-NetScope-2 §3.4.4). Events travel host→parent on the host's stderr; commands travel parent→host on
/// the host's stdin. The set of messages is closed and read-only: there is no memory-write, set-variable or
/// func-eval message in either direction. Nothing here opens a socket or a DAP port.
/// </summary>
public static class DebugProtocol
{
    public const string HostCommand = "__debug-host";

    // ---- messages (host -> parent) -------------------------------------------------

    public static string Ready(int pid) => Obj(w => { w.WriteString("event", "ready"); w.WriteNumber("pid", pid); });

    public static string Stopped(StopReason reason, int threadId, IReadOnlyList<DebugFrame> frames) => Obj(w =>
    {
        w.WriteString("event", "stopped");
        w.WriteString("reason", reason == StopReason.Entry ? "entry" : "breakpoint");
        w.WriteNumber("thread", threadId);
        WriteFrames(w, frames);
    });

    public static string Stack(IReadOnlyList<DebugFrame> frames) => Obj(w =>
    {
        w.WriteString("event", "stack");
        WriteFrames(w, frames);
    });

    public static string Exited(int code) => Obj(w => { w.WriteString("event", "exited"); w.WriteNumber("code", code); });

    public static string ErrorEvent(string message) => Obj(w => { w.WriteString("event", "error"); w.WriteString("message", message); });

    // ---- messages (parent -> host) -------------------------------------------------

    public static string Command(string cmd) => Obj(w => w.WriteString("cmd", cmd));

    // ---- parsing -------------------------------------------------------------------

    public static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement;

    public static string? Text(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int Number(this JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    public static IReadOnlyList<DebugFrame> Frames(this JsonElement e)
    {
        var list = new List<DebugFrame>();
        if (e.TryGetProperty("frames", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var f in arr.EnumerateArray())
                list.Add(new DebugFrame(f.Number("index"), f.Text("method") ?? "?", f.Number("token"), f.Text("module") ?? "", f.TryGetProperty("il", out var il) && il.ValueKind == JsonValueKind.Number ? il.GetInt32() : null));
        return list;
    }

    private static void WriteFrames(Utf8JsonWriter w, IReadOnlyList<DebugFrame> frames)
    {
        w.WriteStartArray("frames");
        foreach (var f in frames)
        {
            w.WriteStartObject();
            w.WriteNumber("index", f.Index);
            w.WriteString("method", f.Method);
            w.WriteNumber("token", f.Token);
            w.WriteString("module", f.Module);
            if (f.Il is int il) w.WriteNumber("il", il);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static string Obj(Action<Utf8JsonWriter> body)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
